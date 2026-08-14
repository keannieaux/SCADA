using System.Buffers.Binary;
using System.IO.Hashing;
using SCADA.Core.Tags;

namespace SCADA.Historian;

/// <summary>
/// Сборщик архивного блока (docs/archive-format.md §8.3): из набора точек
/// формирует самодостаточный байтовый блок с summary, флагами и CRC32.
/// </summary>
public static class BlockBuilder
{
    public static byte[] Build(
        ReadOnlySpan<ArchivePoint> points,
        TagDataType dataType,
        LoggingMode mode,
        double scale = 1.0,
        double offset = 0.0,
        bool closedByTimeout = false)
    {
        if (points.Length == 0)
            throw new ArgumentException("Блок не может быть пустым", nameof(points));

        int count = points.Length;
        var timestamps = new long[count];
        var values = new double[count];
        var qualities = new Quality[count];

        for (int i = 0; i < count; i++)
        {
            timestamps[i] = points[i].TimestampUtcMs;
            values[i] = points[i].Value;
            qualities[i] = points[i].Quality;
        }

        for (int i = 1; i < count; i++)
        {
            if (timestamps[i] <= timestamps[i - 1])
                throw new InvalidDataException(
                    $"Метки времени в блоке должны строго возрастать (нарушение в точке {i}: " +
                    $"{timestamps[i]} <= {timestamps[i - 1]}).");
        }

        var codec = ChooseValueCodec(dataType, values, scale, offset);
        var (summaryA, summaryB, summaryC) = ComputeSummary(dataType, points);
        int goodCount = CountGood(qualities);

        var tsWriter = new BitWriter();
        TimestampCodec.Write(tsWriter, timestamps);
        byte[] tsBytes = tsWriter.ToArray();

        var valueWriter = new BitWriter();
        double? storedScale = null;
        double? storedOffset = null;

        switch (codec)
        {
            case ValueCodec.ScaledInt:
                var units = ValuesToUnits(values, scale, offset);
                ScaledIntCodec.Write(valueWriter, units);
                storedScale = scale;
                storedOffset = offset;
                break;
            case ValueCodec.GorillaXor:
                GorillaXorCodec.Write(valueWriter, values);
                break;
            case ValueCodec.Discrete:
                DiscreteCodec.Write(valueWriter, values);
                break;
            default:
                throw new InvalidOperationException($"Неизвестный кодек значений: {codec}");
        }
        byte[] valueBytes = valueWriter.ToArray();

        var qualityWriter = new BitWriter();
        QualityCodec.Write(qualityWriter, qualities);
        byte[] qualityBytes = qualityWriter.ToArray();

        bool hasScaleOffset = codec == ValueCodec.ScaledInt;
        ushort flags = BlockFlags.Pack(codec, mode, dataType, hasScaleOffset, closedByTimeout);
        int headerSize = hasScaleOffset ? 72 : 56;
        int blockLength = headerSize + tsBytes.Length + valueBytes.Length + qualityBytes.Length + 4;

        byte[] block = new byte[blockLength];
        Span<byte> span = block;
        int pos = 0;

        WriteUInt16(span, ref pos, BlockFlags.Magic);
        WriteInt32(span, ref pos, blockLength);
        WriteUInt16(span, ref pos, flags);
        WriteInt32(span, ref pos, count);
        WriteInt64(span, ref pos, timestamps[0]);
        WriteInt64(span, ref pos, timestamps[^1]);
        WriteInt64(span, ref pos, summaryA);
        WriteInt64(span, ref pos, summaryB);
        WriteInt64(span, ref pos, summaryC);
        WriteInt32(span, ref pos, goodCount);

        if (hasScaleOffset)
        {
            WriteDouble(span, ref pos, storedScale!.Value);
            WriteDouble(span, ref pos, storedOffset!.Value);
        }

        tsBytes.CopyTo(span[pos..]);
        pos += tsBytes.Length;
        valueBytes.CopyTo(span[pos..]);
        pos += valueBytes.Length;
        qualityBytes.CopyTo(span[pos..]);
        pos += qualityBytes.Length;

        uint crc = Crc32.HashToUInt32(span[..pos]);
        WriteUInt32(span, ref pos, crc);

        return block;
    }

    private static ValueCodec ChooseValueCodec(TagDataType dataType, ReadOnlySpan<double> values, double scale, double offset)
    {
        if (dataType == TagDataType.Discrete)
            return ValueCodec.Discrete;

        if (scale == 0.0)
            return ValueCodec.GorillaXor;

        foreach (double value in values)
        {
            long units = (long)Math.Round((value - offset) / scale);
            double restored = units * scale + offset;
            if (BitConverter.DoubleToInt64Bits(restored) != BitConverter.DoubleToInt64Bits(value))
                return ValueCodec.GorillaXor;
        }

        return ValueCodec.ScaledInt;
    }

    private static long[] ValuesToUnits(ReadOnlySpan<double> values, double scale, double offset)
    {
        var units = new long[values.Length];
        for (int i = 0; i < values.Length; i++)
            units[i] = (long)Math.Round((values[i] - offset) / scale);
        return units;
    }

    /// <summary>
    /// Поля summary заголовка. Слоты по 8 байт, смысл зависит от типа данных
    /// (docs/archive-format.md §8.5): у аналогового это min/max/сумма в виде
    /// double, у дискретного — число переключений и время в состоянии 1 как
    /// int64. Возвращаются в сыром виде, чтобы писаться одинаково.
    /// </summary>
    private static (long A, long B, long C) ComputeSummary(TagDataType dataType, ReadOnlySpan<ArchivePoint> points)
    {
        if (dataType == TagDataType.Discrete)
        {
            long transitions = 0;
            long timeInState1 = 0;

            for (int i = 0; i < points.Length - 1; i++)
            {
                if ((points[i].Value != 0.0) != (points[i + 1].Value != 0.0))
                    transitions++;

                if (points[i].Value != 0.0)
                    timeInState1 += points[i + 1].TimestampUtcMs - points[i].TimestampUtcMs;
            }

            return (transitions, timeInState1, 0L);
        }

        // Недостоверные значения в агрегат не входят: точка перехода в Bad
        // несёт последнее известное значение, и включать его в min/max/среднее
        // означало бы выдавать за измерение то, чего не измеряли (§6.2).
        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        double sum = 0.0;
        int goodCount = 0;

        foreach (var point in points)
        {
            if (point.Quality != Quality.Good)
                continue;

            if (point.Value < min) min = point.Value;
            if (point.Value > max) max = point.Value;
            sum += point.Value;
            goodCount++;
        }

        if (goodCount == 0)
        {
            // Блок целиком недостоверен — агрегата не существует.
            // Признак для читателя: GoodCount == 0 в заголовке.
            return (BitConverter.DoubleToInt64Bits(double.NaN),
                    BitConverter.DoubleToInt64Bits(double.NaN),
                    BitConverter.DoubleToInt64Bits(double.NaN));
        }

        return (BitConverter.DoubleToInt64Bits(min),
                BitConverter.DoubleToInt64Bits(max),
                BitConverter.DoubleToInt64Bits(sum));
    }

    private static int CountGood(ReadOnlySpan<Quality> qualities)
    {
        int count = 0;
        foreach (var q in qualities)
            if (q == Quality.Good)
                count++;
        return count;
    }

    private static void WriteUInt16(Span<byte> span, ref int pos, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(span[pos..], value);
        pos += 2;
    }

    private static void WriteInt32(Span<byte> span, ref int pos, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(span[pos..], value);
        pos += 4;
    }

    private static void WriteInt64(Span<byte> span, ref int pos, long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(span[pos..], value);
        pos += 8;
    }

    private static void WriteUInt32(Span<byte> span, ref int pos, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(span[pos..], value);
        pos += 4;
    }

    private static void WriteDouble(Span<byte> span, ref int pos, double value)
    {
        BinaryPrimitives.WriteDoubleLittleEndian(span[pos..], value);
        pos += 8;
    }
}
