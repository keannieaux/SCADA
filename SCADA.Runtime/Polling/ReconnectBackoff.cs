namespace SCADA.Runtime.Polling;

/// <summary>
/// Экспоненциальная задержка переподключения (ТЗ §4.2).
/// Вынесена в параметр, а не зашита константами: это политика, а не свойство
/// протокола. Боевое умолчание 1с → 30с бережёт сеть и лог при долгом обрыве,
/// но делает тесты переподключения либо медленными, либо неустойчивыми —
/// пока накопится нужное число отказов, задержка вырастает до десятков секунд.
/// </summary>
public sealed class ReconnectBackoff
{
    /// <summary>Задержка после первого отказа.</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Потолок задержки.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Боевая политика по ТЗ §4.2: 1 → 2 → 4 → 8 → 16 → 30 секунд.</summary>
    public static ReconnectBackoff Default { get; } = new();

    public TimeSpan Delay(int consecutiveFailures)
    {
        if (consecutiveFailures <= 1)
            return BaseDelay;

        // Сдвиг ограничен 30 разрядами: дальше он переполнил бы long, а до
        // потолка дело доходит на первых же итерациях.
        int shift = Math.Min(consecutiveFailures - 1, 30);
        long ticks = BaseDelay.Ticks << shift;

        return ticks <= 0 || ticks > MaxDelay.Ticks ? MaxDelay : TimeSpan.FromTicks(ticks);
    }
}
