// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-03

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
/// Covers the ampliación endpoint <see cref="CreditLinesController.RaiseCreditLineAsync"/>:
/// 404 (cupo/analysis absent, cupo of another tenant), 409 (cupo not Active, stale analysis,
/// underwater, race), 400 (non-increasing amount, analysis of another entity, analysis not
/// Approved, new tope not covered by the analysis' decision), ownership (creator, Admin and
/// RiskManager may raise; another Analyst may not), and the 200 happy path that raises the amount
/// in place, stamps the analysis and audits old + new amounts.
///
/// Hand-rolled private sealed stubs (no mock library). The raise path touches the tenant
/// accessor, the credit-line + operation + analysis + user repositories, the audit service
/// and the logger, so the entity repository is passed as null! (the constructor performs
/// plain assignment).
/// </summary>
public sealed class CreditLinesControllerRaiseTests
{
    /// <summary>The calling analyst, and by default the creator of the cupo under test.</summary>
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>A DIFFERENT analyst, used as the creator when exercising the ownership refusal.</summary>
    private static readonly Guid OtherUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private const string Tenant      = "tenant-1";
    private const string FirebaseUid = "uid-1";

    /// <summary>The company every cupo and (by default) every analysis in this suite belongs to.</summary>
    private static readonly Guid EntityId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static readonly DateTime CupoCreatedAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime NewerAnalysis = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime OlderAnalysis = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ── Stubs ─────────────────────────────────────────────────────────────────
    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private sealed class StubCreditLineRepository : ICreditLineRepository
    {
        public CreditLine? CupoToReturn { get; set; }
        public CreditLine? RaiseResultToReturn { get; set; }
        public bool RaiseCalled { get; private set; }
        public decimal CapturedNewAmount { get; private set; }
        public Guid CapturedAnalysisId { get; private set; }
        public DateTime? CapturedExpectedLastRaisedAt { get; private set; }

        // Tenant-scoped like the real repository, so a cupo of another tenant reads as absent.
        public Task<CreditLine?> GetByIdAsync(Guid creditLineId, string tenantId, CancellationToken ct)
            => Task.FromResult(CupoToReturn is not null && CupoToReturn.TenantId == tenantId ? CupoToReturn : null);

        public Task<CreditLine?> RaiseAsync(
            Guid creditLineId, string tenantId, decimal newAmount, Guid analysisId,
            DateTime? expectedLastRaisedAt, CancellationToken ct)
        {
            RaiseCalled                  = true;
            CapturedNewAmount            = newAmount;
            CapturedAnalysisId           = analysisId;
            CapturedExpectedLastRaisedAt = expectedLastRaisedAt;
            return Task.FromResult(RaiseResultToReturn);
        }

        public Task<IReadOnlyList<CreditLine>> GetNonTerminalByEntityAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<CreditLine?> GetActiveByEntityCurrencyAsync(Guid entityId, string currency, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task CreateAsync(CreditLine line, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlySet<Guid>> GetEntityIdsWithNonTerminalLineAsync(IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, MainCreditLineProjection>> GetMainActiveByEntityIdsAsync(
            IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<Guid>> GetIdsByOriginAnalysisAsync(Guid analysisId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<Guid>> GetActiveIdsByOriginAnalysisAsync(Guid analysisId, string tenantId, CancellationToken ct)
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
        public IReadOnlyList<CreditLineOperation> OperationsToReturn { get; set; } = Array.Empty<CreditLineOperation>();

        public Task<IReadOnlyList<CreditLineOperation>> GetByCreditLineAsync(Guid creditLineId, string tenantId, CancellationToken ct)
            => Task.FromResult(OperationsToReturn);

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
        public Task<bool> HasNonCancelledByAnalysisAsync(Guid analysisId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubAnalysisRepository : IAnalysisRepository
    {
        public Task<BalanceBaseFiguresProjection?> GetBalanceBaseFiguresAsync(Guid analysisId, string tenantId, CancellationToken ct)
            => Task.FromResult<BalanceBaseFiguresProjection?>(null);

        public Task<IReadOnlyDictionary<Guid, EntityLatestScoreProjection>> GetLatestScoreByEntityIdsAsync(
            IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct) => throw new NotImplementedException();

        // The quorum-crossing write (DDL 067). Throws because no test in this suite confirms a
        // pre-approval, so an unexpected call must fail loudly rather than pass silently.
        public Task<bool> TryConfirmFromPreApprovedAsync(
            Guid analysisId, string tenantId, Guid reviewedBy, DateTime reviewedAt,
            decimal? decisionAmount, string? decisionCurrency, string? confirmedOperationsJson,
            string? reviewNotes, CancellationToken ct)
            => throw new NotImplementedException();
        public Analysis? AnalysisToReturn { get; set; }

        public Task<Analysis?> GetByIdAsync(Guid analysisId, CancellationToken ct)
            => Task.FromResult(AnalysisToReturn);

        public Task<Analysis> CreateAsync(Analysis analysis, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<Analysis>> GetByIdsAsync(IEnumerable<Guid> analysisIds, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Analysis?> GetLatestByDocumentIdAsync(Guid documentId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Analysis> UpdateAsync(Analysis analysis, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, string>> GetLatestStatusByEntityIdsAsync(IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<int> CountByTenantInPeriodAsync(string tenantId, DateTime periodStartUtc, DateTime periodEndUtc, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class StubUserRepository : IUserRepository
    {
        public User? UserToReturn { get; set; }

        /// <summary>Directory backing the batched creator-name lookup on the response mapping.</summary>
        public Dictionary<Guid, string> Directory { get; } = new();

        public int GetByIdsCallCount { get; private set; }

        public Task<User?> GetByFirebaseUidAsync(string firebaseUid, CancellationToken ct)
            => Task.FromResult(UserToReturn);

        public Task<User?> GetByUserIdAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<User?> GetByEmailAsync(string email, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> ExistsByFirebaseUidAsync(string firebaseUid, CancellationToken ct) => throw new NotImplementedException();

        public Task<IReadOnlyList<User>> GetByIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct)
        {
            GetByIdsCallCount++;
            IReadOnlyList<User> found = userIds
                .Where(Directory.ContainsKey)
                .Select(id => new User { UserId = id, DisplayName = Directory[id] })
                .ToList();
            return Task.FromResult(found);
        }
        public Task<User> CreateAsync(User user, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateLastLoginAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateOnboardingFlagsAsync(Guid userId, string? flagsJson, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<User>> GetActiveByTenantAsync(string tenantId, CancellationToken ct) => throw new NotImplementedException();
        // Team assignment (DDL 063) — unused by this suite.
        public Task UpdateTeamAsync(Guid userId, Guid? teamId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> ClearTeamAsync(Guid teamId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class StubAuditService : IAuditService
    {
        public bool LogCalled { get; private set; }
        public string? Action { get; private set; }
        public string? OldValueJson { get; private set; }
        public string? NewValueJson { get; private set; }

        public Task LogAsync(
            string tenantId, Guid? userId, string action, string severity = "Info",
            string? entityType = null, string? entityId = null, string? ipAddress = null,
            string? requestId = null, string? oldValueJson = null, string? newValueJson = null,
            CancellationToken ct = default)
        {
            LogCalled    = true;
            Action       = action;
            OldValueJson = oldValueJson;
            NewValueJson = newValueJson;
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

    /// <summary>
    /// Builds a cupo owned by <paramref name="createdBy"/>, defaulting to the calling analyst so
    /// the pre-existing tests below exercise the owner path.
    /// </summary>
    private static CreditLine BuildCupo(
        decimal amount = 100m, string status = "Active",
        DateTime? lastRaisedAt = null, Guid? createdBy = null, string tenantId = Tenant) => new()
    {
        CreditLineId    = Guid.NewGuid(),
        TenantId        = tenantId,
        EntityId        = EntityId,
        Amount          = amount,
        Currency        = "ARS",
        Status          = status,
        CreatedByUserId = createdBy ?? UserId,
        CreatedAt       = CupoCreatedAt,
        LastRaisedAt    = lastRaisedAt
    };

    /// <summary>
    /// Defaults to an analysis of the cupo's own entity whose decision comfortably covers every
    /// raise the pre-existing tests ask for, so they keep exercising what they did before the
    /// same-entity and decision-bound rules existed.
    /// </summary>
    private static Analysis BuildAnalysis(
        string status = "Approved", DateTime? createdAt = null, string tenantId = Tenant,
        Guid? entityId = null, decimal? decisionAmount = 1_000_000m, string? decisionCurrency = "ARS") => new()
    {
        AnalysisId       = Guid.NewGuid(),
        TenantId         = tenantId,
        EntityId         = entityId ?? EntityId,
        Status           = status,
        CreatedAt        = createdAt ?? NewerAnalysis,
        DecisionAmount   = decisionAmount,
        DecisionCurrency = decisionCurrency
    };

    private static CreditLinesController BuildSut(
        StubCreditLineRepository creditLineRepo,
        StubOperationRepository  operationRepo,
        StubAnalysisRepository   analysisRepo,
        StubAuditService         audit,
        string                   role = Roles.Analyst,
        StubUserRepository?      userRepo = null)
    {
        userRepo ??= new StubUserRepository();
        userRepo.UserToReturn = new User
        {
            UserId      = UserId,
            FirebaseUid = FirebaseUid,
            DisplayName = "Ana Analista",
            Role        = role
        };
        userRepo.Directory[UserId]      = "Ana Analista";
        userRepo.Directory[OtherUserId] = "Otro Analista";

        var sut = new CreditLinesController(
            new StubTenantContextAccessor { TenantContext = BuildTenant() },
            creditLineRepo,
            operationRepo,
            null!,              // IEntityRepository — not used by the raise path
            analysisRepo,
            null!,              // IAnalysisResultRepository — only the structure read touches it
            userRepo,
            audit,
            NullLogger<CreditLinesController>.Instance);

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("uid", FirebaseUid) }, "Test"))
        };
        sut.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return sut;
    }

    private static RaiseCreditLineRequest Request(decimal newAmount, Guid analysisId)
        => new() { NewAmount = newAmount, AnalysisId = analysisId };

    // ── Tests ───────────────────────────────────────────────────────────────
    [Fact]
    public async Task Raise_WhenCupoMissing_Returns404()
    {
        var repo = new StubCreditLineRepository { CupoToReturn = null };
        var sut  = BuildSut(repo, new StubOperationRepository(), new StubAnalysisRepository(), new StubAuditService());

        var result = await sut.RaiseCreditLineAsync(Guid.NewGuid(), Request(150m, Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(repo.RaiseCalled);
    }

    [Fact]
    public async Task Raise_WhenCupoNotActive_Returns409()
    {
        var cupo = BuildCupo(status: "Superseded");
        var repo = new StubCreditLineRepository { CupoToReturn = cupo };
        var sut  = BuildSut(repo, new StubOperationRepository(), new StubAnalysisRepository(), new StubAuditService());

        var result = await sut.RaiseCreditLineAsync(cupo.CreditLineId, Request(150m, Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(409, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(repo.RaiseCalled);
    }

    [Fact]
    public async Task Raise_WhenNewAmountNotGreater_Returns400()
    {
        var cupo = BuildCupo(amount: 100m);
        var repo = new StubCreditLineRepository { CupoToReturn = cupo };
        var sut  = BuildSut(repo, new StubOperationRepository(), new StubAnalysisRepository(), new StubAuditService());

        // Equal amount is rejected (must be strictly greater).
        var result = await sut.RaiseCreditLineAsync(cupo.CreditLineId, Request(100m, Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(repo.RaiseCalled);
    }

    [Fact]
    public async Task Raise_WhenAnalysisMissing_Returns404()
    {
        var cupo     = BuildCupo(amount: 100m);
        var repo     = new StubCreditLineRepository { CupoToReturn = cupo };
        var analysis = new StubAnalysisRepository { AnalysisToReturn = null };
        var sut      = BuildSut(repo, new StubOperationRepository(), analysis, new StubAuditService());

        var result = await sut.RaiseCreditLineAsync(cupo.CreditLineId, Request(150m, Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(repo.RaiseCalled);
    }

    [Fact]
    public async Task Raise_WhenAnalysisBelongsToAnotherTenant_Returns404()
    {
        var cupo     = BuildCupo(amount: 100m);
        var repo     = new StubCreditLineRepository { CupoToReturn = cupo };
        var analysis = new StubAnalysisRepository { AnalysisToReturn = BuildAnalysis(tenantId: "other-tenant") };
        var sut      = BuildSut(repo, new StubOperationRepository(), analysis, new StubAuditService());

        var result = await sut.RaiseCreditLineAsync(cupo.CreditLineId, Request(150m, Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(repo.RaiseCalled);
    }

    [Fact]
    public async Task Raise_WhenAnalysisNotApproved_Returns400()
    {
        var cupo     = BuildCupo(amount: 100m);
        var repo     = new StubCreditLineRepository { CupoToReturn = cupo };
        var analysis = new StubAnalysisRepository { AnalysisToReturn = BuildAnalysis(status: "PendingReview") };
        var sut      = BuildSut(repo, new StubOperationRepository(), analysis, new StubAuditService());

        var result = await sut.RaiseCreditLineAsync(cupo.CreditLineId, Request(150m, Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(repo.RaiseCalled);
    }

    [Fact]
    public async Task Raise_WhenAnalysisNotNewerThanCreatedAt_Returns409()
    {
        var cupo     = BuildCupo(amount: 100m);
        var repo     = new StubCreditLineRepository { CupoToReturn = cupo };
        var analysis = new StubAnalysisRepository { AnalysisToReturn = BuildAnalysis(createdAt: OlderAnalysis) };
        var sut      = BuildSut(repo, new StubOperationRepository(), analysis, new StubAuditService());

        var result = await sut.RaiseCreditLineAsync(cupo.CreditLineId, Request(150m, analysis.AnalysisToReturn!.AnalysisId), CancellationToken.None);

        Assert.Equal(409, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(repo.RaiseCalled);
    }

    [Fact]
    public async Task Raise_WhenAnalysisNewerThanCreatedButOlderThanLastRaise_Returns409()
    {
        // Baseline is LastRaisedAt (2026-06-01), not CreatedAt (2026-01-01); an analysis
        // dated 2026-03-01 is newer than creation but stale relative to the last raise.
        var lastRaise = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var between   = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var cupo      = BuildCupo(amount: 100m, lastRaisedAt: lastRaise);
        var repo      = new StubCreditLineRepository { CupoToReturn = cupo };
        var analysis  = new StubAnalysisRepository { AnalysisToReturn = BuildAnalysis(createdAt: between) };
        var sut       = BuildSut(repo, new StubOperationRepository(), analysis, new StubAuditService());

        var result = await sut.RaiseCreditLineAsync(cupo.CreditLineId, Request(150m, analysis.AnalysisToReturn!.AnalysisId), CancellationToken.None);

        Assert.Equal(409, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(repo.RaiseCalled);
    }

    [Fact]
    public async Task Raise_WhenNewAmountBelowVigenteSum_Returns409()
    {
        var cupo = BuildCupo(amount: 100m);
        var repo = new StubCreditLineRepository { CupoToReturn = cupo };
        var ops  = new StubOperationRepository
        {
            OperationsToReturn = new[]
            {
                new CreditLineOperation { Status = "Vigente", Amount = 200m, Currency = "ARS" }
            }
        };
        var analysis = new StubAnalysisRepository { AnalysisToReturn = BuildAnalysis() };
        var sut      = BuildSut(repo, ops, analysis, new StubAuditService());

        // 150 > cupo.Amount (100) so it passes the increase check, but 150 < Vigente sum (200).
        var result = await sut.RaiseCreditLineAsync(cupo.CreditLineId, Request(150m, analysis.AnalysisToReturn!.AnalysisId), CancellationToken.None);

        Assert.Equal(409, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(repo.RaiseCalled);
    }

    [Fact]
    public async Task Raise_WhenRepositoryReportsNoLongerActive_Returns409()
    {
        var cupo     = BuildCupo(amount: 100m);
        var repo     = new StubCreditLineRepository { CupoToReturn = cupo, RaiseResultToReturn = null };
        var analysis = new StubAnalysisRepository { AnalysisToReturn = BuildAnalysis() };
        var sut      = BuildSut(repo, new StubOperationRepository(), analysis, new StubAuditService());

        var result = await sut.RaiseCreditLineAsync(cupo.CreditLineId, Request(150m, analysis.AnalysisToReturn!.AnalysisId), CancellationToken.None);

        Assert.Equal(409, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.True(repo.RaiseCalled);   // reached the repository, which reported a race
    }

    [Fact]
    public async Task Raise_WhenBaselineChangedConcurrently_Returns409AndPassesValidatedBaseline()
    {
        // A prior raise set LastRaisedAt; the analysis is newer than it, so validation passes,
        // but the conditional UPDATE matches 0 rows because a concurrent raise moved the
        // baseline. The controller must thread the validated baseline into RaiseAsync and map
        // the null (no-update) result to 409.
        var priorRaise   = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var cupo         = BuildCupo(amount: 100m, lastRaisedAt: priorRaise);
        var analysis     = BuildAnalysis(createdAt: NewerAnalysis);          // 2026-06-01 > priorRaise
        var repo         = new StubCreditLineRepository { CupoToReturn = cupo, RaiseResultToReturn = null };
        var analysisRepo = new StubAnalysisRepository { AnalysisToReturn = analysis };
        var sut          = BuildSut(repo, new StubOperationRepository(), analysisRepo, new StubAuditService());

        var result = await sut.RaiseCreditLineAsync(cupo.CreditLineId, Request(150m, analysis.AnalysisId), CancellationToken.None);

        Assert.Equal(409, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.True(repo.RaiseCalled);
        Assert.Equal(priorRaise, repo.CapturedExpectedLastRaisedAt);         // validated baseline threaded through
    }

    [Fact]
    public async Task Raise_WhenValid_RaisesInPlaceAndAuditsOldAndNew()
    {
        var cupo     = BuildCupo(amount: 100m);
        var analysis = BuildAnalysis(createdAt: NewerAnalysis);
        var updated  = new CreditLine
        {
            CreditLineId        = cupo.CreditLineId,
            TenantId            = Tenant,
            EntityId            = cupo.EntityId,
            Amount              = 150m,
            Currency            = "ARS",
            Status              = "Active",
            CreatedAt           = cupo.CreatedAt,
            LastRaisedAt        = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            LastRaiseAnalysisId = analysis.AnalysisId
        };
        var repo         = new StubCreditLineRepository { CupoToReturn = cupo, RaiseResultToReturn = updated };
        var analysisRepo = new StubAnalysisRepository { AnalysisToReturn = analysis };
        var audit        = new StubAuditService();
        var sut          = BuildSut(repo, new StubOperationRepository(), analysisRepo, audit);

        var result = await sut.RaiseCreditLineAsync(cupo.CreditLineId, Request(150m, analysis.AnalysisId), CancellationToken.None);

        var ok       = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<CreditLineResponse>(ok.Value);
        Assert.Equal(150m, response.Amount);
        Assert.Equal(analysis.AnalysisId, response.LastRaiseAnalysisId);
        Assert.NotNull(response.LastRaisedAt);

        Assert.True(repo.RaiseCalled);
        Assert.Equal(150m, repo.CapturedNewAmount);
        Assert.Equal(analysis.AnalysisId, repo.CapturedAnalysisId);

        Assert.True(audit.LogCalled);
        Assert.Equal("CreditLine.Raised", audit.Action);
        Assert.NotNull(audit.OldValueJson);
        Assert.NotNull(audit.NewValueJson);
        Assert.Contains("100", audit.OldValueJson!);
        Assert.Contains("150", audit.NewValueJson!);
        Assert.Contains(analysis.AnalysisId.ToString(), audit.NewValueJson!);
    }

    // ── Ownership (§5.2) ──────────────────────────────────────────────────────

    [Fact]
    public async Task Raise_WhenAnalystIsNotTheCreator_Returns403AndDoesNotWrite()
    {
        var cupo     = BuildCupo(amount: 100m, createdBy: OtherUserId);
        var analysis = BuildAnalysis();
        var repo     = new StubCreditLineRepository { CupoToReturn = cupo };
        var sut      = BuildSut(repo, new StubOperationRepository(),
            new StubAnalysisRepository { AnalysisToReturn = analysis }, new StubAuditService());

        var result = await sut.RaiseCreditLineAsync(
            cupo.CreditLineId, Request(150m, analysis.AnalysisId), CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, problem.StatusCode);
        Assert.Contains("No tenés permiso", Assert.IsType<ProblemDetails>(problem.Value).Detail);
        Assert.False(repo.RaiseCalled);
    }

    /// <summary>
    /// The ownership refusal precedes the status guard, so a non-owner is told 403 rather than
    /// learning that the cupo they may not touch is not Active either.
    /// </summary>
    [Fact]
    public async Task Raise_WhenNotTheCreatorAndCupoNotActive_Returns403NotThe409()
    {
        var cupo     = BuildCupo(amount: 100m, status: "Suspended", createdBy: OtherUserId);
        var analysis = BuildAnalysis();
        var repo     = new StubCreditLineRepository { CupoToReturn = cupo };
        var sut      = BuildSut(repo, new StubOperationRepository(),
            new StubAnalysisRepository { AnalysisToReturn = analysis }, new StubAuditService());

        var result = await sut.RaiseCreditLineAsync(
            cupo.CreditLineId, Request(150m, analysis.AnalysisId), CancellationToken.None);

        Assert.Equal(403, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(repo.RaiseCalled);
    }

    [Fact]
    public async Task Raise_WhenAdminIsNotTheCreator_Returns200()
    {
        var cupo     = BuildCupo(amount: 100m, createdBy: OtherUserId);
        var analysis = BuildAnalysis();
        var raised   = BuildCupo(amount: 150m, createdBy: OtherUserId);
        var repo     = new StubCreditLineRepository
        {
            CupoToReturn        = cupo,
            RaiseResultToReturn = raised
        };
        var sut = BuildSut(repo, new StubOperationRepository(),
            new StubAnalysisRepository { AnalysisToReturn = analysis }, new StubAuditService(),
            role: Roles.Admin);

        var result = await sut.RaiseCreditLineAsync(
            cupo.CreditLineId, Request(150m, analysis.AnalysisId), CancellationToken.None);

        var response = Assert.IsType<CreditLineResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(150m, response.Amount);
        Assert.True(repo.RaiseCalled);

        // Authorship travels with the cupo so the client can decide what to offer as editable.
        Assert.Equal(OtherUserId, response.CreatedByUserId);
        Assert.Equal("Otro Analista", response.CreatedByName);
    }

    // ── RiskManager manages every cupo of its tenant ───────────────────────────

    [Fact]
    public async Task Raise_WhenRiskManagerIsNotTheCreator_Returns200()
    {
        var cupo     = BuildCupo(amount: 100m, createdBy: OtherUserId);
        var analysis = BuildAnalysis();
        var repo     = new StubCreditLineRepository
        {
            CupoToReturn        = cupo,
            RaiseResultToReturn = BuildCupo(amount: 150m, createdBy: OtherUserId)
        };
        var audit = new StubAuditService();
        var sut   = BuildSut(repo, new StubOperationRepository(),
            new StubAnalysisRepository { AnalysisToReturn = analysis }, audit, role: Roles.RiskManager);

        var result = await sut.RaiseCreditLineAsync(
            cupo.CreditLineId, Request(150m, analysis.AnalysisId), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.True(repo.RaiseCalled);
        Assert.Equal("CreditLine.Raised", audit.Action);
    }

    /// <summary>
    /// The pre-approval dead end: a RiskManager confirmed the analysis and CREATED the cupo, so the
    /// proposing analyst is not its creator. Both the creating RiskManager and any other RiskManager
    /// of the tenant can now manage it — before, only an Admin could.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Raise_CupoCreatedByRiskManager_IsManageableByRiskManager(bool callerIsTheCreator)
    {
        var creator  = callerIsTheCreator ? UserId : OtherUserId;
        var cupo     = BuildCupo(amount: 100m, createdBy: creator);
        var analysis = BuildAnalysis();
        var repo     = new StubCreditLineRepository
        {
            CupoToReturn        = cupo,
            RaiseResultToReturn = BuildCupo(amount: 150m, createdBy: creator)
        };
        var sut = BuildSut(repo, new StubOperationRepository(),
            new StubAnalysisRepository { AnalysisToReturn = analysis }, new StubAuditService(),
            role: Roles.RiskManager);

        var result = await sut.RaiseCreditLineAsync(
            cupo.CreditLineId, Request(150m, analysis.AnalysisId), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.True(repo.RaiseCalled);
    }

    /// <summary>Tenant-wide, not cross-tenant: another tenant's cupo does not exist for the caller.</summary>
    [Fact]
    public async Task Raise_WhenRiskManagerTargetsAnotherTenantsCupo_Returns404AndDoesNotWrite()
    {
        var cupo     = BuildCupo(amount: 100m, createdBy: OtherUserId, tenantId: "other-tenant");
        var analysis = BuildAnalysis();
        var repo     = new StubCreditLineRepository { CupoToReturn = cupo };
        var audit    = new StubAuditService();
        var sut      = BuildSut(repo, new StubOperationRepository(),
            new StubAnalysisRepository { AnalysisToReturn = analysis }, audit, role: Roles.RiskManager);

        var result = await sut.RaiseCreditLineAsync(
            cupo.CreditLineId, Request(150m, analysis.AnalysisId), CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(repo.RaiseCalled);
        Assert.False(audit.LogCalled);
    }

    // ── Same entity, bound to the approved decision ────────────────────────────

    private static string? DetailOf(IActionResult result)
        => Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(result).Value).Detail;

    [Fact]
    public async Task Raise_WhenAnalysisBelongsToAnotherEntity_Returns400AndPersistsNothing()
    {
        var cupo     = BuildCupo(amount: 100m);
        var analysis = BuildAnalysis(entityId: Guid.NewGuid());
        var repo     = new StubCreditLineRepository { CupoToReturn = cupo };
        var audit    = new StubAuditService();
        var sut      = BuildSut(repo, new StubOperationRepository(),
            new StubAnalysisRepository { AnalysisToReturn = analysis }, audit);

        var result = await sut.RaiseCreditLineAsync(
            cupo.CreditLineId, Request(150m, analysis.AnalysisId), CancellationToken.None);

        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal("El análisis indicado no corresponde a la entidad del cupo.", DetailOf(result));
        Assert.False(repo.RaiseCalled);
        Assert.False(audit.LogCalled);
    }

    /// <summary>
    /// The entity check precedes the Approved and freshness checks: a stale, unapproved analysis of
    /// another company is reported as the wrong company.
    /// </summary>
    [Fact]
    public async Task Raise_WhenAnalysisOfAnotherEntityIsAlsoStaleAndNotApproved_ReportsTheEntity()
    {
        var cupo     = BuildCupo(amount: 100m);
        var analysis = BuildAnalysis(status: "PendingReview", createdAt: OlderAnalysis, entityId: Guid.NewGuid());
        var repo     = new StubCreditLineRepository { CupoToReturn = cupo };
        var sut      = BuildSut(repo, new StubOperationRepository(),
            new StubAnalysisRepository { AnalysisToReturn = analysis }, new StubAuditService());

        var result = await sut.RaiseCreditLineAsync(
            cupo.CreditLineId, Request(150m, analysis.AnalysisId), CancellationToken.None);

        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal("El análisis indicado no corresponde a la entidad del cupo.", DetailOf(result));
    }

    [Fact]
    public async Task Raise_WhenNewAmountAboveDecisionAmount_Returns400AndPersistsNothing()
    {
        var cupo     = BuildCupo(amount: 100m);
        var analysis = BuildAnalysis(decisionAmount: 149.99m);
        var repo     = new StubCreditLineRepository { CupoToReturn = cupo };
        var audit    = new StubAuditService();
        var sut      = BuildSut(repo, new StubOperationRepository(),
            new StubAnalysisRepository { AnalysisToReturn = analysis }, audit);

        var result = await sut.RaiseCreditLineAsync(
            cupo.CreditLineId, Request(150m, analysis.AnalysisId), CancellationToken.None);

        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Contains("supera el monto aprobado", DetailOf(result)!, StringComparison.Ordinal);
        Assert.False(repo.RaiseCalled);
        Assert.False(audit.LogCalled);
    }

    /// <summary>The decision records the FULL new tope, so a raise to exactly that figure passes.</summary>
    [Fact]
    public async Task Raise_WhenNewAmountEqualsDecisionAmount_Returns200()
    {
        var cupo     = BuildCupo(amount: 100m);
        var analysis = BuildAnalysis(decisionAmount: 150m);
        var repo     = new StubCreditLineRepository
        {
            CupoToReturn        = cupo,
            RaiseResultToReturn = BuildCupo(amount: 150m)
        };
        var sut = BuildSut(repo, new StubOperationRepository(),
            new StubAnalysisRepository { AnalysisToReturn = analysis }, new StubAuditService());

        var result = await sut.RaiseCreditLineAsync(
            cupo.CreditLineId, Request(150m, analysis.AnalysisId), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.True(repo.RaiseCalled);
    }

    [Fact]
    public async Task Raise_WhenDecisionCurrencyDiffersFromCupo_Returns400AndPersistsNothing()
    {
        var cupo     = BuildCupo(amount: 100m);                     // ARS
        var analysis = BuildAnalysis(decisionCurrency: "USD");
        var repo     = new StubCreditLineRepository { CupoToReturn = cupo };
        var audit    = new StubAuditService();
        var sut      = BuildSut(repo, new StubOperationRepository(),
            new StubAnalysisRepository { AnalysisToReturn = analysis }, audit);

        var result = await sut.RaiseCreditLineAsync(
            cupo.CreditLineId, Request(150m, analysis.AnalysisId), CancellationToken.None);

        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Contains("La moneda no coincide con la del análisis aprobado", DetailOf(result)!, StringComparison.Ordinal);
        Assert.False(repo.RaiseCalled);
        Assert.False(audit.LogCalled);
    }

    /// <summary>Approved before DDL 058: no decision amount, so nothing to bound a raise with.</summary>
    [Fact]
    public async Task Raise_WhenApprovedWithoutDecisionAmount_Returns400AndPersistsNothing()
    {
        var cupo     = BuildCupo(amount: 100m);
        var analysis = BuildAnalysis(decisionAmount: null, decisionCurrency: null);
        var repo     = new StubCreditLineRepository { CupoToReturn = cupo };
        var audit    = new StubAuditService();
        var sut      = BuildSut(repo, new StubOperationRepository(),
            new StubAnalysisRepository { AnalysisToReturn = analysis }, audit);

        var result = await sut.RaiseCreditLineAsync(
            cupo.CreditLineId, Request(150m, analysis.AnalysisId), CancellationToken.None);

        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Contains("no registra el monto aprobado", DetailOf(result)!, StringComparison.Ordinal);
        Assert.False(repo.RaiseCalled);
        Assert.False(audit.LogCalled);
    }
}
