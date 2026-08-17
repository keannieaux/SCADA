using System.Threading.Channels;
using SCADA.Alarms;

namespace SCADA.Runtime.Alarms;

/// <summary>
/// Рассылка изменений аварий подписчикам UI (docs/M5-plan.md §9).
/// Публикуют конвейер (события от тегов) и клиент (квитирование).
/// Медленный подписчик теряет промежуточные изменения (DropWrite) — баннер
/// всегда может перечитать полный список через GetActive.
/// </summary>
public sealed class AlarmChangeBroadcaster
{
    private readonly object _sync = new();
    private readonly List<Channel<AlarmChange>> _subscribers = new();

    public void Publish(AlarmChange change)
    {
        lock (_sync)
            foreach (var subscriber in _subscribers)
                subscriber.Writer.TryWrite(change);
    }

    public IAsyncEnumerable<AlarmChange> Subscribe(CancellationToken ct)
        => SubscribeCore(ct);

    private async IAsyncEnumerable<AlarmChange> SubscribeCore(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<AlarmChange>(
            new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true
            });

        lock (_sync)
            _subscribers.Add(channel);

        try
        {
            while (await channel.Reader.WaitToReadAsync(ct))
                while (channel.Reader.TryRead(out var change))
                    yield return change;
        }
        finally
        {
            lock (_sync)
                _subscribers.Remove(channel);
        }
    }
}
