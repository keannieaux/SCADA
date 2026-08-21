namespace SCADA.Expressions;

/// <summary>
/// Рантайм-представление скомпилированного выражения для ВМ: байткод и пул
/// констант. Структура, а не класс, намеренно — два поля-ссылки, 16 байт,
/// передаются в <see cref="ExpressionVM.Evaluate"/> не дороже указателя.
///
/// Причина: фабрики (`CompiledExpression.ToExpression`,
/// `CodeSectionReader.ToExpression`) вызываются в горячем цикле пересчёта
/// схемы — при тысяче привязок и 30 кадрах в секунду классовое представление
/// давало десятки мегабайт мусора в секунду на ровном месте
/// (docs/B0.6-render-allocations.md §3). Со структурой такой вызов
/// не аллоцирует в принципе.
///
/// Инвариант: <see cref="Code"/> непуст. `required` его не гарантирует —
/// `default(Expression)` обходит проверку компилятора, поэтому вход в ВМ
/// проверяется явно.
/// </summary>
public readonly record struct Expression
{
    public required byte[] Code { get; init; }
    public required double[] Constants { get; init; }
}
