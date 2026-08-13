namespace SCADA.Expressions.Compiler;

/// <summary>
/// Ошибка компиляции с позицией в исходном тексте.
/// Позиция -1 — ошибка не привязана к месту (например, из лексера-фасада).
/// </summary>
public sealed record CompileError(string Message, int Position);
