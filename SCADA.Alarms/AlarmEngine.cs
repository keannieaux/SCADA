using SCADA.Core.Alarms;
using SCADA.Core.Tags;
using SCADA.Expressions;

namespace SCADA.Alarms;

/// <summary>
/// Реализация движка (docs/M5-plan.md §7). Одна авария на правило: уставки —
/// внутренние условия с гистерезисом, наружу агрегируются; рост severity при
/// активной аварии — эскалация с re-alert. События пишутся только на фронтах;
/// качество ≠ Good замораживает состояние (§2.10); timestamp события — время
/// фронта по часам сервера. Состояние правил публикуется системными тегами
/// @Alarm.*/@AlarmGroup.*/@AlarmSystem.* через AlarmTagPublisher (концепт §10).
/// </summary>
public sealed class AlarmEngine : IAlarmEngine
{
    private readonly object _sync = new();
    private readonly ITagTable _tags;
    private readonly IReadOnlyList<TagDefinition> _tagDefinitions;
    private readonly AlarmConfiguration _config;
    private readonly AlarmTagPublisher? _tagPublisher;
    private readonly List<RuleRuntime> _runtimes = new();
    private readonly Dictionary<int, List<RuleRuntime>> _byTagIndex = new();

    public AlarmEngine(AlarmConfiguration config, IReadOnlyList<PreparedAlarmRule> rules,
        ITagTable tags, IReadOnlyList<TagDefinition> tagDefinitions,
        AlarmTagPublisher? tagPublisher = null)
    {
        _config = config;
        _tags = tags;
        _tagDefinitions = tagDefinitions;
        _tagPublisher = tagPublisher;

        foreach (var prepared in rules)
        {
            var rt = new RuleRuntime
            {
                Rule = prepared.Rule,
                Condition = prepared.Condition,
                TagIndices = prepared.TagIndices,
                MinDurationMs = prepared.Rule.MinDurationMs ?? config.Defaults.MinDurationMs,
                Limits = prepared.Rule.Type == AlarmType.Threshold
                    ? (prepared.Rule.Limits ?? Array.Empty<ThresholdLimit>())
                        .Select(l => new LimitState { Limit = l }).ToArray()
                    : []
            };
            AddRuntime(rt);
        }
    }

    public IReadOnlyList<AlarmEvent> EvaluateTag(TagId tag, long nowUtcMs)
    {
        lock (_sync)
        {
            if (!_byTagIndex.TryGetValue(tag.Value, out var affected))
                return Array.Empty<AlarmEvent>();

            var events = new List<AlarmEvent>();
            foreach (var rt in affected)
            {
                if (!AllTagsGood(rt))
                {
                    // §2.10: состояние заморожено — на схеме видно как Uncertain
                    Publish(rt, Quality.Uncertain, nowUtcMs);
                    continue;
                }

                var (cond, highest) = EvaluateCondition(rt);
                Process(rt, cond, highest, nowUtcMs, events);
                Publish(rt, Quality.Good, nowUtcMs);
            }
            return events;
        }
    }

    public IReadOnlyList<AlarmEvent> Tick(long nowUtcMs)
    {
        lock (_sync)
        {
            var events = new List<AlarmEvent>();
            foreach (var rt in _runtimes)
            {
                if (!rt.HasPendingFront)
                    continue;
                if (!AllTagsGood(rt))
                    continue;
                TryFireActive(rt, nowUtcMs, events);
                Publish(rt, Quality.Good, nowUtcMs);
            }
            return events;
        }
    }

    public IReadOnlyList<AlarmEvent> EvaluateAll(long nowUtcMs)
    {
        lock (_sync)
        {
            var events = new List<AlarmEvent>();
            foreach (var rt in _runtimes)
            {
                if (!AllTagsGood(rt))
                {
                    Publish(rt, Quality.Uncertain, nowUtcMs);
                    continue;
                }
                var (cond, highest) = EvaluateCondition(rt);
                Process(rt, cond, highest, nowUtcMs, events);
                Publish(rt, Quality.Good, nowUtcMs);
            }
            return events;
        }
    }

    public AlarmEvent? Acknowledge(string ruleName,
        string acknowledgedBy, string? comment, long nowUtcMs)
    {
        lock (_sync)
        {
            var rt = _runtimes.FirstOrDefault(r => r.Rule.Name == ruleName);
            if (rt is null)
                return null;

            switch (rt.State)
            {
                case AlarmState.ActiveUnack:
                    rt.State = AlarmState.ActiveAck;
                    break;
                case AlarmState.RtnUnack:
                    rt.State = AlarmState.Normal;
                    break;
                default:
                    return null; // квитировать нечего
            }

            rt.AcknowledgedBy = acknowledgedBy;
            Publish(rt, Quality.Good, nowUtcMs);

            return new AlarmEvent(
                Id: default,
                TimestampUtcMs: nowUtcMs,
                RuleName: rt.Rule.Name,
                Limit: rt.ActiveLimit?.Kind,
                Type: AlarmEventType.Acknowledged,
                Message: rt.Rule.Description,
                Severity: rt.Severity,
                Area: rt.Area,
                TagSnapshots: Array.Empty<AlarmTagSnapshot>(),
                AcknowledgedBy: acknowledgedBy,
                AckComment: comment,
                AcknowledgedAtUtcMs: nowUtcMs);
        }
    }

    public IReadOnlyList<ActiveAlarm> GetActive(AlarmFilter filter)
    {
        lock (_sync)
        {
            return _runtimes
                .Where(rt => rt.State != AlarmState.Normal)
                .Select(rt => ToActiveAlarm(rt))
                .Where(a => filter.MinSeverity is null || a.Severity >= filter.MinSeverity)
                .Where(a => filter.Area is null || a.Area == filter.Area)
                .Where(a => filter.UnacknowledgedOnly != true
                    || a.State is AlarmState.ActiveUnack or AlarmState.RtnUnack)
                .ToArray();
        }
    }

    public bool IsActive(string ruleName)
    {
        lock (_sync)
        {
            return _runtimes.Any(rt => rt.Rule.Name == ruleName
                && rt.State is AlarmState.ActiveUnack or AlarmState.ActiveAck);
        }
    }

    public ActiveAlarm? GetAlarm(string ruleName)
    {
        lock (_sync)
        {
            var rt = _runtimes.FirstOrDefault(r => r.Rule.Name == ruleName);
            return rt is null ? null : ToActiveAlarm(rt);
        }
    }

    public void RestoreRecovered(IEnumerable<RecoveredAlarmState> states)
    {
        lock (_sync)
        {
            foreach (var state in states)
            {
                var rt = _runtimes.FirstOrDefault(r => r.Rule.Name == state.RuleName);
                if (rt is null)
                    continue; // правило удалено из конфигурации — журнал старше проекта

                rt.State = state.State;
                rt.ActivatedAtUtcMs = state.ActivatedAtUtcMs;
                rt.AcknowledgedBy = state.AcknowledgedBy;
                // иначе первая сверка (EvaluateAll) примет старшую уставку
                // за новую эскалацию
                rt.ActiveLimit = state.Limit is { } kind
                    ? rt.Limits.FirstOrDefault(l => l.Limit.Kind == kind)?.Limit
                    : null;

                // для активных считаем условие истинным — иначе первый же
                // пересчёт не сможет зафиксировать возврат в норму, если за
                // простой значение ушло (§7.3)
                rt.ConditionActive = state.State is AlarmState.ActiveUnack or AlarmState.ActiveAck;
                rt.ConditionTrueSinceUtcMs = rt.ConditionActive ? state.ActivatedAtUtcMs : null;

                // системные теги отражают восстановленное состояние сразу,
                // не дожидаясь первой сверки
                Publish(rt, Quality.Good, state.ActivatedAtUtcMs);
            }
        }
    }

    // --- внутрянка ---

    /// <summary>Публикация снимка правила системными тегами (концепт §10);
    /// писать или нет — решает публикатор сравнением с прошлым снимком.</summary>
    private void Publish(RuleRuntime rt, Quality quality, long nowUtcMs)
        => _tagPublisher?.OnRule(rt.Rule.Name, rt.State, rt.Severity, quality, nowUtcMs);

    private void AddRuntime(RuleRuntime rt)
    {
        _runtimes.Add(rt);
        foreach (int index in rt.TagIndices)
        {
            if (!_byTagIndex.TryGetValue(index, out var list))
                _byTagIndex[index] = list = new List<RuleRuntime>();
            list.Add(rt);
        }
    }

    private void Process(RuleRuntime rt, bool cond, ThresholdLimit? highest,
        long nowUtcMs, List<AlarmEvent> events)
    {
        if (cond && !rt.ConditionActive)
        {
            // фронт false→true; при MinDuration > 0 событие откладывается
            rt.ConditionActive = true;
            rt.ConditionTrueSinceUtcMs = nowUtcMs;
            rt.ActiveLimit = highest;
            TryFireActive(rt, nowUtcMs, events);
        }
        else if (!cond && rt.ConditionActive)
        {
            rt.ConditionActive = false;
            rt.ConditionTrueSinceUtcMs = null;
            if (rt.State is AlarmState.ActiveUnack or AlarmState.ActiveAck)
                FireNormal(rt, nowUtcMs, events);
            rt.ActiveLimit = null;
            // отложенный, но не выстреливший фронт просто отменяется
        }
        else if (cond && rt.HasPendingFront)
        {
            rt.ActiveLimit = highest ?? rt.ActiveLimit;
            TryFireActive(rt, nowUtcMs, events);
        }
        else if (cond && rt.State is AlarmState.ActiveUnack or AlarmState.ActiveAck)
        {
            CheckEscalation(rt, highest, nowUtcMs, events);
        }
    }

    private void TryFireActive(RuleRuntime rt, long nowUtcMs, List<AlarmEvent> events)
    {
        long since = rt.ConditionTrueSinceUtcMs ?? nowUtcMs;
        if (nowUtcMs - since < rt.MinDurationMs)
            return;

        rt.ActivatedAtUtcMs = since;
        rt.AcknowledgedBy = null;
        rt.State = rt.Rule.RequiresAck ? AlarmState.ActiveUnack : AlarmState.ActiveAck;
        events.Add(BuildEvent(rt, AlarmEventType.Active, since, withSnapshots: true));
    }

    private void CheckEscalation(RuleRuntime rt, ThresholdLimit? highest,
        long nowUtcMs, List<AlarmEvent> events)
    {
        if (highest is null)
            return;

        var currentSeverity = rt.ActiveLimit?.Severity ?? rt.Rule.Severity;
        var newSeverity = highest.Severity ?? rt.Rule.Severity;

        if (newSeverity < currentSeverity)
        {
            rt.ActiveLimit = highest; // деэскалация — тихо, без событий
            return;
        }
        if (newSeverity == currentSeverity)
        {
            rt.ActiveLimit = highest; // та же важность — обновляем уставку без события
            return;
        }

        // эскалация: та же авария, новая важность. Квитированная авария
        // снова становится неквитированной — re-alert (§7.1)
        rt.ActiveLimit = highest;
        if (rt.State == AlarmState.ActiveAck)
        {
            rt.State = AlarmState.ActiveUnack;
            rt.AcknowledgedBy = null;
        }
        events.Add(BuildEvent(rt, AlarmEventType.Escalated, nowUtcMs, withSnapshots: true));
    }

    private void FireNormal(RuleRuntime rt, long nowUtcMs, List<AlarmEvent> events)
    {
        rt.State = rt.State == AlarmState.ActiveUnack
            ? AlarmState.RtnUnack
            : AlarmState.Normal;
        events.Add(BuildEvent(rt, AlarmEventType.Normal, nowUtcMs, withSnapshots: false));
    }

    /// <summary>Условие правила: агрегат по уставкам для Threshold
    /// (возвращает старшую сработавшую), значение выражения для Expression.</summary>
    private (bool Condition, ThresholdLimit? Highest) EvaluateCondition(RuleRuntime rt)
    {
        if (rt.Rule.Type == AlarmType.Expression)
        {
            // время заполняем и здесь: правила могут пользоваться now()
            // (например, «условие держится дольше смены»), семантика та же, что на схемах
            bool cond = ExpressionVM.Evaluate(rt.Condition!.Value,
                new EvaluationContext
                {
                    Tags = _tags,
                    NowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }) != 0.0;
            return (cond, null);
        }

        // Threshold: у каждой уставки свой гистерезис и своё прошлое состояние
        double value = _tags.Read(new TagId(rt.TagIndices[0])).Value;
        ThresholdLimit? highest = null;
        foreach (var ls in rt.Limits)
        {
            bool highSide = ls.Limit.Kind is ThresholdKind.Hi or ThresholdKind.HiHi;
            double threshold = ls.Active
                ? (highSide ? ls.Limit.Value - rt.Rule.Hysteresis : ls.Limit.Value + rt.Rule.Hysteresis)
                : ls.Limit.Value;
            ls.Active = highSide ? value >= threshold : value <= threshold;
            if (ls.Active && (highest is null || ls.Limit.Kind > highest.Kind))
                highest = ls.Limit;
        }
        return (highest is not null, highest);
    }

    // §2.10: при качестве ≠ Good хотя бы у одного участвующего тега
    // пересчёт пропускается, состояние заморожено
    private bool AllTagsGood(RuleRuntime rt)
    {
        foreach (int index in rt.TagIndices)
            if (_tags.Read(new TagId(index)).Quality != Quality.Good)
                return false;
        return true;
    }

    private AlarmEvent BuildEvent(RuleRuntime rt, AlarmEventType type, long timestampUtcMs,
        bool withSnapshots)
    {
        var snapshots = withSnapshots
            ? rt.TagIndices.Select(i =>
            {
                var v = _tags.Read(new TagId(i));
                return new AlarmTagSnapshot(new TagId(i), TagName(i), v.Value, v.Quality);
            }).ToArray()
            : Array.Empty<AlarmTagSnapshot>();

        return new AlarmEvent(
            Id: default,
            TimestampUtcMs: timestampUtcMs,
            RuleName: rt.Rule.Name,
            Limit: rt.ActiveLimit?.Kind,
            Type: type,
            Message: RenderMessage(rt, type, snapshots),
            Severity: rt.Severity,
            Area: rt.Area,
            TagSnapshots: snapshots);
    }

    private ActiveAlarm ToActiveAlarm(RuleRuntime rt) => new(
        rt.Rule.Name, rt.ActiveLimit?.Kind, rt.State, rt.Severity, rt.Area,
        RenderMessage(rt, rt.State is AlarmState.Normal or AlarmState.RtnUnack
            ? AlarmEventType.Normal : AlarmEventType.Active),
        rt.ActivatedAtUtcMs, rt.AcknowledgedBy);

    private string RenderMessage(RuleRuntime rt, AlarmEventType type,
        IReadOnlyList<AlarmTagSnapshot>? snapshots = null)
    {
        string template = rt.Rule.MessageTemplate
            ?? (_config.Templates.TryGetValue(
                    MessageRenderer.TemplateKey(rt.Rule.Type, type), out string? t)
                ? t
                : "{Severity}: {Description}");
        return MessageRenderer.Render(template, rt, type,
            snapshots ?? Array.Empty<AlarmTagSnapshot>(), _tagDefinitions);
    }

    private string TagName(int index)
        => index < _tagDefinitions.Count ? _tagDefinitions[index].Name : $"#{index}";
}
