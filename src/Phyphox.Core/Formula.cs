// SPDX-License-Identifier: GPL-3.0-or-later
// Dedicated expression grammar implementing phyphox formula syntax; never executes host code.
using System.Globalization;

namespace Phyphox.Core;
public sealed class Formula
{
    readonly string text;
    int pos;
    readonly Func<double[][], int, double> expression;
    public Formula(string text)
    {
        this.text = text.Replace(" ", "").ToLowerInvariant();
        expression = Parse(0);
        if (pos != this.text.Length)
            throw new FormatException("Unexpected formula token.");
    }

    public double Evaluate(double[][] inputs, int index) => expression(inputs, index);
    Func<double[][], int, double> Parse(int min)
    {
        Func<double[][], int, double> left;
        if (Take('-'))
        {
            var x = Parse(3);
            left = (a, i) => -x(a, i);
        }
        else if (Take('+'))
            left = Parse(3);
        else if (Take('('))
        {
            left = Parse(0);
            Need(')');
        }
        else if (Take('['))
        {
            int start = pos;
            while (pos < text.Length && char.IsDigit(text[pos]))
                pos++;
            var index = int.Parse(text[start..pos]) - 1;
            if (index < 0)
                throw new FormatException("Formula input indices start at one.");
            var vector = Take('_');
            Need(']');
            left = (a, i) => a[index][vector ? i : a[index].Length - 1];
        }
        else if (pos < text.Length && char.IsLetter(text[pos]))
        {
            int start = pos;
            while (pos < text.Length && char.IsLetterOrDigit(text[pos]))
                pos++;
            var name = text[start..pos];
            Need('(');
            var x = Parse(0);
            Func<double[][], int, double>? y = null;
            if (Take(','))
                y = Parse(0);
            Need(')');
            var binary = name is "atan2" or "min" or "max";
            if (binary != (y != null))
                throw new FormatException("Invalid function arity.");
            _ = Function(name, 1, y == null ? null : 1);
            left = (a, i) => Function(name, x(a, i), y?.Invoke(a, i));
        }
        else
        {
            int start = pos;
            while (pos < text.Length && (char.IsDigit(text[pos]) || text[pos] == '.' || text[pos] == 'e' || (pos > start && text[pos - 1] == 'e' && text[pos] is '+' or '-')))
                pos++;
            if (pos == start)
                throw new FormatException("Missing formula operand.");
            var v = double.Parse(text[start..pos], CultureInfo.InvariantCulture);
            left = (_, _) => v;
        }

        while (pos < text.Length)
        {
            var op = text[pos];
            int prec = op switch
            {
                '+' or '-' => 1,
                '*' or '/' or '%' => 2,
                '^' => 4,
                _ => -1
            };
            if (prec < min)
                break;
            pos++;
            var right = Parse(op == '^' ? prec : prec + 1);
            var prior = left;
            left = (a, i) => op switch
            {
                '+' => prior(a, i) + right(a, i),
                '-' => prior(a, i) - right(a, i),
                '*' => prior(a, i) * right(a, i),
                '/' => prior(a, i) / right(a, i),
                '%' => prior(a, i) % right(a, i),
                '^' => Math.Pow(prior(a, i), right(a, i)),
                _ => double.NaN
            };
        }

        return left;
    }

    bool Take(char c)
    {
        if (pos >= text.Length || text[pos] != c)
            return false;
        pos++;
        return true;
    }

    void Need(char c)
    {
        if (!Take(c))
            throw new FormatException($"Expected '{c}' in formula.");
    }

    static double Function(string n, double x, double? y) => n switch
    {
        "sqrt" => Math.Sqrt(x),
        "sin" => Math.Sin(x),
        "cos" => Math.Cos(x),
        "tan" => Math.Tan(x),
        "asin" => Math.Asin(x),
        "acos" => Math.Acos(x),
        "atan" => Math.Atan(x),
        "atan2" when y.HasValue => Math.Atan2(x, y.Value),
        "sinh" => Math.Sinh(x),
        "cosh" => Math.Cosh(x),
        "tanh" => Math.Tanh(x),
        "exp" => Math.Exp(x),
        "log" => Math.Log(x),
        "abs" => Math.Abs(x),
        "round" => Math.Round(x, MidpointRounding.AwayFromZero),
        "ceil" => Math.Ceiling(x),
        "floor" => Math.Floor(x),
        "sign" => double.IsNaN(x) ? double.NaN : x == 0 ? x : Math.Sign(x),
        "heaviside" => double.IsNaN(x) ? double.NaN : x >= 0 ? 1 : 0,
        "min" when y.HasValue => Math.Min(x, y.Value),
        "max" when y.HasValue => Math.Max(x, y.Value),
        _ => throw new FormatException($"Unsupported function or arity: {n}")};
}
