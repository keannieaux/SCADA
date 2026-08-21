namespace SCADA.Core.Schemes;

/// <summary>
/// Дескриптор параметра действия (docs/scheme-controls-plan.md, C1) —
/// аналог PropertyDef для свойств элементов.
/// </summary>
/// <param name="Name">Имя параметра ("Tag", "Value") — стабильно, по нему
/// редактор и диагностики ссылаются на параметр.</param>
/// <param name="DisplayName">Отображаемое имя по-русски. Живёт здесь, а не
/// в редакторе (решение 1): один список, не разъезжается. Локализация, если
/// появится, — слой поверх по Name, каталог остаётся fallback.</param>
/// <param name="Type">Смысловой тип — редактор значения и валидация.</param>
/// <param name="Required">Обязательность (проверяется при сборке).</param>
/// <param name="CanBeExpression">Задел C2: значение может быть выражением,
/// компилируемым в code.bin. До C2 врёт «нет» у всех, кроме задокументированных.</param>
/// <param name="GetValue">Акцессор к значению параметра у экземпляра действия.
/// Делегат, а не рефлексия: читается в горячей валидации сборки, а тип
/// значения у параметров разнородный.</param>
public sealed record ActionParamDef(
    string Name,
    string DisplayName,
    ActionParamType Type,
    bool Required,
    bool CanBeExpression = false,
    Func<SchemeAction, object?>? GetValue = null);
