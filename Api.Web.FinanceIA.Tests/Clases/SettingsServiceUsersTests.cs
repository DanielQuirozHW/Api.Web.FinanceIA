// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-10

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// Query-level tests for <see cref="SettingsService.GetUsersAsync"/> — the admin user list behind
/// GET /api/bff/settings/users — run against the EF Core in-memory provider so the real WHERE
/// clauses are exercised rather than mocked away.
///
/// <para>
/// The team half is what these mostly cover. <c>Users.TeamId</c> (DDL 063) has no EF navigation to
/// <see cref="Team"/> — FK_Users_Team lives in the database only — so the name is resolved from a
/// dictionary built by a second, tenant-scoped query. The two ways that can go wrong are both
/// asserted here: a team of ANOTHER tenant must never lend its name, and an id that names no team
/// at all (deleted between the two queries) must degrade to a null name instead of throwing and
/// taking the whole listing down with it.
/// </para>
///
/// <para>
/// The pre-existing behaviour is pinned too, because the team fields were added to a method that
/// already had callers: inactive users stay in the list (unlike
/// <c>IUserRepository.GetActiveByTenantAsync</c>, which filters them out) and the ordering is still
/// newest-first by <c>CreatedAt</c>.
/// </para>
/// </summary>
public sealed class SettingsServiceUsersTests
{
    private const string Tenant = "tenant-1";
    private const string Other  = "tenant-2";

    // ── Harness ─────────────────────────────────────────────────────────────────
    //
    // Hand-rolled stubs, as everywhere else in this project — no mocking library. Neither the audit
    // service nor the tenant profile is reachable from the read path, so both throw rather than
    // return a default: an accidental dependency must fail loudly, not pass quietly.

    private sealed class ThrowingAuditService : IAuditService
    {
        public Task LogAsync(
            string tenantId, Guid? userId, string action, string severity = "Info",
            string? entityType = null, string? entityId = null, string? ipAddress = null,
            string? requestId = null, string? oldValueJson = null, string? newValueJson = null,
            CancellationToken ct = default)
            => throw new NotSupportedException("Listing users must not write an audit entry.");

        public Task LogSessionAsync(
            Guid tenantId, Guid? userId, string email, string eventType,
            string? ipAddress, string? userAgent, string? failureReason = null,
            CancellationToken ct = default)
            => throw new NotSupportedException("Listing users must not write a session log.");
    }

    private sealed class ThrowingTenantProfileService : ITenantProfileService
    {
        public Task<TenantProfileResolution> GetEffectiveAsync(string tenantId, CancellationToken ct)
            => throw new NotSupportedException("Listing users must not read the tenant profile.");

        public Task<TenantProfileResolution> SetEntityTypeAsync(string tenantId, string entityType, CancellationToken ct)
            => throw new NotSupportedException("Listing users must not write the tenant profile.");
    }

    private static FinanciaDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"settings-users-{Guid.NewGuid()}")
            .Options;
        return new FinanciaDbContext(options);
    }

    private static SettingsService NewService(FinanciaDbContext ctx)
        => new(ctx, new ThrowingAuditService(), new ThrowingTenantProfileService(),
               NullLogger<SettingsService>.Instance);

    private static User NewUser(
        string tenantId,
        string displayName,
        Guid? teamId = null,
        bool isActive = true,
        DateTime? createdAt = null) => new()
        {
            UserId      = Guid.NewGuid(),
            TenantId    = tenantId,
            FirebaseUid = Guid.NewGuid().ToString("N"),
            Email       = $"{displayName.ToLowerInvariant()}@example.com",
            DisplayName = displayName,
            Role        = "Analyst",
            IsActive    = isActive,
            TeamId      = teamId,
            CreatedAt   = createdAt ?? new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)
        };

    private static Team NewTeam(string tenantId, Guid teamId, string name) => new()
    {
        TeamId    = teamId,
        TenantId  = tenantId,
        Name      = name,
        CreatedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    private static async Task SeedAsync(FinanciaDbContext ctx, User[] users, params Team[] teams)
    {
        ctx.Users.AddRange(users);
        ctx.Teams.AddRange(teams);
        ctx.ChangeTracker.DetectChanges(); // AutoDetectChanges is disabled on the context
        await ctx.SaveChangesAsync();
    }

    // ── Team resolution ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetUsersAsync_WhenTheUserBelongsToATeam_ReturnsItsIdAndName()
    {
        var teamId = Guid.NewGuid();

        await using var ctx = NewContext();
        await SeedAsync(ctx,
            [NewUser(Tenant, "Ana", teamId)],
            NewTeam(Tenant, teamId, "Riesgo Norte"));

        var user = Assert.Single(await NewService(ctx).GetUsersAsync(Tenant, CancellationToken.None));

        Assert.Equal(teamId, user.TeamId);
        Assert.Equal("Riesgo Norte", user.TeamName);
    }

    [Fact]
    public async Task GetUsersAsync_WhenTheUserHasNoTeam_LeavesBothFieldsNull()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx,
            [NewUser(Tenant, "Bruno")],
            NewTeam(Tenant, Guid.NewGuid(), "Riesgo Norte"));

        var user = Assert.Single(await NewService(ctx).GetUsersAsync(Tenant, CancellationToken.None));

        Assert.Null(user.TeamId);
        Assert.Null(user.TeamName);
    }

    /// <summary>
    /// A team deleted between the two queries leaves the user pointing at an id that resolves to
    /// nothing. The id is still reported — it is what the row holds — but the name degrades to null
    /// rather than throwing, because one stale reference must not fail the entire user list.
    /// </summary>
    [Fact]
    public async Task GetUsersAsync_WhenTheTeamIdNamesNoTeam_KeepsTheIdAndNullsTheName()
    {
        var ghostTeam = Guid.NewGuid();

        await using var ctx = NewContext();
        await SeedAsync(ctx, [NewUser(Tenant, "Carla", ghostTeam)]);

        var user = Assert.Single(await NewService(ctx).GetUsersAsync(Tenant, CancellationToken.None));

        Assert.Equal(ghostTeam, user.TeamId);
        Assert.Null(user.TeamName);
    }

    /// <summary>
    /// The teams query is tenant-scoped, so another client's team cannot supply the name. There is
    /// no EF global query filter in this project: this predicate is the only thing preventing the
    /// leak of a team name across tenants.
    /// </summary>
    [Fact]
    public async Task GetUsersAsync_DoesNotResolveTheNameFromAnotherTenantsTeam()
    {
        var teamId = Guid.NewGuid();

        await using var ctx = NewContext();
        await SeedAsync(ctx,
            [NewUser(Tenant, "Diego", teamId)],
            NewTeam(Other, teamId, "Equipo ajeno"));

        var user = Assert.Single(await NewService(ctx).GetUsersAsync(Tenant, CancellationToken.None));

        Assert.Equal(teamId, user.TeamId);
        Assert.Null(user.TeamName);
    }

    // ── Behaviour that must not have changed ────────────────────────────────────

    [Fact]
    public async Task GetUsersAsync_ReturnsOnlyTheTenantsOwnUsers()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, [NewUser(Tenant, "Elena"), NewUser(Other, "Fabian")]);

        var users = await NewService(ctx).GetUsersAsync(Tenant, CancellationToken.None);

        Assert.Equal("Elena", Assert.Single(users).DisplayName);
    }

    /// <summary>
    /// Deactivated accounts stay in the admin list — an Admin has to see them to reactivate them.
    /// </summary>
    [Fact]
    public async Task GetUsersAsync_IncludesInactiveUsers()
    {
        var teamId = Guid.NewGuid();

        await using var ctx = NewContext();
        await SeedAsync(ctx,
            [NewUser(Tenant, "Gabriela", teamId, isActive: false)],
            NewTeam(Tenant, teamId, "Riesgo Sur"));

        var user = Assert.Single(await NewService(ctx).GetUsersAsync(Tenant, CancellationToken.None));

        Assert.False(user.IsActive);
        Assert.Equal("Riesgo Sur", user.TeamName);
    }

    [Fact]
    public async Task GetUsersAsync_OrdersByCreatedAtDescending()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx,
        [
            NewUser(Tenant, "Vieja",  createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            NewUser(Tenant, "Nueva",  createdAt: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)),
            NewUser(Tenant, "Media",  createdAt: new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc))
        ]);

        var users = await NewService(ctx).GetUsersAsync(Tenant, CancellationToken.None);

        Assert.Equal(["Nueva", "Media", "Vieja"], users.Select(u => u.DisplayName));
    }

    [Fact]
    public async Task GetUsersAsync_MarksEveryUserAsAcceptedInvitation()
    {
        await using var ctx = NewContext();
        await SeedAsync(ctx, [NewUser(Tenant, "Hugo")]);

        var user = Assert.Single(await NewService(ctx).GetUsersAsync(Tenant, CancellationToken.None));

        Assert.Equal(InvitationStatusResolver.Accepted, user.InvitationStatus);
    }
}
