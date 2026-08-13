using SCADA.Core.Tags;

namespace SCADA.Expressions;
public static class ExpressionVM
{
    private const int MaxStackDepth = 32;
    public static double Evaluate(Expression expression, EvaluationContext context)
    {
        Span<double> stack = stackalloc double[MaxStackDepth];
        int sp = 0;

        byte[] code = expression.Code;
        for(int ip = 0; ip < code.Length;)
        {
            switch ((OpCode)code[ip++])
            {
                case OpCode.LoadConst:
                    stack[sp++] = expression.Constants[code[ip++]];
                    break;
                case OpCode.LoadTag:
                    stack[sp++] = context.Tags.Read(new TagId(code[ip++])).Value;
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

                // операнд — абсолютная позиция (2 байта, little-endian)
                case OpCode.JumpIfFalse:
                {
                    int target = code[ip++] | (code[ip++] << 8);
                    if (stack[--sp] == 0.0)
                        ip = target;
                    break;
                }
                case OpCode.Jump:
                    ip = code[ip++] | (code[ip++] << 8);
                    break;

                // операнды: 1 байт — id функции, 1 байт — число аргументов
                case OpCode.CallBuiltin:
                {
                    int funcId = code[ip++];
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
}
