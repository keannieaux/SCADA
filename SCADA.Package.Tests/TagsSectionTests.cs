using SCADA.Core.Devices;
using SCADA.Core.Tags;
using SCADA.Package.Builder.Sections;
using SCADA.Package.Sections;

namespace SCADA.Package.Tests;

public class TagsSectionTests
{
    [Fact]
    public void RoundTrip_FullTag_AllFieldsPreserved()
    {
        var tags = new List<TagDefinition>
        {
            new()
            {
                Id = new TagId(7),
                DataType = TagDataType.Analog,
                DeviceId = new DeviceId(3),
                Name = "Boiler1.Temp",
                Description = "Температура котла",
                Address = "40001",
                ScaleFactor = 0.1,
                ScaleOffset = -40.0,
                MinValue = 0,
                MaxValue = 150,
                IsArchived = true,
                Precision = 3,
                Units = "°C",
                IsWritable = true,
                RequiresWriteConfirmation = true,
                InitValue = 20.0,
                IsPersistent = true,
                Logging = new TagLoggingConfiguration
                {
                    LogOnChange = true,
                    Interval = TimeSpan.FromMinutes(5),
                    Schedule =
                    [
                        new LogScheduleEntry { Time = new TimeOnly(8, 30), DayOfWeek = DayOfWeek.Monday },
                        new LogScheduleEntry { Time = new TimeOnly(23, 59), DayOfMonth = 15, Month = 6 }
                    ]
                }
            },
            new()
            {
                Id = new TagId(8),
                DataType = TagDataType.Discrete,
                DeviceId = new DeviceId(3),
                Name = "Pump1.Running"
                // всё остальное по умолчанию: пустые строки, null-поля, без Logging
            }
        };

        var bytes = TagsSectionWriter.Write(tags);
        var restored = TagsSectionReader.Read(bytes);

        Assert.Equal(2, restored.Count);

        var full = restored[0];
        Assert.Equal(7, full.Id.Value);
        Assert.Equal(TagDataType.Analog, full.DataType);
        Assert.Equal(3, full.DeviceId.Value);
        Assert.Equal("Boiler1.Temp", full.Name);
        Assert.Equal("Температура котла", full.Description);
        Assert.Equal("40001", full.Address);
        Assert.Equal(0.1, full.ScaleFactor);
        Assert.Equal(-40.0, full.ScaleOffset);
        Assert.Equal(0, full.MinValue);
        Assert.Equal(150, full.MaxValue);
        Assert.True(full.IsArchived);
        Assert.Equal(3, full.Precision);
        Assert.Equal("°C", full.Units);
        Assert.True(full.IsWritable);
        Assert.True(full.RequiresWriteConfirmation);
        Assert.Equal(20.0, full.InitValue);
        Assert.True(full.IsPersistent);

        Assert.NotNull(full.Logging);
        var logging = full.Logging;
        Assert.True(logging.LogOnChange);
        Assert.Equal(TimeSpan.FromMinutes(5), logging.Interval);
        Assert.Equal(2, logging.Schedule.Count);
        Assert.Equal(new TimeOnly(8, 30), logging.Schedule[0].Time);
        Assert.Equal(DayOfWeek.Monday, logging.Schedule[0].DayOfWeek);
        Assert.Null(logging.Schedule[0].DayOfMonth);
        Assert.Equal(new TimeOnly(23, 59), logging.Schedule[1].Time);
        Assert.Null(logging.Schedule[1].DayOfWeek);
        Assert.Equal(15, logging.Schedule[1].DayOfMonth);
        Assert.Equal(6, logging.Schedule[1].Month);

        var minimal = restored[1];
        Assert.Equal("Pump1.Running", minimal.Name);
        Assert.Equal("", minimal.Description);
        Assert.Null(minimal.MinValue);
        Assert.Null(minimal.InitValue);
        Assert.Null(minimal.Logging);
        Assert.False(minimal.IsWritable);
        Assert.False(minimal.RequiresWriteConfirmation);
    }
}
