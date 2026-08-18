namespace SCADA.Core.Schemes;

/// <summary>
/// Ссылка на тег из элемента схемы (концепт §4.4). Абсолютная — имя тега,
/// резолвится в TagId при сборке пакета (раннее связывание, ТЗ §11.6).
/// Параметрическая — имя параметра шаблона ("{$Prefix}.Скорость" расписывается
/// как параметр + суффикс), резолвится при раскрытии экземпляра.
/// </summary>
public readonly record struct SchemeTagRef(string Name, bool IsParametric)
{
    public static SchemeTagRef Absolute(string tagName) => new(tagName, false);

    /// <summary>Ссылка на параметр шаблона: "{Prefix}.Скорость" → ("Prefix", ".Скорость").</summary>
    public static SchemeTagRef Parametric(string parameterName, string suffix)
        => new(parameterName + suffix, true);
}
