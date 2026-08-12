using SCADA.Core.Tags;
using SCADA.Runtime.TagTable;

namespace SCADA.Runtime.Runtime;
public sealed class LocalRuntimeClient : IRuntimeClient
{
    private readonly ITagTable _tagTable;

    public LocalRuntimeClient(ITagTable tagTable) => _tagTable = tagTable;

    public TagValue Read(TagId id) => _tagTable.Read(id);

    public void Read(ReadOnlySpan<TagId> ids, Span<TagValue> results)
    {
        for (int i = 0; i < ids.Length; i++)
            results[i] = _tagTable.Read(ids[i]);
    }

    public long CurrentEpoch => _tagTable.CurrentEpoch;

    public int GetChangedSince(long epoch, Span<TagId> destination)
        => _tagTable.GetChangedSince(epoch, destination);

    public void WriteLocal(TagId id, double value)
        => _tagTable.Write(id, new TagValue(value, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Quality.Good));
}
