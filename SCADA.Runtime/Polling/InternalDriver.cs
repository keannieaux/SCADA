using SCADA.Core.Devices;
using SCADA.Core.Tags;
using SCADA.Drivers.Abstractions;

namespace SCADA.Runtime.Polling;
public sealed class InternalDriver : IDeviceDriver
{
    // ConnectAsync: ничего — теги нам не нужны, мы их не опрашиваем
    // PollAsync: ничего — значения в TagTable пишутся снаружи (оператором, выражениями)
    // DisconnectAsync: ничего
    public string ProtocolName => "internal";

    public Task ConnectAsync(DeviceDefinition device, IReadOnlyList<TagDefinition> tags, CancellationToken ct)
        => Task.CompletedTask;

    public Task DisconnectAsync()
        => Task.CompletedTask;

    public ValueTask<bool> PollAsync(Memory<TagValue> results, CancellationToken ct)
        => ValueTask.FromResult(false); // данные пишутся снаружи, движку писать нечего


}
