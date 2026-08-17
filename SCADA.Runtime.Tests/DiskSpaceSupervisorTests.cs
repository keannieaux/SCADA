using SCADA.Historian;
using SCADA.Runtime.Historian;

namespace SCADA.Runtime.Tests;

/// <summary>
/// Лестница при нехватке места (ТЗ §8.9). Это единственное место, где система
/// сама решает удалять данные заказчика, поэтому решение проверяется отдельно
/// от файловой системы и таймеров.
/// </summary>
public class DiskSpaceSupervisorTests
{
    private const long Threshold = 5000;

    private static readonly IRetentionPolicy Policy = new FixedRetentionPolicy(400, 30);

    private static DiskSpaceSupervisor Create(OnDiskFull onDiskFull = OnDiskFull.DeleteOldest)
        => new(Threshold, onDiskFull, Policy);

    [Fact]
    public void EnoughSpace_NoAlarmNoShrink()
    {
        var decision = Create().Evaluate(freeDiskMb: 10_000);

        Assert.Equal(DiskSpaceState.Normal, decision.State);
        Assert.False(decision.ShouldAlarm);
        Assert.False(decision.SuspendWriting);
        Assert.Null(decision.ForcedRetentionDays);
    }

    [Fact]
    public void BelowThreshold_AlarmsAndShrinks_ButKeepsWriting()
    {
        var decision = Create().Evaluate(freeDiskMb: 100);

        // Ключевое: запись продолжается. Инцидент, который будут разбирать,
        // происходит сейчас, и терять его записи нельзя.
        Assert.Equal(DiskSpaceState.LowSpace, decision.State);
        Assert.True(decision.ShouldAlarm);
        Assert.False(decision.SuspendWriting);
        Assert.Equal(200, decision.ForcedRetentionDays);
        Assert.Contains("ужата до 200", decision.Reason);
    }

    [Fact]
    public void RepeatedPressure_ShrinksStepwise_DownToFloor()
    {
        var supervisor = Create();
        var seen = new List<int?>();

        for (int i = 0; i < 6; i++)
            seen.Add(supervisor.Evaluate(freeDiskMb: 100).ForcedRetentionDays);

        // 400 → 200 → 100 → 50 → пол 30, ниже не опускается никогда.
        Assert.Equal([200, 100, 50, 30, 30, 30], seen);
        Assert.All(seen, days => Assert.True(days >= Policy.MinRetentionDays));
    }

    [Fact]
    public void FloorReached_StopsWritingInsteadOfBreakingPromise()
    {
        var supervisor = Create();

        DiskSpaceDecision decision = default;
        for (int i = 0; i < 5; i++)
            decision = supervisor.Evaluate(freeDiskMb: 100);

        // Ужимать дальше значило бы нарушить обещанную заказчику глубину.
        // Вместо этого останавливаем запись — уже собранное цело.
        Assert.Equal(DiskSpaceState.WritingStopped, decision.State);
        Assert.True(decision.SuspendWriting);
        Assert.Equal(30, decision.ForcedRetentionDays);
        Assert.Contains("освобождать больше нечего", decision.Reason);
    }

    [Fact]
    public void StopWritingMode_NeverDeletes()
    {
        var decision = Create(OnDiskFull.StopWriting).Evaluate(freeDiskMb: 1);

        // Срок закреплён договором: удалять нельзя ничего, даже под давлением.
        Assert.Equal(DiskSpaceState.WritingStopped, decision.State);
        Assert.True(decision.SuspendWriting);
        Assert.Null(decision.ForcedRetentionDays);
        Assert.Contains("StopWriting", decision.Reason);
    }

    [Fact]
    public void SpaceRecovered_ReturnsToNormalRetention()
    {
        var supervisor = Create();

        supervisor.Evaluate(freeDiskMb: 100);
        supervisor.Evaluate(freeDiskMb: 100);
        Assert.Equal(DiskSpaceState.LowSpace, supervisor.State);

        var decision = supervisor.Evaluate(freeDiskMb: 20_000);

        // Место освободили — ограничения снимаются, глубина возвращается
        // к штатной, и следующее давление начнётся снова с 400 суток.
        Assert.Equal(DiskSpaceState.Normal, decision.State);
        Assert.False(decision.SuspendWriting);
        Assert.Null(decision.ForcedRetentionDays);
        Assert.Equal(200, supervisor.Evaluate(freeDiskMb: 100).ForcedRetentionDays);
    }

    [Fact]
    public void ExactlyAtThreshold_IsNormal()
    {
        var decision = Create().Evaluate(freeDiskMb: Threshold);

        Assert.Equal(DiskSpaceState.Normal, decision.State);
        Assert.False(decision.ShouldAlarm);
    }
}
