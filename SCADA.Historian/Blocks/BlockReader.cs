using System.Buffers.Binary;
using System.IO.Hashing;
using SCADA.Core.Tags;

namespace SCADA.Historian;

/// <summary>
/// Результат разбора блока: точки и метаданные заголовка.
/// </summary>
public readonly record struct BlockReadResult(
    int Count,
    long FirstTimestampMs,
    long LastTimestampMs,
    long SummaryA,
    long SummaryB,
    long SummaryC,
    int GoodCount,
    LoggingMode Mode,
    TagDataType DataType,
    ValueCodec Codec,
    double Scale,
    double Offset,
    bool ClosedByTimeout,
    ArchivePoint[] Points)
{
    /// <summary>Минимум достоверных значений блока (аналоговый, §8.5).</summary>
    public double Min => BitConverter.Int64BitsToDouble(SummaryA);

    /// <summary>Максимум достоверных значений блока (аналоговый, §8.5).</summary>
    public double Max => BitConverter.Int64BitsToDouble(SummaryB);

    /// <summary>Сумма достоверных значений блока, для среднего (аналоговый, §8.5).</summary>
    public double Sum => BitConverter.Int64BitsToDouble(SummaryC);

    /// <summary>Число переключений (дискретный, §8.5).</summary>
    public long Transitions => SummaryA;

    /// <summary>Время в состоянии 1, миллисекунды (дискретный, §8.5).</summary>
    public long TimeInStateOneMs => SummaryB;

    /// <summary>Есть ли в блоке достоверные значения (§6.2).</summary>
    public bool HasGoodValues => GoodCount > 0;
}

/// <summary>
/// Заголовок блока без полезной нагрузки (docs/archive-format.md §8.3).
/// Именно он делает агрегаты бесплатными: широкий диапазон читается по
/// заголовкам, содержимое блоков не разжимается (§8.4, §14).
/// </summary>
public readonly record struct BlockHeader(
    int Count,
    long FirstTimestampMs,
    long LastTimestampMs,
    long SummaryA,
    long SummaryB,
    long SummaryC,
    int GoodCount,
    LoggingMode Mode,
    TagDataType DataType,
    ValueCodec Codec,
    bool ClosedByTimeout)
{
    /// <summary>Минимум достоверных значений блока (аналоговый, §8.5).</summary>
    public double Min => BitConverter.Int64BitsToDouble(SummaryA);

    /// <summary>Максимум достоверных значений блока (аналоговый, §8.5).</summary>
    public double Max => BitConverter.Int64BitsToDouble(SummaryB);

    /// <summary>Сумма достоверных значений блока, для среднего (аналоговый, §8.5).</summary>
    public double Sum => BitConverter.Int64BitsToDouble(SummaryC);

    /// <summary>Число переключений (дискретный, §8.5).</summary>
    public long Transitions => SummaryA;

    /// <summary>Время в состоянии 1, миллисекунды (дискретный, §8.5).</summary>
    public long TimeInStateOneMs => SummaryB;

    /// <summary>
    /// Есть ли в блоке достоверные значения. При false агрегаты блока
    /// не определены (§6.2) и в бакет не сливаются.
    /// </summary>
    public bool HasGoodValues => GoodCount > 0;
}

/// <summary>
/// Читатель архивного блока (docs/archive-format.md §8.3): проверяет CRC,
/// разбирает заголовок и декодирует timestamps, значения, качество.
/// </summary>
public static class BlockReader
{
    /// <summary>Размер заголовка без полей масштаба.</summary>
    public const int MinHeaderSize = 56;

    /// <summary>Размер заголовка с полями масштаба (кодек ScaledInt).</summary>
    public const int MaxHeaderSize = 72;

    /// <summary>
    /// Разбирает только заголовок, не трогая полезную нагрузку и не проверяя
    /// CRC: применяется при проходе по блокам, когда содержимое не нужно.
    /// Возвращает false на повреждении, а не бросает — вызывающий обрывает
    /// обход и работает с тем, что успел прочитать (§16.2).
    /// </summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> block, out BlockHeader header)
    {
        header = default;

        if (block.Length < MinHeaderSize)
            return false;

        int pos = 0;
        if (ReadUInt16(block, ref pos) != BlockFlags.Magic)
            return false;

        int blockLength = ReadInt32(block, ref pos);
        if (blockLength < MinHeaderSize || blockLength > block.Length)
            return false;

        ushort flags = ReadUInt16(block, ref pos);
        if (BlockFlags.HasScaleOffset(flags) && block.Length < MaxHeaderSize)
            return false;

        int count = ReadInt32(block, ref pos);
        if (count <= 0)
            return false;

        long firstTimestampMs = ReadInt64(block, ref pos);
        long lastTimestampMs = ReadInt64(block, ref pos);
        if (lastTimestampMs < firstTimestampMs)
            return false;

        long summaryA = ReadInt64(block, ref pos);

        long summaryB = ReadInt64(block, ref pos);

        long summaryC = ReadInt64(block, ref pos);
        int goodCount = ReadInt32(block, ref pos);

        header = new BlockHeader(
            count, firstTimestampMs, lastTimestampMs,
            summaryA, summaryB, summaryC, goodCount,
            BlockFlags.GetLoggingMode(flags),
            BlockFlags.GetDataType(flags),
            BlockFlags.GetValueCodec(flags),
            BlockFlags.ClosedByTimeout(flags));

        return true;
    }

    public static BlockReadResult Read(ReadOnlySpan<byte> block)
    {
        if (block.Length < MinHeaderSize)
            throw new InvalidDataException($"Блок слишком короткий: {block.Length} байт");

        int pos = 0;
        ushort magic = ReadUInt16(block, ref pos);
        if (magic != BlockFlags.Magic)
            throw new InvalidDataException($"Неверная магия блока: 0x{magic:X4}");

        int blockLength = ReadInt32(block, ref pos);
        if (block.Length != blockLength)
            throw new InvalidDataException($"Длина блока в заголовке ({blockLength}) не совпадает с фактической ({block.Length})");

        ushort flags = ReadUInt16(block, ref pos);
        bool hasScaleOffset = BlockFlags.HasScaleOffset(flags);

        // Заголовок с масштабом длиннее: проверяем до чтения полей, иначе на
        // обрезанном блоке вылетит ArgumentOutOfRange вместо InvalidData,
        // и путь восстановления (§16.2) его не поймает.
        int requiredHeader = hasScaleOffset ? MaxHeaderSize : MinHeaderSize;
        if (block.Length < requiredHeader + 4)
            throw new InvalidDataException(
                $"Блок короче своего заголовка: {block.Length} байт при необходимых {requiredHeader + 4}");

        int count = ReadInt32(block, ref pos);
        long firstTimestampMs = ReadInt64(block, ref pos);
        long lastTimestampMs = ReadInt64(block, ref pos);
        long summaryA = ReadInt64(block, ref pos);
        long summaryB = ReadInt64(block, ref pos);
        long summaryC = ReadInt64(block, ref pos);
        int goodCount = ReadInt32(block, ref pos);

        double scale = 0.0;
        double offset = 0.0;
        if (hasScaleOffset)
        {
            scale = ReadDouble(block, ref pos);
            offset = ReadDouble(block, ref pos);
        }

        int headerSize = pos;
        if (blockLength - headerSize - 4 < 0)
            throw new InvalidDataException("Некорректная длина блока: полезная нагрузка отрицательна");

        uint actualCrc = Crc32.HashToUInt32(block[..^4]);
        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(block[^4..]);
        if (actualCrc != storedCrc)
            throw new InvalidDataException($"Несовпадение CRC32 блока: ожидалось {actualCrc:X8}, получили {storedCrc:X8}");

        if (count <= 0)
            throw new InvalidDataException($"Некорректное число отсчётов в блоке: {count}");

        ReadOnlySpan<byte> payload = block.Slice(headerSize, blockLength - headerSize - 4);

        ValueCodec codec = BlockFlags.GetValueCodec(flags);
        LoggingMode mode = BlockFlags.GetLoggingMode(flags);
        TagDataType dataType = BlockFlags.GetDataType(flags);

        long[] timestamps = DecodeTimestamps(payload, count, out int timestampBytes);
        ReadOnlySpan<byte> valuePayload = payload.Slice(timestampBytes);
        double[] values = DecodeValues(valuePayload, codec, count, scale, offset, out int valueBytes);
        ReadOnlySpan<byte> qualityPayload = payload.Slice(timestampBytes + valueBytes);
        Quality[] qualities = DecodeQualities(qualityPayload, count);

        var points = new ArchivePoint[count];
        for (int i = 0; i < count; i++)
            points[i] = new ArchivePoint(timestamps[i], values[i], qualities[i]);

        return new BlockReadResult(
            Count: count,
            FirstTimestampMs: firstTimestampMs,
            LastTimestampMs: lastTimestampMs,
            SummaryA: summaryA,
            SummaryB: summaryB,
            SummaryC: summaryC,
            GoodCount: goodCount,
            Mode: mode,
            DataType: dataType,
            Codec: codec,
            Scale: scale,
            Offset: offset,
            ClosedByTimeout: BlockFlags.ClosedByTimeout(flags),
            Points: points);
    }

    private static long[] DecodeTimestamps(ReadOnlySpan<byte> payload, int count, out int consumedBytes)
    {
        var reader = new BitReader(payload);
        var timestamps = TimestampCodec.Read(ref reader, count);
        consumedBytes = ByteCount(reader.BitPosition);
        return timestamps;
    }

    private static double[] DecodeValues(ReadOnlySpan<byte> payload, ValueCodec codec, int count,
        double scale, double offset, out int consumedBytes)
    {
        var reader = new BitReader(payload);
        double[] values;

        switch (codec)
        {
            case ValueCodec.ScaledInt:
                long[] units = ScaledIntCodec.Read(ref reader, count);
                values = new double[count];
                for (int i = 0; i < count; i++)
                    values[i] = units[i] * scale + offset;
                break;
            case ValueCodec.GorillaXor:
                values = GorillaXorCodec.Read(ref reader, count);
                break;
            case ValueCodec.Discrete:
                values = DiscreteCodec.Read(ref reader, count);
                break;
            default:
                throw new InvalidDataException($"Неизвестный кодек значений: {codec}");
        }

        consumedBytes = ByteCount(reader.BitPosition);
        return values;
    }

    private static Quality[] DecodeQualities(ReadOnlySpan<byte> payload, int count)
    {
        var reader = new BitReader(payload);
        return QualityCodec.Read(ref reader, count);
    }

    private static int ByteCount(int bitPosition) => (bitPosition + 7) >> 3;

    private static ushort ReadUInt16(ReadOnlySpan<byte> span, ref int pos)
    {
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(span[pos..]);
        pos += 2;
        return value;
    }

    private static int ReadInt32(ReadOnlySpan<byte> span, ref int pos)
    {
        int value = BinaryPrimitives.ReadInt32LittleEndian(span[pos..]);
        pos += 4;
        return value;
    }

    private static long ReadInt64(ReadOnlySpan<byte> span, ref int pos)
    {
        long value = BinaryPrimitives.ReadInt64LittleEndian(span[pos..]);
        pos += 8;
        return value;
    }

    private static double ReadDouble(ReadOnlySpan<byte> span, ref int pos)
    {
        double value = BitConverter.ToDouble(span.Slice(pos, 8));
        pos += 8;
        return value;
    }
}
