using SCADA.Core.Alarms;

namespace SCADA.Alarms;

/// <summary>Восстановленное из журнала состояние незавершённой аварии (§7.3).</summary>
public record RecoveredAlarmState(
    string RuleName,
    ThresholdKind? Limit,
    AlarmState State,
    long ActivatedAtUtcMs,
    string? AcknowledgedBy);

/// <summary>
/// Восстановление активных аварий из хвоста журнала при старте (§7.3).
/// События группируются по правилу и проигрываются в прямом порядке через
/// state machine §7.1 — итоговое состояние правила и есть восстановленное.
/// Полностью закрытые (Normal) аварии отбрасываются. Событие Escalated
/// состояние не меняет — авария та же, деталь уставки остаётся в журнале.
/// </summary>
public static class AlarmStateRecovery
{
    public static IReadOnlyList<RecoveredAlarmState> Resolve(IReadOnlyList<AlarmEvent> eventsDesc)
    {
        var result = new List<RecoveredAlarmState>();

        foreach (var group in eventsDesc.GroupBy(e => e.RuleName))
        {
            var state = AlarmState.Normal;
            long activatedAt = 0;
            string? ackBy = null;
            ThresholdKind? limit = null;

            // eventsDesc — от новых к старым, replay идёт в прямом порядке
            foreach (var ev in group.Reverse())
            {
                switch (ev.Type)
                {
                    case AlarmEventType.Active:
                        state = AlarmState.ActiveUnack;
                        activatedAt = ev.TimestampUtcMs;
                        ackBy = null;
                        limit = ev.Limit;
                        break;
                    case AlarmEventType.Escalated:
                        limit = ev.Limit; // та же авария, старшая уставка
                        break;
                    case AlarmEventType.Normal:
                        state = state == AlarmState.ActiveUnack
                            ? AlarmState.RtnUnack
                            : AlarmState.Normal;
                        break;
                    case AlarmEventType.Acknowledged:
                        ackBy = ev.AcknowledgedBy;
                        state = state switch
                        {
                            AlarmState.ActiveUnack => AlarmState.ActiveAck,
                            AlarmState.RtnUnack => AlarmState.Normal,
                            _ => state
                        };
                        break;
                }
            }

            if (state != AlarmState.Normal)
                result.Add(new RecoveredAlarmState(
                    group.Key, limit, state, activatedAt, ackBy));
        }

        return result;
    }
}
