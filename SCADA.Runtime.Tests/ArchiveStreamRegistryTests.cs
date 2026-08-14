using System.Text;
using SCADA.Core.Tags;
using SCADA.Runtime.Historian;

namespace SCADA.Runtime.Tests;

/// <summary>
/// Реестр потоков (docs/archive-format.md §4.1). Проверяется главное свойство:
/// идентификатор потока стабилен между запусками и не переиспользуется —
/// иначе архив сошьёт историю разных тегов.
/// </summary>
public class ArchiveStreamRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public ArchiveStreamRegistryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string IndexPath => Path.Combine(_root, "streams.idx");

    [Fact]
    public void Resolve_SameName_ReturnsSameIdWithinInstance()
    {
        var registry = new ArchiveStreamRegistry(_root);

        int first = registry.Resolve("Boiler1.Temp", TagDataType.Analog);
        int second = registry.Resolve("Boiler1.Temp", TagDataType.Analog);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Resolve_DifferentNames_NeverShareId()
    {
        var registry = new ArchiveStreamRegistry(_root);

        var ids = new HashSet<int>();
        for (int i = 0; i < 500; i++)
            Assert.True(ids.Add(registry.Resolve($"Tag{i}", TagDataType.Analog)));

        Assert.Equal(500, ids.Count);
    }

    [Fact]
    public void Reload_KeepsIdentifiers()
    {
        var first = new ArchiveStreamRegistry(_root);
        int temp = first.Resolve("Boiler1.Temp", TagDataType.Analog);
        int pump = first.Resolve("Pump1.Running", TagDataType.Discrete);

        var reloaded = new ArchiveStreamRegistry(_root);

        Assert.Equal(temp, reloaded.Resolve("Boiler1.Temp", TagDataType.Analog));
        Assert.Equal(pump, reloaded.Resolve("Pump1.Running", TagDataType.Discrete));
    }

    [Fact]
    public void RemovedTag_DoesNotReleaseItsId()
    {
        var first = new ArchiveStreamRegistry(_root);
        first.Resolve("A", TagDataType.Analog);
        int removedId = first.Resolve("B", TagDataType.Analog);
        first.Resolve("C", TagDataType.Analog);

        // "B" исчез из конфигурации, но запись реестра остаётся — новый тег
        // обязан получить свежий идентификатор, а не освободившийся.
        var reloaded = new ArchiveStreamRegistry(_root);
        int newId = reloaded.Resolve("D", TagDataType.Analog);

        Assert.NotEqual(removedId, newId);
        Assert.Equal(removedId, reloaded.Resolve("B", TagDataType.Analog));
    }

    [Fact]
    public void RenamedTag_GetsNewStream()
    {
        var registry = new ArchiveStreamRegistry(_root);
        int before = registry.Resolve("Boiler1.Temp", TagDataType.Analog);
        int after = registry.Resolve("Boiler1.Temperature", TagDataType.Analog);

        // Переименование разрывает историю явно — это лучше, чем молча
        // приписать тегу чужие данные.
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void BrokenLine_IsSkipped_OtherStreamsSurvive()
    {
        File.WriteAllLines(IndexPath,
        [
            "# streamId;name;dataType;createdUtc",
            "1;Good.One;Analog;2026-08-14T09:12:31Z",
            "мусор без разделителей",
            "2;Bad.DataType;Nonsense;2026-08-14T09:12:31Z",
            "x;Bad.Id;Analog;2026-08-14T09:12:31Z",
            "4;Good.Two;Discrete;2026-08-14T09:12:31Z"
        ], Encoding.UTF8);

        var warnings = new List<string>();
        var registry = new ArchiveStreamRegistry(_root, warnings.Add);

        Assert.Equal(1, registry.Resolve("Good.One", TagDataType.Analog));
        Assert.Equal(4, registry.Resolve("Good.Two", TagDataType.Discrete));
        Assert.Equal(3, warnings.Count);

        // Идентификаторы продолжаются от максимального прочитанного, а не от 1:
        // иначе новый тег занял бы номер, под которым уже лежат чужие файлы.
        Assert.Equal(5, registry.Resolve("New.Tag", TagDataType.Analog));
    }

    [Fact]
    public void DuplicateName_KeepsFirstIdAndWarns()
    {
        File.WriteAllLines(IndexPath,
        [
            "1;Boiler1.Temp;Analog;2026-08-14T09:12:31Z",
            "7;Boiler1.Temp;Analog;2026-08-14T10:00:00Z"
        ], Encoding.UTF8);

        var warnings = new List<string>();
        var registry = new ArchiveStreamRegistry(_root, warnings.Add);

        // Первая запись старше — по ней уже могли быть накоплены данные.
        Assert.Equal(1, registry.Resolve("Boiler1.Temp", TagDataType.Analog));
        Assert.Single(warnings);
        Assert.Equal(8, registry.Resolve("Other", TagDataType.Analog));
    }

    [Theory]
    [InlineData("Bad;Name")]
    [InlineData("Bad\nName")]
    [InlineData("Bad\rName")]
    public void NameWithSeparator_IsRejected(string name)
    {
        var registry = new ArchiveStreamRegistry(_root);

        // Молча записать такое имя нельзя: строка стала бы неразбираемой,
        // и при следующей загрузке тег получил бы новый поток.
        Assert.Throws<ArgumentException>(() => registry.Resolve(name, TagDataType.Analog));
    }

    [Fact]
    public void IndexFile_IsHumanReadable()
    {
        var registry = new ArchiveStreamRegistry(_root);
        registry.Resolve("Boiler1.Temp", TagDataType.Analog);

        string[] lines = File.ReadAllLines(IndexPath, Encoding.UTF8);

        Assert.StartsWith("#", lines[0]);
        Assert.StartsWith("1;Boiler1.Temp;Analog;", lines[1]);
        // Метка времени в ISO-8601: реестр чинят руками на объекте (ТЗ §5.4.2).
        Assert.Matches(@"^1;Boiler1\.Temp;Analog;\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", lines[1]);
    }

    [Fact]
    public void Resolve_IsThreadSafe()
    {
        var registry = new ArchiveStreamRegistry(_root);
        var results = new int[8][];

        Parallel.For(0, 8, worker =>
        {
            var local = new int[100];
            for (int i = 0; i < 100; i++)
                local[i] = registry.Resolve($"Tag{i}", TagDataType.Analog);
            results[worker] = local;
        });

        // Все потоки обязаны увидеть одно и то же отображение.
        for (int worker = 1; worker < 8; worker++)
            Assert.Equal(results[0], results[worker]);
    }
}
