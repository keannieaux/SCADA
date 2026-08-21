namespace SCADA.Core.Schemes;

/// <summary>
/// Скомпилированная форма значения составного параметра действия
/// (docs/scheme-controls-plan.md, C1 решение 3 / C2). Заполняется при сборке
/// пакета из исходной строки словаря Parameters. Строки собираются хостом
/// в момент выполнения действия (редкий путь — клик), ВМ остаётся числовой.
/// </summary>
public enum ActionParamValueKind : byte
{
    /// <summary>Константа: Text — как есть ("Pump5").</summary>
    Constant = 0,

    /// <summary>Прямая ссылка на строковый тег (паттерн A7): значение —
    /// текущая строка тега TagId. Покрывает «выбрал в списке» через
    /// сессионный строковый тег.</summary>
    StringTagRef = 1,

    /// <summary>Строковый шаблон с плейсхолдерами "{выражение}": Text —
    /// исходный текст, ExpressionIndices — индексы скомпилированных числовых
    /// выражений в пуле code.bin по порядку плейсхолдеров. Покрывает
    /// регулярную нумерацию: "Pump{N}", "Насосная{Station}.Pump{N}".</summary>
    Template = 2
}

/// <summary>
/// Один параметр экземпляра шаблона/экрана после сборки. Исходная строка
/// сохраняется в <see cref="SourceValue"/> для round-trip и диагностик.
/// </summary>
public sealed class CompiledActionParameter
{
    /// <summary>Ключ словаря Parameters — имя параметра шаблона.</summary>
    public required string Name { get; set; }

    /// <summary>Исходная строка значения из schemes/*.scheme.</summary>
    public required string SourceValue { get; set; }

    public ActionParamValueKind Kind { get; set; }

    /// <summary>Для StringTagRef — TagId строкового тега; иначе -1.</summary>
    public int TagId { get; set; } = -1;

    /// <summary>Для Template — индексы выражений пула code.bin по порядку
    /// плейсхолдеров в <see cref="SourceValue"/>; иначе null.</summary>
    public int[]? ExpressionIndices { get; set; }
}
