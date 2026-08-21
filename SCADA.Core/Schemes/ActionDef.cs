namespace SCADA.Core.Schemes;

/// <summary>
/// Дескриптор типа действия (docs/scheme-controls-plan.md, C1).
/// Каталог описывает то, что есть в коде (решение 4): нет записей про
/// планируемые действия, нет колонки версий — планы живут в документе.
/// </summary>
/// <param name="TypeCode">Байт типа в секции пакета. СТАБИЛЕН: новые действия
/// получают новые коды, занятые не переиспользуются — как id свойств в
/// ElementSchemas.</param>
/// <param name="ClrType">CLR-тип наследника SchemeAction.</param>
/// <param name="JsonName">Дискриминатор "type" в исходниках schemes/*.scheme
/// ("WriteTag"). Держится рядом с TypeCode, чтобы четыре представления
/// действия (CLR, JSON, байт, имя) описывались одной записью.</param>
/// <param name="DisplayName">Отображаемое имя по-русски (решение 1).</param>
/// <param name="Applicability">Где действие имеет смысл (решение 2).</param>
/// <param name="Params">Собственные параметры действия. Модификаторы базового
/// класса (условие, подтверждение, право) — в ActionCatalog.CommonParams.</param>
public sealed record ActionDef(
    byte TypeCode,
    Type ClrType,
    string JsonName,
    string DisplayName,
    ActionApplicability Applicability,
    IReadOnlyList<ActionParamDef> Params);
