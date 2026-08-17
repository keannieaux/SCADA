using SCADA.Core.Tags;
using SCADA.Historian;

namespace SCADA.Historian.Tests;

public class BlockBuilderTests
{
    [Fact]
    public void ScaledInt_RoundTrip_ConstantAnalog()
    {
        var points = new ArchivePoint[100];
        long baseTime = 1_700_000_000_000L;
        for (int i = 0; i < points.Length; i++)
            points[i] = new ArchivePoint(baseTime + i * 1000, 75.31, Quality.Good);

        byte[] block = BlockBuilder.Build(points, TagDataType.Analog, LoggingMode.Periodic,
            scale: 0.01, offset: 0.0);

        BlockReadResult result = BlockReader.Read(block);

        Assert.Equal(points.Length, result.Count);
        Assert.Equal(75.31, result.Points[0].Value, precision: 10);
        Assert.Equal(LoggingMode.Periodic, result.Mode);
        Assert.Equal(TagDataType.Analog, result.DataType);
        Assert.Equal(ValueCodec.ScaledInt, result.Codec);
        Assert.Equal(75.31, result.Min, precision: 10);
        Assert.Equal(75.31, result.Max, precision: 10);
        Assert.Equal(75.31 * points.Length, result.Sum, precision: 5);
        Assert.Equal(points.Length, result.GoodCount);
    }

    [Fact]
    public void ScaledInt_RoundTrip_Ramp()
    {
        var points = new ArchivePoint[50];
        long baseTime = 1_700_000_000_000L;
        for (int i = 0; i < points.Length; i++)
            points[i] = new ArchivePoint(baseTime + i * 1000, i * 0.1, Quality.Good);

        byte[] block = BlockBuilder.Build(points, TagDataType.Analog, LoggingMode.Periodic,
            scale: 0.1, offset: 0.0);

        BlockReadResult result = BlockReader.Read(block);

        for (int i = 0; i < points.Length; i++)
            Assert.Equal(points[i].Value, result.Points[i].Value, precision: 10);
    }

    [Fact]
    public void GorillaXor_RoundTrip_FloatValues()
    {
        var points = new ArchivePoint[20];
        long baseTime = 1_700_000_000_000L;
        for (int i = 0; i < points.Length; i++)
            points[i] = new ArchivePoint(baseTime + i * 500, 3.1415926535 + i * 0.0001, Quality.Good);

        byte[] block = BlockBuilder.Build(points, TagDataType.Analog, LoggingMode.Periodic,
            scale: 0.01, offset: 0.0);

        BlockReadResult result = BlockReader.Read(block);

        Assert.Equal(ValueCodec.GorillaXor, result.Codec);
        for (int i = 0; i < points.Length; i++)
            Assert.Equal(points[i].Value, result.Points[i].Value, precision: 10);
    }

    [Fact]
    public void Discrete_RoundTrip_Toggles()
    {
        var points = new ArchivePoint[32];
        long baseTime = 1_700_000_000_000L;
        for (int i = 0; i < points.Length; i++)
        {
            double v = i % 2 == 0 ? 1.0 : 0.0;
            Quality q = i == 10 ? Quality.Bad : Quality.Good;
            points[i] = new ArchivePoint(baseTime + i * 1000, v, q);
        }

        byte[] block = BlockBuilder.Build(points, TagDataType.Discrete, LoggingMode.OnChange);

        BlockReadResult result = BlockReader.Read(block);

        Assert.Equal(ValueCodec.Discrete, result.Codec);
        Assert.Equal(31, result.Transitions); // число переключений между 32 точками
        Assert.True(result.TimeInStateOneMs > 0);   // время в состоянии 1
        Assert.Equal(31, result.GoodCount);

        for (int i = 0; i < points.Length; i++)
        {
            Assert.Equal(points[i].Value, result.Points[i].Value);
            Assert.Equal(points[i].Quality, result.Points[i].Quality);
        }
    }

    [Fact]
    public void NonMonotonicTimestamp_Throws()
    {
        var points = new[]
        {
            new ArchivePoint(1_700_000_000_000L, 1.0, Quality.Good),
            new ArchivePoint(1_700_000_000_000L, 2.0, Quality.Good) // равна предыдущей
        };

        var ex = Assert.Throws<InvalidDataException>(() =>
            BlockBuilder.Build(points, TagDataType.Analog, LoggingMode.Periodic, scale: 1.0, offset: 0.0));

        Assert.Contains("0", ex.Message); // индекс нарушившей точки
    }

    [Fact]
    public void DecreasingTimestamp_Throws()
    {
        var points = new[]
        {
            new ArchivePoint(1_700_000_000_002L, 1.0, Quality.Good),
            new ArchivePoint(1_700_000_000_001L, 2.0, Quality.Good)
        };

        Assert.Throws<InvalidDataException>(() =>
            BlockBuilder.Build(points, TagDataType.Analog, LoggingMode.Periodic, scale: 1.0, offset: 0.0));
    }

    [Fact]
    public void CrcMismatch_Throws()
    {
        var points = new[]
        {
            new ArchivePoint(1_700_000_000_000L, 1.0, Quality.Good),
            new ArchivePoint(1_700_000_001_000L, 2.0, Quality.Good)
        };

        byte[] block = BlockBuilder.Build(points, TagDataType.Analog, LoggingMode.Periodic,
            scale: 1.0, offset: 0.0);

        // портим один байт в середине
        block[20] ^= 0xFF;

        Assert.Throws<InvalidDataException>(() => BlockReader.Read(block));
    }

    [Fact]
    public void TimestampsAndQualities_RoundTrip()
    {
        var points = new ArchivePoint[10];
        long baseTime = 1_700_000_000_000L;
        for (int i = 0; i < points.Length; i++)
        {
            Quality q = i % 3 == 0 ? Quality.Uncertain : Quality.Good;
            points[i] = new ArchivePoint(baseTime + i * 2500, i, q);
        }

        byte[] block = BlockBuilder.Build(points, TagDataType.Analog, LoggingMode.Periodic,
            scale: 1.0, offset: 0.0);

        BlockReadResult result = BlockReader.Read(block);

        for (int i = 0; i < points.Length; i++)
        {
            Assert.Equal(points[i].TimestampUtcMs, result.Points[i].TimestampUtcMs);
            Assert.Equal(points[i].Quality, result.Points[i].Quality);
        }

        Assert.Equal(6, result.GoodCount);
    }
}
