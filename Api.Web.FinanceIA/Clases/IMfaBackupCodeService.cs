// Project : Api.Web.FinanceIA
// Created : 2026-09-03

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Issues, validates and counts single-use MFA backup codes for one FinancIA user.
/// </summary>
/// <remarks>
/// Firebase Identity Platform owns the TOTP factor but exposes no backup-code API, so this
/// service is the whole recovery mechanism. Codes are stored only as salted PBKDF2 hashes in
/// [dbo].[MfaBackupCodes] — in the FinancIA BUSINESS database (FinanciaDbContext), beside the
/// [dbo].[Users] row each code belongs to and constrained to it by FK_MfaBackupCodes_Users. The
/// plaintext exists once, in the return value of <see cref="GenerateAsync"/>, and is never
/// recoverable afterwards.
/// <para>
/// Every method is keyed by <c>(tenantId, userId)</c>, both of which the caller reads off the
/// resolved <c>Users</c> row — the uid comes from the authenticated principal (or, for
/// redemption, from the email whose password Identity Platform just verified), never from a
/// request body. Taking the tenant from the user row rather than from X-Tenant-ID is deliberate:
/// it is impossible for a code to be filed under a tenant its owner does not belong to.
/// </para>
/// </remarks>
public interface IMfaBackupCodeService
{
    /// <summary>
    /// Replaces the user's unused backup codes with a freshly generated set and returns the new
    /// codes in plaintext. This is the ONLY time the plaintext exists — it is not logged, not
    /// stored and cannot be shown again.
    /// </summary>
    /// <param name="tenantId">Tenant slug of the owning user, from their Users row.</param>
    /// <param name="userId">The owning user's UserId, resolved from the authenticated principal.</param>
    /// <param name="count">How many codes to issue. Must be between 1 and 20.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>The new codes in display form ("ABCD-2345"), in issue order.</returns>
    /// <remarks>
    /// Codes the user has already SPENT are left untouched: they are the consumption audit
    /// trail. Only unspent rows are replaced, so regenerating cannot resurrect a used code.
    /// </remarks>
    Task<IReadOnlyList<string>> GenerateAsync(
        string tenantId,
        Guid userId,
        int count,
        CancellationToken cancellationToken);

    /// <summary>
    /// Verifies a submitted code against the user's unspent codes and, on a match, spends it.
    /// A code is single-use: the same value never validates twice.
    /// </summary>
    /// <param name="tenantId">Tenant slug of the owning user, from their Users row.</param>
    /// <param name="userId">The owning user's UserId, resolved from the authenticated principal.</param>
    /// <param name="code">The submitted code. Case and the display hyphen are insignificant.</param>
    /// <param name="ipAddress">Client IP recorded on the spent row; may be null.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>True when the code matched and was spent by THIS call; false otherwise.</returns>
    Task<bool> ValidateAsync(
        string tenantId,
        Guid userId,
        string code,
        string? ipAddress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Counts the user's unspent codes, for a "you have N codes left" indicator.
    /// </summary>
    Task<int> RemainingCountAsync(
        string tenantId,
        Guid userId,
        CancellationToken cancellationToken);
}
