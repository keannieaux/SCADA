using SCADA.Core.Tags;

namespace SCADA.Runtime.Historian;

public interface IHistorian
{
    void Append(TagId id, TagValue value);
    int Read(TagId id, long fromUtc, long toUtc,Span<TagValue> destination);
}
