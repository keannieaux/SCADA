namespace SCADA.Core.Schemes;

/// <summary>Тип значения свойства элемента (концепт §3.2).</summary>
public enum PropertyType : byte
{
    Number = 0,
    Boolean = 1,
    Color = 2,
    String = 3,

    /// <summary>Выбор из фиксированного списка (выравнивание, стиль линии…).
    /// Значение — целое в Number, допустимые варианты задаёт дескриптор.</summary>
    Choice = 4,
}

/// <summary>
/// Значение свойства элемента — variant без боксинга. Какое поле значимо,
/// определяет <see cref="Type"/>: Number/Boolean/Choice → Number
/// (Boolean/Choice как 0/1 и целое), Color → Color (ARGB), String → Text.
/// Такая форма выбрана под бинарную сериализацию секций пакета: писатель
/// выбирает поле по Type, размер фиксирован на тип.
/// </summary>
public readonly record struct PropertyValue
{
    public PropertyType Type { get; init; }
    public double Number { get; init; }
    public uint Color { get; init; }
    public string? Text { get; init; }

    public static PropertyValue FromNumber(double value) => new() { Type = PropertyType.Number, Number = value };
    public static PropertyValue FromBool(bool value) => new() { Type = PropertyType.Boolean, Number = value ? 1 : 0 };
    public static PropertyValue FromColor(uint argb) => new() { Type = PropertyType.Color, Color = argb };
    public static PropertyValue FromString(string value) => new() { Type = PropertyType.String, Text = value };
    public static PropertyValue FromChoice(int value) => new() { Type = PropertyType.Choice, Number = value };

    public bool AsBool => Number != 0;
    public int AsChoice => (int)Number;
}
