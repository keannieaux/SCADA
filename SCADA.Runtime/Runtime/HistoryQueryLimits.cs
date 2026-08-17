namespace SCADA.Runtime.Runtime;

/// <summary>
/// Пределы запроса истории (docs/archive-format.md §14.1).
/// Проверяются на стороне сервера: он обслуживает все операторские станции
/// (ТЗ §5.1), и без пределов один запрос сырого года по сотне тегов выводит
/// из строя все рабочие места разом.
/// </summary>
public sealed class HistoryQueryLimits
{
    /// <summary>
    /// Потолок сырых точек на один тег в ответе. При превышении ответ
    /// прореживается до агрегатов с признаком, а не отклоняется.
    /// </summary>
    public int MaxPointsPerQuery { get; set; } = 200_000;

    /// <summary>
    /// Потолок числа тегов в одном запросе. Здесь отказ, а не прореживание:
    /// молча урезать список тегов значит нарисовать тренд без части кривых,
    /// и это хуже внятной ошибки.
    /// </summary>
    public int MaxStreamsPerQuery { get; set; } = 100;

    /// <summary>Потолок времени выполнения запроса.</summary>
    public int QueryTimeoutMs { get; set; } = 30_000;

    public void EnsureStreamCount(int requested)
    {
        if (requested > MaxStreamsPerQuery)
        {
            throw new ArgumentException(
                $"Запрошено {requested} тегов при пределе {MaxStreamsPerQuery}. " +
                "Разделите запрос: сервер обслуживает все рабочие места.");
        }
    }
}
