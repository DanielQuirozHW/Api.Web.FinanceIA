// Project : Api.Web.FinanceIA
// Created : 2026-11-02

using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The FACTS a score was built on (score credibility 1b-2a), persisted inside
/// <c>AnalysisResults.ScoreBreakdownJson</c> under <c>reliabilityFacts</c>. Facts only — codes, flags,
/// dates, periods and counts. No verdict and no level: 1b-2b reads these and derives
/// <c>scoreReliability</c> at read time, against the score's own date.
/// <para>
/// Reporting only. Nothing in the engine reads it back, so it cannot move a score and needs no
/// <c>ScoringAlgoVersion</c> bump. Every member is omitted from the JSON when unknown; a boolean that
/// is known is written even when false ("checked, and fine"). Absent altogether on every analysis
/// scored or re-scored before it existed. Built in exactly one place,
/// <c>ScoreReliabilityFactsBuilder</c>, shared by the pipeline and the re-score.
/// </para>
/// <para>
/// No PII: no name, CUIT, amount or free text beyond the sector label (<see cref="SectorRaw"/>).
/// Estados are written as their enum NAMES, so a reader never needs the enum.
/// </para>
/// </summary>
public sealed record ScoreReliabilityFacts
{
    /// <summary>
    /// The shape version of this block, so 1b-2b and later readers can evolve it. 2 (Phase 10-F3a) added
    /// <see cref="ManualCorrections"/> and <see cref="ManualCorrectionsLastAt"/>. 3 (score credibility 1b-5)
    /// added <see cref="ScoreReliabilityIntegrityFacts.AffectedRatios"/>. 4 (reliability policy 5) added
    /// <see cref="ScoreReliabilityIntegrityFacts.FailedCheckNames"/> and <see cref="ScoreReliabilityIntegrityFacts.NetProfitSignFlip"/>.
    /// 5 (trial balances, prompt 2.19) added <see cref="ScoreReliabilityIntegrityFacts.DocumentKind"/>.
    /// 6 (income-statement column mix) added <see cref="ScoreReliabilityIntegrityFacts.NetProfitColumnMix"/>.
    /// </summary>
    public const int CurrentVersion = 6;

    [JsonPropertyName("version")]
    public int Version { get; init; } = CurrentVersion;

    /// <summary>S2 — DebtFound with a situación that is null or outside 1..6; BCRA scored the neutral 50.</summary>
    [JsonPropertyName("bcraSituacionUnreadable")]
    public bool BcraSituacionUnreadable { get; init; }

    /// <summary>S10 — the inflation figure was missing; macro scored the neutral 50.</summary>
    [JsonPropertyName("macroImputed")]
    public bool MacroImputed { get; init; }

    /// <summary>S6 — a non-blank sector matched no known profile and was scored as "General".</summary>
    [JsonPropertyName("sectorFallback")]
    public bool SectorFallback { get; init; }

    /// <summary>S6 — the raw sector text (≤ 100 chars), only when <see cref="SectorFallback"/> is true.</summary>
    [JsonPropertyName("sectorRaw")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SectorRaw { get; init; }

    /// <summary>S11 — equity &lt;= 0 made leverage/ROE not_meaningful. Omitted on model ratios (unknowable).</summary>
    [JsonPropertyName("negativeEquity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? NegativeEquity { get; init; }

    /// <summary>S4 — <c>defaults</c> | <c>tenant</c> | <c>mixed</c>; see <see cref="ScoringConfigSources"/>.</summary>
    [JsonPropertyName("configSource")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConfigSource { get; init; }

    /// <summary>S4 — 16 hex chars of a SHA-256 over the effective config.</summary>
    [JsonPropertyName("configHash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConfigHash { get; init; }

    /// <summary>S8 — <c>computed</c> (deterministic recompute) | <c>model</c> (the model's own ratios).</summary>
    [JsonPropertyName("ratiosSource")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RatiosSource { get; init; }

    /// <summary>S5 — the fiscal year the analysis was requested for (there is no closing date).</summary>
    [JsonPropertyName("fiscalYear")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FiscalYear { get; init; }

    /// <summary>The extraction integrity verdict at score time; omitted when the balance carries none.</summary>
    [JsonPropertyName("integrity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScoreReliabilityIntegrityFacts? Integrity { get; init; }

    /// <summary>
    /// How many distinct balance figures an analyst corrected by hand before this score (BalanceJson.
    /// manual_corrections, Phase 10-F3a); omitted when none was. A count only — no path, value or reason.
    /// </summary>
    [JsonPropertyName("manualCorrections")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ManualCorrections { get; init; }

    /// <summary>UTC instant of the latest manual correction; omitted when none was, or it is unreadable.</summary>
    [JsonPropertyName("manualCorrectionsLastAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ManualCorrectionsLastAt { get; init; }

    /// <summary>V6, S3 — the BCRA payload the score read; omitted when there was none.</summary>
    [JsonPropertyName("bcra")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScoreReliabilityBcraFacts? Bcra { get; init; }

    /// <summary>S9/V7 — the LUFE snapshot the score read; omitted when the entity has none.</summary>
    [JsonPropertyName("lufe")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScoreReliabilityLufeFacts? Lufe { get; init; }

    /// <summary>S9 — the CASFOG snapshot the score read; omitted when the entity has none.</summary>
    [JsonPropertyName("casfog")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScoreReliabilityCasfogFacts? Casfog { get; init; }
}

/// <summary>The extraction-integrity facts, read from the balance by <c>ExtractionIntegrityReader</c>.</summary>
public sealed record ScoreReliabilityIntegrityFacts
{
    [JsonPropertyName("passed")]
    public bool Passed { get; init; }

    [JsonPropertyName("verified")]
    public bool Verified { get; init; }

    [JsonPropertyName("notVerifiedReasons")]
    public IReadOnlyList<string> NotVerifiedReasons { get; init; } = [];

    /// <summary><c>match</c> | <c>mismatch</c> | <c>not_detected</c> (<c>FiscalYearCheck</c>).</summary>
    [JsonPropertyName("fiscalYearStatus")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FiscalYearStatus { get; init; }

    [JsonPropertyName("amountsUnitAssumed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AmountsUnitAssumed { get; init; }

    /// <summary>
    /// <c>trial_balance</c> when the balance was read off a trial balance printed from the ledger, not certified
    /// financial statements (version 5). Omitted otherwise — also on every block recorded before version 5.
    /// </summary>
    [JsonPropertyName("documentKind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DocumentKind { get; init; }

    /// <summary>How many integrity checks FAILED (a count; the names are <see cref="FailedCheckNames"/>).</summary>
    [JsonPropertyName("failedChecks")]
    public int FailedChecks { get; init; }

    /// <summary>
    /// The NAMES of the failed checks (<c>BalanceIntegrityService</c> check codes such as
    /// <c>net_profit_reconciliation</c>), in check order. Codes only, never a figure. Written by every version-4
    /// block, empty when nothing failed; omitted (null) on a block recorded before version 4, which a reader must
    /// treat as "unknown" — so such a score keeps the verdict it always had.
    /// </summary>
    [JsonPropertyName("failedCheckNames")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? FailedCheckNames { get; init; }

    /// <summary>
    /// When net_profit_reconciliation failed: whether it is a sign flip (a misread net profit, never a
    /// presentation), as <c>ExtractionIntegrityReader</c> judged it. Omitted (null) when that check did not fail or
    /// the flip could not be judged — which <c>FailsOnlyOnPresentation</c> takes as "not a presentation".
    /// </summary>
    [JsonPropertyName("netProfitSignFlip")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? NetProfitSignFlip { get; init; }

    /// <summary>
    /// When net_profit_reconciliation failed: whether result before tax and income tax were read from the comparative
    /// column (a misread, never a presentation), as <c>ExtractionIntegrityReader</c> read it (version 6). Omitted (null)
    /// when that check did not fail, the mix could not be checked, or the block predates version 6 — which
    /// <c>FailsOnlyOnPresentation</c> takes as "unknown", keeping the verdict the score always had.
    /// </summary>
    [JsonPropertyName("netProfitColumnMix")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? NetProfitColumnMix { get; init; }

    /// <summary>
    /// The ratio KEYS computed from a line some failed check involved (the balance's
    /// <c>integrity.affectedRatios</c>, the engine's own keys). Keys only, never a figure. Written by every
    /// version-3 block, empty when no ratio is affected; omitted (null) on a block recorded before version 3,
    /// which a reader must treat as "unknown", not as "none".
    /// </summary>
    [JsonPropertyName("affectedRatios")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? AffectedRatios { get; init; }
}

/// <summary>The BCRA facts: estados as names, the period consulted and when it was fetched. No amounts.</summary>
public sealed record ScoreReliabilityBcraFacts
{
    /// <summary><c>DebtFound</c> | <c>NoDebtReported</c> | <c>LookupFailed</c>; omitted with no Deuda block.</summary>
    [JsonPropertyName("deudaEstado")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeudaEstado { get; init; }

    /// <summary>The YYYYMM period of the Central de Deudores reading.</summary>
    [JsonPropertyName("periodoConsultado")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PeriodoConsultado { get; init; }

    [JsonPropertyName("deudaFetchedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? DeudaFetchedAt { get; init; }

    /// <summary><c>ChequesFound</c> | <c>NoChequesReported</c> | <c>LookupFailed</c>; omitted with no cheques block.</summary>
    [JsonPropertyName("chequesEstado")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChequesEstado { get; init; }

    /// <summary>Whether the cheques penalty took part in this score (<c>ScoringResult.ChequesEvaluated</c>).</summary>
    [JsonPropertyName("chequesEvaluated")]
    public bool ChequesEvaluated { get; init; }
}

/// <summary>The LUFE monthly-sales facts.</summary>
public sealed record ScoreReliabilityLufeFacts
{
    /// <summary><c>fresh</c> | <c>carried_forward</c> | <c>lookup_failed</c> | <c>none</c>; see <see cref="ScoreReliabilityLufeVentasEstados"/>.</summary>
    [JsonPropertyName("ventasEstado")]
    public string VentasEstado { get; init; } = ScoreReliabilityLufeVentasEstados.None;

    /// <summary>When a carried-forward series was actually fetched.</summary>
    [JsonPropertyName("ventasAsOf")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? VentasAsOf { get; init; }

    /// <summary>The latest YYYYMM month carrying a sales figure in the series the engine read.</summary>
    [JsonPropertyName("ventasUltimoPeriodo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VentasUltimoPeriodo { get; init; }

    /// <summary>When the snapshot itself was taken (<c>Entities.LufeDataUpdatedAt</c>).</summary>
    [JsonPropertyName("snapshotAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? SnapshotAt { get; init; }
}

/// <summary>The closed vocabulary for <see cref="ScoreReliabilityLufeFacts.VentasEstado"/>.</summary>
public static class ScoreReliabilityLufeVentasEstados
{
    /// <summary>A fresh answer carrying a non-empty series.</summary>
    public const string Fresh = "fresh";

    /// <summary>The call failed and the previously stored series was kept.</summary>
    public const string CarriedForward = Lufe.LufeVentasComprasEstados.CarriedForward;

    /// <summary>The call failed and nothing usable was stored.</summary>
    public const string LookupFailed = Lufe.LufeVentasComprasEstados.LookupFailed;

    /// <summary>A fresh answer with no series at all.</summary>
    public const string None = "none";
}

/// <summary>The CASFOG facts.</summary>
public sealed record ScoreReliabilityCasfogFacts
{
    /// <summary><c>DataFound</c> | <c>NoDataReported</c> | <c>LookupFailed</c>.</summary>
    [JsonPropertyName("estado")]
    public string Estado { get; init; } = string.Empty;

    [JsonPropertyName("fetchedAt")]
    public DateTime FetchedAt { get; init; }
}
