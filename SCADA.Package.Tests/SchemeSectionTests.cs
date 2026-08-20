using System.Text;
using SCADA.Core.Schemes;
using SCADA.Package.Builder.Sections;
using SCADA.Package.Sections;

namespace SCADA.Package.Tests;

/// <summary>
/// Парность SchemeSectionWriter/SchemeSectionReader (концепт §11): roundtrip
/// схем и шаблонов по всем видам полей и правила эволюции §11.2 — неизвестное
/// пропускается, версия выше поддерживаемой — внятный отказ.
/// </summary>
public class SchemeSectionTests
{
    private static readonly Guid SchemeId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ParentId = new("22222222-2222-2222-2222-222222222222");

    private static Scheme BuildSampleScheme() => new()
    {
        Id = SchemeId,
        Name = "overview",
        Properties =
        [
            new ElementProperty(100, PropertyValue.FromColor(0xFF1B1D20)), // Background
            new ElementProperty(101, PropertyValue.FromNumber(1920)),      // DesignWidth
        ],
        Events =
        [
            // событие уровня экрана (§5.1): цепочка с условием и подтверждением
            new SchemeEvent
            {
                Kind = SchemeEventKind.Opened,
                Actions =
                [
                    new WriteTagAction(SchemeTagRef.Absolute("Pump1.Running"), 1)
                    {
                        Condition = "Boiler1.Temp > 0",
                        CompiledConditionIndex = 0,
                        CompiledConditionTagIndices = [1]
                    },
                    new ShowDialogAction("Экран секции"),
                ]
            },
            new SchemeEvent { Kind = SchemeEventKind.Closed, Actions = [new BackAction()] },
        ],
        Elements =
        [
            new SchemeElement
            {
                Id = new Guid("33333333-3333-3333-3333-333333333333"),
                Name = "pipe",
                Kind = ElementKind.Rectangle,
                X = 10.5, Y = -20, Width = 100, Height = 50, ZOrder = 3,
                ParentId = ParentId,
                Properties =
                [
                    new ElementProperty(10, PropertyValue.FromColor(0xFF33383D)), // FillColor
                    new ElementProperty(12, PropertyValue.FromNumber(2.5)),       // BorderThickness
                    new ElementProperty(5, PropertyValue.FromBool(false)),        // Visible
                    new ElementProperty(7, PropertyValue.FromString("Насос")),    // Text
                ],
                Bindings =
                [
                    // интерполированная привязка со стопами цвета
                    new ElementBinding
                    {
                        PropertyId = 10, Expression = "Boiler1.Temp",
                        Mapping = StopMapping.Interpolated,
                        Stops =
                        [
                            new Stop(0, PropertyValue.FromColor(0xFF0000FF)),
                            new Stop(100, PropertyValue.FromColor(0xFFFF0000)),
                        ],
                        CompiledExpressionIndex = 2, CompiledTagIndices = [0]
                    },
                    // volatile-привязка без стопов (§4.3)
                    new ElementBinding
                    {
                        PropertyId = 2, Expression = "t",
                        Mapping = StopMapping.Direct, Volatile = true,
                        CompiledExpressionIndex = 3, CompiledTagIndices = []
                    },
                    // привязка без скомпилированных индексов (несобранный проект)
                    new ElementBinding
                    {
                        PropertyId = 5, Expression = "Pump1.Running",
                        Mapping = StopMapping.Discrete
                    },
                ],
                Events =
                [
                    new SchemeEvent
                    {
                        Kind = SchemeEventKind.Click,
                        Actions =
                        [
                            new WriteTagAction(SchemeTagRef.Absolute("Pump1.Run"), 1)
                            {
                                Condition = "Pump1.Auto > 0",
                                Confirmation = "Запустить насос?",
                                CompiledConditionIndex = 1,
                                CompiledConditionTagIndices = [4, 7]
                            },
                            new ToggleTagAction(SchemeTagRef.Parametric("Prefix", ".Run")),
                            new OpenSchemeAction("detail",
                                new Dictionary<string, string> { ["Prefix"] = "Н7" }),
                            new OpenPopupAction("pump-panel"),
                            new ClosePopupAction(),
                            new BackAction(),
                            new ShowDialogAction("Готово"),
                        ]
                    },
                    new SchemeEvent { Kind = SchemeEventKind.PointerEnter, Actions = [] },
                ]
            },
            // Choice- и цветовые свойства текстового вида
            new SchemeElement
            {
                Id = new Guid("44444444-4444-4444-4444-444444444444"),
                Kind = ElementKind.Text,
                X = 0, Y = 0, Width = 10, Height = 10,
                Properties =
                [
                    new ElementProperty(24, PropertyValue.FromChoice(2)),      // HorizontalAlignment
                    new ElementProperty(23, PropertyValue.FromColor(0xFFE7E9EA)), // Foreground
                ]
            },
            // служебные виды: Control и Instance (§7, §8)
            new SchemeElement
            {
                Id = new Guid("55555555-5555-5555-5555-555555555555"),
                Kind = ElementKind.Control, ControlType = "trend",
                X = 1, Y = 2, Width = 3, Height = 4,
                Properties =
                [
                    // списочный конфиг контрола — JSON-документ (§8)
                    new ElementProperty(50, PropertyValue.FromString(
                        """{"pens":[{"tag":"Boiler1.Temp"}]}""")),
                ]
            },
            new SchemeElement
            {
                Id = new Guid("66666666-6666-6666-6666-666666666666"),
                Kind = ElementKind.Instance, TemplateName = "pump",
                TemplateParameters = new Dictionary<string, string> { ["Prefix"] = "Н7" },
                X = 5, Y = 6, Width = 7, Height = 8, ZOrder = -1
            },
        ]
    };

    [Fact]
    public void Scheme_Roundtrip_PreservesEverything()
    {
        var scheme = BuildSampleScheme();

        var read = SchemeSectionReader.ReadScheme(SchemeSectionWriter.Write(scheme));

        AssertSchemeEqual(scheme, read);
    }

    [Fact]
    public void Template_Roundtrip_PreservesParametersAndElements()
    {
        var template = new SchemeTemplate
        {
            Id = SchemeId,
            Name = "pump",
            Properties =
            [
                new ElementProperty(100, PropertyValue.FromColor(0xFF000000)),
                new ElementProperty(103, PropertyValue.FromNumber(0.5)),
            ],
            Events =
            [
                new SchemeEvent
                {
                    Kind = SchemeEventKind.Closed,
                    Actions = [new ClosePopupAction()]
                },
            ],
            Parameters =
            [
                new TemplateParameter("Prefix", TemplateParameterType.String, "Н1"),
                new TemplateParameter("Speed", TemplateParameterType.Number, null),
            ],
            Elements = BuildSampleScheme().Elements
        };

        var read = SchemeSectionReader.ReadTemplate(SchemeSectionWriter.WriteTemplate(template));

        Assert.Equal(template.Id, read.Id);
        Assert.Equal(template.Name, read.Name);
        Assert.Equal(template.Properties, read.Properties);
        AssertEventsEqual(template.Events, read.Events);
        Assert.Equal(template.Parameters, read.Parameters);
        Assert.Equal(template.Elements.Count, read.Elements.Count);
        for (int i = 0; i < template.Elements.Count; i++)
            AssertElementEqual(template.Elements[i], read.Elements[i]);
    }

    [Fact]
    public void UnknownElementKind_SkippedByBlockLength()
    {
        byte[] section = BuildSection(SchemeSectionWriter.Version, null, null,
            BuildElementBlock(200), // неизвестный вид из будущего
            BuildElementBlock((byte)ElementKind.Rectangle));

        var scheme = SchemeSectionReader.ReadScheme(section);

        var element = Assert.Single(scheme.Elements);
        Assert.Equal(ElementKind.Rectangle, element.Kind);
    }

    [Fact]
    public void UnknownTailInElementBlock_Skipped()
    {
        // «новая версия» дописала поля в хвост блока — старый читатель
        // читает известное и перепрыгивает хвост по длине (§11.2)
        byte[] section = BuildSection(SchemeSectionWriter.Version, null, null,
            BuildElementBlock((byte)ElementKind.Rectangle,
                tail: writer => writer.Write(123.456)));

        var scheme = SchemeSectionReader.ReadScheme(section);

        Assert.Single(scheme.Elements);
    }

    [Fact]
    public void UnknownPropertyId_ReadByTypeAndSkipped()
    {
        byte[] section = BuildSection(SchemeSectionWriter.Version, null, null,
            BuildElementBlock((byte)ElementKind.Rectangle, properties: writer =>
            {
                writer.Write(2); // propertyCount
                writer.Write(999); // неизвестный id — значение читается по типу
                writer.Write((byte)PropertyType.String);
                writer.Write("пропустить");
                writer.Write(10); // FillColor — известный
                writer.Write((byte)PropertyType.Color);
                writer.Write(0xFF0000FFu);
            }));

        var scheme = SchemeSectionReader.ReadScheme(section);

        var property = Assert.Single(scheme.Elements[0].Properties);
        Assert.Equal(10, property.PropertyId);
        Assert.Equal(PropertyValue.FromColor(0xFF0000FF), property.Value);
    }

    [Fact]
    public void UnknownPropertyValueType_Throws()
    {
        // новый ТИП значения — это новая версия формата, а не хвост:
        // размер неизвестен, продолжать чтение нельзя
        byte[] section = BuildSection(SchemeSectionWriter.Version, null, null,
            BuildElementBlock((byte)ElementKind.Rectangle, properties: writer =>
            {
                writer.Write(1);
                writer.Write(10);
                writer.Write((byte)99);
            }));

        Assert.Throws<PackageFormatException>(() => SchemeSectionReader.ReadScheme(section));
    }

    [Fact]
    public void UnknownActionType_SkippedByBlockLength()
    {
        byte[] section = BuildSection(SchemeSectionWriter.Version, null, null,
            BuildElementBlock((byte)ElementKind.Rectangle, events: writer =>
            {
                writer.Write(1); // eventCount
                writer.Write((byte)SchemeEventKind.Click);
                writer.Write(2); // actionCount
                byte[] unknown = [99, 1, 2, 3]; // тип из будущего + мусор
                writer.Write(unknown.Length);
                writer.Write(unknown);
                byte[] closePopup = [4, 255, 255, 255, 255, 255, 255, 255, 255, 0];
                // type=4 (ClosePopup), condition=-1, tagIndices=-1, no confirmation
                writer.Write(closePopup.Length);
                writer.Write(closePopup);
            }));

        var scheme = SchemeSectionReader.ReadScheme(section);

        var action = Assert.Single(scheme.Elements[0].Events[0].Actions);
        Assert.IsType<ClosePopupAction>(action);
    }

    [Fact]
    public void UnknownEventKind_Skipped()
    {
        byte[] section = BuildSection(SchemeSectionWriter.Version, null, null,
            BuildElementBlock((byte)ElementKind.Rectangle, events: writer =>
            {
                writer.Write(1);
                writer.Write((byte)250); // событие из будущего
                writer.Write(0);         // без действий
            }));

        var scheme = SchemeSectionReader.ReadScheme(section);

        Assert.Empty(scheme.Elements[0].Events);
    }

    [Fact]
    public void UnknownSchemePropertyId_ReadByTypeAndSkipped()
    {
        // неизвестное свойство уровня схемы (пакет новее) читается по байту
        // типа и отбрасывается, известное — сохраняется (§11.2)
        byte[] section = BuildSection(SchemeSectionWriter.Version,
            schemeProperties: writer =>
            {
                writer.Write(2);
                writer.Write(999); // неизвестный id
                writer.Write((byte)PropertyType.String);
                writer.Write("пропустить");
                writer.Write(100); // Background — известный
                writer.Write((byte)PropertyType.Color);
                writer.Write(0xFF1B1D20u);
            },
            elementBlocks: [BuildElementBlock((byte)ElementKind.Rectangle)]);

        var scheme = SchemeSectionReader.ReadScheme(section);

        var property = Assert.Single(scheme.Properties);
        Assert.Equal(100, property.PropertyId);
        Assert.Equal(PropertyValue.FromColor(0xFF1B1D20), property.Value);
        Assert.Single(scheme.Elements);
    }

    [Fact]
    public void UnknownSchemeEventKind_Skipped()
    {
        // событие экрана «из будущего» пропускается, как у элементов (§11.2)
        byte[] section = BuildSection(SchemeSectionWriter.Version, null,
            schemeEvents: writer =>
            {
                writer.Write(2);
                writer.Write((byte)250); // неизвестное
                writer.Write(0);
                writer.Write((byte)SchemeEventKind.Opened);
                writer.Write(0);
            },
            elementBlocks: [BuildElementBlock((byte)ElementKind.Rectangle)]);

        var scheme = SchemeSectionReader.ReadScheme(section);

        var schemeEvent = Assert.Single(scheme.Events);
        Assert.Equal(SchemeEventKind.Opened, schemeEvent.Kind);
        Assert.Single(scheme.Elements);
    }

    [Fact]
    public void UnknownTailInBindingBlock_Skipped()
    {
        // «новая версия» дописала поля в хвост блока привязки — читатель
        // читает известное и перепрыгивает хвост по длине (§11.2)
        byte[] section = BuildSection(SchemeSectionWriter.Version,
            elementBlocks:
            [
                BuildElementBlock((byte)ElementKind.Rectangle, bindings: writer =>
                {
                    writer.Write(1); // BindingCount
                    using var blockStream = new MemoryStream();
                    var block = new BinaryWriter(blockStream, Encoding.UTF8);
                    block.Write(10);                 // PropertyId = FillColor
                    block.Write((byte)StopMapping.Direct);
                    block.Write(false);              // Volatile
                    block.Write(5);                  // CompiledExpressionIndex
                    block.Write(-1);                 // CompiledTagIndices = null
                    block.Write(-1);                 // Stops = null
                    block.Write(777.0);              // хвост «будущей версии»
                    block.Flush();
                    writer.Write((int)blockStream.Length);
                    writer.Write(blockStream.GetBuffer(), 0, (int)blockStream.Length);
                })
            ]);

        var scheme = SchemeSectionReader.ReadScheme(section);

        var binding = Assert.Single(scheme.Elements[0].Bindings);
        Assert.Equal(10, binding.PropertyId);
        Assert.Equal(5, binding.CompiledExpressionIndex);
    }

    [Fact]
    public void VersionAboveSupported_Throws()
    {
        byte[] section = BuildSection((byte)(SchemeSectionWriter.Version + 1));

        var ex = Assert.Throws<PackageFormatException>(
            () => SchemeSectionReader.ReadScheme(section));
        Assert.Contains("версия", ex.Message);
    }

    // --- ручная сборка секций для тестов эволюции формата ---

    private static byte[] BuildSection(byte version,
        Action<BinaryWriter>? schemeProperties = null,
        Action<BinaryWriter>? schemeEvents = null,
        params byte[][] elementBlocks)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(version);
        writer.Write(SchemeId.ToByteArray());
        writer.Write("test");
        writer.Write(false); // право схемы отсутствует (§5)
        if (schemeProperties is null) writer.Write(0); else schemeProperties(writer);
        if (schemeEvents is null) writer.Write(0); else schemeEvents(writer);
        writer.Write(elementBlocks.Length);
        foreach (byte[] block in elementBlocks)
        {
            writer.Write(block.Length);
            writer.Write(block);
        }
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BuildElementBlock(byte kind,
        Action<BinaryWriter>? properties = null,
        Action<BinaryWriter>? bindings = null,
        Action<BinaryWriter>? events = null,
        Action<BinaryWriter>? tail = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(kind);
        writer.Write(Guid.NewGuid().ToByteArray());
        writer.Write("e");
        writer.Write(0.0); writer.Write(0.0);   // X, Y
        writer.Write(10.0); writer.Write(10.0); // Width, Height
        writer.Write(0);                        // ZOrder
        writer.Write(false);                    // нет ParentId
        writer.Write(false);                    // нет ControlType
        writer.Write(false);                    // нет TemplateName
        writer.Write(-1);                       // TemplateParameters = null
        if (properties is null) writer.Write(0); else properties(writer);
        if (bindings is null) writer.Write(0); else bindings(writer);
        if (events is null) writer.Write(0); else events(writer);
        writer.Write(false);                    // нет права (§5)
        writer.Write((byte)DeniedState.Disabled);
        // «поля из будущего» дописываются после известных полей блока
        tail?.Invoke(writer);
        writer.Flush();
        return stream.ToArray();
    }

    // --- сравнение ---

    private static void AssertSchemeEqual(Scheme expected, Scheme actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Properties, actual.Properties);
        AssertEventsEqual(expected.Events, actual.Events);
        Assert.Equal(expected.Elements.Count, actual.Elements.Count);
        for (int i = 0; i < expected.Elements.Count; i++)
            AssertElementEqual(expected.Elements[i], actual.Elements[i]);
    }

    private static void AssertElementEqual(SchemeElement expected, SchemeElement actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.X, actual.X);
        Assert.Equal(expected.Y, actual.Y);
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.ZOrder, actual.ZOrder);
        Assert.Equal(expected.ParentId, actual.ParentId);
        Assert.Equal(expected.ControlType, actual.ControlType);
        Assert.Equal(expected.TemplateName, actual.TemplateName);
        AssertStringPairs(expected.TemplateParameters, actual.TemplateParameters);

        Assert.Equal(expected.Properties, actual.Properties);

        Assert.Equal(expected.Bindings.Count, actual.Bindings.Count);
        for (int i = 0; i < expected.Bindings.Count; i++)
        {
            var e = expected.Bindings[i];
            var a = actual.Bindings[i];
            Assert.Equal(e.PropertyId, a.PropertyId);
            Assert.Equal(e.Mapping, a.Mapping);
            Assert.Equal(e.Volatile, a.Volatile);
            // текст выражения в пакет не пишется — только индексы пула (§11.4)
            Assert.Equal("", a.Expression);
            Assert.Equal(e.CompiledExpressionIndex, a.CompiledExpressionIndex);
            Assert.Equal(e.CompiledTagIndices, a.CompiledTagIndices);
            Assert.Equal(e.Stops, a.Stops);
        }

        AssertEventsEqual(expected.Events, actual.Events);
    }

    private static void AssertEventsEqual(IReadOnlyList<SchemeEvent> expected,
        IReadOnlyList<SchemeEvent> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Kind, actual[i].Kind);
            Assert.Equal(expected[i].Actions.Count, actual[i].Actions.Count);
            for (int j = 0; j < expected[i].Actions.Count; j++)
                AssertActionEqual(expected[i].Actions[j], actual[i].Actions[j]);
        }
    }

    private static void AssertActionEqual(SchemeAction expected, SchemeAction actual)
    {
        Assert.Equal(expected.GetType(), actual.GetType());
        // текст условия в пакет не пишется — только индексы пула (§11.4)
        Assert.Null(actual.Condition);
        Assert.Equal(expected.Confirmation, actual.Confirmation);
        Assert.Equal(expected.CompiledConditionIndex, actual.CompiledConditionIndex);
        Assert.Equal(expected.CompiledConditionTagIndices, actual.CompiledConditionTagIndices);

        switch (expected)
        {
            case WriteTagAction e:
                var a = (WriteTagAction)actual;
                Assert.Equal(e.Tag, a.Tag);
                Assert.Equal(e.Value, a.Value);
                break;
            case ToggleTagAction e2:
                Assert.Equal(e2.Tag, ((ToggleTagAction)actual).Tag);
                break;
            case OpenSchemeAction e3:
                var a3 = (OpenSchemeAction)actual;
                Assert.Equal(e3.SchemeName, a3.SchemeName);
                AssertStringPairs(e3.Parameters, a3.Parameters);
                break;
            case OpenPopupAction e4:
                var a4 = (OpenPopupAction)actual;
                Assert.Equal(e4.TemplateName, a4.TemplateName);
                AssertStringPairs(e4.Parameters, a4.Parameters);
                break;
            case ShowDialogAction e5:
                Assert.Equal(e5.Message, ((ShowDialogAction)actual).Message);
                break;
        }
    }

    private static void AssertStringPairs(IReadOnlyDictionary<string, string>? expected,
        IReadOnlyDictionary<string, string>? actual)
    {
        if (expected is null || expected.Count == 0)
        {
            Assert.True(actual is null || actual.Count == 0);
            return;
        }
        Assert.NotNull(actual);
        Assert.Equal(expected.Count, actual.Count);
        foreach (var (key, value) in expected)
            Assert.Equal(value, actual[key]);
    }
}
