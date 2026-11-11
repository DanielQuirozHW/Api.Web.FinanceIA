// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-04

using System.Security.Claims;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Arca;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Lufe;
using Api.Web.FinanceIA.Models.Nosis;
using Api.Web.FinanceIA.Servicios;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Covers the tenant-profile ENTITLEMENT gate (§7.0b) in front of every optional-integration call
/// in <see cref="EntitiesController"/>: LUFE (entity creation, the ARCA lookup, refresh-lufe,
/// lufe-document, lufe-document/import), CASFOG (entity creation, refresh-casfog) and NOSIS
/// (refresh-nosis). For each: an entitled profile leaves the vendor call as it was; a disabled
/// profile never calls the vendor, never logs a Warning or Error, and — on the on-demand endpoints —
/// answers 400 with an es-AR detail; a degraded resolution (<see cref="TenantProfileSource.ReadFailed"/>)
/// or a resolver that throws FAILS OPEN and calls the vendor.
///
/// <para>
/// The availability half of every gate is deliberately OPEN in this harness — a LUFE key is
/// configured, NOSIS is enabled with credentials — so only the entitlement decides. That is also
/// what separates these tests from <c>Create_SkipsLufeWhenTheTenantHasNoApiKeyConfigured</c>: there
/// the skip is the missing key; here the key is present and the profile is what says no.
/// </para>
///
/// <para>
/// ARCA and BCRA are base integrations and must not be gated, so the fan-out cases assert their
/// counts alongside the gated one. Hand-rolled private stubs, per the repo convention; members
/// outside the tested paths throw so an unexpected call is loud.
/// </para>
/// </summary>
public sealed class EntitiesControllerEntitlementGateTests
{
    private const string Tenant = "tenant-1";
    private const string Cuit   = "30-12345678-9";
    private const string Uid    = "firebase-uid-1";
    private const string DocUrl =
        "https://legajounicoapi.produccion.gob.ar/entidades/30123456789/documentos/balance-2025-a.pdf";

    private const string LufeDisabledDetail   = "LUFE no está habilitado para este tenant.";
    private const string CasfogDisabledDetail = "CASFOG no está habilitado para este tenant.";
    private const string NosisDisabledDetail  = "NOSIS no está habilitado para este tenant.";

    // ── Profile stub ──────────────────────────────────────────────────────────
    private sealed class StubTenantProfileService : ITenantProfileService
    {
        public TenantProfileResolution Resolution { get; set; } =
            new(TenantProfilePresets.Sgr, TenantProfileSource.Configured);

        public Exception? ThrowOnCall { get; set; }
        public int CallCount { get; private set; }

        public Task<TenantProfileResolution> GetEffectiveAsync(string tenantId, CancellationToken ct)
        {
            CallCount++;
            if (ThrowOnCall is not null) throw ThrowOnCall;
            return Task.FromResult(Resolution);
        }

        // EntitiesController only ever reads the profile — it gates vendor calls and never writes.
        // Present to satisfy the interface, and loud so a future write from here is not silent.
        public Task<TenantProfileResolution> SetEntityTypeAsync(string tenantId, string entityType, CancellationToken ct)
            => throw new NotSupportedException("EntitiesController must not write the tenant profile.");
    }

    private static TenantProfileResolution Configured(bool lufe = true, bool casfog = true, bool nosis = true)
        => new(new TenantProfile(TenantSettingKeys.EntityTypeSgr, lufe, casfog, nosis), TenantProfileSource.Configured);

    /// <summary>What the real service returns when the settings SELECT fails: the preset, marked degraded.</summary>
    private static TenantProfileResolution Degraded()
        => new(TenantProfilePresets.Default, TenantProfileSource.ReadFailed);

    // ── Vendor stubs (all counting) ───────────────────────────────────────────
    private sealed class StubLufeService : ILufeService
    {
        public int GetLufeDataCalls        { get; private set; }
        public int GetMipymeCategoriaCalls { get; private set; }
        public int DownloadDocumentCalls   { get; private set; }

        public Task<LufeLookupResult> GetLufeDataAsync(string tenantId, long cuit, string apiKey, CancellationToken ct)
        {
            GetLufeDataCalls++;
            return Task.FromResult(new LufeLookupResult(
                LufeConsultaEstado.LegajoFound, new LufeData { Entidad = new LufeEntidad() }));
        }

        public Task<string?> GetMipymeCategoriaAsync(string tenantId, long cuit, string apiKey, CancellationToken ct)
        {
            GetMipymeCategoriaCalls++;
            return Task.FromResult<string?>("Micro");
        }

        public Task<LufeDocumentDownload?> DownloadDocumentAsync(string absoluteUrl, string apiKey, CancellationToken ct)
        {
            DownloadDocumentCalls++;
            return Task.FromResult<LufeDocumentDownload?>(null);   // 502 downstream; the call is what is asserted
        }
    }

    private sealed class StubBcraService : IBcraService
    {
        public int CallCount { get; private set; }

        public Task<BcraData?> GetBcraDataForAnalysisAsync(string cuit, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult<BcraData?>(new BcraData());
        }

        public Task<BcraDeudaData> GetDeudaAsync(string cuit, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<BcraChequesData> GetChequesRechazadosAsync(string cuit, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<BcraMacroData?> GetMacroAsync(CancellationToken ct)
            => throw new NotImplementedException();
        public Task<BcraCambiarioData?> GetCambiarioAsync(CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubCasfogService : ICasfogService
    {
        public int CallCount { get; private set; }

        public Task<CasfogData> GetCentralDeudoresAsync(long cuit, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(new CasfogData { Estado = CasfogConsultaEstado.DataFound });
        }
    }

    private sealed class StubNosisService : INosisService
    {
        public int CallCount { get; private set; }

        public Task<NosisData> GetVariablesAsync(
            string tenantId, long cuit, NosisVariableGroup grupo, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(new NosisData { Estado = NosisConsultaEstado.DataFound });
        }
    }

    private sealed class StubArcaConstanciaService : IArcaConstanciaService
    {
        public int CallCount { get; private set; }

        public Task<ArcaPersonaData?> GetPersonaByCuitAsync(string cuit, string cuitRepresentada, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult<ArcaPersonaData?>(new ArcaPersonaData(
                Cuit: cuit, TipoPersona: "JURIDICA", EstadoClave: "ACTIVO", Nombre: null, Apellido: null,
                RazonSocial: "Test SA", TipoDocumento: null, NumeroDocumento: null, FechaNacimiento: null,
                MesCierre: 12, IdActividadPrincipal: "620100", DescripcionActividadPrincipal: null,
                PeriodoActividadPrincipal: null, FormaJuridica: null, DomicilioFiscal: null, DomicilioLegal: null));
        }
    }

    // ── Infrastructure stubs ──────────────────────────────────────────────────
    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private sealed class StubEntityRepository : IEntityRepository
    {
        public Entity? EntityToReturn { get; set; }
        public Entity? Created { get; private set; }
        public int UpdateCount { get; private set; }

        public Task<Entity?> GetByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => Task.FromResult(EntityToReturn);

        public Task<Entity?> GetByCuitAsync(string cuit, string tenantId, CancellationToken ct)
            => Task.FromResult<Entity?>(null);      // no duplicate — creation and lookup proceed

        public Task<Entity> CreateAsync(Entity entity, CancellationToken ct)
        {
            Created = entity;
            return Task.FromResult(entity);
        }

        public Task<Entity> UpdateAsync(Entity entity, CancellationToken ct)
        {
            UpdateCount++;
            return Task.FromResult(entity);
        }

        public Task<bool> ExistsByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<Entity>> GetAllByTenantAsync(string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<(IReadOnlyList<EntityListProjection> Items, int TotalCount)> GetListPageByTenantAsync(
            string tenantId, int skip, int take, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, EntityNameProjection>> GetNameLookupByIdsAsync(
            IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntityDetailProjection?> GetDetailByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntitySnapshotProjection?> GetBcraSnapshotAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntitySnapshotProjection?> GetCasfogSnapshotAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntitySnapshotProjection?> GetLufeSnapshotAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntitySnapshotProjection?> GetNosisSnapshotAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntityNameProjection?> GetNameByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// The import's dedup lookup reads the entity's existing documents before downloading; an
    /// empty list lets the gate tests reach the download. Nothing else on the path is used.
    /// </summary>
    private sealed class StubDocumentRepository : IDocumentRepository
    {
        public Task<IReadOnlyList<Document>> GetByEntityIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Document>>([]);

        public Task<Document> CreateAsync(Document document, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Document?> GetByIdAsync(Guid documentId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<Document>> GetAllByTenantAsync(string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task UpdateStatusAsync(Guid documentId, string tenantId, string status, DateTime? processedAt, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<bool> SoftDeleteAsync(Guid documentId, string tenantId, DateTime deletedAt, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Document?> FindRecentByHashAsync(Guid entityId, string tenantId, string? fileHashSha256, string documentType, DateTime uploadedSinceUtc, CancellationToken ct)
            => Task.FromResult<Document?>(null);

        public Task<string?> GetLatestExtractionErrorByHashAsync(string? fileHashSha256, string tenantId, CancellationToken ct)
            => Task.FromResult<string?>(null);

        public Task<int> SumExtractionAttemptsByHashAsync(string? fileHashSha256, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task RecordExtractionFailureAsync(
            Guid documentId, string tenantId, DateTime failedAt, string? error, bool countsAsAttempt, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubUserRepository : IUserRepository
    {
        public Task<User?> GetByFirebaseUidAsync(string firebaseUid, CancellationToken ct)
            => Task.FromResult<User?>(new User
            {
                UserId      = Guid.NewGuid(),
                TenantId    = Tenant,
                FirebaseUid = firebaseUid,
                Email       = "analyst@example.com"
            });

        public Task<User?> GetByUserIdAsync(Guid userId, CancellationToken ct)
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
        public Task<IReadOnlyList<User>> GetActiveByTenantAsync(string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        // Team assignment (DDL 063) — unused by this suite.
        public Task UpdateTeamAsync(Guid userId, Guid? teamId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> ClearTeamAsync(Guid teamId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class StubAuditService : IAuditService
    {
        public Task LogAsync(string tenantId, Guid? userId, string action, string severity = "Info",
            string? entityType = null, string? entityId = null, string? ipAddress = null,
            string? requestId = null, string? oldValueJson = null, string? newValueJson = null,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task LogSessionAsync(Guid tenantId, Guid? userId, string email, string eventType,
            string? ipAddress, string? userAgent, string? failureReason = null,
            CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private sealed class StubApiCallLogService : IApiCallLogService
    {
        public Task LogAsync(string tenantId, string service, string endpoint, int? responseStatus,
            int? inputTokens, int? outputTokens, decimal? estimatedCostUsd, bool isSuccess, string? errorMessage,
            int durationMs, string? operation = null, string? model = null, Guid? userId = null,
            Guid? analysisId = null, long? chatMessageId = null, Guid? documentId = null, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    /// <summary>
    /// Captures Warning and Error entries. A disabled profile is the tenant's declared state, not an
    /// anomaly, so the gate must leave both lists empty; only a resolver that violates its
    /// never-throws contract earns a Warning.
    /// </summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];
        public List<string> Errors   { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
            if (logLevel >= LogLevel.Error)   Errors.Add(formatter(state, exception));
        }
    }

    // ── Harness ───────────────────────────────────────────────────────────────
    private sealed record Harness(
        EntitiesController                  Sut,
        StubEntityRepository                Repo,
        StubTenantProfileService            Profile,
        StubLufeService                     Lufe,
        StubBcraService                     Bcra,
        StubCasfogService                   Casfog,
        StubNosisService                    Nosis,
        StubArcaConstanciaService           Arca,
        RecordingLogger<EntitiesController> Logger,
        RecordingLogger<IntegrationAccessService> AccessLogger);

    /// <param name="resolution">What the profile service answers. Default: everything entitled.</param>
    /// <param name="throwOnProfile">Makes the profile service throw — the contract-violation case.</param>
    /// <param name="lufeApiKey">The per-tenant LUFE key; null removes the availability half of the LUFE gate.</param>
    private static Harness BuildHarness(
        TenantProfileResolution? resolution = null,
        Exception? throwOnProfile = null,
        string? lufeApiKey = "test-key",
        Entity? entity = null)
    {
        var repo    = new StubEntityRepository { EntityToReturn = entity };
        var profile = new StubTenantProfileService { ThrowOnCall = throwOnProfile };
        if (resolution is not null) profile.Resolution = resolution;

        var lufe   = new StubLufeService();
        var bcra   = new StubBcraService();
        var casfog = new StubCasfogService();
        var nosis  = new StubNosisService();
        var arca   = new StubArcaConstanciaService();
        var logger = new RecordingLogger<EntitiesController>();
        var accessLogger = new RecordingLogger<IntegrationAccessService>();

        // Every AVAILABILITY gate open: the LUFE key present, NOSIS licensed and configured, the
        // tenant CUIT the ARCA lookup needs. Only the profile can say no.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"Tenants:{Tenant}:LufeApiKey"] = lufeApiKey,
                [$"Tenants:{Tenant}:Cuit"]       = "30-99999999-7",
                ["Nosis:Enabled"]                = "true",
                ["Nosis:ApiKey"]                 = "test-key",
                ["Nosis:VR"]                     = "42"
            })
            .Build();

        var sut = new EntitiesController(
            new StubTenantContextAccessor
            {
                TenantContext = new TenantContext
                {
                    TenantId               = Tenant,
                    FirebaseProjectId      = "proj",
                    FirebaseCredentialJson = "{}",
                    RecaptchaSecretKey     = "secret"
                }
            },
            repo,                        // IEntityRepository
            null!,                       // IArcaPadronService
            arca,                        // IArcaConstanciaService
            null!,                       // IArcaWsaaService
            new StubApiCallLogService(), // IApiCallLogService
            new StubAuditService(),      // IAuditService
            lufe,                        // ILufeService
            bcra,                        // IBcraService
            casfog,                      // ICasfogService
            nosis,                       // INosisService
            new IntegrationAccessService(profile, config, accessLogger), // the subject: the profile behind it
            new StubDocumentRepository(), // IDocumentRepository — dedup lookup only
            null!,                       // IDocumentStorageService
            new StubUserRepository(),    // IUserRepository
            null!,                       // ICreditLineRepository
            null!,                       // IAnalysisRepository
            null!,                       // INoteRepository
            null!,                       // IPersonLegajoRepository
            null!,                       // INosisReportRepository
            null!,                       // INosisManagerReportParser
            null!,                       // KeyedAsyncLock
            config,                      // IConfiguration
            logger);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("uid", Uid) }, "Test"));
        sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        return new Harness(sut, repo, profile, lufe, bcra, casfog, nosis, arca, logger, accessLogger);
    }

    private static CreateEntityRequest CreateRequest() => new()
    {
        CUIT         = Cuit,
        BusinessName = "Test SA",
        IsMonitored  = true
    };

    private static Entity BuildEntity() => new()
    {
        EntityId     = Guid.NewGuid(),
        TenantId     = Tenant,
        CUIT         = Cuit,
        BusinessName = "Test SA"
    };

    private static Entity BuildEntityWithLufeDocument()
    {
        var entity = BuildEntity();
        entity.LufeDataJson = System.Text.Json.JsonSerializer.Serialize(new LufeData
        {
            Documentos = new[]
            {
                new LufeDocumento { Nombre = "Presentación Única de Balances", Url = DocUrl, Periodo = "2025" }
            }
        });
        return entity;
    }

    private static void AssertProblem(IActionResult result, int status, string detail)
    {
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(status, obj.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(obj.Value);
        Assert.Equal(detail, problem.Detail);
    }

    private static void AssertNothingAlarming(Harness h)
    {
        Assert.Empty(h.Logger.Warnings);
        Assert.Empty(h.Logger.Errors);
        Assert.Empty(h.AccessLogger.Warnings);
        Assert.Empty(h.AccessLogger.Errors);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Entity creation — the LUFE + CASFOG fan-out
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Create_WhenEntitled_CallsLufeCasfogAndBcraAsBefore()
    {
        var h = BuildHarness();

        var result = await h.Sut.CreateAsync(CreateRequest(), CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(result);
        Assert.Equal(1, h.Lufe.GetLufeDataCalls);
        Assert.Equal(1, h.Casfog.CallCount);
        Assert.Equal(1, h.Bcra.CallCount);
        Assert.Equal(0, h.Nosis.CallCount);      // still never eager — billed per query
        AssertNothingAlarming(h);
    }

    [Fact]
    public async Task Create_WhenProfileDisablesLufe_SkipsLufeDespiteConfiguredKey_AndStillEnrichesTheRest()
    {
        // The key IS configured, so the only thing holding LUFE back is the profile: this is the
        // case Create_SkipsLufeWhenTheTenantHasNoApiKeyConfigured cannot tell apart.
        var h = BuildHarness(Configured(lufe: false));

        var result = await h.Sut.CreateAsync(CreateRequest(), CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(result);
        Assert.Equal(0, h.Lufe.GetLufeDataCalls);
        Assert.Null(h.Repo.Created!.LufeDataJson);
        Assert.Equal(1, h.Casfog.CallCount);     // sibling untouched
        Assert.Equal(1, h.Bcra.CallCount);       // base integration untouched
        AssertNothingAlarming(h);
    }

    [Fact]
    public async Task Create_WhenProfileDisablesCasfog_SkipsCasfog_AndStillEnrichesLufeAndBcra()
    {
        var h = BuildHarness(Configured(casfog: false));

        var result = await h.Sut.CreateAsync(CreateRequest(), CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(result);
        Assert.Equal(0, h.Casfog.CallCount);
        Assert.Null(h.Repo.Created!.CasfogDataJson);
        Assert.Null(h.Repo.Created.CasfogDataUpdatedAt);
        Assert.Equal(1, h.Lufe.GetLufeDataCalls);
        Assert.Equal(1, h.Bcra.CallCount);
        AssertNothingAlarming(h);
    }

    [Fact]
    public async Task Create_WithTheNonSgrPreset_DropsOnlyCasfog()
    {
        // The real-world case: a non-SGR lender. The preset switches CASFOG off and nothing else.
        var h = BuildHarness(new TenantProfileResolution(TenantProfilePresets.NonSgr, TenantProfileSource.Configured));

        await h.Sut.CreateAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(0, h.Casfog.CallCount);
        Assert.Equal(1, h.Lufe.GetLufeDataCalls);
        Assert.Equal(1, h.Bcra.CallCount);
        AssertNothingAlarming(h);
    }

    [Fact]
    public async Task Create_WhenProfileIsDegraded_FailsOpenAndCallsEveryIntegration()
    {
        var h = BuildHarness(Degraded());

        await h.Sut.CreateAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(1, h.Lufe.GetLufeDataCalls);
        Assert.Equal(1, h.Casfog.CallCount);
        Assert.Equal(1, h.Bcra.CallCount);
        // The service already warned about the failed read; the controller must not warn again.
        AssertNothingAlarming(h);
    }

    [Fact]
    public async Task Create_WhenProfileServiceThrows_FailsOpenAndWarnsOnce()
    {
        var h = BuildHarness(throwOnProfile: new InvalidOperationException("contract violated"));

        var result = await h.Sut.CreateAsync(CreateRequest(), CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(result);
        Assert.Equal(1, h.Lufe.GetLufeDataCalls);
        Assert.Equal(1, h.Casfog.CallCount);
        Assert.Equal(1, h.Bcra.CallCount);
        // The warning comes from the shared IntegrationAccessService; the controller adds none.
        Assert.Single(h.AccessLogger.Warnings);
        Assert.Empty(h.Logger.Warnings);
        Assert.Empty(h.Logger.Errors);
    }

    [Fact]
    public async Task Create_ResolvesTheProfileExactlyOnce()
    {
        // Two gates (LUFE, CASFOG), one read.
        var h = BuildHarness();

        await h.Sut.CreateAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(1, h.Profile.CallCount);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ARCA lookup — the LUFE MiPyME sub-step; ARCA itself is never gated
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task LookupArca_WhenEntitled_ResolvesTheMipymeCategoryFromLufe()
    {
        var h = BuildHarness();

        var result = await h.Sut.LookupEntityFromArca(new ArcaLookupByCuitRequest { Cuit = Cuit }, CancellationToken.None);

        var ok       = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ArcaLookupResponse>(ok.Value);
        Assert.Equal("Micro", response.MipymeCategoria);
        Assert.Equal(1, h.Arca.CallCount);
        Assert.Equal(1, h.Lufe.GetMipymeCategoriaCalls);
        AssertNothingAlarming(h);
    }

    [Fact]
    public async Task LookupArca_WhenProfileDisablesLufe_StillAnswersFromArca_WithoutTheCategory()
    {
        var h = BuildHarness(Configured(lufe: false));

        var result = await h.Sut.LookupEntityFromArca(new ArcaLookupByCuitRequest { Cuit = Cuit }, CancellationToken.None);

        var ok       = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ArcaLookupResponse>(ok.Value);
        Assert.Equal("Test SA", response.RazonSocial);   // ARCA untouched
        Assert.Null(response.MipymeCategoria);
        Assert.Equal(1, h.Arca.CallCount);
        Assert.Equal(0, h.Lufe.GetMipymeCategoriaCalls);
        AssertNothingAlarming(h);
    }

    [Fact]
    public async Task LookupArca_WhenProfileIsDegraded_FailsOpenAndCallsLufe()
    {
        var h = BuildHarness(Degraded());

        await h.Sut.LookupEntityFromArca(new ArcaLookupByCuitRequest { Cuit = Cuit }, CancellationToken.None);

        Assert.Equal(1, h.Lufe.GetMipymeCategoriaCalls);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // refresh-lufe
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RefreshLufe_WhenEntitled_CallsLufeAndPersists()
    {
        var h = BuildHarness(entity: BuildEntity());

        var result = await h.Sut.RefreshLufeAsync(h.Repo.EntityToReturn!.EntityId, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, h.Lufe.GetLufeDataCalls);
        Assert.Equal(1, h.Repo.UpdateCount);
    }

    [Fact]
    public async Task RefreshLufe_WhenProfileDisablesLufe_Returns400AndNeverCallsLufe()
    {
        var h = BuildHarness(Configured(lufe: false), entity: BuildEntity());

        var result = await h.Sut.RefreshLufeAsync(h.Repo.EntityToReturn!.EntityId, CancellationToken.None);

        AssertProblem(result, 400, LufeDisabledDetail);
        Assert.Equal(0, h.Lufe.GetLufeDataCalls);
        Assert.Equal(0, h.Repo.UpdateCount);
        AssertNothingAlarming(h);
    }

    [Fact]
    public async Task RefreshLufe_EntitlementIsCheckedBeforeTheKey()
    {
        // Disabled profile AND no key: the answer names the entitlement, proving the profile is
        // consulted first and the config lookup never happens.
        var h = BuildHarness(Configured(lufe: false), lufeApiKey: null, entity: BuildEntity());

        var result = await h.Sut.RefreshLufeAsync(h.Repo.EntityToReturn!.EntityId, CancellationToken.None);

        AssertProblem(result, 400, LufeDisabledDetail);
    }

    [Fact]
    public async Task RefreshLufe_WhenEntitledButNoKey_StillReportsTheMissingKey()
    {
        // The availability gate is intact behind the entitlement gate.
        var h = BuildHarness(lufeApiKey: null, entity: BuildEntity());

        var result = await h.Sut.RefreshLufeAsync(h.Repo.EntityToReturn!.EntityId, CancellationToken.None);

        AssertProblem(result, 400, "Lufe no está configurado para este tenant.");
        Assert.Equal(0, h.Lufe.GetLufeDataCalls);
    }

    [Fact]
    public async Task RefreshLufe_WhenProfileIsDegraded_FailsOpenAndCallsLufe()
    {
        var h = BuildHarness(Degraded(), entity: BuildEntity());

        var result = await h.Sut.RefreshLufeAsync(h.Repo.EntityToReturn!.EntityId, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, h.Lufe.GetLufeDataCalls);
    }

    [Fact]
    public async Task RefreshLufe_WhenProfileServiceThrows_FailsOpenAndCallsLufe()
    {
        var h = BuildHarness(throwOnProfile: new InvalidOperationException("boom"), entity: BuildEntity());

        var result = await h.Sut.RefreshLufeAsync(h.Repo.EntityToReturn!.EntityId, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, h.Lufe.GetLufeDataCalls);
        Assert.Single(h.AccessLogger.Warnings);
        Assert.Empty(h.Logger.Warnings);
    }

    [Fact]
    public async Task RefreshLufe_WhenEntityMissing_404WinsAndTheProfileIsNeverRead()
    {
        var h = BuildHarness(Configured(lufe: false), entity: null);

        var result = await h.Sut.RefreshLufeAsync(Guid.NewGuid(), CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, obj.StatusCode);
        Assert.Equal(0, h.Profile.CallCount);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // refresh-casfog
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RefreshCasfog_WhenEntitled_CallsCasfogAndPersists()
    {
        var h = BuildHarness(entity: BuildEntity());

        var result = await h.Sut.RefreshCasfogAsync(h.Repo.EntityToReturn!.EntityId, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, h.Casfog.CallCount);
        Assert.Equal(1, h.Repo.UpdateCount);
    }

    [Fact]
    public async Task RefreshCasfog_WhenProfileDisablesCasfog_Returns400AndNeverCallsCasfog()
    {
        var h = BuildHarness(Configured(casfog: false), entity: BuildEntity());

        var result = await h.Sut.RefreshCasfogAsync(h.Repo.EntityToReturn!.EntityId, CancellationToken.None);

        AssertProblem(result, 400, CasfogDisabledDetail);
        Assert.Equal(0, h.Casfog.CallCount);
        Assert.Equal(0, h.Repo.UpdateCount);
        AssertNothingAlarming(h);
    }

    [Fact]
    public async Task RefreshCasfog_WhenProfileIsDegraded_FailsOpenAndCallsCasfog()
    {
        var h = BuildHarness(Degraded(), entity: BuildEntity());

        var result = await h.Sut.RefreshCasfogAsync(h.Repo.EntityToReturn!.EntityId, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, h.Casfog.CallCount);
    }

    [Fact]
    public async Task RefreshCasfog_WhenEntityMissing_404WinsAndTheProfileIsNeverRead()
    {
        var h = BuildHarness(Configured(casfog: false), entity: null);

        var result = await h.Sut.RefreshCasfogAsync(Guid.NewGuid(), CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, obj.StatusCode);
        Assert.Equal(0, h.Profile.CallCount);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // refresh-nosis
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RefreshNosis_WhenEntitled_CallsNosisAndPersists()
    {
        var h = BuildHarness(entity: BuildEntity());

        var result = await h.Sut.RefreshNosisAsync(h.Repo.EntityToReturn!.EntityId, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, h.Nosis.CallCount);
        Assert.Equal(1, h.Repo.UpdateCount);
    }

    [Fact]
    public async Task RefreshNosis_WhenProfileDisablesNosis_Returns400DespiteAFullyConfiguredInstallation()
    {
        // Nosis:Enabled=true with key and VR: the installation could call NOSIS; the tenant may not.
        var h = BuildHarness(Configured(nosis: false), entity: BuildEntity());

        var result = await h.Sut.RefreshNosisAsync(h.Repo.EntityToReturn!.EntityId, CancellationToken.None);

        AssertProblem(result, 400, NosisDisabledDetail);
        Assert.Equal(0, h.Nosis.CallCount);
        Assert.Equal(0, h.Repo.UpdateCount);
        AssertNothingAlarming(h);
    }

    [Fact]
    public async Task RefreshNosis_WhenProfileIsDegraded_FailsOpenAndCallsNosis()
    {
        var h = BuildHarness(Degraded(), entity: BuildEntity());

        var result = await h.Sut.RefreshNosisAsync(h.Repo.EntityToReturn!.EntityId, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, h.Nosis.CallCount);
    }

    [Fact]
    public async Task RefreshNosis_WhenEntityMissing_404WinsAndTheProfileIsNeverRead()
    {
        var h = BuildHarness(Configured(nosis: false), entity: null);

        var result = await h.Sut.RefreshNosisAsync(Guid.NewGuid(), CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, obj.StatusCode);
        Assert.Equal(0, h.Profile.CallCount);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // lufe-document (download) and lufe-document/import — LUFE calls with the LUFE key
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetLufeDocument_WhenEntitled_CallsLufe()
    {
        var h = BuildHarness(entity: BuildEntityWithLufeDocument());

        var result = await h.Sut.GetLufeDocumentAsync(h.Repo.EntityToReturn!.EntityId, 0, CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, obj.StatusCode);              // the stub returns null; the CALL is the point
        Assert.Equal(1, h.Lufe.DownloadDocumentCalls);
    }

    [Fact]
    public async Task GetLufeDocument_WhenProfileDisablesLufe_Returns400AndNeverCallsLufe()
    {
        var h = BuildHarness(Configured(lufe: false), entity: BuildEntityWithLufeDocument());

        var result = await h.Sut.GetLufeDocumentAsync(h.Repo.EntityToReturn!.EntityId, 0, CancellationToken.None);

        AssertProblem(result, 400, LufeDisabledDetail);
        Assert.Equal(0, h.Lufe.DownloadDocumentCalls);
        AssertNothingAlarming(h);
    }

    [Fact]
    public async Task ImportLufeDocument_WhenEntitled_CallsLufe()
    {
        var h = BuildHarness(entity: BuildEntityWithLufeDocument());

        var result = await h.Sut.ImportLufeDocumentAsync(
            h.Repo.EntityToReturn!.EntityId, new ImportLufeDocumentRequest { DocIndex = 0 }, CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, obj.StatusCode);
        Assert.Equal(1, h.Lufe.DownloadDocumentCalls);
    }

    [Fact]
    public async Task ImportLufeDocument_WhenProfileDisablesLufe_Returns400AndNeverCallsLufe()
    {
        var h = BuildHarness(Configured(lufe: false), entity: BuildEntityWithLufeDocument());

        var result = await h.Sut.ImportLufeDocumentAsync(
            h.Repo.EntityToReturn!.EntityId, new ImportLufeDocumentRequest { DocIndex = 0 }, CancellationToken.None);

        AssertProblem(result, 400, LufeDisabledDetail);
        Assert.Equal(0, h.Lufe.DownloadDocumentCalls);
        AssertNothingAlarming(h);
    }

    [Fact]
    public async Task ImportLufeDocument_WhenProfileIsDegraded_FailsOpenAndCallsLufe()
    {
        var h = BuildHarness(Degraded(), entity: BuildEntityWithLufeDocument());

        await h.Sut.ImportLufeDocumentAsync(
            h.Repo.EntityToReturn!.EntityId, new ImportLufeDocumentRequest { DocIndex = 0 }, CancellationToken.None);

        Assert.Equal(1, h.Lufe.DownloadDocumentCalls);
    }
}
