namespace SCADA.Runtime.Runtime;

/// <summary>
/// Состояние жизненного цикла <see cref="RuntimeHost"/>. Живёт на хосте,
/// а не на <see cref="IRuntimeClient"/>: клиент остаётся транспортно-тонким,
/// remote-вариант получит своё состояние отдельно.
/// </summary>
public enum RuntimeState
{
    /// <summary>Хост собран, движки ещё не запущены.</summary>
    Starting,

    /// <summary>Опрос, сигнализация и архив (если включён) работают.</summary>
    Running,

    /// <summary>Старт или остановка завершились исключением.</summary>
    Faulted,

    /// <summary>Штатная остановка выполнена.</summary>
    Stopped
}
