using SCADA.Core.Tags;

namespace SCADA.Runtime.Runtime;

/// <summary>
/// Доступ к оперативным данным рантайма: текущие значения тегов,
/// отслеживание изменений, запись во внутренние теги.
/// Local-реализация работает с TagTable в памяти,
/// remote-реализация (в будущем) ходит на сервер по gRPC — UI не меняется.
/// </summary>
public interface IRuntimeClient
{
    // --- чтение ---

    TagValue Read(TagId id);

    // Пакетное чтение: мнемосхема на каждый кадр читает сотни тегов.
    // По одному вызову на тег в remote-варианте это были бы сотни сетевых запросов —
    // поэтому пакетный метод в контракте с самого начала.
    void Read(ReadOnlySpan<TagId> ids, Span<TagValue> results);

    // --- отслеживание изменений (модель эпох, ТЗ §9.2, §11.7) ---

    long CurrentEpoch { get; }

    // UI помнит эпоху прошлого кадра и спрашивает «что изменилось с тех пор».
    // Обновляет только изменившиеся элементы, а не перечитывает все 10 000 тегов.
    int GetChangedSince(long epoch, Span<TagId> destination);

    // --- история (docs/archive-format.md §13.2) ---
    //
    // Формы запросов повторяют IHistorian, чтобы код тренда писался один раз и
    // одинаково работал в режимах single ARM и выделенного сервера (ТЗ §5.1).
    // Но интерфейс отдельный: через gRPC буферами вызывающего управлять нельзя,
    // результат приходит массивами.
    //
    // Запросы пакетные по тегам: тренд на 10 тегов не должен порождать
    // 10 сетевых обменов. Пределы ответа проверяются на СЕРВЕРЕ — сервер общий
    // для всех АРМов (ТЗ §5.1), и один неудачный запрос не должен выводить
    // из строя остальные рабочие места.

    /// <summary>
    /// Сырые значения тегов за диапазон. Если точек больше
    /// <paramref name="maxPointsPerTag"/>, ответ прореживается до агрегатов, а
    /// в <see cref="HistorySeries.Downsampled"/> ставится признак: оператор,
    /// запросивший год, хочет увидеть год, а не сообщение об ошибке.
    /// </summary>
    ValueTask<HistorySeries[]> ReadHistoryAsync(
        IReadOnlyList<TagId> ids, long fromMs, long toMs,
        int maxPointsPerTag, CancellationToken ct = default);

    /// <summary>
    /// Агрегаты по интервалам одинаковой длины: столько бакетов, сколько
    /// запрошено. Основной запрос тренда на широком диапазоне.
    /// </summary>
    ValueTask<BucketSeries[]> ReadBucketsAsync(
        IReadOnlyList<TagId> ids, long fromMs, long toMs,
        int bucketCount, CancellationToken ct = default);

    /// <summary>
    /// Последние известные значения не позже <paramref name="atMs"/> — якорь
    /// левого края тренда. Без него у тега в режиме OnChange, не менявшегося
    /// сутки, левый край нарисуется пустым вместо горизонтальной линии.
    /// Элемент равен null, если данных до этого момента нет вовсе.
    /// </summary>
    ValueTask<TagValue?[]> ReadAtAsync(
        IReadOnlyList<TagId> ids, long atMs, CancellationToken ct = default);

    /// <summary>
    /// Последние N значений из кольца в памяти: без диска и без await.
    /// Realtime-тренд и правила сигнализации ходят сюда, а не в архив.
    /// </summary>
    int ReadRecent(TagId id, Span<TagValue> destination);

    // --- запись ---

    // Запись во ВНУТРЕННИЕ теги (уставки, режимы). Синхронно и мгновенно.
    // Запись в устройства — это M7 (команда в ПЛК с подтверждением и аудитом),
    // в этом интерфейсе появится отдельным методом позже.
    void WriteLocal(TagId id, double value);
}
