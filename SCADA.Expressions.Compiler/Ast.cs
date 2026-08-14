namespace SCADA.Expressions.Compiler;

// Узлы дерева разбора. Живут только внутри компилятора:
// текст → AST → байткод, после чего AST выбрасывается.
// Позиции тащим для диагностики — биндер скажет «тег не найден, позиция 8».

public abstract record Node;

public sealed record NumberNode(double Value) : Node;

public sealed record TagRefNode(string Name, int Position) : Node;

public sealed record BinaryNode(TokenKind Op, Node Left, Node Right, int Position) : Node;

public sealed record UnaryNode(TokenKind Op, Node Operand, int Position) : Node;

public sealed record ConditionalNode(Node Condition, Node WhenTrue, Node WhenFalse, int Position) : Node;

public sealed record CallNode(string Name, IReadOnlyList<Node> Args, int Position) : Node;
