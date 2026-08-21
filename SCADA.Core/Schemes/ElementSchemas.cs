namespace SCADA.Core.Schemes;

/// <summary>
/// Реестр схем свойств по видам элементов (концепт §3.2) — единственный
/// источник истины: бинарные секции, валидация сборки, рендер и панель
/// свойств редактора читают отсюда.
///
/// Правила расширения:
/// - id свойства глобально уникален и СТАБИЛЕН (пишется в пакет): новое
///   свойство = новый id, занятые никогда не переиспользуются;
/// - добавление свойства/вида не меняет существующие записи — иначе
///   ломается чтение старых пакетов (§11.2).
/// </summary>
public static class ElementSchemas
{
    // Базовые свойства всех визуальных видов (кроме Control/Instance).
    // Бывшие «каналы» M3 — теперь обычные анимируемые свойства (концепт §4.1).
    private static readonly PropertyDef[] Base =
    [
        new(1, "Opacity", PropertyType.Number, PropertyValue.FromNumber(1.0), true, "Внешний вид"),
        new(2, "RotationDegrees", PropertyType.Number, PropertyValue.FromNumber(0), true, "Компоновка"),
        new(3, "PositionOffsetX", PropertyType.Number, PropertyValue.FromNumber(0), true, "Компоновка"),
        new(4, "PositionOffsetY", PropertyType.Number, PropertyValue.FromNumber(0), true, "Компоновка"),
        new(5, "Visible", PropertyType.Boolean, PropertyValue.FromBool(true), true, "Поведение"),
        new(6, "Blink", PropertyType.Boolean, PropertyValue.FromBool(false), true, "Поведение"),
        new(7, "Text", PropertyType.String, PropertyValue.FromString(""), true, "Текст"),
        new(8, "TextFormat", PropertyType.String, PropertyValue.FromString("F1"), false, "Текст"),
        new(9, "Units", PropertyType.String, PropertyValue.FromString(""), false, "Текст"),
    ];

    // Общие свойства фигур.
    private static readonly PropertyDef[] Shape =
    [
        new(10, "FillColor", PropertyType.Color, PropertyValue.FromColor(0xFF33383D), true, "Кисть"),
        new(11, "BorderColor", PropertyType.Color, PropertyValue.FromColor(0xFF3A3F44), true, "Кисть"),
        new(12, "BorderThickness", PropertyType.Number, PropertyValue.FromNumber(0), false, "Кисть"),
        // 0 (прозрачный) = без тонирования: символ рисуется как есть.
        new(13, "TintColor", PropertyType.Color, PropertyValue.FromColor(0), true, "Кисть"),
        new(14, "FillLevel", PropertyType.Number, PropertyValue.FromNumber(0), true, "Заполнение"),
    ];

    private static readonly PropertyDef[] Text =
    [
        new(20, "FontFamily", PropertyType.String, PropertyValue.FromString("Cascadia Code"), false, "Шрифт"),
        new(21, "FontSize", PropertyType.Number, PropertyValue.FromNumber(14), false, "Шрифт"),
        new(22, "FontWeight", PropertyType.Choice, PropertyValue.FromChoice(0), false, "Шрифт")
            { Choices = ["Normal", "Bold"] },
        new(23, "Foreground", PropertyType.Color, PropertyValue.FromColor(0xFFE7E9EA), true, "Шрифт"),
        new(24, "HorizontalAlignment", PropertyType.Choice, PropertyValue.FromChoice(1), false, "Текст")
            { Choices = ["Left", "Center", "Right"] },
        new(25, "VerticalAlignment", PropertyType.Choice, PropertyValue.FromChoice(1), false, "Текст")
            { Choices = ["Top", "Center", "Bottom"] },
    ];

    private static readonly PropertyDef[] Symbol =
    [
        new(30, "SymbolName", PropertyType.String, PropertyValue.FromString(""), false, "Символ"),
    ];

    private static readonly PropertyDef[] Image =
    [
        new(32, "ImageName", PropertyType.String, PropertyValue.FromString(""), false, "Изображение"),
        new(33, "Stretch", PropertyType.Choice, PropertyValue.FromChoice(2), false, "Изображение")
            { Choices = ["None", "Fill", "Uniform", "UniformFill"] },
    ];

    private static readonly PropertyDef[] PathProps =
    [
        new(40, "PathData", PropertyType.String, PropertyValue.FromString(""), false, "Геометрия"),
    ];

    private static readonly PropertyDef[] PolylineProps =
    [
        new(41, "Points", PropertyType.String, PropertyValue.FromString(""), false, "Геометрия"),
    ];

    private static readonly PropertyDef[] RectangleProps =
    [
        new(42, "CornerRadius", PropertyType.Number, PropertyValue.FromNumber(0), false, "Форма"),
    ];

    // Свойства уровня схемы/шаблона (концепт §6): id 100+, те же правила
    // стабильности, что у свойств элементов.
    private static readonly PropertyDef[] SchemeProps =
    [
        new(100, "Background", PropertyType.Color, PropertyValue.FromColor(0xFF1B1D20), false, "Экран"),
        new(101, "DesignWidth", PropertyType.Number, PropertyValue.FromNumber(1920), false, "Экран"),
        new(102, "DesignHeight", PropertyType.Number, PropertyValue.FromNumber(1080), false, "Экран"),
        // Множитель поверх базового масштаба вписывания: 1.0 — экран открыт
        // ровно вписанным. Значим только при разрешённых пане и зуме — иначе
        // оператор увидел бы обрезанный экран без возможности его подвинуть.
        new(103, "StartZoom", PropertyType.Number, PropertyValue.FromNumber(1.0), false, "Экран"),

        // Пан и зум по умолчанию запрещены: схема вписана целиком, двигать
        // её незачем, а случайный сдвиг мышью уводит за край аварийный
        // индикатор — то есть ломает гарантию «оператор видит всё».
        // Включается осознанно: карта цеха, большой план, сенсорная панель.
        new(104, "AllowPanZoom", PropertyType.Boolean, PropertyValue.FromBool(false), false, "Экран"),

        // Предел приближения. Отдалить меньше вписанного нельзя в принципе:
        // схема уплыла бы в угол окна, а вокруг осталась пустота.
        new(105, "MaxZoom", PropertyType.Number, PropertyValue.FromNumber(4.0), false, "Экран"),
    ];

    /// <summary>Схема свойств уровня схемы/шаблона (не элементов).</summary>
    public static IReadOnlyList<PropertyDef> SchemeProperties => SchemeProps;

    public static PropertyDef? FindSchemeProperty(int propertyId)
        => SchemeProps.FirstOrDefault(d => d.Id == propertyId);

    private static readonly Dictionary<ElementKind, IReadOnlyList<PropertyDef>> _byKind = new()
    {
        [ElementKind.Rectangle] = [..Base, ..Shape, ..RectangleProps],
        [ElementKind.Ellipse] = [..Base, ..Shape],
        [ElementKind.Line] = [..Base, ..Shape],
        [ElementKind.Polyline] = [..Base, ..Shape, ..PolylineProps],
        [ElementKind.Path] = [..Base, ..Shape, ..PathProps],
        [ElementKind.Text] = [..Base, ..Text],
        [ElementKind.Symbol] = [..Base, ..Shape, ..Symbol],
        [ElementKind.Image] = [..Base, ..Image],

        // Конфигурация hosted-контрола: списочные конфиги (перья тренда,
        // фильтры журнала) в скалярный PropertyValue не ложатся — поэтому
        // JSON-документ, валидируемый контролом на своей стороне (§8).
        [ElementKind.Control] =
        [
            new PropertyDef(50, "ConfigJson", PropertyType.String,
                PropertyValue.FromString(""), false, "Контрол"),
        ],

        // Параметры экземпляра — поля SchemeElement.TemplateName/Parameters.
        [ElementKind.Instance] = [],
    };

    public static IReadOnlyList<PropertyDef> For(ElementKind kind) => _byKind[kind];

    public static PropertyDef? Find(ElementKind kind, int propertyId)
        => _byKind[kind].FirstOrDefault(d => d.Id == propertyId);

    /// <summary>Все зарегистрированные виды — для тестов целостности реестра.</summary>
    public static IReadOnlyCollection<ElementKind> Kinds => _byKind.Keys;

    /// <summary>Проверка при сборке/загрузке: привязка к свойству, которого
    /// нет у вида или которое неанимируемо, — ошибка конфигурации (§3.2).</summary>
    public static string? ValidateBinding(ElementKind kind, int propertyId)
    {
        var def = Find(kind, propertyId);
        if (def is null)
            return $"у вида {kind} нет свойства с id {propertyId}";
        if (!def.Animatable)
            return $"свойство '{def.Name}' вида {kind} неанимируемо";
        return null;
    }
}
