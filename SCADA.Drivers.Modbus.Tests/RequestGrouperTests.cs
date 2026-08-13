namespace SCADA.Drivers.Modbus.Tests;

public class RequestGrouperTests
{
    private static ModbusTagMapping Tag(int resultIndex, string address)
        => new(resultIndex, ModbusAddress.Parse(address));

    [Fact]
    public void Group_AdjacentRegisters_MergeIntoOneBlock()
    {
        var blocks = RequestGrouper.Group(
            [Tag(0, "hr:100"), Tag(1, "hr:101"), Tag(2, "hr:102")]);

        var block = Assert.Single(blocks);
        Assert.Equal(ModbusTable.HoldingRegister, block.Table);
        Assert.Equal(100, block.Start);
        Assert.Equal(3, block.Count);
        Assert.Equal(3, block.Items.Count);
    }

    [Fact]
    public void Group_Float_OccupiesTwoRegisters()
    {
        // hr:100:f32 занимает 100-101, hr:102 сливается следом
        var blocks = RequestGrouper.Group([Tag(0, "hr:100:f32"), Tag(1, "hr:102")]);

        var block = Assert.Single(blocks);
        Assert.Equal(100, block.Start);
        Assert.Equal(3, block.Count); // регистры 100, 101, 102
    }

    [Fact]
    public void Group_LargeGap_SplitsBlocks()
    {
        var blocks = RequestGrouper.Group([Tag(0, "hr:100"), Tag(1, "hr:200")]);

        Assert.Equal(2, blocks.Count);
    }

    [Fact]
    public void Group_ProtocolLimit_SplitsBlocks()
    {
        // maxGap большой (слияние разрешено), но 126 регистров в один запрос
        // не влезают (лимит 125) — рез именно по лимиту протокола
        var blocks = RequestGrouper.Group(
            [Tag(0, "hr:0"), Tag(1, "hr:124"), Tag(2, "hr:126")], maxGap: 1000);

        Assert.Equal(2, blocks.Count);
        Assert.Equal(125, blocks[0].Count); // 0..124
        Assert.Equal(126, blocks[1].Start);
    }

    [Fact]
    public void Group_DifferentTables_NeverMixed()
    {
        var blocks = RequestGrouper.Group(
            [Tag(0, "hr:100"), Tag(1, "ir:100"), Tag(2, "coil:100"), Tag(3, "hr:101")]);

        Assert.Equal(3, blocks.Count); // hr{0,3}, ir{1}, coil{2}
    }

    [Fact]
    public void Group_PreservesResultIndexAndOffsetWithinBlock()
    {
        var blocks = RequestGrouper.Group(
            [Tag(42, "hr:100"), Tag(77, "hr:103")]);

        var block = Assert.Single(blocks);
        Assert.Equal(42, block.Items[0].ResultIndex);
        Assert.Equal(0, block.Items[0].OffsetWithinBlock);
        Assert.Equal(77, block.Items[1].ResultIndex);
        Assert.Equal(3, block.Items[1].OffsetWithinBlock);
    }

    [Fact]
    public void Group_UnsortedInput_SortsFirst()
    {
        var blocks = RequestGrouper.Group(
            [Tag(0, "hr:102"), Tag(1, "hr:100")]);

        var block = Assert.Single(blocks);
        Assert.Equal(100, block.Start);
        Assert.Equal(1, block.Items[0].ResultIndex); // hr:100 первым
    }
}
