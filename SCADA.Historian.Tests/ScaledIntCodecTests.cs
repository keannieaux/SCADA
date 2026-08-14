namespace SCADA.Historian.Tests;

public class ScaledIntCodecTests
{
    private static long[] RoundTrip(long[] units)
    {
        var writer = new BitWriter();
        ScaledIntCodec.Write(writer, units);
        var reader = new BitReader(writer.ToArray());
        return ScaledIntCodec.Read(ref reader, units.Length);
    }

    [Fact]
    public void RoundTrip_StableTag_OneBitPerValue()
    {
        // тег стоит: все разности и dod — нули
        var units = Enumerable.Repeat(7531L, 4098).ToArray();

        var writer = new BitWriter();
        ScaledIntCodec.Write(writer, units);

        Assert.Equal(units, RoundTrip(units));
        Assert.Equal(64 + 64 + 4096, writer.BitLength); // 1 бит на отсчёт после второго
    }

    [Fact]
    public void RoundTrip_LinearRamp_OneBitPerValue()
    {
        // линейный разгон: разность постоянна → dod = 0
        var units = Enumerable.Range(0, 500).Select(i => 7531L + i * 4).ToArray();

        Assert.Equal(units, RoundTrip(units));
    }

    [Fact]
    public void RoundTrip_Counter_OneBitPerValue()
    {
        // счётчик-накопитель +17 каждый отсчёт
        var units = Enumerable.Range(0, 300).Select(i => 100_000L + i * 17L).ToArray();

        Assert.Equal(units, RoundTrip(units));
    }

    [Fact]
    public void RoundTrip_NoisyTag()
    {
        // дрожание на младший разряд АЦП: ±1..2 единицы
        var random = new Random(11);
        var units = new long[1000];
        units[0] = 7531;
        for (int i = 1; i < units.Length; i++)
            units[i] = units[i - 1] + random.Next(-2, 3);

        Assert.Equal(units, RoundTrip(units));
    }

    [Theory]
    [InlineData(64)]
    [InlineData(-63)]
    [InlineData(65)]
    [InlineData(-64)]
    [InlineData(256)]
    [InlineData(-255)]
    [InlineData(257)]
    [InlineData(2048)]
    [InlineData(-2047)]
    [InlineData(2049)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    [InlineData(5_000_000_000L)]  // за пределами int32 — 64-битная корзина
    public void RoundTrip_BucketBoundaries(long dod)
    {
        long u0 = 10_000_000_000L; // большая база, чтобы dod влезал
        long u1 = u0 + 3_000_000_000L;
        long u2 = u1 + 3_000_000_000L + dod;

        Assert.Equal([u0, u1, u2], RoundTrip([u0, u1, u2]));
    }

    [Fact]
    public void RoundTrip_SingleAndEmpty()
    {
        Assert.Equal([42L], RoundTrip([42L]));
        Assert.Empty(RoundTrip([]));
    }

    [Fact]
    public void RoundTrip_NegativeValues()
    {
        var units = new long[] { -7531, -7535, -7531, -7500, -8000 };
        Assert.Equal(units, RoundTrip(units));
    }
}
