namespace SCADA.Core.Tags;

/// <summary>
/// Агрегат значений тега за интервал времени: результат чтения истории на
/// широком диапазоне (docs/archive-format.md §13.1, §14.1).
/// </summary>
/// <remarks>
/// Живёт в Core, а не в хранилище: это примитив модели данных, общий для
/// хранилища, фасада истории и контракта UI ↔ ядро. Иначе клиентская сторона
/// тянула бы за собой всю механику формата.
///
/// <para><see cref="Count"/> считает все отсчёты интервала, <see cref="GoodCount"/> —
/// только достоверные. Отсюда три различимых состояния:
/// <c>Count == 0</c> — данных не собирали (пропуск);
/// <c>Count > 0, GoodCount == 0</c> — собирали, но достоверных нет, агрегаты равны NaN;
/// <c>GoodCount > 0</c> — агрегаты посчитаны по достоверным значениям.</para>
/// </remarks>
public readonly record struct ArchiveBucket(
    long StartMs,
    long EndMs,
    double Min,
    double Max,
    double Avg,
    int Count,
    int GoodCount)
{
    /// <summary>Данных в интервале не было вовсе.</summary>
    public bool IsEmpty => Count == 0;

    /// <summary>Есть ли достоверные значения, по которым посчитаны агрегаты.</summary>
    public bool HasGoodValues => GoodCount > 0;
}
