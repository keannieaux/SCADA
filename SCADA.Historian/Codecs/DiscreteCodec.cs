namespace SCADA.Historian;

/// <summary>
/// Кодек дискретных значений (docs/archive-format.md §10.3): один бит на отсчёт.
/// Хранится явно, чтобы не зависеть от предположения "значение всегда меняется".
/// </summary>
public static class DiscreteCodec
{
    public static void Write(BitWriter writer, ReadOnlySpan<double> values)
    {
        foreach (double v in values)
            writer.WriteBit(v != 0.0 ? 1 : 0);
    }

    public static double[] Read(ref BitReader reader, int count)
    {
        var result = new double[count];
        for (int i = 0; i < count; i++)
            result[i] = reader.ReadBit() == 1 ? 1.0 : 0.0;
        return result;
    }
}
