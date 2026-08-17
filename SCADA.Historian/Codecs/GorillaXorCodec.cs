namespace SCADA.Historian;

/// <summary>
/// XOR-кодек для double по схеме Gorilla (docs/archive-format.md §10.2).
/// Запасной кодек для значений вне решётки ScaleFactor: вычисляемые теги,
/// настоящие float из ПЛК. XOR с предыдущим значением: ноль — 1 бит,
/// иначе записывается окно значащих бит (повторное окно — короче).
/// </summary>
public static class GorillaXorCodec
{
    public static void Write(BitWriter writer, ReadOnlySpan<double> values)
    {
        if (values.Length == 0)
            return;

        ulong prev = BitConverter.DoubleToUInt64Bits(values[0]);
        writer.WriteBits(prev, 64);

        int prevLeading = 0, prevTrailing = 0;
        for (int i = 1; i < values.Length; i++)
        {
            ulong current = BitConverter.DoubleToUInt64Bits(values[i]);
            ulong xor = current ^ prev;

            if (xor == 0)
            {
                writer.WriteBit(0);
            }
            else
            {
                int leading = int.CreateChecked(ulong.LeadingZeroCount(xor));
                int trailing = int.CreateChecked(ulong.TrailingZeroCount(xor));

                writer.WriteBit(1);
                if (leading >= prevLeading && trailing >= prevTrailing)
                {
                    // окно предыдущего значения покрывает новое — только биты
                    writer.WriteBit(0);
                    int window = 64 - prevLeading - prevTrailing;
                    writer.WriteBits(xor >> prevTrailing, window);
                }
                else
                {
                    // новое окно: 5 бит ведущих нулей + 6 бит длины + биты
                    writer.WriteBit(1);
                    int window = 64 - leading - trailing;
                    writer.WriteBits((ulong)leading, 5);
                    writer.WriteBits((ulong)window, 6);
                    writer.WriteBits(xor >> trailing, window);
                    prevLeading = leading;
                    prevTrailing = trailing;
                }
            }

            prev = current;
        }
    }

    public static double[] Read(ref BitReader reader, int count)
    {
        var result = new double[count];
        if (count == 0)
            return result;

        ulong prev = reader.ReadBits(64);
        result[0] = BitConverter.UInt64BitsToDouble(prev);

        int prevLeading = 0, prevTrailing = 0;
        for (int i = 1; i < count; i++)
        {
            if (reader.ReadBit() == 0)
            {
                result[i] = result[i - 1];
                continue;
            }

            ulong xor;
            if (reader.ReadBit() == 0)
            {
                int window = 64 - prevLeading - prevTrailing;
                xor = reader.ReadBits(window) << prevTrailing;
            }
            else
            {
                int leading = (int)reader.ReadBits(5);
                int window = (int)reader.ReadBits(6);
                int trailing = 64 - leading - window;
                xor = reader.ReadBits(window) << trailing;
                prevLeading = leading;
                prevTrailing = trailing;
            }

            prev = prev ^ xor;
            result[i] = BitConverter.UInt64BitsToDouble(prev);
        }

        return result;
    }
}
