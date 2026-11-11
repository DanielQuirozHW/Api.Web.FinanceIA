// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-13

using System.Security.Claims;
using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// First coverage of PUT /api/entities/{id} — EntitiesController.UpdateAsync.
///
/// <para>
/// The write it pins is MipymeCategory. That field used to be persisted as
/// <c>request.MipymeCategory?.Trim()</c>, bypassing the NormalizeMipymeCategoria that the LUFE
/// backfill paths go through, so whether the column held the canonical set
/// CK_Entities_MipymeCategory accepts — Micro / Pequenia / Mediana / NULL — depended on which path
/// wrote the row. It now goes through the same helper, which also makes the PUT the place a
/// non-idempotent normaliser would show up: the Portal edits an entity by round-tripping the value it
/// just read off GET, so a canonical "Pequenia" arriving back MUST normalise to itself rather than to
/// null.
/// </para>
///
/// Following the repo's no-mock-library convention the dependencies are hand-rolled private sealed
/// stubs; members outside the tested path throw NotImplementedException so an unexpected call is loud.
/// </summary>
public sealed class EntitiesControllerUpdateTests
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
        public Entity? EntityToReturn { get; set; }
        public Entity? Updated        { get; private set; }
        public int     UpdateCount    { get; private set; }

        public Task<Entity?> GetByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => Task.FromResult(EntityToReturn);

        public Task<Entity> UpdateAsync(Entity entity, CancellationToken ct)
        {
            UpdateCount++;
            Updated = entity;
            return Task.FromResult(entity);
        }

        /// <summary>Set by the CUIT-correction tests; otherwise the duplicate lookup is unexpected.</summary>
        public bool AllowCuitLookup { get; set; }

        // Reachable only when the CUIT changes; the category tests keep it identical, so a call there
        // means the duplicate-check branch was entered unexpectedly. The correction tests own the
        // new CUIT (no other entity holds it).
        public Task<Entity?> GetByCuitAsync(string cuit, string tenantId, CancellationToken ct)
            => AllowCuitLookup ? Task.FromResult<Entity?>(null) : throw new NotImplementedException();

        public Task<bool> ExistsByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<Entity> CreateAsync(Entity entity, CancellationToken ct)
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

        public Task UpdateTeamAsync(Guid userId, Guid? teamId, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<int> ClearTeamAsync(Guid teamId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubAuditService : IAuditService
    {
        public List<(string Action, string? OldValueJson, string? NewValueJson)> Calls { get; } = [];

        public Task LogAsync(string tenantId, Guid? userId, string action, string severity = "Info",
            string? entityType = null, string? entityId = null, string? ipAddress = null,
            string? requestId = null, string? oldValueJson = null, string? newValueJson = null,
            CancellationToken ct = default)
        {
            Calls.Add((action, oldValueJson, newValueJson));
            return Task.CompletedTask;
        }

        public Task LogSessionAsync(Guid tenantId, Guid? userId, string email, string eventType,
            string? ipAddress, string? userAgent, string? failureReason = null,
            CancellationToken ct = default)
            => Task.CompletedTask;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private sealed record Harness(EntitiesController Sut, StubEntityRepository Repo, Entity Seeded, StubAuditService Audit);

    /// <param name="seededCategory">
    /// The category already on the row, so a test can tell "the PUT wrote this" apart from "the seed
    /// was left alone".
    /// </param>
    private static Harness BuildHarness(string? seededCategory = null)
    {
        var seeded = new Entity
        {
            EntityId       = Guid.NewGuid(),
            TenantId       = Tenant,
            CUIT           = Cuit,
            BusinessName   = "Test SA",
            MipymeCategory = seededCategory
        };

        var repo  = new StubEntityRepository { EntityToReturn = seeded };
        var audit = new StubAuditService();

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
            audit,                       // IAuditService
            null!,                       // ILufeService      — the PUT enriches nothing
            null!,                       // IBcraService
            null!,                       // ICasfogService
            null!,                       // INosisService
            null!,                       // IIntegrationAccessService
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
            new ConfigurationBuilder().Build(),
            NullLogger<EntitiesController>.Instance);

        // UpdateAsync reads the Firebase uid off the principal and 401s without it.
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("uid", Uid) }, "Test"));
        sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        return new Harness(sut, repo, seeded, audit);
    }

    // The CUIT is deliberately the seeded one: an unchanged CUIT skips the duplicate lookup, keeping
    // each test about the category and nothing else.
    private static UpdateEntityRequest BuildRequest(string? mipymeCategory) => new()
    {
        CUIT           = Cuit,
        BusinessName   = "Test SA",
        IsMonitored    = true,
        MipymeCategory = mipymeCategory
    };

    // ── Tests ─────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("Pequenia",  "Pequenia")]   // ALREADY canonical: the Portal's own round-trip must no-op
    [InlineData("Micro",     "Micro")]
    [InlineData("Mediana",   "Mediana")]
    [InlineData("MICRO",     "Micro")]      // case folded
    [InlineData("  micro  ", "Micro")]      // the .Trim() this replaced is subsumed by the normaliser
    [InlineData("Pequeña",   "Pequenia")]   // accent folded to the value the constraint accepts
    public async Task Update_NormalisesTheCategoryTheClientSends(string sent, string expected)
    {
        var h = BuildHarness(seededCategory: "Mediana");

        var result = await h.Sut.UpdateAsync(h.Seeded.EntityId, BuildRequest(sent), CancellationToken.None);

        var ok       = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<EntityResponse>(ok.Value);

        Assert.Equal(expected, h.Seeded.MipymeCategory);
        Assert.Equal(expected, response.MipymeCategory);
        Assert.Equal(1, h.Repo.UpdateCount);
        Assert.Same(h.Seeded, h.Repo.Updated);
    }

    /// <summary>
    /// An unrecognised value normalises to null — tolerant and silent, the same thing the LUFE
    /// backfill does with a certificate category it cannot place. Deliberately NOT a 400: the update
    /// still succeeds, it just refuses to persist a value the check constraint would reject.
    /// </summary>
    [Theory]
    [InlineData("Grande")]
    [InlineData("II")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Update_DropsAnUnrecognisedCategoryWithoutFailingTheRequest(string? sent)
    {
        var h = BuildHarness(seededCategory: "Micro");

        var result = await h.Sut.UpdateAsync(h.Seeded.EntityId, BuildRequest(sent), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);   // tolerant — no ProblemDetails, no 400
        Assert.Null(h.Seeded.MipymeCategory);
        Assert.Equal(1, h.Repo.UpdateCount);
    }

    // ── Business name normalization ───────────────────────────────────────────

    [Theory]
    [InlineData("\"silo Chico S.A.\"",              "silo Chico S.A.")]
    [InlineData("\u2018\u201CAgro Norte S.A.\u201D\u2019", "Agro Norte S.A.")]
    [InlineData("Sociedad \"La Esperanza\" S.A.",   "Sociedad \"La Esperanza\" S.A.")]
    public async Task Update_StoresTheNormalizedBusinessName(string sent, string expected)
    {
        var h = BuildHarness();
        var request = new UpdateEntityRequest { CUIT = Cuit, BusinessName = sent, IsMonitored = true };

        var result = await h.Sut.UpdateAsync(h.Seeded.EntityId, request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(expected, h.Seeded.BusinessName);
        Assert.Equal(expected, Assert.IsType<EntityResponse>(ok.Value).BusinessName);
    }

    [Fact]
    public async Task Update_NameEmptyAfterNormalizing_Returns400_AndWritesNothing()
    {
        var h = BuildHarness();
        var request = new UpdateEntityRequest { CUIT = Cuit, BusinessName = "\"\"", IsMonitored = true };

        var result = await h.Sut.UpdateAsync(h.Seeded.EntityId, request, CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, obj.StatusCode);
        Assert.Equal("Test SA", h.Seeded.BusinessName);
        Assert.Equal(0, h.Repo.UpdateCount);
    }

    // ── V1: a corrected CUIT drops the old CUIT's vendor snapshots ────────────
    //
    // The document, analysis, legajo, BCRA, LUFE, CASFOG and NOSIS dependencies are null! in this
    // harness: a correction that touched documents or analyses, or fetched anything for the new CUIT,
    // would throw. That the call returns 200 is part of what these tests prove.

    private const string CorrectedCuit = "30-87654321-0";

    private static void SeedAllSnapshots(Entity e)
    {
        e.FiscalDataJson = """{"fiscal":1}""";  e.FiscalDataUpdatedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        e.LufeDataJson   = """{"lufe":1}""";    e.LufeDataUpdatedAt   = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);
        e.BcraDataJson   = """{"bcra":1}""";    e.BcraDataUpdatedAt   = new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc);
        e.CasfogDataJson = """{"casfog":1}""";  e.CasfogDataUpdatedAt = new DateTime(2026, 9, 4, 0, 0, 0, DateTimeKind.Utc);
        e.NosisDataJson  = """{"nosis":1}""";   e.NosisDataUpdatedAt  = new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc);
    }

    private static UpdateEntityRequest CorrectionRequest(string cuit) => new()
    {
        CUIT           = cuit,
        BusinessName   = "Test SA",
        IsMonitored    = true,
        MipymeCategory = "Micro",
        Sector         = "Industria"
    };

    [Fact]
    public async Task ACuitCorrection_ClearsAllFiveSnapshotsAndTheirDates_InTheOneSave()
    {
        var h = BuildHarness();
        h.Repo.AllowCuitLookup = true;
        SeedAllSnapshots(h.Seeded);

        var result = await h.Sut.UpdateAsync(h.Seeded.EntityId, CorrectionRequest(CorrectedCuit), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, h.Repo.UpdateCount);           // no second write
        Assert.Same(h.Seeded, h.Repo.Updated);

        var e = h.Repo.Updated!;
        Assert.Equal(CorrectedCuit, e.CUIT);
        Assert.Null(e.FiscalDataJson); Assert.Null(e.FiscalDataUpdatedAt);
        Assert.Null(e.LufeDataJson);   Assert.Null(e.LufeDataUpdatedAt);
        Assert.Null(e.BcraDataJson);   Assert.Null(e.BcraDataUpdatedAt);
        Assert.Null(e.CasfogDataJson); Assert.Null(e.CasfogDataUpdatedAt);
        Assert.Null(e.NosisDataJson);  Assert.Null(e.NosisDataUpdatedAt);

        // Request-supplied fields are the request's, not cleared.
        Assert.Equal("Micro", e.MipymeCategory);
        Assert.Equal("Industria", e.Sector);
    }

    [Fact]
    public async Task ACuitCorrection_ClearsTheNosisManagerReport_WithoutListingItAsAScoreSource()
    {
        var h = BuildHarness();
        h.Repo.AllowCuitLookup = true;
        h.Seeded.NosisReportJson       = """{"fileSha256":"aa"}""";
        h.Seeded.NosisReportDate       = new DateTime(2026, 9, 6, 14, 36, 0, DateTimeKind.Unspecified);
        h.Seeded.NosisReportImportedAt = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);

        var result = await h.Sut.UpdateAsync(h.Seeded.EntityId, CorrectionRequest(CorrectedCuit), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var e = h.Repo.Updated!;
        Assert.Null(e.NosisReportJson);
        Assert.Null(e.NosisReportDate);
        Assert.Null(e.NosisReportImportedAt);

        // The report feeds no score, so it is not a cleared score source.
        var call = Assert.Single(h.Audit.Calls);
        using var newDoc = JsonDocument.Parse(call.NewValueJson!);
        Assert.Equal(0, newDoc.RootElement.GetProperty("clearedSnapshots").GetArrayLength());
    }

    [Fact]
    public async Task AnUnchangedCuit_KeepsTheNosisManagerReport()
    {
        var h = BuildHarness();
        h.Seeded.NosisReportJson       = """{"fileSha256":"aa"}""";
        h.Seeded.NosisReportDate       = new DateTime(2026, 9, 6, 14, 36, 0, DateTimeKind.Unspecified);
        h.Seeded.NosisReportImportedAt = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);

        var result = await h.Sut.UpdateAsync(h.Seeded.EntityId, CorrectionRequest(Cuit), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("""{"fileSha256":"aa"}""", h.Seeded.NosisReportJson);
        Assert.NotNull(h.Seeded.NosisReportDate);
        Assert.NotNull(h.Seeded.NosisReportImportedAt);
    }

    [Fact]
    public async Task ACuitCorrection_AuditsWhichSnapshotsItCleared_WithoutAnyCuit()
    {
        var h = BuildHarness();
        h.Repo.AllowCuitLookup = true;
        h.Seeded.LufeDataJson      = """{"lufe":1}""";
        h.Seeded.LufeDataUpdatedAt = new DateTime(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc);
        h.Seeded.BcraDataJson      = """{"bcra":1}""";   // a payload with no stamp still counts

        await h.Sut.UpdateAsync(h.Seeded.EntityId, CorrectionRequest(CorrectedCuit), CancellationToken.None);

        var call = Assert.Single(h.Audit.Calls);
        Assert.Equal("Entity.Updated", call.Action);
        Assert.NotNull(call.OldValueJson);
        Assert.NotNull(call.NewValueJson);

        // No CUIT, old or new, enters AuditLogs.
        foreach (var json in new[] { call.OldValueJson!, call.NewValueJson! })
        {
            Assert.DoesNotContain("12345678", json, StringComparison.Ordinal);
            Assert.DoesNotContain("87654321", json, StringComparison.Ordinal);
        }

        using var newDoc = JsonDocument.Parse(call.NewValueJson!);
        Assert.True(newDoc.RootElement.GetProperty("cuitChanged").GetBoolean());
        Assert.Equal(
            new[] { RescoreSource.Lufe, RescoreSource.Bcra },
            newDoc.RootElement.GetProperty("clearedSnapshots").EnumerateArray().Select(x => x.GetString()).ToArray());

        using var oldDoc = JsonDocument.Parse(call.OldValueJson!);
        Assert.True(oldDoc.RootElement.GetProperty("cuitChanged").GetBoolean());
        var snapshots = oldDoc.RootElement.GetProperty("snapshots").EnumerateArray().ToArray();
        Assert.Equal(2, snapshots.Length);
        Assert.Equal(RescoreSource.Lufe, snapshots[0].GetProperty("source").GetString());
        Assert.Equal(new DateTime(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc), snapshots[0].GetProperty("updatedAt").GetDateTime().ToUniversalTime());
        Assert.Equal(JsonValueKind.Null, snapshots[1].GetProperty("updatedAt").ValueKind);
    }

    [Fact]
    public async Task ACuitCorrection_OfAnEntityWithNoSnapshots_AuditsAnEmptyList()
    {
        var h = BuildHarness();
        h.Repo.AllowCuitLookup = true;

        var result = await h.Sut.UpdateAsync(h.Seeded.EntityId, CorrectionRequest(CorrectedCuit), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var call = Assert.Single(h.Audit.Calls);
        using var newDoc = JsonDocument.Parse(call.NewValueJson!);
        Assert.Equal(0, newDoc.RootElement.GetProperty("clearedSnapshots").GetArrayLength());
    }

    [Fact]
    public async Task AnUnchangedCuit_TouchesNoSnapshot_AndAuditsNoJson()
    {
        var h = BuildHarness();
        SeedAllSnapshots(h.Seeded);
        var before = JsonSerializer.Serialize(new
        {
            h.Seeded.FiscalDataJson, h.Seeded.FiscalDataUpdatedAt, h.Seeded.LufeDataJson, h.Seeded.LufeDataUpdatedAt,
            h.Seeded.BcraDataJson, h.Seeded.BcraDataUpdatedAt, h.Seeded.CasfogDataJson, h.Seeded.CasfogDataUpdatedAt,
            h.Seeded.NosisDataJson, h.Seeded.NosisDataUpdatedAt
        });

        // Same CUIT: the stub throws on the duplicate lookup, so it is never reached either.
        var result = await h.Sut.UpdateAsync(h.Seeded.EntityId, CorrectionRequest(Cuit), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var after = JsonSerializer.Serialize(new
        {
            h.Seeded.FiscalDataJson, h.Seeded.FiscalDataUpdatedAt, h.Seeded.LufeDataJson, h.Seeded.LufeDataUpdatedAt,
            h.Seeded.BcraDataJson, h.Seeded.BcraDataUpdatedAt, h.Seeded.CasfogDataJson, h.Seeded.CasfogDataUpdatedAt,
            h.Seeded.NosisDataJson, h.Seeded.NosisDataUpdatedAt
        });
        Assert.Equal(before, after);

        var call = Assert.Single(h.Audit.Calls);
        Assert.Null(call.OldValueJson);
        Assert.Null(call.NewValueJson);
    }
}
