using SCADA.Core.Tags;

namespace SCADA.Expressions;

/// <summary>
/// Реестр встроенных функций. Id функции = индекс в таблице, он попадает
/// в байткод пакета. ПРАВИЛО ФОРМАТА: id append-only — новые функции
/// только в конец, слоты удалённых не переиспользуются,
/// иначе старые пакеты начнут вызывать не те функции.
/// </summary>
public static class BuiltinFunctions
{
    public const int IsGood = 0;
    public const int ValueOr = 1;
    public const int Abs = 2;
    public const int Min = 3;
    public const int Max = 4;
    public const int Clamp = 5;

    private static readonly BuiltinFunction[] _table =
    [
        IsGoodImpl,
        ValueOrImpl,
        (args, _) => Math.Abs(args[0]),
        (args, _) => Math.Min(args[0], args[1]),
        (args, _) => Math.Max(args[0], args[1]),
        (args, _) => Math.Clamp(args[0], args[1], args[2]),
    ];

    public static BuiltinFunction Get(int id) => _table[id];

    private static double IsGoodImpl(ReadOnlySpan<double> args, EvaluationContext context)
    {
        // аргумент — ИНДЕКС тега (не значение): качество живёт в таблице
        var value = context.Tags.Read(new TagId((int)args[0]));
        return value.Quality == Quality.Good ? 1.0 : 0.0;
    }

    private static double ValueOrImpl(ReadOnlySpan<double> args, EvaluationContext context)
    {
        var value = context.Tags.Read(new TagId((int)args[0]));
        return value.Quality == Quality.Good ? value.Value : args[1];
    }
}
