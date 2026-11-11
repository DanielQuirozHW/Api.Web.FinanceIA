// Project : Api.Web.FinanceIA
// Created : 2026-09-03

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// EF Core implementation of <see cref="IMfaBackupCodeService"/> over
/// [dbo].[MfaBackupCodes] in the FinancIA business database.
/// </summary>
/// <remarks>
/// <para>
/// <b>No plaintext ever leaves this class except through <see cref="GenerateAsync"/>'s return
/// value.</b> Nothing here logs a code, a hash, or any fragment of either.
/// </para>
/// <para>
/// <b>FinanciaDbContext disables automatic change detection</b> (see its constructor, exactly as
/// AppDbContext did before this table moved), so a plain property assignment on a tracked entity
/// produces NO UPDATE at SaveChanges — a validated code would silently stay spendable forever.
/// <see cref="ValidateAsync"/> therefore marks the two touched properties modified explicitly
/// rather than relying on detection.
/// </para>
/// </remarks>
public sealed class MfaBackupCodeService : IMfaBackupCodeService
{
    /// <summary>Codes issued when the caller does not ask for a specific number.</summary>
    public const int DefaultCodeCount = 10;

    /// <summary>Upper bound on one issued set. Guards against an absurd request.</summary>
    public const int MaxCodeCount = 20;

    /// <summary>[UsedIp] is nvarchar(45) — wide enough for an IPv6 literal, not for a header.</summary>
    private const int MaxUsedIpLength = 45;

    private readonly FinanciaDbContext                _dbContext;
    private readonly ILogger<MfaBackupCodeService>    _logger;

    public MfaBackupCodeService(
        FinanciaDbContext             dbContext,
        ILogger<MfaBackupCodeService> logger)
    {
        _dbContext = dbContext;
        _logger    = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GenerateAsync(
        string tenantId,
        Guid userId,
        int count,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        if (count is < 1 or > MaxCodeCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count),
                $"El número de códigos debe estar entre 1 y {MaxCodeCount}.");
        }

        // Tracked on purpose — RemoveRange needs tracked entities. Only UNSPENT rows are
        // replaced; spent rows stay as the consumption audit trail.
        var superseded = await _dbContext.MfaBackupCodes
            .Where(x => x.TenantId == tenantId && x.UserId == userId && x.UsedAt == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (superseded.Count > 0)
        {
            _dbContext.MfaBackupCodes.RemoveRange(superseded);
        }

        var now   = DateTime.UtcNow;
        var codes = GenerateDistinctCodes(count);

        foreach (var code in codes)
        {
            _dbContext.MfaBackupCodes.Add(new MfaBackupCode
            {
                TenantId  = tenantId,
                UserId    = userId,
                CodeHash  = MfaBackupCodeHasher.Hash(code),
                CreatedAt = now,
                UsedAt    = null,
                UsedIp    = null
            });
        }

        // One SaveChanges, so the delete and the insert land in a single transaction: the user
        // is never left with neither the old set nor the new one.
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "MFA backup codes regenerated. TenantId: {TenantId} | UserId: {UserId} | " +
            "Issued: {Issued} | Superseded: {Superseded}",
            tenantId,
            userId,
            codes.Count,
            superseded.Count);

        return codes;
    }

    /// <inheritdoc />
    public async Task<bool> ValidateAsync(
        string tenantId,
        Guid userId,
        string code,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var normalized = MfaBackupCodeHasher.Normalize(code);

        var candidates = await _dbContext.MfaBackupCodes
            .Where(x => x.TenantId == tenantId && x.UserId == userId && x.UsedAt == null)
            .OrderBy(x => x.BackupCodeId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (candidates.Count == 0 || normalized.Length == 0)
        {
            // Burn one derivation so "this user has no codes" and "that code was wrong" cost
            // the same wall-clock time from outside.
            MfaBackupCodeHasher.BurnOneVerification();

            _logger.LogWarning(
                "MFA backup code rejected. TenantId: {TenantId} | UserId: {UserId} | Reason: {Reason}",
                tenantId,
                userId,
                candidates.Count == 0 ? "no_unused_codes" : "empty_code");

            return false;
        }

        // Every candidate is verified, with no early exit: stopping at the first match would
        // make the response time reveal how far down the set the submitted code sat.
        MfaBackupCode? match = null;

        foreach (var candidate in candidates)
        {
            if (MfaBackupCodeHasher.Verify(normalized, candidate.CodeHash) && match is null)
            {
                match = candidate;
            }
        }

        if (match is null)
        {
            _logger.LogWarning(
                "MFA backup code rejected. TenantId: {TenantId} | UserId: {UserId} | Reason: {Reason}",
                tenantId,
                userId,
                "no_match");

            return false;
        }

        match.UsedAt = DateTime.UtcNow;
        match.UsedIp = Truncate(ipAddress, MaxUsedIpLength);

        // AutoDetectChangesEnabled is false on this context, so the assignments above are
        // invisible to SaveChanges unless the properties are marked modified by hand.
        var entry = _dbContext.Entry(match);
        entry.Property(x => x.UsedAt).IsModified = true;
        entry.Property(x => x.UsedIp).IsModified = true;

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // UsedAt is a concurrency token, so the UPDATE carries "AND UsedAt IS NULL" and
            // affects no rows when a concurrent request spent the same code first. That request
            // won; this one must not also report success, or the code would be spent twice.
            _logger.LogWarning(
                "MFA backup code rejected. TenantId: {TenantId} | UserId: {UserId} | Reason: {Reason}",
                tenantId,
                userId,
                "already_spent_concurrently");

            return false;
        }

        _logger.LogInformation(
            "MFA backup code accepted and spent. TenantId: {TenantId} | UserId: {UserId} | " +
            "Remaining: {Remaining}",
            tenantId,
            userId,
            candidates.Count - 1);

        return true;
    }

    /// <inheritdoc />
    public Task<int> RemainingCountAsync(
        string tenantId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        return _dbContext.MfaBackupCodes
            .AsNoTracking()
            .CountAsync(
                x => x.TenantId == tenantId && x.UserId == userId && x.UsedAt == null,
                cancellationToken);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Draws <paramref name="count"/> codes, rejecting a repeat within the same set. A collision
    /// is vanishingly unlikely at 40 bits per code, but a duplicate would quietly halve the
    /// user's real recovery options, and the guard costs nothing.
    /// </summary>
    private static List<string> GenerateDistinctCodes(int count)
    {
        var codes = new List<string>(count);
        var seen  = new HashSet<string>(StringComparer.Ordinal);

        while (codes.Count < count)
        {
            var code = MfaBackupCodeHasher.GenerateCode();

            if (seen.Add(MfaBackupCodeHasher.Normalize(code)))
            {
                codes.Add(code);
            }
        }

        return codes;
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        return trimmed.Length <= maxLength
            ? trimmed
            : trimmed[..maxLength];
    }
}
