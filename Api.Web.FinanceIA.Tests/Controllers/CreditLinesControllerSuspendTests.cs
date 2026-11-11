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
/// Covers <see cref="CreditLinesController.SuspendCreditLineAsync"/> and
/// <see cref="CreditLinesController.ReactivateCreditLineAsync"/>: 404 (cupo absent), 409 (wrong
/// current status; on reactivate, another Active cupo already holds the entity+currency slot), and
/// the 200 happy paths that flip only the Status column, leaving operations untouched. Ownership:
/// the creator, an Admin and a RiskManager may act on any cupo of their tenant; another Analyst may
/// not, and a cupo of another tenant is a 404.
///
/// Hand-rolled private sealed stubs (no mock library). The path touches the tenant accessor, the
/// credit-line + operation + user repositories and the audit service, so the entity and analysis
/// repositories are passed as null! (the constructor performs plain assignment). SetStatusAsync is
/// tracked so a 409 that still wrote the row is caught by asserting SetStatusCalled is false.
/// </summary>
public sealed class CreditLinesControllerSuspendTests
{
    private const string Tenant      = "tenant-1";
    private const string FirebaseUid = "uid-1";

    /// <summary>The calling analyst, and by default the creator of the cupo under test.</summary>
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>A DIFFERENT analyst, used as the creator when exercising the ownership refusal.</summary>
    private static readonly Guid OtherUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // ── Stubs ─────────────────────────────────────────────────────────────────
    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private sealed class StubCreditLineRepository : ICreditLineRepository
    {
        public CreditLine? CupoToReturn { get; set; }
        public CreditLine? ActiveConflictToReturn { get; set; }
        public bool SetStatusResult { get; set; } = true;

        public bool SetStatusCalled { get; private set; }
        public string? CapturedExpectedStatus { get; private set; }
        public string? CapturedNewStatus { get; private set; }

        // Tenant-scoped like the real repository, so a cupo of another tenant reads as absent.
        public Task<CreditLine?> GetByIdAsync(Guid creditLineId, string tenantId, CancellationToken ct)
            => Task.FromResult(CupoToReturn is not null && CupoToReturn.TenantId == tenantId ? CupoToReturn : null);

        public Task<CreditLine?> GetActiveByEntityCurrencyAsync(Guid entityId, string currency, string tenantId, CancellationToken ct)
            => Task.FromResult(ActiveConflictToReturn);

        public Task<bool> SetStatusAsync(Guid creditLineId, string tenantId, string expectedStatus, string newStatus, CancellationToken ct)
        {
            SetStatusCalled        = true;
            CapturedExpectedStatus = expectedStatus;
            CapturedNewStatus      = newStatus;
            return Task.FromResult(SetStatusResult);
        }

        public Task<IReadOnlyList<CreditLine>> GetNonTerminalByEntityAsync(Guid entityId, string tenantId, CancellationToken ct)
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
        public Task<IReadOnlyList<Guid>> GetIdsByOriginAnalysisAsync(Guid analysisId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<Guid>> GetActiveIdsByOriginAnalysisAsync(Guid analysisId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<CreditLine>> GetByOriginAnalysisAsync(Guid analysisId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<CreditLine?> GetBlockingByEntityCurrencyAsync(Guid entityId, string currency, string tenantId, CancellationToken ct)
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
        public string? NewValueJson { get; private set; }

        public Task LogAsync(
            string tenantId, Guid? userId, string action, string severity = "Info",
            string? entityType = null, string? entityId = null, string? ipAddress = null,
            string? requestId = null, string? oldValueJson = null, string? newValueJson = null,
            CancellationToken ct = default)
        {
            LogCalled    = true;
            Action       = action;
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
        string status, decimal amount = 100m, Guid? createdBy = null, string tenantId = Tenant) => new()
    {
        CreditLineId    = Guid.NewGuid(),
        TenantId        = tenantId,
        EntityId        = Guid.NewGuid(),
        Amount          = amount,
        Currency        = "ARS",
        Status          = status,
        CreatedByUserId = createdBy ?? UserId,
        CreatedAt       = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    private static CreditLinesController BuildSut(
        StubCreditLineRepository creditLineRepo,
        StubOperationRepository  operationRepo,
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
            null!,              // IEntityRepository — not used by suspend/reactivate
            null!,              // IAnalysisRepository — not used by suspend/reactivate
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

    // ── Suspend tests ─────────────────────────────────────────────────────────
    [Fact]
    public async Task Suspend_WhenActive_Returns200AndPersistsSuspendedLeavingOperationsUntouched()
    {
        var cupo = BuildCupo("Active", amount: 100m);
        var repo = new StubCreditLineRepository { CupoToReturn = cupo };
        var ops  = new StubOperationRepository
        {
            // A live 'Vigente' operation survives suspension: exposure is not undone.
            OperationsToReturn = new[] { new CreditLineOperation { Status = "Vigente", Amount = 50m, Currency = "ARS" } }
        };
        var audit = new StubAuditService();
        var sut   = BuildSut(repo, ops, audit);

        var result = await sut.SuspendCreditLineAsync(
            cupo.CreditLineId, new SuspendCreditLineRequest { Reason = "mora" }, CancellationToken.None);

        var ok       = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<CreditLineResponse>(ok.Value);
        Assert.Equal("Suspended", response.Status);
        Assert.Equal(50m, response.Utilizado);          // operations still counted, untouched
        Assert.Equal(1, response.OperationsCount);

        Assert.True(repo.SetStatusCalled);
        Assert.Equal("Active", repo.CapturedExpectedStatus);
        Assert.Equal("Suspended", repo.CapturedNewStatus);

        Assert.True(audit.LogCalled);
        Assert.Equal("CreditLine.Suspended", audit.Action);
        Assert.Contains("mora", audit.NewValueJson!);   // reason carried into the audit payload
    }

    [Fact]
    public async Task Suspend_WhenAlreadySuspended_Returns409AndDoesNotWrite()
    {
        var cupo = BuildCupo("Suspended");
        var repo = new StubCreditLineRepository { CupoToReturn = cupo };
        var sut  = BuildSut(repo, new StubOperationRepository(), new StubAuditService());

        var result = await sut.SuspendCreditLineAsync(cupo.CreditLineId, request: null, CancellationToken.None);

        Assert.Equal(409, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(repo.SetStatusCalled);
    }

    [Fact]
    public async Task Suspend_WhenCupoMissing_Returns404()
    {
        var repo = new StubCreditLineRepository { CupoToReturn = null };
        var sut  = BuildSut(repo, new StubOperationRepository(), new StubAuditService());

        var result = await sut.SuspendCreditLineAsync(Guid.NewGuid(), request: null, CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(repo.SetStatusCalled);
    }

    /// <summary>
    /// The cupo reads Active, but the conditional UPDATE matches 0 rows because a concurrent request
    /// moved it off Active first. This is the anti-clobber path: we must refuse (409) and NOT audit a
    /// suspension that never happened.
    /// </summary>
    [Fact]
    public async Task Suspend_WhenConcurrentTransitionLosesRace_Returns409AndDoesNotAudit()
    {
        var cupo  = BuildCupo("Active");
        var repo  = new StubCreditLineRepository { CupoToReturn = cupo, SetStatusResult = false };
        var audit = new StubAuditService();
        var sut   = BuildSut(repo, new StubOperationRepository(), audit);

        var result = await sut.SuspendCreditLineAsync(cupo.CreditLineId, request: null, CancellationToken.None);

        Assert.Equal(409, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.True(repo.SetStatusCalled);   // the conditional write was attempted...
        Assert.False(audit.LogCalled);       // ...but nothing was audited on the lost race
    }

    // ── Reactivate tests ────────────────────────────────────────────────────────
    [Fact]
    public async Task Reactivate_WhenSuspendedAndNoOtherActive_Returns200()
    {
        var cupo = BuildCupo("Suspended");
        var repo = new StubCreditLineRepository { CupoToReturn = cupo, ActiveConflictToReturn = null };
        var audit = new StubAuditService();
        var sut   = BuildSut(repo, new StubOperationRepository(), audit);

        var result = await sut.ReactivateCreditLineAsync(cupo.CreditLineId, CancellationToken.None);

        var ok       = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<CreditLineResponse>(ok.Value);
        Assert.Equal("Active", response.Status);

        Assert.True(repo.SetStatusCalled);
        Assert.Equal("Suspended", repo.CapturedExpectedStatus);
        Assert.Equal("Active", repo.CapturedNewStatus);

        Assert.True(audit.LogCalled);
        Assert.Equal("CreditLine.Reactivated", audit.Action);
    }

    [Fact]
    public async Task Reactivate_WhenAnotherActiveCupoExists_Returns409AndDoesNotWrite()
    {
        var cupo = BuildCupo("Suspended");
        // A different Active cupo already holds this entity+currency slot.
        var repo = new StubCreditLineRepository
        {
            CupoToReturn           = cupo,
            ActiveConflictToReturn = new CreditLine { Status = "Active", Currency = "ARS" }
        };
        var sut = BuildSut(repo, new StubOperationRepository(), new StubAuditService());

        var result = await sut.ReactivateCreditLineAsync(cupo.CreditLineId, CancellationToken.None);

        Assert.Equal(409, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(repo.SetStatusCalled);
    }

    [Fact]
    public async Task Reactivate_WhenNotSuspended_Returns409()
    {
        var cupo = BuildCupo("Active");
        var repo = new StubCreditLineRepository { CupoToReturn = cupo };
        var sut  = BuildSut(repo, new StubOperationRepository(), new StubAuditService());

        var result = await sut.ReactivateCreditLineAsync(cupo.CreditLineId, CancellationToken.None);

        Assert.Equal(409, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(repo.SetStatusCalled);
    }

    [Fact]
    public async Task Reactivate_WhenCupoMissing_Returns404()
    {
        var repo = new StubCreditLineRepository { CupoToReturn = null };
        var sut  = BuildSut(repo, new StubOperationRepository(), new StubAuditService());

        var result = await sut.ReactivateCreditLineAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(repo.SetStatusCalled);
    }

    // ── Ownership (§5.2) ──────────────────────────────────────────────────────
    // A cupo is mutable by its creator or by an Admin. Every refusal below also asserts
    // SetStatusCalled is false: a 403 that still flipped the status would be worse than no gate.

    [Fact]
    public async Task Suspend_WhenAnalystIsNotTheCreator_Returns403AndDoesNotWrite()
    {
        var cupo = BuildCupo("Active", createdBy: OtherUserId);
        var repo = new StubCreditLineRepository { CupoToReturn = cupo };
        var ops  = new StubOperationRepository();
        var sut  = BuildSut(repo, ops, new StubAuditService());

        var result = await sut.SuspendCreditLineAsync(cupo.CreditLineId, request: null, CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, problem.StatusCode);
        Assert.Contains("No tenés permiso", Assert.IsType<ProblemDetails>(problem.Value).Detail);
        Assert.False(repo.SetStatusCalled);
    }

    [Fact]
    public async Task Reactivate_WhenAnalystIsNotTheCreator_Returns403AndDoesNotWrite()
    {
        var cupo = BuildCupo("Suspended", createdBy: OtherUserId);
        var repo = new StubCreditLineRepository { CupoToReturn = cupo };
        var ops  = new StubOperationRepository();
        var sut  = BuildSut(repo, ops, new StubAuditService());

        var result = await sut.ReactivateCreditLineAsync(cupo.CreditLineId, CancellationToken.None);

        Assert.Equal(403, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(repo.SetStatusCalled);
    }

    /// <summary>
    /// The ownership refusal is checked BEFORE the status guard, so a non-owner never learns
    /// whether the cupo they cannot touch happens to be in the wrong state for the transition.
    /// </summary>
    [Fact]
    public async Task Suspend_WhenNotTheCreatorAndAlreadySuspended_Returns403NotThe409()
    {
        var cupo = BuildCupo("Suspended", createdBy: OtherUserId);
        var repo = new StubCreditLineRepository { CupoToReturn = cupo };
        var sut  = BuildSut(repo, new StubOperationRepository(), new StubAuditService());

        var result = await sut.SuspendCreditLineAsync(cupo.CreditLineId, request: null, CancellationToken.None);

        Assert.Equal(403, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(repo.SetStatusCalled);
    }

    [Fact]
    public async Task Suspend_WhenAdminIsNotTheCreator_Returns200()
    {
        var cupo = BuildCupo("Active", createdBy: OtherUserId);
        var repo = new StubCreditLineRepository { CupoToReturn = cupo };
        var sut  = BuildSut(repo, new StubOperationRepository(), new StubAuditService(), role: Roles.Admin);

        var result = await sut.SuspendCreditLineAsync(cupo.CreditLineId, request: null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Suspended", Assert.IsType<CreditLineResponse>(ok.Value).Status);
        Assert.True(repo.SetStatusCalled);
    }

    [Fact]
    public async Task Reactivate_WhenAdminIsNotTheCreator_Returns200()
    {
        var cupo = BuildCupo("Suspended", createdBy: OtherUserId);
        var repo = new StubCreditLineRepository { CupoToReturn = cupo };
        var sut  = BuildSut(repo, new StubOperationRepository(), new StubAuditService(), role: Roles.Admin);

        var result = await sut.ReactivateCreditLineAsync(cupo.CreditLineId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Active", Assert.IsType<CreditLineResponse>(ok.Value).Status);
        Assert.True(repo.SetStatusCalled);
    }

    /// <summary>
    /// Authorship rides the cupo read model so the client can decide what to offer as editable,
    /// rather than discovering the 403. The name is resolved, not the raw GUID alone.
    /// </summary>
    [Fact]
    public async Task Suspend_Returns200_CarriesCreatorIdAndResolvedName()
    {
        var cupo = BuildCupo("Active", createdBy: OtherUserId);
        var repo = new StubCreditLineRepository { CupoToReturn = cupo };
        var sut  = BuildSut(repo, new StubOperationRepository(), new StubAuditService(), role: Roles.Admin);

        var result   = await sut.SuspendCreditLineAsync(cupo.CreditLineId, request: null, CancellationToken.None);
        var response = Assert.IsType<CreditLineResponse>(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal(OtherUserId, response.CreatedByUserId);
        Assert.Equal("Otro Analista", response.CreatedByName);
    }

    /// <summary>A creator who no longer resolves to a user falls back to "Usuario", never to blank.</summary>
    [Fact]
    public async Task Suspend_WhenCreatorNoLongerResolves_FallsBackToUsuario()
    {
        var ghost = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var cupo  = BuildCupo("Active", createdBy: ghost);
        var repo  = new StubCreditLineRepository { CupoToReturn = cupo };
        var sut   = BuildSut(repo, new StubOperationRepository(), new StubAuditService(), role: Roles.Admin);

        var result   = await sut.SuspendCreditLineAsync(cupo.CreditLineId, request: null, CancellationToken.None);
        var response = Assert.IsType<CreditLineResponse>(Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal(ghost, response.CreatedByUserId);
        Assert.Equal("Usuario", response.CreatedByName);
    }

    // ── RiskManager manages every cupo of its tenant ───────────────────────────

    [Fact]
    public async Task Suspend_WhenRiskManagerIsNotTheCreator_Returns200()
    {
        var cupo  = BuildCupo("Active", createdBy: OtherUserId);
        var repo  = new StubCreditLineRepository { CupoToReturn = cupo };
        var audit = new StubAuditService();
        var sut   = BuildSut(repo, new StubOperationRepository(), audit, role: Roles.RiskManager);

        var result = await sut.SuspendCreditLineAsync(cupo.CreditLineId, request: null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Suspended", Assert.IsType<CreditLineResponse>(ok.Value).Status);
        Assert.True(repo.SetStatusCalled);
        Assert.True(audit.LogCalled);
    }

    [Fact]
    public async Task Reactivate_WhenRiskManagerIsNotTheCreator_Returns200()
    {
        var cupo = BuildCupo("Suspended", createdBy: OtherUserId);
        var repo = new StubCreditLineRepository { CupoToReturn = cupo };
        var sut  = BuildSut(repo, new StubOperationRepository(), new StubAuditService(), role: Roles.RiskManager);

        var result = await sut.ReactivateCreditLineAsync(cupo.CreditLineId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Active", Assert.IsType<CreditLineResponse>(ok.Value).Status);
        Assert.True(repo.SetStatusCalled);
    }

    [Fact]
    public async Task Suspend_WhenRiskManagerTargetsAnotherTenantsCupo_Returns404AndDoesNotWrite()
    {
        var cupo  = BuildCupo("Active", createdBy: OtherUserId, tenantId: "other-tenant");
        var repo  = new StubCreditLineRepository { CupoToReturn = cupo };
        var audit = new StubAuditService();
        var sut   = BuildSut(repo, new StubOperationRepository(), audit, role: Roles.RiskManager);

        var result = await sut.SuspendCreditLineAsync(cupo.CreditLineId, request: null, CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(repo.SetStatusCalled);
        Assert.False(audit.LogCalled);
    }

    [Fact]
    public async Task Reactivate_WhenRiskManagerTargetsAnotherTenantsCupo_Returns404AndDoesNotWrite()
    {
        var cupo = BuildCupo("Suspended", createdBy: OtherUserId, tenantId: "other-tenant");
        var repo = new StubCreditLineRepository { CupoToReturn = cupo };
        var sut  = BuildSut(repo, new StubOperationRepository(), new StubAuditService(), role: Roles.RiskManager);

        var result = await sut.ReactivateCreditLineAsync(cupo.CreditLineId, CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(repo.SetStatusCalled);
    }
}
