using System.Text;
using SCADA.Core.Schemes;

namespace SCADA.Package.Builder.Sections;

/// <summary>
/// Сериализатор секций schemes/&lt;имя&gt;.bin и templates/&lt;имя&gt;.bin
/// (docs/visualization-concept.md §11). Зеркален SchemeSectionReader
/// (SCADA.Package). Формат общий; у шаблонов после заголовка — блок
/// параметров.
///
/// Раскладка секции (little-endian, строки — BinaryWriter.Write(string)):
/// [byte Version][Guid Id][string Name][свойства схемы][события схемы]
/// [параметры шаблона?][элементы...]. Свойства схемы — int count + пары
/// (int id, значение); события — как у элементов (§5.1).
/// Каждый элемент — [int длина блока][блок]; привязка и действие внутри —
/// тоже [int длина][блок]: неизвестный вид элемента или тип действия
/// пропускается по длине, хвост блока (поля более новых версий) — тоже (§11.2).
/// Неизвестные id свойств читаются по байту типа (размер известен) и
/// отбрасываются.
///
/// Текст выражений привязок и условий в секцию НЕ пишется — только индексы
/// в общем пуле code.bin (CompiledExpressionIndex, CompiledTagIndices §4.1);
/// -1 = отсутствует. Словари: int count (-1 = null) + пары (string, string).
/// До релиза версия = 1, совместимость не поддерживается: поменял раскладку —
/// пересобери пакет.
/// </summary>
public static class SchemeSectionWriter
{
    public const byte Version = 1;

    public static byte[] Write(Scheme scheme)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);

        WriteHeader(writer, scheme.Id, scheme.Name, scheme.Properties, scheme.Events);
        WriteElements(writer, scheme.Elements);

        writer.Flush();
        return stream.ToArray();
    }

    public static byte[] WriteTemplate(SchemeTemplate template)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);

        WriteHeader(writer, template.Id, template.Name, template.Properties, template.Events);

        writer.Write(template.Parameters.Count);
        foreach (var parameter in template.Parameters)
        {
            writer.Write(parameter.Name);
            writer.Write((byte)parameter.Type);
            WriteNullableString(writer, parameter.DefaultValue);
        }

        WriteElements(writer, template.Elements);

        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteHeader(BinaryWriter writer, Guid id, string name,
        IReadOnlyList<ElementProperty> properties, IReadOnlyList<SchemeEvent> events)
    {
        writer.Write(Version);
        writer.Write(id.ToByteArray());
        writer.Write(name);
        WriteProperties(writer, properties);
        WriteEvents(writer, events);
    }

    private static void WriteProperties(BinaryWriter writer,
        IReadOnlyList<ElementProperty> properties)
    {
        writer.Write(properties.Count);
        foreach (var property in properties)
        {
            writer.Write(property.PropertyId);
            WritePropertyValue(writer, property.Value);
        }
    }

    private static void WriteEvents(BinaryWriter writer, IReadOnlyList<SchemeEvent> events)
    {
        writer.Write(events.Count);
        foreach (var schemeEvent in events)
        {
            writer.Write((byte)schemeEvent.Kind);
            writer.Write(schemeEvent.Actions.Count);
            foreach (var action in schemeEvent.Actions)
                WriteAction(writer, action);
        }
    }

    private static void WriteElements(BinaryWriter writer, IReadOnlyList<SchemeElement> elements)
    {
        writer.Write(elements.Count);
        foreach (var element in elements)
            WriteElement(writer, element);
    }

    private static void WriteElement(BinaryWriter writer, SchemeElement element)
    {
        // блок собирается в отдельный поток, чтобы узнать его длину (§11.2)
        using var blockStream = new MemoryStream();
        using (var block = new BinaryWriter(blockStream, Encoding.UTF8, leaveOpen: true))
        {
            block.Write((byte)element.Kind);
            block.Write(element.Id.ToByteArray());
            block.Write(element.Name);
            block.Write(element.X);
            block.Write(element.Y);
            block.Write(element.Width);
            block.Write(element.Height);
            block.Write(element.ZOrder);
            block.Write(element.ParentId.HasValue);
            if (element.ParentId.HasValue)
                block.Write(element.ParentId.Value.ToByteArray());

            WriteNullableString(block, element.ControlType);
            WriteNullableString(block, element.TemplateName);
            WriteStringPairs(block, element.TemplateParameters);

            WriteProperties(block, element.Properties);

            block.Write(element.Bindings.Count);
            foreach (var binding in element.Bindings)
                WriteBinding(block, binding);

            WriteEvents(block, element.Events);

            block.Flush();
        }

        writer.Write((int)blockStream.Length);
        writer.Write(blockStream.GetBuffer(), 0, (int)blockStream.Length);
    }

    private static void WritePropertyValue(BinaryWriter writer, PropertyValue value)
    {
        writer.Write((byte)value.Type);
        switch (value.Type)
        {
            // Boolean/Choice хранятся в поле Number (PropertyValue)
            case PropertyType.Number or PropertyType.Boolean or PropertyType.Choice:
                writer.Write(value.Number);
                break;
            case PropertyType.Color:
                writer.Write(value.Color);
                break;
            case PropertyType.String:
                writer.Write(value.Text ?? "");
                break;
            default:
                throw new InvalidOperationException(
                    $"Неизвестный тип значения свойства {value.Type}");
        }
    }

    private static void WriteBinding(BinaryWriter writer, ElementBinding binding)
    {
        // привязка тоже [длина][блок] — запас на поля будущих версий (§11.2)
        using var blockStream = new MemoryStream();
        using (var block = new BinaryWriter(blockStream, Encoding.UTF8, leaveOpen: true))
        {
            block.Write(binding.PropertyId);
            block.Write((byte)binding.Mapping);
            block.Write(binding.Volatile);
            block.Write(binding.CompiledExpressionIndex ?? -1);
            WriteTagIndices(block, binding.CompiledTagIndices);

            block.Write(binding.Stops?.Count ?? -1);
            if (binding.Stops is not null)
                foreach (var stop in binding.Stops)
                {
                    block.Write(stop.Input);
                    WritePropertyValue(block, stop.Output);
                }

            block.Flush();
        }

        writer.Write((int)blockStream.Length);
        writer.Write(blockStream.GetBuffer(), 0, (int)blockStream.Length);
    }

    private static void WriteAction(BinaryWriter writer, SchemeAction action)
    {
        // у действия тоже [длина][блок]: неизвестный тип пропускается (§5.3, §11.2)
        using var blockStream = new MemoryStream();
        using (var block = new BinaryWriter(blockStream, Encoding.UTF8, leaveOpen: true))
        {
            switch (action)
            {
                case WriteTagAction a:
                    block.Write((byte)0);
                    WriteTagRef(block, a.Tag);
                    block.Write(a.Value);
                    break;
                case ToggleTagAction a:
                    block.Write((byte)1);
                    WriteTagRef(block, a.Tag);
                    break;
                case OpenSchemeAction a:
                    block.Write((byte)2);
                    block.Write(a.SchemeName);
                    WriteStringPairs(block, a.Parameters);
                    break;
                case OpenPopupAction a:
                    block.Write((byte)3);
                    block.Write(a.TemplateName);
                    WriteStringPairs(block, a.Parameters);
                    break;
                case ClosePopupAction:
                    block.Write((byte)4);
                    break;
                case BackAction:
                    block.Write((byte)5);
                    break;
                case ShowDialogAction a:
                    block.Write((byte)6);
                    block.Write(a.Message);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Неизвестный тип действия {action.GetType().Name}");
            }

            // общий хвост: условие (индексы пула code.bin) + подтверждение
            block.Write(action.CompiledConditionIndex ?? -1);
            WriteTagIndices(block, action.CompiledConditionTagIndices);
            WriteNullableString(block, action.Confirmation);

            block.Flush();
        }

        writer.Write((int)blockStream.Length);
        writer.Write(blockStream.GetBuffer(), 0, (int)blockStream.Length);
    }

    private static void WriteTagRef(BinaryWriter writer, SchemeTagRef tag)
    {
        writer.Write(tag.IsParametric);
        writer.Write(tag.Name);
    }

    private static void WriteTagIndices(BinaryWriter writer, int[]? indices)
    {
        writer.Write(indices?.Length ?? -1);
        if (indices is not null)
            foreach (int index in indices)
                writer.Write(index);
    }

    private static void WriteStringPairs(BinaryWriter writer,
        IReadOnlyDictionary<string, string>? pairs)
    {
        writer.Write(pairs?.Count ?? -1);
        if (pairs is not null)
            foreach (var (key, value) in pairs)
            {
                writer.Write(key);
                writer.Write(value);
            }
    }

    private static void WriteNullableString(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null)
            writer.Write(value);
    }
}
