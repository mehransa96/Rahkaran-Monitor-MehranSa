using System.Globalization;
using System.Text;

namespace RSM.Core.Rules.Expressions;

internal enum TokenKind
{
    Number,
    String,
    Identifier,
    True,
    False,
    Null,
    In,
    Plus,
    Minus,
    Star,
    Slash,
    Percent,
    EqualEqual,
    NotEqual,
    Less,
    LessEqual,
    Greater,
    GreaterEqual,
    AndAnd,
    OrOr,
    Bang,
    Question,
    Colon,
    LeftParen,
    RightParen,
    LeftBracket,
    RightBracket,
    Comma,
    End,
}

internal readonly record struct Token(TokenKind Kind, string Text, int Position, double Number = 0);

/// <summary>
/// Tokenizer for rule conditions. Identifiers are fact paths such as <c>sql.config.maxdop</c> or
/// <c>db[Rahkaran].recovery_model</c> (an instance segment must follow the previous segment without spaces).
/// </summary>
internal static class Lexer
{
    public static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            var start = i;
            if (char.IsDigit(c) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
            {
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.' || text[i] == '_'))
                {
                    i++;
                }

                var raw = text[start..i].Replace("_", string.Empty, StringComparison.Ordinal);
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    throw new ExpressionException($"Invalid number '{raw}'", start);
                }

                tokens.Add(new Token(TokenKind.Number, raw, start, number));
                continue;
            }

            if (c is '"' or '\'')
            {
                tokens.Add(new Token(TokenKind.String, ReadString(text, ref i), start));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var path = ReadPath(text, ref i);
                var kind = path switch
                {
                    "true" => TokenKind.True,
                    "false" => TokenKind.False,
                    "null" => TokenKind.Null,
                    "in" => TokenKind.In,
                    _ => TokenKind.Identifier,
                };
                tokens.Add(new Token(kind, path, start));
                continue;
            }

            var two = i + 1 < text.Length ? text.Substring(i, 2) : string.Empty;
            (TokenKind Kind, int Length)? op = two switch
            {
                "==" => (TokenKind.EqualEqual, 2),
                "!=" => (TokenKind.NotEqual, 2),
                "<=" => (TokenKind.LessEqual, 2),
                ">=" => (TokenKind.GreaterEqual, 2),
                "&&" => (TokenKind.AndAnd, 2),
                "||" => (TokenKind.OrOr, 2),
                _ => null,
            };
            op ??= c switch
            {
                '+' => (TokenKind.Plus, 1),
                '-' => (TokenKind.Minus, 1),
                '*' => (TokenKind.Star, 1),
                '/' => (TokenKind.Slash, 1),
                '%' => (TokenKind.Percent, 1),
                '<' => (TokenKind.Less, 1),
                '>' => (TokenKind.Greater, 1),
                '!' => (TokenKind.Bang, 1),
                '?' => (TokenKind.Question, 1),
                ':' => (TokenKind.Colon, 1),
                '(' => (TokenKind.LeftParen, 1),
                ')' => (TokenKind.RightParen, 1),
                '[' => (TokenKind.LeftBracket, 1),
                ']' => (TokenKind.RightBracket, 1),
                ',' => (TokenKind.Comma, 1),
                _ => null,
            };

            if (op is null)
            {
                throw new ExpressionException($"Unexpected character '{c}'", i);
            }

            tokens.Add(new Token(op.Value.Kind, text.Substring(i, op.Value.Length), i));
            i += op.Value.Length;
        }

        tokens.Add(new Token(TokenKind.End, string.Empty, text.Length));
        return tokens;
    }

    private static string ReadString(string text, ref int i)
    {
        var quote = text[i++];
        var sb = new StringBuilder();
        while (i < text.Length && text[i] != quote)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i++;
            }

            sb.Append(text[i++]);
        }

        if (i >= text.Length)
        {
            throw new ExpressionException("Unterminated string", i);
        }

        i++; // closing quote
        return sb.ToString();
    }

    private static string ReadPath(string text, ref int i)
    {
        var sb = new StringBuilder();
        ReadIdentifier(text, ref i, sb);
        while (i < text.Length)
        {
            if (text[i] == '.' && i + 1 < text.Length && (char.IsLetter(text[i + 1]) || text[i + 1] == '_'))
            {
                sb.Append(text[i++]);
                ReadIdentifier(text, ref i, sb);
            }
            else if (text[i] == '[')
            {
                var close = text.IndexOf(']', i);
                if (close < 0)
                {
                    throw new ExpressionException("Unterminated instance segment", i);
                }

                sb.Append(text, i, close - i + 1);
                i = close + 1;
            }
            else
            {
                break;
            }
        }

        return sb.ToString();
    }

    private static void ReadIdentifier(string text, ref int i, StringBuilder sb)
    {
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
        {
            sb.Append(text[i++]);
        }
    }
}

public sealed class ExpressionException(string message, int position) : Exception($"{message} (at {position})")
{
    public int Position { get; } = position;
}
