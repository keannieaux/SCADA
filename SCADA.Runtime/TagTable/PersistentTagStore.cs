using System.Text.Json;
using SCADA.Runtime.Configuration;

namespace SCADA.Runtime.TagTable;

/// <summary>
/// Персистентность internal-тегов (IsPersistent): значения переживают
/// перезапуск службы. Атомарный JSON-файл в папке проекта (ТЗ §14.6):
/// temp-файл + замена, половинчатого файла не бывает.
/// При старте персистентное значение имеет приоритет над InitValue.
/// Записи в такие теги редки (уставки, режимы) — файл на каждую запись
/// нагрузки не создаёт.
/// </summary>
public sealed class PersistentTagStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public PersistentTagStore(string path) => _path = path;

    /// <summary>Имя тега → значение. Битый файл — предупреждение и пусто:
    /// не запустить службу из-за испорченных уставок хуже, чем взять InitValue.</summary>
    public IReadOnlyDictionary<string, double> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
                return new Dictionary<string, double>();
            try
            {
                return JsonSerializer.Deserialize(File.ReadAllText(_path),
                    ProjectJsonContext.Default.DictionaryStringDouble)
                    ?? new Dictionary<string, double>();
            }
            catch (JsonException)
            {
                return new Dictionary<string, double>();
            }
        }
    }

    public void Save(string tagName, double value)
    {
        lock (_gate)
        {
            var values = new Dictionary<string, double>(Load());
            values[tagName] = value;

            string tempPath = _path + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(values,
                ProjectJsonContext.Default.DictionaryStringDouble));
            File.Move(tempPath, _path, overwrite: true);
        }
    }
}
