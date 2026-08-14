namespace SCADA.Drivers.Modbus;

/// <summary>Четыре таблицы данных Modbus.</summary>
public enum ModbusTable : byte
{
    Coil,            // биты, чтение/запись (FC01)
    DiscreteInput,   // биты, только чтение (FC02)
    InputRegister,   // регистры 16 бит, только чтение (FC04)
    HoldingRegister  // регистры 16 бит, чтение/запись (FC03)
}

/// <summary>Тип данных в регистрах. 32-битные занимают два регистра.</summary>
public enum ModbusDataType : byte
{
    UInt16,
    Int16,
    UInt32,
    Int32,
    Float32
    // Точка расширения: порядок байт/регистров (word swap) — у разных ПЛК
    // по-разному, добавим суффикс адреса, когда появится реальное железо.
}

/// <summary>
/// Адрес тега в устройстве Modbus: таблица + смещение + тип.
/// Грамматика: "таблица:смещение[:тип]", например "hr:100:f32", "coil:3".
/// </summary>
public readonly record struct ModbusAddress(
    ModbusTable Table, int Offset, ModbusDataType DataType)
{
    /// <summary>Сколько 16-битных регистров занимает значение (для битовых таблиц — 0).</summary>
    public int RegisterCount =>
        Table is ModbusTable.Coil or ModbusTable.DiscreteInput
            ? 0
            : DataType is ModbusDataType.UInt16 or ModbusDataType.Int16 ? 1 : 2;

    public static ModbusAddress Parse(string address)
    {
        var parts = address.Split(':');
        if (parts.Length is < 2 or > 3)
            throw new FormatException(
                $"Неверный Modbus-адрес: '{address}'. Ожидается 'таблица:смещение[:тип]', например 'hr:100:f32'");

        var table = parts[0].ToLowerInvariant() switch
        {
            "coil" => ModbusTable.Coil,
            "di" => ModbusTable.DiscreteInput,
            "ir" => ModbusTable.InputRegister,
            "hr" => ModbusTable.HoldingRegister,
            _ => throw new FormatException(
                $"Неизвестная таблица в адресе '{address}'. Допустимы: coil, di, ir, hr")
        };

        if (!int.TryParse(parts[1], out int offset) || offset < 0)
            throw new FormatException($"Неверное смещение в адресе '{address}'");

        var dataType = ModbusDataType.UInt16;
        if (parts.Length == 3)
        {
            dataType = parts[2].ToLowerInvariant() switch
            {
                "u16" => ModbusDataType.UInt16,
                "i16" => ModbusDataType.Int16,
                "u32" => ModbusDataType.UInt32,
                "i32" => ModbusDataType.Int32,
                "f32" => ModbusDataType.Float32,
                _ => throw new FormatException(
                    $"Неизвестный тип в адресе '{address}'. Допустимы: u16, i16, u32, i32, f32")
            };
        }

        // тип указывать можно только для регистровых таблиц
        if (parts.Length == 3 && table is ModbusTable.Coil or ModbusTable.DiscreteInput)
            throw new FormatException(
                $"Тип данных не применим к битовой таблице в адресе '{address}'");

        return new ModbusAddress(table, offset, dataType);
    }
}
