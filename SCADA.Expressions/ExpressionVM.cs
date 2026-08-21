using SCADA.Core.Tags;

namespace SCADA.Expressions;
public static class ExpressionVM
{
    private const int MaxStackDepth = 32;
    public static double Evaluate(Expression expression, EvaluationContext context)
    {
        // default(Expression) обходит required (Expression.cs): без явной
        // проверки вместо внятной ошибки был бы NullReferenceException
        // из середины цикла ВМ
        if (expression.Code is null)
            throw new InvalidOperationException(
                "Выражение не инициализировано: пустой байткод (default(Expression))");

        Span<double> stack = stackalloc double[MaxStackDepth];
        int sp = 0;

        byte[] code = expression.Code;
        int ip = 0;
        while (ip < code.Length)
        {
            switch ((OpCode)code[ip++])
            {
                case OpCode.LoadConst:
                    stack[sp++] = expression.Constants[ReadInt(code, ref ip)];
                    break;
                case OpCode.LoadTag:
                    stack[sp++] = context.Tags.Read(new TagId(ReadInt(code, ref ip))).Value;
                    break;
                case OpCode.Add:
                    stack[--sp - 1] += stack[sp];
                    break;
                case OpCode.Sub:
                    stack[--sp - 1] -= stack[sp];
                    break;
                case OpCode.Mul:
                    stack[--sp - 1] *= stack[sp];
                    break;
                case OpCode.Div:
                    stack[--sp - 1] /= stack[sp];
                    break;
                case OpCode.Mod:
                    stack[--sp - 1] %= stack[sp];
                    break;

                // сравнения кладут на стек 1.0 (истина) или 0.0 (ложь) —
                // отдельного булева типа на стеке нет, всё double
                case OpCode.Greater:
                    stack[--sp - 1] = stack[sp - 1] > stack[sp] ? 1.0 : 0.0;
                    break;
                case OpCode.GreaterOrEqual:
                    stack[--sp - 1] = stack[sp - 1] >= stack[sp] ? 1.0 : 0.0;
                    break;
                case OpCode.Less:
                    stack[--sp - 1] = stack[sp - 1] < stack[sp] ? 1.0 : 0.0;
                    break;
                case OpCode.LessOrEqual:
                    stack[--sp - 1] = stack[sp - 1] <= stack[sp] ? 1.0 : 0.0;
                    break;
                case OpCode.Equal:
                    stack[--sp - 1] = stack[sp - 1] == stack[sp] ? 1.0 : 0.0;
                    break;
                case OpCode.NotEqual:
                    stack[--sp - 1] = stack[sp - 1] != stack[sp] ? 1.0 : 0.0;
                    break;
                case OpCode.Not:
                    stack[sp - 1] = stack[sp - 1] == 0.0 ? 1.0 : 0.0;
                    break;

                case OpCode.JumpIfFalse:
                {
                    int target = ReadInt(code, ref ip);
                    if (stack[--sp] == 0.0)
                        ip = target;
                    break;
                }
                case OpCode.Jump:
                    ip = ReadInt(code, ref ip);
                    break;

                // операнды: 4 байта — id функции, 1 байт — число аргументов
                case OpCode.CallBuiltin:
                {
                    int funcId = ReadInt(code, ref ip);
                    int argCount = code[ip++];
                    sp -= argCount;
                    // аргументы — срез прямо на стеке: ни копий, ни аллокаций;
                    // результат пишется на освободившееся место
                    var args = stack.Slice(sp, argCount);
                    stack[sp] = BuiltinFunctions.Get(funcId).Invoke(args, context);
                    sp++;
                    break;
                }

                case OpCode.Return:
                    return stack[sp - 1];
                default:
                    throw new InvalidOperationException($"Неизвестная операция {code[ip - 1]}");
            }
        }

        throw new InvalidOperationException("Выражение завершилось без Return");
    }

    // все индексы и адреса в байткоде — 4-байтные int, little-endian
    private static int ReadInt(byte[] code, ref int ip)
    {
        int value = BitConverter.ToInt32(code, ip);
        ip += 4;
        return value;
    }
}
