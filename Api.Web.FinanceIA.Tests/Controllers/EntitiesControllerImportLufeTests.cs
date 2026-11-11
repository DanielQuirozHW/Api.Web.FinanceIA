// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-12

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
/// Covers the durable LUFE source reference (<see cref="Document.SourceRef"/>) added so the
/// Portal can match an imported document to its exact LUFE listing entry instead of guessing
/// by filename. Two scenarios:
///   (1) a real LUFE import persists SourceRef == the source document's URL and returns it on
///       the 201 response — the import still succeeds;
///   (2) the derivation itself (BuildLufeSourceRef) returns the URL verbatim when present and
///       null when missing/blank, never throwing — the fault-isolation guarantee.
///
/// Note on (2): doc.Url is mandatory for a LUFE import (the endpoint returns 500 on a blank URL
/// BEFORE the row is built), so a "null SourceRef" cannot be produced through the endpoint. The
/// null branch is therefore proven directly on the pure helper (exposed via InternalsVisibleTo),
/// which is the defensive guard that keeps the best-effort link from ever aborting an import.
///
/// Follows the repo's no-mock-library convention (see EntitiesControllerLufeTests): each stub
/// implements only the interface methods the import path touches; every other constructor
/// dependency is passed as null! (the constructor performs plain assignment).
/// </summary>
public sealed class EntitiesControllerImportLufeTests
{
    private const string TenantId    = "tenant-1";
    private const string FirebaseUid = "firebase-uid-1";
    private const string DocUrl      =
        "https://legajounicoapi.produccion.gob.ar/entidades/30123456789/documentos/balance-2025-a.pdf";

    // ── Stubs ───────────────────────────────────────────────────────────────────
    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private sealed class StubEntityRepository : IEntityRepository
    {
        public Entity? EntityToReturn { get; set; }

        public Task<Entity?> GetByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => Task.FromResult(EntityToReturn);

        public Task<Entity> UpdateAsync(Entity entity, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<Entity>> GetAllByTenantAsync(string tenantId, CancellationToken ct)
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
        public Task<bool> ExistsByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Entity?> GetByCuitAsync(string cuit, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Entity> CreateAsync(Entity entity, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<EntityNameProjection?> GetNameByIdAsync(
            Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubLufeService : ILufeService
    {
        public LufeDocumentDownload? Download { get; set; }
        public int DownloadCalls { get; private set; }

        public Task<LufeDocumentDownload?> DownloadDocumentAsync(string absoluteUrl, string apiKey, CancellationToken ct)
        {
            DownloadCalls++;
            return Task.FromResult(Download);
        }

        public Task<LufeLookupResult> GetLufeDataAsync(string tenantId, long cuit, string apiKey, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<string?> GetMipymeCategoriaAsync(string tenantId, long cuit, string apiKey, CancellationToken ct)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Every optional integration entitled (the SGR preset). The entitlement gate (§7.0b) sits in
    /// front of the key check the import passes through, so it must be open here; the gate itself
    /// is covered in EntitiesControllerEntitlementGateTests.
    /// </summary>
    private sealed class StubTenantProfileService : ITenantProfileService
    {
        public Task<TenantProfileResolution> GetEffectiveAsync(string tenantId, CancellationToken ct)
            => Task.FromResult(new TenantProfileResolution(TenantProfilePresets.Sgr, TenantProfileSource.Configured));

        // EntitiesController only ever reads the profile — it gates vendor calls and never writes.
        // Present to satisfy the interface, and loud so a future write from here is not silent.
        public Task<TenantProfileResolution> SetEntityTypeAsync(string tenantId, string entityType, CancellationToken ct)
            => throw new NotSupportedException("EntitiesController must not write the tenant profile.");
    }

    private sealed class StubDocumentRepository : IDocumentRepository
    {
        public Document? Created { get; private set; }

        /// <summary>The entity's existing documents, as the dedup lookup sees them.</summary>
        public List<Document> Existing { get; } = [];

        public Task<Document> CreateAsync(Document document, CancellationToken ct)
        {
            Created = document;
            return Task.FromResult(document);
        }

        public Task<Document?> GetByIdAsync(Guid documentId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<Document>> GetAllByTenantAsync(string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<Document>> GetByEntityIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Document>>(Existing.Where(d => d.EntityId == entityId).ToList());
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
        public int SaveCalls { get; private set; }

        public Task<string> SaveAsync(string tenantId, Guid documentId, Stream content, CancellationToken ct)
        {
            SaveCalls++;
            return Task.FromResult($"tenants/{tenantId}/{documentId}.pdf");
        }

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
        // Team assignment (DDL 063) — unused by this suite.
        public Task UpdateTeamAsync(Guid userId, Guid? teamId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> ClearTeamAsync(Guid teamId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
    }

    // No-op audit sink: the import fires audit as fire-and-forget and must not depend on it.
    private sealed class StubAuditService : IAuditService
    {
        public Task LogAsync(
            string tenantId, Guid? userId, string action, string severity = "Info",
            string? entityType = null, string? entityId = null, string? ipAddress = null,
            string? requestId = null, string? oldValueJson = null, string? newValueJson = null,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task LogSessionAsync(
            Guid tenantId, Guid? userId, string email, string eventType,
            string? ipAddress, string? userAgent, string? failureReason = null,
            CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────
    private static TenantContext BuildTenant() => new()
    {
        TenantId               = TenantId,
        FirebaseProjectId      = "proj",
        FirebaseCredentialJson = "{}",
        RecaptchaSecretKey     = "secret"
    };

    private static IConfiguration BuildConfig()
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"Tenants:{TenantId}:LufeApiKey"] = "test-key"
            })
            .Build();

    private static Entity BuildEntityWithDocument(string url) => new()
    {
        EntityId     = Guid.NewGuid(),
        TenantId     = TenantId,
        CUIT         = "30-12345678-9",
        BusinessName = "Test SA",
        LufeDataJson = System.Text.Json.JsonSerializer.Serialize(new LufeData
        {
            Documentos = new[]
            {
                new LufeDocumento
                {
                    Nombre  = "Presentación Única de Balances",
                    Url     = url,
                    Periodo = "2025"
                }
            }
        })
    };

    private static readonly byte[] PdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4 minimal test content");

    private sealed record Harness(
        EntitiesController Sut, StubLufeService Lufe, StubDocumentRepository Docs, StubDocumentStorageService Storage);

    private static Harness BuildHarness(Entity entity, byte[] download)
    {
        var lufe    = new StubLufeService
        {
            Download = new LufeDocumentDownload(Found: true, Content: download, ContentType: "application/pdf")
        };
        var docs    = new StubDocumentRepository();
        var storage = new StubDocumentStorageService();
        var sut     = BuildSut(new StubTenantContextAccessor { TenantContext = BuildTenant() },
                               new StubEntityRepository { EntityToReturn = entity }, lufe, docs, storage,
                               new StubUserRepository { UserToReturn = new User { UserId = Guid.NewGuid() } });
        return new Harness(sut, lufe, docs, storage);
    }

    private static Document BuildExistingDocument(Guid entityId, string source, string sourceRef, string hash) => new()
    {
        DocumentId       = Guid.NewGuid(),
        TenantId         = TenantId,
        EntityId         = entityId,
        OriginalFileName = "Presentación Única de Balances.pdf",
        FileHashSHA256   = hash,
        DocumentType     = "Balance",
        ProcessingStatus = "Completed",
        Source           = source,
        SourceRef        = sourceRef
    };

    private static EntitiesController BuildSut(
        StubTenantContextAccessor tenant, StubEntityRepository repo, StubLufeService lufe,
        StubDocumentRepository docs, StubDocumentStorageService storage, StubUserRepository users)
    {
        var sut = new EntitiesController(
            tenant,                     // ITenantContextAccessor
            repo,                       // IEntityRepository
            null!,                      // IArcaPadronService
            null!,                      // IArcaConstanciaService
            null!,                      // IArcaWsaaService
            null!,                      // IApiCallLogService
            new StubAuditService(),     // IAuditService
            lufe,                       // ILufeService
            null!,                      // IBcraService
            null!,                      // ICasfogService
            null!,                      // INosisService
            new IntegrationAccessService(new StubTenantProfileService(), BuildConfig(), NullLogger<IntegrationAccessService>.Instance), // all entitled
            docs,                       // IDocumentRepository
            storage,                    // IDocumentStorageService
            users,                      // IUserRepository
            null!,                      // ICreditLineRepository
            null!,                      // IAnalysisRepository
            null!,                      // INoteRepository
            null!,                      // IPersonLegajoRepository
            null!,                      // INosisReportRepository
            null!,                      // INosisManagerReportParser
            null!,                      // KeyedAsyncLock
            BuildConfig(),              // IConfiguration
            NullLogger<EntitiesController>.Instance);

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("uid", FirebaseUid) }, "TestAuth"))
        };
        sut.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return sut;
    }

    // ── (1) Real import persists + returns SourceRef == the source document URL ──────────────────
    [Fact]
    public async Task ImportLufeDocument_WhenLufeEntryHasUrl_PersistsAndReturnsSourceRef()
    {
        var entity  = BuildEntityWithDocument(DocUrl);
        var repo    = new StubEntityRepository { EntityToReturn = entity };
        var lufe    = new StubLufeService
        {
            Download = new LufeDocumentDownload(
                Found: true,
                Content: Encoding.ASCII.GetBytes("%PDF-1.4 minimal test content"),
                ContentType: "application/pdf")
        };
        var docs    = new StubDocumentRepository();
        var storage = new StubDocumentStorageService();
        var users   = new StubUserRepository { UserToReturn = new User { UserId = Guid.NewGuid() } };
        var sut     = BuildSut(new StubTenantContextAccessor { TenantContext = BuildTenant() },
                               repo, lufe, docs, storage, users);

        var result = await sut.ImportLufeDocumentAsync(
            entity.EntityId, new ImportLufeDocumentRequest { DocIndex = 0 }, CancellationToken.None);

        // Import still succeeds …
        var created  = Assert.IsType<CreatedAtActionResult>(result);
        var response = Assert.IsType<DocumentResponse>(created.Value);

        // … and the durable link is persisted and surfaced, verbatim, for the exact Portal match.
        Assert.NotNull(docs.Created);
        Assert.Equal(DocUrl, docs.Created!.SourceRef);
        Assert.Equal(DocUrl, response.SourceRef);
        Assert.Equal("Lufe", docs.Created.Source);
    }

    // ── Dedup: an already-imported document is returned, never duplicated ─────────────────────────
    [Fact]
    public async Task ImportLufeDocument_WhenSameLufeUrlAlreadyImported_Returns200WithExistingAndDoesNotDownload()
    {
        var entity   = BuildEntityWithDocument(DocUrl);
        var existing = BuildExistingDocument(entity.EntityId, source: "Lufe", sourceRef: DocUrl, hash: "abc");
        var h        = BuildHarness(entity, PdfBytes);
        h.Docs.Existing.Add(existing);

        var result = await h.Sut.ImportLufeDocumentAsync(
            entity.EntityId, new ImportLufeDocumentRequest { DocIndex = 0 }, CancellationToken.None);

        var ok       = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<DocumentResponse>(ok.Value);
        Assert.Equal(existing.DocumentId, response.DocumentId);
        Assert.Equal(0, h.Lufe.DownloadCalls);
        Assert.Equal(0, h.Storage.SaveCalls);
        Assert.Null(h.Docs.Created);
    }

    [Fact]
    public async Task ImportLufeDocument_WhenSameContentAlreadyStoredUnderAnotherUrl_Returns200WithExistingAndStoresNothing()
    {
        var entity   = BuildEntityWithDocument(DocUrl);
        var hash     = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(PdfBytes)).ToLowerInvariant();
        // LUFE re-issued the URL: same bytes, different SourceRef. Upper-case hash proves the
        // comparison is case-insensitive.
        var existing = BuildExistingDocument(entity.EntityId, source: "Lufe",
                                             sourceRef: DocUrl + "?v=old", hash: hash.ToUpperInvariant());
        var h        = BuildHarness(entity, PdfBytes);
        h.Docs.Existing.Add(existing);

        var result = await h.Sut.ImportLufeDocumentAsync(
            entity.EntityId, new ImportLufeDocumentRequest { DocIndex = 0 }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(existing.DocumentId, Assert.IsType<DocumentResponse>(ok.Value).DocumentId);
        Assert.Equal(1, h.Lufe.DownloadCalls);
        Assert.Equal(0, h.Storage.SaveCalls);
        Assert.Null(h.Docs.Created);
    }

    [Fact]
    public async Task ImportLufeDocument_WhenMatchingDocumentIsSoftDeleted_ImportsAgain()
    {
        var entity   = BuildEntityWithDocument(DocUrl);
        var existing = BuildExistingDocument(entity.EntityId, source: "Lufe", sourceRef: DocUrl, hash: "abc");
        existing.IsDeleted = true;
        var h        = BuildHarness(entity, PdfBytes);
        h.Docs.Existing.Add(existing);

        var result = await h.Sut.ImportLufeDocumentAsync(
            entity.EntityId, new ImportLufeDocumentRequest { DocIndex = 0 }, CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(result);
        Assert.NotNull(h.Docs.Created);
        Assert.NotEqual(existing.DocumentId, h.Docs.Created!.DocumentId);
    }

    // ── Size ceiling: PdfSizeLimits.MaxPdfBytes (25 MB), shared with the upload ───────────────────

    /// <summary>A LUFE download of <paramref name="size"/> bytes that starts with the PDF magic bytes.</summary>
    private static byte[] PdfOfSize(long size)
    {
        var bytes = new byte[size];
        PdfBytes.CopyTo(bytes, 0);
        Array.Fill(bytes, (byte)'\n', PdfBytes.Length, bytes.Length - PdfBytes.Length);
        return bytes;
    }

    private static long Megabytes(double mb) => (long)(mb * 1024 * 1024);

    [Theory]
    [InlineData(23.0)]   // the balance the 20 MB ceiling refused
    [InlineData(24.9)]
    public async Task ImportLufeDocument_UnderTheCeiling_IsImported(double mb)
    {
        var entity = BuildEntityWithDocument(DocUrl);
        var h      = BuildHarness(entity, PdfOfSize(Megabytes(mb)));

        var result = await h.Sut.ImportLufeDocumentAsync(
            entity.EntityId, new ImportLufeDocumentRequest { DocIndex = 0 }, CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(result);
        Assert.Equal(Megabytes(mb), h.Docs.Created!.FileSizeBytes);
        Assert.Equal(1, h.Storage.SaveCalls);
    }

    [Fact]
    public async Task ImportLufeDocument_ExactlyTheCeiling_IsImported()
    {
        var entity = BuildEntityWithDocument(DocUrl);
        var h      = BuildHarness(entity, PdfOfSize(PdfSizeLimits.MaxPdfBytes));

        var result = await h.Sut.ImportLufeDocumentAsync(
            entity.EntityId, new ImportLufeDocumentRequest { DocIndex = 0 }, CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(result);
        Assert.Equal(PdfSizeLimits.MaxPdfBytes, h.Docs.Created!.FileSizeBytes);
    }

    [Theory]
    [InlineData(25.1)]
    [InlineData(25.0 + 1.0 / (1024 * 1024))]   // one byte over
    public async Task ImportLufeDocument_OverTheCeiling_Is400_WithTheSameProblem_AndStoresNothing(double mb)
    {
        var entity = BuildEntityWithDocument(DocUrl);
        var h      = BuildHarness(entity, PdfOfSize(Megabytes(mb)));

        var result = await h.Sut.ImportLufeDocumentAsync(
            entity.EntityId, new ImportLufeDocumentRequest { DocIndex = 0 }, CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);
        var problem = Assert.IsAssignableFrom<ProblemDetails>(objectResult.Value);
        Assert.Equal(400, problem.Status);
        Assert.Equal("Bad Request", problem.Title);
        Assert.Equal("El documento supera el tamaño máximo permitido (25 MB).", problem.Detail);
        Assert.Null(h.Docs.Created);
        Assert.Equal(0, h.Storage.SaveCalls);
    }

    // ── (2) The derivation is populated-when-present, null-when-blank, never throws ──────────────
    [Fact]
    public void BuildLufeSourceRef_WhenUrlPresent_ReturnsUrlVerbatim()
    {
        var sourceRef = EntitiesController.BuildLufeSourceRef(new LufeDocumento { Url = DocUrl });
        Assert.Equal(DocUrl, sourceRef);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildLufeSourceRef_WhenUrlMissingOrBlank_ReturnsNull(string? url)
    {
        var sourceRef = EntitiesController.BuildLufeSourceRef(new LufeDocumento { Url = url });
        Assert.Null(sourceRef);
    }
}
