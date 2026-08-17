using SCADA.Historian;

namespace SCADA.Runtime.Historian;

/// <summary>Что делать, когда место на диске кончается (ТЗ §8.9).</summary>
public enum OnDiskFull
{
    /// <summary>Удалять самое старое до пола хранения, чтобы продолжать сбор.</summary>
    DeleteOldest = 0,

    /// <summary>Прекратить запись, ничего не удаляя.</summary>
    StopWriting = 1
}

/// <summary>Ступень лестницы по диску (ТЗ §8.9).</summary>
public enum DiskSpaceState
{
    Normal = 0,
    LowSpace = 1,
    WritingStopped = 2
}

/// <summary>Решение надзирателя за местом на диске.</summary>
public readonly record struct DiskSpaceDecision(
    DiskSpaceState State,
    bool ShouldAlarm,
    int? ForcedRetentionDays,
    bool SuspendWriting,
    string? Reason);

/// <summary>
/// Лестница при нехватке места (ТЗ §8.9). Выделена из службы отдельным
/// классом сознательно: это единственное место, где система сама решает
/// удалять данные заказчика, и такое решение должно проверяться тестами
/// без файловой системы и без таймеров.
/// </summary>
/// <remarks>
/// Принцип: потерять новые данные хуже, чем потерять старые — инцидент,
/// который будут разбирать, происходит сейчас, и остановка записи при
/// заполнении диска гарантирует отсутствие записей именно про него.
/// Но и молча срезать обещанный срок нельзя, поэтому есть пол.
/// </remarks>
public sealed class DiskSpaceSupervisor(
    long minFreeDiskMb,
    OnDiskFull onDiskFull,
    IRetentionPolicy policy)
{
    /// <summary>
    /// Во сколько раз ужимается срок хранения на каждом шаге освобождения.
    /// Шаг крупный намеренно: мелкие шаги дали бы десятки проходов ротации
    /// подряд на заполненном диске, каждый со сплошным чтением каталога.
    /// </summary>
    private const int ShrinkDivisor = 2;

    private int? _currentForcedRetention;

    /// <summary>Текущая ступень.</summary>
    public DiskSpaceState State { get; private set; } = DiskSpaceState.Normal;

    /// <summary>
    /// Оценить обстановку и выдать решение.
    /// </summary>
    /// <param name="freeDiskMb">Свободно на томе архива.</param>
    /// <param name="effectiveRetentionDays">
    /// Срок, применённый на прошлом проходе: null, если ротация шла штатно.
    /// </param>
    public DiskSpaceDecision Evaluate(long freeDiskMb, int? effectiveRetentionDays = null)
    {
        _currentForcedRetention = effectiveRetentionDays ?? _currentForcedRetention;

        if (freeDiskMb >= minFreeDiskMb)
        {
            // Место вернулось: снимаем ограничения и возвращаемся к штатному сроку.
            _currentForcedRetention = null;
            State = DiskSpaceState.Normal;
            return new DiskSpaceDecision(State, ShouldAlarm: false,
                ForcedRetentionDays: null, SuspendWriting: false, Reason: null);
        }

        if (onDiskFull == OnDiskFull.StopWriting)
        {
            State = DiskSpaceState.WritingStopped;
            return new DiskSpaceDecision(State, ShouldAlarm: true,
                ForcedRetentionDays: null, SuspendWriting: true,
                Reason: $"свободно {freeDiskMb} МБ при пороге {minFreeDiskMb} МБ, " +
                        "настройка OnDiskFull = StopWriting");
        }

        int nextRetention = ShrinkRetention();

        if (nextRetention <= policy.MinRetentionDays)
        {
            // Пол достигнут: удалять больше нечего, не нарушив обязательство.
            // Останавливаем запись — но данные, которые уже собраны, целы.
            State = DiskSpaceState.WritingStopped;
            return new DiskSpaceDecision(State, ShouldAlarm: true,
                ForcedRetentionDays: policy.MinRetentionDays, SuspendWriting: true,
                Reason: $"свободно {freeDiskMb} МБ, архив ужат до пола " +
                        $"{policy.MinRetentionDays} сут — освобождать больше нечего");
        }

        _currentForcedRetention = nextRetention;
        State = DiskSpaceState.LowSpace;

        return new DiskSpaceDecision(State, ShouldAlarm: true,
            ForcedRetentionDays: nextRetention, SuspendWriting: false,
            Reason: $"свободно {freeDiskMb} МБ при пороге {minFreeDiskMb} МБ, " +
                    $"глубина временно ужата до {nextRetention} сут");
    }

    private int ShrinkRetention()
    {
        int current = _currentForcedRetention ?? policy.GetRetentionDays(0);
        int shrunk = current / ShrinkDivisor;
        return Math.Max(shrunk, policy.MinRetentionDays);
    }
}
