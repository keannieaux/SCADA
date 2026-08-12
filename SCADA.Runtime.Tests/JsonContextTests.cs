using System.Text.Json;
using SCADA.Core.Tags;
using SCADA.Runtime.Configuration;

namespace  SCADA.Runtime.Tests;

public class JsonContextTests
{
    [Fact]
    public void TagDefinition_Deserializes_WithPlainIntIds()
    {
         var json = """{"id": 5, "name": "T1", "dataType": "analog", "deviceId": 0}""";
         var tag = JsonSerializer.Deserialize(json,ProjectJsonContext.Default.TagDefinition);

         Assert.NotNull(tag);
         Assert.Equal(new TagId(5),tag.Id);
         Assert.Equal(TagDataType.Analog,tag.DataType);
    }
}
