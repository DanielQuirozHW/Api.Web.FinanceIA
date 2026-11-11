// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-16

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// Query- and write-level tests for the [dbo].[TeamManagers] half of
/// <see cref="TeamRepository"/> (DDL 064), run against the EF Core in-memory provider so the actual
/// predicates and change-tracking behaviour are exercised rather than a stub's say-so.
///
/// <para>
/// THE RE-SAVE CASE IS WHY THIS FILE EXISTS. <c>PUT /api/teams/{id}</c> mirrors its manager on every
/// call, so re-saving a team WITHOUT changing the manager is the common path, not an edge case. A
/// naive "delete every row then insert the wanted set" would put a Deleted and an Added entity with
/// the same composite key in one SaveChanges, which the change tracker rejects. These tests pin the
/// reconcile that avoids it.
/// </para>
///
/// <para>
/// TENANT ISOLATION IS PROVEN HERE, not in the notifier tests, which stub this repository away. The
/// project has no EF global query filter, so the <c>TenantId</c> predicate written in this
/// repository is the only thing keeping one tenant's managers out of another's.
/// </para>
/// </summary>
public sealed class TeamRepositoryManagersTests
{
    private const string Tenant = "tenant-1";
    private const string Other  = "tenant-2";

    private static readonly Guid TeamId    = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ManagerA  = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ManagerB  = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ManagerC  = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid EditorId  = Guid.Parse("99999999-9999-9999-9999-999999999999");

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static FinanciaDbContext NewContext(string name)
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new FinanciaDbContext(options);
    }

    private static TeamRepository Sut(FinanciaDbContext ctx)
        => new(ctx, NullLogger<TeamRepository>.Instance);

    // ── Tests ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The regression this file is named for: replacing a team's managers with the SAME set must not
    /// throw. PUT /api/teams/{id} mirrors unconditionally, so this runs every time an Admin renames
    /// a team without touching its manager.
    /// </summary>
    [Fact]
    public async Task ReplaceManagers_WithAnUnchangedSet_DoesNotThrowAndKeepsOneRow()
    {
        await using var ctx = NewContext(nameof(ReplaceManagers_WithAnUnchangedSet_DoesNotThrowAndKeepsOneRow));
        var sut = Sut(ctx);

        await sut.ReplaceManagersAsync(TeamId, Tenant, [ManagerA], EditorId, CancellationToken.None);

        // The same set again — a Deleted + Added pair on one composite key if done naively.
        await sut.ReplaceManagersAsync(TeamId, Tenant, [ManagerA], EditorId, CancellationToken.None);

        var managers = await sut.GetManagerUserIdsAsync(TeamId, Tenant, CancellationToken.None);
        Assert.Equal([ManagerA], managers);
        Assert.Equal(1, await ctx.TeamManagers.CountAsync());
    }

    /// <summary>
    /// A row that survives a replace keeps its ORIGINAL CreatedAt. Re-stamping it on every unrelated
    /// team edit would destroy the record of when a manager was actually named.
    /// </summary>
    [Fact]
    public async Task ReplaceManagers_PreservesTheAuditStampOfRowsThatSurvive()
    {
        await using var ctx = NewContext(nameof(ReplaceManagers_PreservesTheAuditStampOfRowsThatSurvive));
        var sut = Sut(ctx);

        await sut.ReplaceManagersAsync(TeamId, Tenant, [ManagerA], EditorId, CancellationToken.None);

        var originalStamp = (await ctx.TeamManagers.AsNoTracking().SingleAsync()).CreatedAt;

        // Add a second manager; the first is unchanged and must not be re-stamped.
        await sut.ReplaceManagersAsync(TeamId, Tenant, [ManagerA, ManagerB], null, CancellationToken.None);

        var surviving = await ctx.TeamManagers
            .AsNoTracking()
            .SingleAsync(m => m.UserId == ManagerA);

        Assert.Equal(originalStamp, surviving.CreatedAt);
        Assert.Equal(EditorId, surviving.CreatedBy);
    }

    /// <summary>
    /// Managers removed from the set are deleted, managers added appear, and the ones in both stay.
    /// </summary>
    [Fact]
    public async Task ReplaceManagers_AddsRemovesAndKeeps()
    {
        await using var ctx = NewContext(nameof(ReplaceManagers_AddsRemovesAndKeeps));
        var sut = Sut(ctx);

        await sut.ReplaceManagersAsync(
            TeamId, Tenant, [ManagerA, ManagerB], EditorId, CancellationToken.None);

        // B drops out, C comes in, A stays.
        await sut.ReplaceManagersAsync(
            TeamId, Tenant, [ManagerA, ManagerC], EditorId, CancellationToken.None);

        var managers = await sut.GetManagerUserIdsAsync(TeamId, Tenant, CancellationToken.None);

        Assert.Equal(2, managers.Count);
        Assert.Contains(ManagerA, managers);
        Assert.Contains(ManagerC, managers);
        Assert.DoesNotContain(ManagerB, managers);
    }

    /// <summary>
    /// An empty set clears the team — the supported way to do it, and what DELETE /api/teams/{id}
    /// relies on to satisfy FK_TeamManagers_Team before removing the team row.
    /// </summary>
    [Fact]
    public async Task ReplaceManagers_WithAnEmptySet_ClearsTheTeam()
    {
        await using var ctx = NewContext(nameof(ReplaceManagers_WithAnEmptySet_ClearsTheTeam));
        var sut = Sut(ctx);

        await sut.ReplaceManagersAsync(
            TeamId, Tenant, [ManagerA, ManagerB], EditorId, CancellationToken.None);

        await sut.ReplaceManagersAsync(TeamId, Tenant, [], EditorId, CancellationToken.None);

        Assert.Empty(await sut.GetManagerUserIdsAsync(TeamId, Tenant, CancellationToken.None));
        Assert.Equal(0, await ctx.TeamManagers.CountAsync());
    }

    /// <summary>
    /// The same id twice in one call collapses to one row. PK_TeamManagers is (TeamId, UserId), so
    /// two Added entities for one key would be rejected outright.
    /// </summary>
    [Fact]
    public async Task ReplaceManagers_CollapsesDuplicateIds()
    {
        await using var ctx = NewContext(nameof(ReplaceManagers_CollapsesDuplicateIds));
        var sut = Sut(ctx);

        await sut.ReplaceManagersAsync(
            TeamId, Tenant, [ManagerA, ManagerA], EditorId, CancellationToken.None);

        Assert.Equal(1, await ctx.TeamManagers.CountAsync());
    }

    /// <summary>
    /// Replacing one tenant's managers must not touch another tenant's, even on the SAME TeamId —
    /// which is the dangerous shape, because TeamId is globally unique and a caller could pass one
    /// belonging elsewhere.
    /// </summary>
    [Fact]
    public async Task ReplaceManagers_IsTenantScoped()
    {
        await using var ctx = NewContext(nameof(ReplaceManagers_IsTenantScoped));
        var sut = Sut(ctx);

        await sut.ReplaceManagersAsync(TeamId, Tenant, [ManagerA], EditorId, CancellationToken.None);
        await sut.ReplaceManagersAsync(TeamId, Other,  [ManagerB], EditorId, CancellationToken.None);

        // Clearing one tenant leaves the other intact.
        await sut.ReplaceManagersAsync(TeamId, Tenant, [], EditorId, CancellationToken.None);

        Assert.Empty(await sut.GetManagerUserIdsAsync(TeamId, Tenant, CancellationToken.None));
        Assert.Equal([ManagerB], await sut.GetManagerUserIdsAsync(TeamId, Other, CancellationToken.None));
    }

    /// <summary>
    /// The read is tenant-scoped too: another tenant's row on the same TeamId is invisible.
    /// </summary>
    [Fact]
    public async Task GetManagerUserIds_IgnoresOtherTenants()
    {
        await using var ctx = NewContext(nameof(GetManagerUserIds_IgnoresOtherTenants));
        var sut = Sut(ctx);

        await sut.ReplaceManagersAsync(TeamId, Other, [ManagerB], EditorId, CancellationToken.None);

        Assert.Empty(await sut.GetManagerUserIdsAsync(TeamId, Tenant, CancellationToken.None));
    }

    /// <summary>
    /// A team with no managers reads as empty rather than throwing. That is a real state — it is
    /// what every team looks like before DDL 066 runs.
    /// </summary>
    [Fact]
    public async Task GetManagerUserIds_ReturnsEmpty_ForATeamWithNoManagers()
    {
        await using var ctx = NewContext(nameof(GetManagerUserIds_ReturnsEmpty_ForATeamWithNoManagers));
        var sut = Sut(ctx);

        Assert.Empty(await sut.GetManagerUserIdsAsync(TeamId, Tenant, CancellationToken.None));
    }
}
