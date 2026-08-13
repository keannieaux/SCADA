namespace SCADA.Expressions.Compiler;

/// <summary>
/// Связывание: разрешает имена тегов в индексы TagTable (§11.6),
/// имена функций — в id реестра, проверяет арность.
/// Собирает все ошибки и кидает одно исключение со списком —
/// редактору (§11.9) нужны все проблемы разом, а не первая.
/// </summary>
public static class Binder
{
    public static BoundExpression Bind(Node ast, ITagCatalog catalog)
    {
        var errors = new List<CompileError>();
        var tagIndices = new List<int>();

        var root = BindNode(ast, catalog, errors, tagIndices, asTagIndex: false);

        if (errors.Count > 0)
            throw new ExpressionCompileException(errors);

        return new BoundExpression(root!, tagIndices.ToArray());
    }

    // asTagIndex: аргумент функции, объявленный ссылкой на тег (IsGood, ValueOr) —
    // эмитим индекс тега числом, а не значение
    private static BoundNode? BindNode(Node node, ITagCatalog catalog,
        List<CompileError> errors, List<int> tagIndices, bool asTagIndex)
    {
        switch (node)
        {
            case NumberNode n:
                if (asTagIndex)
                {
                    errors.Add(new CompileError(
                        "Аргумент должен быть именем тега, а не числом", -1));
                    return null;
                }
                return new BoundNumber(n.Value);

            case TagRefNode t:
                if (!catalog.TryGetIndex(t.Name, out int index))
                {
                    errors.Add(new CompileError($"Тег '{t.Name}' не найден", t.Position));
                    return null;
                }
                if (!tagIndices.Contains(index))
                    tagIndices.Add(index);
                return asTagIndex ? new BoundTagIndex(index) : new BoundTagValue(index);

            case BinaryNode b:
            {
                var left = BindNode(b.Left, catalog, errors, tagIndices, asTagIndex: false);
                var right = BindNode(b.Right, catalog, errors, tagIndices, asTagIndex: false);
                return left is null || right is null ? null : new BoundBinary(b.Op, left, right);
            }

            case UnaryNode u:
            {
                var operand = BindNode(u.Operand, catalog, errors, tagIndices, asTagIndex: false);
                return operand is null ? null : new BoundUnary(u.Op, operand);
            }

            case ConditionalNode c:
            {
                var condition = BindNode(c.Condition, catalog, errors, tagIndices, asTagIndex: false);
                var whenTrue = BindNode(c.WhenTrue, catalog, errors, tagIndices, asTagIndex: false);
                var whenFalse = BindNode(c.WhenFalse, catalog, errors, tagIndices, asTagIndex: false);
                return condition is null || whenTrue is null || whenFalse is null
                    ? null
                    : new BoundConditional(condition, whenTrue, whenFalse);
            }

            case CallNode call:
                return BindCall(call, catalog, errors, tagIndices);

            default:
                throw new ExpressionCompileException($"Неизвестный узел AST: {node.GetType().Name}");
        }
    }

    private static BoundNode? BindCall(CallNode call, ITagCatalog catalog,
        List<CompileError> errors, List<int> tagIndices)
    {
        if (!BuiltinFunctions.TryGetByName(call.Name, out var info))
        {
            errors.Add(new CompileError($"Неизвестная функция '{call.Name}'", call.Position));
            return null;
        }

        if (call.Args.Count != info.ArgCount)
        {
            errors.Add(new CompileError(
                $"Функция '{call.Name}' ожидает {info.ArgCount} арг., получено {call.Args.Count}",
                call.Position));
            return null;
        }

        var boundArgs = new List<BoundNode>(info.ArgCount);
        for (int i = 0; i < call.Args.Count; i++)
        {
            // метаданные функции решают, какие аргументы — ссылки на теги
            var bound = BindNode(call.Args[i], catalog, errors, tagIndices,
                asTagIndex: info.TagRefArgs.Contains(i));
            if (bound is null)
                return null;
            boundArgs.Add(bound);
        }

        return new BoundCall(info, boundArgs);
    }
}
