namespace SCADA.Expressions;

/// <summary>
/// Встроенная функция. Аргументы — срез стека ВМ (без копирований и аллокаций),
/// доступ к тегам/истории — через контекст.
/// </summary>
public delegate double BuiltinFunction(ReadOnlySpan<double> args, EvaluationContext context);
