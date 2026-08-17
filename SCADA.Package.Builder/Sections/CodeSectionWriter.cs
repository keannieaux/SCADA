using SCADA.Expressions;
using SCADA.Expressions.Compiler;

namespace SCADA.Package.Builder.Sections;

/// <summary>
/// Сериализатор секции code.bin — общий пул байткода проекта (ТЗ §14.2):
/// [пул констант][таблица выражений (смещения в blob)][сплошной байткод].
/// Два обязательных свойства: константы всех выражений слиты в один пул
/// (индексы LoadConst переписываются при сборке, загруженное выражение
/// пользуется общим пулом напрямую), одинаковые выражения дедуплицируются —
/// Tag > HiLimit встречается в проекте сотни раз, в пакете живёт один раз.
/// </summary>
public static class CodeSectionWriter
{
    public static byte[] Write(IReadOnlyList<CompiledExpression> expressions)
        => Write(expressions, out _);

    /// <summary>
    /// poolIndices[i] — индекс i-го входного выражения в итоговой таблице
    /// пула с учётом дедупликации. Нужен потребителям, которые ссылаются
    /// на выражения по номеру (правила сигнализации, M5).
    /// </summary>
    public static byte[] Write(IReadOnlyList<CompiledExpression> expressions,
        out int[] poolIndices)
    {
        var constants = new List<double>();
        var constantIndex = new Dictionary<double, int>();
        var blob = new List<byte>();

        // смещение/длина в blob + теги выражения (для пересчёта по эпохам §11.7)
        var table = new List<(int Offset, int Length, int[] Tags)>();
        var indexByKey = new Dictionary<string, int>(); // дедупликация

        poolIndices = new int[expressions.Count];
        for (int i = 0; i < expressions.Count; i++)
        {
            var expr = expressions[i];

            // дедупликация: одинаковые байткод+константы = одна запись в пуле
            string key = Convert.ToBase64String(expr.Code) + "|" +
                         string.Join(",", expr.Constants);
            if (indexByKey.TryGetValue(key, out int existing))
            {
                poolIndices[i] = existing;
                continue;
            }

            byte[] remapped = RemapConstants(expr.Code, expr.Constants, constants, constantIndex);

            indexByKey[key] = table.Count;
            poolIndices[i] = table.Count;
            table.Add((blob.Count, remapped.Length, expr.TagIndices));
            blob.AddRange(remapped);
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(constants.Count);
        foreach (var constant in constants)
            writer.Write(constant);

        writer.Write(table.Count);
        foreach (var (offset, length, tags) in table)
        {
            writer.Write(offset);
            writer.Write(length);
            writer.Write(tags.Length);
            foreach (var tag in tags)
                writer.Write(tag);
        }

        writer.Write(blob.Count);
        writer.Write(blob.ToArray());

        writer.Flush();
        return stream.ToArray();
    }

    // переписывает индексы LoadConst из локального пула выражения
    // в общий пул проекта; для этого идём по инструкциям с таблицей операндов
    private static byte[] RemapConstants(byte[] code, double[] localConstants,
        List<double> pool, Dictionary<double, int> poolIndex)
    {
        var output = new List<byte>(code.Length);
        int ip = 0;

        while (ip < code.Length)
        {
            var op = (OpCode)code[ip];
            output.Add(code[ip++]);

            switch (op)
            {
                case OpCode.LoadConst:
                    int localIndex = BitConverter.ToInt32(code, ip);
                    ip += 4;
                    output.AddRange(BitConverter.GetBytes(
                        AddToPool(localConstants[localIndex], pool, poolIndex)));
                    break;

                case OpCode.LoadTag:
                case OpCode.JumpIfFalse:
                case OpCode.Jump:
                    output.AddRange(code.AsSpan(ip, 4).ToArray());
                    ip += 4;
                    break;

                case OpCode.CallBuiltin:
                    output.AddRange(code.AsSpan(ip, 5).ToArray()); // 4 байта id + 1 байт арность
                    ip += 5;
                    break;

                // остальные инструкции операндов не имеют
            }
        }

        return output.ToArray();
    }

    private static int AddToPool(double value, List<double> pool, Dictionary<double, int> poolIndex)
    {
        if (poolIndex.TryGetValue(value, out int existing))
            return existing;
        pool.Add(value);
        poolIndex[value] = pool.Count - 1;
        return pool.Count - 1;
    }
}
