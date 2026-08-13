using BenchmarkDotNet.Attributes;
using SCADA.Core.Tags;
using SCADA.Expressions;
using SCADA.Runtime.TagTable;

namespace SCADA.Benchmarks;

// Критерий приёмки M1 (ТЗ §11.8): выражение из ~10 инструкций —
// не более 100 нс, ноль аллокаций (колонка Allocated = 0 B).
[MemoryDiagnoser]
public class ExpressionBenchmarks
{
    private Expression _expr = null!;
    private EvaluationContext _context = null!;

    [GlobalSetup]
    public void Setup()
    {
        var table = new TagTable(capacity: 2);
        table.Write(new TagId(0), new TagValue(100.0, 1000, Quality.Good));
        table.Write(new TagId(1), new TagValue(1.0, 1000, Quality.Good));
        _context = new EvaluationContext { Tags = table };

        // эталон: IsGood(Tag0) && Tag0 > 80 — 11 инструкций,
        // типичное выражение динамизации мнемосхемы
        _expr = new Expression
        {
            Constants = [0.0, 80.0, 0.0],
            Code =
            [
                (byte)OpCode.LoadConst, 0,
                (byte)OpCode.CallBuiltin, BuiltinFunctions.IsGood, 1,
                (byte)OpCode.JumpIfFalse, 16, 0,
                (byte)OpCode.LoadTag, 0,
                (byte)OpCode.LoadConst, 1,
                (byte)OpCode.Greater,
                (byte)OpCode.Jump, 18, 0,
                (byte)OpCode.LoadConst, 2,
                (byte)OpCode.Return
            ]
        };
    }

    [Benchmark]
    public double Evaluate() => ExpressionVM.Evaluate(_expr, _context);
}
