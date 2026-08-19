using SCADA.Core.Tags;

namespace SCADA.Core.Alarms;

/// <summary>
/// Именование системных тегов сигнализации (концепт §10). Иерархия — как у
/// тегов: dotted-имя правила это путь в дереве; группа — любой префикс пути.
/// Единственный источник имён и для генератора каталога (ProjectLoader), и
/// для публикации состояний движком (SCADA.Alarms) — иначе они разъедутся.
///
/// <code>
/// @Alarm.&lt;ИмяПравила&gt;.{Active, Unacked, Severity}
/// @AlarmGroup.&lt;Префикс&gt;.{AnyActive, AnyUnacked, MaxSeverity, Count}
/// @AlarmSystem.{AnyActive, AnyUnacked, MaxSeverity, Count, JournalSizeMb}
/// </code>
///
/// Состав метрик append-only: новые имена добавляются в конец, существующие
/// не переименовываются — от порядка зависят генерируемые TagId.
/// </summary>
public static class AlarmTags
{
    public const string RulePrefix = "@Alarm.";
    public const string GroupPrefix = "@AlarmGroup.";
    public const string SystemName = "@AlarmSystem";

    // метрики правила
    public const string ActiveSuffix = "Active";       // 0/1
    public const string UnackedSuffix = "Unacked";     // 0/1
    public const string SeveritySuffix = "Severity";   // 0..3

    // метрики группы и корня
    public const string AnyActiveSuffix = "AnyActive";     // 0/1
    public const string AnyUnackedSuffix = "AnyUnacked";   // 0/1
    public const string MaxSeveritySuffix = "MaxSeverity"; // 0..3 по активным
    public const string CountSuffix = "Count";             // активных в ветке

    // здоровье подсистемы (только корень)
    public const string JournalSizeMbSuffix = "JournalSizeMb"; // размер журнала, МБ

    /// <summary>Метрики правила в порядке генерации (append-only).</summary>
    public static readonly (string Suffix, TagDataType DataType)[] RuleMetrics =
    [
        (ActiveSuffix, TagDataType.Discrete),
        (UnackedSuffix, TagDataType.Discrete),
        (SeveritySuffix, TagDataType.Analog),
    ];

    /// <summary>Метрики группы в порядке генерации (append-only).</summary>
    public static readonly (string Suffix, TagDataType DataType)[] GroupMetrics =
    [
        (AnyActiveSuffix, TagDataType.Discrete),
        (AnyUnackedSuffix, TagDataType.Discrete),
        (MaxSeveritySuffix, TagDataType.Analog),
        (CountSuffix, TagDataType.Analog),
    ];

    /// <summary>Метрики корня: групповые агрегаты + здоровье подсистемы
    /// (append-only).</summary>
    public static readonly (string Suffix, TagDataType DataType)[] SystemMetrics =
    [
        ..GroupMetrics,
        (JournalSizeMbSuffix, TagDataType.Analog),
    ];

    public static string RuleTag(string ruleName, string suffix) => RulePrefix + ruleName + '.' + suffix;
    public static string GroupTag(string groupPath, string suffix) => GroupPrefix + groupPath + '.' + suffix;
    public static string SystemTag(string suffix) => SystemName + '.' + suffix;

    /// <summary>Все собственные префиксы dotted-имени правила:
    /// "Цех2.Секция5.Насос7.Перегрев" → ["Цех2", "Цех2.Секция5",
    /// "Цех2.Секция5.Насос7"]. Правило без точек групп не образует.</summary>
    public static IReadOnlyList<string> GroupPaths(string ruleName)
    {
        var paths = new List<string>();
        int dot = ruleName.IndexOf('.');
        while (dot > 0)
        {
            paths.Add(ruleName[..dot]);
            dot = ruleName.IndexOf('.', dot + 1);
        }
        return paths;
    }

    /// <summary>Имя пригодно для системных тегов: каждый сегмент — непустой
    /// идентификатор выражений (буквы/цифры/подчёркивание, начало — буква или
    /// '_'). Иначе на сгенерированный тег нельзя сослаться из выражения.</summary>
    public static bool IsValidPathName(string name)
    {
        if (name.Length == 0 || name.StartsWith('@'))
            return false;
        foreach (string segment in name.Split('.'))
        {
            if (segment.Length == 0 || !(char.IsLetter(segment[0]) || segment[0] == '_'))
                return false;
            foreach (char c in segment)
                if (!char.IsLetterOrDigit(c) && c != '_')
                    return false;
        }
        return true;
    }
}
