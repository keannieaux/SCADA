namespace SCADA.Historian;

/// <summary>
/// Кодек значений внутри блока (docs/archive-format.md §8.4, биты 0–3).
/// </summary>
public enum ValueCodec : byte
{
    ScaledInt = 0,
    GorillaXor = 1,
    Discrete = 2
}
