using SCADA.Core.Tags;
using SCADA.Runtime.TagTable;

namespace SCADA.Runtime.Tests;

public class TagTableTests
{
    [Fact]
    public void Read_AfterWrite_ReturnsWrittenValue()
    {
        var table = new TagTable.TagTable(capacity: 10);
        table.Write(new TagId(3), new TagValue(42.5, 1000, Quality.Good));
        TagValue result = table.Read(new TagId(3));

        Assert.Equal(42.5, result.Value);
        Assert.Equal(1000, result.TimeStampUtc);
        Assert.Equal(Quality.Good, result.Quality);
    }
    [Fact]
    public void Read_BeforeAnyWrite_ReturnsDefault()
    {
        var table = new TagTable.TagTable(capacity: 10);

        TagValue result = table.Read(new TagId(0));

        Assert.Equal(default, result); // значение и время нулевые, Quality = Bad
    }

    [Fact]
    public async Task ConcurrentReadWrite_NeverReturnsTornValue()
    {
        ThreadPool.SetMinThreads(16, 16);
        const int tagCount = 256;
        const int writerCount = 8;
        var table = new TagTable.TagTable(tagCount);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        // 8 писателей вместо 256: каждый владеет своим диапазоном тегов
        // (как одно устройство — одна задача опроса в реальной системе).
        var writers = Enumerable.Range(0, writerCount).Select(w => Task.Run(() =>
        {
            long counter = 0;
            int start = w * (tagCount / writerCount);
            int end = start + tagCount / writerCount;
            while (!cts.IsCancellationRequested)
            {
                counter++;
                // Все три поля жёстко связаны со счётчиком, причём Quality
                // лежит в другой порции копирования структуры — разрыв виден.
                // Нечётный счётчик -> Good, чётный -> Bad. default (0,0,Bad)
                // этой проверке удовлетворяет, ложных срабатываний не будет.
                var quality = (counter & 1) == 0 ? Quality.Bad : Quality.Good;
                var value = new TagValue(counter, counter, quality);
                for (int id = start; id < end; id++)
                    table.Write(new TagId(id), value);
            }
        })).ToArray();

        int tornReads = 0;
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            var rng = new Random();
            while (!cts.IsCancellationRequested)
            {
                TagValue v = table.Read(new TagId(rng.Next(tagCount)));
                var expectedQuality = (v.TimeStampUtc & 1) == 0 ? Quality.Bad : Quality.Good;
                bool torn = (long)v.Value != v.TimeStampUtc || v.Quality != expectedQuality;
                if (torn)
                    Interlocked.Increment(ref tornReads);
            }
        })).ToArray();

        await Task.WhenAll(writers.Concat(readers));
        Assert.Equal(0, tornReads);
    }

    [Fact]
    public void GetChangedSince_ReturnsExactlyWrittenTags()
    {
        var table = new TagTable.TagTable(256);
        long before = table.CurrentEpoch;

        table.Write(new TagId(5), new TagValue(1, 1, Quality.Good));
        table.Write(new TagId(100), new TagValue(2, 2, Quality.Good));
        table.Write(new TagId(250), new TagValue(3, 3, Quality.Good));

        Span<TagId> buffer = stackalloc TagId[256];
        int count = table.GetChangedSince(before, buffer);

        Assert.Equal(3, count);
        var ids = buffer[..count].ToArray().Select(t => t.Value).ToArray();
        Assert.Equal(new[] { 5, 100, 250 }, ids); // порядок = порядок индексов
    }

    [Fact]
    public void GetChangedSince_BufferTooSmall_ReturnsFullCount_AndFillsWhatFits()
    {
        // контракт переполнения: возвращается число изменившихся ВСЕГО,
        // в буфер попадает сколько влезло. По результату итерироваться
        // нельзя — только по Math.Min(результат, длина буфера)
        var table = new TagTable.TagTable(256);
        long before = table.CurrentEpoch;

        for (int i = 0; i < 10; i++)
            table.Write(new TagId(i), new TagValue(i, 1, Quality.Good));

        Span<TagId> small = stackalloc TagId[4];
        int count = table.GetChangedSince(before, small);

        Assert.Equal(10, count);                 // сигнал: увидел не всё
        Assert.True(count > small.Length);
        var written = small.ToArray().Select(t => t.Value).ToArray();
        Assert.Equal(new[] { 0, 1, 2, 3 }, written); // заполнено до края, без мусора
    }

    [Fact]
    public void GetChangedSince_AfterCheckpoint_ReturnsNothing()
    {
        var table = new TagTable.TagTable(256);
        table.Write(new TagId(5), new TagValue(1, 1, Quality.Good));

        long checkpoint = table.CurrentEpoch; // UI «увидел» всё

        Span<TagId> buffer = stackalloc TagId[256];
        Assert.Equal(0, table.GetChangedSince(checkpoint, buffer));

        table.Write(new TagId(5), new TagValue(2, 2, Quality.Good)); // новая запись
        Assert.Equal(1, table.GetChangedSince(checkpoint, buffer));
    }

    // строковые теги (концепт §4.6): текст лежит в том же слоте,
    // эпоха изменений общая — отдельного канала для строк нет

    [Fact]
    public void WriteString_ReadString_RoundTrips()
    {
        var table = new TagTable.TagTable(capacity: 10);
        table.WriteString(new TagId(3), new StringTagValue("Насос работает", 2000, Quality.Good));

        StringTagValue result = table.ReadString(new TagId(3));

        Assert.Equal("Насос работает", result.Text);
        Assert.Equal(2000, result.TimeStampUtc);
        Assert.Equal(Quality.Good, result.Quality);
    }

    [Fact]
    public void ReadString_BeforeAnyWrite_ReturnsEmpty()
    {
        var table = new TagTable.TagTable(capacity: 10);

        StringTagValue result = table.ReadString(new TagId(0));

        Assert.Equal("", result.Text);
        Assert.Equal(Quality.Uncertain, result.Quality);
    }

    [Fact]
    public void WriteString_PreservesNumericValue()
    {
        var table = new TagTable.TagTable(capacity: 10);
        table.Write(new TagId(3), new TagValue(42.5, 1000, Quality.Good));

        table.WriteString(new TagId(3), new StringTagValue("текст", 2000, Quality.Good));

        Assert.Equal(42.5, table.Read(new TagId(3)).Value); // числовое значение не потеряно
        Assert.Equal("текст", table.ReadString(new TagId(3)).Text);
    }

    [Fact]
    public void GetChangedSince_SeesStringWrites()
    {
        var table = new TagTable.TagTable(256);
        long before = table.CurrentEpoch;

        table.WriteString(new TagId(7), new StringTagValue("текст", 1, Quality.Good));

        Span<TagId> buffer = stackalloc TagId[256];
        int count = table.GetChangedSince(before, buffer);

        Assert.Equal(1, count);
        Assert.Equal(7, buffer[0].Value);
    }
}
