using SCADA.Core.Tags;

namespace SCADA.Runtime.TagTable;

/// <summary>
/// Маршрутизация значений между общей таблицей объекта и локальной таблицей
/// сессионных тегов клиента (docs/session-tags-concept.md §2). Сам реализует
/// <see cref="ITagTable"/>, поэтому клиент и канва о втором хранилище
/// не знают: `TagId` — прямой индекс, выбор таблицы стоит один просмотр
/// массива.
///
/// Эпохи (§4): обе таблицы разделяют одну <see cref="EpochCounter"/>, то есть
/// живут на общей шкале времени. Поэтому число «изменилось после N» здесь
/// значит то же самое, что и в обычной таблице, — ни курсоров, ни истории,
/// ни отката к полному пересчёту.
///
/// Сессионная таблица нумеруется плотно: их десятки, и держать под них
/// массив на весь диапазон `TagId` значило бы просматривать лишние десять
/// тысяч слотов на каждом кадре.
/// </summary>
public sealed class SessionTagRouter : ITagTable
{
    private const int NotSession = -1;

    private readonly ITagTable _shared;
    private readonly TagTable _session;
    private readonly EpochCounter _epochs;

    /// <summary>Глобальный TagId → индекс в сессионной таблице (или -1).</summary>
    private readonly int[] _localIndex;

    /// <summary>Индекс в сессионной таблице → глобальный TagId.</summary>
    private readonly TagId[] _globalId;

    private readonly bool[] _isWritable;
    private readonly string[] _names;

    /// <summary>Оборачивает общую таблицу, если в проекте есть сессионные
    /// теги; иначе возвращает её саму — проект без них не платит ни лишним
    /// просмотром на чтении, ни вторым сканированием слотов.</summary>
    public static ITagTable Wrap(ITagTable shared, IReadOnlyList<TagDefinition> tags,
        EpochCounter epochs)
        => tags.Any(t => t.Scope == TagScope.Session)
            ? new SessionTagRouter(shared, tags, epochs)
            : shared;

    private SessionTagRouter(ITagTable shared, IReadOnlyList<TagDefinition> tags,
        EpochCounter epochs)
    {
        _shared = shared;
        _epochs = epochs;

        int capacity = tags.Max(t => t.Id.Value) + 1;
        _localIndex = new int[capacity];
        Array.Fill(_localIndex, NotSession);

        var sessionTags = tags.Where(t => t.Scope == TagScope.Session).ToArray();
        _globalId = new TagId[sessionTags.Length];
        _isWritable = new bool[sessionTags.Length];
        _names = new string[sessionTags.Length];

        for (int local = 0; local < sessionTags.Length; local++)
        {
            var tag = sessionTags[local];
            _localIndex[tag.Id.Value] = local;
            _globalId[local] = tag.Id;
            _isWritable[local] = tag.IsWritable;
            _names[local] = tag.Name;
        }

        _session = new TagTable(sessionTags.Length, epochs);

        // начальные значения сессионных тегов применяет клиент: движка
        // опроса за ними нет. Пока персистентность отложена (§5), InitValue —
        // единственный способ задать «вид по умолчанию» для всего объекта
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (int local = 0; local < sessionTags.Length; local++)
            if (sessionTags[local].InitValue is double init)
                _session.Write(new TagId(local), new TagValue(init, now, Quality.Good));
    }

    /// <summary>Индекс тега в сессионной таблице или -1, если тег общий.</summary>
    private int Local(TagId id)
        => (uint)id.Value < (uint)_localIndex.Length ? _localIndex[id.Value] : NotSession;

    /// <summary>Сессионный ли тег — нужно клиенту, чтобы не отправлять
    /// такую запись движку опроса.</summary>
    public bool IsSessionTag(TagId id) => Local(id) != NotSession;

    /// <summary>
    /// Операторская запись в сессионный тег: локальная, без сети, без
    /// права `Operate` и без аудита — это состояние интерфейса, а не команда
    /// объекту (§2.2). Флаг IsWritable смысла не теряет: у системных тегов
    /// (`@User.Name`, `@Right.*`) он снят, и переписать их действием нельзя.
    /// </summary>
    public TagWriteResult WriteFromOperator(TagId id, double value)
    {
        int local = Local(id);
        if (local == NotSession)
            return new TagWriteResult(TagWriteStatus.Failed, "тег не сессионный");
        if (!_isWritable[local])
            return new TagWriteResult(TagWriteStatus.NotWritable,
                $"сессионный тег '{_names[local]}' не доступен для записи");

        _session.Write(new TagId(local), new TagValue(value,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Quality.Good));
        return TagWriteResult.Success;
    }

    public TagValue Read(TagId id)
    {
        int local = Local(id);
        return local == NotSession ? _shared.Read(id) : _session.Read(new TagId(local));
    }

    public StringTagValue ReadString(TagId id)
    {
        int local = Local(id);
        return local == NotSession
            ? _shared.ReadString(id)
            : _session.ReadString(new TagId(local));
    }

    public void Write(TagId id, TagValue value)
    {
        int local = Local(id);
        if (local == NotSession)
            _shared.Write(id, value);
        else
            _session.Write(new TagId(local), value);
    }

    public void WriteString(TagId id, StringTagValue value)
    {
        int local = Local(id);
        if (local == NotSession)
            _shared.WriteString(id, value);
        else
            _session.WriteString(new TagId(local), value);
    }

    /// <summary>Общая шкала: значение сопоставимо с эпохами обеих таблиц.</summary>
    public long CurrentEpoch => _epochs.Current;

    /// <summary>
    /// Изменения обеих таблиц одним списком. Контракт тот же, что у обычной
    /// таблицы (<see cref="ITagTable.GetChangedSince"/>): возвращается полное
    /// число изменившихся, буфер заполняется настолько, насколько хватило
    /// места, результат больше его длины — переполнение.
    ///
    /// Две особенности, важные вызывающему: изменений за тик бывает больше,
    /// чем тегов в проектной таблице (сессионные считаются сверх неё), и Id
    /// в буфере всегда глобальные — сессионные плотные индексы переводятся
    /// обратно здесь же.
    /// </summary>
    public int GetChangedSince(long epoch, Span<TagId> destination)
    {
        int count = _shared.GetChangedSince(epoch, destination);

        // счёт от общей таблицы полный: если она уже переполнила буфер,
        // хвоста под сессионные теги нет — но их изменения всё равно
        // попадают в итоговое число, чтобы вызывающий увидел переполнение
        var tail = count < destination.Length ? destination[count..] : Span<TagId>.Empty;
        int sessionCount = _session.GetChangedSince(epoch, tail);

        // сессионная таблица отдаёт свои плотные индексы — переводим обратно
        int written = Math.Min(sessionCount, tail.Length);
        for (int i = 0; i < written; i++)
            tail[i] = _globalId[tail[i].Value];

        return count + sessionCount;
    }
}
