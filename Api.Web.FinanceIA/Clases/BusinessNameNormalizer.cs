// Project : Api.Web.FinanceIA
// Created : 2026-11-06

using System.Text;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Normalizes a company's razón social before it is stored on an Entity or handed to the Portal's
/// autofill: control characters out, whitespace collapsed, and quote characters that WRAP the whole
/// name stripped (repeated layers included), while quotes INSIDE the name are kept.
/// </summary>
/// <remarks>
/// <para>
/// Why: a PyME was created as <c>"silo Chico S.A."</c> — literal quotes stored as part of the name,
/// which every list, PDF and notification then repeated. The name comes from the Portal
/// (<c>CreateEntityRequest.BusinessName</c>, itself autofilled from ARCA's razonSocial), and the API
/// only trimmed it.
/// </para>
/// <para>
/// What it does NOT do: change letter case ("silo Chico" stays as typed — ARCA's casing is ARCA's and
/// the analyst's is theirs), touch punctuation other than wrapping quotes, or rewrite stored vendor
/// payloads (FiscalDataJson and the other blobs stay exactly as received).
/// </para>
/// <para>
/// A pair is stripped only when it really wraps the name. Both ends must be quotes of the same family
/// (double: <c>" “ ” „ ‟ « »</c>; single: <c>' ‘ ’ ‚ ‛ ‹ ›</c>), and the same-family quotes left
/// inside must still pair up, each pair opening at the start of the text or after a space. So
/// <c>"Sociedad "La Esperanza" S.A."</c> becomes <c>Sociedad "La Esperanza" S.A.</c>, but
/// <c>"Uno" y "Dos"</c> — which starts and ends with a quote that are NOT a pair — is left alone. An
/// apostrophe between two letters (<c>O'Brien</c>) is part of a word and never counts as a quote.
/// </para>
/// </remarks>
public static class BusinessNameNormalizer
{
    private const string DoubleQuotes = "\"“”„‟«»";
    private const string SingleQuotes = "'‘’‚‛‹›";

    /// <summary>
    /// Returns the normalized name, or an empty string when nothing is left (null, blank, only
    /// quotes / control characters) — the caller then answers its usual "razón social obligatoria".
    /// </summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        var name = CollapseWhitespace(raw);

        while (name.Length >= 2)
        {
            var family = FamilyOf(name[0]);
            if (family is null || FamilyOf(name[^1]) != family) break;

            var inner = name[1..^1];
            if (!InnerQuotesPairUp(inner, family)) break;

            name = inner.Trim();
        }

        // A name made of nothing but quote characters (a lone '"', or '" "') is no name at all.
        return name.All(c => FamilyOf(c) is not null) ? string.Empty : name;
    }

    /// <summary>
    /// Control characters out — line breaks and tabs become a space, any other C0/C1 control and the
    /// invisible zero-width characters are dropped — then every whitespace run (no-break space
    /// included) becomes one space, and the ends are trimmed.
    /// </summary>
    private static string CollapseWhitespace(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        var pendingSpace = false;

        foreach (var c in raw)
        {
            if (IsInvisible(c)) continue;

            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (char.IsControl(c)) continue;

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static bool IsInvisible(char c)
        => c is '​' or '‌' or '‍' or '⁠' or '﻿';

    private static string? FamilyOf(char c)
        => DoubleQuotes.Contains(c) ? DoubleQuotes
         : SingleQuotes.Contains(c) ? SingleQuotes
         : null;

    /// <summary>
    /// Whether the quotes of <paramref name="family"/> inside <paramref name="inner"/> pair up, each
    /// pair OPENING at the start of the text or after whitespace / an opening bracket. When they do,
    /// the outer two characters are a genuine wrapping pair; when they do not (<c>Uno" y "Dos</c>, where
    /// the first inner quote closes a word), the outer quotes belong to inner quoted words and stay.
    /// </summary>
    private static bool InnerQuotesPairUp(string inner, string family)
    {
        var expectOpener = true;

        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (!family.Contains(c)) continue;

            // An apostrophe inside a word (O'Brien, D'Angelo) is not a quote.
            if (family == SingleQuotes
                && i > 0 && i < inner.Length - 1
                && char.IsLetterOrDigit(inner[i - 1]) && char.IsLetterOrDigit(inner[i + 1]))
            {
                continue;
            }

            if (expectOpener)
            {
                var opensHere = i == 0 || char.IsWhiteSpace(inner[i - 1]) || inner[i - 1] is '(' or '[';
                if (!opensHere) return false;
            }

            expectOpener = !expectOpener;
        }

        return expectOpener;
    }
}
