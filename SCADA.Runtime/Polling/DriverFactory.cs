using SCADA.Core.Devices;
using SCADA.Drivers.Abstractions;
using SCADA.Drivers.Simulator;

namespace SCADA.Runtime.Polling;

public static class DriverFactory
{
    public static IDeviceDriver Create(DeviceDefinition device) =>device.DriverName switch {
        "simulator" => new SimulatorDriver(),
        "internal" => new InternalDriver(),
        _ => throw new InvalidOperationException($"Неизвестный драйвер '{device.DriverName}' (устройство '{device.Name}')")
    };
}
