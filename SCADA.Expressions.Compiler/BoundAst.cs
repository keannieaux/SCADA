using SCADA.Expressions;

namespace SCADA.Expressions.Compiler;

// «Связанное» дерево: имена разрешены в индексы, функции — в id.
// AST — «что написал человек», BoundNode — «что это значит».
// Отсюда эмиттер генерирует байткод, не думая про имена.

public abstract record BoundNode;

public sealed record BoundNumber(double Value) : BoundNode;

// значение тега → LoadTag
public sealed record BoundTagValue(int TagIndex) : BoundNode;

// ссылка на тег как число → LoadConst индекса (аргументы IsGood/ValueOr)
public sealed record BoundTagIndex(int TagIndex) : BoundNode;

public sealed record BoundBinary(TokenKind Op, BoundNode Left, BoundNode Right) : BoundNode;

public sealed record BoundUnary(TokenKind Op, BoundNode Operand) : BoundNode;

public sealed record BoundConditional(BoundNode Condition, BoundNode WhenTrue, BoundNode WhenFalse) : BoundNode;

public sealed record BoundCall(BuiltinInfo Function, IReadOnlyList<BoundNode> Args) : BoundNode;

/// <summary>
/// Результат связывания: дерево + список всех тегов выражения.
/// TagIndices нужен рантайму для пересчёта по эпохам (ТЗ §11.7):
/// выражение пересчитывается, только когда изменился один из этих тегов.
/// </summary>
public sealed record BoundExpression(BoundNode Root, int[] TagIndices);
