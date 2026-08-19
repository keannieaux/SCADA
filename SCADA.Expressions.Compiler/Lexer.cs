namespace SCADA.Expressions.Compiler;

public static class Lexer
{
    public static IReadOnlyList<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        int pos = 0;

        while (pos < text.Length)
        {
            char c = text[pos];

            if (char.IsWhiteSpace(c)) { pos++; continue; }

            if (char.IsDigit(c))
            {
                tokens.Add(ReadNumber(text, ref pos));
                continue;
            }

            if (char.IsLetter(c) || c == '_' || c == '@')
            {
                tokens.Add(ReadIdentifier(text, ref pos));
                continue;
            }

            tokens.Add(ReadOperator(text, ref pos)); // кидает, если символ незнаком
        }

        tokens.Add(new Token(TokenKind.EndOfInput, "", text.Length));
        return tokens;
    }

    private static Token ReadNumber(string text, ref int pos)
    {
        int start = pos;
        while (pos < text.Length && (char.IsDigit(text[pos]) || text[pos] == '.'))
            pos++;
        return new Token(TokenKind.Number, text[start..pos], start);
    }

    private static Token ReadIdentifier(string text, ref int pos)
    {
        int start = pos;
        // имя тега: буквы, цифры, точки, подчёркивания — Boiler1.Temp читается
        // целиком; ведущий '@' — системные теги (@Alarm.…, @AlarmGroup.…, §10)
        if (text[pos] == '@')
            pos++;
        while (pos < text.Length && (char.IsLetterOrDigit(text[pos]) || text[pos] is '.' or '_'))
            pos++;
        return new Token(TokenKind.Identifier, text[start..pos], start);
    }

    private static Token ReadOperator(string text, ref int pos)
    {
        int start = pos;
        char c = text[pos++];

        // СНАЧАЛА двухсимвольные, ПОТОМ односимвольные:
        // ">=" не должно распасться на ">" + "="
        TokenKind kind = c switch
        {
            '>' when Match(text, ref pos, '=') => TokenKind.GreaterOrEqual,
            '<' when Match(text, ref pos, '=') => TokenKind.LessOrEqual,
            '=' when Match(text, ref pos, '=') => TokenKind.EqualEqual,
            '!' when Match(text, ref pos, '=') => TokenKind.NotEqual,
            '&' when Match(text, ref pos, '&') => TokenKind.AndAnd,
            '|' when Match(text, ref pos, '|') => TokenKind.OrOr,

            '+' => TokenKind.Plus,
            '-' => TokenKind.Minus,
            '*' => TokenKind.Star,
            '/' => TokenKind.Slash,
            '>' => TokenKind.Greater,
            '<' => TokenKind.Less,
            '!' => TokenKind.Bang,
            '?' => TokenKind.Question,
            ':' => TokenKind.Colon,
            '(' => TokenKind.LeftParen,
            ')' => TokenKind.RightParen,
            ',' => TokenKind.Comma,

            _ => throw new ExpressionCompileException(
                $"Неожиданный символ '{c}' на позиции {start}")
        };
        return new Token(kind, text[start..pos], start);
    }

    // если следующий символ == expected, съедает его и возвращает true
    private static bool Match(string text, ref int pos, char expected)
    {
        if (pos < text.Length && text[pos] == expected) { pos++; return true; }
        return false;
    }
}
