using SCADA.Core.Tags;

namespace SCADA.Runtime.TagTable;

public sealed class TagTable : ITagTable
{
    private readonly TagSlot[] _slots;
    private long _epoch;

    public long CurrentEpoch => Interlocked.Read(ref _epoch);
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
        slot.LastChangedEpoch = Interlocked.Increment(ref _epoch);
        Interlocked.Increment(ref slot.Version);
    }

    public void WriteString(TagId id, StringTagValue value)
    {
        ref TagSlot slot = ref _slots[id.Value];

        // тот же протокол версий: текст и метка/качество меняются атомарно
        // для читателя, эпоха общая с числовыми записями
        Interlocked.Increment(ref slot.Version);
        slot.Text = value.Text;
        slot.Value = new TagValue(slot.Value.Value, value.TimeStampUtc, value.Quality);
        slot.LastChangedEpoch = Interlocked.Increment(ref _epoch);
        Interlocked.Increment(ref slot.Version);
    }

    public StringTagValue ReadString(TagId id)
    {
        ref TagSlot slot = ref _slots[id.Value];
        while (true)
        {
            int before = Volatile.Read(ref slot.Version);
            if ((before & 1) != 0)
            {
                continue;
            }
            string? text = slot.Text;
            var value = text is null
                ? StringTagValue.Empty // нетронутый слот: пусто и Uncertain, а не дефолтный Bad
                : new StringTagValue(text, slot.Value.TimeStampUtc, slot.Value.Quality);

            int after = Volatile.Read(ref slot.Version);
            if (after == before)
                return value;
        }
    }

    public int GetChangedSince(long epoch, Span<TagId> destination)
    {
        int count = 0;
        for(int i = 0; i< _slots.Length; i++)
        {
            if(Volatile.Read(ref _slots[i].LastChangedEpoch)> epoch)
            {
                if(count < destination.Length)
                {
                    destination[count] = new TagId(i);
                }
                count++;
            }
        }
        return count;
    }
}
