namespace SCADA.Core.Alarms;

/// <summary>Тип условия правила сигнализации (docs/M5-plan.md §2.3).</summary>
public enum AlarmType : byte
{
    /// <summary>Пороговые уставки HiHi/Hi/Lo/LoLo с гистерезисом.</summary>
    Threshold = 0,
    /// <summary>Произвольное условие на выражении §11. Дискретные аварии и
    /// отклонение от уставки выражаются через этот тип, отдельных типов нет.</summary>
    Expression = 1
}
