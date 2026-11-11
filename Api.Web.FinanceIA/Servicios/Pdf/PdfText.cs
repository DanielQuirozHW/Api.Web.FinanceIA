// Project : Api.Web.FinanceIA
// Created : 2026-11-09

using System.Text.RegularExpressions;

namespace Api.Web.FinanceIA.Servicios.Pdf;

/// <summary>Small text normalisations the report applies to values it prints. Pure.</summary>
internal static class PdfText
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>The typographic minus (U+2212) the report prints before a negative figure.</summary>
    internal const char Minus = '\u2212';

    /// <summary>
    /// A sentence written for the screen ("-$ 500") as the report prints it ("−$ 500"). Only the ASCII hyphen of a
    /// negative peso amount changes — the one form <see cref="CashFlowNarrativeService"/> writes — so the words stay
    /// identical to the Portal's and the chat's, and only the sign glyph follows the report's convention.
    /// </summary>
    internal static string TypographicMinus(string? sentence) =>
        string.IsNullOrEmpty(sentence) ? string.Empty : sentence.Replace("-$ ", Minus + "$ ", StringComparison.Ordinal);

    /// <summary>Every whitespace run as one space, trimmed: a name typed "Ana  Pérez" prints "Ana Pérez".</summary>
    internal static string CollapseWhitespace(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : Whitespace.Replace(text, " ").Trim();
}
