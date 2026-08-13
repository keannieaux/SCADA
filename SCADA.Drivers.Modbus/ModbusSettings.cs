namespace SCADA.Drivers.Modbus;

/// <summary>
/// Настройки Modbus-устройства из строки DeviceDefinition.Configuration.
/// Формат: "192.168.0.10:502;unit=1;timeout=1000;maxregs=60;maxgap=8".
/// Ключи необязательны, дефолты — по протоколу.
/// </summary>
public sealed record ModbusSettings(
    string Host,
    int Port,
    byte UnitId,
    int TimeoutMs,
    int MaxRegisters,
    int MaxGap)
{
    public static ModbusSettings Parse(string configuration)
    {
        var parts = configuration.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !parts[0].Contains(':'))
            throw new FormatException(
                $"Неверная конфигурация Modbus-устройства: '{configuration}'. " +
                $"Ожидается 'host:port[;unit=1][;timeout=1000][;maxregs=125][;maxgap=8]'");

        var endpoint = parts[0].Split(':');
        var host = endpoint[0];
        int port = int.Parse(endpoint[1]);

        byte unitId = 0;              // для прямого TCP-подключения стандарт — 0x00 (0xFF — broadcast)
        int timeoutMs = 1000;
        int maxRegisters = 125;       // лимит протокола; ПЛК может быть строже
        int maxGap = 8;

        foreach (var part in parts.Skip(1))
        {
            var kv = part.Split('=');
            if (kv.Length != 2)
                throw new FormatException($"Неверный параметр '{part}' в конфигурации устройства");

            switch (kv[0].Trim().ToLowerInvariant())
            {
                case "unit": unitId = byte.Parse(kv[1]); break;
                case "timeout": timeoutMs = int.Parse(kv[1]); break;
                case "maxregs": maxRegisters = int.Parse(kv[1]); break;
                case "maxgap": maxGap = int.Parse(kv[1]); break;
                default:
                    throw new FormatException($"Неизвестный параметр '{kv[0]}' в конфигурации устройства");
            }
        }

        return new ModbusSettings(host, port, unitId, timeoutMs, maxRegisters, maxGap);
    }
}
