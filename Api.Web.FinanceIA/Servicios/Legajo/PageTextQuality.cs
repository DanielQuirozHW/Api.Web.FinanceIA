// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// The text-quality measurements of one PDF page. Figures only: the page's text itself is never kept.
/// </summary>
/// <param name="Page">1-based page number.</param>
/// <param name="Chars">Non-whitespace characters on the page.</param>
/// <param name="Letters">Of which letters.</param>
/// <param name="Digits">Of which digits.</param>
/// <param name="SuspiciousRatio">Share of <paramref name="Chars"/> that are replacement, control, private-use or "(cid:" characters, 0..1.</param>
/// <param name="AverageWordLength">Mean whitespace-separated token length, 0 when the page has no token.</param>
/// <param name="Verdict">One of <see cref="PageTextVerdicts"/>.</param>
public sealed record PageTextQuality(
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("chars")] int Chars,
    [property: JsonIgnore] int Letters,
    [property: JsonPropertyName("digits")] int Digits,
    [property: JsonPropertyName("suspiciousRatio")] decimal SuspiciousRatio,
    [property: JsonIgnore] decimal AverageWordLength,
    [property: JsonPropertyName("verdict")] string Verdict);
