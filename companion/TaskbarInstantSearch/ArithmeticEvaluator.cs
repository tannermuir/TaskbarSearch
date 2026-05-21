using System.Globalization;

namespace TaskbarInstantSearch;

internal static class ArithmeticEvaluator
{
    public static bool TryEvaluate(string input, out string answer)
    {
        answer = "";
        if (!LooksLikeArithmetic(input))
        {
            return false;
        }

        try
        {
            var parser = new Parser(input);
            double value = parser.ParseExpression();
            parser.SkipWhitespace();
            if (!parser.IsAtEnd || double.IsNaN(value) || double.IsInfinity(value))
            {
                return false;
            }

            answer = FormatNumber(value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeArithmetic(string input)
    {
        bool hasDigit = false;
        bool hasOperator = false;
        foreach (char character in input)
        {
            if (char.IsDigit(character))
            {
                hasDigit = true;
                continue;
            }

            if (character is '+' or '-' or '*' or '/' or '^' or 'x' or 'X' or '(' or ')' or '.' ||
                char.IsWhiteSpace(character))
            {
                hasOperator |= character is '+' or '-' or '*' or '/' or '^' or 'x' or 'X';
                continue;
            }

            return false;
        }

        return hasDigit && hasOperator;
    }

    private static string FormatNumber(double value)
    {
        if (Math.Abs(value - Math.Round(value)) < 0.0000000001)
        {
            return Math.Round(value).ToString(CultureInfo.InvariantCulture);
        }

        return value.ToString("0.##########", CultureInfo.InvariantCulture);
    }

    private sealed class Parser
    {
        private readonly string _input;
        private int _position;

        public Parser(string input)
        {
            _input = input;
        }

        public bool IsAtEnd => _position >= _input.Length;

        public double ParseExpression()
        {
            double value = ParseTerm();
            while (true)
            {
                SkipWhitespace();
                if (TryConsume('+'))
                {
                    value += ParseTerm();
                }
                else if (TryConsume('-'))
                {
                    value -= ParseTerm();
                }
                else
                {
                    return value;
                }
            }
        }

        public void SkipWhitespace()
        {
            while (!IsAtEnd && char.IsWhiteSpace(_input[_position]))
            {
                _position++;
            }
        }

        private double ParseTerm()
        {
            double value = ParsePower();
            while (true)
            {
                SkipWhitespace();
                if (TryConsume('*'))
                {
                    value *= ParsePower();
                }
                else if (TryConsume('x') || TryConsume('X'))
                {
                    value *= ParsePower();
                }
                else if (TryConsume('/'))
                {
                    value /= ParsePower();
                }
                else
                {
                    return value;
                }
            }
        }

        private double ParsePower()
        {
            double value = ParseUnary();
            SkipWhitespace();
            if (TryConsume('^'))
            {
                value = Math.Pow(value, ParsePower());
            }

            return value;
        }

        private double ParseUnary()
        {
            SkipWhitespace();
            if (TryConsume('+'))
            {
                return ParseUnary();
            }

            if (TryConsume('-'))
            {
                return -ParseUnary();
            }

            return ParsePrimary();
        }

        private double ParsePrimary()
        {
            SkipWhitespace();
            if (TryConsume('('))
            {
                double value = ParseExpression();
                SkipWhitespace();
                if (!TryConsume(')'))
                {
                    throw new FormatException("Missing closing parenthesis");
                }

                return value;
            }

            return ParseNumber();
        }

        private double ParseNumber()
        {
            SkipWhitespace();
            int start = _position;
            bool hasDecimal = false;
            while (!IsAtEnd)
            {
                char character = _input[_position];
                if (char.IsDigit(character))
                {
                    _position++;
                    continue;
                }

                if (character == '.' && !hasDecimal)
                {
                    hasDecimal = true;
                    _position++;
                    continue;
                }

                break;
            }

            if (start == _position)
            {
                throw new FormatException("Expected number");
            }

            string token = _input[start.._position];
            return double.Parse(token, CultureInfo.InvariantCulture);
        }

        private bool TryConsume(char expected)
        {
            if (!IsAtEnd && _input[_position] == expected)
            {
                _position++;
                return true;
            }

            return false;
        }
    }
}
