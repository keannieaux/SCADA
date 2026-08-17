namespace SCADA.Historian.Tests;

public class GorillaXorCodecTests
{
    private static double[] RoundTrip(double[] values)
    {
        var writer = new BitWriter();
        GorillaXorCodec.Write(writer, values);
        var reader = new BitReader(writer.ToArray());
        return GorillaXorCodec.Read(ref reader, values.Length);
    }

    [Fact]
    public void RoundTrip_ConstantValue_OneBitEach()
    {
        var values = Enumerable.Repeat(73.42, 500).ToArray();

        var writer = new BitWriter();
        GorillaXorCodec.Write(writer, values);

        Assert.Equal(values, RoundTrip(values));
        Assert.Equal(64 + 499, writer.BitLength); // 64 бита первое + 1 бит на повтор
    }

    [Fact]
    public void RoundTrip_SlowDrift()
    {
        // медленное дрожание температуры — типичный сигнал
        var random = new Random(5);
        var values = new double[1000];
        values[0] = 73.4;
        for (int i = 1; i < values.Length; i++)
            values[i] = values[i - 1] + random.NextDouble() * 0.02 - 0.01;

        Assert.Equal(values, RoundTrip(values));
    }

    [Fact]
    public void RoundTrip_RandomDoubles()
    {
        // худший случай: случайные значения, XOR почти не сжимает
        var random = new Random(9);
        var values = Enumerable.Range(0, 100)
            .Select(_ => random.NextDouble() * 1000 - 500)
            .ToArray();

        Assert.Equal(values, RoundTrip(values));
    }

    [Fact]
    public void RoundTrip_SpecialDoubles()
    {
        var values = new[] { 0.0, -0.0, 1.0, -1.0, double.MaxValue, double.MinValue,
                             double.Epsilon, 1e-300, 1e300 };

        Assert.Equal(values, RoundTrip(values));
    }

    [Fact]
    public void RoundTrip_SingleAndEmpty()
    {
        Assert.Equal([3.14], RoundTrip([3.14]));
        Assert.Empty(RoundTrip([]));
    }

    [Fact]
    public void RoundTrip_WindowReuseAndReset()
    {
        // чередуем похожие и резко отличающиеся значения:
        // окно переиспользуется и пересоздаётся
        var values = new double[200];
        for (int i = 0; i < values.Length; i++)
            values[i] = i % 10 == 0 ? i * 1000.0 : 73.4 + i * 0.001;

        Assert.Equal(values, RoundTrip(values));
    }
}
