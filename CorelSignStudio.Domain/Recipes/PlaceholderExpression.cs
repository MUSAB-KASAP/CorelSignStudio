using System.Globalization;

namespace CorelSignStudio.Domain.Recipes;

/// <summary>
/// The arithmetic allowed inside a <c>{{ … }}</c> placeholder: numbers, variable names, + - * / and
/// parentheses — for derived layout values such as <c>{{WIDTH_MM / 2}}</c>. It is a calculator, not a
/// language: there are no functions, no strings, no member access, and nothing can be executed.
/// </summary>
public static class PlaceholderExpression
{
    /// <summary>True when the placeholder content is a single variable name.</summary>
    public static bool IsName(string content)
    {
        var trimmed = content.Trim();
        return trimmed.Length > 0 && (char.IsAsciiLetter(trimmed[0]) || trimmed[0] == '_') && trimmed.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    }

    /// <summary>The variable names an expression (or plain name) refers to.</summary>
    public static IReadOnlyList<string> Variables(string content)
    {
        var names = new List<string>();
        for (var index = 0; index < content.Length;)
        {
            if (char.IsAsciiLetter(content[index]) || content[index] == '_')
            {
                var start = index;
                while (index < content.Length && (char.IsAsciiLetterOrDigit(content[index]) || content[index] == '_'))
                {
                    index++;
                }

                names.Add(content[start..index]);
            }
            else
            {
                index++;
            }
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Evaluates an arithmetic expression. Every variable must hold a number.</summary>
    /// <param name="content">Text between the braces.</param>
    /// <param name="resolve">Returns the value of a variable, or <c>null</c> when it has none.</param>
    /// <param name="error">Why evaluation failed, as a short English phrase for the caller to wrap.</param>
    public static bool TryEvaluate(string content, Func<string, string?> resolve, out double value, out string? error)
    {
        var parser = new Parser(content, resolve);
        try
        {
            value = parser.ParseExpression();
            parser.ExpectEnd();
            if (!double.IsFinite(value))
            {
                throw new FormatException("the result is not a finite number");
            }

            error = null;
            return true;
        }
        catch (FormatException exception)
        {
            value = 0;
            error = exception.Message;
            return false;
        }
    }

    /// <summary>Invariant, at most four decimals, no trailing zeros — safe inside file names and JSON.</summary>
    public static string Format(double value) => Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);

    private sealed class Parser(string text, Func<string, string?> resolve)
    {
        private int _position;
        private int _depth;

        public double ParseExpression()
        {
            var value = ParseTerm();
            while (true)
            {
                SkipSpaces();
                if (Accept('+'))
                {
                    value += ParseTerm();
                }
                else if (Accept('-'))
                {
                    value -= ParseTerm();
                }
                else
                {
                    return value;
                }
            }
        }

        public void ExpectEnd()
        {
            SkipSpaces();
            if (_position < text.Length)
            {
                throw new FormatException($"unexpected '{text[_position]}'");
            }
        }

        private double ParseTerm()
        {
            var value = ParseFactor();
            while (true)
            {
                SkipSpaces();
                if (Accept('*'))
                {
                    value *= ParseFactor();
                }
                else if (Accept('/'))
                {
                    var divisor = ParseFactor();
                    if (divisor == 0)
                    {
                        throw new FormatException("division by zero");
                    }

                    value /= divisor;
                }
                else
                {
                    return value;
                }
            }
        }

        private double ParseFactor()
        {
            SkipSpaces();
            if (Accept('-'))
            {
                return -ParseFactor();
            }

            if (Accept('('))
            {
                if (++_depth > 32)
                {
                    throw new FormatException("the expression is nested too deeply");
                }

                var inner = ParseExpression();
                SkipSpaces();
                if (!Accept(')'))
                {
                    throw new FormatException("a closing parenthesis is missing");
                }

                _depth--;
                return inner;
            }

            if (_position >= text.Length)
            {
                throw new FormatException("the expression is incomplete");
            }

            var start = _position;
            if (char.IsAsciiDigit(text[_position]) || text[_position] == '.')
            {
                while (_position < text.Length && (char.IsAsciiDigit(text[_position]) || text[_position] == '.'))
                {
                    _position++;
                }

                return double.TryParse(text.AsSpan(start, _position - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    ? number
                    : throw new FormatException($"'{text[start.._position]}' is not a number");
            }

            if (char.IsAsciiLetter(text[_position]) || text[_position] == '_')
            {
                while (_position < text.Length && (char.IsAsciiLetterOrDigit(text[_position]) || text[_position] == '_'))
                {
                    _position++;
                }

                var name = text[start.._position];
                var raw = resolve(name) ?? throw new FormatException($"variable '{name}' has no value");
                return VariableSubstitution.TryParseNumber(raw, out var variable)
                    ? variable
                    : throw new FormatException($"variable '{name}' is not a number ('{raw}')");
            }

            throw new FormatException($"unexpected '{text[_position]}'");
        }

        private bool Accept(char expected)
        {
            if (_position < text.Length && text[_position] == expected)
            {
                _position++;
                return true;
            }

            return false;
        }

        private void SkipSpaces()
        {
            while (_position < text.Length && char.IsWhiteSpace(text[_position]))
            {
                _position++;
            }
        }
    }
}
