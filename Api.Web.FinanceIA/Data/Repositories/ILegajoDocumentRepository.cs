// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// Data access for the persona humana legajo document pipeline (DDL 070): the documents of a legajo,
/// their LegajoStatus state machine, the classification and extraction runs in
/// [dbo].[LegajoExtractions], the analysts' corrections in [dbo].[LegajoCorrections] (append-only), and
/// the consent columns on [dbo].[PersonLegajos]. Every method is tenant-scoped except
/// <see cref="GetRecoverableAsync"/>, which the worker runs at startup and on its periodic sweep
/// across the database.
///
/// <para>
/// ACTIVE CONSENT, one rule everywhere it is read (the worker, the upload, the analyses trigger, the
/// delete guard): ConsentRecordedAt is set AND ConsentDocumentId points at an AutorizacionDatos
/// document of the same legajo and tenant that exists and is not deleted.
/// </para>
/// </summary>
public interface ILegajoDocumentRepository
{
    /// <summary>The legajo without its snapshots, with the entity linked to it; null when not in the tenant.</summary>
    Task<LegajoSummary?> GetLegajoSummaryAsync(Guid personLegajoId, string tenantId, CancellationToken ct);

    /// <summary>
    /// The id of a NON-deleted document of this legajo with this SHA-256, or null. Serves the upload's
    /// 409 dedupe.
    /// </summary>
    Task<Guid?> FindActiveByHashAsync(Guid personLegajoId, string tenantId, string fileHashSha256, CancellationToken ct);

    /// <summary>Every non-deleted document of the legajo, newest first.</summary>
    Task<IReadOnlyList<Document>> GetLegajoDocumentsAsync(Guid personLegajoId, string tenantId, CancellationToken ct);

    /// <summary>The ids of the legajo's non-deleted documents currently in <paramref name="legajoStatus"/>.</summary>
    Task<IReadOnlyList<Guid>> GetDocumentIdsInStatusAsync(
        Guid personLegajoId, string tenantId, string legajoStatus, CancellationToken ct);

    /// <summary>The current Classification run of each document, keyed by DocumentId. Missing = none yet.</summary>
    Task<IReadOnlyDictionary<Guid, LegajoExtraction>> GetCurrentClassificationsAsync(
        IReadOnlyCollection<Guid> documentIds, string tenantId, CancellationToken ct);

    /// <summary>What the worker needs about one document and its legajo; null when not in the tenant.</summary>
    Task<LegajoDocumentWork?> GetWorkAsync(Guid documentId, string tenantId, CancellationToken ct);

    /// <summary>Whether the legajo has ACTIVE consent RIGHT NOW (read fresh, never cached).</summary>
    Task<bool> HasConsentAsync(Guid personLegajoId, string tenantId, CancellationToken ct);

    /// <summary>
    /// The legajo linked to <paramref name="entityId"/> (null for a company) and whether it has ACTIVE
    /// consent. Serves POST /api/analyses: a persona humana's paperwork uploaded without a legajo must
    /// still not reach the model without consent.
    /// </summary>
    Task<(Guid? PersonLegajoId, bool HasActiveConsent)> GetEntityLegajoConsentAsync(
        Guid entityId, string tenantId, CancellationToken ct);

    /// <summary>
    /// True when <paramref name="documentId"/> is the ConsentDocumentId of a legajo of the tenant whose
    /// consent is recorded. The delete guard: that document cannot be deleted while consent stands.
    /// </summary>
    Task<bool> IsConsentDocumentInUseAsync(Guid documentId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Inserts a legajo document. When UX_Documents_Legajo_Hash rejects it (the same file already
    /// live in the legajo — a double-click that passed the pre-check), detaches it and returns the
    /// existing document's id instead of throwing. Returns null when the row was inserted.
    /// </summary>
    Task<Guid?> CreateLegajoDocumentAsync(Document document, CancellationToken ct);

    /// <summary>
    /// Atomically moves a document from <paramref name="fromStatus"/> to <paramref name="toStatus"/>
    /// and stamps LegajoStatusUpdatedAt — ONLY if it is still in <paramref name="fromStatus"/> and not
    /// deleted. One conditional UPDATE: of two callers racing on the same row exactly one sees
    /// <c>true</c>. This is the worker's claim (Queued → Classifying) and every other transition.
    /// </summary>
    Task<bool> TryTransitionAsync(
        Guid documentId, string tenantId, string fromStatus, string toStatus, DateTime now, CancellationToken ct);

    /// <summary>
    /// Classifying → <paramref name="toStatus"/>, writing what the classification read. Conditional on
    /// the row still being Classifying (a delete or reprocess in the meantime wins).
    /// </summary>
    Task<bool> CompleteClassificationAsync(
        Guid documentId, string tenantId, string toStatus, DateTime? periodEnd, string? engagementType,
        string? holderCuit, DateTime now, CancellationToken ct);

    /// <summary>
    /// NeedsReview → ReadyForExtraction by an analyst's confirmation, setting the confirmed document
    /// type and, when given, the period end (otherwise the classified one stays). Conditional on the
    /// row still being NeedsReview — a Blocked document can never be confirmed.
    /// </summary>
    Task<bool> ConfirmClassificationAsync(
        Guid documentId, string tenantId, string documentType, DateTime? periodEnd, DateTime now, CancellationToken ct);

    /// <summary>
    /// <paramref name="fromStatus"/> → Failed, recording the short <paramref name="errorCode"/> in
    /// LastExtractionError and LastExtractionFailedAt, and incrementing ExtractionAttempts when
    /// <paramref name="countsAsAttempt"/> (a request reached the model). Conditional like
    /// <see cref="TryTransitionAsync"/>.
    /// </summary>
    Task<bool> FailAsync(
        Guid documentId, string tenantId, string fromStatus, string errorCode, bool countsAsAttempt,
        DateTime now, CancellationToken ct);

    /// <summary>
    /// Inserts a run. When it is current, the previous current run of the same (DocumentId, Kind) is
    /// flipped to IsCurrent = 0 in the SAME SaveChanges (see LegajoExtractionConfiguration for how EF
    /// orders the two commands).
    /// </summary>
    Task InsertRunAsync(LegajoExtraction run, CancellationToken ct);

    /// <summary>
    /// Records consent against <paramref name="consentDocumentId"/>. Returns false when the legajo is
    /// not in the tenant. Writes only [dbo].[PersonLegajos] — never a document in the same SaveChanges.
    /// </summary>
    Task<bool> SetConsentAsync(
        Guid personLegajoId, string tenantId, Guid consentDocumentId, Guid recordedBy, DateTime now, CancellationToken ct);

    /// <summary>Clears the three consent columns. Returns false when the legajo is not in the tenant.</summary>
    Task<bool> ClearConsentAsync(Guid personLegajoId, string tenantId, DateTime now, CancellationToken ct);

    /// <summary>
    /// Every non-deleted legajo document, in ANY tenant of this database, the worker may have lost:
    /// <list type="bullet">
    /// <item>Classifying or Extracting since before <paramref name="staleCutoff"/> (or with no timestamp) —
    /// a job that stopped mid-way;</item>
    /// <item>Queued since before <paramref name="waitingCutoff"/>;</item>
    /// <item>ReadyForExtraction since before <paramref name="waitingCutoff"/>, of a type in
    /// <paramref name="extractableTypes"/>, whose legajo has ACTIVE consent (without it the worker
    /// would only hand it back);</item>
    /// </list>
    /// every waiting row, whatever its age, when <paramref name="waitingCutoff"/> is null (startup: the
    /// in-memory queue did not survive).
    /// </summary>
    Task<IReadOnlyList<(Guid DocumentId, string TenantId, string LegajoStatus)>> GetRecoverableAsync(
        DateTime staleCutoff, DateTime? waitingCutoff, IReadOnlyCollection<string> extractableTypes, CancellationToken ct);

    // ── Phase 2b: extraction ──────────────────────────────────────────────────────

    /// <summary>
    /// The extraction claim: ReadyForExtraction → Extracting in one conditional UPDATE, ONLY for a
    /// document whose type is in <paramref name="extractableTypes"/>. A type without an extractor is
    /// never claimed and stays ReadyForExtraction.
    /// </summary>
    Task<bool> TryClaimForExtractionAsync(
        Guid documentId, string tenantId, IReadOnlyCollection<string> extractableTypes, DateTime now, CancellationToken ct);

    /// <summary>
    /// Extracting → <paramref name="toStatus"/> (Ready, NeedsReview or Blocked). When
    /// <paramref name="holderCuit"/> is given (a valid CUIT the extraction read) it replaces
    /// Documents.HolderCuit. Conditional on the row still being Extracting.
    /// </summary>
    Task<bool> CompleteExtractionAsync(
        Guid documentId, string tenantId, string toStatus, string? holderCuit, DateTime now, CancellationToken ct);

    /// <summary>The document's current Extraction run, or null.</summary>
    Task<LegajoExtraction?> GetCurrentExtractionAsync(Guid documentId, string tenantId, CancellationToken ct);

    /// <summary>The current Extraction run of each document, keyed by DocumentId. Missing = none.</summary>
    Task<IReadOnlyDictionary<Guid, LegajoExtraction>> GetCurrentExtractionsAsync(
        IReadOnlyCollection<Guid> documentIds, string tenantId, CancellationToken ct);

    /// <summary>Whether the document has a current Extraction run (its NeedsReview is the extraction review's).</summary>
    Task<bool> HasCurrentExtractionAsync(Guid documentId, string tenantId, CancellationToken ct);

    /// <summary>The corrections of the given runs, oldest first (the order they apply in).</summary>
    Task<IReadOnlyList<LegajoCorrection>> GetCorrectionsAsync(
        IReadOnlyCollection<Guid> extractionIds, string tenantId, CancellationToken ct);

    /// <summary>
    /// The correction write — the ONLY one; corrections are never updated or deleted — in ONE
    /// transaction: appends <paramref name="correction"/>, then ALWAYS
    /// attempts the conditional Ready → NeedsReview of the document — whatever status the caller read
    /// earlier — and, when it applied, clears the run's review stamp (reviewer, date, override and reason). An approval that landed after the
    /// caller's read is therefore reopened, never left standing on values it did not see. Returns
    /// whether the document was reopened.
    /// </summary>
    Task<bool> InsertCorrectionAndReopenAsync(
        LegajoCorrection correction, Guid documentId, string tenantId, DateTime now, CancellationToken ct);

    /// <summary>
    /// The optimistic approval, in ONE transaction: NeedsReview → Ready ONLY while the document is still
    /// NeedsReview, <paramref name="extractionId"/> is still its current run, and that run still has
    /// exactly <paramref name="expectedCorrections"/> corrections (the ones the caller evaluated); then
    /// stamps the run with the reviewer, whether the approval was an override and its reason (DDL 071).
    /// False when any of that changed — the caller answers 409.
    /// </summary>
    Task<bool> TryApproveExtractionAsync(
        Guid documentId, string tenantId, Guid extractionId, int expectedCorrections, Guid reviewedBy,
        bool isOverride, string? reason, DateTime now, CancellationToken ct);
}
