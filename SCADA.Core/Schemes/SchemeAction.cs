namespace SCADA.Core.Schemes;

/// <summary>
/// Действие по событию элемента (концепт §5). Цепочка исполняется
/// последовательно; у каждого действия опциональное условие-выражение.
/// Подтверждение — модификатор действия, а не отдельный шаг цепочки.
/// Новые типы действий (ApplyRecipe, ExportCsv, CallIntegration…) — новые
/// наследники; в пакете тип — байт, неизвестный при чтении пропускается.
/// </summary>
public abstract record SchemeAction
{
    /// <summary>Условие-выражение: действие выполняется, только если ≠ 0.</summary>
    public string? Condition { get; init; }

    /// <summary>Текст подтверждения перед выполнением. Стыкуется с
    /// TagDefinition.RequiresWriteConfirmation (M7): тег может требовать
    /// подтверждение сам, действие — дополнительно.</summary>
    public string? Confirmation { get; init; }

    /// <summary>Право, без которого действие не выполняется
    /// (docs/users-plan.md §5). null — ограничений нет. Право на элементе
    /// и право на действии проверяются оба: разрешение видеть кнопку
    /// не означает разрешения ею пользоваться.</summary>
    public string? RequiredRight { get; init; }

    /// <summary>Что показать оператору при отказе. Значимо только при
    /// заданном <see cref="RequiredRight"/>.</summary>
    public DeniedFeedback DeniedFeedback { get; init; } = DeniedFeedback.Notify;

    // --- заполняется при сборке пакета ---
    public int? CompiledConditionIndex { get; set; }
    public int[]? CompiledConditionTagIndices { get; set; }
}

/// <summary>Запись в тег. Идёт через IRuntimeClient.WriteTagsAsync —
/// batch, аудит (ТЗ §13), подтверждения (M7). Не через системный WriteLocal.</summary>
public sealed record WriteTagAction(SchemeTagRef Tag, double Value) : SchemeAction
{
    /// <summary>Значение-выражение (C2): задано — вычисляется в момент
    /// выполнения, а позиционный Value игнорируется. Оба заданы — ошибка
    /// сборки. Покрывает Increase/Decrease/CopyTag одним типом действия:
    /// "Тег + 1", "ДругойТег", "Уставка * 0.9".</summary>
    public string? ValueExpression { get; init; }

    // --- заполняются при сборке пакета ---
    public int? CompiledValueIndex { get; set; }
    public int[]? CompiledValueTagIndices { get; set; }
}

public sealed record ToggleTagAction(SchemeTagRef Tag) : SchemeAction;

/// <summary>Переход на схему с опциональными параметрами (параметризованный экран).</summary>
public sealed record OpenSchemeAction(string SchemeName,
    IReadOnlyDictionary<string, string>? Parameters = null) : SchemeAction
{
    /// <summary>Compiled-формы значений Parameters (C2) — заполняются при сборке.</summary>
    public List<CompiledActionParameter>? CompiledParameters { get; set; }
}

/// <summary>Попап — внутренний модальный оверлей, не окно ОС (концепт §6).</summary>
public sealed record OpenPopupAction(string TemplateName,
    IReadOnlyDictionary<string, string>? Parameters = null) : SchemeAction
{
    /// <summary>Compiled-формы значений Parameters (C2) — заполняются при сборке.</summary>
    public List<CompiledActionParameter>? CompiledParameters { get; set; }
}

public sealed record ClosePopupAction : SchemeAction;

/// <summary>Назад по стеку истории переходов.</summary>
public sealed record BackAction : SchemeAction;

public sealed record ShowDialogAction(string Message) : SchemeAction;

/// <summary>
/// C5: задать свойство элемента (docs/scheme-controls-plan.md). Для состояний
/// без памяти и без индикации: сбросить масштаб, снять выделение, свернуть
/// панель. Не альтернатива тегам — второй инструмент.
///
/// Адресация — по имени элемента <b>в границах своей схемы</b>, а внутри тела
/// шаблона — в границах экземпляра. Дотянуться до элемента чужой схемы нельзя
/// намеренно: сборка проверила бы имя, но не то, открыта ли та схема сейчас,
/// и промах выглядел бы как кнопка, которая иногда не работает. Состояние,
/// живущее между схемами, — это сессионный тег: у него есть память, и он не
/// зависит от того, что на экране.
///
/// Значение — константа <see cref="Value"/> или <see cref="ValueExpression"/>,
/// как у WriteTag (C2); заданы оба или ни одного — ошибка. Выражение допустимо
/// только для числовых по существу свойств (Number/Boolean/Choice): строк в ВМ
/// нет, а вычисляемый цвет — это привязка со стопами, где цвета названы и
/// участвуют в теме (§11.3).
/// </summary>
public sealed record SetPropertyAction(string ElementName, int PropertyId,
    PropertyValue? Value = null) : SchemeAction
{
    /// <summary>Значение-выражение; задано — Value должно быть пустым.</summary>
    public string? ValueExpression { get; init; }

    // --- заполняются при сборке пакета ---
    public int? CompiledValueIndex { get; set; }
    public int[]? CompiledValueTagIndices { get; set; }
}
