using SCADA.Core.Tags;

namespace SCADA.Runtime.TagTable;

public sealed class TagTable : ITagTable
{
    private readonly TagValue[] _values;
    private readonly object _sync = new();

    public TagTable(int capacity)
    {
        _values = new TagValue[capacity];
    }


    public TagValue Read(TagId id)
    {
        lock (_sync)
        {
            return _values[id.Value];
        }
    }

    public void Write(TagId id, TagValue value)
    {
        lock (_sync)
        {
            _values[id.Value] = value;
        }
    }
}
