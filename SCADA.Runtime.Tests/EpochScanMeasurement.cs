using System.Diagnostics;
using System.Runtime.CompilerServices;
using Xunit.Abstractions;

namespace SCADA.Runtime.Tests;

public class EpochScanMeasurement
{
    private readonly ITestOutputHelper _output;

    public EpochScanMeasurement(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Scan_10000_Tags()
    {
        const int capacity = 10_000;
        var epochs = new long[capacity];
        var rng = new Random(42);
        for (int i = 0; i < capacity; i++)
            epochs[i] = rng.Next(1000);

        // Прогрев JIT: первые вызовы медленные, метод ещё компилируется
        long sink = 0;
        for (int w = 0; w < 1000; w++)
            sink += CountChanged(epochs, 500);

        const int iterations = 10_000;
        var sw = Stopwatch.StartNew();
        for (int it = 0; it < iterations; it++)
            sink += CountChanged(epochs, 500);
        sw.Stop();

        _output.WriteLine($"Среднее на проход: {sw.Elapsed.TotalMilliseconds / iterations * 1000:F2} мкс");
        _output.WriteLine($"Контрольное значение (чтобы JIT не выкинул цикл): {sink}");
    }

    // NoInlining — чтобы замерялся сам цикл, а не встраивание
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int CountChanged(long[] epochs, long seen)
    {
        int count = 0;
        for (int i = 0; i < epochs.Length; i++)
            if (epochs[i] > seen)
                count++;
        return count;
    }

    [Fact]
    public void Bus_Write_And_Flush()
    {
        const int writes = 100_000;
        var queue = new System.Collections.Concurrent.ConcurrentQueue<int>();

        // прогрев
        for (int i = 0; i < 10_000; i++) queue.Enqueue(i);
        while (queue.TryDequeue(out _)) { }

        // 1. Цена записи в шину (это делал бы КАЖДЫЙ драйвер при КАЖДОМ изменении)
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < writes; i++)
            queue.Enqueue(i);
        sw.Stop();
        _output.WriteLine($"Enqueue: {sw.Elapsed.TotalMilliseconds * 1000_000 / writes:F1} нс на запись");

        // 2. Цена сброса батча с дедупликацией (это делал бы тик раз в 100 мс)
        var seen = new HashSet<int>();
        sw.Restart();
        while (queue.TryDequeue(out int id))
            seen.Add(id);                    // дедупликация: 50 записей тега -> 1
        sw.Stop();
        _output.WriteLine($"Сброс {writes} элементов с дедупликацией: {sw.Elapsed.TotalMilliseconds:F2} мс");
        _output.WriteLine($"Уникальных тегов в батче: {seen.Count}");
    }
}
