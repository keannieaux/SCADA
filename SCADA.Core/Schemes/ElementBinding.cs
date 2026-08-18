namespace SCADA.Core.Schemes;

/// <summary>Режим отображения результата выражения на свойство (концепт §4.1).</summary>
public enum StopMapping : byte
{
    /// <summary>Значение выражения → свойство как есть (PositionX, Rotation…).</summary>
    Direct = 0,

    /// <summary>Стопы без интерполяции: пороги (warn/crit — частный случай).</summary>
    Discrete = 1,

    /// <summary>Линейная интерполяция между соседними стопами (труба
    /// синий→красный по температуре). Для цветов — lerp по каналам ARGB.</summary>
    Interpolated = 2,
}

/// <summary>Один стоп таблицы отображения: вход выражения → выход в свойство.</summary>
public readonly record struct Stop(double Input, PropertyValue Output);

/// <summary>
/// Привязка: выражение → свойство элемента (концепт §4). Анимируемо любое
/// свойство с флагом Animatable; без привязки свойство статично.
/// Текст выражения компилируется при сборке пакета в общий пул code.bin
/// (по образцу правил сигнализации, M5) — рантайм работает с индексами.
/// </summary>
public sealed class ElementBinding
{
    public required int PropertyId { get; init; }
    public required string Expression { get; init; }
    public StopMapping Mapping { get; init; } = StopMapping.Direct;
    public IReadOnlyList<Stop>? Stops { get; init; }

    /// <summary>Пересчитывать каждый тик независимо от эпох (анимации от
    /// времени: вращение, бегущая строка — концепт §4.3). Дорого: осознанно.</summary>
    public bool Volatile { get; init; }

    // --- заполняется при сборке пакета, в исходнике проекта пусто ---

    /// <summary>Индекс выражения в пуле code.bin (после дедупликации).</summary>
    public int? CompiledExpressionIndex { get; set; }

    /// <summary>Теги, читаемые выражением — для грязного пересчёта по эпохам.</summary>
    public int[]? CompiledTagIndices { get; set; }
}
