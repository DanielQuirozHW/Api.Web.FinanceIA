// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-16

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// The quorum engine — how many signatures a pre-approval needs and when it has them.
///
/// <para>
/// This is the suite that carries the feature's safety. A mistake here is not a wrong screen: it is
/// either a credit line committed on fewer signatures than the tenant required, or a correct
/// approval that never commits because the arithmetic asked for signatures nobody could give.
/// </para>
/// </summary>
/// <remarks>
/// The VOTE STORE IS REAL — <see cref="AnalysisApprovalVoteRepository"/> over the EF Core in-memory
/// provider — because the rules under test are about which rows exist and which of them count, and a
/// stubbed store would let the test assert against its own idea of that. The other three
/// dependencies are hand-rolled stubs: <see cref="IAnalysisRepository"/> because the crossing write
/// is a conditional <c>ExecuteUpdateAsync</c> the in-memory provider does not implement (the same
/// reason <c>FiadorRepositoryTests</c> gives for skipping <c>SetStatusAsync</c>), and the team and
/// user repositories because the team shapes under test are faster to state than to seed.
///
/// <para>
/// NOT covered here, deliberately: the unique constraint's own race. The in-memory provider does not
/// enforce unique indexes, so a duplicate never throws and the catch in <c>RecordAsync</c> cannot be
/// reached. What IS covered is the idempotency an operator actually sees — the same manager voting
/// twice — which the repository's pre-read answers and which is asserted below.
/// </para>
/// </remarks>
public sealed class AnalysisApprovalQuorumServiceTests
{
    private const string Tenant = "tenant-1";
    private const string Other  = "tenant-2";

    private static readonly Guid TeamId    = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AnalystId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ManagerA  = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ManagerB  = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid ManagerC  = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid OutsiderId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    // ── Stubs ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Records the crossing write instead of performing it. <see cref="Crosses"/> false models the
    /// loser of a simultaneous crossing — the guarded UPDATE matching zero rows.
    /// </summary>
    private sealed class StubAnalysisRepository : IAnalysisRepository
    {
        public Task<BalanceBaseFiguresProjection?> GetBalanceBaseFiguresAsync(Guid analysisId, string tenantId, CancellationToken ct)
            => Task.FromResult<BalanceBaseFiguresProjection?>(null);

        public Task<IReadOnlyDictionary<Guid, EntityLatestScoreProjection>> GetLatestScoreByEntityIdsAsync(
            IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public bool      Crosses      { get; set; } = true;
        public int       ConfirmCalls { get; private set; }
        public Analysis? Settled      { get; set; }

        public Guid?     ConfirmedBy      { get; private set; }
        public decimal?  ConfirmedAmount  { get; private set; }
        public string?   ConfirmedJson    { get; private set; }

        public Task<bool> TryConfirmFromPreApprovedAsync(
            Guid analysisId, string tenantId, Guid reviewedBy, DateTime reviewedAt,
            decimal? decisionAmount, string? decisionCurrency, string? confirmedOperationsJson,
            string? reviewNotes, CancellationToken ct)
        {
            ConfirmCalls++;
            ConfirmedBy     = reviewedBy;
            ConfirmedAmount = decisionAmount;
            ConfirmedJson   = confirmedOperationsJson;
            return Task.FromResult(Crosses);
        }

        public Task<Analysis?> GetByIdAsync(Guid analysisId, CancellationToken ct)
            => Task.FromResult(Settled);

        public Task<Analysis> CreateAsync(Analysis analysis, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Analysis> UpdateAsync(Analysis analysis, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<Analysis>> GetByIdsAsync(IEnumerable<Guid> analysisIds, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Analysis?> GetLatestByDocumentIdAsync(Guid documentId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, string>> GetLatestStatusByEntityIdsAsync(
            IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<int> CountByTenantInPeriodAsync(
            string tenantId, DateTime periodStartUtc, DateTime periodEndUtc, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubTeamRepository : ITeamRepository
    {
        public Team?      TeamToReturn     { get; set; }
        public List<Guid> ManagerUserIds   { get; } = [];

        public Task<Team?> GetByIdAsync(Guid teamId, string tenantId, CancellationToken ct)
            => Task.FromResult(
                TeamToReturn is not null && TeamToReturn.TenantId == tenantId ? TeamToReturn : null);

        public Task<IReadOnlyList<Guid>> GetManagerUserIdsAsync(Guid teamId, string tenantId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Guid>>(
                TeamToReturn?.TenantId == tenantId ? ManagerUserIds : []);

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> GetManagerUserIdsByTenantAsync(
            string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<Team>> GetByTenantAsync(string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task CreateAsync(Team team, CancellationToken ct)
            => throw new NotImplementedException();
        public Task UpdateAsync(Team team, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<bool> DeleteAsync(Guid teamId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task ReplaceManagersAsync(
            Guid teamId, string tenantId, IReadOnlyCollection<Guid> userIds, Guid? createdBy, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubUserRepository : IUserRepository
    {
        /// <summary>Every user the tenant knows, active or not, keyed by id.</summary>
        public Dictionary<Guid, User> All { get; } = new();

        public Task<User?> GetByUserIdAsync(Guid userId, CancellationToken ct)
        {
            All.TryGetValue(userId, out var user);
            return Task.FromResult<User?>(user);
        }

        // Mirrors the real query: Users.IsActive AND Users.TenantId. Those two ARE the definition of
        // "active" on this path — there is no flag anywhere else — so the stub must not widen it.
        public Task<IReadOnlyList<User>> GetActiveByTenantAsync(string tenantId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<User>>(
                All.Values.Where(u => u.IsActive && u.TenantId == tenantId).ToList());

        public Task<User?> GetByFirebaseUidAsync(string firebaseUid, CancellationToken ct) => throw new NotImplementedException();
        public Task<User?> GetByEmailAsync(string email, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> ExistsByFirebaseUidAsync(string firebaseUid, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<User>> GetByIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct) => throw new NotImplementedException();
        public Task<User> CreateAsync(User user, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateLastLoginAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateOnboardingFlagsAsync(Guid userId, string? flagsJson, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateTeamAsync(Guid userId, Guid? teamId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> ClearTeamAsync(Guid teamId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private sealed class Harness : IAsyncDisposable
    {
        public required FinanciaDbContext              Context  { get; init; }
        public required StubAnalysisRepository         Analyses { get; init; }
        public required StubTeamRepository             Teams    { get; init; }
        public required StubUserRepository             Users    { get; init; }
        public required AnalysisApprovalQuorumService  Sut      { get; init; }

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    private static Harness BuildHarness(string dbName)
    {
        var context = new FinanciaDbContext(
            new DbContextOptionsBuilder<FinanciaDbContext>().UseInMemoryDatabase(dbName).Options);

        var votes    = new AnalysisApprovalVoteRepository(context, NullLogger<AnalysisApprovalVoteRepository>.Instance);
        var analyses = new StubAnalysisRepository();
        var teams    = new StubTeamRepository();
        var users    = new StubUserRepository();

        return new Harness
        {
            Context  = context,
            Analyses = analyses,
            Teams    = teams,
            Users    = users,
            Sut      = new AnalysisApprovalQuorumService(
                votes, analyses, teams, users,
                NullLogger<AnalysisApprovalQuorumService>.Instance)
        };
    }

    private static User MakeUser(Guid id, string role, bool isActive = true, Guid? teamId = null, string tenant = Tenant)
        => new()
        {
            UserId      = id,
            TenantId    = tenant,
            Role        = role,
            IsActive    = isActive,
            TeamId      = teamId,
            DisplayName = role,
        };

    private static Analysis PreApproved()
        => new()
        {
            AnalysisId    = Guid.NewGuid(),
            TenantId      = Tenant,
            EntityId      = Guid.NewGuid(),
            Status        = AnalysisStatuses.PreApproved,
            PreApprovedBy = AnalystId,
        };

    /// <summary>
    /// A tenant whose analyst belongs to a team with the given quorum and the given managers.
    /// Managers default to active RiskManagers; a test that wants otherwise states it.
    /// </summary>
    private static void SeedTeam(
        Harness harness, int quorum, params User[] managers)
    {
        harness.Teams.TeamToReturn = new Team
        {
            TeamId         = TeamId,
            TenantId       = Tenant,
            Name           = "Riesgo",
            ApprovalQuorum = quorum,
        };
        harness.Teams.ManagerUserIds.AddRange(managers.Select(m => m.UserId));

        harness.Users.All[AnalystId] = MakeUser(AnalystId, Roles.Analyst, teamId: TeamId);
        foreach (var manager in managers) harness.Users.All[manager.UserId] = manager;
    }

    private static Task<(ApprovalVoteOutcome Outcome, ApprovalTally Tally)> VoteAsync(
        Harness harness, Analysis analysis, Guid voterId)
        => harness.Sut.VoteAndTryConfirmAsync(
            Tenant, analysis, harness.Users.All[voterId],
            decisionAmount: 750_000m, decisionCurrency: "ARS",
            confirmedOperationsJson: null, reviewNotes: null,
            nowUtc: DateTime.UtcNow, ct: CancellationToken.None);

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Quorum2_TwoDistinctManagers_ConfirmsOnTheSecondAndNotTheFirst()
    {
        // The headline. If the FIRST vote confirmed, the feature would not exist; if the SECOND did
        // not, a correctly configured team could never commit a credit line at all.
        await using var h = BuildHarness(nameof(Quorum2_TwoDistinctManagers_ConfirmsOnTheSecondAndNotTheFirst));
        SeedTeam(h, 2, MakeUser(ManagerA, Roles.RiskManager), MakeUser(ManagerB, Roles.RiskManager));
        var analysis = PreApproved();

        var (firstOutcome, firstTally) = await VoteAsync(h, analysis, ManagerA);

        Assert.Equal(ApprovalVoteOutcome.Pending, firstOutcome);
        Assert.Equal(1, firstTally.Approved);
        Assert.Equal(2, firstTally.Required);
        Assert.True(firstTally.InForce);
        Assert.Equal(0, h.Analyses.ConfirmCalls);
        // The analysis row is untouched: "stays PreApproved" has to be literally true, or the
        // Portal would show a status the quorum has not earned.
        Assert.Equal(AnalysisStatuses.PreApproved, analysis.Status);
        Assert.Null(analysis.ReviewedBy);

        var (secondOutcome, secondTally) = await VoteAsync(h, analysis, ManagerB);

        Assert.Equal(ApprovalVoteOutcome.Confirmed, secondOutcome);
        Assert.Equal(2, secondTally.Approved);
        Assert.Equal(1, h.Analyses.ConfirmCalls);
        Assert.Equal(ManagerB, h.Analyses.ConfirmedBy);
        Assert.Equal(750_000m, h.Analyses.ConfirmedAmount);
        // Mirrored onto the entity, because the guarded UPDATE bypasses the change tracker and the
        // caller builds its response, audit row and notification from this object.
        Assert.Equal(AnalysisStatuses.Approved, analysis.Status);
        Assert.Equal(ManagerB, analysis.ReviewedBy);
    }

    [Fact]
    public async Task Quorum2_SameManagerTwice_IsStillOneSignature()
    {
        // A second click, a retried request, a second browser tab. One manager is one signature, or
        // the quorum is a formality anybody can satisfy alone.
        await using var h = BuildHarness(nameof(Quorum2_SameManagerTwice_IsStillOneSignature));
        SeedTeam(h, 2, MakeUser(ManagerA, Roles.RiskManager), MakeUser(ManagerB, Roles.RiskManager));
        var analysis = PreApproved();

        await VoteAsync(h, analysis, ManagerA);
        var (outcome, tally) = await VoteAsync(h, analysis, ManagerA);

        Assert.Equal(ApprovalVoteOutcome.Pending, outcome);
        Assert.Equal(1, tally.Approved);
        Assert.Equal(2, tally.Required);
        Assert.Equal(0, h.Analyses.ConfirmCalls);
        Assert.Equal(AnalysisStatuses.PreApproved, analysis.Status);
    }

    [Fact]
    public async Task Quorum3_WithOnlyTwoEligibleManagers_ConfirmsOnTwo()
    {
        // "Never blocks": a quorum larger than the team is capped by the team, so a misconfigured
        // number cannot strand every analysis that team pre-approves.
        await using var h = BuildHarness(nameof(Quorum3_WithOnlyTwoEligibleManagers_ConfirmsOnTwo));
        SeedTeam(h, 3, MakeUser(ManagerA, Roles.RiskManager), MakeUser(ManagerB, Roles.RiskManager));
        var analysis = PreApproved();

        var (_, firstTally) = await VoteAsync(h, analysis, ManagerA);
        Assert.Equal(2, firstTally.Required);

        var (outcome, tally) = await VoteAsync(h, analysis, ManagerB);

        Assert.Equal(ApprovalVoteOutcome.Confirmed, outcome);
        Assert.Equal(2, tally.Required);
        Assert.Equal(2, tally.Approved);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task QuorumOfZeroOrLess_IsFlooredToOne(int stored)
    {
        // DDL 065 shipped NO CHECK constraint, so a DBA can store these. Without the floor the
        // arithmetic would read "0 signatures are enough" and confirm on nobody's approval.
        await using var h = BuildHarness($"{nameof(QuorumOfZeroOrLess_IsFlooredToOne)}-{stored}");
        SeedTeam(h, stored, MakeUser(ManagerA, Roles.RiskManager), MakeUser(ManagerB, Roles.RiskManager));
        var analysis = PreApproved();

        var (outcome, tally) = await VoteAsync(h, analysis, ManagerA);

        Assert.Equal(1, tally.Required);
        Assert.Equal(ApprovalVoteOutcome.Confirmed, outcome);
    }

    [Fact]
    public async Task VoteByANowInactiveManager_DoesNotCountTowardQuorum()
    {
        // The signature stays on the record — an auditor asks for it — but it stops being one of the
        // signatures the credit rests on the moment the account is deactivated.
        await using var h = BuildHarness(nameof(VoteByANowInactiveManager_DoesNotCountTowardQuorum));
        SeedTeam(h, 2,
            MakeUser(ManagerA, Roles.RiskManager),
            MakeUser(ManagerB, Roles.RiskManager),
            MakeUser(ManagerC, Roles.RiskManager));
        var analysis = PreApproved();

        await VoteAsync(h, analysis, ManagerA);

        // A deactivates. Their row is untouched; only the roster changes.
        h.Users.All[ManagerA] = MakeUser(ManagerA, Roles.RiskManager, isActive: false);

        var (outcome, tally) = await VoteAsync(h, analysis, ManagerB);

        Assert.Equal(ApprovalVoteOutcome.Pending, outcome);
        Assert.Equal(1, tally.Approved);
        Assert.DoesNotContain(ManagerA, tally.ApproverUserIds);
        // The vote row itself survived — this is a tally rule, not a delete.
        Assert.Equal(2, await h.Context.AnalysisApprovalVotes.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ManagerWhoCannotConfirm_DoesNotRaiseTheQuorum()
    {
        // [dbo].[TeamManagers] is not role-filtered: a team may name an Analyst. Counting one would
        // set a quorum of 2 that only one person can ever reach, stranding the analysis at 1 de 2.
        await using var h = BuildHarness(nameof(ManagerWhoCannotConfirm_DoesNotRaiseTheQuorum));
        SeedTeam(h, 2,
            MakeUser(ManagerA, Roles.RiskManager),
            MakeUser(ManagerB, Roles.Analyst));
        var analysis = PreApproved();

        var (outcome, tally) = await VoteAsync(h, analysis, ManagerA);

        Assert.Equal(1, tally.Required);
        Assert.Equal(ApprovalVoteOutcome.Confirmed, outcome);
    }

    [Fact]
    public async Task AnyActiveConfirmer_CountsTowardTheQuorum()
    {
        // The team decides HOW MANY signatures, not WHOSE. An Admin or a RiskManager from another
        // team is a legitimate confirmer today, and their approval has to be worth something — but
        // it must be worth ONE signature, not a bypass of the whole quorum.
        await using var h = BuildHarness(nameof(AnyActiveConfirmer_CountsTowardTheQuorum));
        SeedTeam(h, 2, MakeUser(ManagerA, Roles.RiskManager), MakeUser(ManagerB, Roles.RiskManager));
        h.Users.All[OutsiderId] = MakeUser(OutsiderId, Roles.Admin);
        var analysis = PreApproved();

        var (firstOutcome, firstTally) = await VoteAsync(h, analysis, OutsiderId);

        // One signature, not a settlement: the quorum still binds the outsider.
        Assert.Equal(ApprovalVoteOutcome.Pending, firstOutcome);
        Assert.Equal(1, firstTally.Approved);
        Assert.Equal(2, firstTally.Required);

        var (outcome, _) = await VoteAsync(h, analysis, ManagerA);
        Assert.Equal(ApprovalVoteOutcome.Confirmed, outcome);
    }

    [Fact]
    public async Task WithdrawDropsTheTally_AndReApprovingCountsOnce()
    {
        await using var h = BuildHarness(nameof(WithdrawDropsTheTally_AndReApprovingCountsOnce));
        SeedTeam(h, 2, MakeUser(ManagerA, Roles.RiskManager), MakeUser(ManagerB, Roles.RiskManager));
        var analysis = PreApproved();

        await VoteAsync(h, analysis, ManagerA);

        var (withdrawn, afterWithdraw) = await h.Sut.WithdrawAsync(
            Tenant, analysis, h.Users.All[ManagerA], CancellationToken.None);

        Assert.True(withdrawn);
        Assert.Equal(0, afterWithdraw.Approved);

        // Re-approving must restore exactly one signature, not leave a stale row behind that would
        // make the same person count twice.
        var (outcome, tally) = await VoteAsync(h, analysis, ManagerA);

        Assert.Equal(ApprovalVoteOutcome.Pending, outcome);
        Assert.Equal(1, tally.Approved);
        Assert.Equal(1, await h.Context.AnalysisApprovalVotes.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Withdraw_WithNothingOnRecord_IsANoOp()
    {
        await using var h = BuildHarness(nameof(Withdraw_WithNothingOnRecord_IsANoOp));
        SeedTeam(h, 2, MakeUser(ManagerA, Roles.RiskManager), MakeUser(ManagerB, Roles.RiskManager));

        var (withdrawn, tally) = await h.Sut.WithdrawAsync(
            Tenant, PreApproved(), h.Users.All[ManagerA], CancellationToken.None);

        Assert.False(withdrawn);
        Assert.Equal(0, tally.Approved);
    }

    [Fact]
    public async Task DiscardVotes_ResetsTheTallyToZero()
    {
        // Returning an analysis for rework starts the vote over: the signatures were given for a
        // proposal that no longer exists.
        await using var h = BuildHarness(nameof(DiscardVotes_ResetsTheTallyToZero));
        SeedTeam(h, 2, MakeUser(ManagerA, Roles.RiskManager), MakeUser(ManagerB, Roles.RiskManager));
        var analysis = PreApproved();

        await VoteAsync(h, analysis, ManagerA);

        await h.Sut.DiscardVotesAsync(Tenant, analysis.AnalysisId, CancellationToken.None);

        var tally = await h.Sut.GetTallyAsync(Tenant, analysis, CancellationToken.None);
        Assert.Equal(0, tally.Approved);
        Assert.Empty(await h.Context.AnalysisApprovalVotes.ToListAsync(CancellationToken.None));

        // And voting starts from zero, not from where it left off.
        var (outcome, afterReset) = await VoteAsync(h, analysis, ManagerA);
        Assert.Equal(ApprovalVoteOutcome.Pending, outcome);
        Assert.Equal(1, afterReset.Approved);
    }

    [Fact]
    public async Task NoTeam_SingleConfirmerConfirms()
    {
        // The no-regression case. A tenant that has not adopted teams must behave EXACTLY as it did
        // before this feature existed, and must show no tally at all rather than a bare "0 de 1".
        await using var h = BuildHarness(nameof(NoTeam_SingleConfirmerConfirms));
        h.Users.All[AnalystId] = MakeUser(AnalystId, Roles.Analyst, teamId: null);
        h.Users.All[ManagerA]  = MakeUser(ManagerA, Roles.RiskManager);
        var analysis = PreApproved();

        var (outcome, tally) = await VoteAsync(h, analysis, ManagerA);

        Assert.Equal(ApprovalVoteOutcome.Confirmed, outcome);
        Assert.Equal(1, tally.Required);
        Assert.False(tally.InForce);
    }

    [Fact]
    public async Task TeamWithNoActiveManagers_SingleConfirmerConfirms()
    {
        await using var h = BuildHarness(nameof(TeamWithNoActiveManagers_SingleConfirmerConfirms));
        SeedTeam(h, 2, MakeUser(ManagerA, Roles.RiskManager, isActive: false));
        h.Users.All[ManagerB] = MakeUser(ManagerB, Roles.RiskManager);
        var analysis = PreApproved();

        var (outcome, tally) = await VoteAsync(h, analysis, ManagerB);

        Assert.Equal(ApprovalVoteOutcome.Confirmed, outcome);
        Assert.False(tally.InForce);
    }

    [Fact]
    public async Task AnalysisWithNoPreApprover_FallsBackToASingleSignature()
    {
        // A data anomaly rather than a normal state, but it must degrade to today's behaviour rather
        // than throw on a credit decision.
        await using var h = BuildHarness(nameof(AnalysisWithNoPreApprover_FallsBackToASingleSignature));
        SeedTeam(h, 3, MakeUser(ManagerA, Roles.RiskManager), MakeUser(ManagerB, Roles.RiskManager));
        var analysis = PreApproved();
        analysis.PreApprovedBy = null;

        var (outcome, tally) = await VoteAsync(h, analysis, ManagerA);

        Assert.Equal(ApprovalVoteOutcome.Confirmed, outcome);
        Assert.Equal(1, tally.Required);
        Assert.False(tally.InForce);
    }

    [Fact]
    public async Task PreApproverFromAnotherTenant_IsNotRead()
    {
        // GetByUserIdAsync is keyed by a globally unique id and carries no tenant predicate, so the
        // scope check is the service's. Without it another tenant's team would size this quorum.
        await using var h = BuildHarness(nameof(PreApproverFromAnotherTenant_IsNotRead));
        h.Users.All[AnalystId] = MakeUser(AnalystId, Roles.Analyst, teamId: TeamId, tenant: Other);
        h.Users.All[ManagerA]  = MakeUser(ManagerA, Roles.RiskManager);
        h.Teams.TeamToReturn   = new Team { TeamId = TeamId, TenantId = Other, Name = "Otro", ApprovalQuorum = 5 };
        var analysis = PreApproved();

        var (outcome, tally) = await VoteAsync(h, analysis, ManagerA);

        Assert.Equal(ApprovalVoteOutcome.Confirmed, outcome);
        Assert.Equal(1, tally.Required);
        Assert.False(tally.InForce);
    }

    [Fact]
    public async Task WhenAnotherManagerCrossedFirst_TheLoserReportsAlreadyConfirmed()
    {
        // The guarded UPDATE matched zero rows: somebody else's signature settled it in between.
        // The loser must NOT claim the confirmation — the audit row and the analyst's notification
        // belong to the request that actually settled it — and must report the true state.
        await using var h = BuildHarness(nameof(WhenAnotherManagerCrossedFirst_TheLoserReportsAlreadyConfirmed));
        SeedTeam(h, 1, MakeUser(ManagerA, Roles.RiskManager));
        var analysis = PreApproved();

        h.Analyses.Crosses = false;
        h.Analyses.Settled = new Analysis
        {
            AnalysisId     = analysis.AnalysisId,
            TenantId       = Tenant,
            EntityId       = analysis.EntityId,
            Status         = AnalysisStatuses.Approved,
            ReviewedBy     = ManagerB,
            DecisionAmount = 900_000m,
        };

        var (outcome, _) = await VoteAsync(h, analysis, ManagerA);

        Assert.Equal(ApprovalVoteOutcome.AlreadyConfirmed, outcome);
        // Refreshed from the row that won, not from this request's intentions.
        Assert.Equal(AnalysisStatuses.Approved, analysis.Status);
        Assert.Equal(ManagerB, analysis.ReviewedBy);
        Assert.Equal(900_000m, analysis.DecisionAmount);
    }

    [Fact]
    public async Task Veto_IsRecordedAndLeavesTheStandingApprovalsAlone()
    {
        await using var h = BuildHarness(nameof(Veto_IsRecordedAndLeavesTheStandingApprovalsAlone));
        SeedTeam(h, 2, MakeUser(ManagerA, Roles.RiskManager), MakeUser(ManagerB, Roles.RiskManager));
        var analysis = PreApproved();

        await VoteAsync(h, analysis, ManagerA);
        await h.Sut.RecordVetoAsync(Tenant, analysis, h.Users.All[ManagerB], DateTime.UtcNow, CancellationToken.None);

        var stored = await h.Context.AnalysisApprovalVotes.ToListAsync(CancellationToken.None);
        Assert.Equal(2, stored.Count);
        Assert.Contains(stored, v => v.UserId == ManagerB && v.Vote == AnalysisVoteValues.Rejected);
        // The approval that was standing when the veto landed is still on the record: that pairing
        // is what an auditor reconstructs a refused credit from.
        Assert.Contains(stored, v => v.UserId == ManagerA && v.Vote == AnalysisVoteValues.Approved);
    }

    [Fact]
    public async Task Veto_ByAManagerWhoHadAlreadyApproved_ReplacesTheirApproval()
    {
        // One manager, one row — that is the unique constraint. Without replacing their own earlier
        // vote the veto would be refused as a duplicate and vanish, leaving the person who REFUSED
        // the credit recorded as having approved it. That is the worst possible trail.
        await using var h = BuildHarness(nameof(Veto_ByAManagerWhoHadAlreadyApproved_ReplacesTheirApproval));
        SeedTeam(h, 2, MakeUser(ManagerA, Roles.RiskManager), MakeUser(ManagerB, Roles.RiskManager));
        var analysis = PreApproved();

        await VoteAsync(h, analysis, ManagerA);
        await h.Sut.RecordVetoAsync(Tenant, analysis, h.Users.All[ManagerA], DateTime.UtcNow, CancellationToken.None);

        var stored = Assert.Single(await h.Context.AnalysisApprovalVotes.AsNoTracking().ToListAsync(CancellationToken.None));
        Assert.Equal(ManagerA, stored.UserId);
        Assert.Equal(AnalysisVoteValues.Rejected, stored.Vote);

        var tally = await h.Sut.GetTallyAsync(Tenant, analysis, CancellationToken.None);
        Assert.Equal(0, tally.Approved);
    }

    [Fact]
    public async Task RejectedVotes_NeverCountTowardTheQuorum()
    {
        await using var h = BuildHarness(nameof(RejectedVotes_NeverCountTowardTheQuorum));
        SeedTeam(h, 2, MakeUser(ManagerA, Roles.RiskManager), MakeUser(ManagerB, Roles.RiskManager));
        var analysis = PreApproved();

        await h.Sut.RecordVetoAsync(Tenant, analysis, h.Users.All[ManagerA], DateTime.UtcNow, CancellationToken.None);
        await h.Sut.RecordVetoAsync(Tenant, analysis, h.Users.All[ManagerB], DateTime.UtcNow, CancellationToken.None);

        var tally = await h.Sut.GetTallyAsync(Tenant, analysis, CancellationToken.None);

        Assert.Equal(0, tally.Approved);
    }
}
