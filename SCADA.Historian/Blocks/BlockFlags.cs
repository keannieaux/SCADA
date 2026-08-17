using SCADA.Core.Tags;

namespace SCADA.Historian;

/// <summary>
/// Разбор и сборка поля flags заголовка блока (docs/archive-format.md §8.4).
/// </summary>
public static class BlockFlags
{
    public const ushort Magic = 0xB10C;

    public static ushort Pack(ValueCodec codec, LoggingMode mode, TagDataType dataType,
        bool hasScaleOffset, bool closedByTimeout)
    {
        ushort flags = 0;
        flags |= (ushort)((byte)codec & 0x0F);
        if (hasScaleOffset) flags |= 0x10;
        flags |= (ushort)(((byte)mode & 0x03) << 5);
        flags |= (ushort)(TagDataTypeToFlag(dataType) << 7);
        if (closedByTimeout) flags |= 0x0200;
        return flags;
    }

    public static ValueCodec GetValueCodec(ushort flags) => (ValueCodec)(flags & 0x0F);

    public static bool HasScaleOffset(ushort flags) => (flags & 0x10) != 0;

    public static LoggingMode GetLoggingMode(ushort flags) => (LoggingMode)((flags >> 5) & 0x03);

    public static TagDataType GetDataType(ushort flags) => FlagToTagDataType((flags >> 7) & 0x03);

    public static bool ClosedByTimeout(ushort flags) => (flags & 0x0200) != 0;

    private static int TagDataTypeToFlag(TagDataType type) => type switch
    {
        TagDataType.Analog => 0,
        TagDataType.Discrete => 1,
        _ => 0
    };

    private static TagDataType FlagToTagDataType(int flag) => flag switch
    {
        1 => TagDataType.Discrete,
        _ => TagDataType.Analog
    };
}
