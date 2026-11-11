// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-17

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
/// Covers the operation lifecycle on <see cref="CreditLinesController"/> after operations stopped
/// being born in force: <see cref="CreditLinesController.CreateOperationAsync"/> (born 'Aprobada'),
/// the new <see cref="CreditLinesController.ConfirmOperationVigenciaAsync"/> (404 / 409 / 200 with
/// the audit stamp), <see cref="CreditLinesController.CancelOperationAsync"/> reached from
/// 'Aprobada', and the cupo-consumption rule that makes an 'Aprobada' operation reserve saldo from
/// creation rather than at confirmation.
///
/// The two load-bearing behaviours pinned here are: (1) an 'Aprobada' operation counts toward
/// Utilizado, so confirming it does NOT move the figure and a second operation that would exceed the
/// tope is refused while it is merely approved; and (2) confirming is only legal from 'Aprobada' —
/// re-confirming a 'Vigente' operation would overwrite a real timestamp with a later, false one.
///
/// Cancellation is covered here too, and symmetrically: it stamps who/when on the row and records
/// both ends of the transition in the audit trail, so that an 'Aprobada' → 'Cancelada' ("se cayó")
/// stays distinguishable from a 'Vigente' → 'Cancelada' (a close). Because CancelAsync now guards on
/// the exact prior status, a confirmar-vigencia landing mid-request makes it return false; the
/// controller re-reads and retries once, and those two paths are pinned here as well.
///
/// Hand-rolled private sealed stubs (no mock library). The paths touch the tenant accessor, the
/// credit-line + operation + analysis + user repositories and the audit service, so the entity and
/// analysis-result repositories are passed as null! (the constructor performs plain assignment).
/// ConfirmVigenciaAsync, CancelAsync and CreateAsync are tracked so a 401/404/409 that still wrote
/// the row is caught by asserting ConfirmCalled / CancelCalled / CreateCalled is false.
///
/// Ownership: the operation's creator, an Admin and a RiskManager may confirm or cancel it; another
/// Analyst may not, and an operation of another tenant is a 404. CreateOperation also refuses an
/// analysis of a different entity than the cupo.
/// </summary>
public sealed class CreditLinesControllerOperationsTests
{
    private const string Tenant      = "tenant-1";
    private const string FirebaseUid = "uid-1";

    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>A DIFFERENT analyst, used as the creator when exercising the ownership refusal.</summary>
    private static readonly Guid OtherUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>The company every cupo and (by default) every analysis in this suite belongs to.</summary>
    private static readonly Guid EntityId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTime PastDate   = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FutureDate = new(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ── Stubs ─────────────────────────────────────────────────────────────────
    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private sealed class StubCreditLineRepository : ICreditLineRepository
    {
        public CreditLine? CupoToReturn { get; set; }

        // Tenant-scoped like the real repository, so a cupo of another tenant reads as absent.
        public Task<CreditLine?> GetByIdAsync(Guid creditLineId, string tenantId, CancellationToken ct)
            => Task.FromResult(CupoToReturn is not null && CupoToReturn.TenantId == tenantId ? CupoToReturn : null);

        public Task<CreditLine?> GetActiveByEntityCurrencyAsync(Guid entityId, string currency, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<CreditLine?> GetBlockingByEntityCurrencyAsync(Guid entityId, string currency, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
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
        public CreditLineOperation? OperationToReturn { get; set; }
        /// <summary>
        /// Handed back on the SECOND GetByIdAsync, so a test can make the controller's
        /// post-lost-race re-read differ from its first read. Null means "same as the first read",
        /// which is what every test that does not exercise the retry wants.
        /// </summary>
        public CreditLineOperation? OperationOnReread { get; set; }
        public bool ConfirmResult { get; set; } = true;
        public bool CancelResult { get; set; } = true;
        /// <summary>Result of the retry call; null means reuse <see cref="CancelResult"/>.</summary>
        public bool? CancelRetryResult { get; set; }

        public bool CreateCalled { get; private set; }
        public CreditLineOperation? CreatedOperation { get; private set; }
        public int CancelCallCount { get; private set; }
        public bool CancelCalled => CancelCallCount > 0;
        public List<string> CapturedExpectedStatuses { get; } = new();
        public Guid CapturedCancelledBy { get; private set; }
        public DateTime CapturedCancelledAt { get; private set; }
        public bool ConfirmCalled { get; private set; }
        public Guid CapturedConfirmedBy { get; private set; }
        public DateTime CapturedConfirmedAt { get; private set; }

        private int _getByIdCalls;

        public Task<IReadOnlyList<CreditLineOperation>> GetByCreditLineAsync(Guid creditLineId, string tenantId, CancellationToken ct)
            => Task.FromResult(OperationsToReturn);

        // Tenant-scoped like the real repository, so an operation of another tenant reads as absent.
        public Task<CreditLineOperation?> GetByIdAsync(Guid operationId, string tenantId, CancellationToken ct)
        {
            var op = _getByIdCalls++ == 0 ? OperationToReturn : OperationOnReread ?? OperationToReturn;
            return Task.FromResult(op is not null && op.TenantId == tenantId ? op : null);
        }

        public Task CreateAsync(CreditLineOperation op, CancellationToken ct)
        {
            CreateCalled     = true;
            CreatedOperation = op;
            return Task.CompletedTask;
        }

        public Task<bool> CancelAsync(
            Guid operationId, string tenantId, string expectedStatus, Guid cancelledByUserId,
            DateTime cancelledAtUtc, CancellationToken ct)
        {
            CancelCallCount++;
            CapturedExpectedStatuses.Add(expectedStatus);
            CapturedCancelledBy = cancelledByUserId;
            CapturedCancelledAt = cancelledAtUtc;
            return Task.FromResult(CancelCallCount == 1 ? CancelResult : CancelRetryResult ?? CancelResult);
        }

        public Task<bool> ConfirmVigenciaAsync(
            Guid operationId, string tenantId, Guid confirmedByUserId, DateTime confirmedAtUtc, CancellationToken ct)
        {
            ConfirmCalled       = true;
            CapturedConfirmedBy = confirmedByUserId;
            CapturedConfirmedAt = confirmedAtUtc;
            return Task.FromResult(ConfirmResult);
        }

        public Task<IReadOnlyList<CreditLineOperation>> GetByAnalysisAsync(Guid analysisId, string tenantId, CancellationToken ct)
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

    private static CreditLine BuildCupo(decimal amount = 1000m, string status = "Active") => new()
    {
        CreditLineId    = Guid.NewGuid(),
        TenantId        = Tenant,
        EntityId        = EntityId,
        Amount          = amount,
        Currency        = "ARS",
        Status          = status,
        CreatedByUserId = UserId,
        CreatedAt       = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    /// <summary>
    /// Builds an operation owned by <paramref name="createdBy"/>, defaulting to the calling analyst
    /// so the pre-existing tests below exercise the owner path.
    /// </summary>
    private static CreditLineOperation BuildOperation(
        string status, decimal amount, Guid creditLineId, DateTime? validUntil = null,
        DateTime? canceladaEl = null, Guid? canceladaPor = null, Guid? createdBy = null,
        string tenantId = Tenant) => new()
    {
        OperationId     = Guid.NewGuid(),
        TenantId        = tenantId,
        CreditLineId    = creditLineId,
        AnalysisId      = Guid.NewGuid(),
        Type            = 3,
        Amount          = amount,
        Currency        = "ARS",
        ValidUntil      = validUntil,
        Status          = status,
        CreatedByUserId = createdBy ?? UserId,
        CreatedAt       = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
        CanceladaEl     = canceladaEl,
        CanceladaPor    = canceladaPor
    };

    /// <summary>An Approved analysis of the cupo's own entity (operations must match it).</summary>
    private static Analysis BuildApprovedAnalysis() => new()
    {
        AnalysisId       = Guid.NewGuid(),
        TenantId         = Tenant,
        EntityId         = EntityId,
        Status           = "Approved",
        DecisionAmount   = 1_000_000m,
        DecisionCurrency = "ARS"
    };

    private static CreateOperationRequest Request(Guid analysisId, decimal amount = 100m) => new()
    {
        AnalysisId = analysisId,
        Type       = 3,
        Amount     = amount,
        Currency   = "ARS",
        ValidUntil = FutureDate,
    };

    private static CreditLinesController BuildSut(
        StubCreditLineRepository creditLineRepo,
        StubOperationRepository  operationRepo,
        StubAnalysisRepository   analysisRepo,
        StubAuditService         audit,
        bool                     userResolves = true,
        string                   role = Roles.Analyst)
    {
        var userRepo = new StubUserRepository
        {
            UserToReturn = userResolves
                ? new User
                {
                    UserId      = UserId,
                    FirebaseUid = FirebaseUid,
                    DisplayName = "Ana Analista",
                    Role        = role
                }
                : null
        };
        userRepo.Directory[UserId]      = "Ana Analista";
        userRepo.Directory[OtherUserId] = "Otro Analista";

        var sut = new CreditLinesController(
            new StubTenantContextAccessor { TenantContext = BuildTenant() },
            creditLineRepo,
            operationRepo,
            null!,              // IEntityRepository — only the cupo-creation path touches it
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

    // ── Create operation tests ────────────────────────────────────────────────

    /// <summary>
    /// The whole point of the release: granting an operation and confirming it is actually in force
    /// are two separate acts. A newly created operation is committed on paper, not yet vigente.
    /// </summary>
    [Fact]
    public async Task CreateOperation_WhenValid_Returns201AndIsBornAprobada()
    {
        var cupo     = BuildCupo();
        var analysis = BuildApprovedAnalysis();
        var repo     = new StubCreditLineRepository { CupoToReturn = cupo };
        var ops      = new StubOperationRepository();
        var audit    = new StubAuditService();
        var sut      = BuildSut(repo, ops, new StubAnalysisRepository { AnalysisToReturn = analysis }, audit);

        var result = await sut.CreateOperationAsync(
            cupo.CreditLineId, Request(analysis.AnalysisId), CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.True(ops.CreateCalled);
        Assert.Equal("Aprobada", ops.CreatedOperation!.Status);
        // And the client is told so — the portal branches on this exact wire value.
        var response = Assert.IsType<OperationResponse>(Assert.IsType<ObjectResult>(result).Value);
        Assert.Equal("Aprobada", response.Status);
        Assert.Null(response.VigenteConfirmadaEl);
        Assert.Null(response.CanceladaEl);
        Assert.Equal("CreditLineOperation.Created", audit.Action);
    }

    /// <summary>Being born 'Aprobada' must not weaken the approved-analysis gate.</summary>
    [Fact]
    public async Task CreateOperation_WhenAnalysisNotApproved_Returns400AndDoesNotWrite()
    {
        var cupo     = BuildCupo();
        var analysis = BuildApprovedAnalysis();
        analysis.Status = "Pending";
        var ops = new StubOperationRepository();
        var sut = BuildSut(
            new StubCreditLineRepository { CupoToReturn = cupo }, ops,
            new StubAnalysisRepository { AnalysisToReturn = analysis }, new StubAuditService());

        var result = await sut.CreateOperationAsync(
            cupo.CreditLineId, Request(analysis.AnalysisId), CancellationToken.None);

        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(ops.CreateCalled);
    }

    /// <summary>
    /// The correctness core, at the guard: an 'Aprobada' operation reserves saldo from creation, so
    /// a second operation that would exceed the tope is refused while the first is merely approved.
    /// Before this release the reservation only began at 'Vigente', so this second operation was
    /// accepted and the cupo went over its tope.
    /// </summary>
    [Fact]
    public async Task CreateOperation_WhenAprobadaOperationAlreadyReservesTheCupo_Returns400()
    {
        var cupo     = BuildCupo(amount: 1000m);
        var analysis = BuildApprovedAnalysis();
        var ops = new StubOperationRepository
        {
            OperationsToReturn = new[] { BuildOperation("Aprobada", 800m, cupo.CreditLineId) }
        };
        var sut = BuildSut(
            new StubCreditLineRepository { CupoToReturn = cupo }, ops,
            new StubAnalysisRepository { AnalysisToReturn = analysis }, new StubAuditService());

        var result = await sut.CreateOperationAsync(
            cupo.CreditLineId, Request(analysis.AnalysisId, amount: 300m), CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, obj.StatusCode);
        Assert.Contains("supera el cupo", Assert.IsAssignableFrom<ProblemDetails>(obj.Value).Detail!, StringComparison.Ordinal);
        Assert.False(ops.CreateCalled);
    }

    /// <summary>A 'Cancelada' operation frees its reservation — it must not keep blocking the cupo.</summary>
    [Fact]
    public async Task CreateOperation_WhenExistingOperationIsCancelada_FreesTheCupoAndReturns201()
    {
        var cupo     = BuildCupo(amount: 1000m);
        var analysis = BuildApprovedAnalysis();
        var ops = new StubOperationRepository
        {
            OperationsToReturn = new[] { BuildOperation("Cancelada", 800m, cupo.CreditLineId) }
        };
        var sut = BuildSut(
            new StubCreditLineRepository { CupoToReturn = cupo }, ops,
            new StubAnalysisRepository { AnalysisToReturn = analysis }, new StubAuditService());

        var result = await sut.CreateOperationAsync(
            cupo.CreditLineId, Request(analysis.AnalysisId, amount: 300m), CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.True(ops.CreateCalled);
    }

    // ── Confirmar vigencia tests ──────────────────────────────────────────────

    [Fact]
    public async Task ConfirmVigencia_WhenAprobada_Returns200AndStampsWhoAndWhen()
    {
        var cupo      = BuildCupo(amount: 1000m);
        var operation = BuildOperation("Aprobada", 300m, cupo.CreditLineId);
        var ops = new StubOperationRepository
        {
            OperationToReturn  = operation,
            OperationsToReturn = new[] { operation }
        };
        var audit = new StubAuditService();
        var sut   = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops, new StubAnalysisRepository(), audit);

        var before = DateTime.UtcNow;
        var result = await sut.ConfirmOperationVigenciaAsync(
            cupo.CreditLineId, operation.OperationId, CancellationToken.None);
        var after = DateTime.UtcNow;

        var ok       = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<CreditLineResponse>(ok.Value);
        var confirmed = Assert.Single(response.Operations);
        Assert.Equal("Vigente", confirmed.Status);

        Assert.True(ops.ConfirmCalled);
        Assert.Equal(UserId, ops.CapturedConfirmedBy);
        Assert.InRange(ops.CapturedConfirmedAt, before, after);
        // The timestamp the client is shown is the one actually persisted, not a second UtcNow.
        Assert.Equal(ops.CapturedConfirmedAt, confirmed.VigenteConfirmadaEl);

        Assert.True(audit.LogCalled);
        Assert.Equal("CreditLineOperation.VigenciaConfirmed", audit.Action);
        Assert.Contains("Aprobada", audit.OldValueJson!, StringComparison.Ordinal);
        Assert.Contains("Vigente", audit.NewValueJson!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Confirming is legal only from 'Aprobada'. Re-confirming a 'Vigente' operation would overwrite
    /// a real confirmation timestamp with a later, false one, so this is a conflict and not a no-op.
    /// </summary>
    [Fact]
    public async Task ConfirmVigencia_WhenAlreadyVigente_Returns409AndDoesNotWrite()
    {
        var cupo      = BuildCupo();
        var operation = BuildOperation("Vigente", 300m, cupo.CreditLineId, validUntil: FutureDate);
        var ops       = new StubOperationRepository { OperationToReturn = operation };
        var sut       = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops, new StubAnalysisRepository(), new StubAuditService());

        var result = await sut.ConfirmOperationVigenciaAsync(
            cupo.CreditLineId, operation.OperationId, CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(409, obj.StatusCode);
        Assert.Contains("operación aprobada", Assert.IsAssignableFrom<ProblemDetails>(obj.Value).Detail!, StringComparison.Ordinal);
        Assert.False(ops.ConfirmCalled);
    }

    /// <summary>An effective-'Vencida' operation is stored 'Vigente' — equally not confirmable.</summary>
    [Fact]
    public async Task ConfirmVigencia_WhenEffectivelyVencida_Returns409AndDoesNotWrite()
    {
        var cupo      = BuildCupo();
        var operation = BuildOperation("Vigente", 300m, cupo.CreditLineId, validUntil: PastDate);
        var ops       = new StubOperationRepository { OperationToReturn = operation };
        var sut       = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops, new StubAnalysisRepository(), new StubAuditService());

        var result = await sut.ConfirmOperationVigenciaAsync(
            cupo.CreditLineId, operation.OperationId, CancellationToken.None);

        Assert.Equal(409, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(ops.ConfirmCalled);
    }

    /// <summary>A cancelled operation cannot be resurrected into vigencia.</summary>
    [Fact]
    public async Task ConfirmVigencia_WhenCancelada_Returns409AndDoesNotWrite()
    {
        var cupo      = BuildCupo();
        var operation = BuildOperation("Cancelada", 300m, cupo.CreditLineId);
        var ops       = new StubOperationRepository { OperationToReturn = operation };
        var sut       = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops, new StubAnalysisRepository(), new StubAuditService());

        var result = await sut.ConfirmOperationVigenciaAsync(
            cupo.CreditLineId, operation.OperationId, CancellationToken.None);

        Assert.Equal(409, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(ops.ConfirmCalled);
    }

    [Fact]
    public async Task ConfirmVigencia_WhenOperationMissing_Returns404()
    {
        var cupo = BuildCupo();
        var ops  = new StubOperationRepository { OperationToReturn = null };
        var sut  = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops, new StubAnalysisRepository(), new StubAuditService());

        var result = await sut.ConfirmOperationVigenciaAsync(
            cupo.CreditLineId, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(ops.ConfirmCalled);
    }

    /// <summary>
    /// The route asserts a parent cupo. Honouring a mismatched one would let a caller confirm an
    /// operation through a cupo they were not looking at, so the pairing is checked, not assumed.
    /// </summary>
    [Fact]
    public async Task ConfirmVigencia_WhenOperationBelongsToAnotherCupo_Returns404AndDoesNotWrite()
    {
        var cupo      = BuildCupo();
        var operation = BuildOperation("Aprobada", 300m, creditLineId: Guid.NewGuid());
        var ops       = new StubOperationRepository { OperationToReturn = operation };
        var sut       = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops, new StubAnalysisRepository(), new StubAuditService());

        var result = await sut.ConfirmOperationVigenciaAsync(
            cupo.CreditLineId, operation.OperationId, CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(ops.ConfirmCalled);
    }

    /// <summary>
    /// Tenant isolation: the repository filters by TenantId, so another tenant's operation resolves
    /// to null and is a 404 — never a confirmable row, and never a 403 that would confirm it exists.
    /// </summary>
    [Fact]
    public async Task ConfirmVigencia_WhenOperationBelongsToAnotherTenant_Returns404()
    {
        var cupo = BuildCupo();
        var ops  = new StubOperationRepository { OperationToReturn = null };
        var sut  = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops, new StubAnalysisRepository(), new StubAuditService());

        var result = await sut.ConfirmOperationVigenciaAsync(
            cupo.CreditLineId, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(ops.ConfirmCalled);
    }

    [Fact]
    public async Task ConfirmVigencia_WhenCupoMissing_Returns404AndDoesNotWrite()
    {
        var ops = new StubOperationRepository();
        var sut = BuildSut(new StubCreditLineRepository { CupoToReturn = null }, ops, new StubAnalysisRepository(), new StubAuditService());

        var result = await sut.ConfirmOperationVigenciaAsync(
            Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(ops.ConfirmCalled);
    }

    /// <summary>
    /// The conditional UPDATE matched no row, meaning a concurrent request already moved the
    /// operation off 'Aprobada'. Refuse rather than report a confirmation that never happened.
    /// </summary>
    [Fact]
    public async Task ConfirmVigencia_WhenConcurrentTransitionWon_Returns409AndDoesNotAudit()
    {
        var cupo      = BuildCupo();
        var operation = BuildOperation("Aprobada", 300m, cupo.CreditLineId);
        var ops       = new StubOperationRepository { OperationToReturn = operation, ConfirmResult = false };
        var audit     = new StubAuditService();
        var sut       = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops, new StubAnalysisRepository(), audit);

        var result = await sut.ConfirmOperationVigenciaAsync(
            cupo.CreditLineId, operation.OperationId, CancellationToken.None);

        Assert.Equal(409, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.True(ops.ConfirmCalled);
        Assert.False(audit.LogCalled);
    }

    [Fact]
    public async Task ConfirmVigencia_WhenUserUnresolved_Returns401AndDoesNotWrite()
    {
        var cupo      = BuildCupo();
        var operation = BuildOperation("Aprobada", 300m, cupo.CreditLineId);
        var ops       = new StubOperationRepository { OperationToReturn = operation };
        var sut       = BuildSut(
            new StubCreditLineRepository { CupoToReturn = cupo }, ops,
            new StubAnalysisRepository(), new StubAuditService(), userResolves: false);

        var result = await sut.ConfirmOperationVigenciaAsync(
            cupo.CreditLineId, operation.OperationId, CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
        Assert.False(ops.ConfirmCalled);
    }

    // ── Consumption tests ─────────────────────────────────────────────────────

    /// <summary>
    /// The correctness core, at the read: a still-'Aprobada' sibling counts toward Utilizado. Since
    /// the confirmed operation was ALSO counted while it was 'Aprobada', confirming does not move
    /// the figure — 500 before and 500 after. That is the invariant the portal's cupo card depends on.
    /// </summary>
    [Fact]
    public async Task ConfirmVigencia_CountsAprobadaSiblingTowardUtilizadoAndLeavesItUnchanged()
    {
        var cupo      = BuildCupo(amount: 1000m);
        var confirming = BuildOperation("Aprobada", 300m, cupo.CreditLineId);
        var sibling    = BuildOperation("Aprobada", 200m, cupo.CreditLineId);
        var ops = new StubOperationRepository
        {
            OperationToReturn  = confirming,
            OperationsToReturn = new[] { confirming, sibling }
        };
        var sut = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops, new StubAnalysisRepository(), new StubAuditService());

        var result = await sut.ConfirmOperationVigenciaAsync(
            cupo.CreditLineId, confirming.OperationId, CancellationToken.None);

        var response = Assert.IsType<CreditLineResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(500m, response.Utilizado);
        Assert.Equal(500m, response.Amount - response.Disponible);
        Assert.Equal(2, response.OperationsCount);
        // The sibling stayed approved and is still reported as such.
        Assert.Equal("Aprobada", Assert.Single(response.Operations, o => o.OperationId == sibling.OperationId).Status);
    }

    /// <summary>Only an explicit cancellation frees the reservation.</summary>
    [Fact]
    public async Task ConfirmVigencia_ExcludesCanceladaFromUtilizadoAndOperationsCount()
    {
        var cupo       = BuildCupo(amount: 1000m);
        var confirming = BuildOperation("Aprobada", 300m, cupo.CreditLineId);
        var cancelled  = BuildOperation("Cancelada", 400m, cupo.CreditLineId);
        var ops = new StubOperationRepository
        {
            OperationToReturn  = confirming,
            OperationsToReturn = new[] { confirming, cancelled }
        };
        var sut = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops, new StubAnalysisRepository(), new StubAuditService());

        var result = await sut.ConfirmOperationVigenciaAsync(
            cupo.CreditLineId, confirming.OperationId, CancellationToken.None);

        var response = Assert.IsType<CreditLineResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(300m, response.Utilizado);
        Assert.Equal(700m, response.Disponible);
        // Utilizado and OperationsCount come off the same list: 300 utilizado with 0 operations
        // would be an incoherent card.
        Assert.Equal(1, response.OperationsCount);
    }

    // ── EffectiveStatus tests ─────────────────────────────────────────────────

    /// <summary>
    /// 'Vencida' is a statement about a line that WAS in force, so an approval nobody ever confirmed
    /// cannot expire into it. A stale 'Aprobada' reads as 'Aprobada' — honestly stale.
    /// </summary>
    [Fact]
    public async Task EffectiveStatus_WhenAprobadaIsPastValidUntil_StaysAprobada()
    {
        var cupo       = BuildCupo();
        var confirming = BuildOperation("Aprobada", 100m, cupo.CreditLineId);
        var stale      = BuildOperation("Aprobada", 100m, cupo.CreditLineId, validUntil: PastDate);
        var ops = new StubOperationRepository
        {
            OperationToReturn  = confirming,
            OperationsToReturn = new[] { confirming, stale }
        };
        var sut = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops, new StubAnalysisRepository(), new StubAuditService());

        var result = await sut.ConfirmOperationVigenciaAsync(
            cupo.CreditLineId, confirming.OperationId, CancellationToken.None);

        var response = Assert.IsType<CreditLineResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("Aprobada", Assert.Single(response.Operations, o => o.OperationId == stale.OperationId).Status);
    }

    /// <summary>The pre-existing derivation is untouched: a stored 'Vigente' past its date is 'Vencida'.</summary>
    [Fact]
    public async Task EffectiveStatus_WhenVigenteIsPastValidUntil_IsVencida()
    {
        var cupo       = BuildCupo();
        var confirming = BuildOperation("Aprobada", 100m, cupo.CreditLineId);
        var expired    = BuildOperation("Vigente", 100m, cupo.CreditLineId, validUntil: PastDate);
        var ops = new StubOperationRepository
        {
            OperationToReturn  = confirming,
            OperationsToReturn = new[] { confirming, expired }
        };
        var sut = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops, new StubAnalysisRepository(), new StubAuditService());

        var result = await sut.ConfirmOperationVigenciaAsync(
            cupo.CreditLineId, confirming.OperationId, CancellationToken.None);

        var response = Assert.IsType<CreditLineResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("Vencida", Assert.Single(response.Operations, o => o.OperationId == expired.OperationId).Status);
    }

    // ── Cancel operation tests ────────────────────────────────────────────────

    /// <summary>
    /// The "se cayó" path: an approved operation that falls through. The row records who cancelled it
    /// and when, and the audit entry names 'Aprobada' as the prior status — which is what separates a
    /// deal that fell through from a line that was closed.
    /// </summary>
    [Fact]
    public async Task CancelOperation_WhenAprobada_Returns204AndStampsWhoAndWhen()
    {
        var cupo      = BuildCupo();
        var operation = BuildOperation("Aprobada", 300m, cupo.CreditLineId);
        var ops       = new StubOperationRepository { OperationToReturn = operation };
        var audit     = new StubAuditService();
        var sut       = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops, new StubAnalysisRepository(), audit);

        var before = DateTime.UtcNow;
        var result = await sut.CancelOperationAsync(operation.OperationId, CancellationToken.None);
        var after  = DateTime.UtcNow;

        Assert.IsType<NoContentResult>(result);
        Assert.True(ops.CancelCalled);
        Assert.Equal(UserId, ops.CapturedCancelledBy);
        Assert.InRange(ops.CapturedCancelledAt, before, after);
        // The conditional UPDATE is guarded on the status actually read, not on "anything but Cancelada".
        Assert.Equal("Aprobada", Assert.Single(ops.CapturedExpectedStatuses));

        Assert.True(audit.LogCalled);
        Assert.Equal("CreditLineOperation.Cancelled", audit.Action);
        Assert.Contains("Aprobada", audit.OldValueJson!, StringComparison.Ordinal);
        Assert.Contains("Cancelada", audit.NewValueJson!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half: closing a line that was in force. Same endpoint, same 204 — the ONLY thing
    /// that tells this apart from the "se cayó" above is the audit entry's old value, which is the
    /// whole reason it is written.
    /// </summary>
    [Fact]
    public async Task CancelOperation_WhenVigente_AuditsAsVigenteToCancelada()
    {
        var cupo      = BuildCupo();
        var operation = BuildOperation("Vigente", 300m, cupo.CreditLineId, validUntil: FutureDate);
        var ops       = new StubOperationRepository { OperationToReturn = operation };
        var audit     = new StubAuditService();
        var sut       = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops, new StubAnalysisRepository(), audit);

        var result = await sut.CancelOperationAsync(operation.OperationId, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal("Vigente", Assert.Single(ops.CapturedExpectedStatuses));
        Assert.Contains("Vigente", audit.OldValueJson!, StringComparison.Ordinal);
        Assert.Contains("Cancelada", audit.NewValueJson!, StringComparison.Ordinal);
        Assert.DoesNotContain("Aprobada", audit.OldValueJson!, StringComparison.Ordinal);
    }

    /// <summary>
    /// 'Vencida' is derived at read time and never stored, so nothing was ever cancelled FROM it.
    /// The audit — and the UPDATE's guard — must name the stored 'Vigente'.
    /// </summary>
    [Fact]
    public async Task CancelOperation_WhenEffectivelyVencida_AuditsStoredVigenteNotVencida()
    {
        var cupo      = BuildCupo();
        var operation = BuildOperation("Vigente", 300m, cupo.CreditLineId, validUntil: PastDate);
        var ops       = new StubOperationRepository { OperationToReturn = operation };
        var audit     = new StubAuditService();
        var sut       = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops, new StubAnalysisRepository(), audit);

        var result = await sut.CancelOperationAsync(operation.OperationId, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal("Vigente", Assert.Single(ops.CapturedExpectedStatuses));
        Assert.Contains("Vigente", audit.OldValueJson!, StringComparison.Ordinal);
        Assert.DoesNotContain("Vencida", audit.OldValueJson!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The conditional UPDATE matched no row and the re-read shows the operation already cancelled:
    /// a concurrent request got there first. Nothing left to do, and writing a second audit entry for
    /// one transition would double-count it.
    /// </summary>
    [Fact]
    public async Task CancelOperation_WhenConcurrentCancelWon_Returns204AndDoesNotAudit()
    {
        var cupo      = BuildCupo();
        var operation = BuildOperation("Aprobada", 300m, cupo.CreditLineId);
        var ops       = new StubOperationRepository
        {
            OperationToReturn = operation,
            CancelResult      = false,
            OperationOnReread = BuildOperation("Cancelada", 300m, cupo.CreditLineId, canceladaEl: PastDate)
        };
        var audit = new StubAuditService();
        var sut   = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops, new StubAnalysisRepository(), audit);

        var result = await sut.CancelOperationAsync(operation.OperationId, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(1, ops.CancelCallCount);  // no retry: the row is already where we wanted it
        Assert.False(audit.LogCalled);
    }

    /// <summary>
    /// The race the exact-status guard exists for. A confirmar-vigencia commits between our read and
    /// our write, so the guard on 'Aprobada' matches nothing. Had the guard been "anything but
    /// Cancelada" the write would have succeeded and the trail would claim the operation fell through
    /// when it was really closed. Instead the controller re-reads and audits what actually happened.
    /// </summary>
    [Fact]
    public async Task CancelOperation_WhenConcurrentConfirmWon_RetriesAndAuditsVigenteToCancelada()
    {
        var cupo      = BuildCupo();
        var operation = BuildOperation("Aprobada", 300m, cupo.CreditLineId);
        var ops       = new StubOperationRepository
        {
            OperationToReturn = operation,
            CancelResult      = false,
            CancelRetryResult = true,
            OperationOnReread = BuildOperation("Vigente", 300m, cupo.CreditLineId, validUntil: FutureDate)
        };
        var audit = new StubAuditService();
        var sut   = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops, new StubAnalysisRepository(), audit);

        var result = await sut.CancelOperationAsync(operation.OperationId, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(2, ops.CancelCallCount);
        Assert.Equal(new[] { "Aprobada", "Vigente" }, ops.CapturedExpectedStatuses);

        Assert.True(audit.LogCalled);
        Assert.Contains("Vigente", audit.OldValueJson!, StringComparison.Ordinal);
        Assert.DoesNotContain("Aprobada", audit.OldValueJson!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The retry is bounded to one. Losing twice means another writer is already on the row, so stop
    /// rather than spin — 204 is honest either way, and no audit entry is invented for a write that
    /// this request did not make.
    /// </summary>
    [Fact]
    public async Task CancelOperation_WhenLostTwice_Returns204AndDoesNotAudit()
    {
        var cupo      = BuildCupo();
        var operation = BuildOperation("Aprobada", 300m, cupo.CreditLineId);
        var ops       = new StubOperationRepository
        {
            OperationToReturn = operation,
            CancelResult      = false,
            OperationOnReread = BuildOperation("Vigente", 300m, cupo.CreditLineId, validUntil: FutureDate)
        };
        var audit = new StubAuditService();
        var sut   = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops, new StubAnalysisRepository(), audit);

        var result = await sut.CancelOperationAsync(operation.OperationId, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(2, ops.CancelCallCount);
        Assert.False(audit.LogCalled);
    }

    /// <summary>
    /// Idempotent, and deliberately non-destructive: re-cancelling must not overwrite a real
    /// cancellation stamp with a later, false one — the same hazard confirmar-vigencia refuses.
    /// </summary>
    [Fact]
    public async Task CancelOperation_WhenAlreadyCancelada_Returns204AndDoesNotAudit()
    {
        var cupo      = BuildCupo();
        var operation = BuildOperation(
            "Cancelada", 300m, cupo.CreditLineId, canceladaEl: PastDate, canceladaPor: UserId);
        var ops       = new StubOperationRepository { OperationToReturn = operation };
        var audit     = new StubAuditService();
        var sut       = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops, new StubAnalysisRepository(), audit);

        var result = await sut.CancelOperationAsync(operation.OperationId, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.False(ops.CancelCalled);
        Assert.False(audit.LogCalled);
        Assert.Equal(PastDate, operation.CanceladaEl);
    }

    /// <summary>
    /// The pre-read is what keeps a genuinely missing operation from collapsing into a silent 204.
    /// </summary>
    [Fact]
    public async Task CancelOperation_WhenOperationMissing_Returns404AndDoesNotWrite()
    {
        var ops   = new StubOperationRepository { OperationToReturn = null };
        var audit = new StubAuditService();
        var sut   = BuildSut(new StubCreditLineRepository { CupoToReturn = BuildCupo() }, ops, new StubAnalysisRepository(), audit);

        var result = await sut.CancelOperationAsync(Guid.NewGuid(), CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, obj.StatusCode);
        Assert.False(ops.CancelCalled);
        Assert.False(audit.LogCalled);
    }

    /// <summary>Tenant isolation on a path that now writes a user id onto the row.</summary>
    [Fact]
    public async Task CancelOperation_WhenOperationBelongsToAnotherTenant_Returns404()
    {
        // GetByIdAsync filters on TenantId, so another tenant's operation simply does not come back.
        var ops   = new StubOperationRepository { OperationToReturn = null };
        var audit = new StubAuditService();
        var sut   = BuildSut(new StubCreditLineRepository { CupoToReturn = BuildCupo() }, ops, new StubAnalysisRepository(), audit);

        var result = await sut.CancelOperationAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(ops.CancelCalled);
    }

    /// <summary>
    /// The cancelling user is no longer audit-only — it is persisted to CanceladaPor — so an
    /// unresolved user must stop the request before anything is written.
    /// </summary>
    [Fact]
    public async Task CancelOperation_WhenUserUnresolved_Returns401AndDoesNotWrite()
    {
        var cupo      = BuildCupo();
        var operation = BuildOperation("Aprobada", 300m, cupo.CreditLineId);
        var ops       = new StubOperationRepository { OperationToReturn = operation };
        var audit     = new StubAuditService();
        var sut       = BuildSut(
            new StubCreditLineRepository { CupoToReturn = cupo }, ops, new StubAnalysisRepository(), audit,
            userResolves: false);

        var result = await sut.CancelOperationAsync(operation.OperationId, CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
        Assert.False(ops.CancelCalled);
        Assert.False(audit.LogCalled);
    }

    /// <summary>
    /// Consumption keys on Status alone. A cancelled operation carrying a cancellation stamp is still
    /// cancelled — the new columns must not accidentally become a second source of truth for status.
    /// </summary>
    [Fact]
    public async Task CancelOperation_WhenCancelledOperationCarriesStamp_StillFreesTheCupo()
    {
        var cupo      = BuildCupo(amount: 1000m);
        var live      = BuildOperation("Aprobada", 300m, cupo.CreditLineId);
        var cancelled = BuildOperation(
            "Cancelada", 400m, cupo.CreditLineId, canceladaEl: PastDate, canceladaPor: UserId);
        var ops = new StubOperationRepository
        {
            OperationToReturn  = live,
            OperationsToReturn = new[] { live, cancelled }
        };
        var sut = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops, new StubAnalysisRepository(), new StubAuditService());

        var result = await sut.ConfirmOperationVigenciaAsync(
            cupo.CreditLineId, live.OperationId, CancellationToken.None);

        var response = Assert.IsType<CreditLineResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(300m, response.Utilizado);
        Assert.Equal(700m, response.Disponible);
        Assert.Equal(1, response.OperationsCount);
    }

    /// <summary>
    /// The response field. Cancelling returns 204, so MapOperation is unreachable from that path —
    /// the stamp surfaces on any read that carries the operation, here as a cancelled sibling. The
    /// cancelling USER is deliberately absent: OperationResponse has no property to carry it.
    /// </summary>
    [Fact]
    public async Task MapOperation_SurfacesCanceladaEl_ButNotTheCancellingUser()
    {
        var cupo      = BuildCupo();
        var live      = BuildOperation("Aprobada", 300m, cupo.CreditLineId);
        var cancelled = BuildOperation(
            "Cancelada", 400m, cupo.CreditLineId, canceladaEl: PastDate, canceladaPor: UserId);
        var ops = new StubOperationRepository
        {
            OperationToReturn  = live,
            OperationsToReturn = new[] { live, cancelled }
        };
        var sut = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops, new StubAnalysisRepository(), new StubAuditService());

        var result = await sut.ConfirmOperationVigenciaAsync(
            cupo.CreditLineId, live.OperationId, CancellationToken.None);

        var response = Assert.IsType<CreditLineResponse>(Assert.IsType<OkObjectResult>(result).Value);
        var mapped   = Assert.Single(response.Operations, o => o.OperationId == cancelled.OperationId);
        Assert.Equal("Cancelada", mapped.Status);
        Assert.Equal(PastDate, mapped.CanceladaEl);
        // A live operation has nothing to show, which is what makes the field meaningful.
        Assert.Null(Assert.Single(response.Operations, o => o.OperationId == live.OperationId).CanceladaEl);
        Assert.Null(typeof(OperationResponse).GetProperty("CanceladaPor"));
    }

    // ── Ownership on the operation mutators (§5.2) ────────────────────────────
    //
    // An operation is mutable by its creator or by an Admin. Creating one is NOT gated — there is
    // no owner yet — so CreateOperation keeps working for anyone with the role, including under a
    // colleague's cupo. Every refusal below also asserts the write never happened.

    [Fact]
    public async Task ConfirmVigencia_WhenAnalystIsNotTheCreator_Returns403AndDoesNotWrite()
    {
        var cupo      = BuildCupo();
        var operation = BuildOperation("Aprobada", 300m, cupo.CreditLineId, createdBy: OtherUserId);
        var ops       = new StubOperationRepository { OperationToReturn = operation };
        var audit     = new StubAuditService();
        var sut       = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops, new StubAnalysisRepository(), audit);

        var result = await sut.ConfirmOperationVigenciaAsync(
            cupo.CreditLineId, operation.OperationId, CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, problem.StatusCode);
        Assert.Contains("No tenés permiso", Assert.IsType<ProblemDetails>(problem.Value).Detail);
        Assert.False(ops.ConfirmCalled);
        Assert.False(audit.LogCalled);
    }

    [Fact]
    public async Task CancelOperation_WhenAnalystIsNotTheCreator_Returns403AndDoesNotWrite()
    {
        var cupo      = BuildCupo();
        var operation = BuildOperation("Vigente", 300m, cupo.CreditLineId, validUntil: FutureDate, createdBy: OtherUserId);
        var ops       = new StubOperationRepository { OperationToReturn = operation };
        var audit     = new StubAuditService();
        var sut       = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops, new StubAnalysisRepository(), audit);

        var result = await sut.CancelOperationAsync(operation.OperationId, CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, problem.StatusCode);
        Assert.Contains("No tenés permiso", Assert.IsType<ProblemDetails>(problem.Value).Detail);
        Assert.False(ops.CancelCalled);
        Assert.False(audit.LogCalled);
    }

    /// <summary>
    /// The single most important ordering decision in the ownership work. Cancel is idempotent: an
    /// already-'Cancelada' operation answers 204. If the ownership check sat AFTER that guard, a
    /// caller who may not touch the operation would be handed a 204 — success, for a write that
    /// never happened and never could. The check therefore precedes it, and this pins that.
    /// </summary>
    [Fact]
    public async Task CancelOperation_WhenNotTheCreatorAndAlreadyCancelada_Returns403NotThe204()
    {
        var cupo      = BuildCupo();
        var operation = BuildOperation("Cancelada", 300m, cupo.CreditLineId, createdBy: OtherUserId);
        var ops       = new StubOperationRepository { OperationToReturn = operation };
        var sut       = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops, new StubAnalysisRepository(), new StubAuditService());

        var result = await sut.CancelOperationAsync(operation.OperationId, CancellationToken.None);

        Assert.Equal(403, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(ops.CancelCalled);
    }

    /// <summary>
    /// Same ordering on the confirm side: a non-owner is refused before learning that the operation
    /// is in a status that could not be confirmed anyway.
    /// </summary>
    [Fact]
    public async Task ConfirmVigencia_WhenNotTheCreatorAndAlreadyVigente_Returns403NotThe409()
    {
        var cupo      = BuildCupo();
        var operation = BuildOperation("Vigente", 300m, cupo.CreditLineId, validUntil: FutureDate, createdBy: OtherUserId);
        var ops       = new StubOperationRepository { OperationToReturn = operation };
        var sut       = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops, new StubAnalysisRepository(), new StubAuditService());

        var result = await sut.ConfirmOperationVigenciaAsync(
            cupo.CreditLineId, operation.OperationId, CancellationToken.None);

        Assert.Equal(403, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(ops.ConfirmCalled);
    }

    /// <summary>A missing operation is still a 404, not a 403 — ownership is asked about a row that exists.</summary>
    [Fact]
    public async Task ConfirmVigencia_WhenOperationMissing_StillReturns404NotThe403()
    {
        var cupo = BuildCupo();
        var ops  = new StubOperationRepository { OperationToReturn = null };
        var sut  = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops, new StubAnalysisRepository(), new StubAuditService());

        var result = await sut.ConfirmOperationVigenciaAsync(
            cupo.CreditLineId, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(ops.ConfirmCalled);
    }

    [Fact]
    public async Task ConfirmVigencia_WhenAdminIsNotTheCreator_Returns200AndWrites()
    {
        var cupo      = BuildCupo();
        var operation = BuildOperation("Aprobada", 300m, cupo.CreditLineId, createdBy: OtherUserId);
        var ops       = new StubOperationRepository
        {
            OperationToReturn  = operation,
            OperationsToReturn = new[] { operation }
        };
        var sut = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops,
            new StubAnalysisRepository(), new StubAuditService(), role: Roles.Admin);

        var result = await sut.ConfirmOperationVigenciaAsync(
            cupo.CreditLineId, operation.OperationId, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.True(ops.ConfirmCalled);
        // The stamp names the ADMIN who confirmed, not the operation's creator.
        Assert.Equal(UserId, ops.CapturedConfirmedBy);
    }

    [Fact]
    public async Task CancelOperation_WhenAdminIsNotTheCreator_Returns204AndWrites()
    {
        var cupo      = BuildCupo();
        var operation = BuildOperation("Aprobada", 300m, cupo.CreditLineId, createdBy: OtherUserId);
        var ops       = new StubOperationRepository { OperationToReturn = operation };
        var sut       = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops,
            new StubAnalysisRepository(), new StubAuditService(), role: Roles.Admin);

        var result = await sut.CancelOperationAsync(operation.OperationId, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.True(ops.CancelCalled);
        Assert.Equal(UserId, ops.CapturedCancelledBy);
    }

    /// <summary>
    /// Creation is deliberately NOT owner-gated: there is no owner yet. An analyst may add an
    /// operation under a cupo somebody else created — only mutating it afterwards is theirs alone.
    /// </summary>
    [Fact]
    public async Task CreateOperation_UnderAnotherAnalystsCupo_Returns201AndStampsTheCallerAsCreator()
    {
        var cupo     = BuildCupo();
        cupo.CreatedByUserId = OtherUserId;
        var analysis = BuildApprovedAnalysis();
        var ops      = new StubOperationRepository();
        var sut      = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops,
            new StubAnalysisRepository { AnalysisToReturn = analysis }, new StubAuditService());

        var result = await sut.CreateOperationAsync(
            cupo.CreditLineId, Request(analysis.AnalysisId), CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.True(ops.CreateCalled);
        Assert.Equal(UserId, ops.CreatedOperation!.CreatedByUserId);
    }

    /// <summary>
    /// Authorship rides the operation read model so the client knows which rows to offer as
    /// editable. No server-side "isOwned" is computed — the front compares ids, as the chat UI does.
    /// </summary>
    [Fact]
    public async Task MapOperation_CarriesCreatorIdAndResolvedName_ButNoIsOwnedFlag()
    {
        var cupo      = BuildCupo();
        var mine      = BuildOperation("Aprobada", 100m, cupo.CreditLineId);
        var theirs    = BuildOperation("Vigente", 200m, cupo.CreditLineId, validUntil: FutureDate, createdBy: OtherUserId);
        var ops       = new StubOperationRepository
        {
            OperationToReturn  = mine,
            OperationsToReturn = new[] { mine, theirs }
        };
        var sut = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops,
            new StubAnalysisRepository(), new StubAuditService());

        var result   = await sut.ConfirmOperationVigenciaAsync(
            cupo.CreditLineId, mine.OperationId, CancellationToken.None);
        var response = Assert.IsType<CreditLineResponse>(Assert.IsType<OkObjectResult>(result).Value);

        var mineMapped = Assert.Single(response.Operations, o => o.OperationId == mine.OperationId);
        Assert.Equal(UserId,          mineMapped.CreatedByUserId);
        Assert.Equal("Ana Analista",  mineMapped.CreatedByName);

        var theirsMapped = Assert.Single(response.Operations, o => o.OperationId == theirs.OperationId);
        Assert.Equal(OtherUserId,     theirsMapped.CreatedByUserId);
        Assert.Equal("Otro Analista", theirsMapped.CreatedByName);

        // The ownership decision is the client's, from the ids — the server ships no boolean for it.
        Assert.Null(typeof(OperationResponse).GetProperty("IsOwned"));
    }

    // ── Condition flags (§5.3) ────────────────────────────────────────────────
    //
    // Two optional booleans per OPERATION, default false. Pure metadata: they must not reach the
    // cupo maths, the status lifecycle or any validation.

    [Fact]
    public async Task CreateOperation_WhenFlagsSet_PersistsAndEchoesBoth()
    {
        var cupo     = BuildCupo();
        var analysis = BuildApprovedAnalysis();
        var ops      = new StubOperationRepository();
        var sut      = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops,
            new StubAnalysisRepository { AnalysisToReturn = analysis }, new StubAuditService());

        var request = Request(analysis.AnalysisId);
        request.ASolaFirma                 = true;
        request.RequiereContragarantiaReal = true;

        var result = await sut.CreateOperationAsync(cupo.CreditLineId, request, CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.True(ops.CreatedOperation!.ASolaFirma);
        Assert.True(ops.CreatedOperation!.RequiereContragarantiaReal);

        var response = Assert.IsType<OperationResponse>(Assert.IsType<ObjectResult>(result).Value);
        Assert.True(response.ASolaFirma);
        Assert.True(response.RequiereContragarantiaReal);
    }

    /// <summary>Omitted means false, on the entity and on the wire — never null and never true.</summary>
    [Fact]
    public async Task CreateOperation_WhenFlagsOmitted_BothDefaultToFalse()
    {
        var cupo     = BuildCupo();
        var analysis = BuildApprovedAnalysis();
        var ops      = new StubOperationRepository();
        var sut      = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops,
            new StubAnalysisRepository { AnalysisToReturn = analysis }, new StubAuditService());

        var result = await sut.CreateOperationAsync(
            cupo.CreditLineId, Request(analysis.AnalysisId), CancellationToken.None);

        Assert.False(ops.CreatedOperation!.ASolaFirma);
        Assert.False(ops.CreatedOperation!.RequiereContragarantiaReal);

        var response = Assert.IsType<OperationResponse>(Assert.IsType<ObjectResult>(result).Value);
        Assert.False(response.ASolaFirma);
        Assert.False(response.RequiereContragarantiaReal);
    }

    /// <summary>The two are independent: setting one must not set or clear the other.</summary>
    [Fact]
    public async Task CreateOperation_FlagsAreIndependentOfEachOther()
    {
        var cupo     = BuildCupo();
        var analysis = BuildApprovedAnalysis();
        var ops      = new StubOperationRepository();
        var sut      = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops,
            new StubAnalysisRepository { AnalysisToReturn = analysis }, new StubAuditService());

        var request = Request(analysis.AnalysisId);
        request.ASolaFirma = true;      // RequiereContragarantiaReal deliberately left alone

        await sut.CreateOperationAsync(cupo.CreditLineId, request, CancellationToken.None);

        Assert.True(ops.CreatedOperation!.ASolaFirma);
        Assert.False(ops.CreatedOperation!.RequiereContragarantiaReal);
    }

    /// <summary>
    /// The load-bearing guarantee for §5.3: the flags are metadata and touch NOTHING. A flagged
    /// operation reserves cupo exactly like an unflagged one, is born 'Aprobada' exactly the same,
    /// and does not shift utilizado, disponible or the operations count by a peso.
    /// </summary>
    [Fact]
    public async Task Flags_DoNotAffectCupoConsumptionOrTheStatusLifecycle()
    {
        var cupo     = BuildCupo(amount: 1000m);
        var analysis = BuildApprovedAnalysis();

        // An existing flagged operation already occupies 300 of the cupo.
        var existing = BuildOperation("Aprobada", 300m, cupo.CreditLineId);
        existing.ASolaFirma                 = true;
        existing.RequiereContragarantiaReal = true;

        var ops = new StubOperationRepository { OperationsToReturn = new[] { existing } };
        var sut = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops,
            new StubAnalysisRepository { AnalysisToReturn = analysis }, new StubAuditService());

        var request = Request(analysis.AnalysisId, amount: 200m);
        request.ASolaFirma = true;

        var result = await sut.CreateOperationAsync(cupo.CreditLineId, request, CancellationToken.None);

        // Created normally, born 'Aprobada' — the flags changed no part of the lifecycle.
        Assert.Equal(StatusCodes.Status201Created, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal("Aprobada", ops.CreatedOperation!.Status);

        // And the flagged existing operation still consumed its 300 against the sum rule: the same
        // request for 800 (300 + 800 > 1000) must be refused exactly as it would be unflagged.
        var overflowOps = new StubOperationRepository { OperationsToReturn = new[] { existing } };
        var overflowSut = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, overflowOps,
            new StubAnalysisRepository { AnalysisToReturn = analysis }, new StubAuditService());

        var overflow = await overflowSut.CreateOperationAsync(
            cupo.CreditLineId, Request(analysis.AnalysisId, amount: 800m), CancellationToken.None);

        Assert.Equal(400, Assert.IsType<ObjectResult>(overflow).StatusCode);
        Assert.False(overflowOps.CreateCalled);
    }

    // ── RiskManager manages every operation of its tenant ──────────────────────

    [Fact]
    public async Task ConfirmVigencia_WhenRiskManagerIsNotTheCreator_Returns200AndWrites()
    {
        var cupo      = BuildCupo();
        var operation = BuildOperation("Aprobada", 300m, cupo.CreditLineId, createdBy: OtherUserId);
        var ops       = new StubOperationRepository
        {
            OperationToReturn  = operation,
            OperationsToReturn = new[] { operation }
        };
        var audit = new StubAuditService();
        var sut   = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops,
            new StubAnalysisRepository(), audit, role: Roles.RiskManager);

        var result = await sut.ConfirmOperationVigenciaAsync(
            cupo.CreditLineId, operation.OperationId, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.True(ops.ConfirmCalled);
        Assert.Equal(UserId, ops.CapturedConfirmedBy);
        Assert.True(audit.LogCalled);
    }

    [Fact]
    public async Task CancelOperation_WhenRiskManagerIsNotTheCreator_Returns204AndWrites()
    {
        var cupo      = BuildCupo();
        var operation = BuildOperation("Vigente", 300m, cupo.CreditLineId, createdBy: OtherUserId);
        var ops       = new StubOperationRepository { OperationToReturn = operation };
        var audit     = new StubAuditService();
        var sut       = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops,
            new StubAnalysisRepository(), audit, role: Roles.RiskManager);

        var result = await sut.CancelOperationAsync(operation.OperationId, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.True(ops.CancelCalled);
        Assert.Equal(UserId, ops.CapturedCancelledBy);
        Assert.True(audit.LogCalled);
    }

    [Fact]
    public async Task ConfirmVigencia_WhenRiskManagerTargetsAnotherTenantsOperation_Returns404AndDoesNotWrite()
    {
        var cupo      = BuildCupo();
        var operation = BuildOperation("Aprobada", 300m, cupo.CreditLineId, createdBy: OtherUserId, tenantId: "other-tenant");
        var ops       = new StubOperationRepository { OperationToReturn = operation };
        var audit     = new StubAuditService();
        var sut       = BuildSut(new StubCreditLineRepository { CupoToReturn = cupo }, ops,
            new StubAnalysisRepository(), audit, role: Roles.RiskManager);

        var result = await sut.ConfirmOperationVigenciaAsync(
            cupo.CreditLineId, operation.OperationId, CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(ops.ConfirmCalled);
        Assert.False(audit.LogCalled);
    }

    [Fact]
    public async Task CancelOperation_WhenRiskManagerTargetsAnotherTenantsOperation_Returns404AndDoesNotWrite()
    {
        var operation = BuildOperation("Vigente", 300m, Guid.NewGuid(), createdBy: OtherUserId, tenantId: "other-tenant");
        var ops       = new StubOperationRepository { OperationToReturn = operation };
        var audit     = new StubAuditService();
        var sut       = BuildSut(new StubCreditLineRepository { CupoToReturn = BuildCupo() }, ops,
            new StubAnalysisRepository(), audit, role: Roles.RiskManager);

        var result = await sut.CancelOperationAsync(operation.OperationId, CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(ops.CancelCalled);
        Assert.False(audit.LogCalled);
    }

    // ── Same entity ────────────────────────────────────────────────────────────

    /// <summary>
    /// An approved analysis of ANOTHER company cannot justify an operation under this cupo. Refused
    /// before the Approved and sum checks, with nothing persisted and nothing audited.
    /// </summary>
    [Fact]
    public async Task CreateOperation_WhenAnalysisBelongsToAnotherEntity_Returns400AndPersistsNothing()
    {
        var cupo     = BuildCupo();
        var analysis = BuildApprovedAnalysis();
        analysis.EntityId = Guid.NewGuid();
        var ops   = new StubOperationRepository();
        var audit = new StubAuditService();
        var sut   = BuildSut(
            new StubCreditLineRepository { CupoToReturn = cupo }, ops,
            new StubAnalysisRepository { AnalysisToReturn = analysis }, audit);

        var result = await sut.CreateOperationAsync(
            cupo.CreditLineId, Request(analysis.AnalysisId), CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, problem.StatusCode);
        Assert.Equal("El análisis indicado no corresponde a la entidad del cupo.",
            Assert.IsType<ProblemDetails>(problem.Value).Detail);
        Assert.False(ops.CreateCalled);
        Assert.False(audit.LogCalled);
    }

    /// <summary>The entity check wins over the Approved check — the wrong company is the real defect.</summary>
    [Fact]
    public async Task CreateOperation_WhenAnalysisOfAnotherEntityIsAlsoNotApproved_ReportsTheEntity()
    {
        var cupo     = BuildCupo();
        var analysis = BuildApprovedAnalysis();
        analysis.EntityId = Guid.NewGuid();
        analysis.Status   = "PendingReview";
        var ops = new StubOperationRepository();
        var sut = BuildSut(
            new StubCreditLineRepository { CupoToReturn = cupo }, ops,
            new StubAnalysisRepository { AnalysisToReturn = analysis }, new StubAuditService());

        var result = await sut.CreateOperationAsync(
            cupo.CreditLineId, Request(analysis.AnalysisId), CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, problem.StatusCode);
        Assert.Equal("El análisis indicado no corresponde a la entidad del cupo.",
            Assert.IsType<ProblemDetails>(problem.Value).Detail);
        Assert.False(ops.CreateCalled);
    }
}
