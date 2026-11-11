// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-03

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// Behaviour tests for <see cref="MfaBackupCodeService"/> against the EF Core in-memory provider
/// (a database provider, not a mocking library), so the real WHERE clauses and the real change
/// tracking are exercised.
///
/// <para>
/// The load-bearing test here is <see cref="ValidateAsync_SpendsACodeExactlyOnce"/>.
/// <see cref="FinanciaDbContext"/> disables automatic change detection in its constructor — as
/// <c>AppDbContext</c> did before this table moved databases — so assigning <c>UsedAt</c> on a
/// tracked entity produces NO UPDATE at SaveChanges unless the property is marked modified by
/// hand. The failure that causes is silent and total: validation keeps answering true and every
/// backup code becomes infinitely reusable, which is exactly the property a single-use code
/// exists to deny.
/// </para>
/// <para>
/// The second is <see cref="ValidateAsync_IsScopedToTheOwningUser"/>: the (TenantId, UserId)
/// predicate is the only thing standing between one user's code and another user's account.
/// </para>
/// </summary>
public sealed class MfaBackupCodeServiceTests
{
    private const string TenantA = "SgrOneClickSandboxFB2";
    private const string TenantB = "OtroTenantFB3";
    private const string Ip      = "203.0.113.7";

    // Stand-ins for two [dbo].[Users] rows. The service is keyed by these, resolved by the
    // controller from the authenticated principal's Firebase uid.
    private static readonly Guid UserA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UserB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static FinanciaDbContext NewContext(string databaseName)
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;

        return new FinanciaDbContext(options);
    }

    private static FinanciaDbContext NewContext()
        => NewContext($"mfa-backup-codes-{Guid.NewGuid()}");

    private static MfaBackupCodeService NewService(FinanciaDbContext context)
        => new(context, NullLogger<MfaBackupCodeService>.Instance);

    // ── GenerateAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateAsync_IssuesTheRequestedNumberOfDistinctCodes()
    {
        using var context = NewContext();
        var service = NewService(context);

        var codes = await service.GenerateAsync(TenantA, UserA, 10, CancellationToken.None);

        Assert.Equal(10, codes.Count);
        Assert.Equal(10, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(10, await context.MfaBackupCodes.CountAsync());
    }

    [Fact]
    public async Task GenerateAsync_StoresHashesAndNeverThePlaintext()
    {
        using var context = NewContext();
        var service = NewService(context);

        var codes = await service.GenerateAsync(TenantA, UserA, 5, CancellationToken.None);

        var stored = await context.MfaBackupCodes.AsNoTracking().ToListAsync();

        Assert.All(stored, row =>
        {
            Assert.StartsWith("pbkdf2-sha256$", row.CodeHash, StringComparison.Ordinal);
            Assert.Null(row.UsedAt);
            Assert.Null(row.UsedIp);
            Assert.Equal(TenantA, row.TenantId);
            Assert.Equal(UserA,   row.UserId);
        });

        // The plaintext must appear in no column of any row.
        foreach (var code in codes)
        {
            var normalized = MfaBackupCodeHasher.Normalize(code);

            Assert.DoesNotContain(stored, row =>
                row.CodeHash.Contains(normalized, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task GenerateAsync_ReplacesTheUnusedSetButKeepsSpentCodes()
    {
        using var context = NewContext();
        var service = NewService(context);

        var first = await service.GenerateAsync(TenantA, UserA, 10, CancellationToken.None);

        // Spend one, then regenerate.
        Assert.True(await service.ValidateAsync(TenantA, UserA, first[0], Ip, CancellationToken.None));

        var second = await service.GenerateAsync(TenantA, UserA, 10, CancellationToken.None);

        var rows = await context.MfaBackupCodes.AsNoTracking().ToListAsync();

        // 10 fresh unspent rows, plus the one spent row kept as the audit trail. The other nine
        // from the first set are gone — a superseded code must not still open the door.
        Assert.Equal(11, rows.Count);
        Assert.Equal(10, rows.Count(r => r.UsedAt is null));
        Assert.Equal(1,  rows.Count(r => r.UsedAt is not null));

        Assert.False(await service.ValidateAsync(TenantA, UserA, first[1], Ip, CancellationToken.None));
        Assert.True(await service.ValidateAsync(TenantA, UserA, second[0], Ip, CancellationToken.None));
    }

    [Fact]
    public async Task GenerateAsync_LeavesOtherUsersUntouched()
    {
        using var context = NewContext();
        var service = NewService(context);

        var codesB = await service.GenerateAsync(TenantB, UserB, 10, CancellationToken.None);
        await service.GenerateAsync(TenantA, UserA, 10, CancellationToken.None);
        await service.GenerateAsync(TenantA, UserA, 10, CancellationToken.None);

        Assert.Equal(10, await service.RemainingCountAsync(TenantB, UserB, CancellationToken.None));
        Assert.True(await service.ValidateAsync(TenantB, UserB, codesB[0], Ip, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(21)]
    public async Task GenerateAsync_RejectsAnOutOfRangeCount(int count)
    {
        using var context = NewContext();
        var service = NewService(context);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.GenerateAsync(TenantA, UserA, count, CancellationToken.None));
    }

    [Fact]
    public async Task GenerateAsync_RejectsABlankTenant()
    {
        using var context = NewContext();
        var service = NewService(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.GenerateAsync("   ", UserA, 10, CancellationToken.None));
    }

    // ── ValidateAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ValidateAsync_SpendsACodeExactlyOnce()
    {
        using var context = NewContext();
        var service = NewService(context);

        var codes = await service.GenerateAsync(TenantA, UserA, 10, CancellationToken.None);

        Assert.True(await service.ValidateAsync(TenantA, UserA, codes[3], Ip, CancellationToken.None));

        // The second attempt is the whole point. It fails only if the first one actually WROTE
        // UsedAt — which, with AutoDetectChangesEnabled off, needs the explicit IsModified marking
        // in the service.
        Assert.False(await service.ValidateAsync(TenantA, UserA, codes[3], Ip, CancellationToken.None));

        Assert.Equal(9, await service.RemainingCountAsync(TenantA, UserA, CancellationToken.None));
    }

    [Fact]
    public async Task ValidateAsync_PersistsTheSpendAcrossContexts()
    {
        var databaseName = $"mfa-backup-codes-{Guid.NewGuid()}";

        IReadOnlyList<string> codes;

        using (var context = NewContext(databaseName))
        {
            codes = await NewService(context).GenerateAsync(TenantA, UserA, 10, CancellationToken.None);
            Assert.True(await NewService(context)
                .ValidateAsync(TenantA, UserA, codes[0], Ip, CancellationToken.None));
        }

        // A fresh context reads the store, not the change tracker: if the UPDATE never reached
        // the database, this is where an in-memory-only mutation would be exposed.
        using (var context = NewContext(databaseName))
        {
            var service = NewService(context);

            Assert.False(await service.ValidateAsync(TenantA, UserA, codes[0], Ip, CancellationToken.None));
            Assert.Equal(9, await service.RemainingCountAsync(TenantA, UserA, CancellationToken.None));
        }
    }

    [Fact]
    public async Task ValidateAsync_RecordsWhenAndFromWhereTheCodeWasSpent()
    {
        using var context = NewContext();
        var service = NewService(context);

        var codes  = await service.GenerateAsync(TenantA, UserA, 10, CancellationToken.None);
        var before = DateTime.UtcNow;

        Assert.True(await service.ValidateAsync(TenantA, UserA, codes[0], Ip, CancellationToken.None));

        var spent = await context.MfaBackupCodes
            .AsNoTracking()
            .SingleAsync(x => x.UsedAt != null);

        Assert.Equal(Ip, spent.UsedIp);
        Assert.NotNull(spent.UsedAt);
        Assert.InRange(spent.UsedAt!.Value, before.AddSeconds(-5), DateTime.UtcNow.AddSeconds(5));
    }

    [Fact]
    public async Task ValidateAsync_TruncatesAnOverlongIpToTheColumnWidth()
    {
        using var context = NewContext();
        var service = NewService(context);

        var codes = await service.GenerateAsync(TenantA, UserA, 2, CancellationToken.None);

        await service.ValidateAsync(TenantA, UserA, codes[0], new string('9', 200), CancellationToken.None);

        var spent = await context.MfaBackupCodes.AsNoTracking().SingleAsync(x => x.UsedAt != null);

        // [UsedIp] is nvarchar(45); an oversized value must be clamped, not rejected by SQL later.
        Assert.Equal(45, spent.UsedIp!.Length);
    }

    [Fact]
    public async Task ValidateAsync_AcceptsTheCodeHoweverTheUserTypedIt()
    {
        using var context = NewContext();
        var service = NewService(context);

        var codes = await service.GenerateAsync(TenantA, UserA, 10, CancellationToken.None);
        var typed = codes[0].Replace("-", string.Empty).ToLowerInvariant();

        Assert.True(await service.ValidateAsync(TenantA, UserA, typed, Ip, CancellationToken.None));
    }

    [Fact]
    public async Task ValidateAsync_IsScopedToTheOwningUser()
    {
        using var context = NewContext();
        var service = NewService(context);

        var codesA = await service.GenerateAsync(TenantA, UserA, 10, CancellationToken.None);
        await service.GenerateAsync(TenantB, UserB, 10, CancellationToken.None);

        // User B submitting user A's code must fail, and must not consume it either.
        // This is the predicate that separates one user's recovery codes from another's.
        Assert.False(await service.ValidateAsync(TenantB, UserB, codesA[0], Ip, CancellationToken.None));
        Assert.Equal(10, await service.RemainingCountAsync(TenantA, UserA, CancellationToken.None));
        Assert.True(await service.ValidateAsync(TenantA, UserA, codesA[0], Ip, CancellationToken.None));
    }

    [Fact]
    public async Task ValidateAsync_IsScopedToTheOwningUserWithinOneTenant()
    {
        using var context = NewContext();
        var service = NewService(context);

        // Both users belong to the SAME tenant, so the tenant predicate matches for both and only
        // the UserId one separates them. That is the case the (TenantId, UserId) key exists for:
        // two analysts of one PyME's SGR must not be able to spend each other's recovery codes.
        var codesA = await service.GenerateAsync(TenantA, UserA, 10, CancellationToken.None);
        await service.GenerateAsync(TenantA, UserB, 10, CancellationToken.None);

        Assert.False(await service.ValidateAsync(
            TenantA, UserB, codesA[0], Ip, CancellationToken.None));

        Assert.Equal(10, await service.RemainingCountAsync(TenantA, UserA, CancellationToken.None));
        Assert.Equal(10, await service.RemainingCountAsync(TenantA, UserB, CancellationToken.None));
    }

    [Fact]
    public async Task ValidateAsync_IsScopedToTheOwningTenant()
    {
        using var context = NewContext();
        var service = NewService(context);

        var codes = await service.GenerateAsync(TenantA, UserA, 10, CancellationToken.None);

        // Same user id, wrong tenant. Every FinanciaDbContext query carries a TenantId predicate
        // and there is no global query filter to add one, so this asserts the service writes its
        // own — the rows are invisible under any other tenant, and stay unspent.
        Assert.False(await service.ValidateAsync(
            TenantB, UserA, codes[0], Ip, CancellationToken.None));

        Assert.Equal(0,  await service.RemainingCountAsync(TenantB, UserA, CancellationToken.None));
        Assert.Equal(10, await service.RemainingCountAsync(TenantA, UserA, CancellationToken.None));
    }

    [Fact]
    public async Task ValidateAsync_RejectsAWrongCodeWithoutConsumingAnything()
    {
        using var context = NewContext();
        var service = NewService(context);

        await service.GenerateAsync(TenantA, UserA, 10, CancellationToken.None);

        Assert.False(await service.ValidateAsync(TenantA, UserA, "ZZZZ-9999", Ip, CancellationToken.None));
        Assert.Equal(10, await service.RemainingCountAsync(TenantA, UserA, CancellationToken.None));
        Assert.Empty(await context.MfaBackupCodes.AsNoTracking()
            .Where(x => x.UsedAt != null)
            .ToListAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("----")]
    public async Task ValidateAsync_RejectsAnEmptySubmission(string submitted)
    {
        using var context = NewContext();
        var service = NewService(context);

        await service.GenerateAsync(TenantA, UserA, 10, CancellationToken.None);

        Assert.False(await service.ValidateAsync(TenantA, UserA, submitted, Ip, CancellationToken.None));
        Assert.Equal(10, await service.RemainingCountAsync(TenantA, UserA, CancellationToken.None));
    }

    [Fact]
    public async Task ValidateAsync_ReturnsFalseForAUserWithNoCodesAtAll()
    {
        using var context = NewContext();
        var service = NewService(context);

        Assert.False(await service.ValidateAsync(
            TenantA, UserA, "ABCD-2345", Ip, CancellationToken.None));
    }

    // ── RemainingCountAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task RemainingCountAsync_CountsOnlyUnspentCodesOfThatUser()
    {
        using var context = NewContext();
        var service = NewService(context);

        var codes = await service.GenerateAsync(TenantA, UserA, 10, CancellationToken.None);
        await service.GenerateAsync(TenantB, UserB, 4, CancellationToken.None);

        await service.ValidateAsync(TenantA, UserA, codes[0], Ip, CancellationToken.None);
        await service.ValidateAsync(TenantA, UserA, codes[1], Ip, CancellationToken.None);

        Assert.Equal(8, await service.RemainingCountAsync(TenantA, UserA, CancellationToken.None));
        Assert.Equal(4, await service.RemainingCountAsync(TenantB, UserB, CancellationToken.None));
    }

    [Fact]
    public async Task RemainingCountAsync_IsZeroForAnUnknownUser()
    {
        using var context = NewContext();

        Assert.Equal(0, await NewService(context)
            .RemainingCountAsync(TenantA, Guid.NewGuid(), CancellationToken.None));
    }
}
