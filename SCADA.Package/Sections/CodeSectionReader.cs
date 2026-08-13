using SCADA.Expressions;

namespace SCADA.Package.Sections;

/// <summary>
/// Выражение, загруженное из пула: байткод (с ГЛОБАЛЬНЫМИ индексами
/// констант) + список тегов для пересчёта по эпохам (§11.7).
/// </summary>
public sealed record LoadedExpression(byte[] Code, int[] TagIndices);

/// <summary>
/// Читатель секции code.bin. Зеркален CodeSectionWriter.
/// Все выражения разделяют один пул констант — Constants у них общий.
/// </summary>
public static class CodeSectionReader
{
    public static CodePool Read(byte[] section)
    {
        using var stream = new MemoryStream(section);
        using var reader = new BinaryReader(stream);

        int constantCount = reader.ReadInt32();
        var constants = new double[constantCount];
        for (int i = 0; i < constantCount; i++)
            constants[i] = reader.ReadDouble();

        int expressionCount = reader.ReadInt32();
        var table = new (int Offset, int Length, int[] Tags)[expressionCount];
        for (int i = 0; i < expressionCount; i++)
        {
            int offset = reader.ReadInt32();
            int length = reader.ReadInt32();
            int tagCount = reader.ReadInt32();
            var tags = new int[tagCount];
            for (int j = 0; j < tagCount; j++)
                tags[j] = reader.ReadInt32();
            table[i] = (offset, length, tags);
        }

        int blobLength = reader.ReadInt32();
        var blob = reader.ReadBytes(blobLength);

        var expressions = new LoadedExpression[expressionCount];
        for (int i = 0; i < expressionCount; i++)
        {
            var (offset, length, tags) = table[i];
            expressions[i] = new LoadedExpression(blob[offset..(offset + length)], tags);
        }

        return new CodePool(constants, expressions);
    }
}

/// <summary>
/// Загруженный пул байткода: общие константы + выражения.
/// ToExpression собирает рантайм-представление для ВМ.
/// </summary>
public sealed record CodePool(double[] Constants, LoadedExpression[] Expressions)
{
    public Expression ToExpression(int index)
    {
        var loaded = Expressions[index];
        return new Expression { Code = loaded.Code, Constants = Constants };
    }
}
