namespace SCADA.Drivers.Modbus;

/// <summary>
/// Декодирование сырых регистров в double. Modbus передаёт байты big-endian.
/// Точка расширения: порядок байт/слов у конкретных ПЛК (word swap и т.п.) —
/// добавится параметр политики, когда появится реальное железо.
/// </summary>
public static class RegisterDecoder
{
    /// <summary>data — блок ответа, byteOffset — смещение тега в БАЙТАХ (регистры × 2).</summary>
    public static double Decode(ReadOnlySpan<byte> data, int byteOffset, ModbusDataType type)
    {
        // Все ветки приводятся к double ЯВНО: без этого общий тип switch-выражения
        // выводится как float (из-за ветки Float32), и uint/int больше 2^24
        // теряют точность ещё до приведения результата.
        return type switch
        {
            ModbusDataType.UInt16 => (double)((data[byteOffset] << 8) | data[byteOffset + 1]),
            ModbusDataType.Int16 => (double)(short)((data[byteOffset] << 8) | data[byteOffset + 1]),
            ModbusDataType.UInt32 => (double)ReadUInt32BigEndian(data, byteOffset),
            ModbusDataType.Int32 => (double)(int)ReadUInt32BigEndian(data, byteOffset),
            ModbusDataType.Float32 => (double)BitConverter.Int32BitsToSingle(
                (int)ReadUInt32BigEndian(data, byteOffset)),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };
    }

    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> data, int offset)
        => ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16)
         | ((uint)data[offset + 2] << 8) | data[offset + 3];
}
