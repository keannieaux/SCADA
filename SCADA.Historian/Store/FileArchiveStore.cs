using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SCADA.Core.Tags;

namespace SCADA.Historian;

/// <summary>
/// Файловое хранилище архива: один файл на поток на месяц (docs/archive-format.md §5).
/// Поддерживает запись, чтение диапазона и агрегацию по бакетам.
/// Открытый блок копится в памяти до 4096 точек, часа, смены месяца
/// или явного вызова FlushAsync.
/// </summary>
public sealed class FileArchiveStore : IArchiveStore, IDisposable
{
    /// <summary>Заголовок файла потока: магия, версия, streamId, резерв (§8.2).</summary>
    private const int FileHeaderSize = 16;

    private readonly string _archiveRoot;
    private readonly long _blockTimeoutMs;
    private readonly int _blockCapacity;
    private readonly ArchiveWal? _wal;
    private readonly ArchiveDirectoryLock? _directoryLock;
    private bool _disposed;

    // ConcurrentDictionary, а не Dictionary: RegisterStream может вызываться,
    // пока другой поток читает архив. Обычный словарь при одновременных
    // чтении и вставке портит внутренние структуры, и это не воспроизводится.
    private readonly ConcurrentDictionary<int, ArchiveStreamConfig> _streams = new();

    // Открытые блоки живут под _writeLock: их трогают и запись, и чтение.
    private readonly Dictionary<int, OpenBlock> _openBlocks = new();

    // Последняя записанная метка на поток. Переживает закрытие блока: иначе
    // проверка монотонности не заметила бы откат времени на границе блоков.
    private readonly Dictionary<int, long> _lastWritten = new();

    private readonly object _writeLock = new();

    public StoreCapabilities Capabilities { get; } =
        StoreCapabilities.RawRead | StoreCapabilities.NativeAggregation;

    /// <param name="archiveRoot">Каталог архива.</param>
    /// <param name="blockTimeout">Предельный пролёт блока (§8.6).</param>
    /// <param name="durable">
    /// Журнал упреждающей записи и исключительный захват каталога.
    /// По умолчанию включён: выключенный журнал молча отменяет гарантию
    /// ТЗ §4.2 «потеря не более 10 секунд», и умолчание, отменяющее
    /// требование, — ловушка. Выключается осознанно: в тестах, которые
    /// журнал не проверяют, и при разборе архива только на чтение.
    /// </param>
    /// <param name="walSegmentBytes">
    /// Размер сегмента журнала (§21). Влияет на то, насколько крупными
    /// порциями освобождается журнал: сегмент удаляется целиком, когда все
    /// его данные дошли до файлов потоков.
    /// </param>
    /// <param name="blockPoints">
    /// Вместимость блока в отсчётах (§8.6). Прямо определяет пик памяти:
    /// открытый блок держит 24 байта на отсчёт для каждого потока. Значение
    /// выводится из бюджета памяти в <c>ArchiveOptions</c>, а не задаётся
    /// наугад.
    /// </param>
    public FileArchiveStore(string archiveRoot, TimeSpan? blockTimeout = null, bool durable = true,
        long walSegmentBytes = 16L * 1024 * 1024, int blockPoints = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(blockPoints, 1);

        _archiveRoot = archiveRoot ?? throw new ArgumentNullException(nameof(archiveRoot));
        _blockTimeoutMs = (long)(blockTimeout ?? TimeSpan.FromHours(1)).TotalMilliseconds;
        _blockCapacity = blockPoints;
        Directory.CreateDirectory(_archiveRoot);

        if (!durable)
            return;

        _directoryLock = ArchiveDirectoryLock.Acquire(_archiveRoot);
        _wal = new ArchiveWal(_archiveRoot, walSegmentBytes);
    }

    /// <summary>Метка времени последнего сброса журнала на диск, мс UTC (§22).</summary>
    public long LastFlushUtcMs => _wal?.LastFlushUtcMs ?? 0;

    /// <summary>
    /// Возраст самой старой записи журнала, не попавшей в блок на диске.
    /// Диагностический показатель <c>$Archive.WalLagMs</c> (§22).
    /// </summary>
    public long WalLagMs
    {
        get
        {
            if (_wal is null)
                return 0;

            lock (_writeLock)
            {
                long oldest = long.MaxValue;
                foreach (var block in _openBlocks.Values)
                {
                    if (block.FirstTimestampMs < oldest)
                        oldest = block.FirstTimestampMs;
                }

                if (oldest == long.MaxValue)
                    return 0;

                return Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - oldest);
            }
        }
    }

    /// <summary>
    /// Восстановление после аварийного завершения (§16.2): записи журнала,
    /// не попавшие в файлы потоков, возвращаются в открытые блоки.
    /// Вызывается один раз при старте, после регистрации потоков.
    /// </summary>
    /// <returns>
    /// Сколько отсчётов возвращено. Ноль при штатном предыдущем завершении.
    /// Число нужно в журнале запуска: на объекте это единственный признак
    /// того, что процесс в прошлый раз упал, а не был остановлен.
    /// </returns>
    public int RecoverFromWal()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_wal is null)
            return 0;

        int recovered = 0;

        // Точка восстановления на поток: последняя метка, дошедшая до диска.
        var recoveredUpTo = new Dictionary<int, long>();

        lock (_writeLock)
        {
            foreach (var (streamId, _) in _streams)
                recoveredUpTo[streamId] = FindLastTimestampOnDisk(streamId);

            foreach (var (streamId, point) in ArchiveWal.Replay(_archiveRoot))
            {
                if (!_streams.TryGetValue(streamId, out var config))
                    continue;

                if (recoveredUpTo.TryGetValue(streamId, out long lastOnDisk)
                    && point.TimestampUtcMs <= lastOnDisk)
                {
                    continue;
                }

                AppendPoint(streamId, point, config, journal: false);
                recovered++;
            }
        }

        return recovered;
    }

    private long FindLastTimestampOnDisk(int streamId)
    {
        long last = long.MinValue;

        foreach (string month in EnumerateExistingMonthsDescending(long.MaxValue))
        {
            string path = GetFilePath(streamId, month);
            if (!File.Exists(path))
                continue;

            byte[] file;
            try { file = File.ReadAllBytes(path); }
            catch (IOException) { continue; }

            if (file.Length < FileHeaderSize || !ValidateFileHeader(file, streamId))
                continue;

            foreach (var (_, _, header) in EnumerateBlockHeaders(file))
            {
                if (header.LastTimestampMs > last)
                    last = header.LastTimestampMs;
            }

            if (last != long.MinValue)
                break;
        }

        return last;
    }

    public void RegisterStream(int streamId, ArchiveStreamConfig config)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(streamId, 1);
        _streams[streamId] = config;
    }

    /// <summary>
    /// Запись приостановлена: место на диске кончилось, а ужать архив до пола
    /// хранения уже нельзя (ТЗ §8.9). Опрос и HMI при этом работают —
    /// заполнение диска обрабатывается как штатная ситуация, а не как отказ.
    /// </summary>
    public bool WritingSuspended { get; private set; }

    /// <summary>Отсчёты, потерянные из-за нехватки места. Диагностика §22.</summary>
    public long DroppedNoSpaceCount { get; private set; }

    /// <summary>
    /// Приостановить или возобновить запись. Возобновление сбрасывает счётчик
    /// потерь: он показывает потери текущего эпизода, а не за всё время.
    /// </summary>
    public void SuspendWriting(bool suspended)
    {
        lock (_writeLock)
        {
            if (WritingSuspended == suspended)
                return;

            WritingSuspended = suspended;
            if (!suspended)
                DroppedNoSpaceCount = 0;
        }
    }

    public void Write(int streamId, ReadOnlySpan<ArchivePoint> points)
    {
        if (points.Length == 0)
            return;

        if (!_streams.TryGetValue(streamId, out var config))
            throw new InvalidOperationException($"Поток {streamId} не зарегистрирован в архиве");

        lock (_writeLock)
        {
            if (WritingSuspended)
            {
                // Считаем потерянное, но не бросаем: исключение здесь остановило
                // бы конвейер, а с ним и сбор данных для всех остальных тегов.
                DroppedNoSpaceCount += points.Length;
                return;
            }

            foreach (var point in points)
                AppendPoint(streamId, point, config, journal: true);
        }
    }

    /// <summary>
    /// Сброс журнала на диск без закрытия блоков. Вызывается по таймеру
    /// не реже FlushIntervalMs — это и есть гарантия ТЗ §4.2.
    /// Заодно освобождает сегменты, данные которых уже дошли до файлов
    /// потоков: без этого журнал растёт всё время работы службы.
    /// </summary>
    public void FlushJournal()
    {
        if (_wal is null)
            return;

        lock (_writeLock)
        {
            _wal.Flush();
            _wal.Reclaim(OldestUncommittedTimestampMs());
        }
    }

    /// <summary>Размер журнала на диске, байт. Диагностика §22.</summary>
    public long JournalSizeBytes => _wal?.SizeBytes ?? 0;

    /// <summary>
    /// Самая ранняя метка, ещё не дошедшая до файла потока. Всё строго старше
    /// неё уже на диске: точка попадает в открытый блок сразу, а закрытие
    /// блока — это и есть запись в файл.
    /// </summary>
    private long OldestUncommittedTimestampMs()
    {
        long oldest = long.MaxValue;

        foreach (var block in _openBlocks.Values)
        {
            if (block.FirstTimestampMs < oldest)
                oldest = block.FirstTimestampMs;
        }

        return oldest;
    }

    public ValueTask FlushAsync(CancellationToken ct = default)
    {
        lock (_writeLock)
        {
            foreach (var block in _openBlocks.Values.ToArray())
                CloseBlock(block);

            _openBlocks.Clear();

            // Все открытые блоки дошли до файлов — восстанавливать нечего,
            // журнал можно отбросить целиком (§12).
            _wal?.Truncate();
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<int> ReadRawAsync(int streamId, long fromMs, long toMs,
        Memory<ArchivePoint> destination, CancellationToken ct)
    {
        EnsureRegistered(streamId);
        if (!MemoryMarshal.TryGetArray<ArchivePoint>(destination, out var destSegment) || destSegment.Array is null)
            throw new ArgumentException("Destination должен быть массивом", nameof(destination));

        var destArray = destSegment.Array;
        int destOffset = destSegment.Offset;
        int written = 0;

        await foreach (var block in ReadBlocksInRange(streamId, fromMs, toMs, ct))
        {
            foreach (var point in block.Points)
            {
                if (point.TimestampUtcMs < fromMs || point.TimestampUtcMs > toMs)
                    continue;

                if (written >= destination.Length)
                    return written;

                destArray[destOffset + written++] = point;
            }
        }

        // Дописываем открытый блок — он новее закрытых файлов.
        lock (_writeLock)
        {
            if (_openBlocks.TryGetValue(streamId, out var openBlock))
            {
                foreach (var point in openBlock.Points)
                {
                    if (point.TimestampUtcMs < fromMs || point.TimestampUtcMs > toMs)
                        continue;

                    if (written >= destination.Length)
                        return written;

                    destArray[destOffset + written++] = point;
                }
            }
        }

        return written;
    }

    public async ValueTask<int> ReadBucketsAsync(int streamId, long fromMs, long toMs,
        long bucketMs, Memory<ArchiveBucket> destination, CancellationToken ct)
    {
        EnsureRegistered(streamId);
        if (!MemoryMarshal.TryGetArray<ArchiveBucket>(destination, out var bucketSegment) || bucketSegment.Array is null)
            throw new ArgumentException("Destination должен быть массивом", nameof(destination));

        var bucketArray = bucketSegment.Array;
        int bucketOffset = bucketSegment.Offset;

        long firstBucketStart = fromMs;
        for (int i = 0; i < destination.Length; i++)
        {
            long start = firstBucketStart + i * bucketMs;
            long end = start + bucketMs;

            // NaN, а не ноль: у пустого бакета агрегата не существует, и ноль
            // здесь читался бы как измеренное значение. Признак пустоты —
            // Count == 0 (§13.1).
            bucketArray[bucketOffset + i] = new ArchiveBucket(
                start, end, double.NaN, double.NaN, double.NaN, 0, 0);
        }

        int lastFilled = -1;

        foreach (var month in EnumerateMonths(fromMs, toMs))
        {
            ct.ThrowIfCancellationRequested();

            byte[]? file = await TryReadStreamFileAsync(streamId, month, ct);
            if (file is null)
                continue;

            foreach (var (offset, length, header) in EnumerateBlockHeaders(file))
            {
                if (header.LastTimestampMs < fromMs)
                    continue;

                if (header.FirstTimestampMs > toMs)
                    break;

                // Заголовок годится как готовый агрегат, только если блок
                // целиком лежит внутри одного бакета и внутри диапазона.
                // Иначе его точки распределяются по разным бакетам, и без
                // разжатия их не разложить (§13.3, правило 2).
                if (TryUseHeaderAsBucket(header, fromMs, toMs, bucketMs, destination.Length,
                        out int headerIndex))
                {
                    lastFilled = Math.Max(lastFilled, headerIndex);
                    MergeHeaderIntoBucket(ref bucketArray[bucketOffset + headerIndex], header);
                    continue;
                }

                BlockReadResult block;
                try
                {
                    block = BlockReader.Read(file.AsSpan(offset, length));
                }
                catch (InvalidDataException)
                {
                    break;
                }

                foreach (var point in block.Points)
                {
                    if (point.TimestampUtcMs < fromMs || point.TimestampUtcMs > toMs)
                        continue;

                    int index = (int)((point.TimestampUtcMs - fromMs) / bucketMs);
                    if (index < 0 || index >= destination.Length)
                        continue;

                    lastFilled = Math.Max(lastFilled, index);
                    UpdateBucket(ref bucketArray[bucketOffset + index], point);
                }
            }
        }

        lock (_writeLock)
        {
            if (_openBlocks.TryGetValue(streamId, out var openBlock))
            {
                foreach (var point in openBlock.Points)
                {
                    if (point.TimestampUtcMs < fromMs || point.TimestampUtcMs > toMs)
                        continue;

                    int index = (int)((point.TimestampUtcMs - fromMs) / bucketMs);
                    if (index < 0 || index >= destination.Length)
                        continue;

                    lastFilled = Math.Max(lastFilled, index);
                    UpdateBucket(ref bucketArray[bucketOffset + index], point);
                }
            }
        }

        return lastFilled + 1;
    }

    public async ValueTask<ArchivePoint?> ReadAtAsync(int streamId, long atMs, CancellationToken ct)
    {
        EnsureRegistered(streamId);

        // Открытый блок новее всего, что лежит в файлах.
        ArchivePoint? candidate = FindLastInOpenBlock(streamId, atMs);
        if (candidate.HasValue)
            return candidate;

        // Идём по существующим каталогам месяцев назад от atMs. Границы поиска
        // задаются фактическими данными, а не фиксированным окном: последнее
        // изменение OnChange-тега может быть сколь угодно давним.
        foreach (var month in EnumerateExistingMonthsDescending(atMs))
        {
            ct.ThrowIfCancellationRequested();

            string path = GetFilePath(streamId, month);
            if (!File.Exists(path))
                continue;

            byte[] file = await File.ReadAllBytesAsync(path, ct);
            if (file.Length < FileHeaderSize || !ValidateFileHeader(file, streamId))
                continue;

            ArchivePoint? found = FindLastInFile(file, atMs);
            if (found.HasValue)
                return found;
        }

        return null;
    }

    private ArchivePoint? FindLastInOpenBlock(int streamId, long atMs)
    {
        lock (_writeLock)
        {
            if (!_openBlocks.TryGetValue(streamId, out var openBlock))
                return null;

            for (int i = openBlock.Points.Count - 1; i >= 0; i--)
            {
                if (openBlock.Points[i].TimestampUtcMs <= atMs)
                    return openBlock.Points[i];
            }

            return null;
        }
    }

    /// <summary>
    /// Последняя точка файла не позже atMs. Блоки в файле идут по возрастанию
    /// времени, поэтому запоминаем смещение последнего подходящего блока и
    /// разжимаем только его.
    /// </summary>
    private static ArchivePoint? FindLastInFile(byte[] file, long atMs)
    {
        int candidateOffset = -1;
        int candidateLength = 0;

        foreach (var (offset, length, header) in EnumerateBlockHeaders(file))
        {
            if (header.FirstTimestampMs > atMs)
                break;

            candidateOffset = offset;
            candidateLength = length;
        }

        if (candidateOffset < 0)
            return null;

        BlockReadResult block;
        try
        {
            block = BlockReader.Read(file.AsSpan(candidateOffset, candidateLength));
        }
        catch (InvalidDataException)
        {
            return null;
        }

        for (int i = block.Points.Length - 1; i >= 0; i--)
        {
            if (block.Points[i].TimestampUtcMs <= atMs)
                return block.Points[i];
        }

        return null;
    }

    /// <summary>
    /// Каталоги месяцев, фактически существующие в архиве и не позже месяца
    /// atMs, в порядке убывания.
    /// </summary>
    private IEnumerable<string> EnumerateExistingMonthsDescending(long atMs)
    {
        if (!Directory.Exists(_archiveRoot))
            yield break;

        int upperBound = GetMonthKey(atMs);

        var months = new List<int>();
        foreach (string directory in Directory.EnumerateDirectories(_archiveRoot))
        {
            string name = Path.GetFileName(directory);
            if (!TryParseMonthDirectory(name, out int key) || key > upperBound)
                continue;

            months.Add(key);
        }

        months.Sort();
        for (int i = months.Count - 1; i >= 0; i--)
            yield return GetMonthDirectoryFromKey(months[i]);
    }

    private static bool TryParseMonthDirectory(string name, out int monthKey)
    {
        monthKey = 0;

        if (name.Length != 7 || name[4] != '-')
            return false;

        if (!int.TryParse(name.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out int year)
            || !int.TryParse(name.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int month)
            || month is < 1 or > 12)
            return false;

        monthKey = year * 100 + month;
        return true;
    }

    /// <param name="journal">
    /// false при воспроизведении журнала: записи и так пришли из него,
    /// дублировать их обратно нельзя.
    /// </param>
    /// <summary>
    /// Проход ротации (docs/archive-format.md §15): удаляет данные, вышедшие
    /// за срок хранения.
    /// </summary>
    /// <param name="policy">Глубина хранения и пол досрочного удаления.</param>
    /// <param name="nowUtcMs">Момент отсчёта возраста.</param>
    /// <param name="forcedRetentionDays">
    /// Укороченный срок при нехватке места (ТЗ §8.9). Ограничивается снизу
    /// полом политики: ниже него не удаляется ничего и никогда, даже когда
    /// диск заполнен — это договорное обязательство, а не эвристика.
    /// </param>
    public RetentionReport ApplyRetention(IRetentionPolicy policy, long nowUtcMs,
        int? forcedRetentionDays = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int deletedFiles = 0;
        long freedBytes = 0;
        int skippedByFloor = 0;
        int monthsRemoved = 0;
        long oldestRemaining = long.MaxValue;

        lock (_writeLock)
        {
            foreach (string monthDirectory in Directory.EnumerateDirectories(_archiveRoot).ToArray())
            {
                string monthName = Path.GetFileName(monthDirectory);
                if (!TryParseMonthDirectory(monthName, out _))
                    continue;

                foreach (string file in Directory.EnumerateFiles(monthDirectory, "*.dat").ToArray())
                {
                    if (!TryParseStreamId(file, out int streamId))
                        continue;

                    int retentionDays = forcedRetentionDays ?? policy.GetRetentionDays(streamId);

                    // Пол применяется всегда, а не только в штатном режиме:
                    // именно он отличает «ужались, чтобы продолжать собирать»
                    // от «незаметно потеряли обещанную заказчику историю».
                    if (retentionDays < policy.MinRetentionDays)
                    {
                        retentionDays = policy.MinRetentionDays;
                        skippedByFloor++;
                    }

                    long cutoffMs = nowUtcMs - retentionDays * 86_400_000L;
                    long lastTimestamp = FindLastTimestampInFile(file, streamId);

                    if (lastTimestamp == long.MinValue)
                    {
                        // Файл нечитаем: заголовок битый либо чужой поток.
                        // Не удаляем — вдруг там данные, которые ещё вытащат.
                        continue;
                    }

                    // Решение принимается по меткам ВНУТРИ данных, а не по
                    // имени каталога (§15.4): неверно названный каталог иначе
                    // унёс бы свежие данные.
                    if (lastTimestamp >= cutoffMs)
                    {
                        if (lastTimestamp < oldestRemaining)
                            oldestRemaining = lastTimestamp;
                        continue;
                    }

                    // Открытый блок этого потока уже не относится к удаляемому
                    // месяцу — он закрывается по смене месяца (§8.6).
                    long size = new FileInfo(file).Length;
                    try
                    {
                        File.Delete(file);
                        deletedFiles++;
                        freedBytes += size;
                    }
                    catch (IOException)
                    {
                        // файл занят читателем — вернёмся к нему следующим проходом
                    }
                }

                if (!Directory.EnumerateFileSystemEntries(monthDirectory).Any())
                {
                    try
                    {
                        Directory.Delete(monthDirectory);
                        monthsRemoved++;
                    }
                    catch (IOException)
                    {
                        // каталог занят — удалится следующим проходом
                    }
                }
            }
        }

        return new RetentionReport(
            deletedFiles, freedBytes, skippedByFloor, monthsRemoved,
            oldestRemaining == long.MaxValue ? 0 : oldestRemaining);
    }

    private static bool TryParseStreamId(string filePath, out int streamId)
        => int.TryParse(Path.GetFileNameWithoutExtension(filePath),
            NumberStyles.None, CultureInfo.InvariantCulture, out streamId);

    /// <summary>Последняя метка времени в файле по заголовкам блоков.</summary>
    private static long FindLastTimestampInFile(string path, int streamId)
    {
        byte[] file;
        try
        {
            file = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return long.MinValue;
        }

        if (file.Length < FileHeaderSize || !ValidateFileHeader(file, streamId))
            return long.MinValue;

        long last = long.MinValue;
        foreach (var (_, _, header) in EnumerateBlockHeaders(file))
        {
            if (header.LastTimestampMs > last)
                last = header.LastTimestampMs;
        }

        return last;
    }

    private void AppendPoint(int streamId, ArchivePoint point, ArchiveStreamConfig config, bool journal)
    {
        // Монотонность проверяется здесь, а не при сборке блока (§6.3).
        // Проверка на сборке приходит слишком поздно: испорченный блок уже
        // нельзя закрыть, данные потока копятся в памяти без предела, а
        // Dispose падает, не отпустив ни каталог, ни журнал. Отказ на записи
        // оставляет поток исправным и указывает на виновника вызова.
        if (_lastWritten.TryGetValue(streamId, out long lastTimestamp)
            && point.TimestampUtcMs <= lastTimestamp)
        {
            throw new InvalidDataException(
                $"Метки времени потока {streamId} должны строго возрастать: " +
                $"{point.TimestampUtcMs} <= {lastTimestamp}.");
        }

        // В журнал — до попадания в блок: блок живёт в памяти до часа, и
        // порядок «сначала журнал» — единственное, что даёт гарантию §4.2.
        if (journal)
            _wal?.Append(streamId, point);

        _lastWritten[streamId] = point.TimestampUtcMs;

        int pointMonthKey = GetMonthKey(point.TimestampUtcMs);

        if (!_openBlocks.TryGetValue(streamId, out var openBlock))
        {
            openBlock = new OpenBlock
            {
                StreamId = streamId,
                Config = config,
                FirstTimestampMs = point.TimestampUtcMs,
                MonthKey = pointMonthKey,
                Points = new List<ArchivePoint>()
            };
            _openBlocks[streamId] = openBlock;
        }

        bool byTimeout = point.TimestampUtcMs - openBlock.FirstTimestampMs > _blockTimeoutMs;
        bool closeBlock = openBlock.MonthKey != pointMonthKey
            || byTimeout
            || openBlock.Points.Count >= _blockCapacity;

        if (closeBlock)
        {
            // Флаг различает «блок набрал 4096 отсчётов» и «блок закрыт по
            // времени»: во втором случае короткий блок — норма, а не признак
            // сбоя записи (§8.6).
            openBlock.ClosedByTimeout = byTimeout;
            CloseBlock(openBlock);

            openBlock = new OpenBlock
            {
                StreamId = streamId,
                Config = config,
                FirstTimestampMs = point.TimestampUtcMs,
                MonthKey = pointMonthKey,
                Points = new List<ArchivePoint>()
            };
            _openBlocks[streamId] = openBlock;
        }

        openBlock.Points.Add(point);
    }

    private void CloseBlock(OpenBlock block)
    {
        if (block.Points.Count == 0)
            return;

        string monthDirectory = GetMonthDirectoryFromKey(block.MonthKey);
        byte[] blockBytes = BlockBuilder.Build(
            CollectionsMarshal.AsSpan(block.Points),
            block.Config.DataType,
            block.Config.Mode,
            block.Config.Scale,
            block.Config.Offset,
            block.ClosedByTimeout);

        AppendBlockToFile(block.StreamId, monthDirectory, blockBytes);
        _openBlocks.Remove(block.StreamId);
    }

    /// <summary>
    /// Можно ли взять агрегат прямо из заголовка блока. Требуется, чтобы блок
    /// целиком помещался в один бакет и целиком лежал внутри запрошенного
    /// диапазона — тогда его summary и есть готовый ответ, разжимать нечего.
    /// Это и делает агрегаты бесплатными по месту (§8.4).
    /// </summary>
    private static bool TryUseHeaderAsBucket(in BlockHeader header, long fromMs, long toMs,
        long bucketMs, int bucketCount, out int index)
    {
        index = -1;

        // Дискретные обобщаются по времени в состоянии, а не арифметически:
        // их summary в бакет так просто не сливается (§8.5).
        if (header.DataType == TagDataType.Discrete)
            return false;

        if (header.FirstTimestampMs < fromMs || header.LastTimestampMs > toMs)
            return false;

        long firstIndex = (header.FirstTimestampMs - fromMs) / bucketMs;
        long lastIndex = (header.LastTimestampMs - fromMs) / bucketMs;
        if (firstIndex != lastIndex || firstIndex < 0 || firstIndex >= bucketCount)
            return false;

        index = (int)firstIndex;
        return true;
    }

    /// <summary>
    /// Складывает в бакет агрегат целого блока, взятый из заголовка.
    /// Правила сложения обязаны совпадать с <see cref="UpdateBucket"/>: иначе
    /// результат зависел бы от того, сработал ли быстрый путь по заголовкам,
    /// и одна и та же выборка давала бы разные тренды.
    /// </summary>
    private static void MergeHeaderIntoBucket(ref ArchiveBucket bucket, in BlockHeader header)
    {
        // Count считает все отсчёты, включая недостоверные: по нему читатель
        // отличает пропуск (Count == 0) от участка без достоверных данных.
        int count = bucket.Count + header.Count;
        int goodCount = bucket.GoodCount + header.GoodCount;

        if (!header.HasGoodValues)
        {
            bucket = bucket with { Count = count, GoodCount = goodCount };
            return;
        }

        if (bucket.GoodCount == 0)
        {
            bucket = new ArchiveBucket(bucket.StartMs, bucket.EndMs,
                header.Min, header.Max, header.Sum / header.GoodCount,
                count, goodCount);
            return;
        }

        double totalSum = bucket.Avg * bucket.GoodCount + header.Sum;
        bucket = new ArchiveBucket(bucket.StartMs, bucket.EndMs,
            Math.Min(bucket.Min, header.Min),
            Math.Max(bucket.Max, header.Max),
            totalSum / goodCount,
            count, goodCount);
    }

    /// <summary>
    /// Складывает в бакет одну точку. Min/Max/Avg считаются только по
    /// достоверным значениям (§6.2): точка перехода в Bad несёт последнее
    /// известное значение, и в агрегате ей не место.
    /// </summary>
    private static void UpdateBucket(ref ArchiveBucket bucket, ArchivePoint point)
    {
        int count = bucket.Count + 1;

        if (point.Quality != Quality.Good)
        {
            bucket = bucket with { Count = count };
            return;
        }

        int goodCount = bucket.GoodCount + 1;

        if (bucket.GoodCount == 0)
        {
            bucket = new ArchiveBucket(bucket.StartMs, bucket.EndMs,
                point.Value, point.Value, point.Value, count, goodCount);
            return;
        }

        double avg = (bucket.Avg * bucket.GoodCount + point.Value) / goodCount;
        bucket = new ArchiveBucket(bucket.StartMs, bucket.EndMs,
            Math.Min(bucket.Min, point.Value),
            Math.Max(bucket.Max, point.Value),
            avg, count, goodCount);
    }

    private void AppendBlockToFile(int streamId, string month, byte[] block)
    {
        string path = GetFilePath(streamId, month);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        if (fs.Length == 0)
            WriteFileHeader(fs, streamId);

        fs.Seek(0, SeekOrigin.End);
        fs.Write(block);
    }

    private static void WriteFileHeader(FileStream fs, int streamId)
    {
        Span<byte> header = stackalloc byte[16];
        "SCAR"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(header[6..], streamId);
        // остальные байты резерва — нули
        fs.Write(header);
    }

    private string GetFilePath(int streamId, string monthDirectory)
        => Path.Combine(_archiveRoot, monthDirectory, $"{streamId:D6}.dat");

    /// <summary>Границы, представимые в DateTimeOffset.</summary>
    private const long MinRepresentableMs = -62135596800000L;
    private const long MaxRepresentableMs = 253402300799999L;

    private static int GetMonthKey(long timestampUtcMs)
    {
        // Диапазон чтения задаёт вызывающий, и long.MaxValue как «до конца
        // времён» — законный запрос. Без ограничения он валит преобразование.
        long clamped = Math.Clamp(timestampUtcMs, MinRepresentableMs, MaxRepresentableMs);
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(clamped).UtcDateTime;
        return dt.Year * 100 + dt.Month;
    }

    private static string GetMonthDirectoryFromKey(int monthKey)
    {
        int year = monthKey / 100;
        int month = monthKey % 100;
        return new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc)
            .ToString("yyyy-MM", CultureInfo.InvariantCulture);
    }

    private static string GetMonthDirectory(long timestampUtcMs)
        => GetMonthDirectoryFromKey(GetMonthKey(timestampUtcMs));

    private void EnsureRegistered(int streamId)
    {
        if (!_streams.ContainsKey(streamId))
            throw new InvalidOperationException($"Поток {streamId} не зарегистрирован в архиве");
    }

    private async IAsyncEnumerable<BlockReadResult> ReadBlocksInRange(int streamId, long fromMs, long toMs,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var month in EnumerateMonths(fromMs, toMs))
        {
            ct.ThrowIfCancellationRequested();

            byte[]? file = await TryReadStreamFileAsync(streamId, month, ct);
            if (file is null)
                continue;

            foreach (var (offset, length, header) in EnumerateBlockHeaders(file))
            {
                if (header.LastTimestampMs < fromMs)
                    continue;

                if (header.FirstTimestampMs > toMs)
                    yield break;

                BlockReadResult result;
                try
                {
                    result = BlockReader.Read(file.AsSpan(offset, length));
                }
                catch (InvalidDataException)
                {
                    // порванный хвост — дальше по файлу не идём (§16.2)
                    break;
                }

                yield return result;
            }
        }
    }

    private async ValueTask<byte[]?> TryReadStreamFileAsync(int streamId, string month, CancellationToken ct)
    {
        string path = GetFilePath(streamId, month);
        if (!File.Exists(path))
            return null;

        byte[] file;
        try
        {
            file = await File.ReadAllBytesAsync(path, ct);
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        if (file.Length < FileHeaderSize || !ValidateFileHeader(file, streamId))
            return null;

        return file;
    }

    /// <summary>
    /// Проход по блокам файла с разбором только заголовков: полезная нагрузка
    /// не трогается. Год по одному потоку — около 7700 заголовков вместо
    /// 31 миллиона точек (§14). Обход обрывается на первом повреждении.
    /// </summary>
    private static IEnumerable<(int Offset, int Length, BlockHeader Header)> EnumerateBlockHeaders(byte[] file)
    {
        int pos = FileHeaderSize;

        while (pos < file.Length)
        {
            if (file.Length - pos < BlockReader.MinHeaderSize)
                yield break;

            int blockLength = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(pos + 2, 4));
            if (blockLength <= 0 || pos + blockLength > file.Length)
                yield break;

            if (!BlockReader.TryReadHeader(file.AsSpan(pos, blockLength), out var header))
                yield break;

            yield return (pos, blockLength, header);
            pos += blockLength;
        }
    }

    private static bool ValidateFileHeader(byte[] file, int streamId)
    {
        ReadOnlySpan<byte> header = file.AsSpan(0, 16);
        if (header[0] != (byte)'S' || header[1] != (byte)'C' ||
            header[2] != (byte)'A' || header[3] != (byte)'R')
            return false;

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        if (version != 1)
            return false;

        int fileStreamId = BinaryPrimitives.ReadInt32LittleEndian(header[6..]);
        return fileStreamId == streamId;
    }

    private static IEnumerable<string> EnumerateMonths(long fromMs, long toMs)
    {
        long clampedFrom = Math.Clamp(fromMs, MinRepresentableMs, MaxRepresentableMs);
        long clampedTo = Math.Max(clampedFrom, Math.Min(MaxRepresentableMs, toMs));

        var start = DateTimeOffset.FromUnixTimeMilliseconds(clampedFrom).UtcDateTime;
        var end = DateTimeOffset.FromUnixTimeMilliseconds(clampedTo).UtcDateTime;
        var current = new DateTime(start.Year, start.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var endMonth = new DateTime(end.Year, end.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        while (current <= endMonth)
        {
            yield return current.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            if (current.Year == endMonth.Year && current.Month == endMonth.Month)
                yield break;
            current = current.AddMonths(1);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Штатная остановка: блоки закрываются, журнал отбрасывается.
        // Исключение здесь гасится намеренно: незакрытый блок переживёт
        // перезапуск в журнале, а вот неотпущенные блокировка каталога и
        // дескрипторы журнала не дали бы службе стартовать снова.
        try
        {
            FlushAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            // диск отвалился либо блок испорчен — данные отыграются из журнала
        }

        _wal?.Dispose();
        _directoryLock?.Dispose();
    }

    private sealed class OpenBlock
    {
        public required int StreamId;
        public required ArchiveStreamConfig Config;
        public required long FirstTimestampMs;
        public required int MonthKey;
        public required List<ArchivePoint> Points;
        public bool ClosedByTimeout;
    }
}
