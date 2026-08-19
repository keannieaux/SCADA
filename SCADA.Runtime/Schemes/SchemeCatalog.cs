using SCADA.Core.Schemes;
using SCADA.Core.Tags;
using SCADA.Package;
using SCADA.Package.Sections;

namespace SCADA.Runtime.Schemes;

/// <summary>Запись списка схем для навигации (меню, «открыть экран»).
/// IsStart — стартовый экран проекта (project.json → манифест пакета);
/// если ни одна не помечена, UI открывает первую по алфавиту.
/// Позже дополнится хэшем содержимого — remote-клиенты будут кэшировать
/// схемы по хэшу и не гонять неизменные секции по сети.</summary>
public sealed record SchemeInfo(Guid Id, string Name, bool IsStart);

/// <summary>
/// Неизменяемый каталог статических данных проекта: схемы, шаблоны, пул
/// скомпилированных выражений, имена тегов и ассеты. Строится один раз при
/// старте хоста из .scadapkg и не меняется в течение сессии — экраны
/// разделяют одни экземпляры без клонирования, а задел под hot reload схем
/// сводится к атомарной подмене ссылки на каталог.
///
/// Схемы и пул уже в памяти (загружены из бинарных секций); ассеты читаются
/// лениво из пакета по запросу — картинки/шрифты могут быть большими, а
/// обращения редкими (кэширование — забота UI).
///
/// Remote-задел: gRPC-реализация контракта вольна передавать сырые байты
/// секций и парсить их SchemeSectionReader на клиенте — сервер схемы
/// не парсит вообще.
/// </summary>
public sealed class SchemeCatalog
{
    /// <summary>Префиксы секций-ассетов в пакете (см. ProjectBuildService).</summary>
    private static readonly string[] AssetPrefixes = ["symbols/", "images/", "fonts/"];

    private readonly Dictionary<string, Scheme> _schemesByName;
    private readonly HashSet<string> _assetSet;
    private readonly string _packagePath;

    public SchemeCatalog(
        ProjectConfiguration config,
        CodePool codePool,
        IReadOnlyDictionary<string, TagId> tagsByName,
        IReadOnlyList<string> manifestEntryNames,
        string packagePath)
    {
        CodePool = codePool;
        TagsByName = tagsByName;

        // дубликат имени — битый проект: валим старт, а не гадаем в рантайме
        _schemesByName = new Dictionary<string, Scheme>(StringComparer.Ordinal);
        foreach (var scheme in config.Schemes)
            if (!_schemesByName.TryAdd(scheme.Name, scheme))
                throw new InvalidOperationException(
                    $"Дубликат имени схемы '{scheme.Name}' в пакете");

        var templatesByName = new Dictionary<string, SchemeTemplate>(StringComparer.Ordinal);
        foreach (var template in config.Templates)
            if (!templatesByName.TryAdd(template.Name, template))
                throw new InvalidOperationException(
                    $"Дубликат имени шаблона '{template.Name}' в пакете");

        Schemes = config.Schemes
            .Select(s => new SchemeInfo(s.Id, s.Name,
                string.Equals(s.Name, config.StartScheme, StringComparison.Ordinal)))
            .ToArray();
        Templates = config.Templates;
        _packagePath = packagePath;

        Assets = manifestEntryNames
            .Where(n => AssetPrefixes.Any(n.StartsWith))
            .ToArray();
        _assetSet = new HashSet<string>(Assets, StringComparer.Ordinal);
    }

    /// <summary>Пул байткода проекта: привязки и условия действий ссылаются
    /// на него через CompiledExpressionIndex (вычисление — ExpressionVM на клиенте).</summary>
    public CodePool CodePool { get; }

    /// <summary>Разрешение имён тегов из действий (WriteTag и т.п.) и
    /// параметрических ссылок шаблонов. Включает системные теги (@Alarm.*).</summary>
    public IReadOnlyDictionary<string, TagId> TagsByName { get; }

    public IReadOnlyList<SchemeInfo> Schemes { get; }

    public IReadOnlyList<SchemeTemplate> Templates { get; }

    /// <summary>Имена секций-ассетов в пакете ("symbols/valve.svg" и т.п.).</summary>
    public IReadOnlyList<string> Assets { get; }

    /// <summary>Скомпилированная схема по имени. Неизменна — можно кэшировать.</summary>
    public Scheme GetScheme(string name)
        => _schemesByName.TryGetValue(name, out var scheme)
            ? scheme
            : throw new KeyNotFoundException($"Схема '{name}' не найдена в пакете");

    /// <summary>Байты ассета из пакета. Чтение ленивое: каждый вызов открывает
    /// пакет заново — UI обязан кэшировать результат (символы повторяются).</summary>
    public byte[] GetAsset(string path)
    {
        // читаем только известные секции-ассеты: произвольные entries пакета
        // (tags.bin и т.п.) через этот путь недоступны
        if (!_assetSet.Contains(path))
            throw new KeyNotFoundException($"Ассет '{path}' не найден в пакете");
        using var reader = PackageReader.Open(_packagePath);
        return reader.ReadEntry(path);
    }
}
