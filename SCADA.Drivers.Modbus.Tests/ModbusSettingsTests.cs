namespace SCADA.Drivers.Modbus.Tests;

public class ModbusSettingsTests
{
    [Fact]
    public void Parse_EndpointOnly_AppliesProtocolDefaults()
    {
        var settings = ModbusSettings.Parse("192.168.0.10:502");

        Assert.Equal("192.168.0.10", settings.Host);
        Assert.Equal(502, settings.Port);
        Assert.Equal(0, settings.UnitId);   // стандарт для прямого TCP
        Assert.Equal(1000, settings.TimeoutMs);
        Assert.Equal(125, settings.MaxRegisters);
        Assert.Equal(8, settings.MaxGap);
    }

    [Fact]
    public void Parse_AllParameters_Read()
    {
        var settings = ModbusSettings.Parse("10.0.0.5:1502;unit=3;timeout=500;maxregs=60;maxgap=4");

        Assert.Equal("10.0.0.5", settings.Host);
        Assert.Equal(1502, settings.Port);
        Assert.Equal(3, settings.UnitId);
        Assert.Equal(500, settings.TimeoutMs);
        Assert.Equal(60, settings.MaxRegisters);
        Assert.Equal(4, settings.MaxGap);
    }

    [Theory]
    [InlineData("192.168.0.10")]            // нет порта
    [InlineData("192.168.0.10:0")]          // порт вне диапазона
    [InlineData("192.168.0.10:abc")]        // порт не число
    [InlineData("192.168.0.10:502;maxregs=0")]    // ниже минимума
    [InlineData("192.168.0.10:502;maxregs=126")]  // выше лимита протокола
    [InlineData("192.168.0.10:502;timeout=0")]
    [InlineData("192.168.0.10:502;unit=300")]     // не влезает в byte
    [InlineData("192.168.0.10:502;foo=1")]        // неизвестный параметр
    [InlineData("192.168.0.10:502;timeout")]      // нет значения
    public void Parse_InvalidConfiguration_ThrowsFormatException(string configuration)
    {
        Assert.Throws<FormatException>(() => ModbusSettings.Parse(configuration));
    }
}
