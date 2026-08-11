using SCADA.Core.Tags;
namespace SCADA.Runtime.TagTable;

public interface ITagTable
{
    TagValue Read(TagId id);
    void Write(TagId id, TagValue value);
}
