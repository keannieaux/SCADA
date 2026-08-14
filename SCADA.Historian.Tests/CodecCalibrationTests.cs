using SCADA.Core.Tags;
using Xunit.Abstractions;

namespace SCADA.Historian.Tests;

/// <summary>
/// Калибровка констант калькулятора объёма (ТЗ §4.3) по фактическим замерам.
/// </summary>
/// <remarks>
/// Калькулятор оперирует байтами на отсчёт для каждого кодека. Эти числа —
/// не аксиомы, а результат замера, и при изменении кодеков они разъезжаются.
/// Тест меряет их заново на модельных сигналах и падает, если константа
/// разошлась с фактом: иначе оценка объёма тихо начала бы врать, а вслед за
/// ней и требование к диску, выставленное заказчику.
///
/// Какой кодек окажется основным на объекте, заранее неизвестно: он выбирается
/// на каждый блок по тому, ложатся ли значения на решётку ScaleFactor (§7.2).
/// Поэтому меряются оба пути.
/// </remarks>
public class CodecCalibrationTests(ITestOutputHelper output)
{
    private const int Points = 4096;
    private const long BaseTime = 1_700_000_000_000L;

    /// <summary>Константы, зашитые в ArchiveVolumeCalculator.</summary>
    private const double CalculatorAnalogBytes = 0.55;
    private const double CalculatorFloatBytes = 8.4;
    private const double CalculatorFloat32Bytes = 1.4;
    private const double CalculatorDiscreteBytes = 0.5;

    private static double BytesPerPoint(ArchivePoint[] points, TagDataType dataType,
        double scale, double offset = 0.0)
    {
        byte[] block = BlockBuilder.Build(points, dataType, LoggingMode.Periodic, scale, offset);
        return (double)block.Length / points.Length;
    }

    private static ValueCodec CodecOf(ArchivePoint[] points, TagDataType dataType, double scale)
    {
        byte[] block = BlockBuilder.Build(points, dataType, LoggingMode.Periodic, scale, 0.0);
        BlockReader.TryReadHeader(block, out var header);
        return header.Codec;
    }

    // --- целочисленная решётка: значение из регистра ПЛК через ScaleFactor ---

    [Fact]
    public void ScaledInt_StableAnalog_MatchesCalculatorConstant()
    {
        var points = LatticeSeries(changeProbabilityPercent: 20);

        Assert.Equal(ValueCodec.ScaledInt, CodecOf(points, TagDataType.Analog, 0.01));
        double measured = BytesPerPoint(points, TagDataType.Analog, 0.01);

        output.WriteLine($"ScaledInt, 20 % изменений: {measured:F2} байт/отсчёт");

        Assert.True(Math.Abs(measured - CalculatorAnalogBytes) < 0.25,
            $"замер {measured:F2} разошёлся с константой калькулятора {CalculatorAnalogBytes}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(50)]
    [InlineData(100)]
    public void ScaledInt_CostGrowsWithActivity(int changePercent)
    {
        var points = LatticeSeries(changePercent);
        double measured = BytesPerPoint(points, TagDataType.Analog, 0.01);

        output.WriteLine($"ScaledInt, {changePercent,3} % изменений: {measured:F2} байт/отсчёт");

        // Даже при изменении каждого отсчёта решётка держит цену в пределах
        // пары байт: разность разностей на шаге ±1 стоит 9 бит.
        Assert.True(measured < 2.0);
    }

    // --- XOR по double: значения без решётки ---

    [Fact]
    public void GorillaXor_ComputedDoubles_MatchesCalculatorConstant()
    {
        // Вычисляемый тег: результат арифметики с плавающей точкой, младшие
        // биты мантиссы непредсказуемы. Худший случай для XOR.
        var points = ComputedSeries();

        Assert.Equal(ValueCodec.GorillaXor, CodecOf(points, TagDataType.Analog, 1.0));
        double measured = BytesPerPoint(points, TagDataType.Analog, 1.0);

        output.WriteLine($"GorillaXor, вычисляемый double: {measured:F2} байт/отсчёт");

        // Раздувание относительно 8 байт несжатого значения — не дефект
        // реализации, а свойство схемы XOR на данных без общих бит.
        Assert.True(measured > 8.0,
            "ожидалось, что XOR на вычисляемых double не сжимает, а раздувает");

        Assert.True(Math.Abs(measured - CalculatorFloatBytes) < 1.0,
            $"замер {measured:F2} разошёлся с константой калькулятора {CalculatorFloatBytes} — " +
            "оценка объёма и требование к диску начнут врать");
    }

    [Fact]
    public void GorillaXor_Float32FromPlc_IsCheaperThanComputed()
    {
        // float32, расширенный до double: 29 младших бит мантиссы нулевые,
        // и XOR это использует. Типичный случай, когда ПЛК отдаёт REAL.
        var points = Float32Series();

        double measured = BytesPerPoint(points, TagDataType.Analog, 1.0);
        double computed = BytesPerPoint(ComputedSeries(), TagDataType.Analog, 1.0);

        output.WriteLine($"GorillaXor, float32 из ПЛК:   {measured:F2} байт/отсчёт");

        Assert.True(measured < computed,
            "float32 обязан сжиматься лучше вычисляемого double: у него " +
            "29 младших бит мантиссы всегда нулевые");
    }

    [Fact]
    public void Quantization_RescuesComputedDoubles()
    {
        // §7.3: объявленная точность создаёт решётку искусственно, и кодек
        // с раздувающего XOR переключается на разность разностей по целым.
        // Формула квантования совпадает с проверкой решётки в BlockBuilder,
        // поэтому переключение гарантировано, а не вероятно.
        const double quantum = 0.01;

        var raw = ComputedSeries();
        var quantized = new ArchivePoint[raw.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            quantized[i] = raw[i] with
            {
                Value = Math.Round(raw[i].Value / quantum) * quantum
            };
        }

        Assert.Equal(ValueCodec.GorillaXor, CodecOf(raw, TagDataType.Analog, 1.0));
        Assert.Equal(ValueCodec.ScaledInt, CodecOf(quantized, TagDataType.Analog, quantum));

        double before = BytesPerPoint(raw, TagDataType.Analog, 1.0);
        double after = BytesPerPoint(quantized, TagDataType.Analog, quantum);

        output.WriteLine($"вычисляемый double без квантования: {before:F2} байт/отсчёт");
        output.WriteLine($"он же с точностью 2 знака:          {after:F2} байт/отсчёт");
        output.WriteLine($"выигрыш: {before / after:F1}x");

        Assert.True(after < before / 4,
            $"квантование дало {before / after:F1}x — ожидался кратный выигрыш");
    }

    [Fact]
    public void GorillaXor_StableFloat_IsNearlyFree()
    {
        // Стабильный сигнал без решётки: XOR равен нулю, отсчёт стоит 1 бит.
        var points = new ArchivePoint[Points];
        for (int i = 0; i < Points; i++)
            points[i] = new ArchivePoint(BaseTime + i * 1000L, Math.PI, Quality.Good);

        double measured = BytesPerPoint(points, TagDataType.Analog, 1.0);
        output.WriteLine($"GorillaXor, стабильный double: {measured:F2} байт/отсчёт");

        Assert.True(measured < 0.3);
    }

    // --- дискретные ---

    [Fact]
    public void Discrete_MatchesCalculatorConstant()
    {
        var points = new ArchivePoint[Points];
        var random = new Random(7);
        double state = 0;

        for (int i = 0; i < Points; i++)
        {
            if (random.Next(40) == 0)
                state = state == 0 ? 1 : 0;

            points[i] = new ArchivePoint(BaseTime + i * 1000L, state, Quality.Good);
        }

        double measured = BytesPerPoint(points, TagDataType.Discrete, 1.0);
        output.WriteLine($"Discrete:                      {measured:F2} байт/отсчёт");

        Assert.True(Math.Abs(measured - CalculatorDiscreteBytes) < 0.3,
            $"замер {measured:F2} разошёлся с константой калькулятора {CalculatorDiscreteBytes}");
    }

    // --- сводка для разговора о требованиях к диску ---

    [Fact]
    public void Summary_AllArchetypes()
    {
        (string Name, double Bytes)[] measured =
        [
            ("стабильный на решётке (0 % изменений)", BytesPerPoint(LatticeSeries(0), TagDataType.Analog, 0.01)),
            ("типовой на решётке (20 %)", BytesPerPoint(LatticeSeries(20), TagDataType.Analog, 0.01)),
            ("активный на решётке (100 %)", BytesPerPoint(LatticeSeries(100), TagDataType.Analog, 0.01)),
            ("линейный разгон на решётке", BytesPerPoint(RampSeries(), TagDataType.Analog, 0.01)),
            ("float32 из ПЛК", BytesPerPoint(Float32Series(), TagDataType.Analog, 1.0)),
            ("вычисляемый double", BytesPerPoint(ComputedSeries(), TagDataType.Analog, 1.0)),
            ("дискретный", BytesPerPoint(DiscreteSeries(), TagDataType.Discrete, 1.0))
        ];

        output.WriteLine("Байт на отсчёт по архетипам (блок 4096, интервал 1 с):");
        foreach (var (name, bytes) in measured)
            output.WriteLine($"  {name,-40} {bytes,5:F2}   → {bytes * 86400 * 365 / 1024 / 1024,6:F1} МБ/тег/год");

        double worst = measured.Max(m => m.Bytes);
        output.WriteLine($"Худший архетип: {worst:F2} байт/отсчёт");
        output.WriteLine($"На 1500 тегов @1 Гц худшим случаем: " +
            $"{worst * 86400 * 365 * 1500 / 1e9:F0} ГБ/год");

        Assert.True(worst > 0);
    }

    // --- генераторы сигналов ---

    /// <summary>Значение из целого регистра через ScaleFactor: решётка есть.</summary>
    private static ArchivePoint[] LatticeSeries(int changeProbabilityPercent)
    {
        var points = new ArchivePoint[Points];
        var random = new Random(20260814);
        long units = 7531;

        for (int i = 0; i < Points; i++)
        {
            if (random.Next(100) < changeProbabilityPercent)
                units += random.Next(-1, 2);

            points[i] = new ArchivePoint(BaseTime + i * 1000L, units * 0.01, Quality.Good);
        }

        return points;
    }

    /// <summary>Линейный рост: разность разностей вырождается в ноль.</summary>
    private static ArchivePoint[] RampSeries()
    {
        var points = new ArchivePoint[Points];
        for (int i = 0; i < Points; i++)
            points[i] = new ArchivePoint(BaseTime + i * 1000L, (7000 + i * 3) * 0.01, Quality.Good);

        return points;
    }

    /// <summary>float32 из ПЛК, расширенный до double.</summary>
    private static ArchivePoint[] Float32Series()
    {
        var points = new ArchivePoint[Points];
        var random = new Random(31337);
        float value = 75.31f;

        for (int i = 0; i < Points; i++)
        {
            if (random.Next(100) < 20)
                value += (random.Next(3) - 1) * 0.01f;

            points[i] = new ArchivePoint(BaseTime + i * 1000L, value, Quality.Good);
        }

        return points;
    }

    /// <summary>Вычисляемый тег: полная мантисса double меняется каждый отсчёт.</summary>
    private static ArchivePoint[] ComputedSeries()
    {
        var points = new ArchivePoint[Points];
        for (int i = 0; i < Points; i++)
        {
            double value = 75.0 + Math.Sin(i * 0.01) * 3.7 + Math.Cos(i * 0.003) * 1.13;
            points[i] = new ArchivePoint(BaseTime + i * 1000L, value, Quality.Good);
        }

        return points;
    }

    private static ArchivePoint[] DiscreteSeries()
    {
        var points = new ArchivePoint[Points];
        var random = new Random(7);
        double state = 0;

        for (int i = 0; i < Points; i++)
        {
            if (random.Next(40) == 0)
                state = state == 0 ? 1 : 0;

            points[i] = new ArchivePoint(BaseTime + i * 1000L, state, Quality.Good);
        }

        return points;
    }
}
