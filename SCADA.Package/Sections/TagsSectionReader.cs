using System.Text;
using SCADA.Core.Devices;
using SCADA.Core.Tags;

namespace SCADA.Package.Sections;

/// <summary>
/// Читатель секции tags.bin. Зеркален TagsSectionWriter (SCADA.Package.Builder).
/// Хвост записи за пределами известных полей пропускается через длину записи —
/// пакет с дополненными полями не ломает старого читателя.
/// </summary>
public static class TagsSectionReader
{
    public static IReadOnlyList<TagDefinition> Read(byte[] section)
    {
        using var stream = new MemoryStream(section);
        using var reader = new BinaryReader(stream, Encoding.UTF8);

        int count = reader.ReadInt32();
        var tags = new List<TagDefinition>(count);

        for (int i = 0; i < count; i++)
            tags.Add(ReadRecord(reader, stream));

        return tags;
    }

    private static TagDefinition ReadRecord(BinaryReader reader, MemoryStream stream)
    {
        int recordLength = reader.ReadInt32();
        long recordEnd = stream.Position + recordLength;

        var tag = new TagDefinition
        {
            Id = new TagId(reader.ReadInt32()),
            DataType = (TagDataType)reader.ReadByte(),
            DeviceId = new DeviceId(reader.ReadInt32()),
            Name = reader.ReadString(),
            Description = reader.ReadString(),
            Address = reader.ReadString(),
            ScaleFactor = reader.ReadDouble(),
            ScaleOffset = reader.ReadDouble(),
            MinValue = ReadNullable(reader),
            MaxValue = ReadNullable(reader),
            Units = reader.ReadString(),
            IsWritable = reader.ReadBoolean(),
            InitValue = ReadNullable(reader),
            IsPersistent = reader.ReadBoolean(),
            Logging = ReadLogging(reader)
        };

        // поля более поздних раскладок читаем, только если они есть в записи
        if (stream.Position < recordEnd)
            tag.Origin = (TagOrigin)reader.ReadByte();
        if (stream.Position < recordEnd)
        {
            tag.IsArchived = reader.ReadBoolean();
            tag.Precision = ReadNullableInt(reader);
        }

        // хвост записи (поля более нового формата) пропускаем
        stream.Position = recordEnd;
        return tag;
    }

    private static double? ReadNullable(BinaryReader reader)
        => reader.ReadBoolean() ? reader.ReadDouble() : null;

    private static TagLoggingConfiguration? ReadLogging(BinaryReader reader)
    {
        if (!reader.ReadBoolean())
            return null;

        var logOnChange = reader.ReadBoolean();
        TimeSpan? interval = reader.ReadBoolean() ? TimeSpan.FromTicks(reader.ReadInt64()) : null;

        int scheduleCount = reader.ReadInt32();
        var schedule = new List<LogScheduleEntry>(scheduleCount);
        for (int i = 0; i < scheduleCount; i++)
        {
            var time = TimeOnly.FromTimeSpan(TimeSpan.FromTicks(reader.ReadInt64()));
            byte dayOfWeek = reader.ReadByte();
            schedule.Add(new LogScheduleEntry
            {
                Time = time,
                DayOfWeek = dayOfWeek == 255 ? null : (DayOfWeek)dayOfWeek,
                DayOfMonth = ReadNullableInt(reader),
                Month = ReadNullableInt(reader)
            });
        }

        return new TagLoggingConfiguration
        {
            LogOnChange = logOnChange,
            Interval = interval,
            Schedule = schedule
        };
    }

    private static int? ReadNullableInt(BinaryReader reader)
        => reader.ReadBoolean() ? reader.ReadInt32() : null;
}
