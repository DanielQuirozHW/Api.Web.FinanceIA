// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// What the figures of a <see cref="LegajoIntegrityCheck"/> are (its <c>figureKind</c>), so the Portal formats
/// expected, actual and diff correctly. A rule over many fields (a range, a sign) carries none.
/// </summary>
public static class LegajoFigureKinds
{
    /// <summary>Pesos; the diff is in pesos.</summary>
    public const string Money = "money";

    /// <summary>A month as YYYYMM; the diff is in months.</summary>
    public const string Month = "month";

    /// <summary>A year; the diff is in years.</summary>
    public const string Year = "year";

    /// <summary>Every value.</summary>
    public static readonly IReadOnlyList<string> Known = [Money, Month, Year];
}
