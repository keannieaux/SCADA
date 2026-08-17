using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;
using SCADA.Core.Tags;

namespace SCADA.Historian;

/// <summary>
/// Журнал упреждающей записи (docs/archive-format.md §12).
/// Закрывает требование ТЗ §4.2: открытый блок живёт в памяти до часа, и без
/// журнала аварийное завершение теряло бы до 4096 отсчётов на поток.
/// </summary>
/// <remarks>
/// Значение пишется как пришло, без приведения к решётке: журнал — сырой,
/// всё преобразование происходит при сборке блока.
/// </remarks>
public sealed class ArchiveWal : IDisposable
{
    /// <summary>streamId + метка + значение + качество.</summary>
    private const int PayloadSize = 4 + 8 + 8 + 1;

    /// <summary>Длина полезной части + CRC32 + сама полезная часть.</summary>
    private const int RecordSize = 4 + 4 + PayloadSize;

    private readonly string _walDirectory;
    private readonly long _segmentSizeBytes;
    private readonly byte[] _recordBuffer = new byte[RecordSize];

    /// <summary>
    /// Закрытые сегменты и последняя метка времени в каждом. Нужны, чтобы
    /// удалять журнал по мере того, как данные доходят до файлов потоков:
    /// без этого журнал растёт всё время работы службы и обгоняет сам архив.
    /// </summary>
    private readonly List<(string Path, long MaxTimestampMs)> _closedSegments = [];

    private FileStream? _current;
    private int _currentSegment;
    private long _bytesInSegment;
    private long _currentMaxTimestampMs = long.MinValue;
    private bool _disposed;

    public ArchiveWal(string archiveRoot, long segmentSizeBytes = 16L * 1024 * 1024)
    {
        _walDirectory = Path.Combine(archiveRoot, "wal");
        _segmentSizeBytes = segmentSizeBytes;
        Directory.CreateDirectory(_walDirectory);
        RegisterExistingSegments();
    }

    /// <summary>Суммарный размер журнала на диске. Диагностика §22.</summary>
    public long SizeBytes
    {
        get
        {
            long total = 0;
            foreach (string file in Directory.EnumerateFiles(_walDirectory, "*.wal"))
            {
                try { total += new FileInfo(file).Length; }
                catch (IOException) { /* файл удалён между перечислением и замером */ }
            }

            return total;
        }
    }

    /// <summary>
    /// Удаляет сегменты, целиком покрытые данными, которые уже дошли до файлов
    /// потоков (§12).
    /// </summary>
    /// <param name="oldestUncommittedMs">
    /// Самая ранняя метка, ещё не записанная в файл потока. Всё строго старше
    /// неё лежит на диске, и журнал для него больше не нужен.
    /// </param>
    /// <returns>Сколько сегментов удалено.</returns>
    public int Reclaim(long oldestUncommittedMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int removed = 0;

        for (int i = _closedSegments.Count - 1; i >= 0; i--)
        {
            var (path, maxTimestamp) = _closedSegments[i];
            if (maxTimestamp >= oldestUncommittedMs)
                continue;

            try
            {
                File.Delete(path);
                _closedSegments.RemoveAt(i);
                removed++;
            }
            catch (IOException)
            {
                // сегмент читают — вернёмся к нему следующим проходом
            }
        }

        return removed;
    }

    /// <summary>
    /// Регистрирует сегменты, оставшиеся от прошлого запуска: после
    /// восстановления они станут кандидатами на удаление наравне с новыми.
    /// </summary>
    private void RegisterExistingSegments()
    {
        var segments = Directory.GetFiles(_walDirectory, "*.wal");
        Array.Sort(segments, StringComparer.Ordinal);

        foreach (string segment in segments)
        {
            long max = ReadMaxTimestamp(segment);
            if (max != long.MinValue)
                _closedSegments.Add((segment, max));
        }
    }

    private static long ReadMaxTimestamp(string segment)
    {
        long max = long.MinValue;

        foreach (var (_, point) in ReplaySegment(segment))
        {
            if (point.TimestampUtcMs > max)
                max = point.TimestampUtcMs;
        }

        return max;
    }

    /// <summary>Число записей, добавленных с момента открытия.</summary>
    public long AppendedRecords { get; private set; }

    /// <summary>Метка времени последнего успешного сброса на диск, мс UTC.</summary>
    public long LastFlushUtcMs { get; private set; }

    public void Append(int streamId, in ArchivePoint point)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        EnsureSegment();

        Span<byte> record = _recordBuffer;
        Span<byte> payload = record[8..];

        BinaryPrimitives.WriteInt32LittleEndian(payload, streamId);
        BinaryPrimitives.WriteInt64LittleEndian(payload[4..], point.TimestampUtcMs);
        BinaryPrimitives.WriteDoubleLittleEndian(payload[12..], point.Value);
        payload[20] = (byte)point.Quality;

        BinaryPrimitives.WriteInt32LittleEndian(record, PayloadSize);
        BinaryPrimitives.WriteUInt32LittleEndian(record[4..], Crc32.HashToUInt32(payload));

        _current!.Write(record);
        _bytesInSegment += RecordSize;
        AppendedRecords++;

        if (point.TimestampUtcMs > _currentMaxTimestampMs)
            _currentMaxTimestampMs = point.TimestampUtcMs;
    }

    /// <summary>
    /// Сброс на диск. Вызывается не реже FlushIntervalMs (ТЗ §4.2) и при
    /// штатной остановке. <c>Flush(true)</c> — сброс до устройства, а не в
    /// кэш ОС: иначе гарантия «потеря не более 10 секунд» ничем не обеспечена.
    /// </summary>
    public void Flush()
    {
        if (_disposed || _current is null)
            return;

        _current.Flush(flushToDisk: true);
        LastFlushUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// Отбрасывает журнал целиком: вызывается после того, как все открытые
    /// блоки закрыты и записаны в файлы потоков — восстанавливать больше нечего.
    /// </summary>
    public void Truncate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _current?.Dispose();
        _current = null;
        _bytesInSegment = 0;
        _currentMaxTimestampMs = long.MinValue;
        _closedSegments.Clear();

        foreach (string file in Directory.EnumerateFiles(_walDirectory, "*.wal"))
        {
            try { File.Delete(file); }
            catch (IOException) { /* сегмент занят читателем — переживём до следующего раза */ }
        }

        _currentSegment = 0;
    }

    /// <summary>
    /// Читает уцелевшие записи журнала в порядке добавления. Запись с
    /// несовпавшим CRC означает порванный хвост — чтение сегмента на этом
    /// прекращается (§16.2), прочитанное до неё остаётся действительным.
    /// </summary>
    public static IEnumerable<(int StreamId, ArchivePoint Point)> Replay(string archiveRoot)
    {
        string walDirectory = Path.Combine(archiveRoot, "wal");
        if (!Directory.Exists(walDirectory))
            yield break;

        var segments = Directory.GetFiles(walDirectory, "*.wal");
        Array.Sort(segments, StringComparer.Ordinal);

        foreach (string segment in segments)
        {
            foreach (var record in ReplaySegment(segment))
                yield return record;
        }
    }

    /// <summary>
    /// Читает уцелевшие записи одного сегмента. Запись с несовпавшим CRC —
    /// порванный хвост, чтение прекращается (§16.2).
    /// </summary>
    private static IEnumerable<(int StreamId, ArchivePoint Point)> ReplaySegment(string segment)
    {
        byte[] data;
        try
        {
            // FileShare.ReadWrite обязателен: сегмент может быть открыт на
            // запись — живым писателем при диагностике либо процессом,
            // который ещё не отпустил дескриптор. File.ReadAllBytes с его
            // FileShare.Read в этом случае отказывает.
            using var stream = new FileStream(segment, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite);
            data = new byte[stream.Length];
            stream.ReadExactly(data);
        }
        catch (IOException)
        {
            yield break;
        }

        int pos = 0;
        while (pos + RecordSize <= data.Length)
        {
            var record = data.AsSpan(pos, RecordSize);

            int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(record);
            if (payloadLength != PayloadSize)
                break;

            uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
            var payload = record[8..];
            if (Crc32.HashToUInt32(payload) != storedCrc)
                break;

            int streamId = BinaryPrimitives.ReadInt32LittleEndian(payload);
            long timestampMs = BinaryPrimitives.ReadInt64LittleEndian(payload[4..]);
            double value = BinaryPrimitives.ReadDoubleLittleEndian(payload[12..]);
            var quality = (Quality)payload[20];

            yield return (streamId, new ArchivePoint(timestampMs, value, quality));
            pos += RecordSize;
        }
    }

    private void EnsureSegment()
    {
        if (_current is not null && _bytesInSegment < _segmentSizeBytes)
            return;

        if (_current is not null)
        {
            // Заполненный сегмент становится кандидатом на удаление: его
            // данные уйдут в файлы потоков по мере закрытия блоков.
            _closedSegments.Add((_current.Name, _currentMaxTimestampMs));
            _current.Dispose();
        }

        _currentSegment = NextSegmentNumber();
        _bytesInSegment = 0;
        _currentMaxTimestampMs = long.MinValue;

        string path = Path.Combine(_walDirectory,
            _currentSegment.ToString("D8", CultureInfo.InvariantCulture) + ".wal");

        _current = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
    }

    private int NextSegmentNumber()
    {
        if (_currentSegment > 0)
            return _currentSegment + 1;

        int max = 0;
        foreach (string file in Directory.EnumerateFiles(_walDirectory, "*.wal"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number > max)
                max = number;
        }

        return max + 1;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _current?.Dispose();
        _current = null;
    }
}
