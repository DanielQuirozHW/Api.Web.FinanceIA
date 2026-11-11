// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-02

using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Api.Web.FinanceIA.Servicios.ScoreReliability;

namespace Api.Web.FinanceIA.Tests.Servicios.ScoreReliability;

/// <summary>
/// Shared fixtures for the score-reliability verdict (score credibility 1b-2b). Every breakdown is written by
/// the REAL <see cref="AnalysisScoringStage.BuildScoreBreakdownJson"/>, so the tests read the persisted shape,
/// not a hand-typed approximation of it.
/// </summary>
internal static class ScoreReliabilityFixture
{
    /// <summary>The default score date of the evaluator tests.</summary>
    public static readonly DateTime ScoreDate = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    public static readonly IReadOnlyList<string> RatioKeys =
    [
        "liquidez_corriente", "prueba_acida", "endeudamiento", "leverage",
        "cobertura_intereses", "roe", "roa", "margen_neto",
    ];

    /// <summary>All eight ratios measured, BCRA in, no fallback.</summary>
    public static ScoringResult CleanScoring() => new()
    {
        Score     = 42.5m,
        Level     = "Medium",
        Breakdown = RatioKeys.ToDictionary(
            k => k,
            _ => new RatioBreakdownEntry(25m, "Saludable", Excluded: false, EffectiveWeight: 0.125m)),
        ScoringAlgoVersion = "9",
    };

    /// <summary>
    /// The scoring an RT 6 statement leaves (financial results aggregated with RECPAM): interest coverage
    /// excluded with <paramref name="exclusionReason"/>, every other ratio measured, the composite renormalised.
    /// </summary>
    public static ScoringResult InterestCoverageUnreadScoring(string? exclusionReason = RatioExclusionReasons.Unread)
    {
        var scoring = CleanScoring();
        scoring.Breakdown["cobertura_intereses"] = new RatioBreakdownEntry(
            50m, "Atencion", Excluded: true, EffectiveWeight: 0m, ExclusionReason: exclusionReason);
        scoring.NonComputableRatios = ["cobertura_intereses"];
        return scoring;
    }

    /// <summary>The internal composite before the floor in <see cref="FlooredScoring"/>: 820/1000 on the displayed scale.</summary>
    public const decimal FlooredBefore = 18m;

    /// <summary>
    /// A score the critical floor MOVED (score credibility 1b-5): composite <see cref="FlooredBefore"/> raised to
    /// the liquidez floor of 60 (400/1000) by <paramref name="critical"/>.
    /// </summary>
    public static ScoringResult FlooredScoring(params string[] critical)
    {
        var scoring = CleanScoring();
        scoring.ScoreBeforeFloor = FlooredBefore;
        scoring.AppliedFloor     = 60m;
        scoring.Score            = 60m;
        scoring.CriticalRatios   = critical.Length == 0 ? ["liquidez_corriente"] : [.. critical];
        return scoring;
    }

    /// <summary>Integrity that FAILED (verified, two checks) over the ratio keys <paramref name="affected"/>; null = a pre-1b-5 block.</summary>
    public static ScoreReliabilityIntegrityFacts FailedIntegrity(IReadOnlyList<string>? affected) => new()
    {
        Passed             = false,
        Verified           = true,
        FiscalYearStatus   = FiscalYearCheck.Match,
        AmountsUnitAssumed = false,
        FailedChecks       = 2,
        AffectedRatios     = affected,
    };

    /// <summary>Facts that raise nothing at <see cref="ScoreDate"/>.</summary>
    public static ScoreReliabilityFacts CleanFacts() => new()
    {
        BcraSituacionUnreadable = false,
        MacroImputed            = false,
        SectorFallback          = false,
        NegativeEquity          = false,
        ConfigSource            = ScoringConfigSources.Tenant,
        ConfigHash              = "0123456789abcdef",
        RatiosSource            = ScoreReliabilityFactsBuilder.RatiosComputed,
        FiscalYear              = 2025,
        Integrity = new ScoreReliabilityIntegrityFacts
        {
            Passed             = true,
            Verified           = true,
            FiscalYearStatus   = FiscalYearCheck.Match,
            AmountsUnitAssumed = false,
            FailedChecks       = 0,
        },
        Bcra = new ScoreReliabilityBcraFacts
        {
            DeudaEstado       = nameof(BcraDeudaConsultaEstado.DebtFound),
            PeriodoConsultado = "202604",
            DeudaFetchedAt    = new DateTime(2026, 6, 15, 11, 0, 0, DateTimeKind.Utc),
            ChequesEstado     = nameof(BcraChequesConsultaEstado.NoChequesReported),
            ChequesEvaluated  = true,
        },
        Lufe = new ScoreReliabilityLufeFacts
        {
            VentasEstado        = ScoreReliabilityLufeVentasEstados.Fresh,
            VentasUltimoPeriodo = "202604",
            SnapshotAt          = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc),
        },
        Casfog = new ScoreReliabilityCasfogFacts
        {
            Estado    = nameof(CasfogConsultaEstado.DataFound),
            FetchedAt = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc),
        },
    };

    /// <summary>The persisted breakdown for <paramref name="scoring"/> + <paramref name="facts"/> (null facts → JSON null).</summary>
    public static string Json(ScoringResult? scoring = null, ScoreReliabilityFacts? facts = null)
        => AnalysisScoringStage.BuildScoreBreakdownJson(scoring ?? CleanScoring(), facts);

    /// <summary>The breakdown as a row scored before #300 has it: no <c>reliabilityFacts</c> key at all.</summary>
    public static string JsonBeforeFacts(ScoringResult? scoring = null)
    {
        var root = JsonNode.Parse(Json(scoring, CleanFacts()))!.AsObject();
        root.Remove("reliabilityFacts");
        foreach (var (_, entry) in root["breakdown"]!.AsObject())
            entry!.AsObject().Remove("measured");
        return root.ToJsonString();
    }

    public static ScoreReliabilityRow Row(
        decimal?  score      = 42.5m,
        string?   enrichment = "Complete",
        string?   kind       = AnalysisKinds.Balance,
        int?      fiscalYear = 2025,
        DateTime? generated  = null,
        DateTime? rescored   = null)
        => new(score, enrichment, kind, fiscalYear, generated ?? ScoreDate, rescored);

    public static ScoreReliabilityResponse? Evaluate(ScoreReliabilityRow row, string? json)
        => ScoreReliabilityAdapter.Evaluate(row, json).Reliability;

    /// <summary>The verdict as the wire carries it — what the parity tests compare.</summary>
    public static string Wire(ScoreReliabilityResponse? verdict) => JsonSerializer.Serialize(verdict);

    // ── The parity row: one analysis evaluated through the detail, the list and the dashboard ──────────

    public const decimal  ParityScore      = 61.5m;
    public const string   ParityEnrichment = "Degraded";
    public const int      ParityFiscalYear = 2024;
    public static readonly DateTime ParityGeneratedAt = new(2026, 3, 10, 14, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Re-scored in August: Dec-2024 → Aug-2026 is 20 months (balance_stale), while the March generation
    /// alone would be 15 — so a surface that ignores RescoredAt is caught.
    /// </summary>
    public static readonly DateTime ParityRescoredAt = new(2026, 8, 20, 9, 30, 0, DateTimeKind.Utc);

    /// <summary>A rich breakdown: cheques-only Degraded, an unread ratio, a carried-forward LUFE series, a sector fallback.</summary>
    public static string ParityBreakdownJson()
    {
        // ROA, not interest coverage: an unread interest coverage is the RT 6 format and raises nothing
        // (policy 4, ScoreReliabilityPolicy.RatioUnreadByPresentation).
        var scoring = CleanScoring();
        scoring.Breakdown["roa"] = new RatioBreakdownEntry(
            50m, "Atencion", Excluded: true, EffectiveWeight: 0m, ExclusionReason: RatioExclusionReasons.Unread);
        scoring.NonComputableRatios = ["roa"];

        var facts = CleanFacts() with
        {
            FiscalYear     = ParityFiscalYear,
            SectorFallback = true,
            SectorRaw      = "Fabricación de autopartes",
            Bcra = CleanFacts().Bcra! with
            {
                PeriodoConsultado = "202606",
                ChequesEstado     = nameof(BcraChequesConsultaEstado.LookupFailed),
            },
            Lufe = new ScoreReliabilityLufeFacts
            {
                VentasEstado        = ScoreReliabilityLufeVentasEstados.CarriedForward,
                VentasAsOf          = new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc),
                VentasUltimoPeriodo = "202604",
                SnapshotAt          = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            },
            Casfog = new ScoreReliabilityCasfogFacts
            {
                Estado    = nameof(CasfogConsultaEstado.NoDataReported),
                FetchedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            },
        };

        return Json(scoring, facts);
    }

    /// <summary>The codes the parity row must raise — pinned so the fixture cannot quietly become trivial.</summary>
    public static readonly IReadOnlyList<string> ParityCodes =
    [
        ScoreReliabilityCodes.ChequesUnavailable,
        ScoreReliabilityCodes.RatiosNotMeasured,
        ScoreReliabilityCodes.SectorFallback,
        ScoreReliabilityCodes.BalanceStale,
        ScoreReliabilityCodes.LufeCarriedForward,
    ];

    /// <summary>The verdict every surface must return for the parity row.</summary>
    public static ScoreReliabilityResponse ParityExpected()
        => Evaluate(
               new ScoreReliabilityRow(ParityScore, ParityEnrichment, AnalysisKinds.Balance, ParityFiscalYear,
                                       ParityGeneratedAt, ParityRescoredAt),
               ParityBreakdownJson())!;
}
