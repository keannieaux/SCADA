using System.Text;
using SCADA.Core.Alarms;

namespace SCADA.Package.Builder.Sections;

/// <summary>
/// Сериализатор секции alarms.bin (docs/M5-plan.md §6). Зеркален
/// AlarmsSectionReader (SCADA.Package). Раскладка записи правила — как у
/// tags.bin: [длина записи][поля...], новые поля только в хвост.
/// До релиза совместимость не поддерживается: поменял раскладку —
/// пересобери пакет.
/// </summary>
public static class AlarmsSectionWriter
{
    public static byte[] Write(AlarmConfiguration config)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);

        writer.Write(config.Rules.Count);
        foreach (var rule in config.Rules)
            WriteRule(writer, rule);

        writer.Write(config.Templates.Count);
        foreach (var (key, template) in config.Templates)
        {
            writer.Write(key);
            writer.Write(template);
        }

        writer.Write(config.Sound.Enabled);
        writer.Write(config.Sound.Files.Count);
        foreach (var (severity, file) in config.Sound.Files)
        {
            writer.Write((byte)severity);
            writer.Write(file);
        }

        writer.Write(config.Defaults.MinDurationMs);

        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteRule(BinaryWriter writer, AlarmRule rule)
    {
        // запись собирается в отдельный поток, чтобы узнать её длину
        using var recordStream = new MemoryStream();
        using (var record = new BinaryWriter(recordStream, Encoding.UTF8, leaveOpen: true))
        {
            record.Write(rule.Name);
            record.Write(rule.Description);
            record.Write((byte)rule.Type);
            record.Write((byte)rule.Severity);
            record.Write(rule.Area);
            record.Write(rule.RequiresAck);
            WriteNullableString(record, rule.MessageTemplate);
            WriteNullableInt(record, rule.MinDurationMs);

            // --- Threshold ---
            WriteNullableString(record, rule.TagName);
            record.Write(rule.Limits is not null);
            if (rule.Limits is not null)
            {
                record.Write(rule.Limits.Count);
                foreach (var limit in rule.Limits)
                {
                    record.Write((byte)limit.Kind);
                    record.Write(limit.Value);
                    record.Write(limit.Severity.HasValue);
                    if (limit.Severity.HasValue)
                        record.Write((byte)limit.Severity.Value);
                }
            }
            record.Write(rule.Hysteresis);

            // --- Expression ---
            WriteNullableString(record, rule.Condition);

            // --- заполненное при сборке ---
            WriteNullableInt(record, rule.CompiledExpressionIndex);
            record.Write(rule.CompiledTagIndices?.Length ?? -1);
            if (rule.CompiledTagIndices is not null)
                foreach (int index in rule.CompiledTagIndices)
                    record.Write(index);

            record.Flush();
        }

        writer.Write((int)recordStream.Length);
        writer.Write(recordStream.GetBuffer(), 0, (int)recordStream.Length);
    }

    private static void WriteNullableString(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null)
            writer.Write(value);
    }

    private static void WriteNullableInt(BinaryWriter writer, int? value)
    {
        writer.Write(value.HasValue);
        if (value.HasValue)
            writer.Write(value.Value);
    }
}
