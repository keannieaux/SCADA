namespace SCADA.Expressions.Compiler;

/// <summary>
/// Фасад компилятора: текст → CompiledExpression.
/// Полный конвейер: лексер → парсер → биндер → эмиттер.
/// </summary>
public static class ExpressionCompiler
{
    public static CompiledExpression Compile(string text, ITagCatalog catalog)
        => Emitter.Emit(Binder.Bind(Parser.Parse(text), catalog));
}
