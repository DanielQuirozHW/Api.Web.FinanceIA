// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-26

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
/// Covers how <see cref="CreditLinesController.GetStructureAsync"/> decides 404-vs-proceed.
///
/// <para>
/// GET /api/credit-lines?entityId= took ~10s on a warm database, and the cupos were never the cost:
/// the repository reads are projected, index-backed and touch no blob. The expensive step was the
/// 404 guard, which called <c>IEntityRepository.GetByIdAsync</c> — a full-row load whose SELECT
/// names all five <c>nvarchar(max)</c> source snapshots — purely to null-check the result. The
/// returned entity was never read for a field. It now calls <c>ExistsByIdAsync</c>, whose EXISTS
/// names no column and so follows no LOB pointer.
/// </para>
///
/// <para>
/// The load-bearing test is <see cref="GetStructure_NeverLoadsTheWholeEntityRow"/>. The rest pin the
/// output, because the whole point of the change is that nothing observable moved: same 404, same
/// detail string, same DTO. Same counter idiom as
/// <see cref="AlertsControllerLookupScopeTests"/> — a call count asserted to be zero rather than a
/// throwing stub, so a regression reads as a number and not as a NotImplementedException from
/// somewhere in the stack.
/// </para>
///
/// <para>
/// Hand-rolled private sealed stubs (no mock library). Only the repositories the structure read
/// touches are stubbed; the analysis and audit dependencies are passed as null! because that path
/// never reaches them. The USER repository is stubbed rather than null! since authorship joined the
/// read model — it resolves the creators' display names, never the caller. Argument order mirrors
/// the ctor signature.
/// </para>
/// </summary>
public sealed class CreditLinesControllerStructureTests
{
    private const string Tenant      = "tenant-1";
    private const string FirebaseUid = "uid-1";

    // ── Stubs ─────────────────────────────────────────────────────────────────
    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private sealed class StubEntityRepository : IEntityRepository
    {
        public bool Exists { get; set; } = true;

        /// <summary>
        /// How often the endpoint asked for the whole entity row. Must stay 0: answering "does this
        /// PyME exist?" used to read all five nvarchar(max) snapshots off disk.
        /// </summary>
        public int GetByIdCallCount { get; private set; }

        /// <summary>How often the cheap existence check ran. Exactly one per request.</summary>
        public int ExistsCallCount { get; private set; }

        public Task<bool> ExistsByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
        {
            ExistsCallCount++;
            return Task.FromResult(Exists);
        }

        public Task<Entity?> GetByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
        {
            GetByIdCallCount++;
            return Task.FromResult<Entity?>(Exists ? new Entity { EntityId = entityId, TenantId = tenantId } : null);
        }

        public Task<IReadOnlyList<Entity>> GetAllByTenantAsync(string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Entity?> GetByCuitAsync(string cuit, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Entity> CreateAsync(Entity entity, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Entity> UpdateAsync(Entity entity, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, EntityNameProjection>> GetNameLookupByIdsAsync(
            IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<(IReadOnlyList<EntityListProjection> Items, int TotalCount)> GetListPageByTenantAsync(
            string tenantId, int skip, int take, CancellationToken ct)
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
        public IReadOnlyList<CreditLine> LinesToReturn { get; set; } = Array.Empty<CreditLine>();
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<CreditLine>> GetNonTerminalByEntityAsync(
            Guid entityId, string tenantId, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(LinesToReturn);
        }

        public Task<CreditLine?> GetActiveByEntityCurrencyAsync(
            Guid entityId, string currency, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<CreditLine?> GetByIdAsync(Guid creditLineId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task CreateAsync(CreditLine line, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<CreditLine?> RaiseAsync(
            Guid creditLineId, string tenantId, decimal newAmount, Guid analysisId,
            DateTime? expectedLastRaisedAt, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlySet<Guid>> GetEntityIdsWithNonTerminalLineAsync(
            IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct)
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
        public Task<CreditLine?> GetBlockingByEntityCurrencyAsync(
            Guid entityId, string currency, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<bool> SetStatusAsync(
            Guid creditLineId, string tenantId, string expectedStatus, string newStatus, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<CupoAlertSource>> GetActiveForCupoAlertsAsync(string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<PymeReviewSource>> GetLatestApprovedAnalysesOfActiveEntitiesAsync(string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubOperationRepository : ICreditLineOperationRepository
    {
        public Dictionary<Guid, IReadOnlyList<CreditLineOperation>> ByLine { get; } = [];

        public Task<IReadOnlyList<CreditLineOperation>> GetByCreditLineAsync(
            Guid creditLineId, string tenantId, CancellationToken ct)
            => Task.FromResult(ByLine.TryGetValue(creditLineId, out var ops)
                ? ops
                : (IReadOnlyList<CreditLineOperation>)Array.Empty<CreditLineOperation>());

        public Task<IReadOnlyList<CreditLineOperation>> GetByAnalysisAsync(
            Guid analysisId, string tenantId, CancellationToken ct)
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
            Guid operationId, string tenantId, Guid confirmedByUserId, DateTime confirmedAtUtc,
            CancellationToken ct)
            => throw new NotImplementedException();
        public Task<bool> HasNonCancelledByAnalysisAsync(Guid analysisId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubAnalysisResultRepository : IAnalysisResultRepository
    {
        public Dictionary<Guid, Guid> ResultIdByAnalysis { get; } = [];

        public Task<IReadOnlyDictionary<Guid, Guid>> GetResultIdsByAnalysisIdsAsync(
            IEnumerable<Guid> analysisIds, string tenantId, CancellationToken ct)
        {
            var wanted = analysisIds.ToHashSet();
            IReadOnlyDictionary<Guid, Guid> map = ResultIdByAnalysis
                .Where(kv => wanted.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            return Task.FromResult(map);
        }

        public Task<IReadOnlyList<AnalysisResult>> GetAllByTenantAsync(string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<AnalysisResult>> GetByEntityIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<(IReadOnlyList<AnalysisListProjection> Items, int TotalCount)> GetListPageByTenantAsync(
            string tenantId, int skip, int take, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<(IReadOnlyList<AnalysisListProjection> Items, int TotalCount)> GetListPageByEntityAsync(
            Guid entityId, string tenantId, int skip, int take, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<AnalysisResult?> GetByIdAsync(Guid resultId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<AnalysisResult?> GetByDocumentIdAsync(Guid documentId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<AnalysisResult> CreateAsync(AnalysisResult result, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<AnalysisResult> UpdateAsync(AnalysisResult result, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<AnalysisResultDetailProjection?> GetDetailByIdAsync(Guid resultId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<AnalysisContextProjection?> GetContextSnapshotByIdAsync(Guid resultId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<AnalysisBcraProjection?> GetBcraSnapshotByIdAsync(Guid resultId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<AnalysisRatiosProjection?> GetRatiosByIdAsync(Guid resultId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<AnalysisLabelProjection?> GetLabelByIdAsync(
            Guid resultId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// The structure read still never resolves the CALLER, but it now resolves the display names of
    /// the cupo and operation CREATORS, so the client can tell which rows it may offer as editable.
    /// <see cref="GetByIdsCallCount"/> is the guard that this stays ONE batched query for the whole
    /// response rather than one per cupo or per operation — same counter idiom as the entity stub.
    /// </summary>
    private sealed class StubUserRepository : IUserRepository
    {
        public Dictionary<Guid, string> Directory { get; } = new();

        public int GetByIdsCallCount { get; private set; }

        public Task<IReadOnlyList<User>> GetByIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct)
        {
            GetByIdsCallCount++;
            IReadOnlyList<User> found = userIds
                .Where(Directory.ContainsKey)
                .Select(id => new User { UserId = id, DisplayName = Directory[id] })
                .ToList();
            return Task.FromResult(found);
        }

        public Task<User?> GetByFirebaseUidAsync(string firebaseUid, CancellationToken ct) => throw new NotImplementedException();
        public Task<User?> GetByUserIdAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
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

    /// <summary>Default creator of the cupos built by <c>Line</c>.</summary>
    private static readonly Guid CupoAuthor = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>Default creator of the operations built by <c>Operation</c> — a DIFFERENT analyst,
    /// so the batched name lookup has to cover both kinds of row rather than one.</summary>
    private static readonly Guid OperationAuthor = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static TenantContext BuildTenant() => new()
    {
        TenantId               = Tenant,
        FirebaseProjectId      = "proj",
        FirebaseCredentialJson = "{}",
        RecaptchaSecretKey     = "secret"
    };

    private static CreditLinesController BuildSut(
        StubEntityRepository         entityRepo,
        StubCreditLineRepository     creditLineRepo,
        StubOperationRepository      operationRepo,
        StubAnalysisResultRepository resultRepo,
        StubUserRepository?          userRepo = null)
    {
        var sut = new CreditLinesController(
            new StubTenantContextAccessor { TenantContext = BuildTenant() }, // tenantContextAccessor
            creditLineRepo,  // creditLineRepository
            operationRepo,   // operationRepository
            entityRepo,      // entityRepository
            null!,           // analysisRepository — only the create/raise paths resolve an analysis
            resultRepo,      // analysisResultRepository
            // userRepository — the structure read still never resolves the CALLER, but it does
            // resolve the creators' display names for the response.
            userRepo ?? new StubUserRepository(),
            null!,           // auditService — nothing is mutated here
            NullLogger<CreditLinesController>.Instance);

        sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim("uid", FirebaseUid) }, "Test"))
            }
        };
        return sut;
    }

    private static CreditLine Line(
        Guid creditLineId, Guid entityId, string status = "Active",
        string currency = "ARS", decimal amount = 1000m, Guid? originAnalysisId = null,
        Guid? createdBy = null) => new()
    {
        CreditLineId     = creditLineId,
        EntityId         = entityId,
        TenantId         = Tenant,
        Amount           = amount,
        Currency         = currency,
        Status           = status,
        CreatedByUserId  = createdBy ?? CupoAuthor,
        OriginAnalysisId = originAnalysisId,
        CreatedAt        = new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc)
    };

    private static CreditLineOperation Operation(
        Guid creditLineId, decimal amount, string status = "Vigente", Guid? createdBy = null) => new()
    {
        OperationId  = Guid.NewGuid(),
        CreditLineId = creditLineId,
        TenantId     = Tenant,
        Amount       = amount,
        Currency     = "ARS",
        Status       = status,
        CreatedByUserId = createdBy ?? OperationAuthor,
        CreatedAt    = new DateTime(2026, 3, 2, 10, 0, 0, DateTimeKind.Utc)
    };

    private static CreditLineStructureResponse BodyOf(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<CreditLineStructureResponse>(ok.Value);
    }

    // ── The change ────────────────────────────────────────────────────────────

    /// <summary>
    /// THE test for this change. The 404 guard must be answered by the existence check and never by
    /// a full-row load — that read dragged five nvarchar(max) snapshots off disk on every request,
    /// including the one this page makes on every open. Asserted on both branches, because the
    /// absent case is the one where it is tempting to reach for the entity.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetStructure_NeverLoadsTheWholeEntityRow(bool entityExists)
    {
        var entityId   = Guid.NewGuid();
        var entityRepo = new StubEntityRepository { Exists = entityExists };

        await BuildSut(entityRepo, new StubCreditLineRepository(), new StubOperationRepository(),
                       new StubAnalysisResultRepository())
            .GetStructureAsync(entityId, CancellationToken.None);

        Assert.Equal(0, entityRepo.GetByIdCallCount);
        Assert.Equal(1, entityRepo.ExistsCallCount);
    }

    // ── The output, unchanged ─────────────────────────────────────────────────

    /// <summary>
    /// A missing entity is still a 404 carrying the exact same detail string. The client matches on
    /// this response, so "how existence is determined" changing must not change what comes back.
    /// </summary>
    [Fact]
    public async Task GetStructure_WhenEntityAbsent_Returns404WithTheSameDetail()
    {
        var creditLineRepo = new StubCreditLineRepository();

        var result = await BuildSut(
                new StubEntityRepository { Exists = false },
                creditLineRepo,
                new StubOperationRepository(),
                new StubAnalysisResultRepository())
            .GetStructureAsync(Guid.NewGuid(), CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, obj.StatusCode);
        Assert.Equal(
            "No se encontró la entidad.",
            Assert.IsAssignableFrom<ProblemDetails>(obj.Value).Detail);

        // The guard still short-circuits: no cupo read happens for an entity that is not there.
        Assert.Equal(0, creditLineRepo.CallCount);
    }

    /// <summary>
    /// An entity with no cupos is a 200 with an empty list, not a 404 — the existence check decides
    /// "the PyME is real", and the empty structure is a legitimate answer for a PyME with no cupo yet.
    /// </summary>
    [Fact]
    public async Task GetStructure_WhenEntityExistsWithNoCupos_Returns200AndEmptyList()
    {
        var result = await BuildSut(
                new StubEntityRepository { Exists = true },
                new StubCreditLineRepository(),
                new StubOperationRepository(),
                new StubAnalysisResultRepository())
            .GetStructureAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Empty(BodyOf(result).CreditLines);
    }

    /// <summary>
    /// The happy path still assembles the whole structure: the cupos, their operations, the derived
    /// Utilizado/Disponible/OperationsCount, and the batched OriginAnalysisId → ResultId back-link.
    /// None of that moved, which is the claim.
    /// </summary>
    [Fact]
    public async Task GetStructure_WhenEntityExists_StillReturnsCuposWithOperationsAndOriginResultId()
    {
        var entityId   = Guid.NewGuid();
        var lineId     = Guid.NewGuid();
        var analysisId = Guid.NewGuid();
        var resultId   = Guid.NewGuid();

        var creditLineRepo = new StubCreditLineRepository
        {
            LinesToReturn = [Line(lineId, entityId, amount: 1000m, originAnalysisId: analysisId)]
        };
        var operationRepo = new StubOperationRepository();
        operationRepo.ByLine[lineId] = [Operation(lineId, 300m), Operation(lineId, 200m, "Cancelada")];

        var resultRepo = new StubAnalysisResultRepository();
        resultRepo.ResultIdByAnalysis[analysisId] = resultId;

        var result = await BuildSut(
                new StubEntityRepository { Exists = true }, creditLineRepo, operationRepo, resultRepo)
            .GetStructureAsync(entityId, CancellationToken.None);

        var cupo = Assert.Single(BodyOf(result).CreditLines);
        Assert.Equal(lineId,   cupo.CreditLineId);
        Assert.Equal(entityId, cupo.EntityId);
        Assert.Equal(1000m,    cupo.Amount);

        // 'Cancelada' does not occupy the cupo; the 'Vigente' 300 does.
        Assert.Equal(300m, cupo.Utilizado);
        Assert.Equal(700m, cupo.Disponible);
        Assert.Equal(1,    cupo.OperationsCount);
        Assert.Equal(2,    cupo.Operations.Count);   // both are still listed

        Assert.Equal(analysisId, cupo.OriginAnalysisId);
        Assert.Equal(resultId,   cupo.OriginAnalysisResultId);
    }

    // ── Authorship on the read model (§5.2) ───────────────────────────────────

    /// <summary>
    /// Authorship reaches the client on BOTH levels — the cupo and each of its operations — because
    /// the two are gated separately: an analyst can own an operation under a colleague's cupo.
    /// </summary>
    [Fact]
    public async Task GetStructure_CarriesCreatorIdAndNameOnCupoAndOperations()
    {
        var entityId = Guid.NewGuid();
        var lineId   = Guid.NewGuid();

        var creditLineRepo = new StubCreditLineRepository { LinesToReturn = [Line(lineId, entityId)] };
        var operationRepo  = new StubOperationRepository();
        operationRepo.ByLine[lineId] = [Operation(lineId, 300m)];

        var userRepo = new StubUserRepository();
        userRepo.Directory[CupoAuthor]      = "Ana Analista";
        userRepo.Directory[OperationAuthor] = "Otro Analista";

        var result = await BuildSut(
                new StubEntityRepository { Exists = true }, creditLineRepo, operationRepo,
                new StubAnalysisResultRepository(), userRepo)
            .GetStructureAsync(entityId, CancellationToken.None);

        var cupo = Assert.Single(BodyOf(result).CreditLines);
        Assert.Equal(CupoAuthor,     cupo.CreatedByUserId);
        Assert.Equal("Ana Analista", cupo.CreatedByName);

        var operation = Assert.Single(cupo.Operations);
        Assert.Equal(OperationAuthor,  operation.CreatedByUserId);
        Assert.Equal("Otro Analista",  operation.CreatedByName);
    }

    /// <summary>
    /// The load-bearing performance guard for this feature, in the same spirit as
    /// <see cref="GetStructure_NeverLoadsTheWholeEntityRow"/>: the names of every cupo and every
    /// operation author come back in exactly ONE query, not one per row. Three cupos with two
    /// operations each — nine rows, four distinct authors — must still be a single lookup.
    /// </summary>
    [Fact]
    public async Task GetStructure_ResolvesEveryCreatorNameInOneBatchedQuery()
    {
        var entityId = Guid.NewGuid();
        var lineIds  = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var author2  = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var author3  = Guid.Parse("44444444-4444-4444-4444-444444444444");

        var creditLineRepo = new StubCreditLineRepository
        {
            LinesToReturn = lineIds.Select(id => Line(id, entityId)).ToList()
        };
        var operationRepo = new StubOperationRepository();
        foreach (var id in lineIds)
            operationRepo.ByLine[id] = [Operation(id, 100m), Operation(id, 50m, createdBy: author2)];

        var userRepo = new StubUserRepository();
        userRepo.Directory[CupoAuthor]      = "Ana Analista";
        userRepo.Directory[OperationAuthor] = "Otro Analista";
        userRepo.Directory[author2]         = "Tercer Analista";
        userRepo.Directory[author3]         = "Nunca Aparece";

        var result = await BuildSut(
                new StubEntityRepository { Exists = true }, creditLineRepo, operationRepo,
                new StubAnalysisResultRepository(), userRepo)
            .GetStructureAsync(entityId, CancellationToken.None);

        Assert.Equal(3, BodyOf(result).CreditLines.Count);
        Assert.Equal(1, userRepo.GetByIdsCallCount);
    }

    /// <summary>An author who no longer resolves reads as "Usuario", never as an empty string.</summary>
    [Fact]
    public async Task GetStructure_WhenCreatorNoLongerResolves_FallsBackToUsuario()
    {
        var entityId = Guid.NewGuid();
        var lineId   = Guid.NewGuid();

        var creditLineRepo = new StubCreditLineRepository { LinesToReturn = [Line(lineId, entityId)] };
        var operationRepo  = new StubOperationRepository();
        operationRepo.ByLine[lineId] = [Operation(lineId, 300m)];

        // Deliberately empty directory: both authors have left the tenant.
        var result = await BuildSut(
                new StubEntityRepository { Exists = true }, creditLineRepo, operationRepo,
                new StubAnalysisResultRepository(), new StubUserRepository())
            .GetStructureAsync(entityId, CancellationToken.None);

        var cupo = Assert.Single(BodyOf(result).CreditLines);
        Assert.Equal("Usuario", cupo.CreatedByName);
        Assert.Equal("Usuario", Assert.Single(cupo.Operations).CreatedByName);

        // The ids themselves are still reported — only the label degrades.
        Assert.Equal(CupoAuthor, cupo.CreatedByUserId);
    }
}
