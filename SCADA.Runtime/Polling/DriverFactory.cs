using System.Collections.Concurrent;
using SCADA.Core.Devices;
using SCADA.Drivers.Abstractions;

namespace SCADA.Runtime.Polling;

/// <summary>
/// Реестр драйверов по имени протокола (ТЗ §7.2: новый протокол = новый проект
/// + регистрация, правок в остальном коде ноль). Встроенный драйвер
/// internal зарегистрирован по умолчанию; внешние драйверы (modbus-tcp,
/// будущие OPC UA/MQTT) и dev-драйвер simulator регистрирует composition root
/// приложения:
/// <code>DriverFactory.Register("modbus-tcp", () => new ModbusTcpDriver());</code>
/// </summary>
public static class DriverFactory
{
    private static readonly ConcurrentDictionary<string, Func<IDeviceDriver>> Factories = new()
    {
        ["internal"] = () => new InternalDriver()
    };

    /// <summary>Зарегистрировать драйвер под именем протокола (IDeviceDriver.ProtocolName).</summary>
    public static void Register(string protocolName, Func<IDeviceDriver> factory)
        => Factories[protocolName] = factory;

    public static IDeviceDriver Create(DeviceDefinition device)
        => Factories.TryGetValue(device.DriverName, out var factory)
            ? factory()
            : throw new InvalidOperationException(
                $"Неизвестный драйвер '{device.DriverName}' (устройство '{device.Name}')");
}
