using SCADA.Core.Tags;

namespace SCADA.Expressions;

/// <summary>
/// Всё, к чему выражение имеет доступ при вычислении.
/// Растёт членами (Historian для RateOfChange в M5, текущее время и т.д.) —
/// сигнатуры ВМ и builtin-функций при этом НЕ меняются.
/// </summary>
public sealed class EvaluationContext
{
    // Тип — намеренно узкий ITagValueReader (один Read): тогда схемы и панели
    // вычисляют выражения через IRuntimeClient и не зависят от ITagTable —
    // внутренней таблицы движка, которой нет в remote-варианте (ТЗ §12).
    public required ITagValueReader Tags { get; init; }

    // M5: public IHistorian? Historian { get; init; } — когда появится RateOfChange.
    // IHistorian тогда переедет в Core, как ITagTable, — иначе ссылка зациклится.
}
