// Project : Api.Web.FinanceIA
// Created : 2026-11-02

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Lufe;

namespace Api.Web.FinanceIA.Servicios.AnalysisRuns;

/// <summary>
/// What <see cref="ScoreReliabilityFactsBuilder"/> needs besides the engine's result: everything the
/// scoring stage already holds when it scores. No I/O is needed to build it.
/// </summary>
/// <param name="RatiosComputed">True when the ratios came from the deterministic recompute (<see cref="RatiosOutcome.ComputedRatiosJson"/> non-null), false when they are the model's own.</param>
/// <param name="BalanceJson">The balance the analysis persists (the integrity verdict is read from it).</param>
/// <param name="FiscalYear">The fiscal year the analysis was requested for.</param>
/// <param name="BcraData">The BCRA payload the engine scored.</param>
/// <param name="Lufe">The entity's LUFE snapshot, already deserialised for the engine; null when absent or unreadable.</param>
/// <param name="LufeSnapshotAt">When that snapshot was taken (<c>Entities.LufeDataUpdatedAt</c>).</param>
/// <param name="Casfog">The entity's CASFOG snapshot, already deserialised for the engine; null when absent or unreadable.</param>
internal sealed record ScoreReliabilityInputs(
    bool        RatiosComputed,
    string?     BalanceJson,
    int?        FiscalYear,
    BcraData?   BcraData,
    LufeData?   Lufe,
    DateTime?   LufeSnapshotAt,
    CasfogData? Casfog);

/// <summary>
/// Builds <see cref="ScoreReliabilityFacts"/> — the ONE place it is built, called by
/// <see cref="AnalysisScoringStage.ScoreAsync"/> for both a fresh analysis and a re-score, so the two can
/// never produce different facts from the same inputs (score credibility 1b-2a).
/// <para>
/// Pure: no clock, no I/O, no logging. It copies codes, flags, dates, periods and counts only — never
/// a name, a CUIT or an amount from the vendor payloads it reads, all of which hold identifying data.
/// Ages are deliberately NOT computed here: 1b-2b measures them at read time against the score's own
/// date, so the scoring step stays a pure function of its inputs.
/// </para>
/// </summary>
internal static class ScoreReliabilityFactsBuilder
{
    /// <summary>The deterministic ratio recompute produced the ratios the score read (S8).</summary>
    public const string RatiosComputed = "computed";

    /// <summary>The score read the model's own ratios (no balance, or the recompute failed) (S8).</summary>
    public const string RatiosModel = "model";

    /// <summary>Assembles the facts block. Throws only on a null argument.</summary>
    public static ScoreReliabilityFacts Build(ScoringResult scoring, ScoreReliabilityInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(scoring);
        ArgumentNullException.ThrowIfNull(inputs);

        var corrections = BalanceCorrections.ManualCorrectionsReader.Read(inputs.BalanceJson);

        return new ScoreReliabilityFacts
        {
            BcraSituacionUnreadable = scoring.BcraSituacionUnreadable,
            MacroImputed            = scoring.MacroImputed,
            SectorFallback          = scoring.SectorFallback,
            SectorRaw               = scoring.SectorFallback ? scoring.SectorRaw : null,
            NegativeEquity          = scoring.NegativeEquity,
            ConfigSource            = scoring.ConfigSource,
            ConfigHash              = scoring.ConfigHash,
            RatiosSource            = inputs.RatiosComputed ? RatiosComputed : RatiosModel,
            FiscalYear              = inputs.FiscalYear,
            Integrity               = BuildIntegrity(inputs.BalanceJson, inputs.FiscalYear),
            ManualCorrections       = corrections?.Count,
            ManualCorrectionsLastAt = corrections?.LastAt,
            Bcra                    = BuildBcra(inputs.BcraData, scoring.ChequesEvaluated),
            Lufe                    = BuildLufe(inputs.Lufe, inputs.LufeSnapshotAt),
            Casfog                  = inputs.Casfog is { } casfog
                ? new ScoreReliabilityCasfogFacts { Estado = casfog.Estado.ToString(), FetchedAt = casfog.FetchedAt }
                : null,
        };
    }

    /// <summary>
    /// The caveat codes these facts raise, for the one Information log line per scored analysis. Codes
    /// only — never the sector text or anything identifying. A caveat is a fact, not a verdict: weighing
    /// them is 1b-2b's job. Empty when nothing is flagged.
    /// </summary>
    public static IReadOnlyList<string> CaveatCodes(ScoringResult scoring, ScoreReliabilityFacts facts)
    {
        ArgumentNullException.ThrowIfNull(scoring);
        ArgumentNullException.ThrowIfNull(facts);

        var codes = new List<string>();
        void Flag(bool condition, string code)
        {
            if (condition)
                codes.Add(code);
        }

        Flag(scoring.RatiosLowCoverageFallback,                      "ratios_low_coverage");
        Flag(facts.RatiosSource == RatiosModel,                      "ratios_from_model");
        Flag(facts.NegativeEquity == true,                           "negative_equity");
        Flag(facts.BcraSituacionUnreadable,                          "bcra_situacion_unreadable");
        Flag(facts.Bcra?.DeudaEstado == nameof(BcraDeudaConsultaEstado.LookupFailed), "bcra_deuda_lookup_failed");
        Flag(facts.Bcra is { ChequesEvaluated: true }
             && facts.Bcra.ChequesEstado == nameof(BcraChequesConsultaEstado.LookupFailed), "cheques_lookup_failed");
        Flag(facts.MacroImputed,                                     "macro_imputed");
        Flag(facts.SectorFallback,                                   "sector_fallback");
        Flag(scoring.ThresholdFallbacks.Count > 0,                   "config_threshold_fallback");
        var presentationOnly = facts.Integrity is { Passed: false } failed
                               && BalanceIntegrityService.FailsOnlyOnPresentation(
                                   failed.FailedCheckNames, failed.NetProfitSignFlip, failed.NetProfitColumnMix);
        Flag(facts.Integrity is { Passed: false } && !presentationOnly, "integrity_failed");
        Flag(presentationOnly,                                       "integrity_presentation");
        Flag(facts.Integrity is { Verified: false },                 "integrity_unverified");
        Flag(facts.Integrity?.FiscalYearStatus == FiscalYearCheck.Mismatch, "fiscal_year_mismatch");
        Flag(facts.Integrity?.AmountsUnitAssumed == true,            "amounts_unit_assumed");
        Flag(facts.Integrity?.DocumentKind == TrialBalanceNormalizer.DocumentKind, "trial_balance");
        Flag(facts.ManualCorrections > 0,                            "manual_correction");
        Flag(facts.Lufe?.VentasEstado == ScoreReliabilityLufeVentasEstados.CarriedForward, "lufe_carried_forward");
        Flag(facts.Lufe?.VentasEstado == ScoreReliabilityLufeVentasEstados.LookupFailed,   "lufe_lookup_failed");
        Flag(facts.Casfog?.Estado == nameof(CasfogConsultaEstado.LookupFailed),            "casfog_lookup_failed");

        return codes;
    }

    private static ScoreReliabilityIntegrityFacts? BuildIntegrity(string? balanceJson, int? fiscalYear)
    {
        // Never throws: a missing, malformed or pre-2.18 blob yields null, which omits the member.
        var integrity = ExtractionIntegrityReader.Read(balanceJson, fiscalYear);
        if (integrity is null)
            return null;

        return new ScoreReliabilityIntegrityFacts
        {
            Passed             = integrity.Passed,
            Verified           = integrity.Verified,
            NotVerifiedReasons = integrity.NotVerifiedReasons,
            FiscalYearStatus   = integrity.FiscalYear?.Status,
            AmountsUnitAssumed = integrity.AmountsUnitAssumed,
            // A code, never a figure or a label.
            DocumentKind       = integrity.DocumentKind,
            FailedChecks       = integrity.FailedChecks.Count,
            // Check codes only (BalanceIntegrityService constants): no figure, no name, no CUIT.
            FailedCheckNames   = [.. integrity.FailedChecks],
            NetProfitSignFlip  = integrity.NetProfitSignFlip,
            NetProfitColumnMix = integrity.NetProfitColumnMix,
            // Ratio keys only (BalanceIntegrityService writes ScoringDefaults.ThresholdRatios keys).
            AffectedRatios     = integrity.AffectedRatios.Where(k => !string.IsNullOrWhiteSpace(k))
                                          .Distinct(StringComparer.Ordinal).ToList(),
        };
    }

    private static ScoreReliabilityBcraFacts? BuildBcra(BcraData? bcra, bool chequesEvaluated)
    {
        if (bcra is null)
            return null;

        // Estado, period and date only. BcraDeudaData and BcraChequesData also carry the debtor's
        // Denominacion and Identificacion (CUIT) and every amount — none of which is copied.
        return new ScoreReliabilityBcraFacts
        {
            DeudaEstado       = bcra.Deuda?.Estado.ToString(),
            PeriodoConsultado = bcra.Deuda?.PeriodoConsultado,
            DeudaFetchedAt    = bcra.Deuda?.FetchedAt,
            ChequesEstado     = bcra.Cheques?.Estado.ToString(),
            ChequesEvaluated  = chequesEvaluated,
        };
    }

    private static ScoreReliabilityLufeFacts? BuildLufe(LufeData? lufe, DateTime? snapshotAt)
    {
        if (lufe is null)
            return null;

        var serie = lufe.VentasComprasMensual;

        string estado = lufe.VentasComprasMensualEstado switch
        {
            LufeVentasComprasEstados.CarriedForward => ScoreReliabilityLufeVentasEstados.CarriedForward,
            LufeVentasComprasEstados.LookupFailed   => ScoreReliabilityLufeVentasEstados.LookupFailed,
            null when serie is { Count: > 0 }       => ScoreReliabilityLufeVentasEstados.Fresh,
            null                                    => ScoreReliabilityLufeVentasEstados.None,
            // An estado this build does not know: pass the code through rather than guess.
            { } unknown                             => unknown,
        };

        return new ScoreReliabilityLufeFacts
        {
            VentasEstado        = estado,
            VentasAsOf          = estado == ScoreReliabilityLufeVentasEstados.CarriedForward
                ? lufe.VentasComprasMensualAsOf
                : null,
            VentasUltimoPeriodo = LatestSalesPeriodo(serie),
            SnapshotAt          = snapshotAt,
        };
    }

    /// <summary>
    /// The greatest well-formed YYYYMM period carrying a sales figure — the same months the sales-drop
    /// signal reads (a null <c>Ventas</c> is "no declaration", not a datum). Null when there is none.
    /// </summary>
    internal static string? LatestSalesPeriodo(List<LufeVentaCompraMes>? serie)
    {
        if (serie is null)
            return null;

        string? latest = null;
        foreach (var mes in serie)
        {
            if (mes is not { Ventas: not null }) continue;
            if (DeflactorIpc.ShiftPeriodoMonths(mes.Periodo, 0) is not { } periodo) continue;

            if (latest is null || string.CompareOrdinal(periodo, latest) > 0)
                latest = periodo;
        }

        return latest;
    }
}
