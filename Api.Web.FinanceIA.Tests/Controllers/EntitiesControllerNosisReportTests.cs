// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-10

using System.Security.Claims;
using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.Concurrency;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Nosis;
using Api.Web.FinanceIA.Models.Settings;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.NosisReport;
using Api.Web.FinanceIA.Tests.Servicios.NosisReport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// POST / DELETE /api/entities/{id}/nosis/report and the <c>report</c> field of GET {id}/nosis, end to
/// end over an InMemory FinanciaDbContext, the real repositories and the real parser, with synthetic
/// reports from <see cref="NosisReportPdfBuilder"/>.
/// </summary>
public sealed class EntitiesControllerNosisReportTests
{
    private const string Tenant = "tenant-1";
    private const string Other  = "tenant-2";
    private const string Uid    = "firebase-uid-1";

    // ── Stubs ─────────────────────────────────────────────────────────────────

    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private sealed class StubTenantProfileService : ITenantProfileService
    {
        public bool NosisEnabled { get; set; } = true;

        public Task<TenantProfileResolution> GetEffectiveAsync(string tenantId, CancellationToken ct)
            => Task.FromResult(new TenantProfileResolution(
                new TenantProfile(TenantSettingKeys.EntityTypeSgr, true, true, NosisEnabled), TenantProfileSource.Configured));

        public Task<TenantProfileResolution> SetEntityTypeAsync(string tenantId, string entityType, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class StubUserRepository : IUserRepository
    {
        public static readonly Guid UserId = Guid.NewGuid();

        public Task<User?> GetByFirebaseUidAsync(string firebaseUid, CancellationToken ct)
            => Task.FromResult<User?>(new User { UserId = UserId, TenantId = Tenant, FirebaseUid = firebaseUid, Email = "analyst@example.com" });

        public Task<User?> GetByUserIdAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<User?> GetByEmailAsync(string email, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> ExistsByFirebaseUidAsync(string firebaseUid, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<User>> GetByIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct) => throw new NotImplementedException();
        public Task<User> CreateAsync(User user, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateLastLoginAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateOnboardingFlagsAsync(Guid userId, string? flagsJson, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<User>> GetActiveByTenantAsync(string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateTeamAsync(Guid userId, Guid? teamId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> ClearTeamAsync(Guid teamId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed record AuditCall(string TenantId, Guid? UserId, string Action, string? EntityType, string? EntityId, string? OldValueJson, string? NewValueJson);

    private sealed class RecordingAuditService : IAuditService
    {
        public List<AuditCall> Calls { get; } = [];

        public Task LogAsync(string tenantId, Guid? userId, string action, string severity = "Info",
            string? entityType = null, string? entityId = null, string? ipAddress = null,
            string? requestId = null, string? oldValueJson = null, string? newValueJson = null,
            CancellationToken ct = default)
        {
            Calls.Add(new AuditCall(tenantId, userId, action, entityType, entityId, oldValueJson, newValueJson));
            return Task.CompletedTask;
        }

        public Task LogSessionAsync(Guid tenantId, Guid? userId, string email, string eventType,
            string? ipAddress, string? userAgent, string? failureReason = null, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private sealed class AllLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception) + (exception is null ? string.Empty : " " + exception)));
    }

    /// <summary>A report repository whose writes the test can count around a real one.</summary>
    private sealed class CountingReportRepository(INosisReportRepository inner) : INosisReportRepository
    {
        public int Saves { get; private set; }
        public int Clears { get; private set; }

        public Task<NosisReportSubject?> GetSubjectAsync(Guid entityId, string tenantId, CancellationToken ct) => inner.GetSubjectAsync(entityId, tenantId, ct);
        public Task<NosisReportSnapshot?> GetAsync(Guid entityId, string tenantId, CancellationToken ct) => inner.GetAsync(entityId, tenantId, ct);
        public Task<(string? Sha256, string? ParserVersion)> GetStoredFingerprintAsync(Guid entityId, string tenantId, CancellationToken ct)
            => inner.GetStoredFingerprintAsync(entityId, tenantId, ct);

        public Task<bool> SaveAsync(Guid entityId, string tenantId, string json, DateTime reportDate, DateTime importedAt, CancellationToken ct)
        {
            Saves++;
            return inner.SaveAsync(entityId, tenantId, json, reportDate, importedAt, ct);
        }

        public Task<bool> ClearAsync(Guid entityId, string tenantId, CancellationToken ct)
        {
            Clears++;
            return inner.ClearAsync(entityId, tenantId, ct);
        }
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private sealed record Harness(
        EntitiesController Sut,
        DbContextOptions<FinanciaDbContext> Db,
        CountingReportRepository Reports,
        RecordingAuditService Audit,
        AllLogger<EntitiesController> Logger,
        StubTenantProfileService Profile);

    private static Harness Build(string tenantId = Tenant, bool nosisConfigured = false)
    {
        var db = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"nosis-report-controller-{Guid.NewGuid()}")
            .Options;

        // One context per harness is enough: every call here is sequential.
        var ctx = new FinanciaDbContext(db);
        var reports = new CountingReportRepository(new NosisReportRepository(ctx, NullLogger<NosisReportRepository>.Instance));
        var audit = new RecordingAuditService();
        var logger = new AllLogger<EntitiesController>();
        var profile = new StubTenantProfileService();

        // The NOSIS web service is NOT configured unless asked: the import must not need it.
        var settings = new Dictionary<string, string?>();
        if (nosisConfigured)
        {
            settings["Nosis:Enabled"] = "true";
            settings["Nosis:ApiKey"] = "k";
            settings["Nosis:VR"] = "1";
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var sut = new EntitiesController(
            new StubTenantContextAccessor
            {
                TenantContext = new TenantContext
                {
                    TenantId = tenantId, FirebaseProjectId = "proj", FirebaseCredentialJson = "{}", RecaptchaSecretKey = "secret"
                }
            },
            new EntityRepository(ctx, NullLogger<EntityRepository>.Instance), // IEntityRepository
            null!,                       // IArcaPadronService
            null!,                       // IArcaConstanciaService
            null!,                       // IArcaWsaaService
            null!,                       // IApiCallLogService
            audit,                       // IAuditService
            null!,                       // ILufeService
            null!,                       // IBcraService
            null!,                       // ICasfogService
            null!,                       // INosisService — the import never calls NOSIS
            new IntegrationAccessService(profile, config, NullLogger<IntegrationAccessService>.Instance),
            null!,                       // IDocumentRepository — nothing is stored as a document
            null!,                       // IDocumentStorageService — the file is never stored
            new StubUserRepository(),    // IUserRepository
            null!,                       // ICreditLineRepository
            null!,                       // IAnalysisRepository
            null!,                       // INoteRepository
            null!,                       // IPersonLegajoRepository
            reports,                     // INosisReportRepository
            new NosisManagerReportParser(), // INosisManagerReportParser
            new KeyedAsyncLock(),        // KeyedAsyncLock
            config,                      // IConfiguration
            logger);

        sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("uid", Uid)], "Test"))
            }
        };

        return new Harness(sut, db, reports, audit, logger, profile);
    }

    private static async Task<Guid> SeedEntityAsync(
        DbContextOptions<FinanciaDbContext> db, string tenantId = Tenant, string cuit = NosisReportPdfBuilder.SubjectCuit,
        string personType = PersonTypes.Juridica)
    {
        var id = Guid.NewGuid();
        using var ctx = new FinanciaDbContext(db);
        ctx.Entities.Add(new Entity
        {
            EntityId = id, TenantId = tenantId, CUIT = cuit, BusinessName = "Sintética S.A.", PersonType = personType,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, CreatedBy = Guid.NewGuid(),
            NosisDataJson = """{"estado":0}""", NosisDataUpdatedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        ctx.ChangeTracker.DetectChanges();
        await ctx.SaveChangesAsync();
        return id;
    }

    private static async Task<Entity> ReloadAsync(DbContextOptions<FinanciaDbContext> db, Guid id)
    {
        using var ctx = new FinanciaDbContext(db);
        return await ctx.Entities.AsNoTracking().SingleAsync(e => e.EntityId == id);
    }

    private static IFormFile Pdf(byte[] bytes, string contentType = "application/pdf")
        => new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "informe.pdf")
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };

    private static byte[] Report() => new NosisReportPdfBuilder { Encrypt = true }.Build();

    private static EntityNosisResponse Ok200(IActionResult result)
        => Assert.IsType<EntityNosisResponse>(Assert.IsType<OkObjectResult>(result).Value);

    private static ProblemDetails Problem(IActionResult result, int status)
    {
        var obj = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(status, obj.StatusCode);
        return Assert.IsAssignableFrom<ProblemDetails>(obj.Value);
    }

    // ── Import ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Import_StoresTheParsedReport_AndReturnsIt()
    {
        var h = Build();
        var id = await SeedEntityAsync(h.Db);

        var response = Ok200(await h.Sut.ImportNosisReportAsync(id, Pdf(Report()), CancellationToken.None));

        Assert.NotNull(response.Report);
        Assert.Equal("2026-11-03T14:36:00", response.Report!.ReportDate);
        Assert.Equal(64, response.Report.FileSha256.Length);
        Assert.Equal(NosisManagerReportParser.ParserVersion, response.Report.ParserVersion);
        Assert.Equal(742, response.Report.Summary!.Score);
        Assert.NotNull(response.Nosis);   // the WS.01 snapshot rides along, untouched

        var e = await ReloadAsync(h.Db, id);
        Assert.NotNull(e.NosisReportJson);
        Assert.Equal(new DateTime(2026, 11, 3, 14, 36, 0), e.NosisReportDate);
        Assert.NotNull(e.NosisReportImportedAt);
        Assert.Equal("""{"estado":0}""", e.NosisDataJson);

        var audit = Assert.Single(h.Audit.Calls);
        Assert.Equal(EntitiesController.NosisReportImportedAuditAction, audit.Action);
        Assert.Equal("Entity", audit.EntityType);
        Assert.Equal(id.ToString(), audit.EntityId);
        Assert.Equal(StubUserRepository.UserId, audit.UserId);
        Assert.Null(audit.OldValueJson);
        using var doc = JsonDocument.Parse(audit.NewValueJson!);
        Assert.Equal(response.Report.FileSha256, doc.RootElement.GetProperty("fileSha256").GetString());
        Assert.Equal("2026-11-03T14:36:00", doc.RootElement.GetProperty("reportDate").GetString());
        Assert.Equal("ok", doc.RootElement.GetProperty("sections").GetProperty("summary").GetString());
        // Statuses and provenance only — no value from the report.
        Assert.DoesNotContain("7258316409", audit.NewValueJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Banco", audit.NewValueJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_ReturnsTheImportedReport()
    {
        var h = Build();
        var id = await SeedEntityAsync(h.Db);
        await h.Sut.ImportNosisReportAsync(id, Pdf(Report()), CancellationToken.None);

        var response = Ok200(await h.Sut.GetNosisAsync(id, CancellationToken.None));

        Assert.Equal(24, response.Report!.Evolucion24m!.Count);
        Assert.Equal(NosisReportSectionKeys.All, response.Report.Sections.Keys);
    }

    [Fact]
    public async Task Get_WithNoReport_ReturnsANullReport()
    {
        var h = Build();
        var id = await SeedEntityAsync(h.Db);

        Assert.Null(Ok200(await h.Sut.GetNosisAsync(id, CancellationToken.None)).Report);
    }

    [Fact]
    public async Task TheSameFileAgain_WritesNothing_AndAuditsNothing()
    {
        var h = Build();
        var id = await SeedEntityAsync(h.Db);
        var bytes = Report();
        await h.Sut.ImportNosisReportAsync(id, Pdf(bytes), CancellationToken.None);
        var first = await ReloadAsync(h.Db, id);

        var again = Ok200(await h.Sut.ImportNosisReportAsync(id, Pdf(bytes), CancellationToken.None));

        Assert.Equal(1, h.Reports.Saves);
        Assert.Single(h.Audit.Calls);
        Assert.Equal(first.NosisReportImportedAt, (await ReloadAsync(h.Db, id)).NosisReportImportedAt);
        Assert.NotNull(again.Report);
    }

    [Fact]
    public async Task TheSameFile_ReadByAnOlderParser_IsReadAgain()
    {
        var h = Build();
        var id = await SeedEntityAsync(h.Db);
        var bytes = Report();
        var first = Ok200(await h.Sut.ImportNosisReportAsync(id, Pdf(bytes), CancellationToken.None));

        // Pretend the stored report came from the previous parser version.
        using (var ctx = new FinanciaDbContext(h.Db))
        {
            var e = await ctx.Entities.SingleAsync(x => x.EntityId == id);
            e.NosisReportJson = e.NosisReportJson!.Replace(
                $"\"parserVersion\":\"{NosisManagerReportParser.ParserVersion}\"", "\"parserVersion\":\"nm-1.0\"", StringComparison.Ordinal);
            ctx.ChangeTracker.DetectChanges();
            await ctx.SaveChangesAsync();
        }

        var again = Ok200(await h.Sut.ImportNosisReportAsync(id, Pdf(bytes), CancellationToken.None));

        Assert.Equal(2, h.Reports.Saves);
        Assert.Equal(NosisManagerReportParser.ParserVersion, again.Report!.ParserVersion);
        Assert.Equal(first.Report!.FileSha256, again.Report.FileSha256);
        Assert.Equal(EntitiesController.NosisReportReplacedAuditAction, h.Audit.Calls[^1].Action);
    }

    [Fact]
    public async Task ADifferentFile_ReplacesTheReport_AndAuditsTheReplacement()
    {
        var h = Build();
        var id = await SeedEntityAsync(h.Db);
        var first = Ok200(await h.Sut.ImportNosisReportAsync(id, Pdf(Report()), CancellationToken.None));

        var second = Ok200(await h.Sut.ImportNosisReportAsync(
            id, Pdf(new NosisReportPdfBuilder { FooterDate = "04.11.2026 09:15" }.Build()), CancellationToken.None));

        Assert.Equal("2026-11-04T09:15:00", second.Report!.ReportDate);
        Assert.NotEqual(first.Report!.FileSha256, second.Report.FileSha256);
        Assert.Equal(2, h.Reports.Saves);

        var replace = h.Audit.Calls[^1];
        Assert.Equal(EntitiesController.NosisReportReplacedAuditAction, replace.Action);
        using var old = JsonDocument.Parse(replace.OldValueJson!);
        Assert.Equal(first.Report.FileSha256, old.RootElement.GetProperty("fileSha256").GetString());
        Assert.Equal("2026-11-03T14:36:00", old.RootElement.GetProperty("reportDate").GetString());
    }

    [Fact]
    public async Task AReportAboutAnotherCuit_Is422_AndNeitherCuitLeaks()
    {
        var h = Build();
        var id = await SeedEntityAsync(h.Db, cuit: NosisReportPdfBuilder.OtherCuit);

        var result = await h.Sut.ImportNosisReportAsync(id, Pdf(Report()), CancellationToken.None);

        var problem = Problem(result, 422);
        Assert.Equal(EntitiesController.NosisReportCuitMismatchCode, problem.Extensions["errorCode"]);
        Assert.Equal(0, h.Reports.Saves);
        Assert.Empty(h.Audit.Calls);

        var body = JsonSerializer.Serialize(problem);
        var logs = string.Join('\n', h.Logger.Entries.Select(e => e.Message));
        foreach (var text in new[] { body, logs })
        {
            Assert.DoesNotContain("00000000", text, StringComparison.Ordinal);
            Assert.DoesNotContain("30000000001", text, StringComparison.Ordinal);
            Assert.DoesNotContain("30000000002", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task APdfThatIsNotANosisReport_Is422NotRecognized()
    {
        var h = Build();
        var id = await SeedEntityAsync(h.Db);

        var result = await h.Sut.ImportNosisReportAsync(id, Pdf(NosisReportPdfBuilder.PlainPdf("Balance general")), CancellationToken.None);

        Assert.Equal(EntitiesController.NosisReportNotRecognizedCode, Problem(result, 422).Extensions["errorCode"]);
        Assert.Equal(0, h.Reports.Saves);
        Assert.Contains(h.Logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("reason=NoTitle", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APersonaHumana_Is422_BeforeTheFileIsRead()
    {
        var h = Build();
        var id = await SeedEntityAsync(h.Db, cuit: "20-00000000-1", personType: PersonTypes.Humana);

        var result = await h.Sut.ImportNosisReportAsync(id, Pdf(Report()), CancellationToken.None);

        Assert.Equal(EntitiesController.NosisReportPersonTypeUnsupportedCode, Problem(result, 422).Extensions["errorCode"]);
        Assert.Equal(0, h.Reports.Saves);
    }

    [Fact]
    public async Task NosisDisabledForTheTenant_Is400()
    {
        var h = Build();
        h.Profile.NosisEnabled = false;
        var id = await SeedEntityAsync(h.Db);

        var problem = Problem(await h.Sut.ImportNosisReportAsync(id, Pdf(Report()), CancellationToken.None), 400);

        Assert.Equal("NOSIS no está habilitado para este tenant.", problem.Detail);
        Assert.Equal(0, h.Reports.Saves);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheNosisWebServiceConfiguration_PlaysNoPart(bool configured)
    {
        var h = Build(nosisConfigured: configured);
        var id = await SeedEntityAsync(h.Db);

        Assert.NotNull(Ok200(await h.Sut.ImportNosisReportAsync(id, Pdf(Report()), CancellationToken.None)).Report);
    }

    [Fact]
    public async Task AnotherTenantsEntity_Is404_AndIsNotTouched()
    {
        var h = Build(Tenant);
        var foreign = await SeedEntityAsync(h.Db, tenantId: Other);

        Problem(await h.Sut.ImportNosisReportAsync(foreign, Pdf(Report()), CancellationToken.None), 404);
        Problem(await h.Sut.DeleteNosisReportAsync(foreign, CancellationToken.None), 404);
        Problem(await h.Sut.GetNosisAsync(foreign, CancellationToken.None), 404);

        Assert.Null((await ReloadAsync(h.Db, foreign)).NosisReportJson);
        Assert.Equal(0, h.Reports.Saves);
        Assert.Equal(0, h.Reports.Clears);
    }

    [Fact]
    public async Task AnotherTenantsReport_IsNeverServed()
    {
        var h = Build(Tenant);
        var foreign = await SeedEntityAsync(h.Db, tenantId: Other);

        var owner = Build(Other);
        var ownerSut = new EntitiesController(
            new StubTenantContextAccessor { TenantContext = new TenantContext { TenantId = Other, FirebaseProjectId = "p", FirebaseCredentialJson = "{}", RecaptchaSecretKey = "s" } },
            new EntityRepository(new FinanciaDbContext(h.Db), NullLogger<EntityRepository>.Instance),
            null!, null!, null!, null!, owner.Audit, null!, null!, null!, null!,
            new IntegrationAccessService(owner.Profile, new ConfigurationBuilder().Build(), NullLogger<IntegrationAccessService>.Instance),
            null!, null!, new StubUserRepository(), null!, null!, null!, null!,
            new NosisReportRepository(new FinanciaDbContext(h.Db), NullLogger<NosisReportRepository>.Instance),
            new NosisManagerReportParser(), new KeyedAsyncLock(), new ConfigurationBuilder().Build(), owner.Logger)
        {
            ControllerContext = h.Sut.ControllerContext
        };
        Ok200(await ownerSut.ImportNosisReportAsync(foreign, Pdf(Report()), CancellationToken.None));

        Problem(await h.Sut.GetNosisAsync(foreign, CancellationToken.None), 404);
        Assert.NotNull((await ReloadAsync(h.Db, foreign)).NosisReportJson);
    }

    [Theory]
    [InlineData(0, "application/pdf")]
    [InlineData(10, "image/png")]
    public async Task ABadFile_Is400ValidationError(int size, string contentType)
    {
        var h = Build();
        var id = await SeedEntityAsync(h.Db);
        var bytes = size == 0 ? [] : new byte[size];

        var problem = Problem(await h.Sut.ImportNosisReportAsync(id, Pdf(bytes, contentType), CancellationToken.None), 400);

        Assert.Equal("validation_error", problem.Extensions["errorCode"]);
    }

    [Fact]
    public async Task BytesWithoutThePdfSignature_Are400()
    {
        var h = Build();
        var id = await SeedEntityAsync(h.Db);

        var problem = Problem(await h.Sut.ImportNosisReportAsync(id, Pdf("not a pdf at all"u8.ToArray()), CancellationToken.None), 400);

        Assert.Equal("validation_error", problem.Extensions["errorCode"]);
    }

    [Fact]
    public void TheRequestBodyLimit_IsTheSharedPdfUploadLimit()
    {
        var method = typeof(EntitiesController).GetMethod(nameof(EntitiesController.ImportNosisReportAsync))!;
        var limit = method.GetCustomAttributes(typeof(RequestSizeLimitAttribute), false).Cast<RequestSizeLimitAttribute>().Single();

        var bytes = (long)typeof(RequestSizeLimitAttribute)
            .GetField("_bytes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(limit)!;
        Assert.Equal(PdfSizeLimits.MaxUploadRequestBytes, bytes);
    }

    // ── Logs ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheImportLog_CarriesCountsAndStatuses_NeverContent()
    {
        var h = Build();
        var id = await SeedEntityAsync(h.Db);

        await h.Sut.ImportNosisReportAsync(id, Pdf(Report()), CancellationToken.None);

        var info = Assert.Single(h.Logger.Entries, e => e.Message.StartsWith("NOSIS report imported", StringComparison.Ordinal));
        Assert.Contains("pages=", info.Message, StringComparison.Ordinal);
        Assert.Contains("elapsedMs=", info.Message, StringComparison.Ordinal);
        Assert.Contains("summary=ok", info.Message, StringComparison.Ordinal);
        Assert.Contains(Tenant, info.Message, StringComparison.Ordinal);

        var all = string.Join('\n', h.Logger.Entries.Select(e => e.Message));
        foreach (var forbidden in NosisReportPdfBuilder.DecoyValues.Concat(["30000000001", "00000000", "Banco Ficticio", "Proveedora", "7,258,316,409", "7258316409"]))
            Assert.DoesNotContain(forbidden, all, StringComparison.OrdinalIgnoreCase);
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_ClearsTheReport_AuditsOnce_AndIsIdempotent()
    {
        var h = Build();
        var id = await SeedEntityAsync(h.Db);
        var imported = Ok200(await h.Sut.ImportNosisReportAsync(id, Pdf(Report()), CancellationToken.None));

        var deleted = Ok200(await h.Sut.DeleteNosisReportAsync(id, CancellationToken.None));
        var again   = Ok200(await h.Sut.DeleteNosisReportAsync(id, CancellationToken.None));

        Assert.Null(deleted.Report);
        Assert.Null(again.Report);
        Assert.NotNull(deleted.Nosis);

        var e = await ReloadAsync(h.Db, id);
        Assert.Null(e.NosisReportJson);
        Assert.Null(e.NosisReportDate);
        Assert.Null(e.NosisReportImportedAt);
        Assert.Equal("""{"estado":0}""", e.NosisDataJson);

        var audits = h.Audit.Calls.Where(c => c.Action == EntitiesController.NosisReportDeletedAuditAction).ToList();
        var delete = Assert.Single(audits);
        using var old = JsonDocument.Parse(delete.OldValueJson!);
        Assert.Equal(imported.Report!.FileSha256, old.RootElement.GetProperty("fileSha256").GetString());
    }

    [Fact]
    public async Task Delete_OfAnUnknownEntity_Is404()
    {
        var h = Build();
        Problem(await h.Sut.DeleteNosisReportAsync(Guid.NewGuid(), CancellationToken.None), 404);
    }

    [Fact]
    public async Task Delete_IsNotGatedByTheEntitlement()
    {
        var h = Build();
        var id = await SeedEntityAsync(h.Db);
        await h.Sut.ImportNosisReportAsync(id, Pdf(Report()), CancellationToken.None);
        h.Profile.NosisEnabled = false;

        Ok200(await h.Sut.DeleteNosisReportAsync(id, CancellationToken.None));

        Assert.Null((await ReloadAsync(h.Db, id)).NosisReportJson);
    }
}
