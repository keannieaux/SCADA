using SCADA.Core.Tags;
using SCADA.Runtime.TagTable;

namespace SCADA.Expressions.Tests;

public class ExpressionVMTests
{
    // байткод собирается руками, но читается как программа
    private static Expression Program(double[] constants, params byte[] code)
        => new() { Code = code, Constants = constants };

    private static byte Op(OpCode op) => (byte)op;

    // операнд перехода — абсолютная позиция, 2 байта little-endian
    private static byte[] Addr(int position) => [(byte)position, (byte)(position >> 8)];

    private static EvaluationContext ContextFor(ITagTable table)
        => new() { Tags = table };

    // выражениям без LoadTag таблица всё равно нужна по сигнатуре — даём пустую
    private static EvaluationContext EmptyContext() => ContextFor(new TagTable(capacity: 1));

    [Fact]
    public void Evaluate_TwoConstantsAdded_ReturnsSum()
    {
        var expr = Program([2.0, 3.0],
            Op(OpCode.LoadConst), 0,
            Op(OpCode.LoadConst), 1,
            Op(OpCode.Add),
            Op(OpCode.Return));

        Assert.Equal(5.0, ExpressionVM.Evaluate(expr, EmptyContext()));
    }

    [Fact]
    public void Evaluate_SubtractionAndDivision_RespectsOrder()
    {
        // (10 - 4) / 2 = 3
        var expr = Program([10.0, 4.0, 2.0],
            Op(OpCode.LoadConst), 0,
            Op(OpCode.LoadConst), 1,
            Op(OpCode.Sub),
            Op(OpCode.LoadConst), 2,
            Op(OpCode.Div),
            Op(OpCode.Return));

        Assert.Equal(3.0, ExpressionVM.Evaluate(expr, EmptyContext()));
    }

    [Fact]
    public void Evaluate_NoReturnInstruction_Throws()
    {
        var expr = Program([2.0],
            Op(OpCode.LoadConst), 0);

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
            Op(OpCode.LoadTag), 0,
            Op(OpCode.LoadTag), 1,
            Op(OpCode.Add),
            Op(OpCode.Return));

        Assert.Equal(42.5, ExpressionVM.Evaluate(expr, ContextFor(table)));
    }

    [Fact]
    public void Evaluate_GreaterComparison_ReturnsOneOrZero()
    {
        var table = new TagTable(capacity: 1);
        table.Write(new TagId(0), new TagValue(100.0, 1000, Quality.Good));

        // Tag0 > 80
        var expr = Program([80.0],
            Op(OpCode.LoadTag), 0,
            Op(OpCode.LoadConst), 0,
            Op(OpCode.Greater),
            Op(OpCode.Return));

        Assert.Equal(1.0, ExpressionVM.Evaluate(expr, ContextFor(table)));

        table.Write(new TagId(0), new TagValue(50.0, 2000, Quality.Good));
        Assert.Equal(0.0, ExpressionVM.Evaluate(expr, ContextFor(table)));
    }

    [Fact]
    public void Evaluate_LogicalAnd_ShortCircuitsViaJumpIfFalse()
    {
        // Tag0 > 80 && Tag1 > 0
        // разметка кода: 0:LoadTag 2:LoadConst 4:Greater 5:JIF->16
        //                8:LoadTag 10:LoadConst 12:Greater 13:Jump->18 16:LoadConst 18:Return
        var expr = Program([80.0, 0.0],
            Op(OpCode.LoadTag), 0,
            Op(OpCode.LoadConst), 0,
            Op(OpCode.Greater),
            Op(OpCode.JumpIfFalse), Addr(16)[0], Addr(16)[1],
            Op(OpCode.LoadTag), 1,
            Op(OpCode.LoadConst), 1,
            Op(OpCode.Greater),
            Op(OpCode.Jump), Addr(18)[0], Addr(18)[1],
            Op(OpCode.LoadConst), 1,   // ложь: кладём 0.0
            Op(OpCode.Return));

        var table = new TagTable(capacity: 2);

        // оба условия истинны
        table.Write(new TagId(0), new TagValue(100.0, 1000, Quality.Good));
        table.Write(new TagId(1), new TagValue(1.0, 1000, Quality.Good));
        Assert.Equal(1.0, ExpressionVM.Evaluate(expr, ContextFor(table)));

        // первое ложно — второе даже не вычисляется (Tag1 оставим мусорным)
        table.Write(new TagId(0), new TagValue(50.0, 2000, Quality.Good));
        Assert.Equal(0.0, ExpressionVM.Evaluate(expr, ContextFor(table)));
    }

    [Fact]
    public void Evaluate_Ternary_PicksBranch()
    {
        // Tag0 > 80 ? 10 : 20
        // 0:LoadTag 2:LoadConst 4:Greater 5:JIF->13 8:LoadConst(10) 10:Jump->15 13:LoadConst(20) 15:Return
        var expr = Program([80.0, 10.0, 20.0],
            Op(OpCode.LoadTag), 0,
            Op(OpCode.LoadConst), 0,
            Op(OpCode.Greater),
            Op(OpCode.JumpIfFalse), Addr(13)[0], Addr(13)[1],
            Op(OpCode.LoadConst), 1,
            Op(OpCode.Jump), Addr(15)[0], Addr(15)[1],
            Op(OpCode.LoadConst), 2,
            Op(OpCode.Return));

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
            Op(OpCode.LoadTag), 0,
            Op(OpCode.LoadConst), 0,
            Op(OpCode.NotEqual),
            Op(OpCode.Not),
            Op(OpCode.Return));

        Assert.Equal(1.0, ExpressionVM.Evaluate(expr, ContextFor(table)));
    }

    [Fact]
    public void Evaluate_IsGood_ReflectsTagQuality()
    {
        var table = new TagTable(capacity: 1);

        // IsGood(Tag0)  — аргумент функции: ИНДЕКС тега
        var expr = Program([0.0],
            Op(OpCode.LoadConst), 0,                 // индекс тега 0
            Op(OpCode.CallBuiltin), BuiltinFunctions.IsGood, 1,
            Op(OpCode.Return));

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
            Op(OpCode.LoadConst), 0,                 // индекс тега 0
            Op(OpCode.LoadConst), 1,                 // значение по умолчанию -1
            Op(OpCode.CallBuiltin), BuiltinFunctions.ValueOr, 2,
            Op(OpCode.Return));

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
            Op(OpCode.LoadConst), 0,
            Op(OpCode.LoadConst), 1,
            Op(OpCode.LoadConst), 2,
            Op(OpCode.CallBuiltin), BuiltinFunctions.Clamp, 3,
            Op(OpCode.Return));

        Assert.Equal(100.0, ExpressionVM.Evaluate(expr, EmptyContext()));
    }

    [Fact]
    public void Evaluate_RealScadaExpression_IsGoodAndThreshold()
    {
        // IsGood(Tag0) && Tag0 > 80 — эталонное выражение из ТЗ §11.2
        // разметка: 0:LoadConst(индекс) 2:CallBuiltin 5:JIF->16 8:LoadTag
        //           10:LoadConst(80) 12:Greater 13:Jump->18 16:LoadConst(ложь) 18:Return
        var expr = Program([0.0, 80.0, 0.0],
            Op(OpCode.LoadConst), 0,                 // 0-1: индекс тега
            Op(OpCode.CallBuiltin), BuiltinFunctions.IsGood, 1,  // 2-4
            Op(OpCode.JumpIfFalse), Addr(16)[0], Addr(16)[1],    // 5-7
            Op(OpCode.LoadTag), 0,                   // 8-9
            Op(OpCode.LoadConst), 1,                 // 10-11: 80
            Op(OpCode.Greater),                      // 12
            Op(OpCode.Jump), Addr(18)[0], Addr(18)[1],           // 13-15
            Op(OpCode.LoadConst), 2,                 // 16-17: ложь
            Op(OpCode.Return));                      // 18

        var table = new TagTable(capacity: 1);
        table.Write(new TagId(0), new TagValue(100.0, 1000, Quality.Good));
        Assert.Equal(1.0, ExpressionVM.Evaluate(expr, ContextFor(table)));

        // связь оборвалась — выражение ложно, несмотря на «высокое» значение
        table.Write(new TagId(0), new TagValue(100.0, 2000, Quality.Bad));
        Assert.Equal(0.0, ExpressionVM.Evaluate(expr, ContextFor(table)));
    }
}
