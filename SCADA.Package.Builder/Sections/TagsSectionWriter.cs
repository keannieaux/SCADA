using System.Text;
using SCADA.Core.Tags;

namespace SCADA.Package.Builder.Sections;

/// <summary>
/// Сериализатор секции tags.bin. Зеркален TagsSectionReader (SCADA.Package).
/// Раскладка записи: [длина записи][поля...]. Длина в префиксе позволяет
/// читателю перепрыгнуть запись с неизвестными полями в хвосте.
/// Правила раскладки: поля только добавляются в конец, порядок и типы
/// существующих не меняются. До релиза версия формата = 1, совместимость
/// не поддерживается: поменял раскладку — пересобери пакет.
/// </summary>
public static class TagsSectionWriter
{
    public static byte[] Write(IReadOnlyList<TagDefinition> tags)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);

        writer.Write(tags.Count);
        foreach (var tag in tags)
            WriteRecord(writer, tag);

        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteRecord(BinaryWriter writer, TagDefinition tag)
    {
        // запись собирается в отдельный поток, чтобы узнать её длину
        using var recordStream = new MemoryStream();
        using (var record = new BinaryWriter(recordStream, Encoding.UTF8, leaveOpen: true))
        {
            record.Write(tag.Id.Value);
            record.Write((byte)tag.DataType);
            record.Write(tag.DeviceId.Value);
            record.Write(tag.Name);
            record.Write(tag.Description);
            record.Write(tag.Address);
            record.Write(tag.ScaleFactor);
            record.Write(tag.ScaleOffset);
            WriteNullable(record, tag.MinValue);
            WriteNullable(record, tag.MaxValue);
            WriteNullable(record, tag.Deadband);
            record.Write(tag.Units);
            record.Write(tag.IsWritable);
            WriteNullable(record, tag.InitValue);
            record.Write(tag.IsPersistent);
            WriteLogging(record, tag.Logging);
            record.Write((byte)tag.Origin);
            record.Flush();
        }

        writer.Write((int)recordStream.Length);
        writer.Write(recordStream.GetBuffer(), 0, (int)recordStream.Length);
    }

    private static void WriteNullable(BinaryWriter writer, double? value)
    {
        writer.Write(value.HasValue);
        if (value.HasValue)
            writer.Write(value.Value);
    }

    private static void WriteLogging(BinaryWriter writer, TagLoggingConfiguration? logging)
    {
        writer.Write(logging is not null);
        if (logging is null)
            return;

        writer.Write(logging.LogOnChange);
        writer.Write(logging.Interval.HasValue);
        if (logging.Interval.HasValue)
            writer.Write(logging.Interval.Value.Ticks);

        writer.Write(logging.Schedule.Count);
        foreach (var entry in logging.Schedule)
        {
            writer.Write(entry.Time.Ticks);
            writer.Write(entry.DayOfWeek.HasValue ? (byte)entry.DayOfWeek.Value : (byte)255);
            WriteNullableInt(writer, entry.DayOfMonth);
            WriteNullableInt(writer, entry.Month);
        }
    }

    private static void WriteNullableInt(BinaryWriter writer, int? value)
    {
        writer.Write(value.HasValue);
        if (value.HasValue)
            writer.Write(value.Value);
    }
}
