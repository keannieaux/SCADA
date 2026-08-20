namespace SCADA.Runtime.Tests;

/// <summary>
/// Управляемые часы для тестов сессий и правил их завершения
/// (docs/users-plan.md §6.1): автоблокировку проверяем сдвигом времени,
/// а не ожиданием.
/// </summary>
public sealed class FakeTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
