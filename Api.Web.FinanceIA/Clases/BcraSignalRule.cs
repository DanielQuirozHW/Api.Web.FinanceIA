// Project : Api.Web.FinanceIA
// Created : 2026-10-25

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;

namespace Api.Web.FinanceIA.Clases;

/// <summary>One entity's BCRA comparison.</summary>
/// <param name="Row">The comparison row picked for the entity.</param>
/// <param name="Then">Worst situación of the analysis' frozen snapshot; null when it supports none.</param>
/// <param name="Now">Worst situación of the entity's current snapshot; null when it supports none.</param>
internal sealed record BcraEntityComparison(DashboardBcraComparisonRow Row, int? Then, int? Now)
{
    /// <summary>Both readings exist and the situación is worse now than at the analysis.</summary>
    public bool IsWorsened => Then is int then && Now is int now && now > then;

    /// <summary>Both readings exist, so whether it worsened is a real answer rather than "unknown".</summary>
    public bool IsComparable => Then.HasValue && Now.HasValue;
}

/// <summary>
/// The selection behind "did this company's BCRA situación get worse since the analysis that backs its cupo",
/// in one place: the portfolio dashboard's bcraSignals and <see cref="CupoAlertService"/>'s
/// PYME_BCRA_WORSENED both read it, so the two can never disagree about an entity.
/// <para>
/// Extracted verbatim from <c>DashboardService.BuildBcraSignalsAsync</c>.
/// </para>
/// </summary>
internal static class BcraSignalRule
{
    /// <summary>
    /// One comparison per entity: against the most recent analysis behind its Active cupos (one cupo per
    /// currency, each possibly from a different analysis). A cupo whose analysis has no result has nothing
    /// to compare against, so an entity whose every cupo is like that is absent.
    /// </summary>
    public static IReadOnlyList<BcraEntityComparison> PerEntity(IEnumerable<DashboardBcraComparisonRow> rows)
        => rows
            .Where(r => r.ResultId.HasValue && r.AnalysisId.HasValue)
            .GroupBy(r => r.EntityId)
            .Select(g => g.OrderByDescending(r => r.GeneratedAt).ThenBy(r => r.ResultId).First())
            .Select(r => new BcraEntityComparison(
                r,
                Situacion(r.ThenEstadoRaw, r.ThenSituacionMaximaRaw),
                Situacion(r.NowEstadoRaw, r.NowSituacionMaximaRaw)))
            .ToList();

    /// <summary>
    /// The worst situación (1..6) of one snapshot, from the two scalars JSON_VALUE reads out of its
    /// <c>$.deuda.estado</c> and <c>$.deuda.situacionMaxima</c>; null when it supports none. The reading
    /// <see cref="PerEntity"/> applies to both sides of a comparison, and the portfolio dashboard's
    /// bcraCurrent applies to the entity's current snapshot alone.
    /// </summary>
    public static int? Situacion(string? estadoRaw, string? situacionMaximaRaw)
        => BcraSituacionRule.Worst(BcraSituacionRule.Read(estadoRaw, situacionMaximaRaw));
}
