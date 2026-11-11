// Project : Api.Web.FinanceIA
// Created : 2026-11-10

using System.Globalization;
using System.Text.RegularExpressions;

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// Numbers as NOSIS Manager prints them — and the format varies BY SECTION within one report: most
/// sections write US style ("7,258,316,409", "31,472.6"), MAV writes "1.527.384.960" with a total
/// "1,939,384,960", AGIP writes "5,00 %". So every token is read by its OWN shape:
/// <list type="bullet">
/// <item><b>plain</b> — digits only;</item>
/// <item><b>US</b> — two or more comma groups, comma groups with a dot decimal, or a dot followed by a
/// number of digits other than 3;</item>
/// <item><b>AR</b> — the mirror image: two or more dot groups, dot groups with a comma decimal, or a
/// comma followed by a number of digits other than 3;</item>
/// <item><b>ambiguous</b> — one separator followed by exactly 3 digits, at most 3 before it
/// ("1,234", "1.234"). It takes the section's style (<see cref="InferStyle"/>); with no style, or a
/// mixed one, it reads NULL rather than a guess.</item>
/// </list>
/// "-" (or a dash) is the report's "none". "$" and "%" around a number are ignored. Nothing here logs.
/// </summary>
internal static class NosisReportNumbers
{
    private enum Shape { NotNumber, Plain, Us, Ar, Ambiguous }

    private static readonly Regex PlainPattern     = Compile(@"^\d+$");
    private static readonly Regex AmbiguousPattern = Compile(@"^\d{1,3}[.,]\d{3}$");
    private static readonly Regex UsGroups         = Compile(@"^\d{1,3}(,\d{3})+(\.\d+)?$");
    private static readonly Regex ArGroups         = Compile(@"^\d{1,3}(\.\d{3})+(,\d+)?$");
    private static readonly Regex DotDecimal       = Compile(@"^\d+\.\d+$");
    private static readonly Regex CommaDecimal     = Compile(@"^\d+,\d+$");

    /// <summary>The section's figures are in thousands of pesos ("Importes expresados en miles de $").</summary>
    public const decimal Thousands = 1000m;

    /// <summary>True for the report's "none" marker: "-" (or an en/em dash) alone.</summary>
    public static bool IsNone(string? token)
        => token is not null && token.Trim() is "-" or "–" or "—";

    /// <summary>The style a section's unambiguous tokens agree on.</summary>
    public static NosisNumberStyle InferStyle(IEnumerable<NosisReportLine> lines)
    {
        var us = false;
        var ar = false;
        foreach (var token in lines.SelectMany(l => l.Words).Select(w => Clean(w.Text)))
        {
            switch (Classify(token))
            {
                case Shape.Us: us = true; break;
                case Shape.Ar: ar = true; break;
            }
        }

        return (us, ar) switch
        {
            (true, true)  => NosisNumberStyle.Mixed,
            (true, false) => NosisNumberStyle.Us,
            (false, true) => NosisNumberStyle.Ar,
            _             => NosisNumberStyle.Unknown,
        };
    }

    /// <summary>
    /// Parses an amount. "-" → (true, null). An ambiguous token the style cannot decide → (true, null).
    /// Anything that is not a number → false.
    /// </summary>
    public static bool TryAmount(string? token, NosisNumberStyle style, out decimal? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        if (IsNone(token))
            return true;

        var t = Clean(token);
        var negative = t.StartsWith('-');
        if (negative)
            t = t[1..];

        var shape = Classify(t);
        if (shape == Shape.Ambiguous)
        {
            shape = style switch
            {
                NosisNumberStyle.Us => Shape.Us,
                NosisNumberStyle.Ar => Shape.Ar,
                _                   => Shape.NotNumber,
            };
            if (shape == Shape.NotNumber)
                return true;   // ambiguous and undecidable: null, not a guess
        }

        string invariant;
        switch (shape)
        {
            case Shape.Plain: invariant = t; break;
            case Shape.Us:    invariant = t.Replace(",", string.Empty, StringComparison.Ordinal); break;
            case Shape.Ar:    invariant = t.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.'); break;
            default:          return false;
        }

        if (!decimal.TryParse(invariant, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed))
            return false;

        value = negative ? -parsed : parsed;
        return true;
    }

    /// <summary>Parses a whole number. "-" → (true, null). A value with a fraction is not an integer → false.</summary>
    public static bool TryInteger(string? token, NosisNumberStyle style, out int? value)
    {
        value = null;
        if (!TryAmount(token, style, out var amount))
            return false;

        if (amount is null)
            return true;

        if (amount != decimal.Truncate(amount.Value) || amount is < int.MinValue or > int.MaxValue)
            return false;

        value = (int)amount.Value;
        return true;
    }

    /// <summary>First token of <paramref name="text"/> that reads as an amount, or null.</summary>
    public static decimal? FirstAmount(string? text, NosisNumberStyle style)
    {
        foreach (var token in Tokens(text))
        {
            if (TryAmount(token, style, out var value) && value is not null)
                return value;
        }

        return null;
    }

    /// <summary>Every token of <paramref name="text"/> that reads as an amount, in order.</summary>
    public static IReadOnlyList<decimal> Amounts(string? text, NosisNumberStyle style)
    {
        var amounts = new List<decimal>();
        foreach (var token in Tokens(text))
        {
            if (TryAmount(token, style, out var value) && value is not null)
                amounts.Add(value.Value);
        }

        return amounts;
    }

    /// <summary>First token of <paramref name="text"/> that reads as an integer, or null.</summary>
    public static int? FirstInteger(string? text, NosisNumberStyle style)
    {
        foreach (var token in Tokens(text))
        {
            if (TryInteger(token, style, out var value) && value is not null)
                return value;
        }

        return null;
    }

    /// <summary>Scales by <see cref="Thousands"/> when the section is in thousands.</summary>
    public static decimal? ToPesos(decimal? value, bool inThousands)
        => value is null ? null : inThousands ? value * Thousands : value;

    private static Shape Classify(string t)
    {
        if (t.Length == 0)
            return Shape.NotNumber;
        if (PlainPattern.IsMatch(t))
            return Shape.Plain;
        if (AmbiguousPattern.IsMatch(t))
            return Shape.Ambiguous;
        if (UsGroups.IsMatch(t) || DotDecimal.IsMatch(t))
            return Shape.Us;
        if (ArGroups.IsMatch(t) || CommaDecimal.IsMatch(t))
            return Shape.Ar;
        return Shape.NotNumber;
    }

    /// <summary>"$", "%" and surrounding spaces removed; a leading "-" kept.</summary>
    private static string Clean(string token)
    {
        var t = token.Trim().TrimEnd('%').Trim();
        if (t.StartsWith('$'))
            t = t[1..].Trim();
        return t;
    }

    private static IEnumerable<string> Tokens(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split([' ', '\t', ':', ';', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
                  .Select(t => t.Trim().TrimEnd(';'))
                  .Select(t => t.EndsWith('.') && !t.EndsWith("..", StringComparison.Ordinal) ? t[..^1] : t);

    private static Regex Compile(string pattern)
        => new(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));
}
