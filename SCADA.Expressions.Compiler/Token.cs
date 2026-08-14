namespace SCADA.Expressions.Compiler;

public enum TokenKind
{
    Number, Identifier,
    Plus, Minus, Star, Slash,
    Greater, GreaterOrEqual, Less, LessOrEqual, EqualEqual, NotEqual,
    AndAnd, OrOr, Bang,
    Question, Colon,
    LeftParen, RightParen, Comma,
    EndOfInput
}

/// <summary>
/// Лексема: вид, исходный текст и позиция в строке выражения.
/// Позиция нужна для диагностики: «неожиданный символ на позиции 12»
/// в сто раз полезнее «ошибка разбора», а редактор (§11.9) подсветит ей место.
/// </summary>
public readonly record struct Token(TokenKind Kind, string Text, int Position);
