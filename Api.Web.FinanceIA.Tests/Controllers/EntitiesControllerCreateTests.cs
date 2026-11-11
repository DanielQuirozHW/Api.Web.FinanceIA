// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-25

using System.Security.Claims;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Lufe;
using Api.Web.FinanceIA.Models.Nosis;
using Api.Web.FinanceIA.Servicios;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Covers the enrichment fan-out in EntitiesController.CreateAsync, and specifically that
/// <b>NOSIS is never queried during entity creation</b>.
///
/// NOSIS is billed per query, so it is on-demand only: the sole trigger is
/// POST /api/entities/{id}/refresh-nosis. Its three enrichment siblings (LUFE / BCRA / CASFOG) stay
/// eager. ARCA is not called on create at all: the client sends the fiscal snapshot it received from
/// POST /api/entities/lookup-arca, which CreateAsync only serialises (the ARCA services are passed as
/// null! below, so an ARCA call would throw). That asymmetry is easy to "fix" by accident — the
/// obvious reading of CreateAsync is consecutive enrichment blocks and an odd gap — and re-adding the
/// call would cost real money on
/// every PyME created while looking like a tidy-up. These tests are the thing that fails when that
/// happens, so the NOSIS stub here deliberately returns DataFound: if the eager lookup ever comes
/// back, CallCount and the persisted blob both move and two assertions fire at once.
///
/// This is the repo's first CreateAsync test, so it carries the full stub set the create path needs.
/// Following the established no-mock-library convention the dependencies are hand-rolled private
/// sealed stubs; members outside the tested path throw NotImplementedException so an unexpected call
/// is loud rather than silently returning default.
/// </summary>
public sealed class EntitiesControllerCreateTests
{
    private const string Cuit   = "30-12345678-9";
    private const string Tenant = "tenant-1";
    private const string Uid    = "firebase-uid-1";

    // ── Stubs ─────────────────────────────────────────────────────────────────
    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private sealed class StubEntityRepository : IEntityRepository
    {
        public Entity? Created { get; private set; }
        public int UpdateCount { get; private set; }

        public Task<bool> ExistsByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Entity?> GetByCuitAsync(string cuit, string tenantId, CancellationToken ct)
            => Task.FromResult<Entity?>(null);      // no duplicate — creation proceeds

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

        public Task<Entity?> GetByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
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

        public Task<EntityNameProjection?> GetNameByIdAsync(
            Guid entityId, string tenantId, CancellationToken ct)
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
            => Task.CompletedTask;
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

    /// <summary>
    /// Returns DataFound on purpose. If an eager NOSIS lookup is ever restored to CreateAsync, this
    /// stub answers successfully and the entity gets a populated snapshot — so the assertions below
    /// fail loudly instead of the regression passing unnoticed.
    /// </summary>
    private sealed class StubNosisService : INosisService
    {
        public int CallCount { get; private set; }

        public Task<NosisData> GetVariablesAsync(
            string tenantId, long cuit, NosisVariableGroup grupo, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(new NosisData
            {
                Estado       = NosisConsultaEstado.DataFound,
                ScoreVigente = 612
            });
        }
    }

    /// <summary>
    /// Not reached by default: BuildHarness configures no Tenants:{tenantId}:LufeApiKey, so the
    /// block is skipped. Pass a key to BuildHarness and set <see cref="Result"/> to exercise it.
    /// </summary>
    private sealed class StubLufeService : ILufeService
    {
        public int CallCount { get; private set; }
        public LufeData? Result { get; set; }

        public Task<LufeLookupResult> GetLufeDataAsync(string tenantId, long cuit, string apiKey, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(new LufeLookupResult(
                Result is null ? LufeConsultaEstado.LookupFailed : LufeConsultaEstado.LegajoFound, Result));
        }

        public Task<string?> GetMipymeCategoriaAsync(string tenantId, long cuit, string apiKey, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<LufeDocumentDownload?> DownloadDocumentAsync(string absoluteUrl, string apiKey, CancellationToken ct)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Every optional integration entitled (the SGR preset). The entitlement gate (§7.0b) sits in
    /// front of the LUFE and CASFOG blocks, so it must be open for the fan-out these tests pin to
    /// behave as before: the LUFE skip below is still the missing key, and CASFOG still runs. The
    /// gate itself is covered in EntitiesControllerEntitlementGateTests.
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

    // ── Helpers ───────────────────────────────────────────────────────────────
    private sealed record Harness(
        EntitiesController   Sut,
        StubEntityRepository Repo,
        StubBcraService      Bcra,
        StubCasfogService    Casfog,
        StubNosisService     Nosis,
        StubLufeService      Lufe);

    /// <param name="lufeApiKey">
    /// Left null by default, so the LUFE block short-circuits on the missing key exactly as it did
    /// before — every pre-existing test calls BuildHarness() and is unaffected. Supply a key to let
    /// the LUFE enrichment block actually run.
    /// </param>
    private static Harness BuildHarness(string? lufeApiKey = null)
    {
        var repo   = new StubEntityRepository();
        var bcra   = new StubBcraService();
        var casfog = new StubCasfogService();
        var nosis  = new StubNosisService();
        var lufe   = new StubLufeService();

        // NOSIS fully enabled and configured. If CreateAsync were still eager, nothing here would
        // hold it back — which is exactly what makes the CallCount == 0 assertion meaningful.
        var settings = new Dictionary<string, string?>
        {
            ["Nosis:Enabled"] = "true",
            ["Nosis:ApiKey"]  = "test-key",
            ["Nosis:VR"]      = "42"
        };

        if (lufeApiKey is not null)
        {
            settings[$"Tenants:{Tenant}:LufeApiKey"] = lufeApiKey;
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
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
            null!,                       // IArcaConstanciaService
            null!,                       // IArcaWsaaService
            null!,                       // IApiCallLogService
            new StubAuditService(),      // IAuditService
            lufe,                        // ILufeService
            bcra,                        // IBcraService
            casfog,                      // ICasfogService
            nosis,                       // INosisService
            new IntegrationAccessService(new StubTenantProfileService(), config, NullLogger<IntegrationAccessService>.Instance), // all entitled
            null!,                       // IDocumentRepository
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
            NullLogger<EntitiesController>.Instance);

        // CreateAsync reads the Firebase uid off the principal and 401s without it.
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("uid", Uid) }, "Test"));
        sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        return new Harness(sut, repo, bcra, casfog, nosis, lufe);
    }

    private static CreateEntityRequest BuildRequest() => new()
    {
        CUIT         = Cuit,
        BusinessName = "Test SA",
        IsMonitored  = true
    };

    private static CreateEntityRequest BuildRequest(string? mipymeCategory) => new()
    {
        CUIT           = Cuit,
        BusinessName   = "Test SA",
        IsMonitored    = true,
        MipymeCategory = mipymeCategory
    };

    private static LufeData LufeSnapshot(string? categoria) => new()
    {
        Entidad = new LufeEntidad
        {
            Cuit            = 30123456789,
            Nombre          = "Test SA",
            CertificadoPyme = categoria is null
                                ? null
                                : new LufeCertificadoPyme { Categoria = categoria }
        }
    };

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The "SILO CHICO S.A." case: a razón social sent with quotes around it (straight, curly or
    /// angle, any number of layers) is stored without them; quotes inside the name, letter case and
    /// everything else are kept; whitespace and control characters are cleaned.
    /// </summary>
    [Theory]
    [InlineData("\"silo Chico S.A.\"",                  "silo Chico S.A.")]
    [InlineData("\u00AB\"Agro  Norte\tS.A.\"\u00BB",      "Agro Norte S.A.")]
    [InlineData("Sociedad \"La Esperanza\" S.A.",       "Sociedad \"La Esperanza\" S.A.")]
    public async Task Create_StoresTheNormalizedBusinessName(string sent, string expected)
    {
        var h = BuildHarness();
        var request = new CreateEntityRequest { CUIT = Cuit, BusinessName = sent, IsMonitored = true };

        var result = await h.Sut.CreateAsync(request, CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(result);
        Assert.Equal(expected, h.Repo.Created!.BusinessName);
    }

    /// <summary>A name that is nothing but quotes / control characters is the existing "obligatoria" 400.</summary>
    [Theory]
    [InlineData("\"\"")]
    [InlineData(" \u201C \u201D ")]
    [InlineData("\u0000\t")]
    public async Task Create_NameEmptyAfterNormalizing_Returns400(string sent)
    {
        var h = BuildHarness();
        var request = new CreateEntityRequest { CUIT = Cuit, BusinessName = sent, IsMonitored = true };

        var result = await h.Sut.CreateAsync(request, CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, obj.StatusCode);
        Assert.Null(h.Repo.Created);
    }

    [Fact]
    public async Task Create_NeverQueriesNosis_BecauseNosisIsBilledPerQuery()
    {
        var h = BuildHarness();

        var result = await h.Sut.CreateAsync(BuildRequest(), CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(result);
        Assert.Equal(0, h.Nosis.CallCount);          // the whole point of this file
        Assert.NotNull(h.Repo.Created);
        Assert.Null(h.Repo.Created!.NosisDataJson);
        Assert.Null(h.Repo.Created.NosisDataUpdatedAt);
    }

    [Fact]
    public async Task Create_ResponseCarriesNullNosis()
    {
        var h = BuildHarness();

        var result = await h.Sut.CreateAsync(BuildRequest(), CancellationToken.None);

        var created  = Assert.IsType<CreatedAtActionResult>(result);
        var response = Assert.IsType<EntityResponse>(created.Value);
        Assert.Null(response.Nosis);
        Assert.Null(response.NosisDataUpdatedAt);
    }

    [Fact]
    public async Task Create_StillEnrichesTheBilledPerNothingSiblings()
    {
        // Guards the other direction: the NOSIS block was removed without disturbing the BCRA and
        // CASFOG blocks that sit immediately above it.
        var h = BuildHarness();

        await h.Sut.CreateAsync(BuildRequest(), CancellationToken.None);

        Assert.Equal(1, h.Bcra.CallCount);
        Assert.Equal(1, h.Casfog.CallCount);
        Assert.NotNull(h.Repo.Created);
        Assert.False(string.IsNullOrEmpty(h.Repo.Created!.BcraDataJson));
        Assert.False(string.IsNullOrEmpty(h.Repo.Created.CasfogDataJson));
    }

    /// <summary>
    /// A snapshot persisted on create must be stamped, the way the Lufe branch already stamps its
    /// own. The two blocks used to write the JSON and leave the timestamp null, so a freshly created
    /// entity reported that BCRA and CASFOG had never been refreshed while holding their data.
    ///
    /// <para>
    /// That was cosmetic while the detail response shipped the payloads. It stopped being cosmetic
    /// when GET /api/entities/{id} began shipping these timestamps WITHOUT the payloads: the
    /// timestamp is the only thing the detail now says about these two sources.
    /// </para>
    ///
    /// <para>
    /// This fixes rows created from here on. It does NOT retroactively fix older rows — scripts 025,
    /// 033 and 042 added the columns with no backfill by design — which is why a null timestamp
    /// still must not be read as "this source has no data". NOSIS is excluded on purpose: it is
    /// billed per query and is never enriched on create, as the two tests above pin.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Create_StampsTheSnapshotsItPersists()
    {
        var before = DateTime.UtcNow;
        var h = BuildHarness();

        await h.Sut.CreateAsync(BuildRequest(), CancellationToken.None);

        Assert.NotNull(h.Repo.Created);
        Assert.NotNull(h.Repo.Created!.BcraDataUpdatedAt);
        Assert.NotNull(h.Repo.Created.CasfogDataUpdatedAt);
        Assert.InRange(h.Repo.Created.BcraDataUpdatedAt!.Value,   before, DateTime.UtcNow);
        Assert.InRange(h.Repo.Created.CasfogDataUpdatedAt!.Value, before, DateTime.UtcNow);

        // NOSIS is not enriched on create at all, so it stays unstamped.
        Assert.Null(h.Repo.Created.NosisDataUpdatedAt);
    }

    [Fact]
    public async Task Create_SkipsLufeWhenTheTenantHasNoApiKeyConfigured()
    {
        // Documents why the Lufe stub is never invoked here: the key is a per-tenant config lookup,
        // absent in this harness. Pins that the skip is credential-driven, not accidental.
        var h = BuildHarness();

        await h.Sut.CreateAsync(BuildRequest(), CancellationToken.None);

        Assert.Equal(0, h.Lufe.CallCount);
        Assert.Null(h.Repo.Created!.LufeDataJson);
    }

    /// <summary>
    /// The MiPyME category the Portal sends comes from POST /api/entities/lookup-arca, which resolves
    /// it from LUFE under a 4 s timeout. LUFE is slow and inconsistent, so that prefill intermittently
    /// arrives null and the entity used to be created with a blank category that nothing ever filled.
    /// The LUFE enrichment block here already holds a fresh snapshot fetched under a far more generous
    /// 30 s timeout, so it fills the blank from the same certificate — on the write it was already
    /// making, and only when the column is blank.
    /// </summary>
    [Fact]
    public async Task Create_WhenNoCategorySupplied_BackfillsItFromTheLufeSnapshot()
    {
        var h = BuildHarness(lufeApiKey: "test-key");
        h.Lufe.Result = LufeSnapshot("Micro");

        await h.Sut.CreateAsync(BuildRequest(mipymeCategory: null), CancellationToken.None);

        Assert.Equal(1, h.Lufe.CallCount);
        Assert.NotNull(h.Repo.Created);
        Assert.Equal("Micro", h.Repo.Created!.MipymeCategory);
        Assert.False(string.IsNullOrEmpty(h.Repo.Created.LufeDataJson));
    }

    [Fact]
    public async Task Create_WhenACategoryIsSupplied_LufeDoesNotOverwriteIt()
    {
        // The analyst's own choice wins over the snapshot, the same way it does on refresh-lufe.
        var h = BuildHarness(lufeApiKey: "test-key");
        h.Lufe.Result = LufeSnapshot("Micro");

        await h.Sut.CreateAsync(BuildRequest(mipymeCategory: "Mediana"), CancellationToken.None);

        Assert.Equal("Mediana", h.Repo.Created!.MipymeCategory);
        Assert.False(string.IsNullOrEmpty(h.Repo.Created.LufeDataJson));   // snapshot still persisted
    }

    /// <summary>
    /// The category the client SENDS used to be persisted with .Trim() only, so it bypassed the
    /// NormalizeMipymeCategoria the LUFE backfill above already goes through. Whether the column held
    /// the canonical set therefore depended on which path wrote the row. These four pin that the
    /// client path folds to the same set CK_Entities_MipymeCategory accepts — Micro / Pequenia /
    /// Mediana / NULL. No LUFE key here, so the backfill branch stays off and each assertion is about
    /// the client value alone.
    /// </summary>
    [Theory]
    [InlineData("MICRO",     "Micro")]      // case folded
    [InlineData("  micro  ", "Micro")]      // the .Trim() this replaced is subsumed by the normaliser
    [InlineData("Pequeña",   "Pequenia")]   // accent folded to the ASCII value the constraint accepts
    [InlineData("Pequenia",  "Pequenia")]   // ALREADY canonical: normalising must be a no-op, not null
    [InlineData("Mediana",   "Mediana")]
    public async Task Create_NormalisesTheCategoryTheClientSends(string sent, string expected)
    {
        var h = BuildHarness();

        var result = await h.Sut.CreateAsync(BuildRequest(mipymeCategory: sent), CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(result);
        Assert.NotNull(h.Repo.Created);
        Assert.Equal(expected, h.Repo.Created!.MipymeCategory);
    }

    /// <summary>
    /// An unrecognised value normalises to null — tolerant and silent, exactly what the backfill
    /// already does with a certificate category it cannot place. Deliberately NOT a 400: the write
    /// still succeeds, it just refuses to persist a value the check constraint would reject.
    /// </summary>
    [Theory]
    [InlineData("Grande")]
    [InlineData("II")]
    [InlineData("   ")]
    public async Task Create_DropsAnUnrecognisedCategoryWithoutFailingTheRequest(string sent)
    {
        var h = BuildHarness();

        var result = await h.Sut.CreateAsync(BuildRequest(mipymeCategory: sent), CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(result);   // tolerant — no ProblemDetails, no 400
        Assert.NotNull(h.Repo.Created);
        Assert.Null(h.Repo.Created!.MipymeCategory);
    }
}
