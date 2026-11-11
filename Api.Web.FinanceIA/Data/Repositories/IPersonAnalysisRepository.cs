// Project : Api.Web.FinanceIA
// Created : 2026-10-20

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// Persistence of the persona humana analysis (M10 Phase 4a, DDL 072): the three rows it is made of and
/// the reads that branch on <see cref="AnalysisKinds.PersonaHumana"/>. Kept apart from
/// <see cref="IAnalysisRepository"/> on purpose: the balance path does not change, and a member added
/// there would have to be stubbed in every existing test double. Every query is scoped by TenantId.
/// </summary>
public interface IPersonAnalysisRepository
{
    /// <summary>
    /// Inserts the analysis, its result, its solicitante and its fiadores (M10 Phase 5a-2; empty for an
    /// analysis without fiadores) as ONE unit: either every row exists or none does.
    /// </summary>
    Task CreateAsync(
        Analysis analysis, AnalysisResult result, AnalysisParticipant participant,
        IReadOnlyList<AnalysisParticipant> fiadores, CancellationToken ct);

    /// <summary>The participants of an analysis, solicitante first.</summary>
    Task<IReadOnlyList<AnalysisParticipant>> GetParticipantsAsync(Guid analysisId, string tenantId, CancellationToken ct);

    /// <summary>
    /// True when the entity has a persona humana analysis still awaiting its decision (PendingReview or
    /// PreApproved). A new evaluation is refused until it is settled.
    /// </summary>
    Task<bool> HasOpenPersonAnalysisAsync(Guid entityId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Writes Analyses.ConfirmationChecksJson of a persona humana analysis whose final record the quorum engine
    /// has just settled with its guarded UPDATE (DDL 072). False when no such analysis exists in the tenant.
    /// </summary>
    Task<bool> SetConfirmationChecksAsync(Guid analysisId, string tenantId, string checksJson, CancellationToken ct);

    /// <summary>
    /// Fiadores.Denominacion and Fiadores.CUIT of the given fiadores of the tenant, by FiadorId (either null
    /// when a row has none). A fiador of another tenant, or an unknown id, is simply absent. Read live: the
    /// identity is not frozen.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, FiadorIdentity>> GetFiadorIdentitiesAsync(
        IReadOnlyCollection<Guid> fiadorIds, string tenantId, CancellationToken ct);

    /// <summary>
    /// The kind and the requested fiscal year of an analysis, or null when it does not exist in the tenant.
    /// One narrow projection: the chat needs the kind (to refuse a persona humana analysis) and the fiscal
    /// year (for the score-reliability verdict) and reads both with the same query.
    /// </summary>
    Task<AnalysisKindAndFiscalYear?> GetKindAndFiscalYearAsync(Guid analysisId, string tenantId, CancellationToken ct);

    /// <summary>
    /// The tenant's <c>pf_</c> rows of [dbo].[ScoringConfig], by key. Sector overlays (<c>__sector__</c>)
    /// do not apply to the persona evaluation and are skipped.
    /// </summary>
    Task<IReadOnlyDictionary<string, decimal>> GetEvaluationConfigAsync(string tenantId, CancellationToken ct);
}
