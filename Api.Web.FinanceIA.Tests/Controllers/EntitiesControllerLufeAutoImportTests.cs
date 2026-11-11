// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-24

using System.Security.Claims;
using System.Text;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Lufe;
using Api.Web.FinanceIA.Servicios;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// M6 — the LUFE balance is imported automatically whenever a LUFE snapshot is persisted
/// (POST refresh-lufe and POST /api/entities), instead of the analyst pressing "Usar para
/// análisis". Pins:
///   • only "Balance" documents are imported, at most three, audited as
///     <c>Document.AutoImportedFromLufe</c>;
///   • the count rides at the TOP LEVEL of the refresh body as <c>AutoImportedDocuments</c>;
///   • dedup: a document already imported (same LUFE URL) is neither downloaded nor counted;
///   • fault isolation: a failing download never changes the refresh status or the snapshot;
///   • read-only callers (no operator role) never import.
///
/// Hand-rolled stubs per the repo convention; members off the tested path throw.
/// </summary>
public sealed class EntitiesControllerLufeAutoImportTests
{
    private const string TenantId    = "tenant-1";
    private const string FirebaseUid = "firebase-uid-1";
    private const string Cuit        = "30-12345678-9";
    private const string LufeHost    = "https://legajounicoapi.produccion.gob.ar/entidades/30123456789/documentos/";

    private static readonly byte[] PdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4 auto-import test content");

    // ── Stubs ───────────────────────────────────────────────────────────────────
    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private sealed class StubEntityRepository : IEntityRepository
    {
        public Entity? EntityToReturn { get; set; }
        public int UpdateCount { get; private set; }

        public Task<Entity?> GetByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => Task.FromResult(EntityToReturn);
        public Task<Entity> UpdateAsync(Entity entity, CancellationToken ct)
        {
            UpdateCount++;
            return Task.FromResult(entity);
        }
        public Task<Entity?> GetByCuitAsync(string cuit, string tenantId, CancellationToken ct)
            => Task.FromResult<Entity?>(null);
        public Task<Entity> CreateAsync(Entity entity, CancellationToken ct)
        {
            EntityToReturn = entity;
            return Task.FromResult(entity);
        }

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
        public Task<bool> ExistsByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntityNameProjection?> GetNameByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubLufeService : ILufeService
    {
        public LufeData? Snapshot { get; set; }
        public List<string> DownloadedUrls { get; } = [];

        /// <summary>Per-URL download behaviour; default returns a valid PDF.</summary>
        public Func<string, LufeDocumentDownload?> OnDownload { get; set; } =
            _ => new LufeDocumentDownload(Found: true, Content: PdfBytes, ContentType: "application/pdf");

        public Task<LufeLookupResult> GetLufeDataAsync(string tenantId, long cuit, string apiKey, CancellationToken ct)
            => Task.FromResult(new LufeLookupResult(LufeConsultaEstado.LegajoFound, Snapshot));

        public Task<LufeDocumentDownload?> DownloadDocumentAsync(string absoluteUrl, string apiKey, CancellationToken ct)
        {
            DownloadedUrls.Add(absoluteUrl);
            return Task.FromResult(OnDownload(absoluteUrl));
        }

        public Task<string?> GetMipymeCategoriaAsync(string tenantId, long cuit, string apiKey, CancellationToken ct)
            => throw new NotImplementedException();
    }

    /// <summary>NonSgr: LUFE entitled, CASFOG off — so the create path needs no CASFOG stub.</summary>
    private sealed class StubTenantProfileService : ITenantProfileService
    {
        public Task<TenantProfileResolution> GetEffectiveAsync(string tenantId, CancellationToken ct)
            => Task.FromResult(new TenantProfileResolution(TenantProfilePresets.NonSgr, TenantProfileSource.Configured));

        public Task<TenantProfileResolution> SetEntityTypeAsync(string tenantId, string entityType, CancellationToken ct)
            => throw new NotSupportedException("EntitiesController must not write the tenant profile.");
    }

    private sealed class StubBcraService : IBcraService
    {
        public Task<BcraData?> GetBcraDataForAnalysisAsync(string cuit, CancellationToken ct)
            => Task.FromResult<BcraData?>(null);

        public Task<BcraDeudaData> GetDeudaAsync(string cuit, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<BcraChequesData> GetChequesRechazadosAsync(string cuit, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<BcraMacroData?> GetMacroAsync(CancellationToken ct)
            => throw new NotImplementedException();
        public Task<BcraCambiarioData?> GetCambiarioAsync(CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubDocumentRepository : IDocumentRepository
    {
        public List<Document> Existing { get; } = [];
        public List<Document> Created { get; } = [];

        public Task<Document> CreateAsync(Document document, CancellationToken ct)
        {
            Created.Add(document);
            Existing.Add(document);
            return Task.FromResult(document);
        }

        public Task<IReadOnlyList<Document>> GetByEntityIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Document>>(
                Existing.Where(d => d.EntityId == entityId && d.TenantId == tenantId).ToList());

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

    private sealed class StubDocumentStorageService : IDocumentStorageService
    {
        public Task<string> SaveAsync(string tenantId, Guid documentId, Stream content, CancellationToken ct)
            => Task.FromResult($"tenants/{tenantId}/{documentId}.pdf");

        public Task<byte[]> ReadAsync(string storagePath, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubUserRepository : IUserRepository
    {
        public User? UserToReturn { get; set; }

        public Task<User?> GetByFirebaseUidAsync(string firebaseUid, CancellationToken ct)
            => Task.FromResult(UserToReturn);

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
        public Task UpdateTeamAsync(Guid userId, Guid? teamId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> ClearTeamAsync(Guid teamId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
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
            lock (Actions) Actions.Add(action);
            return Task.CompletedTask;
        }

        public Task LogSessionAsync(
            Guid tenantId, Guid? userId, string email, string eventType,
            string? ipAddress, string? userAgent, string? failureReason = null,
            CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    // ── Harness ─────────────────────────────────────────────────────────────────
    private sealed record Harness(
        EntitiesController   Sut,
        StubEntityRepository Repo,
        StubLufeService      Lufe,
        StubDocumentRepository Docs,
        StubAuditService     Audit,
        User                 User);

    private static Harness BuildHarness(LufeData snapshot, string? role = Roles.Analyst, Entity? existingEntity = null)
    {
        var repo  = new StubEntityRepository { EntityToReturn = existingEntity };
        var lufe  = new StubLufeService { Snapshot = snapshot };
        var docs  = new StubDocumentRepository();
        var audit = new StubAuditService();
        var user  = new User { UserId = Guid.NewGuid() };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"Tenants:{TenantId}:LufeApiKey"] = "test-key"
            })
            .Build();

        var sut = new EntitiesController(
            new StubTenantContextAccessor
            {
                TenantContext = new TenantContext
                {
                    TenantId               = TenantId,
                    FirebaseProjectId      = "proj",
                    FirebaseCredentialJson = "{}",
                    RecaptchaSecretKey     = "secret"
                }
            },
            repo,                           // IEntityRepository
            null!,                          // IArcaPadronService
            null!,                          // IArcaConstanciaService
            null!,                          // IArcaWsaaService
            null!,                          // IApiCallLogService
            audit,                          // IAuditService
            lufe,                           // ILufeService
            new StubBcraService(),          // IBcraService
            null!,                          // ICasfogService — CASFOG disabled by the NonSgr profile
            null!,                          // INosisService
            new IntegrationAccessService(new StubTenantProfileService(), config, NullLogger<IntegrationAccessService>.Instance), // IIntegrationAccessService
            docs,                           // IDocumentRepository
            new StubDocumentStorageService(), // IDocumentStorageService
            new StubUserRepository { UserToReturn = user }, // IUserRepository
            null!,                          // ICreditLineRepository
            null!,                          // IAnalysisRepository
            null!,                          // INoteRepository
            null!,                          // IPersonLegajoRepository
            null!,                          // INosisReportRepository
            null!,                          // INosisManagerReportParser
            null!,                          // KeyedAsyncLock
            config,                         // IConfiguration
            NullLogger<EntitiesController>.Instance);

        var claims = new List<Claim> { new("uid", FirebaseUid) };
        if (role is not null)
            claims.Add(new Claim(ClaimTypes.Role, role));

        sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
            }
        };

        return new Harness(sut, repo, lufe, docs, audit, user);
    }

    private static Entity BuildEntity() => new()
    {
        EntityId     = Guid.NewGuid(),
        TenantId     = TenantId,
        CUIT         = Cuit,
        BusinessName = "Test SA"
    };

    private static LufeDocumento Doc(string nombre, string file, string periodo = "2025") => new()
    {
        Nombre  = nombre,
        Url     = LufeHost + file,
        Periodo = periodo
    };

    private static LufeData Snapshot(params LufeDocumento[] documentos) => new()
    {
        Entidad    = new LufeEntidad(),
        Documentos = documentos
    };

    private static async Task<EntityResponse> RefreshAsync(Harness h, Guid entityId)
    {
        var result = await h.Sut.RefreshLufeAsync(entityId, CancellationToken.None);
        var ok     = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<EntityResponse>(ok.Value);
    }

    // ── Refresh ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Refresh_ImportsTheBalanceDocument_AndReportsTheCountAtTopLevel()
    {
        var entity = BuildEntity();
        var h      = BuildHarness(Snapshot(
                         Doc("Presentación Única de Balances", "balance-2025.pdf"),
                         Doc("Certificado MiPyME", "certificado.pdf")),
                         existingEntity: entity);

        var response = await RefreshAsync(h, entity.EntityId);

        Assert.Equal(1, response.AutoImportedDocuments);

        var created = Assert.Single(h.Docs.Created);
        Assert.Equal("Lufe", created.Source);
        Assert.Equal(LufeHost + "balance-2025.pdf", created.SourceRef);
        Assert.Equal("Balance", created.DocumentType);
        Assert.Equal("Pending", created.ProcessingStatus);
        Assert.Equal(2025, created.FiscalYear);
        Assert.Equal(h.User.UserId, created.UploadedBy);

        // The certificate is not a balance: never downloaded.
        Assert.Equal(new[] { LufeHost + "balance-2025.pdf" }, h.Lufe.DownloadedUrls);
        Assert.Contains("Document.AutoImportedFromLufe", h.Audit.Actions);
        Assert.DoesNotContain("Document.ImportedFromLufe", h.Audit.Actions);
    }

    [Fact]
    public async Task Refresh_SerializesAutoImportedDocumentsAsATopLevelProperty()
    {
        var entity = BuildEntity();
        var h      = BuildHarness(Snapshot(Doc("Balance 2025", "b.pdf")), existingEntity: entity);

        var response = await RefreshAsync(h, entity.EntityId);
        var json     = System.Text.Json.JsonSerializer.Serialize(
            response, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        using var parsed = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(1, parsed.RootElement.GetProperty("autoImportedDocuments").GetInt32());
        Assert.Equal(entity.EntityId, parsed.RootElement.GetProperty("entityId").GetGuid());
    }

    [Fact]
    public void EntityResponse_OmitsAutoImportedDocumentsWhenNotSet()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new EntityResponse(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.DoesNotContain("autoImportedDocuments", json);
    }

    [Fact]
    public async Task Refresh_WhenDownloadThrows_StillReturns200WithTheSnapshotAndACountOfZero()
    {
        var entity = BuildEntity();
        var h      = BuildHarness(Snapshot(Doc("Balance 2025", "b.pdf")), existingEntity: entity);
        h.Lufe.OnDownload = _ => throw new InvalidOperationException("LUFE exploded");

        var response = await RefreshAsync(h, entity.EntityId);

        Assert.Equal(0, response.AutoImportedDocuments);
        Assert.NotNull(response.Lufe);
        Assert.NotNull(response.LufeDataUpdatedAt);
        Assert.Equal(1, h.Repo.UpdateCount);
        Assert.Empty(h.Docs.Created);
    }

    [Fact]
    public async Task Refresh_OneFailingBalanceDoesNotStopTheOthers()
    {
        var entity = BuildEntity();
        var h      = BuildHarness(Snapshot(
                         Doc("Balance 2025", "b2025.pdf", "2025"),
                         Doc("Balance 2024", "b2024.pdf", "2024")),
                         existingEntity: entity);
        h.Lufe.OnDownload = url => url.EndsWith("b2025.pdf", StringComparison.Ordinal)
            ? null   // transport failure
            : new LufeDocumentDownload(Found: true, Content: PdfBytes, ContentType: "application/pdf");

        var response = await RefreshAsync(h, entity.EntityId);

        Assert.Equal(1, response.AutoImportedDocuments);
        Assert.Equal(LufeHost + "b2024.pdf", Assert.Single(h.Docs.Created).SourceRef);
    }

    [Fact]
    public async Task Refresh_ImportsAtMostThreeBalances_NewestPeriodFirst()
    {
        var entity = BuildEntity();
        var h      = BuildHarness(Snapshot(
                         Doc("Balance 2021", "b2021.pdf", "2021"),
                         Doc("Balance 2023", "b2023.pdf", "2023"),
                         Doc("Balance 2025", "b2025.pdf", "2025"),
                         Doc("Balance 2022", "b2022.pdf", "2022"),
                         Doc("Balance 2024", "b2024.pdf", "2024")),
                         existingEntity: entity);
        // Distinct bytes per URL so the content-hash dedup does not collapse them.
        h.Lufe.OnDownload = url => new LufeDocumentDownload(
            Found: true, Content: Encoding.ASCII.GetBytes("%PDF-1.4 " + url), ContentType: "application/pdf");

        var response = await RefreshAsync(h, entity.EntityId);

        Assert.Equal(3, response.AutoImportedDocuments);
        Assert.Equal(
            new[] { LufeHost + "b2025.pdf", LufeHost + "b2024.pdf", LufeHost + "b2023.pdf" },
            h.Lufe.DownloadedUrls);
    }

    [Fact]
    public async Task Refresh_WhenTheBalanceWasAlreadyImported_DoesNotDownloadOrCountIt()
    {
        var entity = BuildEntity();
        var h      = BuildHarness(Snapshot(Doc("Balance 2025", "b.pdf")), existingEntity: entity);
        h.Docs.Existing.Add(new Document
        {
            DocumentId     = Guid.NewGuid(),
            TenantId       = TenantId,
            EntityId       = entity.EntityId,
            FileHashSHA256 = "whatever",
            Source         = "Lufe",
            SourceRef      = LufeHost + "b.pdf"
        });

        var response = await RefreshAsync(h, entity.EntityId);

        Assert.Equal(0, response.AutoImportedDocuments);
        Assert.Empty(h.Lufe.DownloadedUrls);
        Assert.Empty(h.Docs.Created);
    }

    [Fact]
    public async Task Refresh_SecondRefreshOfTheSameSnapshot_ImportsNothingNew()
    {
        var entity = BuildEntity();
        var h      = BuildHarness(Snapshot(Doc("Balance 2025", "b.pdf")), existingEntity: entity);

        var first  = await RefreshAsync(h, entity.EntityId);
        var second = await RefreshAsync(h, entity.EntityId);

        Assert.Equal(1, first.AutoImportedDocuments);
        Assert.Equal(0, second.AutoImportedDocuments);
        Assert.Single(h.Docs.Created);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Roles.Auditor)]
    public async Task Refresh_ForANonOperatorCaller_ImportsNothing(string? role)
    {
        var entity = BuildEntity();
        var h      = BuildHarness(Snapshot(Doc("Balance 2025", "b.pdf")), role: role, existingEntity: entity);

        var response = await RefreshAsync(h, entity.EntityId);

        Assert.Equal(0, response.AutoImportedDocuments);
        Assert.Empty(h.Lufe.DownloadedUrls);
        Assert.Empty(h.Docs.Created);
    }

    [Fact]
    public async Task Refresh_WhenThePartialSnapshotIsNotPersisted_ImportsNothing()
    {
        // Stored good data + a snapshot missing Entidad → ShouldPersistLufeSnapshot keeps the old
        // blob, so there is no fresh snapshot to import from.
        var entity = BuildEntity();
        entity.LufeDataJson = System.Text.Json.JsonSerializer.Serialize(Snapshot());
        var partial = new LufeData { Documentos = [Doc("Balance 2025", "b.pdf")] };
        var h       = BuildHarness(partial, existingEntity: entity);

        var response = await RefreshAsync(h, entity.EntityId);

        Assert.Equal(0, response.AutoImportedDocuments);
        Assert.Empty(h.Lufe.DownloadedUrls);
    }

    // ── Create ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_ImportsTheBalanceAndStill201s()
    {
        var h = BuildHarness(Snapshot(Doc("Presentación Única de Balances", "b.pdf")));

        var result = await h.Sut.CreateAsync(
            new CreateEntityRequest { CUIT = Cuit, BusinessName = "Test SA" }, CancellationToken.None);

        var created  = Assert.IsType<CreatedAtActionResult>(result);
        var response = Assert.IsType<EntityResponse>(created.Value);
        Assert.Null(response.AutoImportedDocuments);   // create does not carry the field

        var doc = Assert.Single(h.Docs.Created);
        Assert.Equal(response.EntityId, doc.EntityId);
        Assert.Equal(h.User.UserId, doc.UploadedBy);
        Assert.Contains("Document.AutoImportedFromLufe", h.Audit.Actions);
    }

    [Fact]
    public async Task Create_WhenTheBalanceDownloadFails_StillCreatesTheEntityWithItsLufeSnapshot()
    {
        var h = BuildHarness(Snapshot(Doc("Balance 2025", "b.pdf")));
        h.Lufe.OnDownload = _ => throw new HttpRequestException("boom");

        var result = await h.Sut.CreateAsync(
            new CreateEntityRequest { CUIT = Cuit, BusinessName = "Test SA" }, CancellationToken.None);

        var response = Assert.IsType<EntityResponse>(Assert.IsType<CreatedAtActionResult>(result).Value);
        Assert.NotNull(response.Lufe);
        Assert.Empty(h.Docs.Created);
    }
}
