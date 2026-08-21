namespace SCADA.Core.Schemes;

/// <summary>
/// Контексты, в которых действие имеет смысл (docs/scheme-controls-plan.md,
/// C1, решение 2). Флаги: действие может быть применимо в нескольких
/// контекстах (WriteTag — везде), а может в одном (ClosePopup — только
/// в попапе). Поле — фильтр панели действий редактора и warning при сборке,
/// не жёсткая гарантия: шаблон может быть и экземпляром, и попапом.
/// </summary>
[Flags]
public enum ActionApplicability
{
    /// <summary>События обычного экрана и его элементов.</summary>
    Screen = 1,

    /// <summary>События шаблона, открытого как всплывающее окно.</summary>
    Popup = 2,

    /// <summary>События шаблона, вставленного экземпляром в экран.</summary>
    Template = 4,

    All = Screen | Popup | Template
}
