using BenchmarkDotNet.Attributes;
using SkiaSharp;
using SkiaSharp.Skottie;
using Svg.Skia;

namespace SCADA.Benchmarks;

/// <summary>
/// Проверка концепции: во что обходится анимированный символ на схеме по
/// сравнению с текущим статическим SVG (SymbolCache → SKPicture → DrawPicture).
/// Три пути рисуют одну и ту же картинку (вращающаяся лопасть в кольце),
/// проверено попиксельно — габарит лопасти совпадает на всех временах:
///   - статический SKPicture — то, что SCADA.Graphics делает сегодня;
///   - SVG со SMIL через SKSvg.SetAnimationTime (Svg.Skia 5.x это умеет);
///   - Lottie через SkiaSharp.Skottie.
/// Время задаётся снаружи перемоткой, а не тикает само — это модель
/// «значение = f(теги, время)» из docs/scheme-rendering-benchmark.md §4.1:
/// второго источника времени не заводится, всё считается от now().
/// Стенд отдельный, ничего в SCADA.Graphics не меняется.
/// </summary>
[ShortRunJob]
[MemoryDiagnoser]
public class SymbolAnimationBenchmarks
{
    [Params(10, 50, 200)]
    public int SymbolCount { get; set; }

    private const int Columns = 20;
    private const float CellSize = 50f;
    private const float SymbolSize = 100f;

    private SKSurface _surface = null!;
    private SKPicture _staticPicture = null!;
    private SKSvg _animatedSvg = null!;
    private Animation _lottie = null!;
    private double[] _phases = null!;

    [GlobalSetup]
    public void Setup()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "Assets");

        int rows = (int)Math.Ceiling(SymbolCount / (double)Columns);
        _surface = SKSurface.Create(new SKImageInfo((int)(Columns * CellSize), (int)(rows * CellSize)));

        var staticSvg = new SKSvg();
        _staticPicture = staticSvg.Load(Path.Combine(dir, "symbol-static.svg"))
            ?? throw new InvalidOperationException("не загрузился symbol-static.svg");

        _animatedSvg = new SKSvg();
        _animatedSvg.Load(Path.Combine(dir, "symbol-animated.svg"));
        if (!_animatedSvg.HasAnimations)
            throw new InvalidOperationException("в symbol-animated.svg не нашлось анимаций");

        if (!Animation.TryCreate(Path.Combine(dir, "symbol-animated.json"), out var lottie))
            throw new InvalidOperationException("не разобрался symbol-animated.json");
        _lottie = lottie;

        // у каждого символа своя фаза: на реальной схеме насосы не синхронны,
        // и библиотека не может отдать один и тот же закэшированный кадр
        _phases = new double[SymbolCount];
        for (int i = 0; i < SymbolCount; i++)
            _phases[i] = i * 2.0 / SymbolCount;

        // прогрев: первый кадр строит внутренние кэши обеих библиотек
        StaticPicture();
        AnimatedSvg();
        Lottie();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _surface.Dispose();
        _animatedSvg.Dispose();
        _lottie.Dispose();
    }

    private static void PlaceCell(SKCanvas canvas, int index)
    {
        canvas.Translate(index % Columns * CellSize, index / Columns * CellSize);
        canvas.Scale(CellSize / SymbolSize);
    }

    [Benchmark(Baseline = true, Description = "Статический SKPicture (как сейчас)")]
    public int StaticPicture()
    {
        var canvas = _surface.Canvas;
        for (int i = 0; i < SymbolCount; i++)
        {
            canvas.Save();
            PlaceCell(canvas, i);
            canvas.DrawPicture(_staticPicture);
            canvas.Restore();
        }

        return SymbolCount;
    }

    [Benchmark(Description = "SVG со SMIL: SetAnimationTime + Draw")]
    public int AnimatedSvg()
    {
        var canvas = _surface.Canvas;
        for (int i = 0; i < SymbolCount; i++)
        {
            _animatedSvg.SetAnimationTime(TimeSpan.FromSeconds(_phases[i]));

            canvas.Save();
            PlaceCell(canvas, i);
            _animatedSvg.Draw(canvas);
            canvas.Restore();
        }

        return SymbolCount;
    }

    [Benchmark(Description = "Lottie: SeekFrameTime + Render")]
    public int Lottie()
    {
        var canvas = _surface.Canvas;
        var dst = new SKRect(0, 0, SymbolSize, SymbolSize);
        for (int i = 0; i < SymbolCount; i++)
        {
            _lottie.SeekFrameTime(_phases[i], null);

            canvas.Save();
            PlaceCell(canvas, i);
            _lottie.Render(canvas, dst);
            canvas.Restore();
        }

        return SymbolCount;
    }
}
