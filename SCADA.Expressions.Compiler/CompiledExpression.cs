using SCADA.Expressions;

namespace SCADA.Expressions.Compiler;

/// <summary>
/// Результат компиляции выражения: байткод + пул констант + список тегов.
/// TagIndices нужен рантайму для пересчёта по эпохам (ТЗ §11.7).
/// Растёт членами: в M3 появится пул строковых констант (§11.2).
/// </summary>
public sealed class CompiledExpression
{
    public required byte[] Code { get; init; }
    public required double[] Constants { get; init; }
    public required int[] TagIndices { get; init; }

    // рантайм-представление для ВМ — метаданные остаются в CompiledExpression
    public Expression ToExpression() => new() { Code = Code, Constants = Constants };
}
