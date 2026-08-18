using SCADA.Core.Devices;
using SCADA.Core.Tags;
using SCADA.Drivers.Abstractions;

namespace SCADA.Drivers.Simulator;

public sealed class SimulatorDriver : IWritableDeviceDriver
{
    private readonly Func<DateTimeOffset> _clock;

    private TagDefinition[] _tags = [];
    private SimulatedSignal[] _signals = [];
    // записанные значения: override держится до отключения, сигнал глушится
    private double?[] _overrides = [];

    public SimulatorDriver(Func<DateTimeOffset>? clock = null)
        => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public string ProtocolName => "simulator";

    public Task ConnectAsync(DeviceDefinition device, IReadOnlyList<TagDefinition> tags, CancellationToken ct)
    {
        _tags = tags.ToArray();
        _signals = _tags.Select(t => SimulatedSignal.Parse(t.Address, t.DataType)).ToArray();
        _overrides = new double?[_tags.Length];
        return Task.CompletedTask;
    }

    public ValueTask<bool> PollAsync(Memory<TagValue> results, CancellationToken ct)
    {
        var now = _clock();
        double seconds = now.ToUnixTimeMilliseconds() / 1000.0;
        long timestamp = now.ToUnixTimeMilliseconds();

        var span = results.Span;
        for (int i = 0; i < _tags.Length; i++)
            span[i] = new TagValue(_overrides[i] ?? _signals[i].GetValue(seconds, _tags[i]),
                timestamp, Quality.Good);

        return ValueTask.FromResult(true);
    }

    /// <summary>Запись (M7): значение подменяет сигнал до переподключения —
    /// стенд для проверки цепочки записи без железа. Batch — циклом,
    /// драйвер простой, оптимизировать нечего.</summary>
    public Task<TagWriteResult[]> WriteAsync(IReadOnlyList<DriverWriteItem> items, CancellationToken ct)
    {
        var results = new TagWriteResult[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            int index = Array.FindIndex(_tags, t => t.Id == items[i].Tag.Id);
            if (index < 0)
                results[i] = new TagWriteResult(TagWriteStatus.Failed, "тег не принадлежит устройству");
            else
            {
                _overrides[index] = items[i].RawValue;
                results[i] = TagWriteResult.Success;
            }
        }
        return Task.FromResult(results);
    }

    public Task DisconnectAsync()
    {
        _tags = [];
        _signals = [];
        _overrides = [];
        return Task.CompletedTask;
    }
}
