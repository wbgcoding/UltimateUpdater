namespace UltimateUpdater.Services.Sources.PackageManagers;

/// <summary>
/// Evaluates a PEP 508 environment marker ("python_version &lt; '3.12' and sys_platform == 'win32'")
/// against the environment <c>pip inspect</c> reports for the interpreter being scanned. Hand-rolled and
/// small on purpose: the markers come from installed packages' metadata, which is hostile input, so the
/// grammar is closed (variables, quoted strings, comparison operators, <c>and</c>, <c>or</c>, parentheses)
/// and anything else is "cannot tell", never an exception.
/// Evaluation is three valued. A comparison that cannot be decided (a variable the environment does not
/// carry, an ordering operator on text that is not a version) is Unknown, and Unknown propagates the way
/// it must: <c>False and Unknown</c> is False, <c>True or Unknown</c> is True, everything else involving
/// Unknown stays Unknown. The caller treats an Unknown result as "the requirement applies", because for
/// the dependency guard keeping a constraint is the safe side.
/// </summary>
internal static class PipMarkerEvaluator
{
    private const int MaxMarkerLength = 2000;
    private const int MaxNesting = 32;

    private enum Truth
    {
        False,
        True,
        Unknown,
    }

    private enum TokenKind
    {
        Word,
        Text,
        Operator,
        Open,
        Close,
    }

    private readonly record struct Token(TokenKind Kind, string Value);

    private readonly record struct Operand(string? Value, bool IsVariable, string Name);

    /// <summary>These variables compare as PEP 440 versions when both sides parse as one.</summary>
    private static readonly HashSet<string> VersionVariables =
        new(StringComparer.Ordinal) { "python_version", "python_full_version", "implementation_version" };

    private static readonly string[] Operators = ["===", "==", "!=", "<=", ">=", "~=", "<", ">"];

    /// <summary>True or false when the marker decides for this environment, null when it cannot be
    /// evaluated (unknown variable, unsupported comparison, syntax the grammar does not cover).</summary>
    public static bool? Evaluate(string marker, IReadOnlyDictionary<string, string> environment)
    {
        if (marker.Length > MaxMarkerLength)
            return null;

        try
        {
            var parser = new Parser(Tokenize(marker), environment);
            var result = parser.ParseAll();
            return result switch
            {
                Truth.True => true,
                Truth.False => false,
                _ => null,
            };
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '(')
            {
                tokens.Add(new Token(TokenKind.Open, "("));
                i++;
            }
            else if (c == ')')
            {
                tokens.Add(new Token(TokenKind.Close, ")"));
                i++;
            }
            else if (c is '"' or '\'')
            {
                var end = text.IndexOf(c, i + 1);
                if (end < 0)
                    throw new FormatException("unterminated string");

                tokens.Add(new Token(TokenKind.Text, text[(i + 1)..end]));
                i = end + 1;
            }
            else if (c is '=' or '!' or '<' or '>' or '~')
            {
                var op = Operators.FirstOrDefault(candidate => string.CompareOrdinal(text, i, candidate, 0, candidate.Length) == 0)
                    ?? throw new FormatException("unknown operator");

                tokens.Add(new Token(TokenKind.Operator, op));
                i += op.Length;
            }
            else if (char.IsAsciiLetter(c) || c == '_')
            {
                var start = i;
                while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] is '_' or '.'))
                    i++;

                tokens.Add(new Token(TokenKind.Word, text[start..i]));
            }
            else
            {
                throw new FormatException("unexpected character");
            }
        }

        return tokens;
    }

    private sealed class Parser(List<Token> tokens, IReadOnlyDictionary<string, string> environment)
    {
        private int _position;
        private int _depth;

        public Truth ParseAll()
        {
            var result = ParseOr();
            if (_position != tokens.Count)
                throw new FormatException("trailing tokens");

            return result;
        }

        private Truth ParseOr()
        {
            var result = ParseAnd();
            while (PeekWord("or"))
            {
                _position++;
                var right = ParseAnd();
                result = (result, right) switch
                {
                    (Truth.True, _) or (_, Truth.True) => Truth.True,
                    (Truth.False, Truth.False) => Truth.False,
                    _ => Truth.Unknown,
                };
            }

            return result;
        }

        private Truth ParseAnd()
        {
            var result = ParseAtom();
            while (PeekWord("and"))
            {
                _position++;
                var right = ParseAtom();
                result = (result, right) switch
                {
                    (Truth.False, _) or (_, Truth.False) => Truth.False,
                    (Truth.True, Truth.True) => Truth.True,
                    _ => Truth.Unknown,
                };
            }

            return result;
        }

        private Truth ParseAtom()
        {
            if (Peek() is { Kind: TokenKind.Open })
            {
                if (++_depth > MaxNesting)
                    throw new FormatException("nested too deep");

                _position++;
                var inner = ParseOr();
                if (Peek() is not { Kind: TokenKind.Close })
                    throw new FormatException("missing closing parenthesis");

                _position++;
                _depth--;
                return inner;
            }

            var left = ParseOperand();
            var op = ParseOperator();
            var right = ParseOperand();
            return Compare(left, op, right);
        }

        private Operand ParseOperand()
        {
            var token = Next();
            switch (token.Kind)
            {
                case TokenKind.Text:
                    return new Operand(token.Value, false, string.Empty);
                case TokenKind.Word when !IsKeyword(token.Value):
                    return new Operand(environment.TryGetValue(token.Value, out var value) ? value : null, true, token.Value);
                default:
                    throw new FormatException("expected a variable or a string");
            }
        }

        private string ParseOperator()
        {
            var token = Next();
            if (token.Kind == TokenKind.Operator)
                return token.Value;

            if (token.Kind == TokenKind.Word && token.Value == "in")
                return "in";

            if (token.Kind == TokenKind.Word && token.Value == "not" && Next() is { Kind: TokenKind.Word, Value: "in" })
                return "not in";

            throw new FormatException("expected a comparison operator");
        }

        private Token? Peek() => _position < tokens.Count ? tokens[_position] : null;

        private bool PeekWord(string word) => Peek() is { Kind: TokenKind.Word } token && token.Value == word;

        private Token Next() => _position < tokens.Count
            ? tokens[_position++]
            : throw new FormatException("unexpected end of marker");

        private static bool IsKeyword(string word) => word is "and" or "or" or "in" or "not";
    }

    private static Truth Compare(Operand left, string op, Operand right)
    {
        if (left.Value is null || right.Value is null)
            return Truth.Unknown;

        var a = left.Value;
        var b = right.Value;

        switch (op)
        {
            case "===":
                return Of(string.Equals(a, b, StringComparison.Ordinal));
            case "in":
                return Of(b.Contains(a, StringComparison.Ordinal));
            case "not in":
                return Of(!b.Contains(a, StringComparison.Ordinal));
        }

        var versionContext = (left.IsVariable && VersionVariables.Contains(left.Name))
            || (right.IsVariable && VersionVariables.Contains(right.Name));

        if (versionContext && Pep440.IsVersion(a) && Pep440.IsVersion(b.EndsWith(".*", StringComparison.Ordinal) ? b[..^2] : b))
            return Of(Pep440.Satisfies(a, op + b));

        return op switch
        {
            "==" => Of(string.Equals(a, b, StringComparison.Ordinal)),
            "!=" => Of(!string.Equals(a, b, StringComparison.Ordinal)),
            _ => Truth.Unknown,
        };
    }

    private static Truth Of(bool value) => value ? Truth.True : Truth.False;
}
