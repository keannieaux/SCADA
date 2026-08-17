using System.Text;
using SCADA.Core.Alarms;

namespace SCADA.Package.Sections;

/// <summary>
/// Читатель секции alarms.bin. Зеркален AlarmsSectionWriter
/// (SCADA.Package.Builder). Правила приходят уже скомпилированными:
/// CompiledExpressionIndex — номер в пуле code.bin, CompiledTagIndices —
/// индексы тегов для раннего связывания (docs/M5-plan.md §6).
/// </summary>
public static class AlarmsSectionReader
{
    public static AlarmConfiguration Read(byte[] section)
    {
        using var stream = new MemoryStream(section);
        using var reader = new BinaryReader(stream, Encoding.UTF8);

        int ruleCount = reader.ReadInt32();
        var rules = new List<AlarmRule>(ruleCount);
        for (int i = 0; i < ruleCount; i++)
            rules.Add(ReadRule(reader));

        int templateCount = reader.ReadInt32();
        var templates = new Dictionary<string, string>(templateCount);
        for (int i = 0; i < templateCount; i++)
            templates[reader.ReadString()] = reader.ReadString();

        var sound = new SoundConfiguration { Enabled = reader.ReadBoolean() };
        int soundCount = reader.ReadInt32();
        for (int i = 0; i < soundCount; i++)
            sound.Files[(AlarmSeverity)reader.ReadByte()] = reader.ReadString();

        var defaults = new AlarmDefaults { MinDurationMs = reader.ReadInt32() };

        return new AlarmConfiguration
        {
            Rules = rules,
            Templates = templates,
            Sound = sound,
            Defaults = defaults
        };
    }

    private static AlarmRule ReadRule(BinaryReader reader)
    {
        int recordLength = reader.ReadInt32();
        long recordEnd = reader.BaseStream.Position + recordLength;

        var rule = new AlarmRule
        {
            Name = reader.ReadString(),
            Description = reader.ReadString(),
            Type = (AlarmType)reader.ReadByte(),
            Severity = (AlarmSeverity)reader.ReadByte(),
            Area = reader.ReadString(),
            RequiresAck = reader.ReadBoolean(),
            MessageTemplate = ReadNullableString(reader),
            MinDurationMs = ReadNullableInt(reader),

            // --- Threshold ---
            TagName = ReadNullableString(reader),
            Limits = reader.ReadBoolean() ? ReadLimits(reader) : null,
            Hysteresis = reader.ReadDouble(),

            // --- Expression ---
            Condition = ReadNullableString(reader),

            // --- заполненное при сборке ---
            CompiledExpressionIndex = ReadNullableInt(reader),
            CompiledTagIndices = ReadTagIndices(reader)
        };

        // в хвосте записи могут быть поля более новой версии — пропускаем
        reader.BaseStream.Position = recordEnd;
        return rule;
    }

    private static List<ThresholdLimit> ReadLimits(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        var limits = new List<ThresholdLimit>(count);
        for (int i = 0; i < count; i++)
        {
            var kind = (ThresholdKind)reader.ReadByte();
            double value = reader.ReadDouble();
            AlarmSeverity? severity = reader.ReadBoolean()
                ? (AlarmSeverity)reader.ReadByte()
                : null;
            limits.Add(new ThresholdLimit { Kind = kind, Value = value, Severity = severity });
        }
        return limits;
    }

    private static int[]? ReadTagIndices(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count < 0)
            return null;
        var indices = new int[count];
        for (int i = 0; i < count; i++)
            indices[i] = reader.ReadInt32();
        return indices;
    }

    private static string? ReadNullableString(BinaryReader reader)
        => reader.ReadBoolean() ? reader.ReadString() : null;

    private static int? ReadNullableInt(BinaryReader reader)
        => reader.ReadBoolean() ? reader.ReadInt32() : null;
}
