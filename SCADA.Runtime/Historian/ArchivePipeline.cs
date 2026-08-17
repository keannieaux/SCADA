using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using SCADA.Core.Tags;
using SCADA.Historian;

namespace SCADA.Runtime.Historian;

/// <summary>
/// Фоновый конвейер архива (docs/archive-format.md §3). Читает TagTable,
/// применяет режимы логирования, правила качества и монотонности,
/// и пишет в IArchiveStore.
/// </summary>
public sealed class ArchivePipeline : BackgroundService
{
    private readonly ITagTable _tagTable;
    private readonly IArchiveStore _store;
    private readonly IArchiveStreamRegistry _streamRegistry;
    private readonly ArchivePipelineOptions _options;
    private readonly Dictionary<TagId, TagState> _states = new();

    // Накопитель на один тик: пачкой в стор дешевле, чем по точке.
    // Границу блока задаёт стор, а не этот список (§8.6).
    private readonly Dictionary<int, List<ArchivePoint>> _pending = new();

    /// <summary>Счётчик немонотонных точек, отброшенных конвейером (§6.3).</summary>
    public long DroppedNonMonotonicCount { get; private set; }

    /// <summary>Всего отсчётов передано в хранилище с момента запуска (§22).</summary>
    public long WrittenPointsCount { get; private set; }

    public ArchivePipeline(
        ITagTable tagTable,
        IArchiveStore store,
        IArchiveStreamRegistry streamRegistry,
        ProjectConfiguration config,
        ArchivePipelineOptions? options = null)
    {
        _tagTable = tagTable;
        _store = store;
        _streamRegistry = streamRegistry;
        _options = options ?? new ArchivePipelineOptions();
        Initialize(config);
    }

    internal void ProcessTick()
    {
        foreach (var state in _states.Values)
        {
            var value = _tagTable.Read(state.TagId);
            long timestampMs = value.TimeStampUtc;
            bool shouldLog = false;

            if (value.Quality == Quality.Good)
            {
                if (!state.HasEverLogged)
                    shouldLog = true;
                else if (state.LastLoggedQuality != Quality.Good)
                    shouldLog = true; // восстановление
                else
                {
                    switch (state.Mode)
                    {
                        case LoggingMode.Periodic:
                            if (timestampMs >= state.NextLogTimeMs)
                                shouldLog = true;
                            break;
                        case LoggingMode.Schedule:
                            if (timestampMs >= state.NextLogTimeMs)
                                shouldLog = true;
                            break;
                        case LoggingMode.OnChange:
                            if (BitConverter.DoubleToInt64Bits(value.Value) !=
                                BitConverter.DoubleToInt64Bits(state.LastLoggedValue))
                                shouldLog = true;
                            break;
                    }
                }
            }
            else
            {
                if (state.HasEverLogged && state.LastLoggedQuality == Quality.Good)
                    shouldLog = true; // точка перехода Good → Bad/Uncertain
            }

            if (!shouldLog)
                continue;

            if (timestampMs <= state.LastLoggedTimestampMs)
            {
                DroppedNonMonotonicCount++;
                continue;
            }

            // Квантование применяется только на записи в архив: в TagTable,
            // на мнемосхеме и в правилах сигнализации живёт полное значение.
            double archivedValue = state.Quantum is { } quantum
                ? Quantize(value.Value, quantum, state.Offset)
                : value.Value;

            var point = new ArchivePoint(timestampMs, archivedValue, value.Quality);
            EnqueuePoint(state.StreamId, point);

            state.LastLoggedTimestampMs = timestampMs;
            state.LastLoggedValue = value.Value;
            state.LastLoggedQuality = value.Quality;
            state.HasEverLogged = true;

            if (state.Mode == LoggingMode.Periodic || state.Mode == LoggingMode.Schedule)
                state.NextLogTimeMs = ComputeNextLogTimeMs(state, timestampMs);
        }
    }

    internal void FlushPending()
    {
        foreach (var (streamId, list) in _pending)
        {
            if (list.Count == 0)
                continue;

            _store.Write(streamId, CollectionsMarshal.AsSpan(list));
            WrittenPointsCount += list.Count;
            list.Clear();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.TickInterval);
        long lastJournalFlushMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long journalIntervalMs = (long)_options.FlushInterval.TotalMilliseconds;

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            ProcessTick();
            FlushPending();

            // Сброс журнала по своему расписанию, а не каждый тик: тик — 100 мс,
            // а требование ТЗ §4.2 — не реже 10 с. Сбрасывать чаще значит
            // упереться в диск без выигрыша по надёжности.
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now - lastJournalFlushMs >= journalIntervalMs)
            {
                (_store as FileArchiveStore)?.FlushJournal();
                lastJournalFlushMs = now;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        ProcessTick();
        FlushPending();
        await _store.FlushAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    private void Initialize(ProjectConfiguration config)
    {
        foreach (var tag in config.Tags)
        {
            if (!tag.IsArchived)
                continue;

            var mode = LoggingModeHelper.Infer(tag.Logging);
            double quantum = ResolveQuantum(tag);
            double offset = tag.DataType == TagDataType.Analog ? tag.ScaleOffset : 0.0;

            var archiveConfig = new ArchiveStreamConfig(tag.DataType, mode, quantum, offset);
            int streamId = _streamRegistry.Resolve(tag.Name, tag.DataType);
            _store.RegisterStream(streamId, archiveConfig);

            _states[tag.Id] = new TagState
            {
                TagId = tag.Id,
                StreamId = streamId,
                Config = archiveConfig,
                Mode = mode,
                LoggingConfig = tag.Logging,
                Quantum = tag.Precision.HasValue ? quantum : null,
                Offset = offset,
                NextLogTimeMs = 0,
                LastLoggedTimestampMs = long.MinValue,
                LastLoggedValue = 0,
                LastLoggedQuality = Quality.Good,
                HasEverLogged = false
            };
        }
    }

    /// <summary>
    /// Шаг решётки, по которой значение представляется целым (§7).
    /// </summary>
    /// <remarks>
    /// Для тега, чьё значение получено из регистра масштабированием, решётка
    /// уже есть — это <c>ScaleFactor</c>. Для вычисляемых тегов и настоящих
    /// float решётки нет, и кодек откатывается на XOR по double, который на
    /// таких данных не сжимает, а **раздувает**: замер даёт 8,4 байта на
    /// отсчёт против 8 байт несжатого значения. Объявленная точность
    /// <c>Precision</c> создаёт решётку искусственно и возвращает отсчёт
    /// к десятым долям байта.
    /// </remarks>
    private static double ResolveQuantum(TagDefinition tag)
    {
        if (tag.DataType != TagDataType.Analog)
            return 1.0;

        if (tag.Precision is not { } precision)
            return tag.ScaleFactor;

        ArgumentOutOfRangeException.ThrowIfNegative(precision, nameof(tag.Precision));
        return Math.Pow(10, -precision);
    }

    /// <summary>
    /// Приведение значения к решётке. Формула та же, которой стор проверяет
    /// принадлежность решётке, — значит проверка проходит по построению,
    /// а не по совпадению округлений.
    /// </summary>
    private static double Quantize(double value, double quantum, double offset)
        => Math.Round((value - offset) / quantum) * quantum + offset;

    private void EnqueuePoint(int streamId, ArchivePoint point)
    {
        if (!_pending.TryGetValue(streamId, out var list))
        {
            list = new List<ArchivePoint>();
            _pending[streamId] = list;
        }

        list.Add(point);
    }

    private long ComputeNextLogTimeMs(TagState state, long afterTimestampMs)
    {
        var cfg = state.LoggingConfig;

        if (state.Mode == LoggingMode.Periodic)
        {
            long intervalMs = cfg?.Interval is { } interval
                ? (long)interval.TotalMilliseconds
                : (long)_options.DefaultInterval.TotalMilliseconds;
            return afterTimestampMs + intervalMs;
        }

        // Schedule
        if (cfg is null)
            return afterTimestampMs + (long)_options.DefaultInterval.TotalMilliseconds;

        var now = DateTimeOffset.FromUnixTimeMilliseconds(afterTimestampMs);
        var last = state.LastLoggedTimestampMs == long.MinValue
            ? default
            : DateTimeOffset.FromUnixTimeMilliseconds(state.LastLoggedTimestampMs);

        var next = cfg.GetNextLoggingTime(now, last, _options.SiteTimeZone);
        return next == DateTimeOffset.MaxValue ? long.MaxValue : next.ToUnixTimeMilliseconds();
    }

    private sealed class TagState
    {
        public required TagId TagId;
        public required int StreamId;
        public required ArchiveStreamConfig Config;
        public required LoggingMode Mode;
        public TagLoggingConfiguration? LoggingConfig;

        /// <summary>Шаг решётки, если тег требует квантования; иначе null.</summary>
        public double? Quantum;

        public double Offset;
        public long NextLogTimeMs;
        public long LastLoggedTimestampMs;
        public double LastLoggedValue;
        public Quality LastLoggedQuality;
        public bool HasEverLogged;
    }
}

/// <summary>
/// Настройки ArchivePipeline.
/// </summary>
public sealed class ArchivePipelineOptions
{
    /// <summary>
    /// Период опроса TagTable. Должен быть не реже самого частого опроса
    /// устройств, иначе теряются короткие импульсы дискретных тегов (§3).
    /// </summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Интервал Periodic по умолчанию, если не задан на теге (§6).</summary>
    public TimeSpan DefaultInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Часовой пояс объекта для режима Schedule (§6). Расписание задаётся в
    /// местном времени: «08:00» — начало смены на объекте, а не 08:00 UTC.
    /// </summary>
    public TimeZoneInfo SiteTimeZone { get; set; } = TimeZoneInfo.Local;

    /// <summary>
    /// Периодичность сброса журнала на диск. Потолок задан ТЗ §4.2:
    /// потеря при аварийном завершении не более 10 секунд.
    /// </summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(10);
}
