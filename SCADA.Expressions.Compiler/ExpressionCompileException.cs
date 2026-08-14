namespace SCADA.Expressions.Compiler;

public sealed class ExpressionCompileException : Exception
{
    public ExpressionCompileException(string message)
        : base(message)
    {
        Errors = [new CompileError(message, -1)];
    }

    public ExpressionCompileException(IReadOnlyList<CompileError> errors)
        : base("Ошибки компиляции выражения:\n" +
               string.Join("\n", errors.Select(e =>
                   e.Position >= 0 ? $"{e.Message} (позиция {e.Position})" : e.Message)))
    {
        Errors = errors;
    }

    public IReadOnlyList<CompileError> Errors { get; }
}
