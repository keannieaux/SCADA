using SCADA.Core.Tags;

namespace SCADA.Runtime.TagTable;

public sealed class TagTable : ITagTable
{
    private readonly TagSlot[] _slots;
    public TagTable(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _slots = new TagSlot[capacity];
    }


    public TagValue Read(TagId id)
    {
        ref TagSlot slot = ref _slots[id.Value];
        while (true)
        {
            int before = Volatile.Read(ref slot.Version);
            if((before & 1) != 0)
            {
                continue;
            }
            TagValue value = slot.Value;

            int after = Volatile.Read(ref slot.Version);
            if(after == before)
                return value;
        }
    }

    public void Write(TagId id, TagValue value)
    {
        ref TagSlot slot = ref _slots[id.Value];
        Interlocked.Increment(ref slot.Version);
        slot.Value = value;
        Interlocked.Increment(ref slot.Version);
    }
}
