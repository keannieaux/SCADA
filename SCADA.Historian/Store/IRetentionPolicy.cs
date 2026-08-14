namespace SCADA.Historian;

/// <summary>
/// Глубина хранения на поток (docs/archive-format.md §15.1).
/// </summary>
/// <remarks>
/// Узел заведён, хотя сегодня отвечает одинаково для всех потоков: глубина на
/// класс тегов — предсказуемое требование следующих проектов («аварийные три
/// года, вспомогательные полгода»), а разница в реализации сводится к тому,
/// перебирать файлы внутри каталога месяца или удалять каталог целиком.
/// Формат, кодеки и чтение не затрагиваются.
/// </remarks>
public interface IRetentionPolicy
{
    /// <summary>Сколько суток хранить данные потока.</summary>
    int GetRetentionDays(int streamId);

    /// <summary>
    /// Пол досрочного удаления: ниже этой глубины данные не удаляются никогда,
    /// в том числе при заполненном диске (ТЗ §8.9). Договорное обязательство
    /// перед заказчиком, а не эвристика.
    /// </summary>
    int MinRetentionDays { get; }
}

/// <summary>
/// Один срок на всю систему — конфигурация M4 (ТЗ §8.6).
/// </summary>
public sealed class FixedRetentionPolicy : IRetentionPolicy
{
    private readonly int _retentionDays;

    public FixedRetentionPolicy(int retentionDays, int minRetentionDays)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retentionDays, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(minRetentionDays, 1);

        if (minRetentionDays > retentionDays)
        {
            throw new ArgumentException(
                $"Пол хранения ({minRetentionDays} сут) не может превышать основную глубину " +
                $"({retentionDays} сут): при нехватке места система не смогла бы освободить ничего.",
                nameof(minRetentionDays));
        }

        _retentionDays = retentionDays;
        MinRetentionDays = minRetentionDays;
    }

    public int MinRetentionDays { get; }

    public int GetRetentionDays(int streamId) => _retentionDays;
}
