// Project : Api.Web.FinanceIA
// Created : 2026-11-02

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The deterministic integrity verdict on the kept balance extraction, as the Portal reads it:
/// <c>AnalysisResultResponse.ExtractionIntegrity</c>. Read from <c>BalanceJson.integrity</c>; null
/// on analyses extracted before prompt 2.18 (no integrity node). A FLAG for the analyst: the score
/// is computed from the same figures whatever this says.
/// </summary>
public sealed record ExtractionIntegrityResponse
{
    /// <summary>"integrity_unavailable": the integrity evaluation itself failed, so no check ran.</summary>
    public const string IntegrityUnavailableReason = "integrity_unavailable";

    /// <summary>Suffix of the reason a verification check did not run: <c>assets_components_skipped</c>.</summary>
    public const string SkippedReasonSuffix = "_skipped";

    /// <summary>
    /// True when no integrity check that ran failed. Also false when the evaluation itself failed
    /// (<see cref="NotVerifiedReasons"/> = <c>integrity_unavailable</c>): nothing was proven.
    /// </summary>
    public required bool Passed { get; init; }

    /// <summary>Names of the failed checks (e.g. <c>current_assets_items</c>), distinct, in check order.</summary>
    public required IReadOnlyList<string> FailedChecks { get; init; }

    /// <summary>
    /// True when the report failed and EVERY failed check is a presentation check (today only
    /// <c>net_profit_reconciliation</c>, when it is neither a sign flip nor a column mix): the income statement does
    /// not reconcile as read — which may be how it is presented (RECPAM after the tax, no pre-tax subtotal) — and the
    /// balance figures add up. Describes this report only; what the decision asks for
    /// follows <c>scoreReliability</c> / <c>decisionRequirements</c>, which for a score built before policy 5
    /// may still report <c>integrity_failed</c>. Additive: false whenever <see cref="Passed"/> is true.
    /// </summary>
    public bool PresentationOnly { get; init; }

    /// <summary>
    /// When <c>net_profit_reconciliation</c> failed: true when the reported net profit is the NEGATIVE of the
    /// expected one (a sign lost on reading — a misread, so never <see cref="PresentationOnly"/>), false when it is
    /// not. Read from the check's <c>signFlip</c>, or, on a blob written before that was recorded, re-judged from the
    /// stored net_profit / result_before_tax / income_tax. Null when that check did not fail, or the figures to judge
    /// it are missing (unknown, which is not a presentation either).
    /// </summary>
    public bool? NetProfitSignFlip { get; init; }

    /// <summary>
    /// When <c>net_profit_reconciliation</c> failed: true when result before tax and income tax, as read, are the
    /// COMPARATIVE column's (they reconcile to the prior year's net profit — a misread, so never
    /// <see cref="PresentationOnly"/>), false when that was checked and they are not. Read only from the check's
    /// <c>columnMix</c>; a blob written before it was recorded is NOT re-judged. Null when that check did not fail, the
    /// mix could not be checked (no comparative net profit), or the blob predates it — unknown, which keeps the
    /// verdict the report always had. Additive.
    /// </summary>
    public bool? NetProfitColumnMix { get; init; }

    /// <summary>Scoring ratio keys computed from a line involved in a failed check.</summary>
    public required IReadOnlyList<string> AffectedRatios { get; init; }

    /// <summary>
    /// Whether a second, differently-read extraction pass ran. Null on an analysis persisted before
    /// this was recorded — unknown, not "no".
    /// </summary>
    public bool? SecondPassRan { get; init; }

    /// <summary>"first" or "second"; null when not recorded (see <see cref="SecondPassRan"/>).</summary>
    public string? KeptPass { get; init; }

    /// <summary>
    /// "unread" when the uploaded PDF carries the balance as an embedded attachment that could not be
    /// opened, so only the wrapper was extracted (<c>integrity.extraction.embeddedAttachment</c>).
    /// Null otherwise, and on analyses persisted before this was recorded.
    /// </summary>
    public string? EmbeddedAttachment { get; init; }

    /// <summary>
    /// "orphaned_filespec" when the embedded attachment taken as the balance — or, with
    /// <see cref="EmbeddedAttachment"/> = "unread", the one left unread — was found in a re-saved PDF whose
    /// live catalog no longer reaches it (<c>integrity.extraction.embeddedAttachmentSource</c>). Null
    /// otherwise, and on analyses persisted before this was recorded. Additive.
    /// </summary>
    public string? EmbeddedAttachmentSource { get; init; }

    /// <summary>
    /// True when the balance COULD be verified: the integrity report exists and the three structural
    /// checks (assets_components, liabilities_components, accounting_equation) all ran. Independent
    /// of <see cref="Passed"/>: a verified balance may still fail a check. False means
    /// <see cref="Passed"/> proves little, since a report whose checks were skipped passes vacuously.
    /// </summary>
    public required bool Verified { get; init; }

    /// <summary>
    /// Why <see cref="Verified"/> is false: <c>integrity_unavailable</c>, or one
    /// <c>&lt;check&gt;_skipped</c> per structural check that did not run. Empty when verified.
    /// </summary>
    public required IReadOnlyList<string> NotVerifiedReasons { get; init; }

    /// <summary>
    /// The printed unit the figures were scaled from: pesos, miles or millones. Null when the
    /// integrity report is unavailable.
    /// </summary>
    public string? AmountsUnit { get; init; }

    /// <summary>True when the document declared no unit and pesos was assumed; null when unavailable.</summary>
    public bool? AmountsUnitAssumed { get; init; }

    /// <summary>
    /// <c>trial_balance</c> when the extraction read the document as a trial balance (a "Balance de
    /// Presentación" / sumas y saldos printed from the ledger, prompt 2.19+): the BalanceJson root
    /// <c>document_kind</c>. Null for financial statements and for every analysis extracted before it.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? DocumentKind { get; init; }

    /// <summary>
    /// The fiscal year the document states, against the one the analysis was requested for. Null
    /// when the response does not know the analysis's fiscal year.
    /// </summary>
    public ExtractionFiscalYearResponse? FiscalYear { get; init; }
}

/// <summary>
/// <c>extractionIntegrity.fiscalYear</c>: the requested fiscal year (<c>Analysis.FiscalYear</c>), the
/// one the extraction read off the document (<c>fiscal_year_detected</c>), and <c>match</c> |
/// <c>mismatch</c> | <c>not_detected</c> (<see cref="Api.Web.FinanceIA.Servicios.FiscalYearCheck"/>).
/// A fiscal year is identified by its closing year.
/// </summary>
public sealed record ExtractionFiscalYearResponse
{
    /// <summary>The fiscal year the analysis was requested for.</summary>
    public required int Requested { get; init; }

    /// <summary>The fiscal year read off the document; null when not detected.</summary>
    public int? Detected { get; init; }

    /// <summary><c>match</c>, <c>mismatch</c> or <c>not_detected</c>.</summary>
    public required string Status { get; init; }
}
