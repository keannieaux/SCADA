using SCADA.Core.Alarms;
using SCADA.Core.Tags;

namespace SCADA.Alarms;

/// <summary>
/// Публикация состояния аварий системными тегами (концепт §10). Движок
/// сообщает снимок правила после каждого возможного изменения; публикатор
/// пишет в TagTable только реальные изменения — запись безусловно бампит
/// эпоху (TagTable.Write), а лишняя эпоха это лишний пересчёт схем.
///
/// Групповые агрегаты (@AlarmGroup.&lt;префикс&gt;.*) и корень (@AlarmSystem.*)
/// ведутся инкрементально: переход правила поправляет счётчики всех
/// префиксов его dotted-имени за O(глубина), без обхода правил.
///
/// Качество: замороженное правило (качество входов ≠ Good, M5-план §2.10)
/// публикуется с Uncertain — индикатор на схеме сереет общим механизмом
/// качества. Группы агрегируют известные состояния и всегда Good.
///
/// Вызывается под локом AlarmEngine — собственной синхронизации нет.
/// Правило без сгенерированных тегов (движок собран не из ProjectLoader)
/// молча пропускается.
/// </summary>
public sealed class AlarmTagPublisher
{
    private readonly ITagTable _tags;

    private sealed record RuleTagIds(TagId Active, TagId Unacked, TagId Severity);
    private sealed record GroupTagIds(TagId AnyActive, TagId AnyUnacked,
        TagId MaxSeverity, TagId Count);

    private readonly Dictionary<string, RuleTagIds> _ruleTags = new();
    private readonly Dictionary<string, GroupTagIds> _groupTags = new();
    private readonly GroupTagIds? _systemTags;
    private readonly Dictionary<string, string[]> _groupsByRule = new();

    private sealed class RuleSnapshot
    {
        public bool Active;
        public bool Unacked;
        public int Severity;
        public Quality Quality = Quality.Good;
    }

    private readonly Dictionary<string, RuleSnapshot> _snapshots = new();

    private sealed class GroupCounters
    {
        public int Active;
        public int Unacked;
        public readonly int[] ActiveBySeverity = new int[4];

        // последние записанные производные — пишем теги только при изменении;
        // нули совпадают с InitValue сгенерированных тегов — старт без записей
        public bool WrittenActive;
        public int WrittenUnacked;
        public int WrittenMaxSeverity;
        public int WrittenCount;

        public int MaxSeverity
        {
            get
            {
                for (int i = 3; i > 0; i--)
                    if (ActiveBySeverity[i] > 0)
                        return i;
                return 0;
            }
        }
    }

    private readonly Dictionary<string, GroupCounters> _groupCounters = new();
    private readonly GroupCounters _rootCounters = new();

    /// <param name="resolve">Имя системного тега → TagId (из каталога проекта).
    /// null — тега нет: соответствующая запись пропускается.</param>
    public AlarmTagPublisher(ITagTable tags, IEnumerable<string> ruleNames,
        Func<string, TagId?> resolve)
    {
        _tags = tags;

        foreach (string ruleName in ruleNames)
        {
            if (resolve(AlarmTags.RuleTag(ruleName, AlarmTags.ActiveSuffix)) is not { } active ||
                resolve(AlarmTags.RuleTag(ruleName, AlarmTags.UnackedSuffix)) is not { } unacked ||
                resolve(AlarmTags.RuleTag(ruleName, AlarmTags.SeveritySuffix)) is not { } severity)
                continue;

            _ruleTags[ruleName] = new RuleTagIds(active, unacked, severity);

            string[] paths = [..AlarmTags.GroupPaths(ruleName)];
            _groupsByRule[ruleName] = paths;
            foreach (string path in paths)
            {
                if (_groupTags.ContainsKey(path))
                    continue;
                if (resolve(AlarmTags.GroupTag(path, AlarmTags.AnyActiveSuffix)) is { } anyActive &&
                    resolve(AlarmTags.GroupTag(path, AlarmTags.AnyUnackedSuffix)) is { } anyUnacked &&
                    resolve(AlarmTags.GroupTag(path, AlarmTags.MaxSeveritySuffix)) is { } maxSeverity &&
                    resolve(AlarmTags.GroupTag(path, AlarmTags.CountSuffix)) is { } count)
                {
                    _groupTags[path] = new GroupTagIds(anyActive, anyUnacked, maxSeverity, count);
                    _groupCounters[path] = new GroupCounters();
                }
            }
        }

        if (resolve(AlarmTags.SystemTag(AlarmTags.AnyActiveSuffix)) is { } sysActive &&
            resolve(AlarmTags.SystemTag(AlarmTags.AnyUnackedSuffix)) is { } sysUnacked &&
            resolve(AlarmTags.SystemTag(AlarmTags.MaxSeveritySuffix)) is { } sysMaxSev &&
            resolve(AlarmTags.SystemTag(AlarmTags.CountSuffix)) is { } sysCount)
        {
            _systemTags = new GroupTagIds(sysActive, sysUnacked, sysMaxSev, sysCount);
        }
    }

    /// <summary>Сообщить текущий снимок правила. Движок вызывает после каждого
    /// пересчёта правила; запись в теги происходит только при изменении.</summary>
    public void OnRule(string ruleName, AlarmState state, AlarmSeverity severity,
        Quality quality, long nowUtcMs)
    {
        if (!_ruleTags.TryGetValue(ruleName, out var ids))
            return;

        bool active = state is AlarmState.ActiveUnack or AlarmState.ActiveAck;
        bool unacked = state is AlarmState.ActiveUnack or AlarmState.RtnUnack;
        int sev = (int)severity;

        if (!_snapshots.TryGetValue(ruleName, out var snap))
        {
            // совпадает с InitValue сгенерированных тегов — старт без записей
            snap = new RuleSnapshot();
            _snapshots[ruleName] = snap;
        }

        // групповые дельты считаем до обновления снимка
        if (active != snap.Active || unacked != snap.Unacked ||
            (active && sev != snap.Severity))
        {
            foreach (string path in _groupsByRule[ruleName])
                if (_groupCounters.TryGetValue(path, out var counters))
                    ApplyDelta(counters, snap, active, unacked, sev);
            ApplyDelta(_rootCounters, snap, active, unacked, sev);

            foreach (string path in _groupsByRule[ruleName])
                if (_groupCounters.TryGetValue(path, out var counters) &&
                    _groupTags.TryGetValue(path, out var groupIds))
                    PublishGroup(groupIds, counters, nowUtcMs);
            if (_systemTags is not null)
                PublishGroup(_systemTags, _rootCounters, nowUtcMs);
        }

        // теги правила — по отдельности, только изменившиеся
        if (active != snap.Active || quality != snap.Quality)
            _tags.Write(ids.Active, new TagValue(active ? 1 : 0, nowUtcMs, quality));
        if (unacked != snap.Unacked || quality != snap.Quality)
            _tags.Write(ids.Unacked, new TagValue(unacked ? 1 : 0, nowUtcMs, quality));
        if (sev != snap.Severity || quality != snap.Quality)
            _tags.Write(ids.Severity, new TagValue(sev, nowUtcMs, quality));

        snap.Active = active;
        snap.Unacked = unacked;
        snap.Severity = sev;
        snap.Quality = quality;
    }

    private static void ApplyDelta(GroupCounters counters, RuleSnapshot snap,
        bool active, bool unacked, int sev)
    {
        if (active != snap.Active)
        {
            counters.Active += active ? 1 : -1;
            if (snap.Active)
                counters.ActiveBySeverity[snap.Severity]--;
            if (active)
                counters.ActiveBySeverity[sev]++;
        }
        else if (active && sev != snap.Severity)
        {
            counters.ActiveBySeverity[snap.Severity]--;
            counters.ActiveBySeverity[sev]++;
        }
        if (unacked != snap.Unacked)
            counters.Unacked += unacked ? 1 : -1;
    }

    private void PublishGroup(GroupTagIds ids, GroupCounters counters, long nowUtcMs)
    {
        bool anyActive = counters.Active > 0;
        if (anyActive != counters.WrittenActive)
        {
            _tags.Write(ids.AnyActive, new TagValue(anyActive ? 1 : 0, nowUtcMs, Quality.Good));
            counters.WrittenActive = anyActive;
        }
        if (counters.Unacked != counters.WrittenUnacked)
        {
            _tags.Write(ids.AnyUnacked, new TagValue(counters.Unacked > 0 ? 1 : 0,
                nowUtcMs, Quality.Good));
            counters.WrittenUnacked = counters.Unacked;
        }
        if (counters.MaxSeverity != counters.WrittenMaxSeverity)
        {
            _tags.Write(ids.MaxSeverity, new TagValue(counters.MaxSeverity, nowUtcMs, Quality.Good));
            counters.WrittenMaxSeverity = counters.MaxSeverity;
        }
        if (counters.Active != counters.WrittenCount)
        {
            _tags.Write(ids.Count, new TagValue(counters.Active, nowUtcMs, Quality.Good));
            counters.WrittenCount = counters.Active;
        }
    }
}
