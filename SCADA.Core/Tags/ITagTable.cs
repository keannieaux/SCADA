namespace SCADA.Core.Tags;

public interface ITagTable : ITagValueReader
{
    void Write(TagId id, TagValue value);
    long CurrentEpoch{get;}
    int GetChangedSince(long epoch, Span<TagId> destination);
}
