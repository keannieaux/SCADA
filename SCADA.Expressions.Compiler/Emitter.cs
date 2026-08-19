using SCADA.Expressions;

namespace SCADA.Expressions.Compiler;

/// <summary>
/// Эмиттер: связанное дерево → байткод. Пул констант дедуплицируется
/// (одинаковые числа занимают один слот), переходы собираются через
/// backpatching: сначала заглушка, адрес вписывается, когда цель доэмитирована.
/// </summary>
public static class Emitter
{
    public static CompiledExpression Emit(BoundExpression bound)
    {
        var code = new List<byte>(64);
        var constants = new List<double>();

        EmitNode(bound.Root, code, constants);
        code.Add((byte)OpCode.Return);

        return new CompiledExpression
        {
            Code = code.ToArray(),
            Constants = constants.ToArray(),
            TagIndices = bound.TagIndices
        };
    }

    private static void EmitNode(BoundNode node, List<byte> code, List<double> constants)
    {
        switch (node)
        {
            case BoundNumber n:
                EmitLoadConst(code, constants, n.Value);
                break;

            case BoundTagIndex t:
                // ссылка на тег — это число (индекс), кладём через пул констант
                EmitLoadConst(code, constants, t.TagIndex);
                break;

            case BoundTagValue t:
                code.Add((byte)OpCode.LoadTag);
                EmitInt(code, t.TagIndex);
                break;

            // constant folding: -5 — это одна константа, без унарной инструкции
            case BoundUnary { Op: TokenKind.Minus, Operand: BoundNumber n }:
                EmitLoadConst(code, constants, -n.Value);
                break;

            case BoundUnary { Op: TokenKind.Minus } u:
                // -x  →  x * (-1)
                EmitNode(u.Operand, code, constants);
                EmitLoadConst(code, constants, -1.0);
                code.Add((byte)OpCode.Mul);
                break;

            case BoundUnary { Op: TokenKind.Bang } u:
                EmitNode(u.Operand, code, constants);
                code.Add((byte)OpCode.Not);
                break;

            case BoundBinary { Op: TokenKind.AndAnd } b:
            {
                // <left> JIF->L1 <right> Jump->L2 L1: 0 L2:
                EmitNode(b.Left, code, constants);
                int toFalse = EmitJump(code, OpCode.JumpIfFalse);
                EmitNode(b.Right, code, constants);
                int toEnd = EmitJump(code, OpCode.Jump);
                PatchJump(code, toFalse);
                EmitLoadConst(code, constants, 0.0);
                PatchJump(code, toEnd);
                break;
            }

            case BoundBinary { Op: TokenKind.OrOr } b:
            {
                // <left> JIF->L1 1 Jump->L2 L1: <right> L2:
                EmitNode(b.Left, code, constants);
                int toRight = EmitJump(code, OpCode.JumpIfFalse);
                EmitLoadConst(code, constants, 1.0);
                int toEnd = EmitJump(code, OpCode.Jump);
                PatchJump(code, toRight);
                EmitNode(b.Right, code, constants);
                PatchJump(code, toEnd);
                break;
            }

            case BoundBinary b:
                EmitNode(b.Left, code, constants);
                EmitNode(b.Right, code, constants);
                code.Add((byte)MapBinaryOp(b.Op));
                break;

            case BoundConditional c:
            {
                // <cond> JIF->L1 <true> Jump->L2 L1: <false> L2:
                EmitNode(c.Condition, code, constants);
                int toFalse = EmitJump(code, OpCode.JumpIfFalse);
                EmitNode(c.WhenTrue, code, constants);
                int toEnd = EmitJump(code, OpCode.Jump);
                PatchJump(code, toFalse);
                EmitNode(c.WhenFalse, code, constants);
                PatchJump(code, toEnd);
                break;
            }

            case BoundCall call:
                foreach (var arg in call.Args)
                    EmitNode(arg, code, constants);
                code.Add((byte)OpCode.CallBuiltin);
                EmitInt(code, call.Function.Id);
                code.Add((byte)call.Args.Count);
                break;

            default:
                throw new ExpressionCompileException($"Неизвестный узел: {node.GetType().Name}");
        }
    }

    private static OpCode MapBinaryOp(TokenKind op) => op switch
    {
        TokenKind.Plus => OpCode.Add,
        TokenKind.Minus => OpCode.Sub,
        TokenKind.Star => OpCode.Mul,
        TokenKind.Slash => OpCode.Div,
        TokenKind.Percent => OpCode.Mod,
        TokenKind.Greater => OpCode.Greater,
        TokenKind.GreaterOrEqual => OpCode.GreaterOrEqual,
        TokenKind.Less => OpCode.Less,
        TokenKind.LessOrEqual => OpCode.LessOrEqual,
        TokenKind.EqualEqual => OpCode.Equal,
        TokenKind.NotEqual => OpCode.NotEqual,
        _ => throw new ExpressionCompileException($"Оператор {op} не поддерживается эмиттером")
    };

    // пул констант дедуплицируется: одинаковые числа — один слот
    private static int AddConstant(List<double> constants, double value)
    {
        int existing = constants.IndexOf(value);
        if (existing >= 0)
            return existing;
        constants.Add(value);
        return constants.Count - 1;
    }

    private static void EmitLoadConst(List<byte> code, List<double> constants, double value)
    {
        code.Add((byte)OpCode.LoadConst);
        EmitInt(code, AddConstant(constants, value));
    }

    // --- backpatching переходов ---

    // пишет опкод перехода + 4 байта заглушки под адрес, возвращает позицию заглушки
    private static int EmitJump(List<byte> code, OpCode jump)
    {
        code.Add((byte)jump);
        int stub = code.Count;
        EmitInt(code, 0);
        return stub;
    }

    // вписывает в заглушку текущую позицию кода — цель доэмитирована
    private static void PatchJump(List<byte> code, int stub)
    {
        int target = code.Count;
        code[stub] = (byte)target;
        code[stub + 1] = (byte)(target >> 8);
        code[stub + 2] = (byte)(target >> 16);
        code[stub + 3] = (byte)(target >> 24);
    }

    private static void EmitInt(List<byte> code, int value)
    {
        code.Add((byte)value);
        code.Add((byte)(value >> 8));
        code.Add((byte)(value >> 16));
        code.Add((byte)(value >> 24));
    }
}
