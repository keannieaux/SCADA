using SCADA.Core.Tags;
using SCADA.Runtime.TagTable;

namespace SCADA.Expressions.Tests;

public class ExpressionVMTests
{
    // байткод собирается руками, но читается как программа
    private static Expression Program(double[] constants, byte[] code)
        => new() { Code = code, Constants = constants };

    private static byte Op(OpCode op) => (byte)op;

    // все индексы и адреса — 4-байтные int, little-endian
    private static byte[] I4(int value) => BitConverter.GetBytes(value);

    private static EvaluationContext ContextFor(ITagTable table)
        => new() { Tags = table };

    // выражениям без LoadTag таблица всё равно нужна по сигнатуре — даём пустую
    private static EvaluationContext EmptyContext() => ContextFor(new TagTable(capacity: 1));

    [Fact]
    public void Evaluate_TwoConstantsAdded_ReturnsSum()
    {
        var expr = Program([2.0, 3.0],
            [Op(OpCode.LoadConst), ..I4(0),
             Op(OpCode.LoadConst), ..I4(1),
             Op(OpCode.Add),
             Op(OpCode.Return)]);

        Assert.Equal(5.0, ExpressionVM.Evaluate(expr, EmptyContext()));
    }

    [Fact]
    public void Evaluate_SubtractionAndDivision_RespectsOrder()
    {
        // (10 - 4) / 2 = 3
        var expr = Program([10.0, 4.0, 2.0],
            [Op(OpCode.LoadConst), ..I4(0),
             Op(OpCode.LoadConst), ..I4(1),
             Op(OpCode.Sub),
             Op(OpCode.LoadConst), ..I4(2),
             Op(OpCode.Div),
             Op(OpCode.Return)]);

        Assert.Equal(3.0, ExpressionVM.Evaluate(expr, EmptyContext()));
    }

    [Fact]
    public void Evaluate_NoReturnInstruction_Throws()
    {
        var expr = Program([2.0],
            [Op(OpCode.LoadConst), ..I4(0)]);

        Assert.Throws<InvalidOperationException>(() => ExpressionVM.Evaluate(expr, EmptyContext()));
    }

    [Fact]
    public void Evaluate_TwoTagsAdded_ReturnsSum()
    {
        var table = new TagTable(capacity: 2);
        table.Write(new TagId(0), new TagValue(10.0, 1000, Quality.Good));
        table.Write(new TagId(1), new TagValue(32.5, 1000, Quality.Good));

        // Tag0 + Tag1
        var expr = Program([],
            [Op(OpCode.LoadTag), ..I4(0),
             Op(OpCode.LoadTag), ..I4(1),
             Op(OpCode.Add),
             Op(OpCode.Return)]);

        Assert.Equal(42.5, ExpressionVM.Evaluate(expr, ContextFor(table)));
    }

    [Fact]
    public void Evaluate_GreaterComparison_ReturnsOneOrZero()
    {
        var table = new TagTable(capacity: 1);
        table.Write(new TagId(0), new TagValue(100.0, 1000, Quality.Good));

        // Tag0 > 80
        var expr = Program([80.0],
            [Op(OpCode.LoadTag), ..I4(0),
             Op(OpCode.LoadConst), ..I4(0),
             Op(OpCode.Greater),
             Op(OpCode.Return)]);

        Assert.Equal(1.0, ExpressionVM.Evaluate(expr, ContextFor(table)));

        table.Write(new TagId(0), new TagValue(50.0, 2000, Quality.Good));
        Assert.Equal(0.0, ExpressionVM.Evaluate(expr, ContextFor(table)));
    }

    [Fact]
    public void Evaluate_LogicalAnd_ShortCircuitsViaJumpIfFalse()
    {
        // Tag0 > 80 && Tag1 > 0
        // разметка (операнды по 4 байта): 0:LoadTag 5:LoadConst 10:Greater
        //   11:JIF->32  16:LoadTag 21:LoadConst 26:Greater 27:Jump->35  32:LoadConst 37? — см. код
        var expr = Program([80.0, 0.0],
            [Op(OpCode.LoadTag), ..I4(0),        // 0-4
             Op(OpCode.LoadConst), ..I4(0),      // 5-9
             Op(OpCode.Greater),                 // 10
             Op(OpCode.JumpIfFalse), ..I4(32),   // 11-15
             Op(OpCode.LoadTag), ..I4(1),        // 16-20
             Op(OpCode.LoadConst), ..I4(1),      // 21-25
             Op(OpCode.Greater),                 // 26
             Op(OpCode.Jump), ..I4(37),          // 27-31
             Op(OpCode.LoadConst), ..I4(1),      // 32-36: ложь
             Op(OpCode.Return)]);                // 37

        var table = new TagTable(capacity: 2);

        // оба условия истинны
        table.Write(new TagId(0), new TagValue(100.0, 1000, Quality.Good));
        table.Write(new TagId(1), new TagValue(1.0, 1000, Quality.Good));
        Assert.Equal(1.0, ExpressionVM.Evaluate(expr, ContextFor(table)));

        // первое ложно — второе даже не вычисляется
        table.Write(new TagId(0), new TagValue(50.0, 2000, Quality.Good));
        Assert.Equal(0.0, ExpressionVM.Evaluate(expr, ContextFor(table)));
    }

    [Fact]
    public void Evaluate_Ternary_PicksBranch()
    {
        // Tag0 > 80 ? 10 : 20
        // 0:LoadTag 5:LoadConst 10:Greater 11:JIF->26 16:LoadConst(10) 21:Jump->31 26:LoadConst(20) 31:Return
        var expr = Program([80.0, 10.0, 20.0],
            [Op(OpCode.LoadTag), ..I4(0),        // 0-4
             Op(OpCode.LoadConst), ..I4(0),      // 5-9
             Op(OpCode.Greater),                 // 10
             Op(OpCode.JumpIfFalse), ..I4(26),   // 11-15
             Op(OpCode.LoadConst), ..I4(1),      // 16-20: 10
             Op(OpCode.Jump), ..I4(31),          // 21-25
             Op(OpCode.LoadConst), ..I4(2),      // 26-30: 20
             Op(OpCode.Return)]);                // 31

        var table = new TagTable(capacity: 1);
        table.Write(new TagId(0), new TagValue(100.0, 1000, Quality.Good));
        Assert.Equal(10.0, ExpressionVM.Evaluate(expr, ContextFor(table)));

        table.Write(new TagId(0), new TagValue(50.0, 2000, Quality.Good));
        Assert.Equal(20.0, ExpressionVM.Evaluate(expr, ContextFor(table)));
    }

    [Fact]
    public void Evaluate_NotEqualAndNot_Work()
    {
        var table = new TagTable(capacity: 1);
        table.Write(new TagId(0), new TagValue(5.0, 1000, Quality.Good));

        // !(Tag0 != 5)  →  !(0)  →  1
        var expr = Program([5.0],
            [Op(OpCode.LoadTag), ..I4(0),
             Op(OpCode.LoadConst), ..I4(0),
             Op(OpCode.NotEqual),
             Op(OpCode.Not),
             Op(OpCode.Return)]);

        Assert.Equal(1.0, ExpressionVM.Evaluate(expr, ContextFor(table)));
    }

    [Fact]
    public void Evaluate_IsGood_ReflectsTagQuality()
    {
        var table = new TagTable(capacity: 1);

        // IsGood(Tag0)  — аргумент функции: ИНДЕКС тега
        var expr = Program([0.0],
            [Op(OpCode.LoadConst), ..I4(0),           // индекс тега 0
             Op(OpCode.CallBuiltin), ..I4(BuiltinFunctions.IsGood), 1,
             Op(OpCode.Return)]);

        table.Write(new TagId(0), new TagValue(42.0, 1000, Quality.Good));
        Assert.Equal(1.0, ExpressionVM.Evaluate(expr, ContextFor(table)));

        table.Write(new TagId(0), new TagValue(0.0, 2000, Quality.Bad));
        Assert.Equal(0.0, ExpressionVM.Evaluate(expr, ContextFor(table)));
    }

    [Fact]
    public void Evaluate_ValueOr_ReturnsDefaultOnBadQuality()
    {
        var table = new TagTable(capacity: 1);

        // ValueOr(Tag0, -1)
        var expr = Program([0.0, -1.0],
            [Op(OpCode.LoadConst), ..I4(0),           // индекс тега 0
             Op(OpCode.LoadConst), ..I4(1),           // значение по умолчанию -1
             Op(OpCode.CallBuiltin), ..I4(BuiltinFunctions.ValueOr), 2,
             Op(OpCode.Return)]);

        table.Write(new TagId(0), new TagValue(42.0, 1000, Quality.Good));
        Assert.Equal(42.0, ExpressionVM.Evaluate(expr, ContextFor(table)));

        table.Write(new TagId(0), new TagValue(0.0, 2000, Quality.Bad));
        Assert.Equal(-1.0, ExpressionVM.Evaluate(expr, ContextFor(table)));
    }

    [Fact]
    public void Evaluate_Clamp_LimitsValue()
    {
        // Clamp(150, 0, 100) = 100
        var expr = Program([150.0, 0.0, 100.0],
            [Op(OpCode.LoadConst), ..I4(0),
             Op(OpCode.LoadConst), ..I4(1),
             Op(OpCode.LoadConst), ..I4(2),
             Op(OpCode.CallBuiltin), ..I4(BuiltinFunctions.Clamp), 3,
             Op(OpCode.Return)]);

        Assert.Equal(100.0, ExpressionVM.Evaluate(expr, EmptyContext()));
    }

    [Fact]
    public void Evaluate_Now_ReturnsContextTimeInSeconds()
    {
        // now() — время из контекста в секундах (B0.4: анимации now() * 90 % 360)
        var expr = Program([],
            [Op(OpCode.CallBuiltin), ..I4(BuiltinFunctions.Now), 0,
             Op(OpCode.Return)]);

        var context = new EvaluationContext
        {
            Tags = new TagTable(capacity: 1),
            NowUnixMs = 1_700_000_000_000L + 2500
        };
        Assert.Equal(1_700_000_002.5, ExpressionVM.Evaluate(expr, context));

        // контекст без времени — детерминированный ноль, а не реальные часы
        Assert.Equal(0.0, ExpressionVM.Evaluate(expr, EmptyContext()));
    }

    [Fact]
    public void Evaluate_RealScadaExpression_IsGoodAndThreshold()
    {
        // IsGood(Tag0) && Tag0 > 80 — эталонное выражение из ТЗ §11.2
        // 0:LoadConst(индекс) 5:CallBuiltin(4+1 байт операндов) 11:JIF->32
        // 16:LoadTag 21:LoadConst(80) 26:Greater 27:Jump->37 32:LoadConst(ложь) 37:Return
        var expr = Program([0.0, 80.0, 0.0],
            [Op(OpCode.LoadConst), ..I4(0),                        // 0-4
             Op(OpCode.CallBuiltin), ..I4(BuiltinFunctions.IsGood), 1,  // 5-10
             Op(OpCode.JumpIfFalse), ..I4(32),                     // 11-15
             Op(OpCode.LoadTag), ..I4(0),                          // 16-20
             Op(OpCode.LoadConst), ..I4(1),                        // 21-25: 80
             Op(OpCode.Greater),                                   // 26
             Op(OpCode.Jump), ..I4(37),                            // 27-31
             Op(OpCode.LoadConst), ..I4(2),                        // 32-36: ложь
             Op(OpCode.Return)]);                                  // 37

        var table = new TagTable(capacity: 1);
        table.Write(new TagId(0), new TagValue(100.0, 1000, Quality.Good));
        Assert.Equal(1.0, ExpressionVM.Evaluate(expr, ContextFor(table)));

        // связь оборвалась — выражение ложно, несмотря на «высокое» значение
        table.Write(new TagId(0), new TagValue(100.0, 2000, Quality.Bad));
        Assert.Equal(0.0, ExpressionVM.Evaluate(expr, ContextFor(table)));
    }

    [Fact]
    public void DefaultExpression_ThrowsClearError()
    {
        // Expression — структура (ради нулевых аллокаций в горячем цикле),
        // а default обходит required: без явной проверки вместо внятной
        // ошибки был бы NullReferenceException из середины цикла ВМ
        var ex = Assert.Throws<InvalidOperationException>(
            () => ExpressionVM.Evaluate(default, EmptyContext()));

        Assert.Contains("не инициализировано", ex.Message);
    }
}
