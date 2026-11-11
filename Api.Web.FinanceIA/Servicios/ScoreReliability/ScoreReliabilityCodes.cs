// Project : Api.Web.FinanceIA
// Created : 2026-11-02

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.ScoreReliability;

/// <summary>
/// The reason codes of the score-reliability verdict and their severity. Persisted nowhere, but read by
/// the Portal: never rename a code. <see cref="Catalogue"/> is ALSO the stable order of reasons within a
/// severity.
/// </summary>
internal static class ScoreReliabilityCodes
{
    // ── none ────────────────────────────────────────────────────────────────
    public const string ScoreUnavailable = "score_unavailable";

    // ── provisional ─────────────────────────────────────────────────────────
    public const string BcraUnavailable     = "bcra_unavailable";
    public const string IntegrityFailed     = "integrity_failed";
    public const string CriticalFloorUnverified = "critical_floor_unverified";
    public const string IntegrityUnverified = "integrity_unverified";
    public const string FiscalYearMismatch  = "fiscal_year_mismatch";
    public const string RatiosLowCoverage   = "ratios_low_coverage";

    // ── caveat ──────────────────────────────────────────────────────────────
    public const string ChequesUnavailable      = "cheques_unavailable";
    public const string RatiosNotMeasured       = "ratios_not_measured";
    public const string NegativeEquity          = "negative_equity";
    public const string RatiosNotMeaningful     = "ratios_not_meaningful";
    public const string RatiosFromModel         = "ratios_from_model";
    public const string IntegrityPresentation   = "integrity_presentation";
    public const string TrialBalance            = "trial_balance";
    public const string AmountsUnitAssumed      = "amounts_unit_assumed";
    public const string ManualCorrection        = "manual_correction";
    public const string SectorFallback          = "sector_fallback";
    public const string BalanceStale            = "balance_stale";
    public const string LufeCarriedForward      = "lufe_carried_forward";
    public const string LufeUnavailable         = "lufe_unavailable";
    public const string LufeStale               = "lufe_stale";
    public const string CasfogUnavailable       = "casfog_unavailable";
    public const string CasfogStale             = "casfog_stale";
    public const string BcraSituacionUnreadable = "bcra_situacion_unreadable";
    public const string BcraPeriodLag           = "bcra_period_lag";
    public const string BcraNotApplicable       = "bcra_not_applicable";
    public const string MacroImputed            = "macro_imputed";
    public const string ConfigThresholdFallback = "config_threshold_fallback";
    public const string FactsUnavailable        = "facts_unavailable";

    /// <summary>Every code with its severity, in reporting order.</summary>
    public static readonly IReadOnlyList<(string Code, string Severity)> Catalogue =
    [
        (ScoreUnavailable,        ScoreReliabilitySeverities.None),

        (BcraUnavailable,         ScoreReliabilitySeverities.Provisional),
        (IntegrityFailed,         ScoreReliabilitySeverities.Provisional),
        (CriticalFloorUnverified, ScoreReliabilitySeverities.Provisional),
        (IntegrityUnverified,     ScoreReliabilitySeverities.Provisional),
        (FiscalYearMismatch,      ScoreReliabilitySeverities.Provisional),
        (RatiosLowCoverage,       ScoreReliabilitySeverities.Provisional),

        (ChequesUnavailable,      ScoreReliabilitySeverities.Caveat),
        (RatiosNotMeasured,       ScoreReliabilitySeverities.Caveat),
        (NegativeEquity,          ScoreReliabilitySeverities.Caveat),
        (RatiosNotMeaningful,     ScoreReliabilitySeverities.Caveat),
        (RatiosFromModel,         ScoreReliabilitySeverities.Caveat),
        (IntegrityPresentation,   ScoreReliabilitySeverities.Caveat),
        (TrialBalance,            ScoreReliabilitySeverities.Caveat),
        (AmountsUnitAssumed,      ScoreReliabilitySeverities.Caveat),
        (ManualCorrection,        ScoreReliabilitySeverities.Caveat),
        (SectorFallback,          ScoreReliabilitySeverities.Caveat),
        (BalanceStale,            ScoreReliabilitySeverities.Caveat),
        (LufeCarriedForward,      ScoreReliabilitySeverities.Caveat),
        (LufeUnavailable,         ScoreReliabilitySeverities.Caveat),
        (LufeStale,               ScoreReliabilitySeverities.Caveat),
        (CasfogUnavailable,       ScoreReliabilitySeverities.Caveat),
        (CasfogStale,             ScoreReliabilitySeverities.Caveat),
        (BcraSituacionUnreadable, ScoreReliabilitySeverities.Caveat),
        (BcraPeriodLag,           ScoreReliabilitySeverities.Caveat),
        (BcraNotApplicable,       ScoreReliabilitySeverities.Caveat),
        (MacroImputed,            ScoreReliabilitySeverities.Caveat),
        (ConfigThresholdFallback, ScoreReliabilitySeverities.Caveat),
        (FactsUnavailable,        ScoreReliabilitySeverities.Caveat),
    ];

    private static readonly Dictionary<string, (string Severity, int Order)> Index =
        Catalogue.Select((entry, order) => (entry.Code, entry.Severity, order))
                 .ToDictionary(e => e.Code, e => (e.Severity, e.order), StringComparer.Ordinal);

    /// <summary>The severity of <paramref name="code"/>; throws on a code outside the catalogue (a bug).</summary>
    public static string SeverityOf(string code) => Index[code].Severity;

    /// <summary>The reporting position of <paramref name="code"/> (severity first, then catalogue order).</summary>
    public static int OrderOf(string code) => Index[code].Order;
}
