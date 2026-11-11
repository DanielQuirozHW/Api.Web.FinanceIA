// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-25

using Api.Web.FinanceIA.Tests.Servicios.ScoreReliability;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Covers the narrow LIST projection on <see cref="EntitiesController.GetAllAsync"/>: GET
/// /api/entities returns <see cref="EntityListItemResponse"/> rows instead of the full
/// <see cref="EntityResponse"/> it used to reuse, which deserialised and shipped all five
/// nvarchar(max) source snapshots (fiscal / LUFE / BCRA / CASFOG / NOSIS) on every row of every page.
///
/// The load-bearing test is <see cref="GetAll_MalformedSourceBlobs_SucceedAndNothingIsEverParsed"/>.
/// The only thing that logs a Warning on this path is the "Could not parse …" catch in
/// <c>EntitiesController.ParseSnapshot</c>, so asserting ZERO warnings against a page of rows whose
/// blobs are garbage proves the list never deserialised them — not merely that it survived doing so.
///
/// Hand-rolled private sealed stubs (no mock library). Only the repositories this path reads are
/// stubbed; the rest of the 19 ctor dependencies are passed as null!. Argument order mirrors the
/// ctor signature.
/// </summary>
public sealed class EntitiesControllerListProjectionTests
{
    private const string Tenant = "tenant-1";

    // ── Stubs ─────────────────────────────────────────────────────────────────
    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private sealed class StubEntityRepository : IEntityRepository
    {
        /// <summary>
        /// Fixtures are full <see cref="Entity"/> rows, because that is what the table holds.
        /// <see cref="GetListPageByTenantAsync"/> then projects and pages them exactly as
        /// <c>EntityRepository.BuildListPage</c> does, so a fixture carrying garbage in all five
        /// snapshots still proves something here: those columns cannot reach the controller, because
        /// the projection is where they get dropped. That the real SQL never *reads* them is proved
        /// separately, against the emitted query, in EntityRepositoryListPageTests.
        /// </summary>
        public IReadOnlyList<Entity> EntitiesToReturn { get; set; } = Array.Empty<Entity>();
        public Entity? EntityToReturn { get; set; }

        /// <summary>
        /// Stands in for the legajo side of BuildDetail's subquery (DDL 069), which a stub over
        /// <see cref="Entity"/> rows cannot join: the monotributo category of the linked legajo.
        /// </summary>
        public string? LegajoCategoryToReturn { get; set; }

        /// <summary>
        /// How often the list fell back to a full-row read of the whole tenant. Must stay 0.
        /// </summary>
        public int GetAllCallCount { get; private set; }

        public int? LastSkip { get; private set; }
        public int? LastTake { get; private set; }

        public Task<IReadOnlyList<Entity>> GetAllByTenantAsync(string tenantId, CancellationToken ct)
        {
            GetAllCallCount++;
            return Task.FromResult(EntitiesToReturn);
        }

        public Task<(IReadOnlyList<EntityListProjection> Items, int TotalCount)> GetListPageByTenantAsync(
            string tenantId, int skip, int take, CancellationToken ct)
        {
            LastSkip = skip;
            LastTake = take;

            IReadOnlyList<EntityListProjection> items = EntitiesToReturn
                .Select(e => new EntityListProjection
                {
                    EntityId           = e.EntityId,
                    CUIT               = e.CUIT,
                    BusinessName       = e.BusinessName,
                    TradeName          = e.TradeName,
                    Sector             = e.Sector,
                    MipymeCategory     = e.MipymeCategory,
                    IsMonitored        = e.IsMonitored,
                    RiskScore          = e.RiskScore,
                    RiskScoreUpdatedAt = e.RiskScoreUpdatedAt,
                    CreatedAt          = e.CreatedAt,
                    CreatedBy          = e.CreatedBy,
                    PersonType         = e.PersonType,
                    FiscalRegime       = e.FiscalRegime
                })
                .OrderByDescending(p => p.CreatedAt)
                .ThenBy(p => p.EntityId)
                .Skip(skip)
                .Take(take)
                .ToList();

            return Task.FromResult((items, EntitiesToReturn.Count));
        }

        public Task<Entity?> GetByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => Task.FromResult(EntityToReturn);

        /// <summary>
        /// Projects the fixture row exactly as <c>EntityRepository.BuildDetail</c> does — carrying
        /// FISCAL and LUFE, dropping BCRA, CASFOG and NOSIS while keeping their timestamps. So the
        /// same argument the list stub makes holds for the detail: a fixture with garbage in all
        /// five snapshots proves the three lazy columns cannot reach the controller, because the
        /// projection is where they get dropped. That the real SQL never *reads* them is proved
        /// separately, against the emitted query, in EntityRepositoryDetailTests.
        /// </summary>
        public Task<EntityDetailProjection?> GetDetailByIdAsync(
            Guid entityId, string tenantId, CancellationToken ct)
        {
            var e = EntityToReturn;
            if (e is null) return Task.FromResult<EntityDetailProjection?>(null);

            return Task.FromResult<EntityDetailProjection?>(new EntityDetailProjection
            {
                EntityId            = e.EntityId,
                TenantId            = e.TenantId,
                CUIT                = e.CUIT,
                BusinessName        = e.BusinessName,
                TradeName           = e.TradeName,
                Sector              = e.Sector,
                MipymeCategory      = e.MipymeCategory,
                SgrOneClickEntityId = e.SgrOneClickEntityId,
                RiskScore           = e.RiskScore,
                RiskScoreUpdatedAt  = e.RiskScoreUpdatedAt,
                IsMonitored         = e.IsMonitored,
                CreatedAt           = e.CreatedAt,
                UpdatedAt           = e.UpdatedAt,
                CreatedBy           = e.CreatedBy,
                FiscalDataJson      = e.FiscalDataJson,
                FiscalDataUpdatedAt = e.FiscalDataUpdatedAt,
                LufeDataJson        = e.LufeDataJson,
                LufeDataUpdatedAt   = e.LufeDataUpdatedAt,
                BcraDataUpdatedAt   = e.BcraDataUpdatedAt,
                CasfogDataUpdatedAt = e.CasfogDataUpdatedAt,
                NosisDataUpdatedAt  = e.NosisDataUpdatedAt,
                PersonType          = e.PersonType,
                FiscalRegime        = e.FiscalRegime,
                PersonLegajoId      = e.PersonLegajoId,
                MonotributoCategory = e.PersonLegajoId is null ? null : LegajoCategoryToReturn
            });
        }

        public Task<bool> ExistsByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
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
        public IReadOnlySet<Guid> WithLineToReturn { get; set; } = new HashSet<Guid>();
        public int CallCount { get; private set; }
        public List<Guid> LastEntityIds { get; private set; } = [];

        public Task<IReadOnlySet<Guid>> GetEntityIdsWithNonTerminalLineAsync(
            IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct)
        {
            CallCount++;
            LastEntityIds = entityIds.ToList();
            return Task.FromResult(WithLineToReturn);
        }

        /// <summary>Main cupo per entity, keyed by entity id. Entities absent from it have no Active cupo.</summary>
        public IReadOnlyDictionary<Guid, MainCreditLineProjection> MainLinesToReturn { get; set; }
            = new Dictionary<Guid, MainCreditLineProjection>();

        /// <summary>How often the main-cupo projection was queried. Must be 1 per list request, not 1 per row.</summary>
        public int MainCallCount { get; private set; }

        public List<Guid> LastMainEntityIds { get; private set; } = [];

        public Task<IReadOnlyDictionary<Guid, MainCreditLineProjection>> GetMainActiveByEntityIdsAsync(
            IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct)
        {
            MainCallCount++;
            LastMainEntityIds = entityIds.ToList();
            return Task.FromResult(MainLinesToReturn);
        }

        public Task<IReadOnlyList<CreditLine>> GetByOriginAnalysisAsync(Guid analysisId, string tenantId, CancellationToken ct)
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
        public Task<IReadOnlyList<Guid>> GetIdsByOriginAnalysisAsync(Guid analysisId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<Guid>> GetActiveIdsByOriginAnalysisAsync(Guid analysisId, string tenantId, CancellationToken ct)
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

    private sealed class StubAnalysisRepository : IAnalysisRepository
    {
        public Task<BalanceBaseFiguresProjection?> GetBalanceBaseFiguresAsync(Guid analysisId, string tenantId, CancellationToken ct)
            => Task.FromResult<BalanceBaseFiguresProjection?>(null);


        // The quorum-crossing write (DDL 067). Throws because no test in this suite confirms a
        // pre-approval, so an unexpected call must fail loudly rather than pass silently.
        public Task<bool> TryConfirmFromPreApprovedAsync(
            Guid analysisId, string tenantId, Guid reviewedBy, DateTime reviewedAt,
            decimal? decisionAmount, string? decisionCurrency, string? confirmedOperationsJson,
            string? reviewNotes, CancellationToken ct)
            => throw new NotImplementedException();
        public IReadOnlyDictionary<Guid, string> StatusesToReturn { get; set; } = new Dictionary<Guid, string>();
        public int CallCount { get; private set; }

        public Task<IReadOnlyDictionary<Guid, string>> GetLatestStatusByEntityIdsAsync(
            IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(StatusesToReturn);
        }

        /// <summary>The latest scored pair per entity. Entities absent from it have no score.</summary>
        public IReadOnlyDictionary<Guid, EntityLatestScoreProjection> ScoresToReturn { get; set; }
            = new Dictionary<Guid, EntityLatestScoreProjection>();

        public int ScoreCallCount { get; private set; }
        public string? LastScoreTenantId { get; private set; }

        public Task<IReadOnlyDictionary<Guid, EntityLatestScoreProjection>> GetLatestScoreByEntityIdsAsync(
            IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct)
        {
            ScoreCallCount++;
            LastScoreTenantId = tenantId;
            return Task.FromResult(ScoresToReturn);
        }

        public Task<Analysis?> GetByIdAsync(Guid analysisId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Analysis> CreateAsync(Analysis analysis, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<Analysis>> GetByIdsAsync(IEnumerable<Guid> analysisIds, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Analysis?> GetLatestByDocumentIdAsync(Guid documentId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Analysis> UpdateAsync(Analysis analysis, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<int> CountByTenantInPeriodAsync(string tenantId, DateTime periodStartUtc, DateTime periodEndUtc, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class StubUserRepository : IUserRepository
    {
        public IReadOnlyList<User> UsersToReturn { get; set; } = Array.Empty<User>();
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<User>> GetByIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(UsersToReturn);
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

    /// <summary>
    /// Captures Warning-level log calls. The entities mapping path logs a warning in exactly one
    /// place — the "Could not parse …" catch in <c>EntitiesController.ParseSnapshot</c> — so an empty
    /// <see cref="Warnings"/> list against malformed blobs is direct evidence they were never read.
    /// </summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static TenantContext BuildTenant() => new()
    {
        TenantId               = Tenant,
        FirebaseProjectId      = "proj",
        FirebaseCredentialJson = "{}",
        RecaptchaSecretKey     = "secret"
    };

    private static Entity BuildEntity(Guid? id = null, Guid? createdBy = null) => new()
    {
        EntityId           = id ?? Guid.NewGuid(),
        TenantId           = Tenant,
        CUIT               = "30-71234567-1",
        BusinessName       = "Metalúrgica del Sur S.A.",
        TradeName          = "MetalSur",
        Sector             = "Industria",
        MipymeCategory     = "II",
        IsMonitored        = true,
        RiskScore          = 63.25m,
        RiskScoreUpdatedAt = new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc),
        CreatedAt          = new DateTime(2025, 11, 3, 8, 30, 0, DateTimeKind.Utc),
        UpdatedAt          = new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc),
        CreatedBy          = createdBy
    };

    private static EntitiesController BuildSut(
        StubEntityRepository     entityRepo,
        StubCreditLineRepository creditLineRepo,
        StubAnalysisRepository   analysisRepo,
        StubUserRepository       userRepo,
        RecordingLogger<EntitiesController> logger,
        bool                     withTenant = true)
        => new(
            new StubTenantContextAccessor { TenantContext = withTenant ? BuildTenant() : null }, // ITenantContextAccessor
            entityRepo,     // IEntityRepository
            null!,          // IArcaPadronService
            null!,          // IArcaConstanciaService
            null!,          // IArcaWsaaService
            null!,          // IApiCallLogService
            null!,          // IAuditService
            null!,          // ILufeService
            null!,          // IBcraService
            null!,          // ICasfogService
            null!,          // INosisService
            null!,          // IIntegrationAccessService — never resolved by the read endpoints
            null!,          // IDocumentRepository
            null!,          // IDocumentStorageService
            userRepo,       // IUserRepository
            creditLineRepo, // ICreditLineRepository
            analysisRepo,   // IAnalysisRepository
            null!,          // INoteRepository
            null!,          // IPersonLegajoRepository
            null!,          // INosisReportRepository
            null!,          // INosisManagerReportParser
            null!,          // KeyedAsyncLock
            null!,          // IConfiguration
            logger)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every field the list view renders survives the projection.
    /// </summary>
    [Fact]
    public async Task GetAll_LightRow_CarriesEveryProjectedField()
    {
        var entity = BuildEntity();
        var sut = BuildSut(
            new StubEntityRepository { EntitiesToReturn = new[] { entity } },
            new StubCreditLineRepository(), new StubAnalysisRepository(), new StubUserRepository(),
            new RecordingLogger<EntitiesController>());

        var response = await sut.GetAllAsync(cancellationToken: CancellationToken.None);
        var body = Assert.IsType<EntityListResponse>(Assert.IsType<OkObjectResult>(response).Value);

        var row = Assert.Single(body.Items);
        Assert.Equal(entity.EntityId, row.EntityId);
        Assert.Equal("30-71234567-1", row.CUIT);
        Assert.Equal("Metalúrgica del Sur S.A.", row.BusinessName);
        Assert.Equal("MetalSur", row.TradeName);
        Assert.Equal("Industria", row.Sector);
        Assert.Equal("II", row.MipymeCategory);
        Assert.True(row.IsMonitored);
        Assert.Equal(63.25m, row.RiskScore);
        Assert.Equal(new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc), row.RiskScoreUpdatedAt);
        Assert.Equal(new DateTime(2025, 11, 3, 8, 30, 0, DateTimeKind.Utc), row.CreatedAt);
    }

    /// <summary>
    /// The wire contract, locked by reflection. Catches a heavy snapshot creeping back onto the
    /// list row AND a rendered field being silently dropped.
    /// </summary>
    [Fact]
    public void EntityListItemResponse_ExposesExactlyTheProjectedProperties()
    {
        var actual = typeof(EntityListItemResponse)
            .GetProperties()
            .Select(p => p.Name)
            .ToHashSet();

        var expected = new HashSet<string>
        {
            nameof(EntityListItemResponse.EntityId),
            nameof(EntityListItemResponse.CUIT),
            nameof(EntityListItemResponse.BusinessName),
            nameof(EntityListItemResponse.TradeName),
            nameof(EntityListItemResponse.Sector),
            nameof(EntityListItemResponse.MipymeCategory),
            nameof(EntityListItemResponse.IsMonitored),
            nameof(EntityListItemResponse.RiskScore),
            nameof(EntityListItemResponse.RiskScoreUpdatedAt),
            nameof(EntityListItemResponse.CreatedAt),
            nameof(EntityListItemResponse.CreatedBy),
            // Persona humana (DDL 069): two short scalars off the entity, no legajo join.
            nameof(EntityListItemResponse.PersonType),
            nameof(EntityListItemResponse.FiscalRegime),
            nameof(EntityListItemResponse.CreatedByName),
            nameof(EntityListItemResponse.HasCreditLine),
            nameof(EntityListItemResponse.LatestAnalysisStatus),
            // The latest stored score + level pair (score level contract), shared with the detail.
            nameof(EntityListItemResponse.LatestScore),
            nameof(EntityListItemResponse.LatestRiskLevel),
            nameof(EntityListItemResponse.LatestScoreAnalysisId),
            nameof(EntityListItemResponse.LatestScoredAt),
            // The main cupo, for the PyMEs table's utilisation bar. LIST ONLY — deliberately absent
            // from IEntityProjectionTarget so the detail DTO does not silently inherit them.
            nameof(EntityListItemResponse.MainCreditLineAmount),
            nameof(EntityListItemResponse.MainCreditLineDisponible),
            nameof(EntityListItemResponse.MainCreditLineCurrency),
            // Whether LatestScore is provisional (K21). LIST ONLY, like the cupo fields.
            nameof(EntityListItemResponse.LatestScoreProvisional)
        };

        Assert.Equal(expected, actual);

        // ...and the envelope actually ships the light row, not the detail DTO.
        Assert.Equal(
            typeof(IReadOnlyList<EntityListItemResponse>),
            typeof(EntityListResponse).GetProperty(nameof(EntityListResponse.Items))!.PropertyType);
    }

    /// <summary>
    /// THE test for this change. Every one of the five source snapshots is unparseable garbage, and
    /// the page still comes back correct WITHOUT a single parse warning — which is only possible if
    /// the list path never touched those columns. Under the old code this logged five warnings per
    /// row.
    /// </summary>
    [Fact]
    public async Task GetAll_MalformedSourceBlobs_SucceedAndNothingIsEverParsed()
    {
        var entity = BuildEntity();
        entity.FiscalDataJson = "{not json";
        entity.LufeDataJson   = "{not json";
        entity.BcraDataJson   = "{not json";
        entity.CasfogDataJson = "{not json";
        entity.NosisDataJson  = "{not json";

        var logger = new RecordingLogger<EntitiesController>();
        var sut = BuildSut(
            new StubEntityRepository { EntitiesToReturn = new[] { entity } },
            new StubCreditLineRepository(), new StubAnalysisRepository(), new StubUserRepository(),
            logger);

        var response = await sut.GetAllAsync(cancellationToken: CancellationToken.None);
        var body = Assert.IsType<EntityListResponse>(Assert.IsType<OkObjectResult>(response).Value);

        var row = Assert.Single(body.Items);
        Assert.Equal(entity.EntityId, row.EntityId);
        Assert.Equal("Metalúrgica del Sur S.A.", row.BusinessName);
        Assert.Empty(logger.Warnings);
    }

    /// <summary>
    /// The projected fields still land on the light row: the entity with a live cupo, an analysis
    /// and a known author gets all three; the one with none gets the honest false/null.
    /// </summary>
    [Fact]
    public async Task GetAll_Enrichment_SetsHasCreditLineLatestStatusAndCreatedByName()
    {
        var author = Guid.NewGuid();
        var withEverything = BuildEntity(createdBy: author);
        var withNothing    = BuildEntity();

        var sut = BuildSut(
            new StubEntityRepository { EntitiesToReturn = new[] { withEverything, withNothing } },
            new StubCreditLineRepository { WithLineToReturn = new HashSet<Guid> { withEverything.EntityId } },
            new StubAnalysisRepository
            {
                StatusesToReturn = new Dictionary<Guid, string> { [withEverything.EntityId] = "Approved" }
            },
            new StubUserRepository { UsersToReturn = new[] { new User { UserId = author, DisplayName = "Ana Gómez" } } },
            new RecordingLogger<EntitiesController>());

        var response = await sut.GetAllAsync(cancellationToken: CancellationToken.None);
        var body = Assert.IsType<EntityListResponse>(Assert.IsType<OkObjectResult>(response).Value);

        var rich = Assert.Single(body.Items, i => i.EntityId == withEverything.EntityId);
        Assert.True(rich.HasCreditLine);
        Assert.Equal("Approved", rich.LatestAnalysisStatus);
        Assert.Equal("Ana Gómez", rich.CreatedByName);

        var bare = Assert.Single(body.Items, i => i.EntityId == withNothing.EntityId);
        Assert.False(bare.HasCreditLine);
        Assert.Null(bare.LatestAnalysisStatus);
        Assert.Null(bare.CreatedByName);
    }

    /// <summary>
    /// The enrichment stays set-based: one query per source for the whole page, scoped to the
    /// page's ids rather than the tenant's. This is the no-N+1 lock.
    /// </summary>
    [Fact]
    public async Task GetAll_Enrichment_QueriesEachSourceExactlyOnce()
    {
        var entities = new[] { BuildEntity(), BuildEntity(), BuildEntity() };
        var creditLineRepo = new StubCreditLineRepository();
        var analysisRepo   = new StubAnalysisRepository();
        var userRepo       = new StubUserRepository();

        var sut = BuildSut(
            new StubEntityRepository { EntitiesToReturn = entities },
            creditLineRepo, analysisRepo, userRepo,
            new RecordingLogger<EntitiesController>());

        await sut.GetAllAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(1, creditLineRepo.CallCount);
        Assert.Equal(1, analysisRepo.CallCount);
        Assert.Equal(1, analysisRepo.ScoreCallCount);
        Assert.Equal(1, userRepo.CallCount);
        Assert.Equal(entities.Select(e => e.EntityId).OrderBy(g => g), creditLineRepo.LastEntityIds.OrderBy(g => g));
    }

    /// <summary>
    /// The detail contract after the lazy-load change. GetById still carries every detail-only
    /// scalar the list row drops, still parses the two snapshots the Resumen tab renders, and still
    /// enriches through the shared projection contract — but BCRA, CASFOG and NOSIS now arrive as
    /// timestamps ONLY. Their payloads come from GET {id}/bcra, /casfog and /nosis when the user
    /// opens that tab.
    ///
    /// <para>
    /// This test used to be <c>GetById_StillReturnsTheFullDtoWithBlobsIntact</c>, asserting all five
    /// snapshots were parsed. That was the correct guard while the detail path was the one place
    /// that legitimately loaded every blob; it is exactly the behaviour this change reverses, so the
    /// assertions are inverted rather than the test deleted.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GetById_ParsesResumenSnapshots_AndShipsTheLazyOnesAsTimestampsOnly()
    {
        var author = Guid.NewGuid();
        var entity = BuildEntity(createdBy: author);
        entity.FiscalDataJson = "{}";
        entity.LufeDataJson   = "{}";
        entity.BcraDataJson   = "{}";
        entity.CasfogDataJson = "{}";
        entity.NosisDataJson  = "{}";

        entity.FiscalDataUpdatedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        entity.LufeDataUpdatedAt   = new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc);
        entity.BcraDataUpdatedAt   = new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc);
        entity.CasfogDataUpdatedAt = new DateTime(2026, 8, 4, 0, 0, 0, DateTimeKind.Utc);
        entity.NosisDataUpdatedAt  = new DateTime(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc);

        var sut = BuildSut(
            new StubEntityRepository { EntityToReturn = entity },
            new StubCreditLineRepository { WithLineToReturn = new HashSet<Guid> { entity.EntityId } },
            new StubAnalysisRepository
            {
                StatusesToReturn = new Dictionary<Guid, string> { [entity.EntityId] = "PendingReview" }
            },
            new StubUserRepository { UsersToReturn = new[] { new User { UserId = author, DisplayName = "Ana Gómez" } } },
            new RecordingLogger<EntitiesController>());

        var response = await sut.GetById(entity.EntityId, CancellationToken.None);
        var body = Assert.IsType<EntityResponse>(Assert.IsType<OkObjectResult>(response).Value);

        // Detail-only fields the list row deliberately drops are still here.
        Assert.Equal(Tenant, body.TenantId);
        Assert.Equal(entity.UpdatedAt, body.UpdatedAt);

        // The two the Resumen tab renders still parse on the detail path.
        Assert.NotNull(body.Fiscal);
        Assert.NotNull(body.Lufe);

        // The three lazy ones do not — the projection never carried the columns.
        Assert.Null(body.Bcra);
        Assert.Null(body.Casfog);
        Assert.Null(body.Nosis);

        // ...but every timestamp does, so the detail can still render "last refreshed".
        Assert.Equal(entity.FiscalDataUpdatedAt, body.FiscalDataUpdatedAt);
        Assert.Equal(entity.LufeDataUpdatedAt,   body.LufeDataUpdatedAt);
        Assert.Equal(entity.BcraDataUpdatedAt,   body.BcraDataUpdatedAt);
        Assert.Equal(entity.CasfogDataUpdatedAt, body.CasfogDataUpdatedAt);
        Assert.Equal(entity.NosisDataUpdatedAt,  body.NosisDataUpdatedAt);

        // ...and enrichment still applies through the shared projection contract.
        Assert.True(body.HasCreditLine);
        Assert.Equal("PendingReview", body.LatestAnalysisStatus);
        Assert.Equal("Ana Gómez", body.CreatedByName);
    }

    /// <summary>
    /// The detail counterpart of <see cref="GetAll_MalformedSourceBlobs_SucceedAndNothingIsEverParsed"/>.
    /// BCRA, CASFOG and NOSIS hold unparseable garbage while FISCAL and LUFE are valid, and the
    /// detail still comes back with ZERO warnings — which is only possible if the three lazy
    /// columns were never deserialised. Under the old code this logged three warnings per request.
    /// </summary>
    [Fact]
    public async Task GetById_MalformedLazyBlobs_SucceedAndNothingIsEverParsed()
    {
        var entity = BuildEntity();
        entity.FiscalDataJson = "{}";
        entity.LufeDataJson   = "{}";
        entity.BcraDataJson   = "{not json";
        entity.CasfogDataJson = "{not json";
        entity.NosisDataJson  = "{not json";

        var logger = new RecordingLogger<EntitiesController>();
        var sut = BuildSut(
            new StubEntityRepository { EntityToReturn = entity },
            new StubCreditLineRepository(), new StubAnalysisRepository(), new StubUserRepository(),
            logger);

        var response = await sut.GetById(entity.EntityId, CancellationToken.None);
        var body = Assert.IsType<EntityResponse>(Assert.IsType<OkObjectResult>(response).Value);

        Assert.NotNull(body.Fiscal);
        Assert.NotNull(body.Lufe);
        Assert.Null(body.Bcra);
        Assert.Null(body.Casfog);
        Assert.Null(body.Nosis);
        Assert.Empty(logger.Warnings);
    }

    /// <summary>
    /// Persona humana (DDL 069): the detail ships personType, fiscalRegime and personLegajoId off the
    /// entity and monotributoCategory off its legajo — the four fields the Portal's detail reads.
    /// </summary>
    [Fact]
    public async Task GetById_Humana_ShipsThePersonFields()
    {
        var legajoId = Guid.NewGuid();
        var entity   = BuildEntity();
        entity.CUIT           = "20-11111111-2";
        entity.PersonType     = PersonTypes.Humana;
        entity.FiscalRegime   = FiscalRegimes.Monotributo;
        entity.PersonLegajoId = legajoId;

        var sut = BuildSut(
            new StubEntityRepository { EntityToReturn = entity, LegajoCategoryToReturn = "H" },
            new StubCreditLineRepository(),
            new StubAnalysisRepository(),
            new StubUserRepository(),
            new RecordingLogger<EntitiesController>());

        var response = await sut.GetById(entity.EntityId, CancellationToken.None);
        var body = Assert.IsType<EntityResponse>(Assert.IsType<OkObjectResult>(response).Value);

        Assert.Equal(PersonTypes.Humana,        body.PersonType);
        Assert.Equal(FiscalRegimes.Monotributo, body.FiscalRegime);
        Assert.Equal(legajoId,                  body.PersonLegajoId);
        Assert.Equal("H",                       body.MonotributoCategory);
    }

    /// <summary>An id with no entity in this tenant is a 404, not an empty 200.</summary>
    [Fact]
    public async Task GetById_UnknownEntity_Returns404()
    {
        var sut = BuildSut(
            new StubEntityRepository { EntityToReturn = null },
            new StubCreditLineRepository(), new StubAnalysisRepository(), new StubUserRepository(),
            new RecordingLogger<EntitiesController>());

        var response = await sut.GetById(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(response).StatusCode);
    }

    /// <summary>An unresolved tenant context is a 500 before any read is attempted.</summary>
    [Fact]
    public async Task GetById_WithoutTenantContext_Returns500()
    {
        var sut = BuildSut(
            new StubEntityRepository { EntityToReturn = BuildEntity() },
            new StubCreditLineRepository(), new StubAnalysisRepository(), new StubUserRepository(),
            new RecordingLogger<EntitiesController>(),
            withTenant: false);

        var response = await sut.GetById(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(500, Assert.IsType<ObjectResult>(response).StatusCode);
    }

    /// <summary>
    /// Paging guards are untouched by the projection, and Total still counts the whole tenant set.
    /// </summary>
    [Fact]
    public async Task GetAll_PagingGuards_ClampAndTotalCountsTheFullSet()
    {
        var sut = BuildSut(
            new StubEntityRepository { EntitiesToReturn = new[] { BuildEntity(), BuildEntity(), BuildEntity() } },
            new StubCreditLineRepository(), new StubAnalysisRepository(), new StubUserRepository(),
            new RecordingLogger<EntitiesController>());

        var response = await sut.GetAllAsync(page: 0, pageSize: 0, cancellationToken: CancellationToken.None);
        var body = Assert.IsType<EntityListResponse>(Assert.IsType<OkObjectResult>(response).Value);

        Assert.Equal(1, body.Page);
        Assert.Equal(20, body.PageSize);
        Assert.Equal(3, body.Total);
        Assert.Equal(3, body.Items.Count);
    }

    /// <summary>
    /// Paging is pushed to the database: the controller hands the repository the real skip/take and
    /// never asks for a full-row read. Before this change it loaded the entire tenant — every row,
    /// all five nvarchar(max) snapshots included — and called Skip/Take on the resulting List.
    /// </summary>
    [Fact]
    public async Task GetAll_PagesAtTheDatabase_AndNeverLoadsTheWholeTenant()
    {
        var entities = Enumerable.Range(1, 5)
            .Select(d =>
            {
                var e = BuildEntity();
                e.CreatedAt = new DateTime(2025, 11, d, 8, 30, 0, DateTimeKind.Utc);
                return e;
            })
            .ToArray();

        var entityRepo = new StubEntityRepository { EntitiesToReturn = entities };
        var sut = BuildSut(
            entityRepo,
            new StubCreditLineRepository(), new StubAnalysisRepository(), new StubUserRepository(),
            new RecordingLogger<EntitiesController>());

        var response = await sut.GetAllAsync(page: 3, pageSize: 2, cancellationToken: CancellationToken.None);
        var body = Assert.IsType<EntityListResponse>(Assert.IsType<OkObjectResult>(response).Value);

        Assert.Equal(4, entityRepo.LastSkip);   // (3 - 1) * 2
        Assert.Equal(2, entityRepo.LastTake);
        Assert.Equal(0, entityRepo.GetAllCallCount);
        Assert.Equal(5, body.Total);
        Assert.Equal(3, body.Page);
        Assert.Single(body.Items);              // the 5th row is the only one left on page 3
    }

    /// <summary>
    /// The tenant guard still fires before the repository is touched.
    /// </summary>
    [Fact]
    public async Task GetAll_WhenTenantContextMissing_Returns500AndNeverQueries()
    {
        var entityRepo = new StubEntityRepository();
        var sut = BuildSut(
            entityRepo,
            new StubCreditLineRepository(), new StubAnalysisRepository(), new StubUserRepository(),
            new RecordingLogger<EntitiesController>(),
            withTenant: false);

        var response = await sut.GetAllAsync(cancellationToken: CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(response);
        Assert.Equal(500, problem.StatusCode);
        Assert.Equal(0, entityRepo.GetAllCallCount);
        Assert.Null(entityRepo.LastSkip);   // the paged read was not reached either
    }

    // ── Main credit line (cupo) on the list row ───────────────────────────────

    private static MainCreditLineProjection MainCupo(
        Guid entityId, decimal amount, decimal utilizado, string currency) => new()
    {
        EntityId     = entityId,
        CreditLineId = Guid.NewGuid(),
        Amount       = amount,
        Currency     = currency,
        CreatedAt    = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc),
        Utilizado    = utilizado
    };

    /// <summary>
    /// The three main-cupo fields reach the wire together, and disponible is amount minus the
    /// cupo's utilizado — so the Portal can draw the utilisation bar from the list payload alone.
    /// </summary>
    [Fact]
    public async Task GetAll_EntityWithActiveCupo_CarriesAmountDisponibleAndCurrency()
    {
        var entity = BuildEntity();
        var creditLineRepo = new StubCreditLineRepository
        {
            MainLinesToReturn = new Dictionary<Guid, MainCreditLineProjection>
            {
                [entity.EntityId] = MainCupo(entity.EntityId, amount: 1_000_000m, utilizado: 250_000m, "ARS")
            }
        };

        var sut = BuildSut(
            new StubEntityRepository { EntitiesToReturn = new[] { entity } },
            creditLineRepo, new StubAnalysisRepository(), new StubUserRepository(),
            new RecordingLogger<EntitiesController>());

        var body = Assert.IsType<EntityListResponse>(
            Assert.IsType<OkObjectResult>(await sut.GetAllAsync(cancellationToken: CancellationToken.None)).Value);

        var row = Assert.Single(body.Items);
        Assert.Equal(1_000_000m, row.MainCreditLineAmount);
        Assert.Equal(750_000m,   row.MainCreditLineDisponible);
        Assert.Equal("ARS",      row.MainCreditLineCurrency);
    }

    /// <summary>
    /// An entity with no Active cupo leaves all three null TOGETHER — never a figure without its
    /// currency, which the client would have no safe way to render.
    /// </summary>
    [Fact]
    public async Task GetAll_EntityWithoutActiveCupo_LeavesAllThreeFieldsNull()
    {
        var entity = BuildEntity();
        var sut = BuildSut(
            new StubEntityRepository { EntitiesToReturn = new[] { entity } },
            new StubCreditLineRepository(),   // empty MainLinesToReturn
            new StubAnalysisRepository(), new StubUserRepository(),
            new RecordingLogger<EntitiesController>());

        var body = Assert.IsType<EntityListResponse>(
            Assert.IsType<OkObjectResult>(await sut.GetAllAsync(cancellationToken: CancellationToken.None)).Value);

        var row = Assert.Single(body.Items);
        Assert.Null(row.MainCreditLineAmount);
        Assert.Null(row.MainCreditLineDisponible);
        Assert.Null(row.MainCreditLineCurrency);
    }

    /// <summary>
    /// THE PREDICATE DIFFERENCE, locked. hasCreditLine counts 'Active' OR 'Suspended'; the main
    /// cupo is 'Active'-only. An entity whose only cupo is Suspended therefore reports
    /// hasCreditLine: true with all three figures null — correct, because a suspended cupo lends
    /// nothing further and has no live tope to draw a bar for. A future change that collapses the
    /// two predicates into one breaks this test, which is the point.
    /// </summary>
    [Fact]
    public async Task GetAll_WhenOnlyCupoIsSuspended_HasCreditLineButNoMainCupoFigures()
    {
        var entity = BuildEntity();
        var creditLineRepo = new StubCreditLineRepository
        {
            // The Suspended cupo counts as a line...
            WithLineToReturn  = new HashSet<Guid> { entity.EntityId },
            // ...but is not Active, so the main-cupo projection returns nothing for it.
            MainLinesToReturn = new Dictionary<Guid, MainCreditLineProjection>()
        };

        var sut = BuildSut(
            new StubEntityRepository { EntitiesToReturn = new[] { entity } },
            creditLineRepo, new StubAnalysisRepository(), new StubUserRepository(),
            new RecordingLogger<EntitiesController>());

        var body = Assert.IsType<EntityListResponse>(
            Assert.IsType<OkObjectResult>(await sut.GetAllAsync(cancellationToken: CancellationToken.None)).Value);

        var row = Assert.Single(body.Items);
        Assert.True(row.HasCreditLine);
        Assert.Null(row.MainCreditLineAmount);
        Assert.Null(row.MainCreditLineDisponible);
        Assert.Null(row.MainCreditLineCurrency);
    }

    /// <summary>
    /// An over-committed cupo reports a NEGATIVE disponible rather than a reassuring zero, exactly
    /// as CreditLineResponse.Disponible does. Clamping here would hide real over-exposure from the
    /// PyMEs table.
    /// </summary>
    [Fact]
    public async Task GetAll_WhenCupoIsUnderwater_DisponibleIsNegativeNotClamped()
    {
        var entity = BuildEntity();
        var creditLineRepo = new StubCreditLineRepository
        {
            MainLinesToReturn = new Dictionary<Guid, MainCreditLineProjection>
            {
                [entity.EntityId] = MainCupo(entity.EntityId, amount: 100_000m, utilizado: 130_000m, "USD")
            }
        };

        var sut = BuildSut(
            new StubEntityRepository { EntitiesToReturn = new[] { entity } },
            creditLineRepo, new StubAnalysisRepository(), new StubUserRepository(),
            new RecordingLogger<EntitiesController>());

        var body = Assert.IsType<EntityListResponse>(
            Assert.IsType<OkObjectResult>(await sut.GetAllAsync(cancellationToken: CancellationToken.None)).Value);

        Assert.Equal(-30_000m, Assert.Single(body.Items).MainCreditLineDisponible);
    }

    /// <summary>
    /// THE ANTI-N+1 GUARD. The whole point of this feature is that the Portal stops issuing one
    /// credit-line call per row — so the server must not do it either. One batched call for the
    /// whole page, carrying every entity id, regardless of how many rows have a cupo.
    /// </summary>
    [Fact]
    public async Task GetAll_QueriesTheMainCupoOnceForTheWholePage_NotPerRow()
    {
        var entities = Enumerable.Range(0, 25).Select(_ => BuildEntity()).ToArray();
        // Only one row actually has a cupo: the batch must still be queried once, with all 25 ids.
        var creditLineRepo = new StubCreditLineRepository
        {
            MainLinesToReturn = new Dictionary<Guid, MainCreditLineProjection>
            {
                [entities[7].EntityId] = MainCupo(entities[7].EntityId, 500m, 100m, "ARS")
            }
        };

        var sut = BuildSut(
            new StubEntityRepository { EntitiesToReturn = entities },
            creditLineRepo, new StubAnalysisRepository(), new StubUserRepository(),
            new RecordingLogger<EntitiesController>());

        var body = Assert.IsType<EntityListResponse>(
            Assert.IsType<OkObjectResult>(
                await sut.GetAllAsync(page: 1, pageSize: 25, CancellationToken.None)).Value);

        Assert.Equal(1, creditLineRepo.MainCallCount);
        Assert.Equal(25, creditLineRepo.LastMainEntityIds.Count);
        Assert.Equal(
            entities.Select(e => e.EntityId).OrderBy(g => g),
            creditLineRepo.LastMainEntityIds.OrderBy(g => g));

        // ...and exactly one row carries the figures.
        Assert.Single(body.Items, r => r.MainCreditLineAmount == 500m);
        Assert.Equal(24, body.Items.Count(r => r.MainCreditLineAmount is null));
    }

    // ── Latest score + stored level (score level contract) ────────────────────

    private static EntityLatestScoreProjection Scored(Guid entityId, decimal score, string level) => new(
        entityId, Guid.NewGuid(), score, level, new DateTime(2026, 11, 3, 15, 0, 0, DateTimeKind.Utc));

    /// <summary>
    /// The list row carries the latest score WITH the level the BFF stored for it, both from one
    /// projection row. The level is passed through, never derived: 32 is "Low" under the default
    /// cutoffs, yet a tenant with low = 30 stored "Medium", and "Medium" is what goes out.
    /// </summary>
    [Fact]
    public async Task GetAll_CarriesTheLatestScoreWithItsStoredLevel_NeverARecomputedOne()
    {
        var scored   = BuildEntity();
        var unscored = BuildEntity();
        var latest   = Scored(scored.EntityId, 32m, "Medium");

        var analysisRepo = new StubAnalysisRepository
        {
            ScoresToReturn = new Dictionary<Guid, EntityLatestScoreProjection> { [scored.EntityId] = latest }
        };

        var sut = BuildSut(
            new StubEntityRepository { EntitiesToReturn = new[] { scored, unscored } },
            new StubCreditLineRepository(), analysisRepo, new StubUserRepository(),
            new RecordingLogger<EntitiesController>());

        var body = Assert.IsType<EntityListResponse>(
            Assert.IsType<OkObjectResult>(await sut.GetAllAsync(cancellationToken: CancellationToken.None)).Value);

        var row = Assert.Single(body.Items, i => i.EntityId == scored.EntityId);
        Assert.Equal(32m,               row.LatestScore);
        Assert.Equal("Medium",          row.LatestRiskLevel);
        Assert.Equal(latest.AnalysisId, row.LatestScoreAnalysisId);
        Assert.Equal(latest.ScoredAt,   row.LatestScoredAt);

        // No score → no level: all four are null together.
        var bare = Assert.Single(body.Items, i => i.EntityId == unscored.EntityId);
        Assert.Null(bare.LatestScore);
        Assert.Null(bare.LatestRiskLevel);
        Assert.Null(bare.LatestScoreAnalysisId);
        Assert.Null(bare.LatestScoredAt);

        Assert.Equal(Tenant, analysisRepo.LastScoreTenantId);
    }

    /// <summary>The entity header's source, GET /api/entities/{id}, carries the same pair.</summary>
    [Fact]
    public async Task GetById_CarriesTheLatestScoreWithItsStoredLevel()
    {
        var entity = BuildEntity();
        var latest = Scored(entity.EntityId, 81.5m, "Critical");

        var sut = BuildSut(
            new StubEntityRepository { EntityToReturn = entity },
            new StubCreditLineRepository(),
            new StubAnalysisRepository
            {
                ScoresToReturn = new Dictionary<Guid, EntityLatestScoreProjection> { [entity.EntityId] = latest }
            },
            new StubUserRepository(),
            new RecordingLogger<EntitiesController>());

        var body = Assert.IsType<EntityResponse>(
            Assert.IsType<OkObjectResult>(await sut.GetById(entity.EntityId, CancellationToken.None)).Value);

        Assert.Equal(81.5m,             body.LatestScore);
        Assert.Equal("Critical",        body.LatestRiskLevel);
        Assert.Equal(latest.AnalysisId, body.LatestScoreAnalysisId);
        Assert.Equal(latest.ScoredAt,   body.LatestScoredAt);

        // The legacy column is passed through untouched and is NOT what the pair reports.
        Assert.Equal(63.25m, body.RiskScore);
    }

    // ── latestScoreProvisional (K21) ─────────────────────────────────────────────

    private static EntityLatestScoreProjection ScoredRow(
        Guid entityId, string? breakdownJson, string? enrichment = "Complete", string kind = AnalysisKinds.Balance)
        => new(entityId, Guid.NewGuid(), 42.5m, "Medium", ScoreReliabilityFixture.ScoreDate)
        {
            ResultId           = Guid.NewGuid(),
            EnrichmentStatus   = enrichment,
            AnalysisKind       = kind,
            FiscalYear         = 2025,
            GeneratedAt        = ScoreReliabilityFixture.ScoreDate,
            ScoreBreakdownJson = breakdownJson,
        };

    private static readonly string ProvisionalJson = ScoreReliabilityFixture.Json(
        facts: ScoreReliabilityFixture.CleanFacts() with
        {
            Integrity = ScoreReliabilityFixture.CleanFacts().Integrity! with { Verified = false },
        });

    private static readonly string CompleteJson    = ScoreReliabilityFixture.Json(facts: ScoreReliabilityFixture.CleanFacts());
    private static readonly string WithCaveatsJson = ScoreReliabilityFixture.Json(facts: ScoreReliabilityFixture.CleanFacts() with { MacroImputed = true });
    private static readonly string BeforeFactsJson = ScoreReliabilityFixture.JsonBeforeFacts();

    /// <summary>
    /// The list says "provisional" exactly when the analysis detail would, for the row that supplied
    /// LatestScore: true for provisional, false for complete and with_caveats (a pre-facts row included),
    /// null with no score.
    /// </summary>
    [Fact]
    public async Task GetAll_LatestScoreProvisional_FollowsTheDetailsVerdictOnTheSameRow()
    {
        var provisional = BuildEntity();
        var complete    = BuildEntity();
        var caveats     = BuildEntity();
        var beforeFacts = BuildEntity();
        var unscored    = BuildEntity();
        var rows = new Dictionary<Guid, EntityLatestScoreProjection>
        {
            [provisional.EntityId] = ScoredRow(provisional.EntityId, ProvisionalJson),
            [complete.EntityId]    = ScoredRow(complete.EntityId, CompleteJson),
            [caveats.EntityId]     = ScoredRow(caveats.EntityId, WithCaveatsJson),
            [beforeFacts.EntityId] = ScoredRow(beforeFacts.EntityId, BeforeFactsJson),
        };

        var logger = new RecordingLogger<EntitiesController>();
        var sut = BuildSut(
            new StubEntityRepository { EntitiesToReturn = new[] { provisional, complete, caveats, beforeFacts, unscored } },
            new StubCreditLineRepository(), new StubAnalysisRepository { ScoresToReturn = rows }, new StubUserRepository(),
            logger);

        var body = Assert.IsType<EntityListResponse>(
            Assert.IsType<OkObjectResult>(await sut.GetAllAsync(cancellationToken: CancellationToken.None)).Value);
        bool? Flag(Entity e) => Assert.Single(body.Items, i => i.EntityId == e.EntityId).LatestScoreProvisional;

        Assert.True(Flag(provisional));
        Assert.False(Flag(complete));
        Assert.False(Flag(caveats));
        Assert.False(Flag(beforeFacts));
        Assert.Null(Flag(unscored));
        Assert.Empty(logger.Warnings);

        // The same verdict the detail computes for the same row.
        foreach (var (entityId, row) in rows)
        {
            var detailLevel = ScoreReliabilityFixture.Evaluate(
                ScoreReliabilityFixture.Row(enrichment: row.EnrichmentStatus), row.ScoreBreakdownJson)!.Level;
            Assert.Equal(detailLevel == ScoreReliabilityLevels.Provisional,
                Assert.Single(body.Items, i => i.EntityId == entityId).LatestScoreProvisional);
        }
    }

    [Fact]
    public async Task GetAll_AnUnreadableBreakdown_IsJudgedAsTheDetailWould_AndLoggedOncePerPage()
    {
        var first  = BuildEntity();
        var second = BuildEntity();
        var rows = new Dictionary<Guid, EntityLatestScoreProjection>
        {
            [first.EntityId]  = ScoredRow(first.EntityId, "{not json"),
            [second.EntityId] = ScoredRow(second.EntityId, "[1,2]"),
        };
        var logger = new RecordingLogger<EntitiesController>();
        var sut = BuildSut(
            new StubEntityRepository { EntitiesToReturn = new[] { first, second } },
            new StubCreditLineRepository(), new StubAnalysisRepository { ScoresToReturn = rows }, new StubUserRepository(),
            logger);

        var body = Assert.IsType<EntityListResponse>(
            Assert.IsType<OkObjectResult>(await sut.GetAllAsync(cancellationToken: CancellationToken.None)).Value);

        Assert.All(body.Items, i => Assert.False(i.LatestScoreProvisional));   // facts_unavailable: with_caveats
        Assert.Single(logger.Warnings);
    }

    [Fact]
    public void LatestScoreProvisional_IsNullWithoutAVerdict()
    {
        var persona = ScoredRow(Guid.NewGuid(), CompleteJson, kind: AnalysisKinds.PersonaHumana);

        Assert.Null(EntitiesController.LatestScoreProvisional(persona).Provisional);
    }

    [Fact]
    public void TheDetailPayload_DoesNotCarryTheFlag()
    {
        Assert.Null(typeof(EntityResponse).GetProperty(nameof(EntityListItemResponse.LatestScoreProvisional)));
    }
}
