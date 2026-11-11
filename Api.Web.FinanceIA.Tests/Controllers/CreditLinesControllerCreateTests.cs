// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-07

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
/// Covers the cupo-creation endpoint <see cref="CreditLinesController.CreateCreditLineAsync"/>
/// after it began requiring the approved analysis that justifies the cupo:
/// 400 (analysis id absent), 404 (analysis absent or another tenant), 400 (analysis for a
/// DIFFERENT entity), 400 (analysis not Approved), 400 (amount or currency not covered by the
/// analysis' decision, or no decision amount recorded), 409 (duplicate active cupo, proving the
/// new checks do not regress ordering), and the 201 happy path that persists the cupo with
/// its OriginAnalysisId stamped.
///
/// Hand-rolled private sealed stubs (no mock library). The create path touches the tenant
/// accessor, the entity + credit-line + analysis + user repositories, the audit service and
/// the logger, so the operation repository is passed as null! (the constructor performs
/// plain assignment).
/// </summary>
public sealed class CreditLinesControllerCreateTests
{
    /// <summary>The calling analyst, who becomes the created cupo's owner.</summary>
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private const string Tenant      = "tenant-1";
    private const string FirebaseUid = "uid-1";

    // ── Stubs ─────────────────────────────────────────────────────────────────
    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
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

    private sealed class StubCreditLineRepository : ICreditLineRepository
    {
        // Drives the create-dedup: the endpoint now blocks on any Active OR Suspended cupo via
        // GetBlockingByEntityCurrencyAsync (not the Active-only GetActiveByEntityCurrencyAsync).
        public CreditLine? ExistingToReturn { get; set; }
        public bool CreateCalled { get; private set; }
        public CreditLine? CreatedLine { get; private set; }

        public Task<CreditLine?> GetBlockingByEntityCurrencyAsync(Guid entityId, string currency, string tenantId, CancellationToken ct)
            => Task.FromResult(ExistingToReturn);

        public Task CreateAsync(CreditLine line, CancellationToken ct)
        {
            CreateCalled = true;
            CreatedLine  = line;
            return Task.CompletedTask;
        }

        public Task<CreditLine?> GetActiveByEntityCurrencyAsync(Guid entityId, string currency, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<CreditLine?> GetByIdAsync(Guid creditLineId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<CreditLine>> GetNonTerminalByEntityAsync(Guid entityId, string tenantId, CancellationToken ct)
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
        public Task<IReadOnlyList<Guid>> GetIdsByOriginAnalysisAsync(Guid analysisId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<Guid>> GetActiveIdsByOriginAnalysisAsync(Guid analysisId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<CreditLine>> GetByOriginAnalysisAsync(Guid analysisId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<bool> SetStatusAsync(Guid creditLineId, string tenantId, string expectedStatus, string newStatus, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<CupoAlertSource>> GetActiveForCupoAlertsAsync(string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<PymeReviewSource>> GetLatestApprovedAnalysesOfActiveEntitiesAsync(string tenantId, CancellationToken ct)
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

        public Task LogAsync(
            string tenantId, Guid? userId, string action, string severity = "Info",
            string? entityType = null, string? entityId = null, string? ipAddress = null,
            string? requestId = null, string? oldValueJson = null, string? newValueJson = null,
            CancellationToken ct = default)
        {
            LogCalled = true;
            Action    = action;
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

    private static Entity BuildEntity(Guid entityId) => new()
    {
        EntityId = entityId,
        TenantId = Tenant
    };

    /// <summary>
    /// The decision defaults cover every request the suite sends (Request defaults to 100 ARS), so
    /// the pre-existing tests keep exercising what they did before the decision bound existed.
    /// </summary>
    private static Analysis BuildAnalysis(
        Guid entityId, string status = "Approved", string tenantId = Tenant,
        decimal? decisionAmount = 1_000m, string? decisionCurrency = "ARS") => new()
    {
        AnalysisId       = Guid.NewGuid(),
        TenantId         = tenantId,
        EntityId         = entityId,
        Status           = status,
        DecisionAmount   = decisionAmount,
        DecisionCurrency = decisionCurrency
    };

    private static CreateCreditLineRequest Request(Guid analysisId, decimal amount = 100m, string currency = "ARS")
        => new() { Amount = amount, Currency = currency, AnalysisId = analysisId };

    private static CreditLinesController BuildSut(
        Guid                     entityId,
        StubCreditLineRepository creditLineRepo,
        StubAnalysisRepository   analysisRepo,
        StubAuditService         audit,
        StubEntityRepository?    entityRepo = null,
        StubUserRepository?      userRepo   = null)
    {
        entityRepo ??= new StubEntityRepository { EntityToReturn = BuildEntity(entityId) };
        userRepo   ??= new StubUserRepository();
        userRepo.UserToReturn = new User
        {
            UserId      = UserId,
            FirebaseUid = FirebaseUid,
            DisplayName = "Ana Analista",
            Role        = Roles.Analyst
        };
        userRepo.Directory[UserId] = "Ana Analista";

        var sut = new CreditLinesController(
            new StubTenantContextAccessor { TenantContext = BuildTenant() },
            creditLineRepo,
            null!,              // ICreditLineOperationRepository — not used by the create path
            entityRepo,
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

    // ── Tests ───────────────────────────────────────────────────────────────
    [Fact]
    public async Task CreateCreditLine_WhenAnalysisIdEmpty_Returns400()
    {
        var entityId = Guid.NewGuid();
        var repo     = new StubCreditLineRepository();
        var sut      = BuildSut(entityId, repo, new StubAnalysisRepository(), new StubAuditService());

        var result = await sut.CreateCreditLineAsync(entityId, Request(Guid.Empty), CancellationToken.None);

        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(repo.CreateCalled);
    }

    [Fact]
    public async Task CreateCreditLine_WhenAnalysisMissing_Returns404()
    {
        var entityId = Guid.NewGuid();
        var repo     = new StubCreditLineRepository();
        var analysis = new StubAnalysisRepository { AnalysisToReturn = null };
        var sut      = BuildSut(entityId, repo, analysis, new StubAuditService());

        var result = await sut.CreateCreditLineAsync(entityId, Request(Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(repo.CreateCalled);
    }

    [Fact]
    public async Task CreateCreditLine_WhenAnalysisBelongsToAnotherTenant_Returns404()
    {
        var entityId = Guid.NewGuid();
        var repo     = new StubCreditLineRepository();
        var analysis = new StubAnalysisRepository { AnalysisToReturn = BuildAnalysis(entityId, tenantId: "other-tenant") };
        var sut      = BuildSut(entityId, repo, analysis, new StubAuditService());

        var result = await sut.CreateCreditLineAsync(entityId, Request(Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(repo.CreateCalled);
    }

    /// <summary>
    /// The failure that is invisible on screen: a correct-looking amount silently justified
    /// by another company's analysis. The analysis exists and is Approved for THIS tenant,
    /// but belongs to a different entity than the cupo being created → 400, never persisted.
    /// </summary>
    [Fact]
    public async Task CreateCreditLine_AnalysisBelongsToDifferentEntity_Returns400()
    {
        var entityId      = Guid.NewGuid();
        var otherEntityId = Guid.NewGuid();
        var repo          = new StubCreditLineRepository();
        var analysis      = new StubAnalysisRepository { AnalysisToReturn = BuildAnalysis(otherEntityId) };
        var sut           = BuildSut(entityId, repo, analysis, new StubAuditService());

        var result = await sut.CreateCreditLineAsync(entityId, Request(Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(repo.CreateCalled);
    }

    [Fact]
    public async Task CreateCreditLine_WhenAnalysisNotApproved_Returns400()
    {
        var entityId = Guid.NewGuid();
        var repo     = new StubCreditLineRepository();
        var analysis = new StubAnalysisRepository { AnalysisToReturn = BuildAnalysis(entityId, status: "PendingReview") };
        var sut      = BuildSut(entityId, repo, analysis, new StubAuditService());

        var result = await sut.CreateCreditLineAsync(entityId, Request(Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(repo.CreateCalled);
    }

    [Fact]
    public async Task CreateCreditLine_WhenActiveCupoAlreadyExists_Returns409()
    {
        var entityId = Guid.NewGuid();
        // Valid Approved analysis for this entity, but an Active cupo already exists: the new
        // analysis checks must pass and let the pre-existing 409 win (ordering unchanged).
        var repo     = new StubCreditLineRepository { ExistingToReturn = new CreditLine { Status = "Active", Currency = "ARS" } };
        var analysis = new StubAnalysisRepository { AnalysisToReturn = BuildAnalysis(entityId) };
        var sut      = BuildSut(entityId, repo, analysis, new StubAuditService());

        var result = await sut.CreateCreditLineAsync(entityId, Request(Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(409, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(repo.CreateCalled);
    }

    /// <summary>
    /// A Suspended cupo still reserves the (entity, currency) slot (it can carry live 'Vigente'
    /// operations), so creating a second cupo is a 409 — and the message must point the analyst
    /// at reactivation, not creation, so nobody ends up with two parallel lines in one currency.
    /// </summary>
    [Fact]
    public async Task CreateCreditLine_WhenSuspendedCupoExists_Returns409AndMentionsReactivation()
    {
        var entityId = Guid.NewGuid();
        var repo     = new StubCreditLineRepository { ExistingToReturn = new CreditLine { Status = "Suspended", Currency = "ARS" } };
        var analysis = new StubAnalysisRepository { AnalysisToReturn = BuildAnalysis(entityId) };
        var sut      = BuildSut(entityId, repo, analysis, new StubAuditService());

        var result = await sut.CreateCreditLineAsync(entityId, Request(Guid.NewGuid()), CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(409, obj.StatusCode);
        var detail = Assert.IsAssignableFrom<ProblemDetails>(obj.Value).Detail;
        // Points at reactivation, not creation — and names the Suspended state so the two 409s differ.
        Assert.Contains("Reactívelo", detail!, StringComparison.Ordinal);
        Assert.Contains("suspendido", detail!, StringComparison.Ordinal);
        Assert.False(repo.CreateCalled);
    }

    [Fact]
    public async Task CreateCreditLine_WhenValid_Returns201AndStampsOriginAnalysisId()
    {
        var entityId   = Guid.NewGuid();
        var analysisId = Guid.NewGuid();
        var repo       = new StubCreditLineRepository();
        var analysis   = new StubAnalysisRepository { AnalysisToReturn = new Analysis
        {
            AnalysisId = analysisId,
            TenantId   = Tenant,
            EntityId   = entityId,
            Status     = "Approved",
            DecisionAmount   = 1_000m,
            DecisionCurrency = "ARS"
        } };
        var audit = new StubAuditService();
        var sut   = BuildSut(entityId, repo, analysis, audit);

        var result = await sut.CreateCreditLineAsync(entityId, Request(analysisId), CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.True(repo.CreateCalled);
        // The point is not that the column exists, but that it is populated with the analysis
        // that justified the cupo.
        Assert.NotNull(repo.CreatedLine);
        Assert.Equal(analysisId, repo.CreatedLine!.OriginAnalysisId);
        Assert.Equal("Active", repo.CreatedLine.Status);
        Assert.True(audit.LogCalled);
        Assert.Equal("CreditLine.Created", audit.Action);
    }

    /// <summary>
    /// A persona humana analysis (DDL 072) has no document and no balance, but once approved it
    /// originates a cupo exactly like a balance one: the gate reads only the analysis's tenant, entity,
    /// status and decision.
    /// </summary>
    [Fact]
    public async Task CreateCreditLine_FromAnApprovedPersonaAnalysis_Returns201()
    {
        var entityId   = Guid.NewGuid();
        var analysisId = Guid.NewGuid();
        var repo       = new StubCreditLineRepository();
        var analysis   = new StubAnalysisRepository { AnalysisToReturn = new Analysis
        {
            AnalysisId       = analysisId,
            TenantId         = Tenant,
            EntityId         = entityId,
            DocumentId       = null,
            AnalysisKind     = AnalysisKinds.PersonaHumana,
            Status           = "Approved",
            DecisionAmount   = 1_000m,
            DecisionCurrency = "ARS"
        } };
        var sut = BuildSut(entityId, repo, analysis, new StubAuditService());

        var result = await sut.CreateCreditLineAsync(entityId, Request(analysisId), CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(analysisId, repo.CreatedLine!.OriginAnalysisId);
    }

    [Fact]
    public async Task CreateCreditLine_FromAPendingPersonaAnalysis_IsRefusedLikeAnyOther()
    {
        var entityId   = Guid.NewGuid();
        var analysisId = Guid.NewGuid();
        var repo       = new StubCreditLineRepository();
        var analysis   = new StubAnalysisRepository { AnalysisToReturn = new Analysis
        {
            AnalysisId   = analysisId,
            TenantId     = Tenant,
            EntityId     = entityId,
            AnalysisKind = AnalysisKinds.PersonaHumana,
            Status       = "PendingReview"
        } };
        var sut = BuildSut(entityId, repo, analysis, new StubAuditService());

        var result = await sut.CreateCreditLineAsync(entityId, Request(analysisId), CancellationToken.None);

        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(repo.CreateCalled);
    }

    // ── Bound to the approved decision ─────────────────────────────────────────

    private static string? DetailOf(IActionResult result)
        => Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(result).Value).Detail;

    /// <summary>
    /// The decision is the ceiling: an approved analysis that committed 1.000 cannot justify a cupo
    /// of 1.000,01. Refused before anything is written, and nothing is audited.
    /// </summary>
    [Fact]
    public async Task CreateCreditLine_AmountAboveDecisionAmount_Returns400AndPersistsNothing()
    {
        var entityId = Guid.NewGuid();
        var repo     = new StubCreditLineRepository();
        var analysis = new StubAnalysisRepository { AnalysisToReturn = BuildAnalysis(entityId, decisionAmount: 1_000m) };
        var audit    = new StubAuditService();
        var sut      = BuildSut(entityId, repo, analysis, audit);

        var result = await sut.CreateCreditLineAsync(entityId, Request(Guid.NewGuid(), amount: 1_000.01m), CancellationToken.None);

        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Contains("supera el monto aprobado", DetailOf(result)!, StringComparison.Ordinal);
        Assert.Contains("ARS 1.000,00", DetailOf(result)!, StringComparison.Ordinal);
        Assert.False(repo.CreateCalled);
        Assert.False(audit.LogCalled);
    }

    [Fact]
    public async Task CreateCreditLine_AmountEqualToDecisionAmount_Returns201()
    {
        var entityId = Guid.NewGuid();
        var repo     = new StubCreditLineRepository();
        var analysis = new StubAnalysisRepository { AnalysisToReturn = BuildAnalysis(entityId, decisionAmount: 1_000m) };
        var sut      = BuildSut(entityId, repo, analysis, new StubAuditService());

        var result = await sut.CreateCreditLineAsync(entityId, Request(Guid.NewGuid(), amount: 1_000m), CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.True(repo.CreateCalled);
    }

    [Fact]
    public async Task CreateCreditLine_CurrencyDiffersFromDecision_Returns400AndPersistsNothing()
    {
        var entityId = Guid.NewGuid();
        var repo     = new StubCreditLineRepository();
        var analysis = new StubAnalysisRepository { AnalysisToReturn = BuildAnalysis(entityId, decisionCurrency: "ARS") };
        var audit    = new StubAuditService();
        var sut      = BuildSut(entityId, repo, analysis, audit);

        var result = await sut.CreateCreditLineAsync(entityId, Request(Guid.NewGuid(), currency: "USD"), CancellationToken.None);

        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Contains("La moneda no coincide con la del análisis aprobado", DetailOf(result)!, StringComparison.Ordinal);
        Assert.False(repo.CreateCalled);
        Assert.False(audit.LogCalled);
    }

    /// <summary>
    /// An analysis approved before DDL 058 carries no decision amount. It cannot justify a cupo —
    /// there is no fallback to RequestedAmount.
    /// </summary>
    [Fact]
    public async Task CreateCreditLine_ApprovedWithoutDecisionAmount_Returns400AndPersistsNothing()
    {
        var entityId = Guid.NewGuid();
        var repo     = new StubCreditLineRepository();
        var analysis = new StubAnalysisRepository
        {
            AnalysisToReturn = BuildAnalysis(entityId, decisionAmount: null, decisionCurrency: null)
        };
        var audit = new StubAuditService();
        var sut   = BuildSut(entityId, repo, analysis, audit);

        var result = await sut.CreateCreditLineAsync(entityId, Request(Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Contains("no registra el monto aprobado", DetailOf(result)!, StringComparison.Ordinal);
        Assert.False(repo.CreateCalled);
        Assert.False(audit.LogCalled);
    }

    /// <summary>An uncovered justification wins over "a cupo already exists", like the entity check.</summary>
    [Fact]
    public async Task CreateCreditLine_AmountAboveDecisionAndExistingCupo_Returns400NotThe409()
    {
        var entityId = Guid.NewGuid();
        var repo     = new StubCreditLineRepository
        {
            ExistingToReturn = new CreditLine { EntityId = entityId, TenantId = Tenant, Currency = "ARS", Status = "Active" }
        };
        var analysis = new StubAnalysisRepository { AnalysisToReturn = BuildAnalysis(entityId, decisionAmount: 50m) };
        var sut      = BuildSut(entityId, repo, analysis, new StubAuditService());

        var result = await sut.CreateCreditLineAsync(entityId, Request(Guid.NewGuid(), amount: 100m), CancellationToken.None);

        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(repo.CreateCalled);
    }
}
