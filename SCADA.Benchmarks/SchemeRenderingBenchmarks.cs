using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using SCADA.Core.Schemes;
using SCADA.Core.Tags;
using SCADA.Expressions;
using SCADA.Expressions.Compiler;
using SCADA.Graphics;
using SCADA.Runtime.Runtime;
using SCADA.Runtime.TagTable;
using SkiaSharp;

namespace SCADA.Benchmarks;

/// <summary>
/// Замер кадра мнемосхемы: пересчёт volatile-привязок (now()-вращение на
/// каждом элементе) + построение визуалов + отрисовка Skia на CPU-поверхности.
/// Headless — Avalonia не поднимается, рисуется тот же код, что и в
/// SchemeDrawOperation. Цель: ответить числом, вывозит ли канва 30 FPS
/// (33 мс на кадр) при 500/1000/2000 непрерывно анимируемых элементов (B0.6).
/// </summary>
[ShortRunJob]
[MemoryDiagnoser]
public class SchemeRenderingBenchmarks
{
    [Params(500, 1000, 2000)]
    public int ElementCount { get; set; }

    private static readonly string[] TagNames = ["Temperature", "Pressure", "PumpRunning", "Setpoint"];

    private SchemeElementRuntime[] _runtime = null!;
    private EvaluationContext _context = null!;
    private List<SchemeElementVisual> _visuals = null!;
    private SKSurface _surface = null!;
    private int _schemeWidth;
    private int _schemeHeight;

    [GlobalSetup]
    public void Setup()
    {
        var scheme = SyntheticSchemeGenerator.Generate(ElementCount, TagNames, volatileEvery: 1);
        var compiled = SchemeLoader.Compile(scheme, new FakeCatalog());
        _runtime = compiled.Select(e => new SchemeElementRuntime(e)).ToArray();

        var table = new TagTable(TagNames.Length);
        for (int i = 0; i < TagNames.Length; i++)
            table.Write(new TagId(i), new TagValue(50.0 + i, 1000, Quality.Good));
        _context = new EvaluationContext
        {
            Tags = new LocalRuntimeClient(table),
            NowUnixMs = 1_700_000_000_000
        };

        // viewport = вся схема целиком: худший случай, куллинг ничего не отсекает
        _schemeWidth = 25 * 20;
        _schemeHeight = (int)Math.Ceiling(ElementCount / 25.0) * 20;
        _surface = SKSurface.Create(new SKImageInfo(_schemeWidth, _schemeHeight));
        _visuals = new List<SchemeElementVisual>(ElementCount);

        // прогрев кэшей шрифтов Skia до замера
        Tick();
        Render();
    }

    [GlobalCleanup]
    public void Cleanup() => _surface.Dispose();

    [Benchmark(Description = "Tick: пересчёт всех volatile-привязок")]
    public int Tick()
    {
        foreach (var element in _runtime)
            SchemeCanvas.Recompute(element, _context);
        return _runtime.Length;
    }

    [Benchmark(Description = "Render: построение визуалов + отрисовка Skia")]
    public int Render()
    {
        _visuals.Clear();
        SchemeCanvas.BuildVisuals(_runtime, 0, 0, 1, _schemeWidth, _schemeHeight,
            blinkPhase: true, _visuals);
        SchemeDrawOperation.DrawItems(_surface.Canvas, _visuals, 0, 0, 1);
        return _visuals.Count;
    }

    private sealed class FakeCatalog : ITagCatalog
    {
        public bool TryGetIndex(string name, out int index)
        {
            index = Array.IndexOf(TagNames, name);
            return index >= 0;
        }
    }
}
