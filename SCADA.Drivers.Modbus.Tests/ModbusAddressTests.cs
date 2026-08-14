namespace SCADA.Drivers.Modbus.Tests;

public class ModbusAddressTests
{
    [Fact]
    public void Parse_RegisterWithDefaults()
    {
        var address = ModbusAddress.Parse("hr:100");

        Assert.Equal(ModbusTable.HoldingRegister, address.Table);
        Assert.Equal(100, address.Offset);
        Assert.Equal(ModbusDataType.UInt16, address.DataType);
        Assert.Equal(1, address.RegisterCount);
    }

    [Fact]
    public void Parse_Float_TakesTwoRegisters()
    {
        var address = ModbusAddress.Parse("ir:50:f32");

        Assert.Equal(ModbusTable.InputRegister, address.Table);
        Assert.Equal(50, address.Offset);
        Assert.Equal(ModbusDataType.Float32, address.DataType);
        Assert.Equal(2, address.RegisterCount);
    }

    [Fact]
    public void Parse_BitTables()
    {
        Assert.Equal(ModbusTable.Coil, ModbusAddress.Parse("coil:3").Table);
        Assert.Equal(ModbusTable.DiscreteInput, ModbusAddress.Parse("di:10").Table);
        Assert.Equal(0, ModbusAddress.Parse("coil:3").RegisterCount);
    }

    [Fact]
    public void Parse_CaseInsensitive()
    {
        Assert.Equal(ModbusTable.HoldingRegister, ModbusAddress.Parse("HR:5").Table);
        Assert.Equal(ModbusDataType.Float32, ModbusAddress.Parse("hr:5:F32").DataType);
    }

    [Theory]
    [InlineData("hr")]              // нет смещения
    [InlineData("hr:")]             // пустое смещение
    [InlineData("hr:abc")]          // смещение не число
    [InlineData("hr:-5")]           // отрицательное смещение
    [InlineData("xy:10")]           // неизвестная таблица
    [InlineData("hr:10:f64")]       // неизвестный тип
    [InlineData("coil:3:u16")]      // тип у битовой таблицы
    [InlineData("")]                // пустая строка
    public void Parse_Invalid_ThrowsWithAddressInMessage(string input)
    {
        var ex = Assert.Throws<FormatException>(() => ModbusAddress.Parse(input));

        Assert.Contains(input.Length > 0 ? input : "адрес", ex.Message);
    }
}
