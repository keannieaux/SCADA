using SCADA.Core.Tags;
using SCADA.Runtime.TagTable;

namespace SCADA.Runtime.Historian;

public sealed class InMemoryHistorian : IHistorian
{
    // ВНИМАНИЕ: это заглушка для разработки трендов (§16.4), а не хранилище.
    // Час данных всех тегов в RAM невозможен физически (20k × 3600 × 24 Б ≈ 1.7 ГБ) —
    // боевой архив M4 дисковый, со сжатием и агрегатами (§8).
    // Спасает ленивое выделение: буферы создаются только для пишущихся тегов.
    private readonly int _capacityPerTag;
    private readonly RingBuffer?[] _buffers;

    // подаватель данных: включён, только если передана таблица
    private readonly ITagTable? _tagTable;
    private readonly TimeSpan _feedPeriod;
    private long _lastEpoch;
    private CancellationTokenSource? _cts;
    private Task? _feedTask;

    public InMemoryHistorian(int tagCapacity, int capacityPerTag = 3600,
        ITagTable? tagTable = null, TimeSpan? feedPeriod = null)
    {
        _capacityPerTag = capacityPerTag;
        _buffers = new RingBuffer?[tagCapacity];
        _tagTable = tagTable;
        _feedPeriod = feedPeriod ?? TimeSpan.FromMilliseconds(100);
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_tagTable is null)
            throw new InvalidOperationException("Историк создан без TagTable — подаватель недоступен");
        if (_cts is not null)
            throw new InvalidOperationException("Уже запущен");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // стартуем с «сейчас»: данные до запуска историка не догоняем
        _lastEpoch = _tagTable.CurrentEpoch;
        _feedTask = FeedAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is null)
            return;

        await _cts.CancelAsync();
        try { await _feedTask!; }
        catch (OperationCanceledException) { } // штатная остановка

        _cts.Dispose();
        _cts = null;
        _feedTask = null;
    }

    // Подписчик таблицы (как UI): на каждом тике забирает изменившиеся
    // с прошлого тика теги и пишет их значения в архив.
    private async Task FeedAsync(CancellationToken ct)
    {
        var changed = new TagId[4096];

        using var timer = new PeriodicTimer(_feedPeriod);
        while (await timer.WaitForNextTickAsync(ct))
        {
            int count = _tagTable!.GetChangedSince(_lastEpoch, changed);

            // изменений больше, чем буфер: для заглушки просто перевыделяем побольше.
            // (в боевом M4-конвейере это штатная ситуация с backpressure — там свой механизм)
            if (count > changed.Length)
            {
                changed = new TagId[count];
                count = _tagTable.GetChangedSince(_lastEpoch, changed);
            }

            for (int i = 0; i < count; i++)
                Append(changed[i], _tagTable.Read(changed[i]));

            // эпоху берём ПОСЛЕ чтения: записи, пришедшие во время обработки,
            // попадут в следующий тик и не потеряются
            _lastEpoch = _tagTable.CurrentEpoch;
        }
    }

    public void Append(TagId id, TagValue value)
    {
        var buffer = _buffers[id.Value] ??= new RingBuffer(_capacityPerTag);
        lock (buffer)
            buffer.Add(value);
    }

    public int Read(TagId id, long fromUtc, long toUtc, Span<TagValue> destination)
    {
        var buffer = _buffers[id.Value];
        if (buffer is null) return 0;
        lock (buffer)
            return buffer.CopyRange(fromUtc, toUtc, destination);
    }


    private sealed class RingBuffer
    {
        private readonly TagValue[] _items;
        private int _head;   // куда пишем СЛЕДУЮЩИЙ элемент
        private int _count;  // сколько элементов реально лежит (0..capacity)

        public RingBuffer(int capacity) => _items = new TagValue[capacity];

        public void Add(TagValue value)
        {
            _items[_head] = value;
            _head = (_head + 1) % _items.Length;
            if (_count < _items.Length) _count++;
        }

        // i-й по старшинству элемент (0 = самый старый).
        // Голова указывает на следующую запись, поэтому самый старый —
        // на _count позиций назад от неё; +Length перед % защищает от ухода в минус
        private TagValue Oldest(int i)
            => _items[(_head - _count + i + _items.Length) % _items.Length];

        public int CopyRange(long fromUtc, long toUtc, Span<TagValue> destination)
        {
            // первый проход: сколько точек попадает в диапазон
            int matching = 0;
            for (int i = 0; i < _count; i++)
            {
                var t = Oldest(i).TimeStampUtc;
                if (t >= fromUtc && t <= toUtc) matching++;
            }

            // если больше, чем влезает в буфер, — отдаём САМЫЕ ПОЗДНИЕ:
            // пропускаем (matching - destination.Length) самых старых
            int skip = Math.Max(0, matching - destination.Length);
            int written = 0;
            for (int i = 0; i < _count && written < destination.Length; i++)
            {
                var item = Oldest(i);
                if (item.TimeStampUtc < fromUtc || item.TimeStampUtc > toUtc) continue;
                if (skip > 0) { skip--; continue; }
                destination[written++] = item;
            }
            return written;
        }
    }
}




