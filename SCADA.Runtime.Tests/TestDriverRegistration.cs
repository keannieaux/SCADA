using System.Runtime.CompilerServices;
using SCADA.Drivers.Simulator;
using SCADA.Runtime.Polling;

namespace SCADA.Runtime.Tests;

internal static class TestDriverRegistration
{
    [ModuleInitializer]
    public static void Initialize()
    {
        // Симулятор — dev-драйвер, поэтому в Runtime не регистрируется по умолчанию.
        // Для тестов регистрируем его явно, чтобы тестовые конфиги с driverName: "simulator"
        // продолжали работать (ТЗ §7.2, §5.3).
        DriverFactory.Register("simulator", () => new SimulatorDriver());
    }
}
