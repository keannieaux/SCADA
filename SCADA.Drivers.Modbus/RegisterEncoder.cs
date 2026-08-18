namespace SCADA.Drivers.Modbus;

/// <summary>
/// Кодирование значений в регистры для записи — зеркало RegisterDecoder.
/// Big-endian, как на чтении. Целочисленные типы округляются до ближайшего
/// целого; значение вне диапазона типа — ArgumentOutOfRangeException,
/// чтобы ошибка падала ДО передачи в устройство (двухфазная запись, M7).
/// </summary>
public static class RegisterEncoder
{
    /// <summary>Значение → байты регистров (1 регистр = 2 байта, 32-битные = 4).</summary>
    public static byte[] Encode(double value, ModbusDataType type)
    {
        switch (type)
        {
            case ModbusDataType.UInt16:
                return Bytes16((ushort)RoundAndCheck(value, 0, ushort.MaxValue, type));

            case ModbusDataType.Int16:
                return Bytes16(unchecked((ushort)RoundAndCheck(value, short.MinValue, short.MaxValue, type)));

            case ModbusDataType.UInt32:
                return Bytes32(unchecked((uint)RoundAndCheck(value, 0, uint.MaxValue, type)));

            case ModbusDataType.Int32:
                return Bytes32(unchecked((uint)(int)RoundAndCheck(value, int.MinValue, int.MaxValue, type)));

            case ModbusDataType.Float32:
                if (double.IsNaN(value))
                    throw new ArgumentOutOfRangeException(nameof(value), value,
                        "значение не кодируется в Float32 (NaN)");
                float f = (float)value;
                if (float.IsInfinity(f))
                    throw new ArgumentOutOfRangeException(nameof(value), value,
                        "значение не кодируется в Float32 (переполнение)");
                return Bytes32(unchecked((uint)BitConverter.SingleToInt32Bits(f)));

            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, null);
        }
    }

    // округление до ближайшего целого: обратное масштабирование даёт
    // значения вроде 229.9999999, которые по смыслу — 230
    private static long RoundAndCheck(double value, long min, long max, ModbusDataType type)
    {
        if (double.IsNaN(value))
            throw new ArgumentOutOfRangeException(nameof(value), value,
                $"значение не кодируется в {type} (NaN)");
        double rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        if (rounded < min || rounded > max)
            throw new ArgumentOutOfRangeException(nameof(value), value,
                $"значение не кодируется в {type} (допустимо {min}..{max})");
        return (long)rounded;
    }

    private static byte[] Bytes16(ushort v) => [(byte)(v >> 8), (byte)v];

    private static byte[] Bytes32(uint v) =>
        [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
}
