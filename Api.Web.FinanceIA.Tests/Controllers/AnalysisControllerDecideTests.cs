// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-07

using System.Security.Claims;
using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Tests.Servicios.ScoreReliability;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Covers the decision endpoint <see cref="AnalysisController.DecideAsync"/> and, specifically, the
/// guard that blocks Approved → Rejected while the credit the analysis justifies is still OPERATIVE:
/// an ACTIVE cupo or a non-cancelled operation. Rejection is refused (409) and the analysis is NEVER
/// written; a merely SUSPENDED cupo lends nothing further and no longer blocks on its own, so once
/// every dependent cupo is suspended (and no operation is live) rejection succeeds. The remedies
/// differ — suspend an active cupo, but wait out a live operation — and the 409 detail says which.
/// Every other transition is unchanged.
///
/// Hand-rolled private sealed stubs (no mock library). DecideAsync touches the tenant accessor, the
/// analysis-result + analysis + entity + user + credit-line + operation repositories and the audit
/// service, so the many other AnalysisController dependencies are passed as null! (the constructor
/// performs plain assignment). StubAnalysisRepository tracks UpdateAsync so a 409 that still mutated
/// the row — the worst possible outcome — is caught by asserting UpdateCalled is false. The
/// credit-line stub's write members throw, so a successful rejection also proves no cupo was touched.
/// </summary>
public sealed partial class AnalysisControllerDecideTests
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

        public Task<IReadOnlyList<AnalysisResult>> GetAllByTenantAsync(string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<AnalysisResult?> GetByDocumentIdAsync(Guid documentId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<AnalysisResult> CreateAsync(AnalysisResult result, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<AnalysisResult>> GetByEntityIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<AnalysisResult> UpdateAsync(AnalysisResult result, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, Guid>> GetResultIdsByAnalysisIdsAsync(IEnumerable<Guid> analysisIds, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<(IReadOnlyList<AnalysisListProjection> Items, int TotalCount)> GetListPageByTenantAsync(
            string tenantId, int skip, int take, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<(IReadOnlyList<AnalysisListProjection> Items, int TotalCount)> GetListPageByEntityAsync(
            Guid entityId, string tenantId, int skip, int take, CancellationToken ct)
            => throw new NotImplementedException();
        // DecideAsync is a WRITE path: it loads the full row via GetByIdAsync and hands it to
        // UpdateAsync. It must never take the projected detail read.
        public Task<AnalysisResultDetailProjection?> GetDetailByIdAsync(
            Guid resultId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<AnalysisContextProjection?> GetContextSnapshotByIdAsync(
            Guid resultId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<AnalysisBcraProjection?> GetBcraSnapshotByIdAsync(
            Guid resultId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<AnalysisRatiosProjection?> GetRatiosByIdAsync(
            Guid resultId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<AnalysisLabelProjection?> GetLabelByIdAsync(
            Guid resultId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubAnalysisRepository : IAnalysisRepository
    {
        public Task<BalanceBaseFiguresProjection?> GetBalanceBaseFiguresAsync(Guid analysisId, string tenantId, CancellationToken ct)
            => Task.FromResult<BalanceBaseFiguresProjection?>(null);

        public Task<IReadOnlyDictionary<Guid, EntityLatestScoreProjection>> GetLatestScoreByEntityIdsAsync(
            IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct) => throw new NotImplementedException();

        // The quorum-crossing write (DDL 067). Throws on purpose: the CONTROLLER must never call it
        // directly — only AnalysisApprovalQuorumService does, behind IAnalysisApprovalQuorumService —
        // so a call arriving here means the confirmation path bypassed the engine.
        public Task<bool> TryConfirmFromPreApprovedAsync(
            Guid analysisId, string tenantId, Guid reviewedBy, DateTime reviewedAt,
            decimal? decisionAmount, string? decisionCurrency, string? confirmedOperationsJson,
            string? reviewNotes, CancellationToken ct)
            => throw new NotImplementedException();
        public Analysis? AnalysisToReturn { get; set; }
        public bool UpdateCalled { get; private set; }
        public Analysis? Updated { get; private set; }

        public Task<Analysis?> GetByIdAsync(Guid analysisId, CancellationToken ct)
            => Task.FromResult(AnalysisToReturn);

        public Task<Analysis> UpdateAsync(Analysis analysis, CancellationToken ct)
        {
            UpdateCalled = true;
            Updated      = analysis;
            return Task.FromResult(analysis);
        }

        public Task<Analysis> CreateAsync(Analysis analysis, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<Analysis>> GetByIdsAsync(IEnumerable<Guid> analysisIds, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Analysis?> GetLatestByDocumentIdAsync(Guid documentId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, string>> GetLatestStatusByEntityIdsAsync(IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<int> CountByTenantInPeriodAsync(string tenantId, DateTime periodStartUtc, DateTime periodEndUtc, CancellationToken ct) => throw new NotImplementedException();
    }

    /// <summary>
    /// Approval limits keyed by (UserId, Currency). An absent key returns null, which is the real
    /// repository's meaningful default: that user may not approve directly in that currency.
    /// </summary>
    private sealed class StubUserApprovalLimitRepository : IUserApprovalLimitRepository
    {
        private readonly Dictionary<(Guid, string), UserApprovalLimit> _limits = new();

        /// <summary>An empty set — nobody may approve directly in any currency.</summary>
        public static StubUserApprovalLimitRepository Empty() => new();

        public static StubUserApprovalLimitRepository WithLimit(
            Guid userId, string currency, bool canApprove, decimal? maxAmount)
        {
            var repo = new StubUserApprovalLimitRepository();
            repo._limits[(userId, currency)] = new UserApprovalLimit
            {
                TenantId   = Tenant,
                UserId     = userId,
                Currency   = currency,
                CanApprove = canApprove,
                MaxAmount  = maxAmount
            };
            return repo;
        }

        public Task<UserApprovalLimit?> GetAsync(
            Guid userId, string currency, string tenantId, CancellationToken ct)
        {
            _limits.TryGetValue((userId, currency), out var limit);
            return Task.FromResult<UserApprovalLimit?>(limit);
        }

        public Task<IReadOnlyList<UserApprovalLimit>> GetByUserAsync(
            Guid userId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();

        // A decision must never rewrite the limits it is being judged against.
        public Task<IReadOnlyList<UserApprovalLimit>> ReplaceForUserAsync(
            Guid userId, string tenantId, IReadOnlyCollection<UserApprovalLimit> limits,
            Guid editedBy, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubEntityRepository : IEntityRepository
    {
        public Entity? EntityToReturn { get; set; }

        public Task<Entity?> GetByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => Task.FromResult(EntityToReturn);

        public Task<IReadOnlyList<Entity>> GetAllByTenantAsync(string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<bool> ExistsByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Entity?> GetByCuitAsync(string cuit, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Entity> CreateAsync(Entity entity, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Entity> UpdateAsync(Entity entity, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<(IReadOnlyList<EntityListProjection> Items, int TotalCount)> GetListPageByTenantAsync(
            string tenantId, int skip, int take, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, EntityNameProjection>> GetNameLookupByIdsAsync(
            IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntityDetailProjection?> GetDetailByIdAsync(
            Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntitySnapshotProjection?> GetBcraSnapshotAsync(
            Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntitySnapshotProjection?> GetCasfogSnapshotAsync(
            Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntitySnapshotProjection?> GetLufeSnapshotAsync(
            Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<EntitySnapshotProjection?> GetNosisSnapshotAsync(
            Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<EntityNameProjection?> GetNameByIdAsync(
            Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubUserRepository : IUserRepository
    {
        public User? CurrentUser { get; set; }
        public User? RequesterToReturn { get; set; }

        /// <summary>Users resolvable by the batched actor-name lookup, keyed by UserId.</summary>
        public Dictionary<Guid, User> Directory { get; } = new();

        /// <summary>How many times the batched lookup ran — one per request is the contract.</summary>
        public int GetByIdsCallCount { get; private set; }

        public Task<User?> GetByFirebaseUidAsync(string firebaseUid, CancellationToken ct)
            => Task.FromResult(CurrentUser);
        public Task<User?> GetByUserIdAsync(Guid userId, CancellationToken ct)
            => Task.FromResult(RequesterToReturn);

        public Task<IReadOnlyList<User>> GetByIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct)
        {
            GetByIdsCallCount++;
            IReadOnlyList<User> found = userIds
                .Where(Directory.ContainsKey)
                .Select(id => Directory[id])
                .ToList();
            return Task.FromResult(found);
        }

        public Task<User?> GetByEmailAsync(string email, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> ExistsByFirebaseUidAsync(string firebaseUid, CancellationToken ct) => throw new NotImplementedException();
        public Task<User> CreateAsync(User user, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateLastLoginAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateOnboardingFlagsAsync(Guid userId, string? flagsJson, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<User>> GetActiveByTenantAsync(string tenantId, CancellationToken ct) => throw new NotImplementedException();
        // Team assignment (DDL 063) — unused by this suite.
        public Task UpdateTeamAsync(Guid userId, Guid? teamId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> ClearTeamAsync(Guid teamId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class StubCreditLineRepository : ICreditLineRepository
    {
        // The reject guard now asks only for the ACTIVE cupos (GetActiveIdsByOriginAnalysisAsync).
        // A merely Suspended cupo is not in this set, so "all suspended" is modelled as an empty list.
        public IReadOnlyList<Guid> ActiveCupoIds { get; set; } = Array.Empty<Guid>();

        public Task<IReadOnlyList<Guid>> GetActiveIdsByOriginAnalysisAsync(Guid analysisId, string tenantId, CancellationToken ct)
            => Task.FromResult(ActiveCupoIds);

        // No longer called by DecideAsync; throw so an accidental re-introduction is caught loudly.
        public Task<IReadOnlyList<Guid>> GetIdsByOriginAnalysisAsync(Guid analysisId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<CreditLine>> GetNonTerminalByEntityAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<CreditLine?> GetActiveByEntityCurrencyAsync(Guid entityId, string currency, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<CreditLine?> GetByIdAsync(Guid creditLineId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task CreateAsync(CreditLine line, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<CreditLine?> RaiseAsync(
            Guid creditLineId, string tenantId, decimal newAmount, Guid analysisId,
            DateTime? expectedLastRaisedAt, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlySet<Guid>> GetEntityIdsWithNonTerminalLineAsync(IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, MainCreditLineProjection>> GetMainActiveByEntityIdsAsync(
            IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<CreditLine>> GetByOriginAnalysisAsync(Guid analysisId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<CreditLine?> GetBlockingByEntityCurrencyAsync(Guid entityId, string currency, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<bool> SetStatusAsync(Guid creditLineId, string tenantId, string expectedStatus, string newStatus, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<CupoAlertSource>> GetActiveForCupoAlertsAsync(string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<PymeReviewSource>> GetLatestApprovedAnalysesOfActiveEntitiesAsync(string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubOperationRepository : ICreditLineOperationRepository
    {
        public bool HasNonCancelledToReturn { get; set; }

        public Task<bool> HasNonCancelledByAnalysisAsync(Guid analysisId, string tenantId, CancellationToken ct)
            => Task.FromResult(HasNonCancelledToReturn);

        public Task<IReadOnlyList<CreditLineOperation>> GetByCreditLineAsync(Guid creditLineId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<CreditLineOperation>> GetByAnalysisAsync(Guid analysisId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<CreditLineOperation?> GetByIdAsync(Guid operationId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task CreateAsync(CreditLineOperation op, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<bool> CancelAsync(
            Guid operationId, string tenantId, string expectedStatus, Guid cancelledByUserId,
            DateTime cancelledAtUtc, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<bool> ConfirmVigenciaAsync(
            Guid operationId, string tenantId, Guid confirmedByUserId, DateTime confirmedAtUtc, CancellationToken ct)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Records the decision-notification calls without touching a database.
    /// </summary>
    /// <remarks>
    /// A real stub rather than <c>null!</c> because DecideAsync now calls this on every decision.
    /// Passing null would still let these tests pass — the controller's emission is wrapped in a
    /// try/catch that swallows the NullReferenceException — but it would do so by exercising the
    /// failure path, so a genuinely broken notifier would look identical to a working one.
    /// </remarks>
    private sealed class StubDecisionNotifier : IAnalysisDecisionNotifier
    {
        public int CallCount { get; private set; }

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
        public bool LogCalled { get; private set; }
        public string? Action { get; private set; }
        public string? NewValueJson { get; private set; }

        /// <summary>Every row written, in order (score credibility 1b-5 reads the newValueJson).</summary>
        public List<(string Action, string? NewValueJson)> Calls { get; } = [];

        public Task LogAsync(
            string tenantId, Guid? userId, string action, string severity = "Info",
            string? entityType = null, string? entityId = null, string? ipAddress = null,
            string? requestId = null, string? oldValueJson = null, string? newValueJson = null,
            CancellationToken ct = default)
        {
            LogCalled    = true;
            Action       = action;
            NewValueJson = newValueJson;
            Calls.Add((action, newValueJson));
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

    private static Analysis BuildAnalysis(string status) => new()
    {
        AnalysisId = Guid.NewGuid(),
        TenantId   = Tenant,
        EntityId   = Guid.NewGuid(),
        Status     = status
    };

    private static AnalysisResult BuildResult(Guid analysisId) => new()
    {
        ResultId   = Guid.NewGuid(),
        AnalysisId = analysisId,
        TenantId   = Tenant
    };

    /// <summary>
    /// A decision payload. Amount and currency default to values that satisfy the approval path, so
    /// a test that only cares about the cupo guard need not restate them; a test about the amount
    /// rules passes its own (or nulls, to exercise the 400).
    /// <para>
    /// It acknowledges the score-reliability caveats by default (score credibility 1b-5): the fixture result
    /// carries no score, so its verdict is never 'complete' and the review gate would otherwise refuse every
    /// test here. The gate's own tests (ReviewGate partial) pass null or false explicitly.
    /// </para>
    /// </summary>
    private static AnalysisDecisionRequest Request(
        string decision, decimal? amount = 1_000m, string? currency = "ARS",
        IReadOnlyList<AnalysisDecisionOperation>? operations = null,
        bool? acknowledgedCaveats = true, string? figuresReviewNote = null, string? reviewNotes = null)
        => new()
        {
            Decision            = decision,
            Amount              = amount,
            Currency            = currency,
            Operations          = operations,
            AcknowledgedCaveats = acknowledgedCaveats,
            FiguresReviewNote   = figuresReviewNote,
            ReviewNotes         = reviewNotes
        };

    /// <summary>
    /// One proposed/confirmed operation. Defaults are a valid ARS operation, so a test that cares
    /// about one field states only that field.
    /// </summary>
    private static AnalysisDecisionOperation Operation(
        int type = 3, decimal amount = 100m, string? currency = "ARS",
        bool aSolaFirma = false, bool requiereContragarantiaReal = false,
        DateTime? validUntil = null, string? notes = null)
        => new()
        {
            Type                       = type,
            Amount                     = amount,
            Currency                   = currency,
            ValidUntil                 = validUntil,
            Notes                      = notes,
            ASolaFirma                 = aSolaFirma,
            RequiereContragarantiaReal = requiereContragarantiaReal
        };

    /// <summary>
    /// The analysis as it stands once the decision has been written, WHICHEVER path wrote it.
    /// </summary>
    /// <remarks>
    /// Since the quorum engine landed there are two writers. A pre-approval, a rejection and a return
    /// still go through <c>IAnalysisRepository.UpdateAsync</c>, so <c>Updated</c> holds them. A
    /// CONFIRMATION does not: the engine writes it with a guarded conditional UPDATE and mirrors the
    /// result onto the entity, precisely so two managers crossing a quorum at once cannot both save
    /// the row. Both writers act on the same instance the controller loaded, so reading it covers
    /// both — and the confirmation tests below separately assert that the engine was the one asked,
    /// which is the part <c>Updated</c> used to prove.
    /// </remarks>
    private static Analysis Settled(StubAnalysisRepository analyses)
        => analyses.Updated ?? analyses.AnalysisToReturn!;

    /// <summary>
    /// Reads a stored operations blob back with the same case-insensitive options the controller
    /// uses, so a test asserts on the DECODED terms rather than on a string's exact spelling.
    /// </summary>
    private static IReadOnlyList<AnalysisDecisionOperation> ReadOperations(string? json)
    {
        Assert.NotNull(json);
        var parsed = JsonSerializer.Deserialize<List<AnalysisDecisionOperation>>(
            json!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(parsed);
        return parsed!;
    }

    /// <summary>
    /// Builds the controller with only the DecideAsync-relevant dependencies stubbed; everything
    /// else is null! (the constructor only assigns). Argument order mirrors the ctor signature.
    /// <para>
    /// The caller defaults to an <c>Analyst</c> holding an ARS limit large enough to approve
    /// directly, which is the pre-existing one-step behaviour every test in this file was written
    /// against. The routing tests next door override both.
    /// </para>
    /// </summary>
    private static AnalysisController BuildSut(
        StubAnalysisResultRepository resultRepo,
        StubAnalysisRepository       analysisRepo,
        StubCreditLineRepository     creditLineRepo,
        StubOperationRepository      operationRepo,
        StubAuditService             audit,
        User?                        caller = null,
        StubUserApprovalLimitRepository? limitRepo = null,
        StubAnalysisApprovalQuorumService? quorumService = null,
        Api.Web.FinanceIA.Tests.PersonAnalysis.StubPersonAnalysisRepository? personAnalyses = null,
        Microsoft.Extensions.Logging.ILogger<AnalysisController>? logger = null)
    {
        var currentUser = caller ?? new User
        {
            UserId      = Guid.NewGuid(),
            FirebaseUid = FirebaseUid,
            DisplayName = "Analyst",
            Role        = Roles.Analyst
        };
        var requester = new User { UserId = Guid.NewGuid(), DisplayName = "Requester" };

        var userRepo = new StubUserRepository
        {
            CurrentUser       = currentUser,
            RequesterToReturn = requester
        };
        userRepo.Directory[currentUser.UserId] = currentUser;
        userRepo.Directory[requester.UserId]   = requester;

        limitRepo ??= StubUserApprovalLimitRepository.WithLimit(
            currentUser.UserId, "ARS", canApprove: true, maxAmount: 1_000_000m);

        var entityRepo = new StubEntityRepository { EntityToReturn = new Entity { EntityId = Guid.NewGuid(), TenantId = Tenant } };

        var notifier = new StubDecisionNotifier();

        // Defaults to NO quorum in force — a tenant whose teams name no managers — which is the
        // state every test in this file was written against and the one the quorum engine must leave
        // byte-for-byte alone. A test that wants a quorum passes its own.
        quorumService ??= new StubAnalysisApprovalQuorumService();

        var sut = new AnalysisController(
            new StubTenantContextAccessor { TenantContext = BuildTenant() }, // tenantContextAccessor
            null!,            // documentRepository
            resultRepo,       // analysisResultRepository
            analysisRepo,     // analysisRepository
            null!,            // claudeService
            null!,            // cashFlowReconciliationService
            null!,            // cashFlowNarrativeService
            null!,            // alertRatioReconciliationService
            entityRepo,       // entityRepository
            userRepo,         // userRepository
            null!,            // chatRepository
            creditLineRepo,   // creditLineRepository
            operationRepo,    // operationRepository
            audit,            // auditService
            null!,            // apiCallLogService
            null!,            // excelCashFlowParser
            null!,            // billingOptions
            null!,            // tenantBillingConfigService
            limitRepo,        // userApprovalLimitRepository
            notifier,         // decisionNotifier
            quorumService,    // approvalQuorumService
            new Api.Web.FinanceIA.Tests.Legajo.FakeLegajoDocumentRepository(),
            null!,            // analysisPipeline
            null!,            // analysisJobQueue
            null!,            // analysisRunRegistry
            null!,            // analysisRunStateRepository
            personAnalyses ?? new Api.Web.FinanceIA.Tests.PersonAnalysis.StubPersonAnalysisRepository(),
            logger ?? NullLogger<AnalysisController>.Instance);

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("uid", FirebaseUid) }, "Test"))
        };
        sut.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return sut;
    }

    // ── Tests ───────────────────────────────────────────────────────────────
    [Fact]
    public async Task Decide_ApprovedToRejected_WithActiveCupo_Returns409AndNeverWrites()
    {
        var cupoId   = Guid.NewGuid();
        var analysis = BuildAnalysis("Approved");
        var resultRepo   = new StubAnalysisResultRepository { ResultToReturn = BuildResult(analysis.AnalysisId) };
        var analysisRepo = new StubAnalysisRepository { AnalysisToReturn = analysis };
        var creditLines  = new StubCreditLineRepository { ActiveCupoIds = new[] { cupoId } };
        var sut = BuildSut(resultRepo, analysisRepo, creditLines, new StubOperationRepository(), new StubAuditService());

        var result = await sut.DecideAsync(resultRepo.ResultToReturn!.ResultId, Request("Rejected"), CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(409, obj.StatusCode);
        // The worst possible outcome would be a 409 that still mutated the row.
        Assert.False(analysisRepo.UpdateCalled);
        // The Portal links to the cupo from a structured extension, never by parsing the detail.
        var pd  = Assert.IsAssignableFrom<ProblemDetails>(obj.Value);
        var ids = Assert.IsType<string[]>(pd.Extensions["affectedCreditLineIds"]);
        Assert.Contains(cupoId.ToString(), ids);
    }

    [Fact]
    public async Task Decide_ApprovedToRejected_WithOnlyCanceladaOperationAndNoCupo_IsAllowed()
    {
        var analysis = BuildAnalysis("Approved");
        var resultRepo   = new StubAnalysisResultRepository { ResultToReturn = BuildResult(analysis.AnalysisId) };
        var analysisRepo = new StubAnalysisRepository { AnalysisToReturn = analysis };
        var creditLines  = new StubCreditLineRepository { ActiveCupoIds = Array.Empty<Guid>() };
        // No active cupo, and the only operation is Cancelada -> HasNonCancelled returns false -> allowed.
        var operations   = new StubOperationRepository { HasNonCancelledToReturn = false };
        var sut = BuildSut(resultRepo, analysisRepo, creditLines, operations, new StubAuditService());

        var result = await sut.DecideAsync(resultRepo.ResultToReturn!.ResultId, Request("Rejected"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.True(analysisRepo.UpdateCalled);
        Assert.Equal("Rejected", analysisRepo.Updated!.Status);
    }

    [Fact]
    public async Task Decide_ApprovedToRejected_WithNoDependents_IsAllowed()
    {
        var analysis = BuildAnalysis("Approved");
        var resultRepo   = new StubAnalysisResultRepository { ResultToReturn = BuildResult(analysis.AnalysisId) };
        var analysisRepo = new StubAnalysisRepository { AnalysisToReturn = analysis };
        var sut = BuildSut(resultRepo, analysisRepo, new StubCreditLineRepository(), new StubOperationRepository(), new StubAuditService());

        var result = await sut.DecideAsync(resultRepo.ResultToReturn!.ResultId, Request("Rejected"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.True(analysisRepo.UpdateCalled);
        Assert.Equal("Rejected", analysisRepo.Updated!.Status);
    }

    [Fact]
    public async Task Decide_ApprovedToRejected_WithAllSuspendedCuposNoOperations_IsAllowedAndPersisted()
    {
        // Regression: every dependent cupo is Suspended (so the ACTIVE-only set is empty) and no
        // operation is live. Rejection must now SUCCEED and persist — this is the dead end the change
        // fixes. The credit-line stub's write members throw, so a passing run also proves that
        // rejecting never touched a cupo (provenance / OriginAnalysisId left intact).
        var analysis = BuildAnalysis("Approved");
        var resultRepo   = new StubAnalysisResultRepository { ResultToReturn = BuildResult(analysis.AnalysisId) };
        var analysisRepo = new StubAnalysisRepository { AnalysisToReturn = analysis };
        var creditLines  = new StubCreditLineRepository { ActiveCupoIds = Array.Empty<Guid>() };
        var operations   = new StubOperationRepository { HasNonCancelledToReturn = false };
        var sut = BuildSut(resultRepo, analysisRepo, creditLines, operations, new StubAuditService());

        var result = await sut.DecideAsync(resultRepo.ResultToReturn!.ResultId, Request("Rejected"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.True(analysisRepo.UpdateCalled);
        Assert.Equal("Rejected", analysisRepo.Updated!.Status);
    }

    [Fact]
    public async Task Decide_ApprovedToRejected_WithSuspendedCuposAndLiveOperation_Returns409()
    {
        // No ACTIVE cupo (all dependents are suspended) but a non-cancelled operation is live -> still
        // 409, because a live operation cannot be undone by rejecting. affectedCreditLineIds is the
        // ALWAYS-emitted empty array (no cupo to act on); the analysis is never written.
        var analysis = BuildAnalysis("Approved");
        var resultRepo   = new StubAnalysisResultRepository { ResultToReturn = BuildResult(analysis.AnalysisId) };
        var analysisRepo = new StubAnalysisRepository { AnalysisToReturn = analysis };
        var creditLines  = new StubCreditLineRepository { ActiveCupoIds = Array.Empty<Guid>() };
        var operations   = new StubOperationRepository { HasNonCancelledToReturn = true };
        var sut = BuildSut(resultRepo, analysisRepo, creditLines, operations, new StubAuditService());

        var result = await sut.DecideAsync(resultRepo.ResultToReturn!.ResultId, Request("Rejected"), CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(409, obj.StatusCode);
        Assert.False(analysisRepo.UpdateCalled);
        var pd  = Assert.IsAssignableFrom<ProblemDetails>(obj.Value);
        var ids = Assert.IsType<string[]>(pd.Extensions["affectedCreditLineIds"]);
        Assert.Empty(ids);
        // The detail must point at the operation (wait for it to settle), not at suspending a cupo.
        Assert.Contains("operación vigente", pd.Detail);
    }

    [Fact]
    public async Task Decide_ApprovedToRejected_WithActiveCupoAndLiveOperation_Returns409()
    {
        // Both causes present: the active cupo id is surfaced for the analyst to suspend, and the
        // detail also flags the live operation so following it actually clears the guard.
        var cupoId   = Guid.NewGuid();
        var analysis = BuildAnalysis("Approved");
        var resultRepo   = new StubAnalysisResultRepository { ResultToReturn = BuildResult(analysis.AnalysisId) };
        var analysisRepo = new StubAnalysisRepository { AnalysisToReturn = analysis };
        var creditLines  = new StubCreditLineRepository { ActiveCupoIds = new[] { cupoId } };
        var operations   = new StubOperationRepository { HasNonCancelledToReturn = true };
        var sut = BuildSut(resultRepo, analysisRepo, creditLines, operations, new StubAuditService());

        var result = await sut.DecideAsync(resultRepo.ResultToReturn!.ResultId, Request("Rejected"), CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(409, obj.StatusCode);
        Assert.False(analysisRepo.UpdateCalled);
        var pd  = Assert.IsAssignableFrom<ProblemDetails>(obj.Value);
        var ids = Assert.IsType<string[]>(pd.Extensions["affectedCreditLineIds"]);
        Assert.Contains(cupoId.ToString(), ids);
        Assert.Contains("operación vigente", pd.Detail);
    }

    [Fact]
    public async Task Decide_PendingReviewToRejected_IsUnchanged()
    {
        var analysis = BuildAnalysis("PendingReview");
        var resultRepo   = new StubAnalysisResultRepository { ResultToReturn = BuildResult(analysis.AnalysisId) };
        var analysisRepo = new StubAnalysisRepository { AnalysisToReturn = analysis };
        // Even with an active dependent cupo, a non-Approved analysis is outside the guard entirely.
        var creditLines  = new StubCreditLineRepository { ActiveCupoIds = new[] { Guid.NewGuid() } };
        var sut = BuildSut(resultRepo, analysisRepo, creditLines, new StubOperationRepository(), new StubAuditService());

        var result = await sut.DecideAsync(resultRepo.ResultToReturn!.ResultId, Request("Rejected"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.True(analysisRepo.UpdateCalled);
        Assert.Equal("Rejected", analysisRepo.Updated!.Status);
    }

    [Fact]
    public async Task Decide_PendingReviewToApproved_IsUnchanged()
    {
        var analysis = BuildAnalysis("PendingReview");
        var resultRepo   = new StubAnalysisResultRepository { ResultToReturn = BuildResult(analysis.AnalysisId) };
        var analysisRepo = new StubAnalysisRepository { AnalysisToReturn = analysis };
        var sut = BuildSut(resultRepo, analysisRepo, new StubCreditLineRepository(), new StubOperationRepository(), new StubAuditService());

        var result = await sut.DecideAsync(resultRepo.ResultToReturn!.ResultId, Request("Approved"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.True(analysisRepo.UpdateCalled);
        Assert.Equal("Approved", analysisRepo.Updated!.Status);
    }

    // ── Two-step routing: role + per-currency approval limit ────────────────────
    //
    // The matrix that decides WHICH of the three acts a decision becomes. The cupo guard above is
    // orthogonal to all of it and is left alone; these fix the routing itself, where a mistake
    // means either a credit committed without the second signature it needed, or an analyst
    // permanently unable to approve anything.

    private static User Caller(string role, string displayName = "Caller") => new()
    {
        UserId      = Guid.NewGuid(),
        FirebaseUid = FirebaseUid,
        DisplayName = displayName,
        Role        = role
    };

    private static (StubAnalysisResultRepository Results, StubAnalysisRepository Analyses) Fixture(string status)
    {
        var analysis = BuildAnalysis(status);
        return (new StubAnalysisResultRepository { ResultToReturn = BuildResult(analysis.AnalysisId) },
                new StubAnalysisRepository { AnalysisToReturn = analysis });
    }

    [Fact]
    public async Task Decide_Analyst_WithinLimit_ApprovesDirectlyAndWritesFinalRecord()
    {
        var caller = Caller(Roles.Analyst);
        var (results, analyses) = Fixture("PendingReview");
        var limits = StubUserApprovalLimitRepository.WithLimit(caller.UserId, "ARS", true, 500_000m);
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, limits);

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Approved", 400_000m, "ARS"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var saved = analyses.Updated!;
        Assert.Equal("Approved", saved.Status);
        Assert.Equal(caller.UserId, saved.ReviewedBy);
        Assert.Equal(400_000m, saved.DecisionAmount);
        Assert.Equal("ARS", saved.DecisionCurrency);
        // One step: nothing was pre-approved, so that record must stay empty.
        Assert.Null(saved.PreApprovedBy);
        Assert.Null(saved.PreApprovedAmount);
    }

    [Fact]
    public async Task Decide_Analyst_AtExactlyTheLimit_ApprovesDirectly()
    {
        // The ceiling is inclusive. An off-by-one here silently escalates every decision that lands
        // exactly on a round number, which is most of them.
        var caller = Caller(Roles.Analyst);
        var (results, analyses) = Fixture("PendingReview");
        var limits = StubUserApprovalLimitRepository.WithLimit(caller.UserId, "ARS", true, 500_000m);
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, limits);

        await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Approved", 500_000m, "ARS"), CancellationToken.None);

        Assert.Equal("Approved", analyses.Updated!.Status);
    }

    [Fact]
    public async Task Decide_Analyst_AboveLimit_PreApprovesAndWritesOnlyPreApprovalRecord()
    {
        var caller = Caller(Roles.Analyst);
        var (results, analyses) = Fixture("PendingReview");
        var limits = StubUserApprovalLimitRepository.WithLimit(caller.UserId, "ARS", true, 500_000m);
        var audit  = new StubAuditService();
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           audit, caller, limits);

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Approved", 500_001m, "ARS"), CancellationToken.None);

        // Not an error — the normal delegation path.
        Assert.IsType<OkObjectResult>(result);
        var saved = analyses.Updated!;
        Assert.Equal("PreApproved", saved.Status);
        Assert.Equal(caller.UserId, saved.PreApprovedBy);
        Assert.Equal(500_001m, saved.PreApprovedAmount);
        Assert.Equal("ARS", saved.PreApprovedCurrency);
        // Nothing is committed yet: the final record must stay empty or the analysis would read as
        // approved by the very person who was not allowed to approve it.
        Assert.Null(saved.ReviewedBy);
        Assert.Null(saved.DecisionAmount);
        Assert.Equal("Analysis.PreApproved", audit.Action);
    }

    [Fact]
    public async Task Decide_Analyst_WithNoLimitRowForCurrency_PreApproves()
    {
        // A limit in ARS says nothing about USD. Absence is a denial, never an unlimited default.
        var caller = Caller(Roles.Analyst);
        var (results, analyses) = Fixture("PendingReview");
        var limits = StubUserApprovalLimitRepository.WithLimit(caller.UserId, "ARS", true, 10_000_000m);
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, limits);

        await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Approved", 1m, "USD"), CancellationToken.None);

        Assert.Equal("PreApproved", analyses.Updated!.Status);
    }

    [Fact]
    public async Task Decide_Analyst_WithNoLimitsAtAll_PreApproves()
    {
        var caller = Caller(Roles.Analyst);
        var (results, analyses) = Fixture("PendingReview");
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, StubUserApprovalLimitRepository.Empty());

        await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Approved", 1m, "ARS"), CancellationToken.None);

        Assert.Equal("PreApproved", analyses.Updated!.Status);
    }

    [Fact]
    public async Task Decide_Analyst_WithCanApproveFalse_PreApprovesEvenBelowMaxAmount()
    {
        // CanApprove is the gate; MaxAmount only bounds it. A row that stores a ceiling but denies
        // approval must not approve just because the amount fits under the ceiling.
        var caller = Caller(Roles.Analyst);
        var (results, analyses) = Fixture("PendingReview");
        var limits = StubUserApprovalLimitRepository.WithLimit(caller.UserId, "ARS", false, 999_999m);
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, limits);

        await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Approved", 1m, "ARS"), CancellationToken.None);

        Assert.Equal("PreApproved", analyses.Updated!.Status);
    }

    [Fact]
    public async Task Decide_Analyst_WithNullMaxAmountAndCanApprove_ApprovesAnyAmount()
    {
        // NULL MaxAmount with CanApprove is the explicit no-ceiling case, distinct from no row.
        var caller = Caller(Roles.Analyst);
        var (results, analyses) = Fixture("PendingReview");
        var limits = StubUserApprovalLimitRepository.WithLimit(caller.UserId, "ARS", true, null);
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, limits);

        await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Approved", 999_999_999m, "ARS"), CancellationToken.None);

        Assert.Equal("Approved", analyses.Updated!.Status);
    }

    [Fact]
    public async Task Decide_Analyst_OnPreApprovedAnalysis_Returns403AndNeverWrites()
    {
        // The whole point of the second signature: an analyst cannot supply it, not even their own.
        var caller = Caller(Roles.Analyst);
        var (results, analyses) = Fixture("PreApproved");
        var limits = StubUserApprovalLimitRepository.WithLimit(caller.UserId, "ARS", true, 10_000_000m);
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, limits);

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Approved", 1m, "ARS"), CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, obj.StatusCode);
        Assert.False(analyses.UpdateCalled);
    }

    [Fact]
    public async Task Decide_RiskManager_ConfirmsWithModifiedAmount_PreservesPreApprovalRecord()
    {
        var preApprover = Guid.NewGuid();
        var caller      = Caller(Roles.RiskManager, "Risk Manager");
        var (results, analyses) = Fixture("PreApproved");
        analyses.AnalysisToReturn!.PreApprovedBy       = preApprover;
        analyses.AnalysisToReturn.PreApprovedAmount    = 900_000m;
        analyses.AnalysisToReturn.PreApprovedCurrency  = "ARS";
        var audit = new StubAuditService();

        // Empty limits: a confirmer must never consult this table, so an empty one proves it.
        var quorum = new StubAnalysisApprovalQuorumService();
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           audit, caller, StubUserApprovalLimitRepository.Empty(), quorum);

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Approved", 750_000m, "ARS"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, quorum.VoteCallCount);
        var saved = Settled(analyses);
        Assert.Equal("Approved", saved.Status);
        Assert.Equal(caller.UserId, saved.ReviewedBy);
        Assert.Equal(750_000m, saved.DecisionAmount);
        // The first signature survives, and with the amount that was originally proposed — this is
        // what makes "the RM lowered it" visible at all.
        Assert.Equal(preApprover, saved.PreApprovedBy);
        Assert.Equal(900_000m, saved.PreApprovedAmount);
        Assert.Equal("Analysis.Confirmed", audit.Action);
    }

    // ── Persona humana analysis (DDL 072) ───────────────────────────────────────
    //
    // A persona analysis has no document and no balance: its result row carries only the required
    // columns. The decision path reads neither, so the same routing — limit, pre-approval, quorum —
    // must hold for it unchanged.

    private static (StubAnalysisResultRepository Results, StubAnalysisRepository Analyses) PersonaFixture(string status)
    {
        var analysis = BuildAnalysis(status);
        analysis.AnalysisKind = AnalysisKinds.PersonaHumana;
        analysis.DocumentId   = null;
        var result = new AnalysisResult
        {
            ResultId         = Guid.NewGuid(),
            AnalysisId       = analysis.AnalysisId,
            TenantId         = Tenant,
            AiInputHash      = new string('a', 64),
            AiOutputHash     = new string('b', 64),
            ValidationStatus = "Passed",
            EnrichmentStatus = "Complete"
        };
        return (new StubAnalysisResultRepository { ResultToReturn = result },
                new StubAnalysisRepository { AnalysisToReturn = analysis });
    }

    /// <summary>A persona repository holding the solicitante of <paramref name="analysis"/>, with or without a Rechazo filter.</summary>
    private static Api.Web.FinanceIA.Tests.PersonAnalysis.StubPersonAnalysisRepository PersonaRepo(
        Analysis analysis, bool rechazoSugerido, params (string Key, decimal Value)[] config)
    {
        var repo = new Api.Web.FinanceIA.Tests.PersonAnalysis.StubPersonAnalysisRepository();
        var filters = new List<PersonFilterResult>
        {
            new()
            {
                Id = "bcra_situacion", Evidence = "x", Source = "BCRA",
                Outcome = rechazoSugerido ? PersonFilterOutcomes.Rechazo : PersonFilterOutcomes.Ok
            }
        };
        repo.Participants.Add(new AnalysisParticipant
        {
            ParticipantId     = Guid.NewGuid(),
            TenantId          = Tenant,
            AnalysisId        = analysis.AnalysisId,
            PersonLegajoId    = Guid.NewGuid(),
            Role              = ParticipantRoles.Solicitante,
            AmountAtCharge    = 1_000m,
            Currency          = "ARS",
            FiltersJson       = Api.Web.FinanceIA.Servicios.PersonEvaluation.PersonEvaluationJson.Serialize(filters),
            EvaluationVersion = "0.1"
        });
        foreach (var (key, value) in config)
            repo.Config[key] = value;
        return repo;
    }

    /// <summary>A persona approval carrying the reason and both confirmations.</summary>
    private static AnalysisDecisionRequest PersonaApproval(decimal amount, string? notes = "Contragarantía hipotecaria verificada",
        bool? counterGuarantees = true, bool? fdrLimit = true)
    {
        var request = Request("Approved", amount, "ARS");
        request.ReviewNotes                = notes;
        request.CounterGuaranteesConfirmed = counterGuarantees;
        request.FdrLimitConfirmed          = fdrLimit;
        return request;
    }

    private static ProblemDetails CodedProblemOf(IActionResult result, int status, string errorCode)
    {
        var obj = Assert.IsAssignableFrom<ObjectResult>(result);
        var problem = Assert.IsType<ProblemDetails>(obj.Value);
        Assert.Equal(status, obj.StatusCode ?? problem.Status);
        Assert.Equal(errorCode, problem.Extensions["errorCode"]);
        return problem;
    }

    [Fact]
    public async Task Decide_PersonaAnalysis_WithinLimit_ApprovesDirectly_AndKeepsItsKind()
    {
        var caller = Caller(Roles.Analyst);
        var (results, analyses) = PersonaFixture("PendingReview");
        var limits = StubUserApprovalLimitRepository.WithLimit(caller.UserId, "ARS", true, 500_000m);
        var persona = PersonaRepo(analyses.AnalysisToReturn!, rechazoSugerido: false);
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, limits, personAnalyses: persona);

        var result = await sut.DecideAsync(results.ResultToReturn!.ResultId, PersonaApproval(400_000m), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<AnalysisResultResponse>(ok.Value);
        Assert.Equal(AnalysisKinds.PersonaHumana, response.AnalysisKind);
        Assert.Equal(Guid.Empty, response.DocumentId);
        Assert.Null(response.AiSuggestedScore);
        // The stored "Passed" is a placeholder the column constraint forces; it is never shown as an approval.
        Assert.Equal(string.Empty, response.ValidationStatus);
        Assert.NotNull(response.PersonEvaluation!.DecisionRules);
        Assert.True(response.ConfirmationChecks!.CounterGuaranteesConfirmed);

        var saved = analyses.Updated!;
        Assert.Equal("Approved", saved.Status);
        Assert.Equal(400_000m, saved.DecisionAmount);
        Assert.Null(saved.DocumentId);
        Assert.Null(saved.PreApprovalChecksJson);
        var checks = Api.Web.FinanceIA.Servicios.PersonEvaluation.PersonEvaluationJson
            .TryDeserialize<AnalysisDecisionChecks>(saved.ConfirmationChecksJson)!;
        Assert.True(checks.FdrLimitConfirmed);
        Assert.Equal("Contragarantía hipotecaria verificada", checks.Reason);
        Assert.Equal(caller.UserId, checks.By);
    }

    [Fact]
    public async Task Decide_PersonaAnalysis_AboveLimit_PreApproves_AndRecordsThePreApprovalChecks()
    {
        var caller = Caller(Roles.Analyst);
        var (results, analyses) = PersonaFixture("PendingReview");
        var limits = StubUserApprovalLimitRepository.WithLimit(caller.UserId, "ARS", true, 500_000m);
        var audit  = new StubAuditService();
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           audit, caller, limits, personAnalyses: PersonaRepo(analyses.AnalysisToReturn!, false));

        await sut.DecideAsync(results.ResultToReturn!.ResultId, PersonaApproval(2_000_000m), CancellationToken.None);

        var saved = analyses.Updated!;
        Assert.Equal("PreApproved", saved.Status);
        Assert.Equal(2_000_000m, saved.PreApprovedAmount);
        Assert.Null(saved.DecisionAmount);
        Assert.NotNull(saved.PreApprovalChecksJson);
        Assert.Null(saved.ConfirmationChecksJson);
        Assert.Equal("Analysis.PreApproved", audit.Action);
    }

    [Fact]
    public async Task Decide_PersonaAnalysis_PreApproved_IsConfirmedThroughTheQuorumEngine_AndRecordsTheChecks()
    {
        var caller = Caller(Roles.RiskManager, "Risk Manager");
        var (results, analyses) = PersonaFixture("PreApproved");
        analyses.AnalysisToReturn!.PreApprovedBy      = Guid.NewGuid();
        analyses.AnalysisToReturn.PreApprovedAmount   = 2_000_000m;
        analyses.AnalysisToReturn.PreApprovedCurrency = "ARS";
        var quorum  = new StubAnalysisApprovalQuorumService();
        var persona = PersonaRepo(analyses.AnalysisToReturn, rechazoSugerido: false);
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, StubUserApprovalLimitRepository.Empty(), quorum, persona);

        var result = await sut.DecideAsync(results.ResultToReturn!.ResultId, PersonaApproval(1_800_000m), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, quorum.VoteCallCount);
        var saved = Settled(analyses);
        Assert.Equal("Approved", saved.Status);
        Assert.Equal(1_800_000m, saved.DecisionAmount);
        Assert.Equal(AnalysisKinds.PersonaHumana, saved.AnalysisKind);
        Assert.Equal(analyses.AnalysisToReturn.AnalysisId, Assert.Single(persona.ConfirmationChecksWrites).AnalysisId);
    }

    [Fact]
    public async Task Decide_PersonaAnalysis_WithoutAReason_Is400ReasonRequired_AndWritesNothing()
    {
        var caller = Caller(Roles.Admin);
        var (results, analyses) = PersonaFixture("PendingReview");
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, personAnalyses: PersonaRepo(analyses.AnalysisToReturn!, false));

        var result = await sut.DecideAsync(results.ResultToReturn!.ResultId, PersonaApproval(1_000m, notes: "  "), CancellationToken.None);

        CodedProblemOf(result, 400, "reason_required");
        Assert.False(analyses.UpdateCalled);
    }

    /// <summary>
    /// M10 Phase 5a-2: with fiadores whose aggregate coverage is below the minimum the approval still works,
    /// but only with a reason — even where the tenant waives it — and the refusal says why.
    /// </summary>
    [Theory]
    [InlineData(1_000, false)]   // 500 executable of 1.000 requested: 50 % < 100 %
    [InlineData(2_000, true)]    // 1.000 of 1.000: 100 %
    public async Task Decide_PersonaAnalysis_FiadoresBelowTheMinimum_RequireAReason(int inmueble, bool meetsMin)
    {
        var caller = Caller(Roles.Admin);
        var (results, analyses) = PersonaFixture("PendingReview");
        var persona = PersonaRepo(analyses.AnalysisToReturn!, false, ("pf_aprobacion_requiere_motivo", 0m));
        persona.Participants.Add(Api.Web.FinanceIA.Tests.PersonAnalysis.PersonFiadorFixtures.FiadorRow(
            analyses.AnalysisToReturn!.AnalysisId, Tenant, 1_000m, inmueble));
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, personAnalyses: persona);

        var result = await sut.DecideAsync(results.ResultToReturn!.ResultId, PersonaApproval(1_000m, notes: null), CancellationToken.None);

        if (meetsMin)
        {
            Assert.IsType<OkObjectResult>(result);
            return;
        }

        var problem = CodedProblemOf(result, 400, "reason_required");
        Assert.Equal(true, problem.Extensions["counterGuaranteeBelowMin"]);
        Assert.Contains("cubre el 50 % del monto solicitado, por debajo del mínimo de 100 %", problem.Detail);
        Assert.False(analyses.UpdateCalled);
    }

    /// <summary>
    /// "Reanalizar": a re-evaluation overwrites the participant rows in place, and the decision reads whatever
    /// is stored. Here the fiador's coverage met the minimum at creation; the re-evaluation, written through the
    /// real repository, drops it to 50 %, and the PATCH now demands a reason for exactly that.
    /// </summary>
    [Fact]
    public async Task Decide_PersonaAnalysis_AfterAReevaluation_ReadsTheNewEvaluation()
    {
        var caller = Caller(Roles.Admin);
        var (results, analyses) = PersonaFixture("PendingReview");
        var analysis = analyses.AnalysisToReturn!;
        var persona = PersonaRepo(analysis, false, ("pf_aprobacion_requiere_motivo", 0m));
        var fiador = Api.Web.FinanceIA.Tests.PersonAnalysis.PersonFiadorFixtures.FiadorRow(analysis.AnalysisId, Tenant, 1_000m, 2_000m);
        persona.Participants.Add(fiador);

        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Api.Web.FinanceIA.Data.FinanciaDbContext>()
            .UseInMemoryDatabase($"decide-reevaluated-{Guid.NewGuid()}").Options;
        await using (var seed = new Api.Web.FinanceIA.Data.FinanciaDbContext(options))
        {
            seed.Analyses.Add(analysis);
            seed.AnalysisResults.Add(results.ResultToReturn!);
            seed.AnalysisParticipants.AddRange(persona.Participants);
            seed.ChangeTracker.DetectChanges();
            await seed.SaveChangesAsync();
        }

        var before = Api.Web.FinanceIA.Servicios.PersonEvaluation.PersonEvaluationJson.DecisionRules(
            persona.Config, persona.Participants[0], [fiador]);
        Assert.False(before.CounterGuaranteeBelowMin);

        // The re-evaluation: the fiador's legajo now holds half the value (1.000 of market value executes 500).
        var lowered = Api.Web.FinanceIA.Tests.PersonAnalysis.PersonFiadorFixtures.FiadorRow(analysis.AnalysisId, Tenant, 1_000m, 1_000m);
        await using (var db = new Api.Web.FinanceIA.Data.FinanciaDbContext(options))
        {
            var applied = await new AnalysisRescoreRepository(db, NullLogger<AnalysisRescoreRepository>.Instance)
                .TryApplyAsync(new PersonAnalysisRescoreWrite
                {
                    ResultId = results.ResultToReturn!.ResultId, AnalysisId = analysis.AnalysisId, TenantId = Tenant,
                    ExpectedRescoreCount = 0, RescoredAt = DateTime.UtcNow, RescoredBy = caller.UserId,
                    AiInputHash = new string('c', 64), AiOutputHash = new string('d', 64), EnrichmentStatus = "Complete",
                    Participants = persona.Participants.Select(p => new PersonParticipantReevaluation
                    {
                        ParticipantId        = p.ParticipantId,
                        DocumentVersionsJson = p.DocumentVersionsJson,
                        FiltersJson          = p.FiltersJson,
                        IndicatorsJson       = p.IndicatorsJson,
                        CoverageJson         = p.ParticipantId == fiador.ParticipantId ? lowered.CoverageJson : p.CoverageJson,
                        EvaluationVersion    = p.EvaluationVersion,
                        CreatedAt            = p.CreatedAt
                    }).ToList(),
                    History = new AnalysisRescore
                    {
                        RescoreId = Guid.NewGuid(), TenantId = Tenant, AnalysisId = analysis.AnalysisId,
                        ResultId = results.ResultToReturn!.ResultId, RescoredAt = DateTime.UtcNow, RescoredBy = caller.UserId,
                        PreviousEnrichmentStatus = "Complete", NewEnrichmentStatus = "Complete",
                        PreviousValuesJson = "{}", SourcesJson = "[]"
                    }
                }, CancellationToken.None);
            Assert.Equal(AnalysisRescoreWriteOutcome.Applied, applied);
        }

        // The decision reads the rows as they are stored now.
        await using (var read = new Api.Web.FinanceIA.Data.FinanciaDbContext(options))
        {
            persona.Participants.Clear();
            persona.Participants.AddRange(await read.AnalysisParticipants.AsNoTracking().ToListAsync());
        }
        Assert.Equal(lowered.CoverageJson, persona.Participants.Single(p => p.ParticipantId == fiador.ParticipantId).CoverageJson);

        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, personAnalyses: persona);
        var result = await sut.DecideAsync(results.ResultToReturn!.ResultId, PersonaApproval(1_000m, notes: null), CancellationToken.None);

        var problem = CodedProblemOf(result, 400, "reason_required");
        Assert.Equal(true, problem.Extensions["counterGuaranteeBelowMin"]);
        Assert.Contains("cubre el 50 % del monto solicitado", problem.Detail);
        Assert.False(analyses.UpdateCalled);
    }

    [Fact]
    public async Task Decide_PersonaAnalysis_FiadoresBelowTheMinimum_WithAReason_Approves()
    {
        var caller = Caller(Roles.Admin);
        var (results, analyses) = PersonaFixture("PendingReview");
        var persona = PersonaRepo(analyses.AnalysisToReturn!, false);
        persona.Participants.Add(Api.Web.FinanceIA.Tests.PersonAnalysis.PersonFiadorFixtures.FiadorRow(
            analyses.AnalysisToReturn!.AnalysisId, Tenant, 1_000m, 1_000m));
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, personAnalyses: persona);

        var result = await sut.DecideAsync(results.ResultToReturn!.ResultId, PersonaApproval(1_000m), CancellationToken.None);

        var body = Assert.IsType<AnalysisResultResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("Approved", analyses.Updated!.Status);
        Assert.True(body.PersonEvaluation!.DecisionRules!.CounterGuaranteeBelowMin);
        Assert.Equal(0.5m, body.PersonEvaluation.CounterGuaranteeCoverage!.Ratio);
    }

    [Fact]
    public async Task Decide_PersonaAnalysis_ReasonRuleCanBeTurnedOff()
    {
        var caller = Caller(Roles.Admin);
        var (results, analyses) = PersonaFixture("PendingReview");
        var persona = PersonaRepo(analyses.AnalysisToReturn!, false, ("pf_aprobacion_requiere_motivo", 0m));
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, personAnalyses: persona);

        var result = await sut.DecideAsync(results.ResultToReturn!.ResultId, PersonaApproval(1_000m, notes: null), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Decide_PersonaAnalysis_WithoutBothConfirmations_Is400(bool? counterGuarantees, bool? fdrLimit)
    {
        var caller = Caller(Roles.Admin);
        var (results, analyses) = PersonaFixture("PendingReview");
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, personAnalyses: PersonaRepo(analyses.AnalysisToReturn!, false));

        var result = await sut.DecideAsync(results.ResultToReturn!.ResultId,
            PersonaApproval(1_000m, counterGuarantees: counterGuarantees, fdrLimit: fdrLimit), CancellationToken.None);

        CodedProblemOf(result, 400, "confirmations_required");
        Assert.False(analyses.UpdateCalled);
    }

    [Fact]
    public async Task Decide_PersonaRechazoSugerido_ForcesAPreApproval_EvenForAnAdmin()
    {
        var caller = Caller(Roles.Admin);
        var (results, analyses) = PersonaFixture("PendingReview");
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, personAnalyses: PersonaRepo(analyses.AnalysisToReturn!, rechazoSugerido: true));

        var result = await sut.DecideAsync(results.ResultToReturn!.ResultId, PersonaApproval(1_000m), CancellationToken.None);

        var response = Assert.IsType<AnalysisResultResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("PreApproved", analyses.Updated!.Status);
        Assert.Equal(caller.UserId, analyses.Updated.PreApprovedBy);
        Assert.Null(analyses.Updated.DecisionAmount);
        Assert.True(response.PersonEvaluation!.DecisionRules!.RequiresPreApproval);
    }

    [Fact]
    public async Task Decide_PersonaRechazoSugerido_RuleOff_AnAdminApprovesDirectly()
    {
        var caller = Caller(Roles.Admin);
        var (results, analyses) = PersonaFixture("PendingReview");
        var persona = PersonaRepo(analyses.AnalysisToReturn!, true, ("pf_rechazo_sugerido_requiere_preaprobacion", 0m));
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, personAnalyses: persona);

        await sut.DecideAsync(results.ResultToReturn!.ResultId, PersonaApproval(1_000m), CancellationToken.None);

        Assert.Equal("Approved", analyses.Updated!.Status);
    }

    [Fact]
    public async Task Decide_PersonaRechazoSugerido_ThePreApproverCannotConfirm()
    {
        var caller = Caller(Roles.RiskManager, "Risk Manager");
        var (results, analyses) = PersonaFixture("PreApproved");
        analyses.AnalysisToReturn!.PreApprovedBy      = caller.UserId;
        analyses.AnalysisToReturn.PreApprovedAmount   = 1_000m;
        analyses.AnalysisToReturn.PreApprovedCurrency = "ARS";
        var quorum = new StubAnalysisApprovalQuorumService();
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, StubUserApprovalLimitRepository.Empty(), quorum,
                           PersonaRepo(analyses.AnalysisToReturn, rechazoSugerido: true));

        var result = await sut.DecideAsync(results.ResultToReturn!.ResultId, PersonaApproval(1_000m), CancellationToken.None);

        CodedProblemOf(result, 403, "self_confirmation_not_allowed");
        Assert.Equal(0, quorum.VoteCallCount);
    }

    [Fact]
    public async Task Decide_PersonaRechazoSugerido_ADifferentConfirmerConfirms()
    {
        var caller = Caller(Roles.RiskManager, "Risk Manager");
        var (results, analyses) = PersonaFixture("PreApproved");
        analyses.AnalysisToReturn!.PreApprovedBy      = Guid.NewGuid();
        analyses.AnalysisToReturn.PreApprovedAmount   = 1_000m;
        analyses.AnalysisToReturn.PreApprovedCurrency = "ARS";
        var quorum = new StubAnalysisApprovalQuorumService();
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, StubUserApprovalLimitRepository.Empty(), quorum,
                           PersonaRepo(analyses.AnalysisToReturn, rechazoSugerido: true));

        var result = await sut.DecideAsync(results.ResultToReturn!.ResultId, PersonaApproval(1_000m), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, quorum.VoteCallCount);
        Assert.Equal("Approved", Settled(analyses).Status);
    }

    // ── Persona humana: no operation a sola firma while a counter-guarantee coverage is required ──

    private static AnalysisDecisionRequest PersonaApprovalWithOperations(decimal amount, bool aSolaFirma)
    {
        var request = PersonaApproval(amount);
        request.Operations = [Operation(type: 5, amount: amount / 2, aSolaFirma: aSolaFirma),
                              Operation(type: 7, amount: amount / 2, requiereContragarantiaReal: true)];
        return request;
    }

    [Fact]
    public async Task Decide_PersonaApproval_WithASolaFirmaOperation_Is400_AndWritesNothing()
    {
        // The production case: counterGuaranteesConfirmed = true beside an operation a sola firma, on the
        // default coverage of 100 %.
        var caller = Caller(Roles.Analyst);
        var (results, analyses) = PersonaFixture("PendingReview");
        var limits  = StubUserApprovalLimitRepository.WithLimit(caller.UserId, "ARS", true, 500_000m);
        var audit   = new StubAuditService();
        var persona = PersonaRepo(analyses.AnalysisToReturn!, rechazoSugerido: false);
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           audit, caller, limits, personAnalyses: persona);

        var result = await sut.DecideAsync(results.ResultToReturn!.ResultId,
            PersonaApprovalWithOperations(400_000m, aSolaFirma: true), CancellationToken.None);

        var problem = CodedProblemOf(result, 400, "person_sola_firma_not_allowed");
        Assert.Equal(
            "En persona humana la contragarantía tiene que cubrir al menos el 100 % del aval: " +
            "ninguna operación puede ser a sola firma.",
            problem.Detail);
        Assert.False(analyses.UpdateCalled);
        Assert.False(audit.LogCalled);
        Assert.Empty(persona.ConfirmationChecksWrites);
    }

    [Fact]
    public async Task Decide_PersonaApproval_WithoutASolaFirmaOperation_IsApprovedAsBefore()
    {
        var caller = Caller(Roles.Analyst);
        var (results, analyses) = PersonaFixture("PendingReview");
        var limits = StubUserApprovalLimitRepository.WithLimit(caller.UserId, "ARS", true, 500_000m);
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, limits,
                           personAnalyses: PersonaRepo(analyses.AnalysisToReturn!, rechazoSugerido: false));

        var result = await sut.DecideAsync(results.ResultToReturn!.ResultId,
            PersonaApprovalWithOperations(400_000m, aSolaFirma: false), CancellationToken.None);

        var response = Assert.IsType<AnalysisResultResponse>(Assert.IsType<OkObjectResult>(result).Value);
        var saved = analyses.Updated!;
        Assert.Equal("Approved", saved.Status);
        Assert.Equal(2, ReadOperations(saved.ConfirmedOperationsJson).Count);
        Assert.NotNull(saved.ConfirmationChecksJson);
        Assert.False(response.SolaFirmaAllowed);
        Assert.Equal(100, response.MinCounterGuaranteeCoveragePct);
    }

    [Fact]
    public async Task Decide_PersonaPreApproval_WithASolaFirmaOperation_Is400_AndWritesNothing()
    {
        var caller = Caller(Roles.Analyst);
        var (results, analyses) = PersonaFixture("PendingReview");
        var limits = StubUserApprovalLimitRepository.WithLimit(caller.UserId, "ARS", true, 500_000m);
        var quorum = new StubAnalysisApprovalQuorumService();
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, limits, quorum,
                           PersonaRepo(analyses.AnalysisToReturn!, rechazoSugerido: false));

        // Above the analyst's limit: without the rule this would be a pre-approval.
        var result = await sut.DecideAsync(results.ResultToReturn!.ResultId,
            PersonaApprovalWithOperations(2_000_000m, aSolaFirma: true), CancellationToken.None);

        CodedProblemOf(result, 400, "person_sola_firma_not_allowed");
        Assert.False(analyses.UpdateCalled);
        Assert.Equal("PendingReview", analyses.AnalysisToReturn!.Status);
        Assert.Null(analyses.AnalysisToReturn.ProposedOperationsJson);
        Assert.Null(analyses.AnalysisToReturn.PreApprovalChecksJson);
    }

    [Fact]
    public async Task Decide_PersonaConfirmation_WithASolaFirmaOperation_Is400_AndCastsNoVote()
    {
        var caller = Caller(Roles.RiskManager, "Risk Manager");
        var (results, analyses) = PersonaFixture("PreApproved");
        analyses.AnalysisToReturn!.PreApprovedBy      = Guid.NewGuid();
        analyses.AnalysisToReturn.PreApprovedAmount   = 2_000_000m;
        analyses.AnalysisToReturn.PreApprovedCurrency = "ARS";
        var quorum  = new StubAnalysisApprovalQuorumService();
        var persona = PersonaRepo(analyses.AnalysisToReturn, rechazoSugerido: false);
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, StubUserApprovalLimitRepository.Empty(), quorum, persona);

        var result = await sut.DecideAsync(results.ResultToReturn!.ResultId,
            PersonaApprovalWithOperations(1_800_000m, aSolaFirma: true), CancellationToken.None);

        CodedProblemOf(result, 400, "person_sola_firma_not_allowed");
        Assert.Equal(0, quorum.VoteCallCount);
        Assert.False(analyses.UpdateCalled);
        Assert.Empty(persona.ConfirmationChecksWrites);
        Assert.Equal("PreApproved", analyses.AnalysisToReturn.Status);
    }

    [Fact]
    public async Task Decide_PersonaRechazoSugerido_WithASolaFirmaOperation_Is400_BeforeTheForcedPreApproval()
    {
        var caller = Caller(Roles.Admin);
        var (results, analyses) = PersonaFixture("PendingReview");
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller,
                           personAnalyses: PersonaRepo(analyses.AnalysisToReturn!, rechazoSugerido: true));

        var result = await sut.DecideAsync(results.ResultToReturn!.ResultId,
            PersonaApprovalWithOperations(1_000m, aSolaFirma: true), CancellationToken.None);

        CodedProblemOf(result, 400, "person_sola_firma_not_allowed");
        Assert.False(analyses.UpdateCalled);
    }

    [Fact]
    public async Task Decide_PersonaApproval_TenantWithCoverageZero_AllowsASolaFirma()
    {
        var caller = Caller(Roles.Analyst);
        var (results, analyses) = PersonaFixture("PendingReview");
        var limits = StubUserApprovalLimitRepository.WithLimit(caller.UserId, "ARS", true, 500_000m);
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, limits,
                           personAnalyses: PersonaRepo(analyses.AnalysisToReturn!, rechazoSugerido: false,
                                                       ("pf_contragarantia_cobertura_min_pct", 0m)));

        var result = await sut.DecideAsync(results.ResultToReturn!.ResultId,
            PersonaApprovalWithOperations(400_000m, aSolaFirma: true), CancellationToken.None);

        var response = Assert.IsType<AnalysisResultResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("Approved", analyses.Updated!.Status);
        Assert.True(ReadOperations(analyses.Updated.ConfirmedOperationsJson)[0].ASolaFirma);
        Assert.True(response.SolaFirmaAllowed);
        Assert.Equal(0, response.MinCounterGuaranteeCoveragePct);
    }

    [Fact]
    public async Task Decide_PersonaApproval_TheDetailNamesTheTenantsCoverage()
    {
        var caller = Caller(Roles.Admin);
        var (results, analyses) = PersonaFixture("PendingReview");
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller,
                           personAnalyses: PersonaRepo(analyses.AnalysisToReturn!, rechazoSugerido: false,
                                                       ("pf_contragarantia_cobertura_min_pct", 50m)));

        var result = await sut.DecideAsync(results.ResultToReturn!.ResultId,
            PersonaApprovalWithOperations(1_000m, aSolaFirma: true), CancellationToken.None);

        var problem = CodedProblemOf(result, 400, "person_sola_firma_not_allowed");
        Assert.Contains("al menos el 50 % del aval", problem.Detail);
        Assert.False(analyses.UpdateCalled);
    }

    [Fact]
    public async Task Decide_PersonaRejection_CarryingASolaFirmaOperations_IsUnaffected()
    {
        var caller = Caller(Roles.Analyst);
        var (results, analyses) = PersonaFixture("PendingReview");
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller,
                           personAnalyses: PersonaRepo(analyses.AnalysisToReturn!, rechazoSugerido: false));

        var result = await sut.DecideAsync(results.ResultToReturn!.ResultId,
            Request("Rejected", operations: [Operation(aSolaFirma: true)]), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Rejected", analyses.Updated!.Status);
    }

    [Fact]
    public async Task Decide_BalanceApproval_WithASolaFirmaOperation_IsUnchanged()
    {
        var caller = Caller(Roles.Admin);
        var (results, analyses) = Fixture("PendingReview");
        var persona = new Api.Web.FinanceIA.Tests.PersonAnalysis.StubPersonAnalysisRepository();
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, personAnalyses: persona);

        var result = await sut.DecideAsync(results.ResultToReturn!.ResultId,
            Request("Approved", 1_000m, "ARS", [Operation(amount: 1_000m, aSolaFirma: true)]), CancellationToken.None);

        var response = Assert.IsType<AnalysisResultResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("Approved", analyses.Updated!.Status);
        Assert.True(Assert.Single(ReadOperations(analyses.Updated.ConfirmedOperationsJson)).ASolaFirma);
        Assert.Null(response.SolaFirmaAllowed);
        Assert.Null(response.MinCounterGuaranteeCoveragePct);
    }

    [Fact]
    public async Task Decide_PersonaRejection_NeedsNoReasonNorConfirmations()
    {
        var caller = Caller(Roles.Analyst);
        var (results, analyses) = PersonaFixture("PendingReview");
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, personAnalyses: PersonaRepo(analyses.AnalysisToReturn!, true));

        var result = await sut.DecideAsync(results.ResultToReturn!.ResultId, Request("Rejected"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Rejected", analyses.Updated!.Status);
    }

    [Fact]
    public async Task Decide_BalanceAnalysis_ApprovalStillNeedsNoReasonNorConfirmations()
    {
        var caller = Caller(Roles.Admin);
        var (results, analyses) = Fixture("PendingReview");
        var persona = new Api.Web.FinanceIA.Tests.PersonAnalysis.StubPersonAnalysisRepository();
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, personAnalyses: persona);

        var result = await sut.DecideAsync(results.ResultToReturn!.ResultId, Request("Approved", 1_000m, "ARS"), CancellationToken.None);

        var response = Assert.IsType<AnalysisResultResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("Approved", analyses.Updated!.Status);
        Assert.Null(analyses.Updated.ConfirmationChecksJson);
        Assert.Null(response.PersonEvaluation);
        Assert.Empty(persona.ConfirmationChecksWrites);
    }

    [Fact]
    public async Task Decide_Admin_AboveAnyLimit_ApprovesDirectly()
    {
        var caller = Caller(Roles.Admin, "Administrator");
        var (results, analyses) = Fixture("PendingReview");
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, StubUserApprovalLimitRepository.Empty());

        await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Approved", 999_999_999m, "ARS"), CancellationToken.None);

        Assert.Equal("Approved", analyses.Updated!.Status);
    }

    [Fact]
    public async Task Decide_RiskManager_RejectsPreApproved_BecomesRejected()
    {
        var caller = Caller(Roles.RiskManager);
        var (results, analyses) = Fixture("PreApproved");
        var audit = new StubAuditService();
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           audit, caller, StubUserApprovalLimitRepository.Empty());

        await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Rejected", null, null), CancellationToken.None);

        var saved = analyses.Updated!;
        Assert.Equal("Rejected", saved.Status);
        // A rejection commits nothing; recording an amount here would read as money granted.
        Assert.Null(saved.DecisionAmount);
        Assert.Equal("Analysis.Rejected", audit.Action);
    }

    [Fact]
    public async Task Decide_RiskManager_ReturnsPreApproved_BecomesPendingReview()
    {
        var caller = Caller(Roles.RiskManager);
        var (results, analyses) = Fixture("PreApproved");
        var audit = new StubAuditService();
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           audit, caller, StubUserApprovalLimitRepository.Empty());

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("PendingReview", null, null), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("PendingReview", analyses.Updated!.Status);
        Assert.Equal("Analysis.ReturnedToReview", audit.Action);
    }

    [Fact]
    public async Task Decide_Analyst_CannotReturnToPendingReview_Returns403()
    {
        var caller = Caller(Roles.Analyst);
        var (results, analyses) = Fixture("PendingReview");
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, StubUserApprovalLimitRepository.Empty());

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("PendingReview", null, null), CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, obj.StatusCode);
        Assert.False(analyses.UpdateCalled);
    }

    [Fact]
    public async Task Decide_RiskManager_CannotReturnAnalysisThatIsNotPreApproved_Returns403()
    {
        // Returning for rework is an answer to a pending confirmation, not a general reset.
        var caller = Caller(Roles.RiskManager);
        var (results, analyses) = Fixture("Approved");
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, StubUserApprovalLimitRepository.Empty());

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("PendingReview", null, null), CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, obj.StatusCode);
        Assert.False(analyses.UpdateCalled);
    }

    [Theory]
    [InlineData(null, "ARS")]    // no amount
    [InlineData(0.0, "ARS")]     // zero is not an amount anyone can commit
    [InlineData(-5.0, "ARS")]    // negative
    public async Task Decide_ApprovedWithoutUsableAmount_Returns400AndNeverWrites(
        double? amount, string currency)
    {
        var (results, analyses) = Fixture("PendingReview");
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService());

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId,
            Request("Approved", amount is null ? null : (decimal)amount.Value, currency),
            CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, obj.StatusCode);
        Assert.False(analyses.UpdateCalled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("GBP")]      // real ISO code, not one this system accepts
    public async Task Decide_ApprovedWithUnusableCurrency_Returns400AndNeverWrites(string? currency)
    {
        var (results, analyses) = Fixture("PendingReview");
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService());

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Approved", 1_000m, currency), CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, obj.StatusCode);
        Assert.False(analyses.UpdateCalled);
    }

    [Theory]
    [InlineData("ars")]
    [InlineData(" ARS ")]
    public async Task Decide_NormalisesCurrency_SoTheLimitLookupStillMatches(string supplied)
    {
        // The currency is the KEY of the limit lookup. If ' ars ' did not normalise it would miss a
        // perfectly good ARS limit and escalate a decision the analyst was entitled to make.
        var caller = Caller(Roles.Analyst);
        var (results, analyses) = Fixture("PendingReview");
        var limits = StubUserApprovalLimitRepository.WithLimit(caller.UserId, "ARS", true, 500_000m);
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, limits);

        await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Approved", 1_000m, supplied), CancellationToken.None);

        Assert.Equal("Approved", analyses.Updated!.Status);
        // ...and what is stored is the canonical code, not what the caller happened to type.
        Assert.Equal("ARS", analyses.Updated.DecisionCurrency);
    }

    [Fact]
    public async Task Decide_UnknownDecisionValue_Returns400()
    {
        var (results, analyses) = Fixture("PendingReview");
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService());

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Reviewed", null, null), CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, obj.StatusCode);
        Assert.False(analyses.UpdateCalled);
    }

    [Fact]
    public async Task Decide_RejectedIgnoresAmountAndCurrency_AndNeedsNoLimit()
    {
        // A rejection commits nothing, so it neither requires an amount nor consults a limit. The
        // empty limit repository would deny an approval outright, which is what makes this sharp.
        var caller = Caller(Roles.Analyst);
        var (results, analyses) = Fixture("PendingReview");
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, StubUserApprovalLimitRepository.Empty());

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Rejected", null, null), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Rejected", analyses.Updated!.Status);
    }

    [Fact]
    public async Task Decide_OldApprovedRow_KeepsNullPreApprovalRecord()
    {
        // Backwards compatibility: an analysis decided before the two-step flow has all six new
        // columns NULL, and re-deciding it must not invent a pre-approval it never had.
        var caller = Caller(Roles.Analyst);
        var (results, analyses) = Fixture("Reviewed");
        var limits = StubUserApprovalLimitRepository.WithLimit(caller.UserId, "ARS", true, 10_000_000m);
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, limits);

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Approved", 5_000m, "ARS"), CancellationToken.None);

        var ok       = Assert.IsType<OkObjectResult>(result);
        var payload  = Assert.IsType<AnalysisResultResponse>(ok.Value);
        Assert.Null(payload.PreApprovedBy);
        Assert.Null(payload.PreApprovedByName);
        Assert.Null(payload.PreApprovedAmount);
        Assert.Equal(5_000m, payload.DecisionAmount);
    }

    [Fact]
    public async Task Decide_ResolvesActorNames_InOneBatchedLookup()
    {
        var caller = Caller(Roles.Analyst);
        var (results, analyses) = Fixture("PendingReview");
        var limits = StubUserApprovalLimitRepository.WithLimit(caller.UserId, "ARS", true, 10_000_000m);
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, limits);

        await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Approved", 1_000m, "ARS"), CancellationToken.None);

        // The names of up to three actors cost exactly one query, not one per actor.
        Assert.Equal(1, StubUserRepositoryProbe(sut));
    }


    // ── Proposed vs confirmed operations (DDL 061) ──────────────────────────────
    //
    // A pre-approval used to persist an amount and discard the operations the analyst entered, so
    // the confirmer received a bare figure. These pin the two records and, above all, that a
    // confirmation never rewrites the proposal — the pair IS the traceability.

    [Fact]
    public async Task Decide_PreApproval_PersistsProposedOperations_AndLeavesConfirmedNull()
    {
        var caller = Caller(Roles.Analyst);
        var (results, analyses) = Fixture("PendingReview");
        var limits = StubUserApprovalLimitRepository.WithLimit(caller.UserId, "ARS", true, 500_000m);
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, limits);

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId,
            Request("Approved", 600_000m, "ARS",
                    [Operation(type: 5, amount: 400_000m, aSolaFirma: true),
                     Operation(type: 7, amount: 200_000m, requiereContragarantiaReal: true)]),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var saved = analyses.Updated!;
        Assert.Equal("PreApproved", saved.Status);

        var proposed = ReadOperations(saved.ProposedOperationsJson);
        Assert.Equal(2, proposed.Count);
        Assert.Equal(5, proposed[0].Type);
        Assert.Equal(400_000m, proposed[0].Amount);
        Assert.True(proposed[0].ASolaFirma);
        Assert.False(proposed[0].RequiereContragarantiaReal);
        Assert.Equal(7, proposed[1].Type);
        Assert.True(proposed[1].RequiereContragarantiaReal);
        // Nothing is committed by a pre-approval, so the confirmed record must stay empty — the
        // same rule DecisionAmount follows one line above it.
        Assert.Null(saved.ConfirmedOperationsJson);
        Assert.Null(saved.DecisionAmount);
    }

    [Fact]
    public async Task Decide_Confirmation_PersistsConfirmedOperations_AndLeavesProposedUntouched()
    {
        // The headline. The confirmer's version is what the granted line is built from; the
        // analyst's proposal survives byte-for-byte beside it, which is the only reason an
        // auditor can see that the operations were changed at all.
        var caller = Caller(Roles.RiskManager, "Risk Manager");
        var (results, analyses) = Fixture("PreApproved");
        var proposedJson = JsonSerializer.Serialize(new[] { Operation(type: 5, amount: 900_000m) });
        analyses.AnalysisToReturn!.PreApprovedBy          = Guid.NewGuid();
        analyses.AnalysisToReturn.PreApprovedAmount       = 900_000m;
        analyses.AnalysisToReturn.PreApprovedCurrency     = "ARS";
        analyses.AnalysisToReturn.ProposedOperationsJson  = proposedJson;
        var audit = new StubAuditService();

        var quorum = new StubAnalysisApprovalQuorumService();
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           audit, caller, StubUserApprovalLimitRepository.Empty(), quorum);

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId,
            Request("Approved", 750_000m, "ARS", [Operation(type: 5, amount: 750_000m)]),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var saved = Settled(analyses);
        Assert.Equal("Approved", saved.Status);

        var confirmed = ReadOperations(saved.ConfirmedOperationsJson);
        Assert.Equal(750_000m, Assert.Single(confirmed).Amount);
        // Untouched, not merely present: a confirmation that rewrote the proposal would destroy
        // the very record this column exists for.
        Assert.Equal(proposedJson, saved.ProposedOperationsJson);
        Assert.Equal("Analysis.Confirmed", audit.Action);

        // The confirmation went THROUGH the quorum engine and not around it. Without this the test
        // would still pass if the controller wrote the row itself and skipped the engine entirely —
        // which is exactly the regression that would let one signature settle a two-signature
        // analysis. UpdateCalled false is the other half: the engine owns the guarded write, and a
        // second unguarded save of the same row would undo the concurrency guarantee it exists for.
        Assert.Equal(1, quorum.VoteCallCount);
        Assert.False(analyses.UpdateCalled);
    }

    [Fact]
    public async Task Decide_OneStepApproval_WritesConfirmedOnly_AndProposedStaysNull()
    {
        // No pre-approval step happened, so there is no proposal. Proposed non-null means, and only
        // means, that an analyst escalated — the rule PreApprovedAmount already follows.
        var caller = Caller(Roles.Analyst);
        var (results, analyses) = Fixture("PendingReview");
        var limits = StubUserApprovalLimitRepository.WithLimit(caller.UserId, "ARS", true, 500_000m);
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, limits);

        await sut.DecideAsync(
            results.ResultToReturn!.ResultId,
            Request("Approved", 400_000m, "ARS", [Operation(amount: 400_000m)]),
            CancellationToken.None);

        var saved = analyses.Updated!;
        Assert.Equal("Approved", saved.Status);
        Assert.Equal(400_000m, Assert.Single(ReadOperations(saved.ConfirmedOperationsJson)).Amount);
        Assert.Null(saved.ProposedOperationsJson);
    }

    [Theory]
    [InlineData("Rejected")]
    [InlineData("PendingReview")]
    public async Task Decide_RejectionOrReturn_LeavesConfirmedOperationsNull(string decision)
    {
        // Neither commits anything, so operations there would read as credit that was granted —
        // exactly why DecisionAmount is nulled on the same line. And the array is IGNORED, not
        // merely discarded after validation: the payload below carries operations with no decision
        // currency to match against, which an approval would answer 400 to. A rejection must not.
        var caller = Caller(Roles.RiskManager);
        var (results, analyses) = Fixture("PreApproved");
        analyses.AnalysisToReturn!.ConfirmedOperationsJson = JsonSerializer.Serialize(new[] { Operation() });
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, StubUserApprovalLimitRepository.Empty());

        await sut.DecideAsync(
            results.ResultToReturn!.ResultId,
            Request(decision, null, null, [Operation()]),
            CancellationToken.None);

        Assert.Equal(decision, analyses.Updated!.Status);
        Assert.Null(analyses.Updated.ConfirmedOperationsJson);
    }

    [Fact]
    public async Task Decide_WithNoOperations_StillSucceeds_AndStoresNull()
    {
        // Fault isolation: the field is optional and a decision without it is exactly as valid as
        // it was before the field existed.
        var (results, analyses) = Fixture("PendingReview");
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService());

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Approved", 1_000m, "ARS"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Approved", analyses.Updated!.Status);
        Assert.Null(analyses.Updated.ConfirmedOperationsJson);
        Assert.Null(analyses.Updated.ProposedOperationsJson);
    }

    [Fact]
    public async Task Decide_WithEmptyOperationsArray_StoresNullNotAnEmptyArray()
    {
        // "Nothing was recorded" must stay ONE value in the column. Two spellings would force every
        // reader — SQL, the Portal, a future report — to check for both.
        var (results, analyses) = Fixture("PendingReview");
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService());

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId,
            Request("Approved", 1_000m, "ARS", []),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Null(analyses.Updated!.ConfirmedOperationsJson);
    }

    [Theory]
    [InlineData(18, 100.0, "ARS")]    // type outside the SGR catalogue
    [InlineData(-1, 100.0, "ARS")]    // ditto, below the range
    [InlineData(3, 0.0, "ARS")]       // zero is not an amount
    [InlineData(3, -100.0, "ARS")]    // negative
    [InlineData(3, 100.0, "USD")]     // a currency the decision is not in
    [InlineData(3, 100.0, "GBP")]     // ...or that the system does not accept at all
    public async Task Decide_WithInvalidOperation_Returns400AndNeverWrites(
        int type, double amount, string currency)
    {
        // Present-but-invalid is a 400, not a silent drop: discarding a proposal the analyst typed
        // is the defect this field exists to fix. And the 400 must not have written the row.
        var (results, analyses) = Fixture("PendingReview");
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService());

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId,
            Request("Approved", 1_000m, "ARS",
                    [Operation(type: type, amount: (decimal)amount, currency: currency)]),
            CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, obj.StatusCode);
        Assert.False(analyses.UpdateCalled);
    }

    [Fact]
    public async Task Decide_WithTooManyOperations_Returns400AndNeverWrites()
    {
        var (results, analyses) = Fixture("PendingReview");
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService());

        var tooMany = Enumerable.Range(0, 51).Select(_ => Operation()).ToList();

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId,
            Request("Approved", 1_000m, "ARS", tooMany),
            CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, obj.StatusCode);
        Assert.False(analyses.UpdateCalled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ars")]
    public async Task Decide_OperationCurrency_InheritsOrNormalisesToTheDecisionCurrency(string? supplied)
    {
        // The operation can only ever live in the cupo's currency, so an omitted one inherits and a
        // differently-cased one normalises. What is STORED is the canonical code, so the blob can be
        // handed straight to the operations route.
        var (results, analyses) = Fixture("PendingReview");
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService());

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId,
            Request("Approved", 1_000m, "ARS", [Operation(currency: supplied)]),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("ARS", Assert.Single(ReadOperations(analyses.Updated!.ConfirmedOperationsJson)).Currency);
    }

    [Fact]
    public async Task Decide_Response_ExposesBothOperationRecordsAsTypedArrays()
    {
        // The Portal renders the proposal on the confirm screen and builds the line from the
        // confirmed version; neither should have to parse a string to do it.
        var caller = Caller(Roles.RiskManager);
        var (results, analyses) = Fixture("PreApproved");
        analyses.AnalysisToReturn!.ProposedOperationsJson =
            JsonSerializer.Serialize(new[] { Operation(type: 5, amount: 900_000m) });
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, StubUserApprovalLimitRepository.Empty());

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId,
            Request("Approved", 750_000m, "ARS", [Operation(type: 5, amount: 750_000m)]),
            CancellationToken.None);

        var ok      = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<AnalysisResultResponse>(ok.Value);

        Assert.Equal(900_000m, Assert.Single(payload.ProposedOperations!).Amount);
        Assert.Equal(750_000m, Assert.Single(payload.ConfirmedOperations!).Amount);
    }

    [Fact]
    public async Task Decide_WithMalformedStoredProposedJson_SucceedsAndExposesNull()
    {
        // The read side is where fault isolation lives: a column that will not parse must not fail
        // a decision that otherwise validates. Null and unparseable both read as "none recorded".
        var caller = Caller(Roles.RiskManager);
        var (results, analyses) = Fixture("PreApproved");
        analyses.AnalysisToReturn!.ProposedOperationsJson = "{not json at all";
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, StubUserApprovalLimitRepository.Empty());

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId,
            Request("Approved", 750_000m, "ARS", [Operation(amount: 750_000m)]),
            CancellationToken.None);

        var ok      = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<AnalysisResultResponse>(ok.Value);
        Assert.Null(payload.ProposedOperations);
        // The decision itself still went through, and the unreadable blob was NOT rewritten.
        var saved = Settled(analyses);
        Assert.Equal("Approved", saved.Status);
        Assert.Equal("{not json at all", saved.ProposedOperationsJson);
        Assert.NotNull(payload.ConfirmedOperations);
    }

    [Fact]
    public async Task Decide_OldRowWithNoOperationRecord_ExposesBothArraysAsNull()
    {
        // Backwards compatibility: an analysis decided before this shipped has both columns NULL,
        // and re-deciding it without operations must not invent an empty array.
        var (results, analyses) = Fixture("Reviewed");
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService());

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Approved", 5_000m, "ARS"), CancellationToken.None);

        var ok      = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<AnalysisResultResponse>(ok.Value);
        Assert.Null(payload.ProposedOperations);
        Assert.Null(payload.ConfirmedOperations);
    }

    /// <summary>
    /// Reads the batched-lookup counter off the controller's user repository. The stub is created
    /// inside <see cref="BuildSut"/>, so the count is reached through the controller rather than
    /// threading another out-parameter through every call site.
    /// </summary>
    private static int StubUserRepositoryProbe(AnalysisController sut)
    {
        var field = typeof(AnalysisController)
            .GetField("_userRepository", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var repo = Assert.IsType<StubUserRepository>(field!.GetValue(sut));
        return repo.GetByIdsCallCount;
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

    /// <summary>
    /// A decision answers the verdict of the score it returns — judged at that score's own date, which on a
    /// re-scored row is RescoredAt (the decision response does not expose RescoredAt, but the verdict uses it).
    /// </summary>
    [Fact]
    public async Task Decide_Approval_CarriesTheScoreReliabilityOfItsScore()
    {
        var caller = Caller(Roles.Analyst);
        var (results, analyses) = Fixture("PendingReview");
        UseParityRow(results.ResultToReturn!, analyses.AnalysisToReturn!);
        var limits = StubUserApprovalLimitRepository.WithLimit(caller.UserId, "ARS", true, 500_000m);
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, limits);

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Approved", 400_000m, "ARS"), CancellationToken.None);

        AssertParityVerdict(Assert.IsType<AnalysisResultResponse>(Assert.IsType<OkObjectResult>(result).Value));
    }

    [Fact]
    public async Task Decide_Confirmation_CarriesTheScoreReliabilityOfItsScore()
    {
        var caller = Caller(Roles.RiskManager, "Risk Manager");
        var (results, analyses) = Fixture("PreApproved");
        analyses.AnalysisToReturn!.PreApprovedBy      = Guid.NewGuid();
        analyses.AnalysisToReturn.PreApprovedAmount   = 900_000m;
        analyses.AnalysisToReturn.PreApprovedCurrency = "ARS";
        UseParityRow(results.ResultToReturn!, analyses.AnalysisToReturn);
        var sut = BuildSut(results, analyses, new StubCreditLineRepository(), new StubOperationRepository(),
                           new StubAuditService(), caller, StubUserApprovalLimitRepository.Empty(),
                           new StubAnalysisApprovalQuorumService());

        var result = await sut.DecideAsync(
            results.ResultToReturn!.ResultId, Request("Approved", 750_000m, "ARS"), CancellationToken.None);

        AssertParityVerdict(Assert.IsType<AnalysisResultResponse>(Assert.IsType<OkObjectResult>(result).Value));
    }
}
