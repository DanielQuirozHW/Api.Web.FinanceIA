// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-16

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
/// Covers the WRITE path for a team's manager SET and its approval quorum —
/// <see cref="TeamsController.CreateTeamAsync"/> and <see cref="TeamsController.UpdateTeamAsync"/>.
///
/// Both used to take a single <c>ManagerUserId</c> and mirror it into [dbo].[TeamManagers] as a
/// 0/1-element set, while <c>ApprovalQuorum</c> was read-only and every team therefore sat at the
/// DDL 065 default of 1. Since Phase 2 the quorum decides how many signatures commit a credit
/// decision, so these two fields are what an Admin actually configures.
///
/// What these tests pin, in order of how much it would cost to get wrong:
///  • Validation happens BEFORE any write. One bad id in the array must leave the team untouched —
///    a partial apply would drop managers on a request that answered 400.
///  • An omitted quorum PRESERVES the stored value on update. The opposite (a full replace) would
///    silently reset a configured quorum of 2 to 1 on a plain rename, weakening a credit control
///    with nothing in the UI to show for it.
///  • A quorum ABOVE the manager count is accepted. The engine caps the effective quorum at the
///    eligible-manager count, so rejecting it here would make the valid range depend on a set that
///    changes independently.
///  • The deprecated Teams.ManagerUserId keeps mirroring the FIRST of the set, which is what makes
///    the union read in TeamManagerResolution resolve someone on a tenant whose DDL 066 never ran.
///  • The LIST endpoint's query count does not grow with the number of teams.
///
/// Hand-rolled private sealed stubs (no mock library).
/// </summary>
public sealed class TeamsControllerManagersQuorumTests
{
    /// <summary>The calling Admin.</summary>
    private static readonly Guid AdminId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>Three active users of the tenant, available as managers.</summary>
    private static readonly Guid ManagerA = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ManagerB = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ManagerC = Guid.Parse("44444444-4444-4444-4444-444444444444");

    /// <summary>A user of ANOTHER tenant — absent from this tenant's active roster.</summary>
    private static readonly Guid Outsider = Guid.Parse("55555555-5555-5555-5555-555555555555");

    /// <summary>A user of this tenant whose account has been deactivated.</summary>
    private static readonly Guid Deactivated = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private static readonly Guid ExistingTeamId = Guid.Parse("77777777-7777-7777-7777-777777777777");

    private const string Tenant      = "tenant-1";
    private const string FirebaseUid = "uid-1";

    // ── Stubs ─────────────────────────────────────────────────────────────────
    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private sealed class StubTeamRepository : ITeamRepository
    {
        public int   CreateCount { get; private set; }
        public int   UpdateCount { get; private set; }
        public Team? Created     { get; private set; }
        public Team? Updated     { get; private set; }

        /// <summary>Every ReplaceManagersAsync call, in order, with the set it was given.</summary>
        public List<(Guid TeamId, string TenantId, Guid[] UserIds, Guid? CreatedBy)> ManagerWrites { get; } = [];

        public List<Team> Existing { get; } = [];

        /// <summary>Manager rows keyed by team, as [dbo].[TeamManagers] would hold them.</summary>
        public Dictionary<Guid, IReadOnlyList<Guid>> ManagerRows { get; } = [];

        /// <summary>
        /// How many times the LIST endpoint went to this repository. The N+1 guard reads it: it must
        /// not grow with the number of teams.
        /// </summary>
        public int ReadCallCount { get; private set; }

        public Task CreateAsync(Team team, CancellationToken ct)
        {
            CreateCount++;
            Created = team;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(Team team, CancellationToken ct)
        {
            UpdateCount++;
            Updated = team;
            return Task.CompletedTask;
        }

        public Task ReplaceManagersAsync(
            Guid teamId, string tenantId, IReadOnlyCollection<Guid> userIds,
            Guid? createdBy, CancellationToken ct)
        {
            ManagerWrites.Add((teamId, tenantId, [.. userIds], createdBy));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Team>> GetByTenantAsync(string tenantId, CancellationToken ct)
        {
            ReadCallCount++;
            return Task.FromResult<IReadOnlyList<Team>>([.. Existing.Where(t => t.TenantId == tenantId)]);
        }

        public Task<Team?> GetByIdAsync(Guid teamId, string tenantId, CancellationToken ct)
            => Task.FromResult(Existing.FirstOrDefault(t => t.TeamId == teamId && t.TenantId == tenantId));

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> GetManagerUserIdsByTenantAsync(
            string tenantId, CancellationToken ct)
        {
            ReadCallCount++;
            return Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>>(ManagerRows);
        }

        public Task<IReadOnlyList<Guid>> GetManagerUserIdsAsync(Guid teamId, string tenantId, CancellationToken ct)
            // The single-team read is what the batch above replaces on the list path. Throwing means a
            // regression to a per-team loop fails loudly here instead of quietly costing a query each.
            => throw new NotImplementedException();

        public Task<bool> DeleteAsync(Guid teamId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubUserRepository : IUserRepository
    {
        public User?      Caller { get; set; }
        public List<User> Roster { get; } = [];

        public Task<User?> GetByFirebaseUidAsync(string firebaseUid, CancellationToken ct)
            => Task.FromResult(Caller);

        /// <summary>
        /// Mirrors the real GetActiveByTenantAsync: TenantId AND IsActive, which is exactly the
        /// predicate the controller's manager validation now relies on.
        /// </summary>
        public Task<IReadOnlyList<User>> GetActiveByTenantAsync(string tenantId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<User>>(
                [.. Roster.Where(u => u.TenantId == tenantId && u.IsActive)]);

        public Task<User?> GetByUserIdAsync(Guid userId, CancellationToken ct)
            // The set-based validation resolves everyone from the roster in ONE query. A per-id lookup
            // reaching this stub would mean that guarantee was lost.
            => throw new NotImplementedException();

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
        public Task LogAsync(
            string tenantId, Guid? userId, string action, string severity = "Info",
            string? entityType = null, string? entityId = null, string? ipAddress = null,
            string? requestId = null, string? oldValueJson = null, string? newValueJson = null,
            CancellationToken ct = default)
            => Task.CompletedTask;

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

    private static User BuildUser(
        Guid userId, string displayName, bool isActive = true, string tenantId = Tenant) => new()
    {
        UserId      = userId,
        TenantId    = tenantId,
        FirebaseUid = FirebaseUid,
        DisplayName = displayName,
        Role        = Roles.Admin,
        IsActive    = isActive
    };

    /// <summary>
    /// A repository/user pair with the calling Admin and the three candidate managers all active in
    /// this tenant, plus an outsider and a deactivated account to fail validation against.
    /// </summary>
    private static (StubTeamRepository Teams, StubUserRepository Users) BuildRepositories()
    {
        var users = new StubUserRepository { Caller = BuildUser(AdminId, "Ana Admin") };

        users.Roster.Add(users.Caller!);
        users.Roster.Add(BuildUser(ManagerA, "Ana Manager"));
        users.Roster.Add(BuildUser(ManagerB, "Bruno Manager"));
        users.Roster.Add(BuildUser(ManagerC, "Carla Manager"));
        users.Roster.Add(BuildUser(Outsider, "Otro Tenant", tenantId: "tenant-2"));
        users.Roster.Add(BuildUser(Deactivated, "Dario Baja", isActive: false));

        return (new StubTeamRepository(), users);
    }

    private static TeamsController BuildSut(StubTeamRepository teams, StubUserRepository users)
    {
        var sut = new TeamsController(
            new StubTenantContextAccessor { TenantContext = BuildTenant() },
            teams,
            users,
            new StubAuditService(),
            NullLogger<TeamsController>.Instance);

        sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("uid", FirebaseUid)], "Test"))
            }
        };

        return sut;
    }

    /// <summary>An existing team to update, with the given stored quorum and manager.</summary>
    private static Team SeedTeam(
        StubTeamRepository teams, int approvalQuorum = 1, Guid? managerUserId = null)
    {
        var team = new Team
        {
            TeamId         = ExistingTeamId,
            TenantId       = Tenant,
            Name           = "Riesgo Norte",
            ManagerUserId  = managerUserId,
            ApprovalQuorum = approvalQuorum,
            CreatedAt      = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt      = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        teams.Existing.Add(team);
        return team;
    }

    private static TeamDto DtoOf(IActionResult result) => result switch
    {
        CreatedResult created => Assert.IsType<TeamDto>(created.Value),
        OkObjectResult ok     => Assert.IsType<TeamDto>(ok.Value),
        _                     => throw new InvalidOperationException($"Unexpected result {result.GetType().Name}")
    };

    // ── The manager set: 0, 1 and 3 ───────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task CreateTeam_PersistsExactlyTheRequestedManagerSet(int managerCount)
    {
        var (teams, users) = BuildRepositories();
        var sut = BuildSut(teams, users);

        List<Guid> requested = managerCount switch
        {
            0 => [],
            1 => [ManagerA],
            _ => [ManagerA, ManagerB, ManagerC]
        };

        var result = await sut.CreateTeamAsync(
            new CreateTeamRequest { Name = "Riesgo Norte", ManagerUserIds = requested },
            CancellationToken.None);

        Assert.IsType<CreatedResult>(result);

        var write = Assert.Single(teams.ManagerWrites);
        Assert.Equal(requested, write.UserIds);
        Assert.Equal(Tenant, write.TenantId);
        Assert.Equal(AdminId, write.CreatedBy);
        Assert.Equal(teams.Created!.TeamId, write.TeamId);

        Assert.Equal(requested, [.. DtoOf(result).Managers.Select(m => m.UserId)]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task UpdateTeam_ReplacesTheManagerSetWithExactlyWhatWasSent(int managerCount)
    {
        var (teams, users) = BuildRepositories();
        SeedTeam(teams, managerUserId: ManagerC);
        var sut = BuildSut(teams, users);

        List<Guid> requested = managerCount switch
        {
            0 => [],
            1 => [ManagerA],
            _ => [ManagerA, ManagerB, ManagerC]
        };

        var result = await sut.UpdateTeamAsync(
            ExistingTeamId,
            new UpdateTeamRequest { Name = "Riesgo Norte", ManagerUserIds = requested },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);

        var write = Assert.Single(teams.ManagerWrites);
        Assert.Equal(requested, write.UserIds);
        Assert.Equal(ExistingTeamId, write.TeamId);
    }

    [Fact]
    public async Task CreateTeam_CollapsesDuplicateIds_KeepingTheOrderOfTheSurvivors()
    {
        var (teams, users) = BuildRepositories();
        var sut = BuildSut(teams, users);

        var result = await sut.CreateTeamAsync(
            new CreateTeamRequest
            {
                Name           = "Riesgo Norte",
                ManagerUserIds = [ManagerB, ManagerA, ManagerB, ManagerA]
            },
            CancellationToken.None);

        Assert.IsType<CreatedResult>(result);

        // First occurrence wins, so B stays first — which also decides the legacy mirror below.
        Assert.Equal([ManagerB, ManagerA], Assert.Single(teams.ManagerWrites).UserIds);
        Assert.Equal(ManagerB, teams.Created!.ManagerUserId);
    }

    // ── Validation: nothing is written on a bad request ───────────────────────

    public static TheoryData<Guid> RejectedManagerIds() => new()
    {
        Outsider,     // active, but another tenant's
        Deactivated,  // this tenant's, but not active
        Guid.Empty,   // never a user
        Guid.Parse("99999999-9999-9999-9999-999999999999") // no such user at all
    };

    [Theory]
    [MemberData(nameof(RejectedManagerIds))]
    public async Task CreateTeam_WhenAnyIdIsNotAnActiveTenantUser_Returns400AndWritesNothing(Guid badId)
    {
        var (teams, users) = BuildRepositories();
        var sut = BuildSut(teams, users);

        // The bad id sits in the MIDDLE: a validator that stopped at the first entry, or that ran
        // per-id as it wrote, would have already persisted ManagerA by the time it reached this one.
        var result = await sut.CreateTeamAsync(
            new CreateTeamRequest { Name = "Riesgo Norte", ManagerUserIds = [ManagerA, badId, ManagerC] },
            CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, problem.StatusCode);

        Assert.Equal(0, teams.CreateCount);
        Assert.Empty(teams.ManagerWrites);
    }

    [Theory]
    [MemberData(nameof(RejectedManagerIds))]
    public async Task UpdateTeam_WhenAnyIdIsNotAnActiveTenantUser_Returns400AndLeavesTheTeamUntouched(Guid badId)
    {
        var (teams, users) = BuildRepositories();
        var team = SeedTeam(teams, approvalQuorum: 2, managerUserId: ManagerC);
        var sut = BuildSut(teams, users);

        var result = await sut.UpdateTeamAsync(
            ExistingTeamId,
            new UpdateTeamRequest
            {
                Name           = "Nombre nuevo",
                ManagerUserIds = [ManagerA, badId],
                ApprovalQuorum = 3
            },
            CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, problem.StatusCode);

        Assert.Equal(0, teams.UpdateCount);
        Assert.Empty(teams.ManagerWrites);

        // The in-memory row is the same object the controller mutates, so this also pins that no
        // field was written before validation ran.
        Assert.Equal("Riesgo Norte", team.Name);
        Assert.Equal(2, team.ApprovalQuorum);
        Assert.Equal(ManagerC, team.ManagerUserId);
    }

    // ── The quorum ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task CreateTeam_WhenQuorumIsBelowOne_Returns400AndWritesNothing(int quorum)
    {
        var (teams, users) = BuildRepositories();
        var sut = BuildSut(teams, users);

        var result = await sut.CreateTeamAsync(
            new CreateTeamRequest
            {
                Name           = "Riesgo Norte",
                ManagerUserIds = [ManagerA],
                ApprovalQuorum = quorum
            },
            CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, problem.StatusCode);

        Assert.Equal(0, teams.CreateCount);
        Assert.Empty(teams.ManagerWrites);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task UpdateTeam_WhenQuorumIsBelowOne_Returns400AndWritesNothing(int quorum)
    {
        var (teams, users) = BuildRepositories();
        var team = SeedTeam(teams, approvalQuorum: 2);
        var sut = BuildSut(teams, users);

        var result = await sut.UpdateTeamAsync(
            ExistingTeamId,
            new UpdateTeamRequest { Name = "Riesgo Norte", ApprovalQuorum = quorum },
            CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, problem.StatusCode);

        Assert.Equal(0, teams.UpdateCount);
        Assert.Equal(2, team.ApprovalQuorum);
    }

    [Fact]
    public async Task CreateTeam_AcceptsAQuorumLargerThanTheManagerCount()
    {
        var (teams, users) = BuildRepositories();
        var sut = BuildSut(teams, users);

        // AnalysisApprovalQuorumService applies MIN(quorum, eligible managers), so this team simply
        // settles on two signatures. Refusing it here would make the valid range depend on a manager
        // set that changes independently — deactivate one and a saved team becomes unsaveable.
        var result = await sut.CreateTeamAsync(
            new CreateTeamRequest
            {
                Name           = "Riesgo Norte",
                ManagerUserIds = [ManagerA, ManagerB],
                ApprovalQuorum = 3
            },
            CancellationToken.None);

        Assert.IsType<CreatedResult>(result);
        Assert.Equal(3, teams.Created!.ApprovalQuorum);
        Assert.Equal(3, DtoOf(result).ApprovalQuorum);
    }

    [Fact]
    public async Task CreateTeam_AcceptsAQuorumWithNoManagersAtAll()
    {
        var (teams, users) = BuildRepositories();
        var sut = BuildSut(teams, users);

        var result = await sut.CreateTeamAsync(
            new CreateTeamRequest { Name = "Riesgo Norte", ManagerUserIds = [], ApprovalQuorum = 2 },
            CancellationToken.None);

        Assert.IsType<CreatedResult>(result);
        Assert.Equal(2, teams.Created!.ApprovalQuorum);
    }

    [Fact]
    public async Task CreateTeam_WhenQuorumIsOmitted_DefaultsToOne()
    {
        var (teams, users) = BuildRepositories();
        var sut = BuildSut(teams, users);

        var result = await sut.CreateTeamAsync(
            new CreateTeamRequest { Name = "Riesgo Norte", ManagerUserIds = [ManagerA] },
            CancellationToken.None);

        Assert.IsType<CreatedResult>(result);
        Assert.Equal(1, teams.Created!.ApprovalQuorum);
        Assert.Equal(1, DtoOf(result).ApprovalQuorum);
    }

    [Fact]
    public async Task UpdateTeam_WhenQuorumIsOmitted_PreservesTheStoredValue()
    {
        var (teams, users) = BuildRepositories();
        var team = SeedTeam(teams, approvalQuorum: 2, managerUserId: ManagerA);
        var sut = BuildSut(teams, users);

        // THE REGRESSION THIS SUITE EXISTS FOR. A Portal build that predates the quorum field sends
        // a plain rename with no approvalQuorum; a strict full replace would reset the DBA's 2 to 1
        // and quietly halve the signatures a credit decision needs.
        var result = await sut.UpdateTeamAsync(
            ExistingTeamId,
            new UpdateTeamRequest { Name = "Riesgo Sur", ManagerUserIds = [ManagerA] },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(2, team.ApprovalQuorum);
        Assert.Equal(2, teams.Updated!.ApprovalQuorum);
        Assert.Equal(2, DtoOf(result).ApprovalQuorum);
        Assert.Equal("Riesgo Sur", team.Name);
    }

    [Fact]
    public async Task UpdateTeam_WhenQuorumIsSupplied_ReplacesTheStoredValue()
    {
        var (teams, users) = BuildRepositories();
        var team = SeedTeam(teams, approvalQuorum: 3, managerUserId: ManagerA);
        var sut = BuildSut(teams, users);

        var result = await sut.UpdateTeamAsync(
            ExistingTeamId,
            new UpdateTeamRequest
            {
                Name           = "Riesgo Norte",
                ManagerUserIds = [ManagerA],
                ApprovalQuorum = 1
            },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, team.ApprovalQuorum);
    }

    // ── Backward compatibility with the singular ManagerUserId ────────────────

    [Fact]
    public async Task CreateTeam_WithOnlyTheLegacySingularManager_StillPersistsAOneElementSet()
    {
        var (teams, users) = BuildRepositories();
        var sut = BuildSut(teams, users);

        // An un-updated Portal build sends exactly this. It must keep working for one release.
        var result = await sut.CreateTeamAsync(
            new CreateTeamRequest { Name = "Riesgo Norte", ManagerUserId = ManagerA },
            CancellationToken.None);

        Assert.IsType<CreatedResult>(result);
        Assert.Equal([ManagerA], Assert.Single(teams.ManagerWrites).UserIds);
        Assert.Equal(ManagerA, teams.Created!.ManagerUserId);
    }

    [Fact]
    public async Task CreateTeam_WithNeitherManagerField_PersistsAnEmptySet()
    {
        var (teams, users) = BuildRepositories();
        var sut = BuildSut(teams, users);

        var result = await sut.CreateTeamAsync(
            new CreateTeamRequest { Name = "Riesgo Norte" },
            CancellationToken.None);

        Assert.IsType<CreatedResult>(result);
        Assert.Empty(Assert.Single(teams.ManagerWrites).UserIds);
        Assert.Null(teams.Created!.ManagerUserId);
    }

    [Fact]
    public async Task CreateTeam_WhenBothManagerFieldsAreSent_TheArrayWins()
    {
        var (teams, users) = BuildRepositories();
        var sut = BuildSut(teams, users);

        var result = await sut.CreateTeamAsync(
            new CreateTeamRequest
            {
                Name           = "Riesgo Norte",
                ManagerUserId  = ManagerC,
                ManagerUserIds = [ManagerA, ManagerB]
            },
            CancellationToken.None);

        Assert.IsType<CreatedResult>(result);
        Assert.Equal([ManagerA, ManagerB], Assert.Single(teams.ManagerWrites).UserIds);
    }

    [Fact]
    public async Task UpdateTeam_WithAnEmptyArrayAndALegacyManager_ClearsTheSet()
    {
        var (teams, users) = BuildRepositories();
        SeedTeam(teams, managerUserId: ManagerC);
        var sut = BuildSut(teams, users);

        // An EMPTY array is "present", so it wins over the singular field. This is the only way the
        // Portal can express "remove every manager", and reading it as "fall back to managerUserId"
        // would make that impossible.
        var result = await sut.UpdateTeamAsync(
            ExistingTeamId,
            new UpdateTeamRequest
            {
                Name           = "Riesgo Norte",
                ManagerUserId  = ManagerC,
                ManagerUserIds = []
            },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Empty(Assert.Single(teams.ManagerWrites).UserIds);
        Assert.Null(teams.Updated!.ManagerUserId);
    }

    // ── The deprecated Teams.ManagerUserId mirror ─────────────────────────────

    [Fact]
    public async Task CreateTeam_WritesTheFirstManagerIntoTheLegacyColumn()
    {
        var (teams, users) = BuildRepositories();
        var sut = BuildSut(teams, users);

        var result = await sut.CreateTeamAsync(
            new CreateTeamRequest
            {
                Name           = "Riesgo Norte",
                ManagerUserIds = [ManagerB, ManagerA, ManagerC]
            },
            CancellationToken.None);

        Assert.IsType<CreatedResult>(result);

        // The union read in TeamManagerResolution needs SOMEBODY here on a tenant whose DDL 066 data
        // migration never ran; the whole set lives in [dbo].[TeamManagers].
        Assert.Equal(ManagerB, teams.Created!.ManagerUserId);
        Assert.Equal(ManagerB, DtoOf(result).ManagerUserId);
        Assert.Equal("Bruno Manager", DtoOf(result).ManagerName);
    }

    [Fact]
    public async Task UpdateTeam_WithAnEmptySet_NullsTheLegacyColumn()
    {
        var (teams, users) = BuildRepositories();
        SeedTeam(teams, managerUserId: ManagerA);
        var sut = BuildSut(teams, users);

        var result = await sut.UpdateTeamAsync(
            ExistingTeamId,
            new UpdateTeamRequest { Name = "Riesgo Norte", ManagerUserIds = [] },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Null(teams.Updated!.ManagerUserId);
        Assert.Null(DtoOf(result).ManagerUserId);
        Assert.Empty(DtoOf(result).Managers);
    }

    // ── The response ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateTeam_ResolvesEveryManagerName()
    {
        var (teams, users) = BuildRepositories();
        var sut = BuildSut(teams, users);

        var result = await sut.CreateTeamAsync(
            new CreateTeamRequest
            {
                Name           = "Riesgo Norte",
                ManagerUserIds = [ManagerA, ManagerC]
            },
            CancellationToken.None);

        var managers = DtoOf(result).Managers;

        Assert.Collection(managers,
            first =>
            {
                Assert.Equal(ManagerA, first.UserId);
                Assert.Equal("Ana Manager", first.DisplayName);
            },
            second =>
            {
                Assert.Equal(ManagerC, second.UserId);
                Assert.Equal("Carla Manager", second.DisplayName);
            });
    }

    // ── The LIST endpoint ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetTeams_ReturnsEveryManagerOfEveryTeam_WithoutAQueryPerTeam()
    {
        var (teams, users) = BuildRepositories();

        var secondTeamId = Guid.Parse("88888888-8888-8888-8888-888888888888");
        var thirdTeamId  = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        foreach (var (teamId, name) in new[]
                 {
                     (ExistingTeamId, "A Norte"), (secondTeamId, "B Sur"), (thirdTeamId, "C Centro")
                 })
        {
            teams.Existing.Add(new Team
            {
                TeamId = teamId, TenantId = Tenant, Name = name, ApprovalQuorum = 2
            });
        }

        teams.ManagerRows[ExistingTeamId] = [ManagerA, ManagerB];
        teams.ManagerRows[secondTeamId]   = [ManagerC];
        // The third team has no rows at all — it must come back with an empty manager list.

        var sut = BuildSut(teams, users);

        var result = Assert.IsType<OkObjectResult>(await sut.GetTeamsAsync(CancellationToken.None));
        var dtos = Assert.IsAssignableFrom<IReadOnlyList<TeamDto>>(result.Value);

        Assert.Equal(3, dtos.Count);
        Assert.Equal([ManagerA, ManagerB], [.. dtos[0].Managers.Select(m => m.UserId)]);
        Assert.Equal([ManagerC], [.. dtos[1].Managers.Select(m => m.UserId)]);
        Assert.Empty(dtos[2].Managers);
        Assert.All(dtos, dto => Assert.Equal(2, dto.ApprovalQuorum));

        // THE N+1 GUARD. Two reads — the teams and the tenant's whole manager set — for three teams.
        // A per-team GetManagerUserIdsAsync would throw from the stub anyway; this pins the count so a
        // batch call placed inside the loop still fails.
        Assert.Equal(2, teams.ReadCallCount);
    }

    [Fact]
    public async Task GetTeams_UnionsTheLegacyColumnForATenantWhoseDataMigrationNeverRan()
    {
        var (teams, users) = BuildRepositories();

        // DDL 066 is a DATA script and --verify-schema compares columns, not rows, so a tenant that
        // skipped it still has its manager only in Teams.ManagerUserId. The list has to show them.
        teams.Existing.Add(new Team
        {
            TeamId = ExistingTeamId, TenantId = Tenant, Name = "Riesgo Norte",
            ManagerUserId = ManagerA, ApprovalQuorum = 1
        });

        var sut = BuildSut(teams, users);

        var result = Assert.IsType<OkObjectResult>(await sut.GetTeamsAsync(CancellationToken.None));
        var dto = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<TeamDto>>(result.Value));

        Assert.Equal([ManagerA], [.. dto.Managers.Select(m => m.UserId)]);
        Assert.Equal(ManagerA, dto.ManagerUserId);
    }

    [Fact]
    public async Task GetTeams_DoesNotDuplicateAManagerHeldInBothTheTableAndTheLegacyColumn()
    {
        var (teams, users) = BuildRepositories();

        // The state DDL 066 deliberately creates: the row is migrated AND the column is retained.
        teams.Existing.Add(new Team
        {
            TeamId = ExistingTeamId, TenantId = Tenant, Name = "Riesgo Norte",
            ManagerUserId = ManagerA, ApprovalQuorum = 1
        });
        teams.ManagerRows[ExistingTeamId] = [ManagerA, ManagerB];

        var sut = BuildSut(teams, users);

        var result = Assert.IsType<OkObjectResult>(await sut.GetTeamsAsync(CancellationToken.None));
        var dto = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<TeamDto>>(result.Value));

        Assert.Equal([ManagerA, ManagerB], [.. dto.Managers.Select(m => m.UserId)]);
    }

    [Fact]
    public async Task GetTeams_KeepsAManagerDeactivatedAfterBeingNamed_WithNoDisplayName()
    {
        var (teams, users) = BuildRepositories();

        teams.Existing.Add(new Team
        {
            TeamId = ExistingTeamId, TenantId = Tenant, Name = "Riesgo Norte", ApprovalQuorum = 1
        });
        teams.ManagerRows[ExistingTeamId] = [ManagerA, Deactivated];

        var sut = BuildSut(teams, users);

        var result = Assert.IsType<OkObjectResult>(await sut.GetTeamsAsync(CancellationToken.None));
        var dto = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<TeamDto>>(result.Value));

        // The id stays — it is what the row stores, and an Admin has to see it in order to remove it
        // — but the name cannot be resolved from a roster that holds active users only.
        Assert.Collection(dto.Managers,
            first =>
            {
                Assert.Equal(ManagerA, first.UserId);
                Assert.Equal("Ana Manager", first.DisplayName);
            },
            second =>
            {
                Assert.Equal(Deactivated, second.UserId);
                Assert.Null(second.DisplayName);
            });
    }
}
