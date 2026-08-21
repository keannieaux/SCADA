using System.Globalization;
using System.Text;
using SCADA.Core.Schemes;
using SCADA.Core.Tags;
using SCADA.Expressions;

namespace SCADA.Graphics;

/// <summary>
/// Сборка строкового значения параметра действия в момент выполнения (C2).
/// Константа — как есть; ссылка на строковый тег — текущее значение тега;
/// шаблон — подстановка числовых выражений инвариантно ("Pump{N}"). Чтение
/// строковых тегов передано колбэком, чтобы резолвер не зависел от
/// IRuntimeClient и тестировался без него. Образец — MessageRenderer
/// (SCADA.Alarms): строка собирается хостом, ВМ остаётся числовой.
/// </summary>
public static class ActionParameterText
{
    /// <param name="readString">Чтение строкового тега (для StringTagRef);
    /// может быть null, если ссылки на строковые теги не ожидаются.</param>
    public static string Resolve(ResolvedActionParameter parameter,
        EvaluationContext context, Func<TagId, string>? readString = null)
        => parameter.Kind switch
        {
            ActionParamValueKind.Constant => parameter.Text ?? "",
            ActionParamValueKind.StringTagRef =>
                parameter.StringTagId is { } tagId && readString is not null
                    ? readString(tagId)
                    : "",
            ActionParamValueKind.Template => ResolveTemplate(parameter, context),
            _ => parameter.Text ?? ""
        };

    private static string ResolveTemplate(ResolvedActionParameter parameter,
        EvaluationContext context)
    {
        var literals = parameter.Literals;
        var placeholders = parameter.Placeholders;
        if (literals is null || placeholders is null)
            return parameter.Text ?? "";

        var builder = new StringBuilder(parameter.Text?.Length ?? 32);
        for (int i = 0; i < placeholders.Count; i++)
        {
            builder.Append(literals[i]);
            double value = ExpressionVM.Evaluate(placeholders[i], context);
            builder.Append(value.ToString(CultureInfo.InvariantCulture));
        }
        builder.Append(literals[^1]);
        return builder.ToString();
    }
}
