using System;
using System.Globalization;

namespace CloCloWidget;

// Evaluates only the arithmetic grammar below. No scripting engine, reflection,
// process launch, network call, or user-provided function name is involved.
internal static class ScientificCalculator
{
    public const int MaxExpressionLength = 256;

    public static bool TryEvaluate(string expression, bool degrees, double previousAnswer,
        out double value, out string error)
    {
        value = 0;
        error = "";
        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "Enter an expression.";
            return false;
        }
        if (expression.Length > MaxExpressionLength)
        {
            error = "Expression is too long.";
            return false;
        }

        try
        {
            var parser = new Parser(expression, degrees, previousAnswer);
            value = parser.Evaluate();
            if (double.IsFinite(value)) return true;
            error = "Result is outside the supported range.";
        }
        catch (FormatException ex)
        {
            error = ex.Message;
        }
        catch (ArithmeticException ex)
        {
            error = ex.Message;
        }
        value = 0;
        return false;
    }

    private sealed class Parser(string input, bool degrees, double previousAnswer)
    {
        private int _position;
        private int _depth;

        public double Evaluate()
        {
            var result = ParseExpression();
            SkipSpace();
            if (_position != input.Length) throw new FormatException("Unexpected character in expression.");
            return Finite(result);
        }

        private double ParseExpression()
        {
            var result = ParseTerm();
            while (true)
            {
                if (Take('+')) result = Finite(result + ParseTerm());
                else if (Take('-') || Take('−')) result = Finite(result - ParseTerm());
                else return result;
            }
        }

        private double ParseTerm()
        {
            var result = ParseUnary();
            while (true)
            {
                if (Take('*') || Take('×')) result = Finite(result * ParseUnary());
                else if (Take('/') || Take('÷'))
                {
                    var divisor = ParseUnary();
                    if (divisor == 0) throw new ArithmeticException("Cannot divide by zero.");
                    result = Finite(result / divisor);
                }
                else if (StartsPrimary()) result = Finite(result * ParseUnary());
                else return result;
            }
        }

        private double ParseUnary()
        {
            if (Take('+')) return ParseUnary();
            if (Take('-') || Take('−')) return Finite(-ParseUnary());
            return ParsePower();
        }

        private double ParsePower()
        {
            var result = ParsePostfix();
            return Take('^') ? Finite(Math.Pow(result, ParseUnary())) : result;
        }

        private double ParsePostfix()
        {
            var result = ParsePrimary();
            while (true)
            {
                if (Take('%')) result = Finite(result / 100);
                else if (Take('!')) result = Factorial(result);
                else return result;
            }
        }

        private double ParsePrimary()
        {
            SkipSpace();
            if (Take('('))
            {
                EnterGroup();
                try
                {
                    var result = ParseExpression();
                    if (!Take(')')) throw new FormatException("Missing closing parenthesis.");
                    return result;
                }
                finally { _depth--; }
            }

            if (_position < input.Length && (char.IsAsciiDigit(input[_position]) || input[_position] == '.'))
                return ParseNumber();

            var name = ReadName();
            if (name.Length == 0) throw new FormatException("Expected a number or function.");
            if (name is "pi" or "π") return Math.PI;
            if (name == "e") return Math.E;
            if (name == "ans") return previousAnswer;
            if (name is not ("sin" or "cos" or "tan" or "asin" or "acos" or "atan" or
                "sqrt" or "ln" or "log" or "abs" or "exp"))
                throw new FormatException("Unknown function.");
            if (!Take('(')) throw new FormatException("Function needs parentheses.");
            EnterGroup();
            try
            {
                var argument = ParseExpression();
                if (!Take(')')) throw new FormatException("Missing closing parenthesis.");
                var radians = degrees ? argument * Math.PI / 180 : argument;
                var result = name switch
                {
                    "sin" => Math.Sin(radians),
                    "cos" => Math.Cos(radians),
                    "tan" => Math.Abs(Math.Cos(radians)) < 1e-12
                        ? throw new ArithmeticException("Tangent is undefined at this angle.") : Math.Tan(radians),
                    "asin" => degrees ? Math.Asin(argument) * 180 / Math.PI : Math.Asin(argument),
                    "acos" => degrees ? Math.Acos(argument) * 180 / Math.PI : Math.Acos(argument),
                    "atan" => degrees ? Math.Atan(argument) * 180 / Math.PI : Math.Atan(argument),
                    "sqrt" => Math.Sqrt(argument),
                    "ln" => Math.Log(argument),
                    "log" => Math.Log10(argument),
                    "abs" => Math.Abs(argument),
                    _ => Math.Exp(argument),
                };
                return Finite(result);
            }
            finally { _depth--; }
        }

        private double ParseNumber()
        {
            var start = _position;
            while (_position < input.Length && char.IsAsciiDigit(input[_position])) _position++;
            if (_position < input.Length && input[_position] == '.')
            {
                _position++;
                while (_position < input.Length && char.IsAsciiDigit(input[_position])) _position++;
            }
            if (_position < input.Length && (input[_position] == 'e' || input[_position] == 'E'))
            {
                var exponent = _position + 1;
                if (exponent < input.Length && (input[exponent] == '+' || input[exponent] == '-')) exponent++;
                if (exponent < input.Length && char.IsAsciiDigit(input[exponent]))
                {
                    _position = exponent + 1;
                    while (_position < input.Length && char.IsAsciiDigit(input[_position])) _position++;
                }
            }
            if (!double.TryParse(input.AsSpan(start, _position - start), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var number))
                throw new FormatException("Invalid number.");
            return Finite(number);
        }

        private string ReadName()
        {
            var start = _position;
            while (_position < input.Length && (char.IsLetter(input[_position]) || input[_position] == 'π'))
                _position++;
            return input[start.._position].ToLowerInvariant();
        }

        private bool StartsPrimary()
        {
            SkipSpace();
            return _position < input.Length && (input[_position] == '(' ||
                char.IsLetter(input[_position]) || input[_position] == 'π');
        }

        private bool Take(char character)
        {
            SkipSpace();
            if (_position >= input.Length || input[_position] != character) return false;
            _position++;
            return true;
        }

        private void SkipSpace()
        {
            while (_position < input.Length && char.IsWhiteSpace(input[_position])) _position++;
        }

        private void EnterGroup()
        {
            if (++_depth > 32) throw new FormatException("Too many nested parentheses.");
        }

        private static double Factorial(double value)
        {
            if (value < 0 || value > 170 || value != Math.Truncate(value))
                throw new ArithmeticException("Factorial needs a whole number from 0 to 170.");
            var result = 1.0;
            for (var n = 2; n <= (int)value; n++) result *= n;
            return Finite(result);
        }

        private static double Finite(double value) => double.IsFinite(value)
            ? value : throw new ArithmeticException("Result is outside the supported range.");
    }
}
