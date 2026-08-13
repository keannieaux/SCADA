using System.Globalization;

namespace SCADA.Expressions.Compiler;

/// <summary>
/// Пратт-парсер: приоритет операторов задаётся числом (таблица BindingPower),
/// а не лесенкой методов. Новый оператор = новый токен + одна строка в таблице.
/// </summary>
public sealed class Parser
{
    private readonly IReadOnlyList<Token> _tokens;
    private int _pos;

    private Parser(IReadOnlyList<Token> tokens) => _tokens = tokens;

    public static Node Parse(string text)
    {
        var parser = new Parser(Lexer.Tokenize(text));
        var node = parser.ParseExpression(0);
        if (parser.Current.Kind != TokenKind.EndOfInput)
            throw new ExpressionCompileException(
                $"Неожиданный токен '{parser.Current.Text}' на позиции {parser.Current.Position}");
        return node;
    }

    // сила связывания: чем больше число, тем крепче оператор держит операнды.
    // 0 — токен не является бинарным оператором (останавливает цикл разбора)
    private static int BindingPower(TokenKind kind) => kind switch
    {
        TokenKind.OrOr => 1,
        TokenKind.AndAnd => 2,
        TokenKind.EqualEqual or TokenKind.NotEqual => 3,
        TokenKind.Less or TokenKind.LessOrEqual
            or TokenKind.Greater or TokenKind.GreaterOrEqual => 4,
        TokenKind.Plus or TokenKind.Minus => 5,
        TokenKind.Star or TokenKind.Slash => 6,
        _ => 0
    };

    private Token Current => _tokens[_pos];
    private Token Next() => _tokens[_pos++];

    private Node ParseExpression(int minBindingPower)
    {
        var left = ParsePrimary();

        while (true)
        {
            // тернарник — трёхместный инфикс с самым низким приоритетом
            if (Current.Kind == TokenKind.Question && minBindingPower == 0)
            {
                var question = Next();
                var whenTrue = ParseExpression(0);
                Expect(TokenKind.Colon);
                var whenFalse = ParseExpression(0);
                left = new ConditionalNode(left, whenTrue, whenFalse, question.Position);
                continue;
            }

            int power = BindingPower(Current.Kind);
            if (power == 0 || power < minBindingPower)
                break; // оператор слабее нас (или не оператор) — отдаём наверх

            var op = Next();
            // правая часть забирает только операторы строго сильнее текущего
            var right = ParseExpression(power + 1);
            left = new BinaryNode(op.Kind, left, right, op.Position);
        }

        return left;
    }

    private Node ParsePrimary()
    {
        var token = Next();
        switch (token.Kind)
        {
            case TokenKind.Number:
                return new NumberNode(
                    double.Parse(token.Text, CultureInfo.InvariantCulture)); // точка, не запятая

            case TokenKind.Identifier:
                return Current.Kind == TokenKind.LeftParen
                    ? ParseCall(token)
                    : new TagRefNode(token.Text, token.Position);

            case TokenKind.LeftParen:
                var inner = ParseExpression(0);
                Expect(TokenKind.RightParen);
                return inner;

            case TokenKind.Bang:
            case TokenKind.Minus:
                return new UnaryNode(token.Kind, ParsePrimary(), token.Position);

            case TokenKind.EndOfInput:
                throw new ExpressionCompileException(
                    $"Неожиданный конец выражения на позиции {token.Position}");

            default:
                throw new ExpressionCompileException(
                    $"Ожидалось число, имя или '(', получено '{token.Text}' на позиции {token.Position}");
        }
    }

    private CallNode ParseCall(Token name)
    {
        Expect(TokenKind.LeftParen);
        var args = new List<Node>();

        if (Current.Kind != TokenKind.RightParen)
        {
            args.Add(ParseExpression(0));
            while (Current.Kind == TokenKind.Comma)
            {
                Next();
                args.Add(ParseExpression(0));
            }
        }

        Expect(TokenKind.RightParen);
        return new CallNode(name.Text, args, name.Position);
    }

    private Token Expect(TokenKind kind)
    {
        if (Current.Kind != kind)
            throw new ExpressionCompileException(
                $"Ожидался {kind}, получен '{Current.Text}' на позиции {Current.Position}");
        return Next();
    }
}
