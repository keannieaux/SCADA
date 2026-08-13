namespace SCADA.Expressions.Compiler.Tests;

public class ParserTests
{
    private static Node Parse(string text) => Parser.Parse(text);

    [Fact]
    public void Parse_MultiplicationBindsTighterThanPlus()
    {
        // 2 + 3 * 4  →  2 + (3 * 4)
        var node = Parse("2 + 3 * 4");

        var add = Assert.IsType<BinaryNode>(node);
        Assert.Equal(TokenKind.Plus, add.Op);
        Assert.Equal(2.0, Assert.IsType<NumberNode>(add.Left).Value);

        var mul = Assert.IsType<BinaryNode>(add.Right);
        Assert.Equal(TokenKind.Star, mul.Op);
        Assert.Equal(3.0, Assert.IsType<NumberNode>(mul.Left).Value);
        Assert.Equal(4.0, Assert.IsType<NumberNode>(mul.Right).Value);
    }

    [Fact]
    public void Parse_Parentheses_OverridePrecedence()
    {
        // (2 + 3) * 4  →  (2 + 3) * 4
        var node = Parse("(2 + 3) * 4");

        var mul = Assert.IsType<BinaryNode>(node);
        Assert.Equal(TokenKind.Star, mul.Op);
        Assert.Equal(TokenKind.Plus, Assert.IsType<BinaryNode>(mul.Left).Op);
    }

    [Fact]
    public void Parse_LeftAssociativity()
    {
        // 10 - 4 - 3  →  (10 - 4) - 3
        var node = Parse("10 - 4 - 3");

        var outer = Assert.IsType<BinaryNode>(node);
        Assert.Equal(3.0, Assert.IsType<NumberNode>(outer.Right).Value);
        var inner = Assert.IsType<BinaryNode>(outer.Left);
        Assert.Equal(10.0, Assert.IsType<NumberNode>(inner.Left).Value);
    }

    [Fact]
    public void Parse_ComparisonAndLogic()
    {
        // a > 80 && b — && слабее сравнения
        var node = Parse("a > 80 && b");

        var and = Assert.IsType<BinaryNode>(node);
        Assert.Equal(TokenKind.AndAnd, and.Op);
        Assert.Equal(TokenKind.Greater, Assert.IsType<BinaryNode>(and.Left).Op);
        Assert.Equal("b", Assert.IsType<TagRefNode>(and.Right).Name);
    }

    [Fact]
    public void Parse_FunctionCall_WithArgs()
    {
        var node = Parse("Clamp(x, 0, 100)");

        var call = Assert.IsType<CallNode>(node);
        Assert.Equal("Clamp", call.Name);
        Assert.Equal(3, call.Args.Count);
        Assert.Equal("x", Assert.IsType<TagRefNode>(call.Args[0]).Name);
    }

    [Fact]
    public void Parse_Ternary()
    {
        var node = Parse("Valve1.Open ? 1 : 0");

        var cond = Assert.IsType<ConditionalNode>(node);
        Assert.Equal("Valve1.Open", Assert.IsType<TagRefNode>(cond.Condition).Name);
        Assert.Equal(1.0, Assert.IsType<NumberNode>(cond.WhenTrue).Value);
        Assert.Equal(0.0, Assert.IsType<NumberNode>(cond.WhenFalse).Value);
    }

    [Fact]
    public void Parse_UnaryNot()
    {
        var node = Parse("!Pump1.Running");

        var not = Assert.IsType<UnaryNode>(node);
        Assert.Equal(TokenKind.Bang, not.Op);
        Assert.Equal("Pump1.Running", Assert.IsType<TagRefNode>(not.Operand).Name);
    }

    [Fact]
    public void Parse_UnaryMinus_AppliesToOperandOnly()
    {
        // -2 * 3  →  (-2) * 3
        var node = Parse("-2 * 3");

        var mul = Assert.IsType<BinaryNode>(node);
        var minus = Assert.IsType<UnaryNode>(mul.Left);
        Assert.Equal(TokenKind.Minus, minus.Op);
    }

    [Fact]
    public void Parse_TruncatedExpression_Throws()
    {
        Assert.Throws<ExpressionCompileException>(() => Parse("Temp >"));
    }

    [Fact]
    public void Parse_TrailingTokens_Throws()
    {
        Assert.Throws<ExpressionCompileException>(() => Parse("2 3"));
    }

    [Fact]
    public void Parse_UnclosedParen_Throws()
    {
        Assert.Throws<ExpressionCompileException>(() => Parse("(2 + 3"));
    }
}
