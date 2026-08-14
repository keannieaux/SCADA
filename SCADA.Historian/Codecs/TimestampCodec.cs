namespace SCADA.Historian;

/// <summary>
/// Кодек меток времени (docs/archive-format.md §9): delta-of-delta по схеме
/// Gorilla. Первая метка — 64 бита, вторая — разность 32 бита, далее разность
/// разностей в корзинах. Диапазоны корзин асимметричны (-63..64 и т.д.) —
/// значения сдвигаются при записи и обратно при чтении.
/// Метки обязаны строго возрастать (§6.3) — монотонность обеспечивает конвейер.
/// </summary>
public static class TimestampCodec
{
    public static void Write(BitWriter writer, ReadOnlySpan<long> timestamps)
    {
        if (timestamps.Length == 0)
            return;

        writer.WriteBits((ulong)timestamps[0], 64);
        if (timestamps.Length == 1)
            return;

        long delta = timestamps[1] - timestamps[0];
        writer.WriteBits((ulong)(uint)delta, 32);

        long prevDelta = delta;
        for (int i = 2; i < timestamps.Length; i++)
        {
            delta = timestamps[i] - timestamps[i - 1];
            long dod = delta - prevDelta;
            WriteDod(writer, dod);
            prevDelta = delta;
        }
    }

    public static long[] Read(ref BitReader reader, int count)
    {
        var result = new long[count];
        if (count == 0)
            return result;

        result[0] = (long)reader.ReadBits(64);
        if (count == 1)
            return result;

        long delta = (uint)reader.ReadBits(32);
        result[1] = result[0] + delta;

        for (int i = 2; i < count; i++)
        {
            delta += ReadDod(ref reader);
            result[i] = result[i - 1] + delta;
        }

        return result;
    }

    private static void WriteDod(BitWriter writer, long dod)
    {
        switch (dod)
        {
            case 0:
                writer.WriteBit(0);
                break;
            case >= -63 and <= 64:
                writer.WriteBits(0b10, 2);
                writer.WriteBits((ulong)(dod + 63), 7);   // 0..127
                break;
            case >= -255 and <= 256:
                writer.WriteBits(0b110, 3);
                writer.WriteBits((ulong)(dod + 255), 9);  // 0..511
                break;
            case >= -2047 and <= 2048:
                writer.WriteBits(0b1110, 4);
                writer.WriteBits((ulong)(dod + 2047), 12); // 0..4095
                break;
            default:
                if (dod < int.MinValue || dod > int.MaxValue)
                    throw new InvalidDataException(
                        $"Delta-of-delta {dod} не помещается в 32-битную корзину кодека меток времени.");

                writer.WriteBits(0b1111, 4);
                writer.WriteBits((ulong)(uint)dod, 32);   // dod по построению в int32
                break;
        }
    }

    private static long ReadDod(ref BitReader reader)
    {
        if (reader.ReadBit() == 0)
            return 0;

        int second = reader.ReadBit();
        if (second == 0)
            return (long)reader.ReadBits(7) - 63;

        int third = reader.ReadBit();
        if (third == 0)
            return (long)reader.ReadBits(9) - 255;

        int fourth = reader.ReadBit();
        if (fourth == 0)
            return (long)reader.ReadBits(12) - 2047;

        return (int)(uint)reader.ReadBits(32);
    }
}
