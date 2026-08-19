using SCADA.Core.Tags;

namespace SCADA.Expressions;

/// <summary>
/// Метаданные встроенной функции. Единый источник для ВМ (Impl),
/// компилятора (ArgCount, TagRefArgs) и редактора (Name — автодополнение §11.9).
/// TagRefArgs — индексы аргументов, которые являются ССЫЛКОЙ на тег:
/// для них эмитится индекс тега (LoadConst), а не значение (LoadTag),
/// потому что качество живёт в таблице, не на стеке.
/// </summary>
public sealed record BuiltinInfo(string Name, int Id, int ArgCount, int[] TagRefArgs, BuiltinFunction Impl);

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
    public const int Now = 6;

    private static readonly BuiltinInfo[] _table =
    [
        new(nameof(IsGood), IsGood, ArgCount: 1, TagRefArgs: [0], IsGoodImpl),
        new(nameof(ValueOr), ValueOr, ArgCount: 2, TagRefArgs: [0], ValueOrImpl),
        new(nameof(Abs), Abs, ArgCount: 1, TagRefArgs: [], (args, _) => Math.Abs(args[0])),
        new(nameof(Min), Min, ArgCount: 2, TagRefArgs: [], (args, _) => Math.Min(args[0], args[1])),
        new(nameof(Max), Max, ArgCount: 2, TagRefArgs: [], (args, _) => Math.Max(args[0], args[1])),
        new(nameof(Clamp), Clamp, ArgCount: 3, TagRefArgs: [], (args, _) => Math.Clamp(args[0], args[1], args[2])),
        // текущее время в СЕКУНДАХ (дробных): анимации вида now() * 90 % 360;
        // хранится в контексте в unix-мс — как TagValue.TimeStampUtc
        new(nameof(Now), Now, ArgCount: 0, TagRefArgs: [], (_, ctx) => ctx.NowUnixMs / 1000.0),
    ];

    // имена регистронезависимы: isgood(t) и IsGood(t) — одна функция
    private static readonly Dictionary<string, BuiltinInfo> _byName =
        _table.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

    public static BuiltinFunction Get(int id) => _table[id].Impl;

    public static bool TryGetByName(string name, out BuiltinInfo info)
        => _byName.TryGetValue(name, out info!);

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
