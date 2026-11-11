// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-16

using System.Security.Claims;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Tests.Servicios.ScoreReliability;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// The CONTRACT between <see cref="AnalysisController"/> and the quorum engine — what the endpoint
/// does with each outcome, and the guards on retracting an approval.
/// </summary>
/// <remarks>
/// The engine's own arithmetic (the cap, the floor, the eligibility intersection, the idempotent
/// re-vote) is proven against real repositories in <c>Clases/AnalysisApprovalQuorumServiceTests</c>.
/// Here the engine is the scripted <see cref="StubAnalysisApprovalQuorumService"/>, so each test
/// fixes one outcome and asserts what the CONTROLLER then writes, audits, notifies and returns.
///
/// <para>
/// The sharpest assertions are negative. A vote below quorum must leave the analysis row untouched
/// and emit NO notification — telling the analyst their pre-approval was "confirmed" while it still
/// waits would be worse than telling them nothing — and it must never reach
/// <c>IAnalysisRepository.UpdateAsync</c>, whose unguarded full-entity save is precisely what the
/// engine's conditional write exists to avoid.
/// </para>
/// </remarks>
public sealed class AnalysisControllerQuorumTests
{
    private const string Tenant      = "tenant-1";
    private const string FirebaseUid = "uid-1";

    // ── Stubs ─────────────────────────────────────────────────────────────────
    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private sealed class StubAnalysisResultRepository : IAnalysisResultRepository
    {
        public AnalysisResult? ResultToReturn { get; set; }

        public Task<AnalysisResult?> GetByIdAsync(Guid resultId, string tenantId, CancellationToken ct)
            => Task.FromResult(ResultToReturn);

        public Task<IReadOnlyList<AnalysisResult>> GetAllByTenantAsync(string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<AnalysisResult?> GetByDocumentIdAsync(Guid documentId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<AnalysisResult> CreateAsync(AnalysisResult result, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<AnalysisResult>> GetByEntityIdAsync(Guid entityId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<AnalysisResult> UpdateAsync(AnalysisResult result, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, Guid>> GetResultIdsByAnalysisIdsAsync(IEnumerable<Guid> analysisIds, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<(IReadOnlyList<AnalysisListProjection> Items, int TotalCount)> GetListPageByTenantAsync(string tenantId, int skip, int take, CancellationToken ct) => throw new NotImplementedException();
        public Task<(IReadOnlyList<AnalysisListProjection> Items, int TotalCount)> GetListPageByEntityAsync(Guid entityId, string tenantId, int skip, int take, CancellationToken ct) => throw new NotImplementedException();
        public Task<AnalysisResultDetailProjection?> GetDetailByIdAsync(Guid resultId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<AnalysisContextProjection?> GetContextSnapshotByIdAsync(Guid resultId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<AnalysisBcraProjection?> GetBcraSnapshotByIdAsync(Guid resultId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<AnalysisRatiosProjection?> GetRatiosByIdAsync(Guid resultId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<AnalysisLabelProjection?> GetLabelByIdAsync(Guid resultId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class StubAnalysisRepository : IAnalysisRepository
    {
        public Task<BalanceBaseFiguresProjection?> GetBalanceBaseFiguresAsync(Guid analysisId, string tenantId, CancellationToken ct)
            => Task.FromResult<BalanceBaseFiguresProjection?>(null);

        public Task<IReadOnlyDictionary<Guid, EntityLatestScoreProjection>> GetLatestScoreByEntityIdsAsync(
            IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Analysis? AnalysisToReturn { get; set; }
        public bool      UpdateCalled     { get; private set; }

        public Task<Analysis?> GetByIdAsync(Guid analysisId, CancellationToken ct)
            => Task.FromResult(AnalysisToReturn);

        public Task<Analysis> UpdateAsync(Analysis analysis, CancellationToken ct)
        {
            UpdateCalled = true;
            return Task.FromResult(analysis);
        }

        // The guarded crossing write belongs to the engine. Reaching it from the controller would
        // mean the confirmation path went around the quorum entirely.
        public Task<bool> TryConfirmFromPreApprovedAsync(
            Guid analysisId, string tenantId, Guid reviewedBy, DateTime reviewedAt,
            decimal? decisionAmount, string? decisionCurrency, string? confirmedOperationsJson,
            string? reviewNotes, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<Analysis> CreateAsync(Analysis analysis, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Analysis>> GetByIdsAsync(IEnumerable<Guid> analysisIds, CancellationToken ct) => throw new NotImplementedException();
        public Task<Analysis?> GetLatestByDocumentIdAsync(Guid documentId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, string>> GetLatestStatusByEntityIdsAsync(IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> CountByTenantInPeriodAsync(string tenantId, DateTime periodStartUtc, DateTime periodEndUtc, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class StubEntityRepository : IEntityRepository
    {
        public Task<Entity?> GetByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => Task.FromResult<Entity?>(new Entity { EntityId = entityId, TenantId = Tenant, BusinessName = "ACME SA" });

        public Task<IReadOnlyList<Entity>> GetAllByTenantAsync(string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> ExistsByIdAsync(Guid entityId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<Entity?> GetByCuitAsync(string cuit, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<Entity> CreateAsync(Entity entity, CancellationToken ct) => throw new NotImplementedException();
        public Task<Entity> UpdateAsync(Entity entity, CancellationToken ct) => throw new NotImplementedException();
        public Task<(IReadOnlyList<EntityListProjection> Items, int TotalCount)> GetListPageByTenantAsync(string tenantId, int skip, int take, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, EntityNameProjection>> GetNameLookupByIdsAsync(IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<EntityDetailProjection?> GetDetailByIdAsync(Guid entityId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<EntitySnapshotProjection?> GetBcraSnapshotAsync(Guid entityId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<EntitySnapshotProjection?> GetCasfogSnapshotAsync(Guid entityId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<EntitySnapshotProjection?> GetLufeSnapshotAsync(Guid entityId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<EntitySnapshotProjection?> GetNosisSnapshotAsync(Guid entityId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<EntityNameProjection?> GetNameByIdAsync(Guid entityId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class StubUserRepository : IUserRepository
    {
        public User? CurrentUser { get; set; }

        public Task<User?> GetByFirebaseUidAsync(string firebaseUid, CancellationToken ct)
            => Task.FromResult(CurrentUser);

        public Task<IReadOnlyList<User>> GetByIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<User>>([]);

        public Task<User?> GetByUserIdAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<User?> GetByEmailAsync(string email, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> ExistsByFirebaseUidAsync(string firebaseUid, CancellationToken ct) => throw new NotImplementedException();
        public Task<User> CreateAsync(User user, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateLastLoginAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateOnboardingFlagsAsync(Guid userId, string? flagsJson, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<User>> GetActiveByTenantAsync(string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateTeamAsync(Guid userId, Guid? teamId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> ClearTeamAsync(Guid teamId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class StubCreditLineRepository : ICreditLineRepository
    {
        public IReadOnlyList<Guid> ActiveCupoIds { get; set; } = Array.Empty<Guid>();

        public Task<IReadOnlyList<Guid>> GetActiveIdsByOriginAnalysisAsync(Guid analysisId, string tenantId, CancellationToken ct)
            => Task.FromResult(ActiveCupoIds);

        public Task<IReadOnlyList<CreditLine>> GetByOriginAnalysisAsync(Guid analysisId, string tenantId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<CreditLine>>([]);

        public Task<IReadOnlyList<Guid>> GetIdsByOriginAnalysisAsync(Guid analysisId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<CreditLine>> GetNonTerminalByEntityAsync(Guid entityId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<CreditLine?> GetActiveByEntityCurrencyAsync(Guid entityId, string currency, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<CreditLine?> GetByIdAsync(Guid creditLineId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task CreateAsync(CreditLine line, CancellationToken ct) => throw new NotImplementedException();
        public Task<CreditLine?> RaiseAsync(Guid creditLineId, string tenantId, decimal newAmount, Guid analysisId, DateTime? expectedLastRaisedAt, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlySet<Guid>> GetEntityIdsWithNonTerminalLineAsync(IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, MainCreditLineProjection>> GetMainActiveByEntityIdsAsync(IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<CreditLine?> GetBlockingByEntityCurrencyAsync(Guid entityId, string currency, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> SetStatusAsync(Guid creditLineId, string tenantId, string expectedStatus, string newStatus, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<CupoAlertSource>> GetActiveForCupoAlertsAsync(string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<PymeReviewSource>> GetLatestApprovedAnalysesOfActiveEntitiesAsync(string tenantId, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class StubOperationRepository : ICreditLineOperationRepository
    {
        public bool HasNonCancelledToReturn { get; set; }

        public Task<bool> HasNonCancelledByAnalysisAsync(Guid analysisId, string tenantId, CancellationToken ct)
            => Task.FromResult(HasNonCancelledToReturn);

        public Task<IReadOnlyList<CreditLineOperation>> GetByCreditLineAsync(Guid creditLineId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<CreditLineOperation>> GetByAnalysisAsync(Guid analysisId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<CreditLineOperation?> GetByIdAsync(Guid operationId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task CreateAsync(CreditLineOperation op, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> CancelAsync(Guid operationId, string tenantId, string expectedStatus, Guid cancelledByUserId, DateTime cancelledAtUtc, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> ConfirmVigenciaAsync(Guid operationId, string tenantId, Guid confirmedByUserId, DateTime confirmedAtUtc, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class StubDecisionNotifier : IAnalysisDecisionNotifier
    {
        public int     CallCount       { get; private set; }
        public string? LastAuditAction { get; private set; }

        public Task NotifyAsync(
            string tenantId, Analysis analysis, User currentUser, string auditAction,
            bool wasPreApproved, string? entityName, decimal? amount, string? currency,
            CancellationToken ct)
        {
            CallCount++;
            LastAuditAction = auditAction;
            return Task.CompletedTask;
        }
    }

    private sealed class StubAuditService : IAuditService
    {
        public List<string> Actions { get; } = [];

        public Task LogAsync(
            string tenantId, Guid? userId, string action, string severity = "Info",
            string? entityType = null, string? entityId = null, string? ipAddress = null,
            string? requestId = null, string? oldValueJson = null, string? newValueJson = null,
            CancellationToken ct = default)
        {
            Actions.Add(action);
            return Task.CompletedTask;
        }

        public Task LogSessionAsync(
            Guid tenantId, Guid? userId, string email, string eventType,
            string? ipAddress, string? userAgent, string? failureReason = null,
            CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class StubUserApprovalLimitRepository : IUserApprovalLimitRepository
    {
        // Empty on purpose: a confirmer must never consult this table, so any read returns nothing
        // and a path that depended on one would fail visibly.
        public Task<UserApprovalLimit?> GetAsync(Guid userId, string currency, string tenantId, CancellationToken ct)
            => Task.FromResult<UserApprovalLimit?>(null);

        public Task<IReadOnlyList<UserApprovalLimit>> GetByUserAsync(Guid userId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<UserApprovalLimit>> ReplaceForUserAsync(Guid userId, string tenantId, IReadOnlyCollection<UserApprovalLimit> limits, Guid editedBy, CancellationToken ct) => throw new NotImplementedException();
    }

    // ── Fixture ───────────────────────────────────────────────────────────────

    private sealed class Fixture
    {
        public required AnalysisController                Sut       { get; init; }
        public required StubAnalysisRepository            Analyses  { get; init; }
        public required StubAnalysisResultRepository      Results   { get; init; }
        public required StubAnalysisApprovalQuorumService Quorum    { get; init; }
        public required StubDecisionNotifier              Notifier  { get; init; }
        public required StubAuditService                  Audit     { get; init; }
        public required StubCreditLineRepository          Cupos     { get; init; }
        public required StubOperationRepository           Operations{ get; init; }
        public required Analysis                          Analysis  { get; init; }
        public required User                              Caller    { get; init; }

        public Guid ResultId => Results.ResultToReturn!.ResultId;
    }

    private static Fixture Build(
        string status = "PreApproved",
        string role   = Roles.RiskManager,
        StubAnalysisApprovalQuorumService? quorum = null)
    {
        var caller = new User
        {
            UserId      = Guid.NewGuid(),
            TenantId    = Tenant,
            FirebaseUid = FirebaseUid,
            DisplayName = role,
            Role        = role,
        };

        var analysis = new Analysis
        {
            AnalysisId    = Guid.NewGuid(),
            TenantId      = Tenant,
            EntityId      = Guid.NewGuid(),
            Status        = status,
            PreApprovedBy = Guid.NewGuid(),
        };

        var results    = new StubAnalysisResultRepository
        {
            ResultToReturn = new AnalysisResult
            {
                ResultId   = Guid.NewGuid(),
                AnalysisId = analysis.AnalysisId,
                TenantId   = Tenant,
            }
        };
        var analyses   = new StubAnalysisRepository { AnalysisToReturn = analysis };
        var cupos      = new StubCreditLineRepository();
        var operations = new StubOperationRepository();
        var audit      = new StubAuditService();
        var notifier   = new StubDecisionNotifier();
        quorum       ??= new StubAnalysisApprovalQuorumService();

        var sut = new AnalysisController(
            new StubTenantContextAccessor
            {
                TenantContext = new TenantContext
                {
                    TenantId               = Tenant,
                    FirebaseProjectId      = "proj",
                    FirebaseCredentialJson = "{}",
                    RecaptchaSecretKey     = "secret",
                }
            },
            null!,                                    // documentRepository
            results,                                  // analysisResultRepository
            analyses,                                 // analysisRepository
            null!, null!, null!, null!,
            new StubEntityRepository(),               // entityRepository
            new StubUserRepository { CurrentUser = caller },
            null!,                                    // chat
            cupos,                                    // creditLineRepository
            operations,                               // operationRepository
            audit,                                    // auditService
            null!, null!, null!, null!,               // apiCallLog … tenantBillingConfigService
            new StubUserApprovalLimitRepository(),
            notifier,                                 // decisionNotifier
            quorum,                                   // approvalQuorumService
            new Api.Web.FinanceIA.Tests.Legajo.FakeLegajoDocumentRepository(),
            null!,                                    // analysisPipeline
            null!,                                    // analysisJobQueue
            null!,                                    // analysisRunRegistry
            null!,                                    // analysisRunStateRepository
            new Api.Web.FinanceIA.Tests.PersonAnalysis.StubPersonAnalysisRepository(),
            NullLogger<AnalysisController>.Instance);

        sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("uid", FirebaseUid)], "Test")),
            }
        };

        return new Fixture
        {
            Sut = sut, Analyses = analyses, Results = results, Quorum = quorum,
            Notifier = notifier, Audit = audit, Cupos = cupos, Operations = operations,
            Analysis = analysis, Caller = caller,
        };
    }

    /// <summary>
    /// A decision payload. It acknowledges the score-reliability caveats by default (score credibility 1b-5):
    /// these fixtures carry no score, so their verdict is never 'complete' and the review gate would refuse
    /// every one of them; the gate itself is covered by AnalysisControllerDecideTests.ReviewGate.
    /// </summary>
    private static AnalysisDecisionRequest Request(
        string decision, decimal? amount = 1_000m, string? currency = "ARS", bool? acknowledgedCaveats = true)
        => new() { Decision = decision, Amount = amount, Currency = currency, AcknowledgedCaveats = acknowledgedCaveats };

    // ── Approving below quorum ────────────────────────────────────────────────

    [Fact]
    public async Task VoteBelowQuorum_Returns200_LeavesTheAnalysisPreApproved_AndNotifiesNobody()
    {
        var approver = Guid.NewGuid();
        var quorum = new StubAnalysisApprovalQuorumService
        {
            Outcome = ApprovalVoteOutcome.Pending,
            Tally   = StubAnalysisApprovalQuorumService.Quorum(2, approver),
        };
        var f = Build(quorum: quorum);

        var result = await f.Sut.DecideAsync(f.ResultId, Request("Approved"), CancellationToken.None);

        var ok      = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<AnalysisResultResponse>(ok.Value);

        // The row must be untouched — "stays PreApproved" has to be literally true.
        Assert.False(f.Analyses.UpdateCalled);
        Assert.Equal("PreApproved", f.Analysis.Status);
        Assert.Null(f.Analysis.ReviewedBy);

        // The tally the Portal renders as "aprobaron 1 de 2".
        Assert.Equal(2, payload.ApprovalQuorum);
        Assert.Equal(1, payload.ApprovalCount);
        Assert.Equal([approver.ToString()], payload.ApprovedByUserIds);

        // No inbox copy: the analyst's pre-approval has NOT been confirmed yet.
        Assert.Equal(0, f.Notifier.CallCount);
        Assert.Contains("Analysis.ApprovalVoted", f.Audit.Actions);
        Assert.DoesNotContain("Analysis.Confirmed", f.Audit.Actions);
    }

    [Fact]
    public async Task VoteThatCrossesTheQuorum_ConfirmsAndNotifiesExactlyAsASingleSignatureDid()
    {
        var quorum = new StubAnalysisApprovalQuorumService
        {
            Outcome = ApprovalVoteOutcome.Confirmed,
            Tally   = StubAnalysisApprovalQuorumService.Quorum(2, Guid.NewGuid(), Guid.NewGuid()),
        };
        var f = Build(quorum: quorum);

        var result = await f.Sut.DecideAsync(
            f.ResultId, Request("Approved", 750_000m, "ARS"), CancellationToken.None);

        var ok      = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<AnalysisResultResponse>(ok.Value);

        Assert.Equal("Approved", f.Analysis.Status);
        Assert.Equal(f.Caller.UserId, f.Analysis.ReviewedBy);
        Assert.Equal(750_000m, f.Analysis.DecisionAmount);
        Assert.Equal("ARS", f.Analysis.DecisionCurrency);

        // The engine owns the write. A second, unguarded save of the same row would undo the
        // exactly-once property the guarded write exists for.
        Assert.False(f.Analyses.UpdateCalled);
        Assert.Equal(1, f.Quorum.VoteCallCount);

        Assert.Equal(1, f.Notifier.CallCount);
        Assert.Equal("Analysis.Confirmed", f.Notifier.LastAuditAction);
        Assert.Contains("Analysis.Confirmed", f.Audit.Actions);
        Assert.Equal(2, payload.ApprovalCount);
    }

    [Fact]
    public async Task WhenAnotherManagerCrossedFirst_TheLoserReports200_AndClaimsNoConfirmation()
    {
        // Their vote landed and the analysis IS approved — just not on their signature. Emitting a
        // second 'Analysis.Confirmed' and a second inbox copy would credit them with a decision they
        // did not make and tell the analyst twice.
        var quorum = new StubAnalysisApprovalQuorumService
        {
            Outcome = ApprovalVoteOutcome.AlreadyConfirmed,
            Tally   = StubAnalysisApprovalQuorumService.Quorum(2, Guid.NewGuid(), Guid.NewGuid()),
        };
        var f = Build(quorum: quorum);

        var result = await f.Sut.DecideAsync(f.ResultId, Request("Approved"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.False(f.Analyses.UpdateCalled);
        Assert.Equal(0, f.Notifier.CallCount);
        Assert.DoesNotContain("Analysis.Confirmed", f.Audit.Actions);
        Assert.Contains("Analysis.ApprovalVoted", f.Audit.Actions);
    }

    [Fact]
    public async Task NoQuorumInForce_ConfirmsOnOneSignature_AndShowsNoTally()
    {
        // The no-regression case: a tenant whose teams name no managers must behave exactly as it
        // did before, and must show no tally rather than a misleading "1 de 1".
        var f = Build();   // the stub defaults to NoQuorum + Confirmed

        var result = await f.Sut.DecideAsync(f.ResultId, Request("Approved"), CancellationToken.None);

        var payload = Assert.IsType<AnalysisResultResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("Approved", f.Analysis.Status);
        Assert.Equal(1, f.Notifier.CallCount);
        Assert.Null(payload.ApprovalQuorum);
        Assert.Null(payload.ApprovalCount);
        Assert.Null(payload.ApprovedByUserIds);
    }

    // ── Veto, return and re-pre-approval ──────────────────────────────────────

    [Fact]
    public async Task Rejection_IsImmediate_EvenWithApprovalsOnRecord()
    {
        // A veto answers to no quorum. One manager refusing ends it, however many colleagues signed.
        var quorum = new StubAnalysisApprovalQuorumService
        {
            Tally = StubAnalysisApprovalQuorumService.Quorum(3, Guid.NewGuid(), Guid.NewGuid()),
        };
        var f = Build(quorum: quorum);

        var result = await f.Sut.DecideAsync(f.ResultId, Request("Rejected", null, null), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.True(f.Analyses.UpdateCalled);
        Assert.Equal("Rejected", f.Analysis.Status);
        Assert.Equal(0, f.Quorum.VoteCallCount);
        // The veto is on the record beside the approvals that were standing when it landed.
        Assert.Equal(1, f.Quorum.VetoCallCount);
        Assert.Contains("Analysis.Rejected", f.Audit.Actions);
    }

    [Fact]
    public async Task RejectingAnApprovedAnalysisWithALiveCupo_StillReturns409AndNeverVotes()
    {
        // The pre-existing operative-credit guard is upstream of everything the quorum engine added
        // and must stay exactly where it was: no write, and now also no vote and no veto row.
        var f = Build(status: "Approved");
        f.Cupos.ActiveCupoIds = [Guid.NewGuid()];

        var result = await f.Sut.DecideAsync(f.ResultId, Request("Rejected", null, null), CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(409, obj.StatusCode);
        Assert.False(f.Analyses.UpdateCalled);
        Assert.Equal(0, f.Quorum.VoteCallCount);
        Assert.Equal(0, f.Quorum.VetoCallCount);
        Assert.Equal(0, f.Quorum.DiscardCallCount);
    }

    [Fact]
    public async Task ReturnToReview_DiscardsEveryVote()
    {
        var f = Build();

        var result = await f.Sut.DecideAsync(f.ResultId, Request("PendingReview", null, null), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("PendingReview", f.Analysis.Status);
        Assert.Equal(1, f.Quorum.DiscardCallCount);
        Assert.Equal([f.Analysis.AnalysisId], f.Quorum.Discarded);
        Assert.Contains("Analysis.ReturnedToReview", f.Audit.Actions);
    }

    [Fact]
    public async Task EnteringPreApprovedAgain_DiscardsTheVotesOfThePreviousRound()
    {
        // Returning for rework is not the only way back into 'PreApproved' — a rejected or approved
        // analysis can be re-decided — and signatures given for a superseded proposal must never
        // pre-fill the new quorum.
        var f = Build(status: "Rejected", role: Roles.Analyst);

        var result = await f.Sut.DecideAsync(f.ResultId, Request("Approved", 5_000m, "ARS"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("PreApproved", f.Analysis.Status);
        Assert.Equal(1, f.Quorum.DiscardCallCount);
        Assert.Contains("Analysis.PreApproved", f.Audit.Actions);
    }

    // ── Withdraw ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Withdraw_ByTheOwnerWhilePreApproved_DropsTheTally()
    {
        var remaining = Guid.NewGuid();
        var quorum = new StubAnalysisApprovalQuorumService
        {
            Tally           = StubAnalysisApprovalQuorumService.Quorum(2, remaining),
            WithdrawRemoves = true,
        };
        var f = Build(quorum: quorum);

        var result = await f.Sut.WithdrawApprovalVoteAsync(f.ResultId, CancellationToken.None);

        var payload = Assert.IsType<AnalysisResultResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(1, payload.ApprovalCount);
        Assert.Equal(2, payload.ApprovalQuorum);
        Assert.Equal(1, f.Quorum.WithdrawCallCount);
        Assert.Contains("Analysis.ApprovalVoteWithdrawn", f.Audit.Actions);
        // A withdrawal is not a decision: the analysis row is never written.
        Assert.False(f.Analyses.UpdateCalled);
        Assert.Equal("PreApproved", f.Analysis.Status);
    }

    [Fact]
    public async Task Withdraw_WithNothingOnRecord_Is200AndWritesNoAuditRow()
    {
        // Idempotent: the caller asked for their approval not to be standing, and it is not. An
        // audit row here would fill the trail with acts that never happened.
        var quorum = new StubAnalysisApprovalQuorumService { WithdrawRemoves = false };
        var f = Build(quorum: quorum);

        var result = await f.Sut.WithdrawApprovalVoteAsync(f.ResultId, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.DoesNotContain("Analysis.ApprovalVoteWithdrawn", f.Audit.Actions);
    }

    [Fact]
    public async Task Withdraw_ByAnAnalyst_Returns403()
    {
        // An Analyst can never have cast a vote, so this could only ever be an attempt at somebody
        // else's. Refused on the role from the DATABASE row, as the decision endpoint is.
        var f = Build(role: Roles.Analyst);

        var result = await f.Sut.WithdrawApprovalVoteAsync(f.ResultId, CancellationToken.None);

        Assert.Equal(403, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(0, f.Quorum.WithdrawCallCount);
    }

    [Theory]
    [InlineData("Approved")]
    [InlineData("Rejected")]
    [InlineData("PendingReview")]
    public async Task Withdraw_OnAnAnalysisThatIsNotPreApproved_Returns409(string status)
    {
        // Once settled, the signatures are the record of HOW it was settled. Withdrawing one would
        // rewrite the justification of a credit decision that is already committed.
        var f = Build(status: status);

        var result = await f.Sut.WithdrawApprovalVoteAsync(f.ResultId, CancellationToken.None);

        Assert.Equal(409, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(0, f.Quorum.WithdrawCallCount);
    }

    [Fact]
    public async Task Withdraw_OnAnUnknownResult_Returns404()
    {
        var f = Build();
        f.Results.ResultToReturn = null;

        var result = await f.Sut.WithdrawApprovalVoteAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(0, f.Quorum.WithdrawCallCount);
    }

    [Fact]
    public async Task Withdraw_OnAnAnalysisOfAnotherTenant_Returns404()
    {
        var f = Build();
        f.Analysis.TenantId = "tenant-2";

        var result = await f.Sut.WithdrawApprovalVoteAsync(f.ResultId, CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(0, f.Quorum.WithdrawCallCount);
    }

    // ── Score reliability (score credibility 1b-2b) ─────────────────────────

    /// <summary>Makes the fixture result the shared score-reliability parity row (score credibility 1b-2b).</summary>
    private static void UseParityRow(AnalysisResult result, Analysis analysis)
    {
        result.AiSuggestedScore   = ScoreReliabilityFixture.ParityScore;
        result.EnrichmentStatus   = ScoreReliabilityFixture.ParityEnrichment;
        result.GeneratedAt        = ScoreReliabilityFixture.ParityGeneratedAt;
        result.RescoredAt         = ScoreReliabilityFixture.ParityRescoredAt;
        result.ScoreBreakdownJson = ScoreReliabilityFixture.ParityBreakdownJson();
        analysis.FiscalYear       = ScoreReliabilityFixture.ParityFiscalYear;
    }

    private static void AssertParityVerdict(AnalysisResultResponse response)
        => Assert.Equal(ScoreReliabilityFixture.Wire(ScoreReliabilityFixture.ParityExpected()),
                        ScoreReliabilityFixture.Wire(response.ScoreReliability));

    [Fact]
    public async Task VoteBelowQuorum_CarriesTheScoreReliabilityOfItsScore()
    {
        var quorum = new StubAnalysisApprovalQuorumService
        {
            Outcome = ApprovalVoteOutcome.Pending,
            Tally   = StubAnalysisApprovalQuorumService.Quorum(2, Guid.NewGuid()),
        };
        var f = Build(quorum: quorum);
        UseParityRow(f.Results.ResultToReturn!, f.Analysis);

        var result = await f.Sut.DecideAsync(f.ResultId, Request("Approved"), CancellationToken.None);

        AssertParityVerdict(Assert.IsType<AnalysisResultResponse>(Assert.IsType<OkObjectResult>(result).Value));
    }

    [Fact]
    public async Task WithdrawVote_CarriesTheScoreReliabilityOfItsScore()
    {
        var quorum = new StubAnalysisApprovalQuorumService
        {
            Tally           = StubAnalysisApprovalQuorumService.Quorum(2, Guid.NewGuid()),
            WithdrawRemoves = true,
        };
        var f = Build(quorum: quorum);
        UseParityRow(f.Results.ResultToReturn!, f.Analysis);

        var result = await f.Sut.WithdrawApprovalVoteAsync(f.ResultId, CancellationToken.None);

        AssertParityVerdict(Assert.IsType<AnalysisResultResponse>(Assert.IsType<OkObjectResult>(result).Value));
    }
}
