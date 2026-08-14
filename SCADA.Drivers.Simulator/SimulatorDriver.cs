using SCADA.Core.Devices;
using SCADA.Core.Tags;
using SCADA.Drivers.Abstractions;

namespace SCADA.Drivers.Simulator;

public sealed class SimulatorDriver : IDeviceDriver
{
    private readonly Func<DateTimeOffset> _clock;

    private TagDefinition[] _tags = [];
    private SimulatedSignal[] _signals = [];

    public SimulatorDriver(Func<DateTimeOffset>? clock = null)
        => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public string ProtocolName => "simulator";

    public Task ConnectAsync(DeviceDefinition device, IReadOnlyList<TagDefinition> tags, CancellationToken ct)
    {
        _tags = tags.ToArray();
        _signals = _tags.Select(t => SimulatedSignal.Parse(t.Address, t.DataType)).ToArray();
        return Task.CompletedTask;
    }

    public ValueTask<bool> PollAsync(Memory<TagValue> results, CancellationToken ct)
    {
        var now = _clock();
        double seconds = now.ToUnixTimeMilliseconds() / 1000.0;
        long timestamp = now.ToUnixTimeMilliseconds();

        var span = results.Span;
        for (int i = 0; i < _tags.Length; i++)
            span[i] = new TagValue(_signals[i].GetValue(seconds, _tags[i]), timestamp, Quality.Good);

        return ValueTask.FromResult(true);
    }

    public Task DisconnectAsync()
    {
        _tags = [];
        _signals = [];
        return Task.CompletedTask;
    }
}
