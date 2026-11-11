// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// CUIT/CUIL helpers for the legajo pipeline: reduce to digits, validate the check digit, and format
/// as <c>XX-XXXXXXXX-X</c>. Pure. Nothing here logs — a CUIT is PII.
/// </summary>
public static class LegajoCuit
{
    private static readonly int[] Weights = [5, 4, 3, 2, 7, 6, 5, 4, 3, 2];

    /// <summary>The 11 digits of <paramref name="value"/>, or null when it does not hold exactly 11.</summary>
    public static string? Digits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
        return digits.Length == 11 ? digits : null;
    }

    /// <summary>True when <paramref name="digits"/> is 11 digits whose last one is the modulo-11 check digit.</summary>
    public static bool HasValidCheckDigit(string? digits)
    {
        if (digits is null || digits.Length != 11 || !digits.All(char.IsAsciiDigit))
            return false;

        var sum = 0;
        for (var i = 0; i < 10; i++)
            sum += (digits[i] - '0') * Weights[i];

        var expected = (11 - sum % 11) % 11;
        return expected != 10 && expected == digits[10] - '0';
    }

    /// <summary>Formats 11 digits as <c>XX-XXXXXXXX-X</c>, the form [dbo].[Entities].CUIT uses.</summary>
    public static string Format(string digits)
    {
        ArgumentNullException.ThrowIfNull(digits);
        if (digits.Length != 11)
            throw new ArgumentException("A CUIT has 11 digits.", nameof(digits));

        return $"{digits[..2]}-{digits[2..10]}-{digits[10]}";
    }
}
