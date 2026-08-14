using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("SCADA.Runtime.Tests")]

// Бенчмаркам нужны пошаговые методы конвейера (ProcessTick/FlushPending):
// мерить надо тик, а не фоновую службу с таймером. Публичными их делать
// незачем — снаружи конвейер обязан оставаться самоходным (§3).
[assembly: InternalsVisibleTo("SCADA.Benchmarks")]
