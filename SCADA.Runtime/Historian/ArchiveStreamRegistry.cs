using System.Globalization;
using System.Text;
using SCADA.Core.Tags;

namespace SCADA.Runtime.Historian;

/// <summary>
/// Файловый реестр архивных потоков (docs/archive-format.md §4.1).
/// Хранит отображение <c>имя тега → streamId</c> в файле <c>archive/streams.idx</c>.
/// Идентификаторы выдаются последовательно, начиная с 1, никогда не
/// переиспользуются; переименование тега создаёт новый поток.
/// Формат текстовый сознательно: на объекте нет редактора (ТЗ §5.4.2),
/// реестр должен читаться и чиниться подручными средствами.
/// </summary>
public sealed class ArchiveStreamRegistry : IArchiveStreamRegistry
{
    /// <summary>Разделитель полей. Имя тега, содержащее его, недопустимо.</summary>
    private const char FieldSeparator = ';';

    private readonly string _indexPath;
    private readonly Action<string> _logWarning;
    private readonly Dictionary<string, StreamEntry> _byName = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private int _nextId = 1;

    /// <summary>
    /// Загружает существующий реестр из <paramref name="archiveRoot"/>/streams.idx
    /// или создаёт пустой.
    /// </summary>
    public ArchiveStreamRegistry(string archiveRoot, Action<string>? logWarning = null)
    {
        if (string.IsNullOrWhiteSpace(archiveRoot))
            throw new ArgumentException("Корень архива не может быть пустым", nameof(archiveRoot));

        _indexPath = Path.Combine(archiveRoot, "streams.idx");
        _logWarning = logWarning ?? (_ => { });
        Directory.CreateDirectory(archiveRoot);
        Load();
    }

    /// <inheritdoc/>
    public int Resolve(string tagName, TagDataType dataType)
    {
        ArgumentException.ThrowIfNullOrEmpty(tagName);

        // Имя — ключ истории тега на годы вперёд. Разделитель в нём сделал бы
        // строку реестра неразбираемой, и при следующей загрузке тег получил бы
        // новый поток, молча оборвав историю. Поэтому отказ, а не escaping:
        // символ в имени тега не нужен, а невидимая потеря истории недопустима.
        if (tagName.Contains(FieldSeparator) || tagName.Contains('\n') || tagName.Contains('\r'))
            throw new ArgumentException(
                $"Имя тега не может содержать '{FieldSeparator}' или перевод строки: \"{tagName}\".",
                nameof(tagName));

        lock (_lock)
        {
            if (_byName.TryGetValue(tagName, out var entry))
                return entry.StreamId;

            int id = _nextId++;
            var newEntry = new StreamEntry(id, tagName, dataType, DateTimeOffset.UtcNow);
            _byName[tagName] = newEntry;
            AppendToFile(newEntry);
            return id;
        }
    }

    private void Load()
    {
        if (!File.Exists(_indexPath))
            return;

        int lineNumber = 0;
        foreach (var line in File.ReadLines(_indexPath, Encoding.UTF8))
        {
            lineNumber++;

            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                continue;

            if (!TryParse(line, out var entry))
            {
                _logWarning($"streams.idx, строка {lineNumber}: пропущена битая запись \"{line}\".");
                continue;
            }

            if (_byName.TryGetValue(entry.Name, out var existing))
            {
                // Обычно результат ручной правки. Оставляем первую запись:
                // по ней уже могли быть записаны данные, а вторая — новее и пуста.
                _logWarning(
                    $"streams.idx, строка {lineNumber}: имя \"{entry.Name}\" встречается повторно " +
                    $"(id {existing.StreamId} и {entry.StreamId}). Оставлен id {existing.StreamId}.");
                AdvanceNextId(entry.StreamId);
                continue;
            }

            _byName[entry.Name] = entry;
            AdvanceNextId(entry.StreamId);
        }
    }

    private static bool TryParse(string line, out StreamEntry entry)
    {
        entry = default;

        var parts = line.Split(FieldSeparator);
        if (parts.Length != 4)
            return false;

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int streamId)
            || streamId < 1)
            return false;

        if (parts[1].Length == 0)
            return false;

        if (!Enum.TryParse<TagDataType>(parts[2], ignoreCase: false, out var dataType))
            return false;

        if (!DateTimeOffset.TryParse(parts[3], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var createdUtc))
            return false;

        entry = new StreamEntry(streamId, parts[1], dataType, createdUtc);
        return true;
    }

    private void AdvanceNextId(int streamId)
    {
        if (streamId >= _nextId)
            _nextId = streamId + 1;
    }

    private void AppendToFile(StreamEntry entry)
    {
        bool isNewFile = !File.Exists(_indexPath) || new FileInfo(_indexPath).Length == 0;

        using var writer = new StreamWriter(_indexPath, append: true, Encoding.UTF8);
        if (isNewFile)
            writer.WriteLine("# streamId;name;dataType;createdUtc");

        writer.WriteLine(
            $"{entry.StreamId}{FieldSeparator}{entry.Name}{FieldSeparator}{entry.DataType}{FieldSeparator}" +
            entry.CreatedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
    }

    private readonly record struct StreamEntry(
        int StreamId, string Name, TagDataType DataType, DateTimeOffset CreatedUtc);
}
