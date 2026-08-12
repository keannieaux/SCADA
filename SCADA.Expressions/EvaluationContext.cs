using SCADA.Core.Tags;

namespace SCADA.Expressions;

/// <summary>
/// Всё, к чему выражение имеет доступ при вычислении.
/// Растёт членами (Historian для RateOfChange в M5, текущее время и т.д.) —
/// сигнатуры ВМ и builtin-функций при этом НЕ меняются.
/// </summary>
public sealed class EvaluationContext
{
    public required ITagTable Tags { get; init; }

    // M5: public IHistorian? Historian { get; init; } — когда появится RateOfChange.
    // IHistorian тогда переедет в Core, как ITagTable, — иначе ссылка зациклится.
}
