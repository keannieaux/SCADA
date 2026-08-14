namespace SCADA.Historian;

/// <summary>
/// Кодек целочисленных значений (docs/archive-format.md §10.1): разность
/// разностей по units. Применяется, когда значения лежат на решётке
/// ScaleFactor (§7): стабильный тег, разгон, счётчик — 1 бит на отсчёт.
/// Диапазоны корзин асимметричны, как у кодека меток времени:
/// 7 бит покрывают -63..64, а не |dod| ≤ 64 (знаковое 7-битное — -64..63).
/// </summary>
public static class ScaledIntCodec
{
    public static void Write(BitWriter writer, ReadOnlySpan<long> units)
    {
        if (units.Length == 0)
            return;

        writer.WriteBits((ulong)units[0], 64);
        if (units.Length == 1)
            return;

        long delta = units[1] - units[0];
        writer.WriteBits((ulong)delta, 64);

        long prevDelta = delta;
        for (int i = 2; i < units.Length; i++)
        {
            delta = units[i] - units[i - 1];
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

        long delta = (long)reader.ReadBits(64);
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
                writer.WriteBits((ulong)(dod + 63), 7);
                break;
            case >= -255 and <= 256:
                writer.WriteBits(0b110, 3);
                writer.WriteBits((ulong)(dod + 255), 9);
                break;
            case >= -2047 and <= 2048:
                writer.WriteBits(0b1110, 4);
                writer.WriteBits((ulong)(dod + 2047), 12);
                break;
            case >= int.MinValue and <= int.MaxValue:
                writer.WriteBits(0b11110, 5);
                writer.WriteBits((ulong)(uint)dod, 32);
                break;
            default:
                writer.WriteBits(0b11111, 5);
                writer.WriteBits((ulong)dod, 64);
                break;
        }
    }

    private static long ReadDod(ref BitReader reader)
    {
        if (reader.ReadBit() == 0)
            return 0;

        if (reader.ReadBit() == 0)
            return (long)reader.ReadBits(7) - 63;

        if (reader.ReadBit() == 0)
            return (long)reader.ReadBits(9) - 255;

        int fourth = reader.ReadBit();
        if (fourth == 0)
            return (long)reader.ReadBits(12) - 2047;

        int fifth = reader.ReadBit();
        if (fifth == 0)
            return (int)(uint)reader.ReadBits(32);

        return (long)reader.ReadBits(64);
    }
}
