using SCADA.Core.Tags;

namespace SCADA.Runtime.TagTable;

public sealed class TagTable : ITagTable
{
    private readonly TagSlot[] _slots;
    private readonly EpochCounter _epochs;

    public long CurrentEpoch => _epochs.Current;

    /// <param name="epochs">
    /// Общая шкала времени зрителя (docs/session-tags-concept.md §4). Таблицы,
    /// которые читает один и тот же клиент, обязаны получить один экземпляр —
    /// иначе «что изменилось после N» перестаёт быть сопоставимым между ними.
    /// null — таблица заводит собственную шкалу (одиночное использование,
    /// тесты).
    /// </param>
    public TagTable(int capacity, EpochCounter? epochs = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _slots = new TagSlot[capacity];
        _epochs = epochs ?? new EpochCounter();
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
        slot.LastChangedEpoch = _epochs.Next();
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
        slot.LastChangedEpoch = _epochs.Next();
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
