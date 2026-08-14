using SCADA.Core.Tags;

namespace SCADA.Historian;

/// <summary>
/// Кодек потока качества (docs/archive-format.md §11): длины серий.
/// Качество меняется редко, поэтому типичный блок — одна серия в 2–3 байта.
/// Формат: [varint длина серии][1 байт качество] × ..., пока сумма длин
/// не достигнет count.
/// </summary>
public static class QualityCodec
{
    public static void Write(BitWriter writer, ReadOnlySpan<Quality> qualities)
    {
        int i = 0;
        while (i < qualities.Length)
        {
            var quality = qualities[i];
            int run = 1;
            while (i + run < qualities.Length && qualities[i + run] == quality)
                run++;

            WriteVarint(writer, (ulong)run);
            writer.WriteBits((byte)quality, 8);
            i += run;
        }
    }

    public static Quality[] Read(ref BitReader reader, int count)
    {
        var result = new Quality[count];
        int filled = 0;

        while (filled < count)
        {
            int run = checked((int)ReadVarint(ref reader));
            var quality = (Quality)reader.ReadBits(8);

            if (filled + run > count)
                throw new InvalidDataException("Серии качества не сходятся с числом отсчётов блока");

            for (int j = 0; j < run; j++)
                result[filled + j] = quality;
            filled += run;
        }

        return result;
    }

    // varint: 7 бит данных на байт, старший бит — признак продолжения
    private static void WriteVarint(BitWriter writer, ulong value)
    {
        while (value >= 0x80)
        {
            writer.WriteBits((value & 0x7F) | 0x80, 8);
            value >>= 7;
        }
        writer.WriteBits(value, 8);
    }

    private static ulong ReadVarint(ref BitReader reader)
    {
        ulong value = 0;
        int shift = 0;
        while (true)
        {
            ulong b = reader.ReadBits(8);
            value |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return value;
            shift += 7;
        }
    }
}
