namespace SCADA.Core.Tags;

public interface ITagTable : ITagValueReader
{
    void Write(TagId id, TagValue value);

    /// <summary>Запись строкового тега (концепт §4.6). Бампит ту же общую
    /// эпоху, что и числовая запись, — GetChangedSince покрывает строки.</summary>
    void WriteString(TagId id, StringTagValue value);

    /// <summary>Чтение строкового тега. Метка и качество — из общей части
    /// слота; нетронутый тег — <see cref="StringTagValue.Empty"/>.</summary>
    StringTagValue ReadString(TagId id);

    long CurrentEpoch{get;}
    int GetChangedSince(long epoch, Span<TagId> destination);
}
