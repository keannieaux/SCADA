using SCADA.Core.Tags;

namespace SCADA.Historian.Tests;

public class QualityCodecTests
{
    private static Quality[] RoundTrip(Quality[] qualities)
    {
        var writer = new BitWriter();
        QualityCodec.Write(writer, qualities);
        var reader = new BitReader(writer.ToArray());
        return QualityCodec.Read(ref reader, qualities.Length);
    }

    [Fact]
    public void RoundTrip_AllGood_OneRun()
    {
        var qualities = Enumerable.Repeat(Quality.Good, 4096).ToArray();

        var writer = new BitWriter();
        QualityCodec.Write(writer, qualities);

        Assert.Equal(qualities, RoundTrip(qualities));
        // varint(4096) = 2 байта + 1 байт качества = 24 бита на весь блок
        Assert.Equal(24, writer.BitLength);
    }

    [Fact]
    public void RoundTrip_QualityChanges()
    {
        // связь оборвалась и восстановилась: Good ×100, Bad ×50, Good ×200
        var qualities = Enumerable.Repeat(Quality.Good, 100)
            .Concat(Enumerable.Repeat(Quality.Bad, 50))
            .Concat(Enumerable.Repeat(Quality.Good, 200))
            .ToArray();

        Assert.Equal(qualities, RoundTrip(qualities));
    }

    [Fact]
    public void RoundTrip_AlternatingEveryPoint()
    {
        // худший случай: серия длиной 1 на каждый отсчёт
        var qualities = Enumerable.Range(0, 100)
            .Select(i => i % 2 == 0 ? Quality.Good : Quality.Uncertain)
            .ToArray();

        Assert.Equal(qualities, RoundTrip(qualities));
    }

    [Fact]
    public void RoundTrip_LongRun_VarintMultiByte()
    {
        // серия 1 000 000 — varint из нескольких байт
        var qualities = Enumerable.Repeat(Quality.Good, 1_000_000).ToArray();

        Assert.Equal(qualities, RoundTrip(qualities));
    }

    [Fact]
    public void RoundTrip_Empty()
    {
        Assert.Empty(RoundTrip([]));
    }

    [Fact]
    public void Read_RunExceedsCount_Throws()
    {
        // серия заявляет больше, чем отсчётов в блоке — повреждение, не мусор
        var writer = new BitWriter();
        QualityCodec.Write(writer, Enumerable.Repeat(Quality.Good, 10).ToArray());

        // BitReader — ref struct, в лямбду его не передать: ловим руками.
        var reader = new BitReader(writer.ToArray());
        try
        {
            QualityCodec.Read(ref reader, 5);
            Assert.Fail("Ожидалось InvalidDataException: серия длиннее блока");
        }
        catch (InvalidDataException)
        {
            // ожидаемо
        }
    }
}
