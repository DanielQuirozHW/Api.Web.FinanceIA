// Project : Api.Web.FinanceIA
// Created : 2026-07-20

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// Persistence operations for the general credit line (cupo).
/// All reads are tenant-isolated. Exactly one credit line is Active per
/// (TenantId, EntityId, Currency). A cupo's amount is raised in place (see
/// <see cref="RaiseAsync"/>); operations are never re-parented.
/// </summary>
public interface ICreditLineRepository
{
    /// <summary>
    /// Retrieves all credit lines for an entity in a non-terminal status — 'Active' or
    /// 'Suspended' — across every currency, tenant-isolated. Terminal ('Superseded') cupos are
    /// excluded. A suspended cupo must stay visible on the entity's card (its Reactivate action
    /// lives there), so this is deliberately NOT Active-only. Ordered by Status then Currency, so
    /// 'Active' precedes 'Suspended' deterministically.
    /// </summary>
    Task<IReadOnlyList<CreditLine>> GetNonTerminalByEntityAsync(
        Guid entityId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Retrieves the single Active credit line for an entity in the given currency,
    /// or null when none exists. Tenant-isolated.
    /// </summary>
    Task<CreditLine?> GetActiveByEntityCurrencyAsync(
        Guid entityId, string currency, string tenantId, CancellationToken ct);

    /// <summary>
    /// Retrieves a credit line by its id, tenant-isolated. Returns null when the
    /// line does not exist or belongs to another tenant (regardless of status).
    /// </summary>
    Task<CreditLine?> GetByIdAsync(Guid creditLineId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Appends a new credit line.
    /// </summary>
    Task CreateAsync(CreditLine line, CancellationToken ct);

    /// <summary>
    /// Raises (amplía) an Active cupo in place: sets <see cref="CreditLine.Amount"/> to
    /// <paramref name="newAmount"/> and stamps <see cref="CreditLine.LastRaisedAt"/> /
    /// <see cref="CreditLine.LastRaiseAnalysisId"/>. Tenant-isolated.
    /// Optimistic concurrency: the update applies only when the cupo is still Active AND its
    /// <see cref="CreditLine.LastRaisedAt"/> still equals <paramref name="expectedLastRaisedAt"/>
    /// (the baseline the caller validated the freshness rule against). Returns the updated
    /// entity, or null when the cupo no longer exists, is no longer Active, or was raised by a
    /// concurrent request first. A single atomic conditional UPDATE — no explicit transaction.
    /// </summary>
    Task<CreditLine?> RaiseAsync(
        Guid creditLineId, string tenantId, decimal newAmount, Guid analysisId,
        DateTime? expectedLastRaisedAt, CancellationToken ct);

    /// <summary>
    /// Returns the subset of <paramref name="entityIds"/> that have at least one credit line in a
    /// non-terminal status — 'Active' or 'Suspended' — tenant-isolated. Terminal ('Superseded')
    /// cupos do not count. An entity whose only cupo is Suspended still has a line, so this is
    /// deliberately NOT Active-only. A single set-based query — used to project hasCreditLine onto
    /// the entities list without N+1.
    /// </summary>
    Task<IReadOnlySet<Guid>> GetEntityIdsWithNonTerminalLineAsync(
        IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct);

    /// <summary>
    /// Projects the MAIN credit line of each of <paramref name="entityIds"/>, keyed by entity id,
    /// tenant-isolated. Entities with no main cupo are simply absent from the dictionary.
    ///
    /// <para>
    /// The main cupo is the <b>'Active'</b> one with the most recent <see cref="CreditLine.CreatedAt"/>,
    /// whatever its currency. Not the largest amount — amounts across currencies are not comparable —
    /// and not <see cref="CreditLine.LastRaisedAt"/>, because an ampliación mutates Amount in place on
    /// the same row and must not re-order cupos.
    /// </para>
    ///
    /// <para>
    /// ACTIVE-ONLY, and therefore STRICTER than <see cref="GetEntityIdsWithNonTerminalLineAsync"/>,
    /// which also counts 'Suspended'. The two are not interchangeable and the difference is
    /// intentional: hasCreditLine answers "does this PyME have a cupo on file at all" (a Suspended
    /// one still shows on its card, with its Reactivate action), whereas this answers "how much live
    /// tope is there to draw a utilisation bar for" — and a Suspended cupo lends nothing further.
    /// So an entity can be in the result of that method and absent from this one.
    /// </para>
    ///
    /// <para>
    /// Set-based: ONE query for the whole batch, with each cupo's Utilizado summed in SQL, so the
    /// entities list can project the main cupo per row without N+1 and without materialising a single
    /// operation row.
    /// </para>
    /// </summary>
    Task<IReadOnlyDictionary<Guid, MainCreditLineProjection>> GetMainActiveByEntityIdsAsync(
        IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct);

    /// <summary>
    /// Projects the ids (not full rows) of every credit line whose
    /// <see cref="CreditLine.OriginAnalysisId"/> equals <paramref name="analysisId"/> and whose status
    /// is non-terminal ('Active' or 'Suspended'), tenant-isolated. Terminal ('Superseded') cupos are
    /// excluded — "live dependent" here matches <see cref="GetBlockingByEntityCurrencyAsync"/>. A
    /// non-empty result means the analysis still justifies live exposure; the ids are surfaced so a
    /// 409 can link the client straight to the affected cupo(s).
    /// </summary>
    Task<IReadOnlyList<Guid>> GetIdsByOriginAnalysisAsync(
        Guid analysisId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Projects the ids (not full rows) of the ACTIVE credit lines whose
    /// <see cref="CreditLine.OriginAnalysisId"/> equals <paramref name="analysisId"/>, tenant-isolated.
    /// Answers "which cupos originated by this analysis are still actively lending?" — the operative
    /// subset that blocks rejecting the origin analysis and that the reject-guard 409 names for the
    /// analyst to suspend. Narrower than <see cref="GetIdsByOriginAnalysisAsync"/> (non-terminal:
    /// 'Active' + 'Suspended'): a 'Suspended' cupo lends nothing further and no longer blocks rejection
    /// on its own. Distinct again from <see cref="GetByOriginAnalysisAsync"/> (all states, full rows,
    /// provenance). Do NOT unify the three — they answer different questions.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetActiveIdsByOriginAnalysisAsync(
        Guid analysisId, string tenantId, CancellationToken ct);

    /// <summary>
    /// PROVENANCE view: every credit line whose <see cref="CreditLine.OriginAnalysisId"/> equals
    /// <paramref name="analysisId"/>, tenant-isolated, REGARDLESS of status — including terminal
    /// 'Superseded'. This answers "which cupos did this analysis originate?", a historical/provenance
    /// question, NOT "which live cupos still depend on it?". It therefore deliberately does NOT filter
    /// by status, unlike the id-only, non-terminal-only <see cref="GetIdsByOriginAnalysisAsync"/> used
    /// by the reject-analysis guard. Do NOT unify the two — they answer different questions. Returns
    /// full rows (an analysis may originate up to one cupo per currency), ordered Currency then Status.
    /// </summary>
    Task<IReadOnlyList<CreditLine>> GetByOriginAnalysisAsync(
        Guid analysisId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Retrieves the credit line (if any) that BLOCKS creating a new one for this
    /// (entity, currency): a row in a non-terminal status — 'Active' or 'Suspended' — tenant-isolated.
    /// A 'Superseded' row is terminal and does not block. When both an Active and a Suspended row ever
    /// coexist, the Active one is returned. Distinct from <see cref="GetActiveByEntityCurrencyAsync"/>,
    /// whose Active-only semantics the reactivate guard relies on.
    /// </summary>
    Task<CreditLine?> GetBlockingByEntityCurrencyAsync(
        Guid entityId, string currency, string tenantId, CancellationToken ct);

    /// <summary>
    /// Transitions a cupo's <see cref="CreditLine.Status"/> from <paramref name="expectedStatus"/> to
    /// <paramref name="newStatus"/> in a single atomic conditional UPDATE, tenant-isolated. Writes only
    /// the Status column. Returns false when no row matches (missing, wrong tenant, or the status was
    /// already changed by a concurrent request) so the caller can 409 rather than silently overwrite.
    /// </summary>
    Task<bool> SetStatusAsync(
        Guid creditLineId, string tenantId, string expectedStatus, string newStatus, CancellationToken ct);

    /// <summary>
    /// Every <b>'Active'</b> cupo of the tenant with its cupo-occupying operations — the input of the
    /// cupo alerts. Active-only like <see cref="GetMainActiveByEntityIdsAsync"/> and the dashboard: a
    /// Suspended cupo draws no utilisation bar and raises no alert.
    ///
    /// <para>
    /// Two queries for the whole tenant, however many cupos it has: the cupos, then their operations
    /// under <c>CreditLineRules.OccupiesCupoPredicate</c>, both projected to the columns the alerts read.
    /// TenantId is filtered on both sides of the join.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<CupoAlertSource>> GetActiveForCupoAlertsAsync(string tenantId, CancellationToken ct);

    /// <summary>
    /// Every PyME of the tenant that has at least one <b>'Active'</b> cupo, each with its most recent
    /// analysis in Status 'Approved' (any AnalysisKind), dated ReviewedAt ?? CreatedAt — the input of
    /// PYME_REVIEW_DUE. A PyME with no approved analysis is present with a null AnalysisId.
    ///
    /// <para>
    /// One statement for the whole tenant, however many PyMEs it has: the Active cupos' entities, left
    /// joined to the latest approval date per entity (one GROUP BY over Analyses) and back to the analysis
    /// carrying it. TenantId is filtered on every table; nothing but ids and dates is selected.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<PymeReviewSource>> GetLatestApprovedAnalysesOfActiveEntitiesAsync(string tenantId, CancellationToken ct);
}
