// Project : Api.Web.FinanceIA
// Created : 2026-05-08

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Lufe;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Deterministic credit-scoring engine.
/// Computes a 0–100 risk score from financial ratios, BCRA (the Central de Deudores situación, its
/// trend, and — since algorithm version 5 — the rejected-cheques block), and macroeconomic data,
/// adjusted — since algorithm version 6 — by a penalty for a drop in LUFE-reported monthly sales
/// and — since algorithm version 8 — by a penalty for the share of the CASFOG-reported guarantee
/// balance in arrears.
/// Parameters are loaded per-tenant from <c>[dbo].[ScoringConfig]</c>; built-in
/// defaults are used as fallback when no configuration exists for the tenant.
/// <para>
/// Deterministic literally: the engine reads no clock, so the result is a pure function of the
/// tenant configuration and the arguments below. Scoring the same payload again — next month, or
/// years later while reproducing an audit — yields the same score. Which anchoring produced a given
/// persisted result is recorded by
/// <see cref="Api.Web.FinanceIA.Servicios.ScoringService.ScoringAlgoVersion"/>.
/// </para>
/// </summary>
public interface IScoringService
{
    /// <summary>
    /// Calculates the composite risk score for an entity.
    /// </summary>
    /// <param name="tenantId">Firebase tenant slug used to load per-tenant config.</param>
    /// <param name="entitySector">Entity sector (e.g. "Industria", "Servicios"). Null falls back to "General".</param>
    /// <param name="ratios">Financial ratios extracted from the balance sheet.</param>
    /// <param name="bcraData">
    /// BCRA enrichment data, or <c>null</c> if unavailable. The engine reads <see cref="BcraData.Deuda"/>
    /// for the component's base and trend, and <see cref="BcraData.Cheques"/> for the rejected-cheques
    /// penalty — a null, failed or empty cheques block is a zero penalty, never a fabricated risk.
    /// </param>
    /// <param name="macroData">Macroeconomic variables, or <c>null</c> if unavailable.</param>
    /// <param name="ventasMensuales">
    /// The monthly sales series from the entity's LUFE snapshot, or <c>null</c> when the tenant does
    /// not use LUFE, the entity has no snapshot, or the stored blob would not parse. Drives the
    /// sales-drop penalty on the final score (algorithm version 6): a null, empty or unusable series
    /// is a zero penalty, never a fabricated risk. Only the series is taken, never the whole
    /// <c>LufeData</c> — that carries the company name, CUIT and domicilio, and the engine has no
    /// business holding identifying data to compare two numbers.
    /// </param>
    /// <param name="garantiasPorSgr">
    /// The per-SGR guarantee rows from the entity's CASFOG snapshot, or <c>null</c> when the tenant
    /// does not use CASFOG, the entity has no snapshot, or the stored blob would not parse. Drives the
    /// guarantee-arrears penalty on the final score (algorithm version 8): a null, empty or unusable
    /// set of rows is a zero penalty, never a fabricated risk. Only the rows are taken, never the
    /// whole <c>CasfogData</c> — that carries the related parties' business names and CUITs, and the
    /// engine has no business holding identifying data to divide one sum by another.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="ScoringResult"/> with the composite score, risk level, and breakdown.
    /// Never throws; on an unexpected error returns a neutral placeholder (score = 50, level = Medium)
    /// with <see cref="ScoringResult.EngineFailed"/> = true. That placeholder is not a score and must
    /// never be persisted as one — <c>AnalysisScoringStage.ScoreAsync</c> throws on it.
    /// </returns>
    Task<ScoringResult> CalculateAsync(
        string                      tenantId,
        string?                     entitySector,
        RatiosData                  ratios,
        BcraData?                   bcraData,
        BcraMacroData?              macroData,
        List<LufeVentaCompraMes>?   ventasMensuales,
        List<CasfogGarantiaSgr>?    garantiasPorSgr,
        CancellationToken           ct);

    /// <summary>
    /// The tenant's CURRENT level cutoffs on its base profile — the values a score computed now
    /// would be classified with — resolved by the same rule the engine uses
    /// (<see cref="ScoreLevelCutoffs.Resolve"/>), falling back per key to the code defaults.
    /// <para>
    /// For labelling rows whose <c>ScoreBreakdownJson</c> predates <c>levelCutoffs</c>. A row that
    /// carries its own <c>levelCutoffs</c> must be labelled with those: they are what that score was
    /// actually classified with, even after the tenant edited its config, and they also reflect any
    /// sector-suffixed override this base read leaves out. Unlike <see cref="CalculateAsync"/> this
    /// may throw on a database failure; the caller decides how to degrade.
    /// </para>
    /// </summary>
    /// <param name="tenantId">Firebase tenant slug whose config is read.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ScoreLevelCutoffs> GetLevelCutoffsAsync(string tenantId, CancellationToken ct);
}
