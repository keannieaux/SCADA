namespace SCADA.Core.Tags;

/// <summary>
/// Значение строкового тега (TagDataType.String, концепт §4.6). Хранится
/// параллельно числовому слоту: метка времени и качество живут в той же
/// записи, что и у чисел, поэтому грязный пересчёт по эпохам покрывает
/// строки без отдельного канала. ВМ выражений строки не видит — выражения
/// остаются числовыми (§14).
/// </summary>
public readonly record struct StringTagValue(string Text, long TimeStampUtc, Quality Quality)
{
    /// <summary>Нетронутый строковый тег: пустой текст, неопределённое качество.</summary>
    public static readonly StringTagValue Empty = new("", 0, Quality.Uncertain);
}
