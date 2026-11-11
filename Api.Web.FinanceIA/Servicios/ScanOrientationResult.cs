// Project : Api.Web.FinanceIA
// Created : 2026-11-03

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// The answer of an <see cref="IScanOrientationDetector"/>: one entry per page asked about, in the
/// order asked, holding the clockwise turn (0, 90, 180 or 270) that makes the page read upright, or
/// null — "unknown" — when the page was not judged. An unknown page is rendered as it is.
/// </summary>
/// <param name="Rotations">Aligned with the page indexes the detector was given.</param>
/// <param name="Usage">The billable call, or null when no request reached the API.</param>
public sealed record ScanOrientationResult(IReadOnlyList<int?> Rotations, ScanOrientationUsage? Usage)
{
    /// <summary>Every one of <paramref name="pages"/> pages unknown, with no call made.</summary>
    public static ScanOrientationResult Unknown(int pages) => new(new int?[pages], null);
}
