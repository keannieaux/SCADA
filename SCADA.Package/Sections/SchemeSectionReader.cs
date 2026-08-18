using System.Text;
using SCADA.Core.Schemes;

namespace SCADA.Package.Sections;

/// <summary>
/// Читатель секций schemes/&lt;имя&gt;.bin и templates/&lt;имя&gt;.bin.
/// Зеркален SchemeSectionWriter (SCADA.Package.Builder), раскладка описана
/// там. Правила эволюции (docs/visualization-concept.md §11.2):
/// версия выше поддерживаемой — PackageFormatException; неизвестный вид
/// элемента или тип действия — пропуск блока по длине; неизвестный id
/// свойства — значение читается по байту типа и отбрасывается; хвосты блоков
/// (поля более новых версий) пропускаются.
///
/// Выражения приходят скомпилированными: CompiledExpressionIndex — номер в
/// пуле code.bin, CompiledTagIndices — индексы тегов для грязного пересчёта
/// по эпохам (§4.1, ТЗ §11.7). Текст выражений в секции нет — читатель
/// подставляет пустую строку (обратная распаковка §11.9 — отдельная история).
/// </summary>
public static class SchemeSectionReader
{
    public const byte MaxSupportedVersion = 1;

    public static Scheme ReadScheme(byte[] section)
    {
        using var stream = new MemoryStream(section);
        using var reader = new BinaryReader(stream, Encoding.UTF8);

        var (id, name, properties, events) = ReadHeader(reader);
        return new Scheme
        {
            Id = id,
            Name = name,
            Properties = properties,
            Events = events,
            Elements = ReadElements(reader)
        };
    }

    public static SchemeTemplate ReadTemplate(byte[] section)
    {
        using var stream = new MemoryStream(section);
        using var reader = new BinaryReader(stream, Encoding.UTF8);

        var (id, name, properties, events) = ReadHeader(reader);

        int parameterCount = reader.ReadInt32();
        var parameters = new List<TemplateParameter>(parameterCount);
        for (int i = 0; i < parameterCount; i++)
        {
            parameters.Add(new TemplateParameter(
                reader.ReadString(),
                (TemplateParameterType)reader.ReadByte(),
                ReadNullableString(reader)));
        }

        return new SchemeTemplate
        {
            Id = id,
            Name = name,
            Properties = properties,
            Events = events,
            Parameters = parameters,
            Elements = ReadElements(reader)
        };
    }

    private static (Guid Id, string Name, List<ElementProperty> Properties,
        List<SchemeEvent> Events) ReadHeader(BinaryReader reader)
    {
        byte version = reader.ReadByte();
        if (version > MaxSupportedVersion)
            throw new PackageFormatException(
                $"Секция схемы собрана более новой версией инженерной поставки " +
                $"(версия секции {version}, поддерживается {MaxSupportedVersion})");

        var id = new Guid(reader.ReadBytes(16));
        string name = reader.ReadString();
        return (id, name, ReadSchemeProperties(reader), ReadEvents(reader));
    }

    private static List<ElementProperty> ReadSchemeProperties(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        var properties = new List<ElementProperty>(count);
        for (int i = 0; i < count; i++)
        {
            int propertyId = reader.ReadInt32();
            // значение читаем всегда — байт типа даёт размер; неизвестный id
            // (пакет новее читателя) отбрасываем (§11.2)
            var value = ReadPropertyValue(reader);
            if (ElementSchemas.FindSchemeProperty(propertyId) is not null)
                properties.Add(new ElementProperty(propertyId, value));
        }
        return properties;
    }

    private static List<SchemeElement> ReadElements(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        var elements = new List<SchemeElement>(count);
        for (int i = 0; i < count; i++)
        {
            var element = ReadElement(reader);
            if (element is not null)
                elements.Add(element);
        }
        return elements;
    }

    private static SchemeElement? ReadElement(BinaryReader reader)
    {
        int blockLength = reader.ReadInt32();
        long blockEnd = reader.BaseStream.Position + blockLength;

        var kind = (ElementKind)reader.ReadByte();
        if (!ElementSchemas.Kinds.Contains(kind))
        {
            // неизвестный вид (пакет новее читателя) — блок пропускается (§11.2)
            reader.BaseStream.Position = blockEnd;
            return null;
        }

        var element = new SchemeElement
        {
            Kind = kind,
            Id = new Guid(reader.ReadBytes(16)),
            Name = reader.ReadString(),
            X = reader.ReadDouble(),
            Y = reader.ReadDouble(),
            Width = reader.ReadDouble(),
            Height = reader.ReadDouble(),
            ZOrder = reader.ReadInt32(),
            ParentId = reader.ReadBoolean() ? new Guid(reader.ReadBytes(16)) : null,
            ControlType = ReadNullableString(reader),
            TemplateName = ReadNullableString(reader),
            TemplateParameters = ReadStringPairs(reader),
            Properties = ReadProperties(reader, kind),
            Bindings = ReadBindings(reader),
            Events = ReadEvents(reader)
        };

        // хвост блока (поля более новой версии) пропускаем
        reader.BaseStream.Position = blockEnd;
        return element;
    }

    private static List<ElementProperty> ReadProperties(BinaryReader reader, ElementKind kind)
    {
        int count = reader.ReadInt32();
        var properties = new List<ElementProperty>(count);
        for (int i = 0; i < count; i++)
        {
            int propertyId = reader.ReadInt32();
            // значение читаем всегда — байт типа даёт размер; неизвестный id
            // (пакет новее читателя) отбрасываем (§11.2)
            var value = ReadPropertyValue(reader);
            if (ElementSchemas.Find(kind, propertyId) is not null)
                properties.Add(new ElementProperty(propertyId, value));
        }
        return properties;
    }

    private static PropertyValue ReadPropertyValue(BinaryReader reader)
    {
        var type = (PropertyType)reader.ReadByte();
        return type switch
        {
            PropertyType.Number or PropertyType.Boolean or PropertyType.Choice =>
                new PropertyValue { Type = type, Number = reader.ReadDouble() },
            PropertyType.Color =>
                new PropertyValue { Type = type, Color = reader.ReadUInt32() },
            PropertyType.String =>
                new PropertyValue { Type = type, Text = reader.ReadString() },
            // новый тип значения — это новая версия секции, а не хвост:
            // без знания размера продолжать чтение нельзя
            _ => throw new PackageFormatException(
                $"Неизвестный тип значения свойства {(byte)type} в секции схемы")
        };
    }

    private static List<ElementBinding> ReadBindings(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        var bindings = new List<ElementBinding>(count);
        for (int i = 0; i < count; i++)
        {
            // блок [длина][тело]: известные поля читаем, хвост пропускаем (§11.2)
            int blockLength = reader.ReadInt32();
            long blockEnd = reader.BaseStream.Position + blockLength;

            int propertyId = reader.ReadInt32();
            var mapping = (StopMapping)reader.ReadByte();
            bool isVolatile = reader.ReadBoolean();
            int expressionIndex = reader.ReadInt32();
            int[]? tagIndices = ReadTagIndices(reader);

            int stopCount = reader.ReadInt32();
            List<Stop>? stops = stopCount > 0 ? new List<Stop>(stopCount) : null;
            for (int j = 0; j < stopCount; j++)
                stops!.Add(new Stop(reader.ReadDouble(), ReadPropertyValue(reader)));

            bindings.Add(new ElementBinding
            {
                PropertyId = propertyId,
                Expression = "", // текста в пакете нет — только индексы пула
                Mapping = mapping,
                Volatile = isVolatile,
                Stops = stops,
                CompiledExpressionIndex = expressionIndex < 0 ? null : expressionIndex,
                CompiledTagIndices = tagIndices
            });

            reader.BaseStream.Position = blockEnd;
        }
        return bindings;
    }

    private static List<SchemeEvent> ReadEvents(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        var events = new List<SchemeEvent>(count);
        for (int i = 0; i < count; i++)
        {
            var kind = (SchemeEventKind)reader.ReadByte();
            int actionCount = reader.ReadInt32();
            var actions = new List<SchemeAction>(actionCount);
            for (int j = 0; j < actionCount; j++)
            {
                var action = ReadAction(reader);
                if (action is not null)
                    actions.Add(action);
            }

            // неизвестное событие пропускаем (действия уже разобраны по длинам)
            if (Enum.IsDefined(kind))
                events.Add(new SchemeEvent { Kind = kind, Actions = actions });
        }
        return events;
    }

    private static SchemeAction? ReadAction(BinaryReader reader)
    {
        int actionLength = reader.ReadInt32();
        long actionEnd = reader.BaseStream.Position + actionLength;

        byte type = reader.ReadByte();
        if (type > 6)
        {
            // неизвестный тип действия (пакет новее) — пропуск по длине (§5.3)
            reader.BaseStream.Position = actionEnd;
            return null;
        }

        SchemeAction action = type switch
        {
            0 => new WriteTagAction(ReadTagRef(reader), reader.ReadDouble()),
            1 => new ToggleTagAction(ReadTagRef(reader)),
            2 => new OpenSchemeAction(reader.ReadString(), ReadStringPairs(reader)),
            3 => new OpenPopupAction(reader.ReadString(), ReadStringPairs(reader)),
            4 => new ClosePopupAction(),
            5 => new BackAction(),
            _ => new ShowDialogAction(reader.ReadString())
        };

        int conditionIndex = reader.ReadInt32();
        action.CompiledConditionIndex = conditionIndex < 0 ? null : conditionIndex;
        action.CompiledConditionTagIndices = ReadTagIndices(reader);
        string? confirmation = ReadNullableString(reader);
        if (confirmation is not null)
            action = action with { Confirmation = confirmation };

        // хвост блока действия пропускаем
        reader.BaseStream.Position = actionEnd;
        return action;
    }

    private static SchemeTagRef ReadTagRef(BinaryReader reader)
    {
        bool isParametric = reader.ReadBoolean();
        return new SchemeTagRef(reader.ReadString(), isParametric);
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

    private static Dictionary<string, string>? ReadStringPairs(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count < 0)
            return null;
        var pairs = new Dictionary<string, string>(count);
        for (int i = 0; i < count; i++)
            pairs[reader.ReadString()] = reader.ReadString();
        return pairs;
    }

    private static string? ReadNullableString(BinaryReader reader)
        => reader.ReadBoolean() ? reader.ReadString() : null;
}
