// Project : Api.Web.FinanceIA
// Created : 2026-09-04

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Data.Repositories;

/// <summary>
/// Persistence operations for <see cref="UserApprovalLimit"/> — the per-(user, currency) ceiling
/// that decides whether an Analyst approves directly or can only pre-approve.
/// </summary>
public interface IUserApprovalLimitRepository
{
    /// <summary>
    /// Returns the limit for one user in one currency, or null when none is configured.
    /// </summary>
    /// <remarks>
    /// Null is the meaningful default, not an error: it means this user may not approve directly in
    /// this currency and their decision becomes a pre-approval. Callers must not treat null as
    /// "unlimited".
    /// </remarks>
    /// <param name="userId">The user whose limit to read.</param>
    /// <param name="currency">ISO 4217 code.</param>
    /// <param name="tenantId">Tenant scope (always enforced).</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task<UserApprovalLimit?> GetAsync(Guid userId, string currency, string tenantId, CancellationToken ct);

    /// <summary>
    /// Returns every configured limit for one user, ordered by currency.
    /// </summary>
    /// <param name="userId">The user whose limits to read.</param>
    /// <param name="tenantId">Tenant scope (always enforced).</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task<IReadOnlyList<UserApprovalLimit>> GetByUserAsync(Guid userId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Replaces the complete set of limits for one user with <paramref name="limits"/>, in a single
    /// transaction: rows for currencies absent from the new set are deleted, the rest are inserted
    /// or updated.
    /// </summary>
    /// <remarks>
    /// Replace rather than merge, because the Admin screen edits the whole set at once and removing
    /// a currency has to be expressible. Deleting a row is not the same as setting CanApprove=false:
    /// both stop direct approval, but the absent row is the "never configured" default.
    /// </remarks>
    /// <param name="userId">The user whose limits to replace.</param>
    /// <param name="tenantId">Tenant scope (always enforced).</param>
    /// <param name="limits">The complete desired set. Empty clears every limit for the user.</param>
    /// <param name="editedBy">Admin performing the change, recorded on the affected rows.</param>
    /// <param name="ct">Token to observe for cancellation.</param>
    Task<IReadOnlyList<UserApprovalLimit>> ReplaceForUserAsync(
        Guid userId,
        string tenantId,
        IReadOnlyCollection<UserApprovalLimit> limits,
        Guid editedBy,
        CancellationToken ct);
}
