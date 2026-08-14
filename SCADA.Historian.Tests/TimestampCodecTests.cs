namespace SCADA.Historian.Tests;

public class TimestampCodecTests
{
    private static long[] RoundTrip(long[] timestamps)
    {
        var writer = new BitWriter();
        TimestampCodec.Write(writer, timestamps);
        var reader = new BitReader(writer.ToArray());
        return TimestampCodec.Read(ref reader, timestamps.Length);
    }

    [Fact]
    public void RoundTrip_SingleTimestamp()
    {
        Assert.Equal([1_700_000_000_000L], RoundTrip([1_700_000_000_000L]));
    }

    [Fact]
    public void RoundTrip_RegularOneSecond_AllDodZero()
    {
        // Periodic 1 Гц: dod всегда 0 → 1 бит на метку после второй
        var ts = Enumerable.Range(0, 1000).Select(i => 1_700_000_000_000L + i * 1000L).ToArray();

        Assert.Equal(ts, RoundTrip(ts));
    }

    [Fact]
    public void RoundTrip_RegularInterval_OneBitPerTimestamp()
    {
        var ts = Enumerable.Range(0, 4098).Select(i => 1_700_000_000_000L + i * 1000L).ToArray();

        var writer = new BitWriter();
        TimestampCodec.Write(writer, ts);

        // 64 + 32 бита на первые две + по 1 биту на остальные 4096
        Assert.Equal(64 + 32 + 4096, writer.BitLength);
    }

    [Fact]
    public void RoundTrip_SchedulerJitter()
    {
        // дрожание планировщика ±50 мс — корзина 9 бит
        var random = new Random(7);
        var ts = new long[500];
        ts[0] = 1_700_000_000_000L;
        for (int i = 1; i < ts.Length; i++)
            ts[i] = ts[i - 1] + 1000 + random.Next(-50, 51);

        Assert.Equal(ts, RoundTrip(ts));
    }

    [Theory]
    [InlineData(64)]     // верхняя граница 7-битной корзины
    [InlineData(-63)]    // нижняя граница
    [InlineData(65)]     // выход вверх → 9 бит
    [InlineData(-64)]    // выход вниз → 9 бит
    [InlineData(256)]    // граница 9-битной
    [InlineData(-255)]
    [InlineData(257)]    // → 12 бит
    [InlineData(2048)]   // граница 12-битной
    [InlineData(-2047)]
    [InlineData(2049)]   // → 32 бита
    [InlineData(-2048)]
    [InlineData(100000)] // далеко за всеми корзинами
    public void RoundTrip_BucketBoundaries(long dod)
    {
        // три метки: dod между второй и третьей = заданный
        long t0 = 1_700_000_000_000L;
        long t1 = t0 + 1000;
        long t2 = t1 + 1000 + dod;

        Assert.Equal([t0, t1, t2], RoundTrip([t0, t1, t2]));
    }

    [Fact]
    public void RoundTrip_TenHertz_WithPauses()
    {
        // 10 Гц с пропусками (паузы в опросе — большие dod)
        var random = new Random(13);
        var ts = new List<long> { 1_700_000_000_000L };
        for (int i = 1; i < 300; i++)
            ts.Add(ts[^1] + (random.NextDouble() < 0.1 ? 5000 : 100));

        Assert.Equal(ts.ToArray(), RoundTrip(ts.ToArray()));
    }

    [Fact]
    public void RoundTrip_Empty()
    {
        Assert.Empty(RoundTrip([]));
    }

    [Fact]
    public void DodOutOfInt32Range_Throws()
    {
        long t0 = 1_700_000_000_000L;
        long t1 = t0 + 1000;
        // delta-of-delta за пределами int32 — кодек не может корректно записать 32-битную корзину
        long t2 = t1 + 1000 + ((long)int.MaxValue + 1);

        var writer = new BitWriter();
        Assert.Throws<InvalidDataException>(() => TimestampCodec.Write(writer, [t0, t1, t2]));
    }
}
