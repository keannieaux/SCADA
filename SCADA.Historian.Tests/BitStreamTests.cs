namespace SCADA.Historian.Tests;

public class BitStreamTests
{
    [Fact]
    public void RoundTrip_SingleBits()
    {
        var writer = new BitWriter();
        writer.WriteBit(1);
        writer.WriteBit(0);
        writer.WriteBit(1);
        writer.WriteBit(1);

        var reader = new BitReader(writer.ToArray());

        Assert.Equal(1, reader.ReadBit());
        Assert.Equal(0, reader.ReadBit());
        Assert.Equal(1, reader.ReadBit());
        Assert.Equal(1, reader.ReadBit());
    }

    [Fact]
    public void RoundTrip_MultiBitValues()
    {
        var writer = new BitWriter();
        writer.WriteBits(0b101, 3);        // 3 бита
        writer.WriteBits(300, 9);          // 9 бит — пересекает границу байта
        writer.WriteBits(ulong.MaxValue, 64); // 64 бита

        var reader = new BitReader(writer.ToArray());

        Assert.Equal(0b101UL, reader.ReadBits(3));
        Assert.Equal(300UL, reader.ReadBits(9));
        Assert.Equal(ulong.MaxValue, reader.ReadBits(64));
    }

    [Fact]
    public void RoundTrip_LongMixedSequence()
    {
        var random = new Random(42);
        var expected = new (ulong value, int bits)[1000];
        var writer = new BitWriter();

        for (int i = 0; i < expected.Length; i++)
        {
            int bits = random.Next(1, 65);
            // маска по числу бит: 1L << 63 переполняется, поэтому так
            ulong raw = (ulong)random.NextInt64();
            ulong value = bits == 64 ? raw : raw & ((1UL << bits) - 1);
            expected[i] = (value, bits);
            writer.WriteBits(value, bits);
        }

        var reader = new BitReader(writer.ToArray());
        foreach (var (value, bits) in expected)
            Assert.Equal(value, reader.ReadBits(bits));
    }

    [Fact]
    public void ToArray_TailIsZeroPadded()
    {
        var writer = new BitWriter();
        writer.WriteBit(1); // только первый бит первого байта

        var bytes = writer.ToArray();

        Assert.Single(bytes);
        Assert.Equal(0b1000_0000, bytes[0]);
    }

    [Fact]
    public void Read_PastEnd_Throws()
    {
        var writer = new BitWriter();
        writer.WriteBits(1, 3);

        // BitReader — ref struct, в лямбду его не передать: ловим руками.
        var reader = new BitReader(writer.ToArray());
        reader.ReadBits(8); // добитый хвост читается как нули

        try
        {
            reader.ReadBit();
            Assert.Fail("Ожидалось InvalidDataException при выходе за границу потока");
        }
        catch (InvalidDataException)
        {
            // ожидаемо
        }
    }
}
