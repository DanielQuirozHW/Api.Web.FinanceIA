// Project : Api.Web.FinanceIA
// Created : 2026-07-20

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// Persistence operations for child operations under a credit line (cupo).
/// All reads are tenant-isolated. Persisted status is 'Aprobada', 'Vigente' or
/// 'Cancelada' only; 'Vencida' is derived at read time and never stored.
/// </summary>
public interface ICreditLineOperationRepository
{
    /// <summary>
    /// Retrieves all operations under a credit line, tenant-isolated.
    /// </summary>
    Task<IReadOnlyList<CreditLineOperation>> GetByCreditLineAsync(
        Guid creditLineId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Retrieves all operations stemming from a given analysis, tenant-isolated.
    /// </summary>
    Task<IReadOnlyList<CreditLineOperation>> GetByAnalysisAsync(
        Guid analysisId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Retrieves an operation by its id, tenant-isolated. Returns null when the
    /// operation does not exist or belongs to another tenant.
    /// </summary>
    Task<CreditLineOperation?> GetByIdAsync(Guid operationId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Appends a new operation.
    /// </summary>
    Task CreateAsync(CreditLineOperation op, CancellationToken ct);

    /// <summary>
    /// Cancels an operation: flips it to 'Cancelada' and stamps who did it and when. Tenant-isolated.
    /// A single conditional UPDATE guarded on the current status being <paramref name="expectedStatus"/>
    /// — not merely on "not already cancelled" — so that a concurrent confirmar-vigencia landing
    /// between the caller's read and this write matches 0 rows instead of being recorded as a
    /// cancellation from a status the row had already left. That distinction is the point: an
    /// audit entry saying 'Aprobada' → 'Cancelada' means the operation fell through ("se cayó"),
    /// and it must not be written for what was really a close. Status and both audit columns are
    /// written in the same statement, so no half-cancelled row is reachable.
    /// </summary>
    /// <returns>
    /// True when the row was cancelled; false when its status was no longer
    /// <paramref name="expectedStatus"/> (a concurrent cancel or confirmation won, or the row is gone).
    /// Unlike <see cref="ConfirmVigenciaAsync"/>, false is not automatically a 409: cancellation is
    /// idempotent, so the caller re-reads to tell "someone else already cancelled it" (204, nothing
    /// left to do) from "someone confirmed it and the status I must audit against changed" (retry).
    /// </returns>
    Task<bool> CancelAsync(
        Guid operationId, string tenantId, string expectedStatus, Guid cancelledByUserId,
        DateTime cancelledAtUtc, CancellationToken ct);

    /// <summary>
    /// Confirms vigencia: flips an operation from 'Aprobada' to 'Vigente' and stamps who did it and
    /// when. Tenant-isolated. A single conditional UPDATE guarded on the current status being
    /// 'Aprobada', so a concurrent transition that already moved the row matches 0 rows and this
    /// returns false rather than clobbering it — the caller 409s. Status and both audit columns are
    /// written in the same statement, so no half-confirmed row is reachable.
    /// </summary>
    /// <returns>True when the row was confirmed; false when it was not 'Aprobada' (or is gone).</returns>
    Task<bool> ConfirmVigenciaAsync(
        Guid operationId, string tenantId, Guid confirmedByUserId, DateTime confirmedAtUtc,
        CancellationToken ct);

    /// <summary>
    /// True iff any operation stemming from <paramref name="analysisId"/> has a stored status other
    /// than 'Cancelada' — an 'Aprobada' or 'Vigente' operation — tenant-isolated. An existence
    /// check: loads no rows. Used to decide whether an analysis still justifies live exposure.
    /// </summary>
    Task<bool> HasNonCancelledByAnalysisAsync(Guid analysisId, string tenantId, CancellationToken ct);
}
