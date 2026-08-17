using SCADA.Core.Alarms;
using SCADA.Core.Tags;

namespace SCADA.Alarms;

/// <summary>
/// Движок правил сигнализации (docs/M5-plan.md §7). Вычисляет состояния по
/// изменениям тегов, генерирует события журнала и держит активные аварии.
/// События возвращаются с Id = default — первичный ключ присваивает журнал.
/// Потокобезопасен: вызывается из конвейера опроса и из UI (квитирование).
/// </summary>
public interface IAlarmEngine
{
    /// <summary>Пересчитать правила, зависящие от изменившегося тега.</summary>
    IReadOnlyList<AlarmEvent> EvaluateTag(TagId tag, long nowUtcMs);

    /// <summary>Проверить отложенные по MinDuration фронты. Вызывается
    /// периодически конвейером, даже если теги не менялись.</summary>
    IReadOnlyList<AlarmEvent> Tick(long nowUtcMs);

    /// <summary>Пересчитать все правила по текущим значениям. Вызывается один
    /// раз после RestoreRecovered — свести восстановленное состояние журнала
    /// с фактическим состоянием тегов (§7.3).</summary>
    IReadOnlyList<AlarmEvent> EvaluateAll(long nowUtcMs);

    /// <summary>Принять состояния, восстановленные из журнала при старте
    /// (AlarmStateRecovery). События в журнал при этом НЕ пишутся —
    /// они там уже есть.</summary>
    void RestoreRecovered(IEnumerable<RecoveredAlarmState> states);

    /// <summary>Квитирование. null — авария не найдена или не требует квитирования
    /// в текущем состоянии.</summary>
    AlarmEvent? Acknowledge(string ruleName,
        string acknowledgedBy, string? comment, long nowUtcMs);

    /// <summary>Активные и ожидающие квитирования аварии для баннера.</summary>
    IReadOnlyList<ActiveAlarm> GetActive(AlarmFilter filter);

    /// <summary>Авария правила активна (условие истинно). Для alarm() на мнемосхемах —
    /// дешёвый lookup, рендер не дублирует условие (§11).</summary>
    bool IsActive(string ruleName);

    /// <summary>Текущее представление правила (для рассылки изменений
    /// подписчикам). null — такого правила нет.</summary>
    ActiveAlarm? GetAlarm(string ruleName);
}
