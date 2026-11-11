// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-13

using System.Security.Claims;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Covers the 201 response of <see cref="TeamsController.CreateTeamAsync"/>.
///
/// The endpoint used to answer 500 on a SUCCESSFUL create: the row was committed by
/// <c>CreateAsync</c> and the response was then built with
/// <c>CreatedAtAction(nameof(GetTeamsAsync), ...)</c>. MVC trims the <c>Async</c> suffix from
/// action names (<c>MvcOptions.SuppressAsyncSuffixInActionNames</c> defaults to true), so no
/// endpoint answers to "GetTeamsAsync", URL generation returned null and the result threw
/// InvalidOperationException — after the insert. The client saw an error for a team that exists.
///
/// The exact-type assertion on <see cref="CreatedResult"/> is the guard: the route lookup that
/// failed only happens inside <c>CreatedAtActionResult</c>, so a regression to any
/// <c>CreatedAtAction</c> overload fails here without needing an endpoint graph.
///
/// Hand-rolled private sealed stubs (no mock library).
/// </summary>
public sealed class TeamsControllerCreateTests
{
    /// <summary>The calling Admin.</summary>
    private static readonly Guid AdminId   = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>An active user of the tenant, proposed as the team's manager.</summary>
    private static readonly Guid ManagerId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private const string Tenant      = "tenant-1";
    private const string FirebaseUid = "uid-1";
    private const string ManagerName = "Marta Manager";

    // ── Stubs ─────────────────────────────────────────────────────────────────
    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private sealed class StubTeamRepository : ITeamRepository
    {
        public int   CreateCount { get; private set; }
        public Team? Created     { get; private set; }

        /// <summary>
        /// Every ReplaceManagersAsync call, in order: the mirror write into [dbo].[TeamManagers]
        /// (DDL 064) that keeps the deprecated Teams.ManagerUserId and the new table in step.
        /// </summary>
        public List<(Guid TeamId, string TenantId, Guid[] UserIds, Guid? CreatedBy)> ManagerWrites { get; } = [];

        public Task CreateAsync(Team team, CancellationToken ct)
        {
            CreateCount++;
            Created = team;
            return Task.CompletedTask;
        }

        public Task ReplaceManagersAsync(
            Guid teamId, string tenantId, IReadOnlyCollection<Guid> userIds,
            Guid? createdBy, CancellationToken ct)
        {
            ManagerWrites.Add((teamId, tenantId, [.. userIds], createdBy));
            return Task.CompletedTask;
        }

        /// <summary>Rows the LIST endpoint should return. Empty unless a test seeds it.</summary>
        public List<Team> Existing { get; } = [];

        public Task<IReadOnlyList<Team>> GetByTenantAsync(string tenantId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Team>>(
                [.. Existing.Where(t => t.TenantId == tenantId)]);
        public Task<Team?> GetByIdAsync(Guid teamId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<Guid>> GetManagerUserIdsAsync(Guid teamId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> GetManagerUserIdsByTenantAsync(
            string tenantId, CancellationToken ct)
            // The LIST endpoint calls this. These suites exercise a team with no manager rows, for
            // which an empty dictionary is exactly what the real repository returns.
            => Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>>(
                new Dictionary<Guid, IReadOnlyList<Guid>>());
        public Task UpdateAsync(Team team, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<bool> DeleteAsync(Guid teamId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubUserRepository : IUserRepository
    {
        public User?       Caller { get; set; }
        public User?       ManagerLookup { get; set; }
        public List<User>  Roster { get; } = [];

        public Task<User?> GetByFirebaseUidAsync(string firebaseUid, CancellationToken ct)
            => Task.FromResult(Caller);

        public Task<User?> GetByUserIdAsync(Guid userId, CancellationToken ct)
            => Task.FromResult(ManagerLookup);

        public Task<IReadOnlyList<User>> GetActiveByTenantAsync(string tenantId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<User>>(Roster);

        public Task<User?> GetByEmailAsync(string email, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<bool> ExistsByFirebaseUidAsync(string firebaseUid, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<User>> GetByIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<User> CreateAsync(User user, CancellationToken ct)
            => throw new NotImplementedException();
        public Task UpdateLastLoginAsync(Guid userId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task UpdateOnboardingFlagsAsync(Guid userId, string? flagsJson, CancellationToken ct)
            => throw new NotImplementedException();
        public Task UpdateTeamAsync(Guid userId, Guid? teamId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<int> ClearTeamAsync(Guid teamId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubAuditService : IAuditService
    {
        public string? Action { get; private set; }

        public Task LogAsync(
            string tenantId, Guid? userId, string action, string severity = "Info",
            string? entityType = null, string? entityId = null, string? ipAddress = null,
            string? requestId = null, string? oldValueJson = null, string? newValueJson = null,
            CancellationToken ct = default)
        {
            Action = action;
            return Task.CompletedTask;
        }

        public Task LogSessionAsync(
            Guid tenantId, Guid? userId, string email, string eventType,
            string? ipAddress, string? userAgent, string? failureReason = null,
            CancellationToken ct = default)
            => Task.CompletedTask;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static TenantContext BuildTenant() => new()
    {
        TenantId               = Tenant,
        FirebaseProjectId      = "proj",
        FirebaseCredentialJson = "{}",
        RecaptchaSecretKey     = "secret"
    };

    private static User BuildUser(Guid userId, string displayName, bool isActive = true) => new()
    {
        UserId      = userId,
        TenantId    = Tenant,
        FirebaseUid = FirebaseUid,
        DisplayName = displayName,
        Role        = Roles.Admin,
        IsActive    = isActive
    };

    private static TeamsController BuildSut(
        StubTeamRepository teamRepo,
        StubUserRepository userRepo,
        StubAuditService   audit)
    {
        var sut = new TeamsController(
            new StubTenantContextAccessor { TenantContext = BuildTenant() },
            teamRepo,
            userRepo,
            audit,
            NullLogger<TeamsController>.Instance);

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("uid", FirebaseUid) }, "Test"))
        };
        sut.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return sut;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────
    [Fact]
    public async Task CreateTeam_WhenValid_Returns201CreatedResultWithTeamDto()
    {
        var teamRepo = new StubTeamRepository();
        var userRepo = new StubUserRepository { Caller = BuildUser(AdminId, "Ana Admin") };
        userRepo.ManagerLookup = BuildUser(ManagerId, ManagerName);
        userRepo.Roster.Add(userRepo.ManagerLookup);

        var sut = BuildSut(teamRepo, userRepo, new StubAuditService());

        var result = await sut.CreateTeamAsync(
            new CreateTeamRequest { Name = "  Riesgo Norte  ", ManagerUserId = ManagerId },
            CancellationToken.None);

        // Exact type: CreatedAtActionResult would fail here, which is the regression this locks.
        var created = Assert.IsType<CreatedResult>(result);

        Assert.Equal(201, created.StatusCode);
        Assert.Equal(1, teamRepo.CreateCount);
        Assert.Equal($"/api/teams/{teamRepo.Created!.TeamId}", created.Location);

        var dto = Assert.IsType<TeamDto>(created.Value);
        Assert.Equal(teamRepo.Created.TeamId, dto.TeamId);
        Assert.Equal("Riesgo Norte", dto.Name);
        Assert.Equal(ManagerId, dto.ManagerUserId);
        Assert.Equal(ManagerName, dto.ManagerName);
        // A team is created empty: nobody has been assigned to it yet.
        Assert.Equal(0, dto.MemberCount);

        // Every team starts at one signature — DDL 065's DEFAULT (1), which is today's behaviour.
        // The field is read-only in this release, so this is the only value it can have.
        Assert.Equal(1, dto.ApprovalQuorum);
    }

    /// <summary>
    /// The manager named here has to reach [dbo].[TeamManagers] as well as the deprecated
    /// Teams.ManagerUserId column, because the notification path now reads that table. Without the
    /// mirror, a manager named through this endpoint would be invisible to every pre-approval.
    /// </summary>
    [Fact]
    public async Task CreateTeam_MirrorsTheManagerIntoTeamManagers()
    {
        var teamRepo = new StubTeamRepository();
        var userRepo = new StubUserRepository { Caller = BuildUser(AdminId, "Ana Admin") };
        userRepo.ManagerLookup = BuildUser(ManagerId, ManagerName);
        userRepo.Roster.Add(userRepo.ManagerLookup);

        var sut = BuildSut(teamRepo, userRepo, new StubAuditService());

        await sut.CreateTeamAsync(
            new CreateTeamRequest { Name = "Riesgo Norte", ManagerUserId = ManagerId },
            CancellationToken.None);

        var write = Assert.Single(teamRepo.ManagerWrites);
        Assert.Equal(teamRepo.Created!.TeamId, write.TeamId);
        Assert.Equal(Tenant, write.TenantId);
        Assert.Equal(AdminId, write.CreatedBy);

        var mirrored = Assert.Single(write.UserIds);
        Assert.Equal(ManagerId, mirrored);

        // And the deprecated column is still written — the two are kept in step, not swapped.
        Assert.Equal(ManagerId, teamRepo.Created.ManagerUserId);
    }

    /// <summary>
    /// A team created with no manager mirrors an EMPTY set rather than skipping the call. The empty
    /// set is meaningful: it is how a team's managers are cleared, and calling unconditionally keeps
    /// create and update on the same path.
    /// </summary>
    [Fact]
    public async Task CreateTeam_WithNoManager_MirrorsAnEmptySet()
    {
        var teamRepo = new StubTeamRepository();
        var userRepo = new StubUserRepository { Caller = BuildUser(AdminId, "Ana Admin") };

        var sut = BuildSut(teamRepo, userRepo, new StubAuditService());

        await sut.CreateTeamAsync(
            new CreateTeamRequest { Name = "Sin responsable", ManagerUserId = null },
            CancellationToken.None);

        var write = Assert.Single(teamRepo.ManagerWrites);
        Assert.Empty(write.UserIds);
    }

    [Fact]
    public async Task CreateTeam_WhenNoManagerNamed_Returns201WithNullManagerName()
    {
        var teamRepo = new StubTeamRepository();
        var userRepo = new StubUserRepository { Caller = BuildUser(AdminId, "Ana Admin") };

        var sut = BuildSut(teamRepo, userRepo, new StubAuditService());

        var result = await sut.CreateTeamAsync(
            new CreateTeamRequest { Name = "Sin responsable", ManagerUserId = null },
            CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result);
        var dto     = Assert.IsType<TeamDto>(created.Value);

        Assert.Null(dto.ManagerUserId);
        Assert.Null(dto.ManagerName);
        Assert.Equal(1, teamRepo.CreateCount);
    }

    [Fact]
    public async Task CreateTeam_StampsTenantAndCaller()
    {
        var teamRepo = new StubTeamRepository();
        var userRepo = new StubUserRepository { Caller = BuildUser(AdminId, "Ana Admin") };
        var audit    = new StubAuditService();

        var sut = BuildSut(teamRepo, userRepo, audit);

        await sut.CreateTeamAsync(
            new CreateTeamRequest { Name = "Equipo", ManagerUserId = null },
            CancellationToken.None);

        Assert.Equal(Tenant,  teamRepo.Created!.TenantId);
        Assert.Equal(AdminId, teamRepo.Created.CreatedBy);
        Assert.Equal(AdminId, teamRepo.Created.UpdatedBy);
        Assert.Equal("Team.Created", audit.Action);
    }

    [Fact]
    public async Task CreateTeam_WhenManagerInactive_Returns400AndDoesNotInsert()
    {
        var teamRepo = new StubTeamRepository();
        var userRepo = new StubUserRepository { Caller = BuildUser(AdminId, "Ana Admin") };
        userRepo.ManagerLookup = BuildUser(ManagerId, ManagerName, isActive: false);

        var sut = BuildSut(teamRepo, userRepo, new StubAuditService());

        var result = await sut.CreateTeamAsync(
            new CreateTeamRequest { Name = "Equipo", ManagerUserId = ManagerId },
            CancellationToken.None);

        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(0, teamRepo.CreateCount);
    }

    [Fact]
    public async Task CreateTeam_WhenNameBlank_Returns400AndDoesNotInsert()
    {
        var teamRepo = new StubTeamRepository();
        var userRepo = new StubUserRepository { Caller = BuildUser(AdminId, "Ana Admin") };

        var sut = BuildSut(teamRepo, userRepo, new StubAuditService());

        var result = await sut.CreateTeamAsync(
            new CreateTeamRequest { Name = "   ", ManagerUserId = null },
            CancellationToken.None);

        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(0, teamRepo.CreateCount);
    }
    /// <summary>
    /// The LIST endpoint builds its DTOs inline rather than through <c>BuildDtoAsync</c>, so it is
    /// the one place ApprovalQuorum can silently go missing — serving 0 from the list while the
    /// single-team responses serve 1. This pins the two together.
    /// </summary>
    [Fact]
    public async Task GetTeams_SurfacesApprovalQuorum()
    {
        var teamRepo = new StubTeamRepository();
        var userRepo = new StubUserRepository { Caller = BuildUser(AdminId, "Ana Admin") };

        teamRepo.Existing.Add(new Team
        {
            TeamId         = Guid.NewGuid(),
            TenantId       = Tenant,
            Name           = "Riesgo Norte",
            // Whatever the row says — the endpoint must echo it, not recompute or default it.
            ApprovalQuorum = 2
        });

        var sut = BuildSut(teamRepo, userRepo, new StubAuditService());

        var result = await sut.GetTeamsAsync(CancellationToken.None);

        var ok    = Assert.IsType<OkObjectResult>(result);
        var teams = Assert.IsAssignableFrom<IReadOnlyList<TeamDto>>(ok.Value);

        Assert.Equal(2, Assert.Single(teams).ApprovalQuorum);
    }

}
