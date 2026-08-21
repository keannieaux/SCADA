namespace SCADA.Core.Schemes;

/// <summary>
/// Реестр типов действий (docs/scheme-controls-plan.md, C1) — единственный
/// источник истины: байтовый тип и проверка известности в секции пакета,
/// дискриминатор JSON исходников, валидация параметров при сборке и панель
/// действий редактора читают отсюда. Образец — ElementSchemas.
///
/// Правила расширения:
/// - новое действие = новый наследник SchemeAction + ОДНА запись здесь
///   (тест целостности ActionCatalogTests ловит пропуск);
/// - TypeCode занятых никогда не переиспользуется (пишется в пакет);
/// - сериализация параметров остаётся ручным switch в писателе секции
///   (уровень 2: метаданные без сериализации по метаданным — разнородные
///   параметры иначе породили бы мини-движок сериализации).
/// </summary>
public static class ActionCatalog
{
    private static readonly ActionDef[] All =
    [
        new(0, typeof(WriteTagAction), "WriteTag", "Записать значение в тег",
            ActionApplicability.All,
            [
                new("Tag", "Тег", ActionParamType.TagRef, Required: true,
                    GetValue: a => ((WriteTagAction)a).Tag),
                new("Value", "Значение", ActionParamType.Number, Required: true,
                    CanBeExpression: true, GetValue: a => ((WriteTagAction)a).Value),
            ]),

        new(1, typeof(ToggleTagAction), "ToggleTag", "Переключить тег (0/1)",
            ActionApplicability.All,
            [
                new("Tag", "Тег", ActionParamType.TagRef, Required: true,
                    GetValue: a => ((ToggleTagAction)a).Tag),
            ]),

        // навигационный стек — экранный: из попапа переход оформляется
        // парой ClosePopup + OpenScheme, а не одним действием
        new(2, typeof(OpenSchemeAction), "OpenScheme", "Открыть экран",
            ActionApplicability.Screen,
            [
                new("SchemeName", "Экран", ActionParamType.Text, Required: true,
                    GetValue: a => ((OpenSchemeAction)a).SchemeName),
                new("Parameters", "Параметры", ActionParamType.StringMap, Required: false,
                    GetValue: a => ((OpenSchemeAction)a).Parameters),
            ]),

        new(3, typeof(OpenPopupAction), "OpenPopup", "Открыть всплывающее окно",
            ActionApplicability.All,
            [
                new("TemplateName", "Шаблон", ActionParamType.Text, Required: true,
                    GetValue: a => ((OpenPopupAction)a).TemplateName),
                new("Parameters", "Параметры", ActionParamType.StringMap, Required: false,
                    GetValue: a => ((OpenPopupAction)a).Parameters),
            ]),

        new(4, typeof(ClosePopupAction), "ClosePopup", "Закрыть всплывающее окно",
            ActionApplicability.Popup, []),

        new(5, typeof(BackAction), "Back", "Назад (история переходов)",
            ActionApplicability.Screen, []),

        new(6, typeof(ShowDialogAction), "ShowDialog", "Показать сообщение",
            ActionApplicability.All,
            [
                new("Message", "Текст", ActionParamType.Text, Required: true,
                    GetValue: a => ((ShowDialogAction)a).Message),
            ]),
    ];

    private static readonly Dictionary<byte, ActionDef> ByCode = All.ToDictionary(d => d.TypeCode);
    private static readonly Dictionary<Type, ActionDef> ByType = All.ToDictionary(d => d.ClrType);
    private static readonly Dictionary<string, ActionDef> ByJsonName =
        All.ToDictionary(d => d.JsonName, StringComparer.Ordinal);

    /// <summary>Все зарегистрированные действия — для тестов целостности
    /// и панели действий редактора.</summary>
    public static IReadOnlyList<ActionDef> Actions => All;

    /// <summary>Модификаторы базового класса SchemeAction — общие для всех
    /// действий. Редактор рисует их одинаковой группой у любого действия,
    /// не дублируя в каждой записи каталога.</summary>
    public static IReadOnlyList<ActionParamDef> CommonParams { get; } =
    [
        new("Condition", "Условие (выполнять, если…)", ActionParamType.Text,
            Required: false, CanBeExpression: true),
        new("Confirmation", "Подтверждение", ActionParamType.Text, Required: false),
        new("RequiredRight", "Требуемое право", ActionParamType.Text, Required: false),
        new("DeniedFeedback", "Реакция на отказ", ActionParamType.Text, Required: false),
    ];

    /// <summary>Известен ли байт типа (читатель секции: неизвестный тип
    /// от пакета поновее пропускается по длине записи, §5.3).</summary>
    public static bool IsKnown(byte typeCode) => ByCode.ContainsKey(typeCode);

    public static ActionDef? Find(byte typeCode)
        => ByCode.GetValueOrDefault(typeCode);

    public static ActionDef? Find(Type clrType)
        => ByType.GetValueOrDefault(clrType);

    /// <summary>По дискриминатору "type" исходника схемы ("WriteTag").</summary>
    public static ActionDef? FindByJsonName(string jsonName)
        => ByJsonName.GetValueOrDefault(jsonName);

    /// <summary>Байт типа для писателя секции. Незарегистрированное действие —
    /// исключение: такого быть не может после теста целостности.</summary>
    public static byte TypeCodeFor(SchemeAction action)
        => ByType.TryGetValue(action.GetType(), out var def)
            ? def.TypeCode
            : throw new InvalidOperationException(
                $"Действие {action.GetType().Name} не зарегистрировано в ActionCatalog");

    /// <summary>Ссылки на теги из TagRef-параметров действия — для валидации
    /// сборки (существование тегов, параметрические ссылки) без hardcoded
    /// switch по типам действий.</summary>
    public static IEnumerable<SchemeTagRef> TagRefsOf(SchemeAction action)
    {
        if (!ByType.TryGetValue(action.GetType(), out var def))
            yield break;
        foreach (var param in def.Params)
            if (param.Type == ActionParamType.TagRef &&
                param.GetValue?.Invoke(action) is SchemeTagRef tagRef)
                yield return tagRef;
    }
}
