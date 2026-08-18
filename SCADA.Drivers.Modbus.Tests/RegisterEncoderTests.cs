namespace SCADA.Drivers.Modbus.Tests;

/// <summary>RegisterEncoder — зеркало RegisterDecoder (M7): round-trip
/// через Decode и отказы за пределами диапазона типа, до передачи в устройство.</summary>
public class RegisterEncoderTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(230)]
    [InlineData(65535)]
    public void UInt16_RoundTrips(double value)
        => Assert.Equal(value, RegisterDecoder.Decode(
            RegisterEncoder.Encode(value, ModbusDataType.UInt16), 0, ModbusDataType.UInt16));

    [Theory]
    [InlineData(-32768)]
    [InlineData(-5)]
    [InlineData(32767)]
    public void Int16_RoundTrips(double value)
        => Assert.Equal(value, RegisterDecoder.Decode(
            RegisterEncoder.Encode(value, ModbusDataType.Int16), 0, ModbusDataType.Int16));

    [Theory]
    [InlineData(0)]
    [InlineData(4294967295.0)]
    public void UInt32_RoundTrips(double value)
        => Assert.Equal(value, RegisterDecoder.Decode(
            RegisterEncoder.Encode(value, ModbusDataType.UInt32), 0, ModbusDataType.UInt32));

    [Theory]
    [InlineData(-2147483648.0)]
    [InlineData(2147483647.0)]
    public void Int32_RoundTrips(double value)
        => Assert.Equal(value, RegisterDecoder.Decode(
            RegisterEncoder.Encode(value, ModbusDataType.Int32), 0, ModbusDataType.Int32));

    [Theory]
    [InlineData(0.0)]
    [InlineData(23.5)]
    [InlineData(-1234.75)]
    public void Float32_RoundTrips(double value)
        => Assert.Equal((float)value, (float)RegisterDecoder.Decode(
            RegisterEncoder.Encode(value, ModbusDataType.Float32), 0, ModbusDataType.Float32));

    [Fact]
    public void Encode_BigEndianLayout()
    {
        // 0x0102 → hi-байт первым (Modbus big-endian)
        var bytes = RegisterEncoder.Encode(0x0102, ModbusDataType.UInt16);
        Assert.Equal([0x01, 0x02], bytes);

        var bytes32 = RegisterEncoder.Encode(0x01020304, ModbusDataType.UInt32);
        Assert.Equal([0x01, 0x02, 0x03, 0x04], bytes32);
    }

    [Fact]
    public void Encode_RoundsToNearestInteger()
    {
        // обратное масштабирование даёт 229.9999999 — по смыслу 230
        var bytes = RegisterEncoder.Encode(229.9999999, ModbusDataType.UInt16);
        Assert.Equal(230.0, RegisterDecoder.Decode(bytes, 0, ModbusDataType.UInt16));
    }

    [Theory]
    [InlineData(65536, ModbusDataType.UInt16)]
    [InlineData(-1, ModbusDataType.UInt16)]
    [InlineData(32768, ModbusDataType.Int16)]
    [InlineData(-32769, ModbusDataType.Int16)]
    [InlineData(4294967296.0, ModbusDataType.UInt32)]
    [InlineData(3.5e38, ModbusDataType.Float32)]
    [InlineData(double.NaN, ModbusDataType.Float32)]
    [InlineData(double.NaN, ModbusDataType.Int16)]
    public void Encode_OutOfRange_ThrowsBeforeTransmission(double value, ModbusDataType type)
        => Assert.Throws<ArgumentOutOfRangeException>(() => RegisterEncoder.Encode(value, type));
}
