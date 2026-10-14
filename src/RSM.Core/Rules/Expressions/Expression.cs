using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RSM.Core.Rules.Expressions;

/// <summary>Resolves fact paths during evaluation.</summary>
public interface IEvaluationScope
{
    bool TryResolve(string path, out object? value);

    DateTimeOffset Now { get; }
}

/// <summary>Thrown when a condition references a fact that was not collected; the rule becomes indeterminate.</summary>
public sealed class MissingFactException(string path) : Exception($"Missing fact '{path}'")
{
    public string Path { get; } = path;
}

/// <summary>
/// A parsed rule condition. The language is deliberately tiny: literals, fact paths, arithmetic, comparisons,
/// boolean logic, <c>in [..]</c>, <c>?:</c> and a fixed function set. No reflection, no method calls, no I/O —
/// a rule cannot execute anything.
/// </summary>
public sealed class Expression
{
    private readonly Node _root;

    private Expression(string text, Node root)
    {
        Text = text;
        _root = root;
    }

    public string Text { get; }

    /// <summary>All fact paths referenced by the expression (for evidence and "missing fact" reporting).</summary>
    public IReadOnlyCollection<string> ReferencedPaths => _root.Paths().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static Expression Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parser = new Parser(Lexer.Tokenize(text));
        var root = parser.ParseExpression();
        parser.Expect(TokenKind.End);
        return new Expression(text, root);
    }

    public object? Evaluate(IEvaluationScope scope) => _root.Eval(scope);

    public bool EvaluateBool(IEvaluationScope scope) => Values.ToBool(Evaluate(scope));

    // ---------------------------------------------------------------- AST

    private abstract class Node
    {
        public abstract object? Eval(IEvaluationScope scope);

        public virtual IEnumerable<string> Paths() => [];
    }

    private sealed class Literal(object? value) : Node
    {
        public override object? Eval(IEvaluationScope scope) => value;
    }

    private sealed class PathRef(string path) : Node
    {
        public string Path { get; } = path;

        public override object? Eval(IEvaluationScope scope) =>
            scope.TryResolve(Path, out var value) ? Values.Normalize(value) : throw new MissingFactException(Path);

        public override IEnumerable<string> Paths() => [Path];
    }

    private sealed class ListNode(IReadOnlyList<Node> items) : Node
    {
        public override object? Eval(IEvaluationScope scope) => items.Select(i => i.Eval(scope)).ToList();

        public override IEnumerable<string> Paths() => items.SelectMany(i => i.Paths());
    }

    private sealed class Unary(TokenKind op, Node operand) : Node
    {
        public override object? Eval(IEvaluationScope scope)
        {
            var v = operand.Eval(scope);
            return op switch
            {
                TokenKind.Bang => !Values.ToBool(v),
                TokenKind.Minus => -Values.ToNumber(v),
                _ => throw new InvalidOperationException(op.ToString()),
            };
        }

        public override IEnumerable<string> Paths() => operand.Paths();
    }

    private sealed class Binary(TokenKind op, Node left, Node right) : Node
    {
        public override object? Eval(IEvaluationScope scope)
        {
            // Short-circuit logic.
            if (op == TokenKind.AndAnd)
            {
                return Values.ToBool(left.Eval(scope)) && Values.ToBool(right.Eval(scope));
            }

            if (op == TokenKind.OrOr)
            {
                return Values.ToBool(left.Eval(scope)) || Values.ToBool(right.Eval(scope));
            }

            var l = left.Eval(scope);
            var r = right.Eval(scope);
            return op switch
            {
                TokenKind.Plus when l is string || r is string => Values.ToText(l) + Values.ToText(r),
                TokenKind.Plus => Values.ToNumber(l) + Values.ToNumber(r),
                TokenKind.Minus => Values.ToNumber(l) - Values.ToNumber(r),
                TokenKind.Star => Values.ToNumber(l) * Values.ToNumber(r),
                TokenKind.Slash => Values.ToNumber(l) / Values.ToNumber(r),
                TokenKind.Percent => Values.ToNumber(l) % Values.ToNumber(r),
                TokenKind.EqualEqual => Values.AreEqual(l, r),
                TokenKind.NotEqual => !Values.AreEqual(l, r),
                TokenKind.Less => Values.Compare(l, r) < 0,
                TokenKind.LessEqual => Values.Compare(l, r) <= 0,
                TokenKind.Greater => Values.Compare(l, r) > 0,
                TokenKind.GreaterEqual => Values.Compare(l, r) >= 0,
                TokenKind.In => Values.ToList(r).Any(item => Values.AreEqual(l, item)),
                _ => throw new InvalidOperationException(op.ToString()),
            };
        }

        public override IEnumerable<string> Paths() => left.Paths().Concat(right.Paths());
    }

    private sealed class Conditional(Node condition, Node whenTrue, Node whenFalse) : Node
    {
        public override object? Eval(IEvaluationScope scope) =>
            Values.ToBool(condition.Eval(scope)) ? whenTrue.Eval(scope) : whenFalse.Eval(scope);

        public override IEnumerable<string> Paths() => condition.Paths().Concat(whenTrue.Paths()).Concat(whenFalse.Paths());
    }

    private sealed class Call(string name, IReadOnlyList<Node> args, int position) : Node
    {
        public override object? Eval(IEvaluationScope scope) => Functions.Invoke(name, args, scope, position);

        public override IEnumerable<string> Paths() => args.SelectMany(a => a.Paths());
    }

    // ---------------------------------------------------------------- Parser (precedence climbing)

    private sealed class Parser(List<Token> tokens)
    {
        private int _pos;

        private Token Current => tokens[_pos];

        public void Expect(TokenKind kind)
        {
            if (Current.Kind != kind)
            {
                throw new ExpressionException($"Expected {kind} but found '{Current.Text}'", Current.Position);
            }

            _pos++;
        }

        public Node ParseExpression() => ParseConditional();

        private bool Accept(TokenKind kind)
        {
            if (Current.Kind != kind)
            {
                return false;
            }

            _pos++;
            return true;
        }

        private Node ParseConditional()
        {
            var condition = ParseOr();
            if (!Accept(TokenKind.Question))
            {
                return condition;
            }

            var whenTrue = ParseConditional();
            Expect(TokenKind.Colon);
            var whenFalse = ParseConditional();
            return new Conditional(condition, whenTrue, whenFalse);
        }

        private Node ParseOr()
        {
            var left = ParseAnd();
            while (Accept(TokenKind.OrOr))
            {
                left = new Binary(TokenKind.OrOr, left, ParseAnd());
            }

            return left;
        }

        private Node ParseAnd()
        {
            var left = ParseEquality();
            while (Accept(TokenKind.AndAnd))
            {
                left = new Binary(TokenKind.AndAnd, left, ParseEquality());
            }

            return left;
        }

        private Node ParseEquality()
        {
            var left = ParseComparison();
            while (Current.Kind is TokenKind.EqualEqual or TokenKind.NotEqual)
            {
                var op = tokens[_pos++].Kind;
                left = new Binary(op, left, ParseComparison());
            }

            return left;
        }

        private Node ParseComparison()
        {
            var left = ParseAdditive();
            while (Current.Kind is TokenKind.Less or TokenKind.LessEqual or TokenKind.Greater or TokenKind.GreaterEqual or TokenKind.In)
            {
                var op = tokens[_pos++].Kind;
                left = new Binary(op, left, ParseAdditive());
            }

            return left;
        }

        private Node ParseAdditive()
        {
            var left = ParseMultiplicative();
            while (Current.Kind is TokenKind.Plus or TokenKind.Minus)
            {
                var op = tokens[_pos++].Kind;
                left = new Binary(op, left, ParseMultiplicative());
            }

            return left;
        }

        private Node ParseMultiplicative()
        {
            var left = ParseUnary();
            while (Current.Kind is TokenKind.Star or TokenKind.Slash or TokenKind.Percent)
            {
                var op = tokens[_pos++].Kind;
                left = new Binary(op, left, ParseUnary());
            }

            return left;
        }

        private Node ParseUnary()
        {
            if (Current.Kind is TokenKind.Bang or TokenKind.Minus)
            {
                var op = tokens[_pos++].Kind;
                return new Unary(op, ParseUnary());
            }

            return ParsePrimary();
        }

        private Node ParsePrimary()
        {
            var token = tokens[_pos++];
            switch (token.Kind)
            {
                case TokenKind.Number:
                    return new Literal(token.Number);
                case TokenKind.String:
                    return new Literal(token.Text);
                case TokenKind.True:
                    return new Literal(true);
                case TokenKind.False:
                    return new Literal(false);
                case TokenKind.Null:
                    return new Literal(null);
                case TokenKind.LeftParen:
                    var inner = ParseExpression();
                    Expect(TokenKind.RightParen);
                    return inner;
                case TokenKind.LeftBracket:
                    return new ListNode(ParseArguments(TokenKind.RightBracket));
                case TokenKind.Identifier when Current.Kind == TokenKind.LeftParen:
                    _pos++;
                    if (!Functions.IsKnown(token.Text))
                    {
                        throw new ExpressionException($"Unknown function '{token.Text}'", token.Position);
                    }

                    return new Call(token.Text, ParseArguments(TokenKind.RightParen), token.Position);
                case TokenKind.Identifier:
                    return new PathRef(token.Text);
                default:
                    throw new ExpressionException($"Unexpected '{token.Text}'", token.Position);
            }
        }

        private List<Node> ParseArguments(TokenKind close)
        {
            var args = new List<Node>();
            if (Accept(close))
            {
                return args;
            }

            do
            {
                args.Add(ParseExpression());
            }
            while (Accept(TokenKind.Comma));

            Expect(close);
            return args;
        }
    }

    // ---------------------------------------------------------------- Functions

    private static class Functions
    {
        private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
        {
            "exists", "coalesce", "pct", "abs", "min", "max", "sum", "avg", "count", "len",
            "lower", "upper", "contains", "startsWith", "matches", "daysSince", "hoursSince", "version_ge",
        };

        public static bool IsKnown(string name) => Known.Contains(name);

        public static object? Invoke(string name, IReadOnlyList<Node> args, IEvaluationScope scope, int position)
        {
            switch (name)
            {
                case "exists":
                    Arity(name, args, 1, position);
                    if (args[0] is not PathRef p)
                    {
                        throw new ExpressionException("exists() takes a fact path", position);
                    }

                    return scope.TryResolve(p.Path, out _);
                case "coalesce":
                    foreach (var arg in args)
                    {
                        try
                        {
                            var value = arg.Eval(scope);
                            if (value is not null)
                            {
                                return value;
                            }
                        }
                        catch (MissingFactException)
                        {
                            // try next
                        }
                    }

                    return null;
            }

            var v = args.Select(a => a.Eval(scope)).ToArray();
            switch (name)
            {
                case "pct":
                    Arity(name, args, 2, position);
                    var denominator = Values.ToNumber(v[1]);
                    return denominator == 0 ? 0d : Values.ToNumber(v[0]) / denominator * 100d;
                case "abs":
                    Arity(name, args, 1, position);
                    return Math.Abs(Values.ToNumber(v[0]));
                case "min":
                    return Numbers(v).DefaultIfEmpty(double.NaN).Min();
                case "max":
                    return Numbers(v).DefaultIfEmpty(double.NaN).Max();
                case "sum":
                    return Numbers(v).Sum();
                case "avg":
                    var nums = Numbers(v).ToList();
                    return nums.Count == 0 ? double.NaN : nums.Average();
                case "count":
                    Arity(name, args, 1, position);
                    return (double)Values.ToList(v[0]).Count;
                case "len":
                    Arity(name, args, 1, position);
                    return (double)Values.ToText(v[0]).Length;
                case "lower":
                    Arity(name, args, 1, position);
                    return Values.ToText(v[0]).ToLowerInvariant();
                case "upper":
                    Arity(name, args, 1, position);
                    return Values.ToText(v[0]).ToUpperInvariant();
                case "contains":
                    Arity(name, args, 2, position);
                    return v[0] is string s
                        ? s.Contains(Values.ToText(v[1]), StringComparison.OrdinalIgnoreCase)
                        : Values.ToList(v[0]).Any(i => Values.AreEqual(i, v[1]));
                case "startsWith":
                    Arity(name, args, 2, position);
                    return Values.ToText(v[0]).StartsWith(Values.ToText(v[1]), StringComparison.OrdinalIgnoreCase);
                case "matches":
                    Arity(name, args, 2, position);
                    return Regex.IsMatch(Values.ToText(v[0]), Values.ToText(v[1]), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                case "daysSince":
                    Arity(name, args, 1, position);
                    return (scope.Now - Values.ToTime(v[0])).TotalDays;
                case "hoursSince":
                    Arity(name, args, 1, position);
                    return (scope.Now - Values.ToTime(v[0])).TotalHours;
                case "version_ge":
                    Arity(name, args, 2, position);
                    return Version.TryParse(Values.ToText(v[0]), out var a) && Version.TryParse(Values.ToText(v[1]), out var b) && a >= b;
                default:
                    throw new ExpressionException($"Unknown function '{name}'", position);
            }
        }

        private static IEnumerable<double> Numbers(object?[] values) =>
            values.SelectMany(x => x is IList list ? list.Cast<object?>() : [x]).Select(Values.ToNumber);

        private static void Arity(string name, IReadOnlyList<Node> args, int expected, int position)
        {
            if (args.Count != expected)
            {
                throw new ExpressionException($"{name}() expects {expected} argument(s)", position);
            }
        }
    }
}

/// <summary>Value conversions used by the evaluator. Numbers are doubles; strings compare case-insensitively.</summary>
public static class Values
{
    public static object? Normalize(object? value) => value switch
    {
        null => null,
        string or bool or double or DateTimeOffset => value,
        DateTime dt => new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt),
        int or long or short or byte or float or decimal or uint or ulong or ushort or sbyte => Convert.ToDouble(value, CultureInfo.InvariantCulture),
        Enum e => e.ToString(),
        IEnumerable<object?> seq => seq.Select(Normalize).ToList(),
        IEnumerable seq when value is not string => seq.Cast<object?>().Select(Normalize).ToList(),
        _ => value.ToString(),
    };

    public static bool ToBool(object? value) => value switch
    {
        bool b => b,
        null => false,
        double d => d != 0,
        string s => s.Length > 0 && !s.Equals("false", StringComparison.OrdinalIgnoreCase) && s != "0",
        _ => true,
    };

    public static double ToNumber(object? value) => value switch
    {
        double d => d,
        bool b => b ? 1 : 0,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        null => throw new InvalidCastException("null is not a number"),
        _ => Convert.ToDouble(Normalize(value), CultureInfo.InvariantCulture),
    };

    public static string ToText(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        double d => d.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        DateTimeOffset t => t.ToString("O", CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    public static DateTimeOffset ToTime(object? value) => value switch
    {
        DateTimeOffset t => t,
        DateTime dt => (DateTimeOffset)Normalize(dt)!,
        string s when DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) => t,
        _ => throw new InvalidCastException($"'{value}' is not a timestamp"),
    };

    public static IReadOnlyList<object?> ToList(object? value) => value switch
    {
        IReadOnlyList<object?> list => list,
        IList list => list.Cast<object?>().ToList(),
        null => [],
        _ => [value],
    };

    public static bool AreEqual(object? a, object? b)
    {
        a = Normalize(a);
        b = Normalize(b);
        return (a, b) switch
        {
            (null, null) => true,
            (null, _) or (_, null) => false,
            (double x, double y) => x.Equals(y),
            (string x, string y) => string.Equals(x, y, StringComparison.OrdinalIgnoreCase),
            (double x, string y) => double.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d.Equals(x),
            (string x, double y) => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d.Equals(y),
            _ => a.Equals(b),
        };
    }

    public static int Compare(object? a, object? b)
    {
        a = Normalize(a);
        b = Normalize(b);
        return (a, b) switch
        {
            (string x, string y) when !IsNumeric(x) || !IsNumeric(y) => string.Compare(x, y, StringComparison.OrdinalIgnoreCase),
            (DateTimeOffset x, DateTimeOffset y) => x.CompareTo(y),
            _ => ToNumber(a).CompareTo(ToNumber(b)),
        };
    }

    private static bool IsNumeric(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
}
