namespace SCADA.Expressions.Compiler.Tests;

public class LexerTests
{
    private static IReadOnlyList<Token> Tokenize(string text) => Lexer.Tokenize(text);

    private static TokenKind[] Kinds(IReadOnlyList<Token> tokens)
        => tokens.Select(t => t.Kind).ToArray();

    [Fact]
    public void Tokenize_SimpleComparison_ProducesExpectedTokens()
    {
        var tokens = Tokenize("Temp > 80");

        Assert.Equal(
            [TokenKind.Identifier, TokenKind.Greater, TokenKind.Number, TokenKind.EndOfInput],
            Kinds(tokens));
        Assert.Equal("Temp", tokens[0].Text);
        Assert.Equal("80", tokens[2].Text);
    }

    [Fact]
    public void Tokenize_TwoCharOperators_AreSingleToken()
    {
        var tokens = Tokenize("a>=b <= c==d != e&&f || !g");

        Assert.Equal(
            [TokenKind.Identifier, TokenKind.GreaterOrEqual,
             TokenKind.Identifier, TokenKind.LessOrEqual,
             TokenKind.Identifier, TokenKind.EqualEqual,
             TokenKind.Identifier, TokenKind.NotEqual,
             TokenKind.Identifier, TokenKind.AndAnd,
             TokenKind.Identifier, TokenKind.OrOr,
             TokenKind.Bang, TokenKind.Identifier,
             TokenKind.EndOfInput],
            Kinds(tokens));
    }

    [Fact]
    public void Tokenize_DottedTagName_ReadsAsOneIdentifier()
    {
        var tokens = Tokenize("Boiler1.Temp > 80");

        Assert.Equal("Boiler1.Temp", tokens[0].Text);
        Assert.Equal(TokenKind.Identifier, tokens[0].Kind);
    }

    [Fact]
    public void Tokenize_AllConstructs_ProducesExpectedSequence()
    {
        // IsGood(Boiler1.Temp) && (Tank1.Level / 100) ? 1 : 2
        var tokens = Tokenize("IsGood(Boiler1.Temp) && (Tank1.Level / 100) ? 1 : 2");

        Assert.Equal(
            [TokenKind.Identifier, TokenKind.LeftParen, TokenKind.Identifier, TokenKind.RightParen,
             TokenKind.AndAnd,
             TokenKind.LeftParen, TokenKind.Identifier, TokenKind.Slash, TokenKind.Number, TokenKind.RightParen,
             TokenKind.Question, TokenKind.Number, TokenKind.Colon, TokenKind.Number,
             TokenKind.EndOfInput],
            Kinds(tokens));
    }

    [Fact]
    public void Tokenize_SingleEquals_ThrowsWithPosition()
    {
        // одиночное = в языке нет — только ==
        var ex = Assert.Throws<ExpressionCompileException>(() => Tokenize("Temp = 80"));

        Assert.Contains("5", ex.Message); // позиция символа '='
    }

    [Fact]
    public void Tokenize_UnknownChar_ThrowsWithPosition()
    {
        var ex = Assert.Throws<ExpressionCompileException>(() => Tokenize("a + #"));

        Assert.Contains("#", ex.Message);
        Assert.Contains("4", ex.Message);
    }

    [Fact]
    public void Tokenize_TracksPositions()
    {
        var tokens = Tokenize("a +  b");

        Assert.Equal(0, tokens[0].Position); // a
        Assert.Equal(2, tokens[1].Position); // +
        Assert.Equal(5, tokens[2].Position); // b
    }

    [Fact]
    public void Tokenize_DecimalNumber_ReadsWhole()
    {
        var tokens = Tokenize("2.5 * x");

        Assert.Equal("2.5", tokens[0].Text);
        Assert.Equal(TokenKind.Number, tokens[0].Kind);
    }

    [Fact]
    public void Tokenize_EmptyInput_ReturnsOnlyEndOfInput()
    {
        var tokens = Tokenize("   ");

        Assert.Single(tokens);
        Assert.Equal(TokenKind.EndOfInput, tokens[0].Kind);
    }

    [Fact]
    public void Tokenize_SystemTagName_LeadingAt_ReadsAsOneIdentifier()
    {
        // системные теги (@Alarm.…/@AlarmGroup.…, концепт §10) — обычные
        // идентификаторы с ведущим '@'
        var tokens = Tokenize("@AlarmGroup.Цех2.Секция5.AnyUnacked > 0");

        Assert.Equal(
            [TokenKind.Identifier, TokenKind.Greater, TokenKind.Number, TokenKind.EndOfInput],
            Kinds(tokens));
        Assert.Equal("@AlarmGroup.Цех2.Секция5.AnyUnacked", tokens[0].Text);
    }
}
