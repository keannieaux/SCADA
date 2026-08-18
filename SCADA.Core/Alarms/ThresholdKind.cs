namespace SCADA.Core.Alarms;

/// <summary>Уставка порогового правила. Значения упорядочены по рангу:
/// HiHi &gt; Hi &gt; Lo &gt; LoLo — валидатор проверяет строгое убывание.</summary>
public enum ThresholdKind : byte
{
    LoLo = 0,
    Lo = 1,
    Hi = 2,
    HiHi = 3
}
