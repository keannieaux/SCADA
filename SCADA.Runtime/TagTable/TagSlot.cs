using SCADA.Core.Tags;

namespace SCADA.Runtime.TagTable;

internal struct TagSlot
{
    public int Version;
    public TagValue Value;

    // строковое значение (TagDataType.String, концепт §4.6): живёт в том же
    // слоте и под тем же протоколом версий — эпохи и грязный пересчёт общие
    public string? Text;

    public long LastChangedEpoch;
}
