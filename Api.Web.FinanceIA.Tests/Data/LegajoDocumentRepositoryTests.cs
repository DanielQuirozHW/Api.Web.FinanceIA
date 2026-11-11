// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-28

using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Tests.Legajo;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// <see cref="LegajoDocumentRepository"/> on the InMemory provider.
///
/// <para>
/// The InMemory provider cannot run ExecuteUpdateAsync, so the conditional transitions (the atomic
/// claim among them) are covered in two other ways: the WHERE clause itself —
/// <see cref="LegajoDocumentRepository.InStatus"/> — is compiled and evaluated over in-memory rows
/// here, and the compare-and-set behaviour the worker relies on is exercised through
/// FakeLegajoDocumentRepository in LegajoDocumentProcessorTests (two workers, one model call).
/// Everything tracked (the IsCurrent swap, consent) and every read runs for real below.
/// </para>
/// </summary>
public sealed class LegajoDocumentRepositoryTests
{
    private static FinanciaDbContext NewContext() => new(
        new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"legajo-{Guid.NewGuid()}")
            .Options);

    private static LegajoDocumentRepository Repo(FinanciaDbContext db)
        => new(db, NullLogger<LegajoDocumentRepository>.Instance);

    private static PersonLegajo Legajo(string tenant = LegajoTestData.Tenant) => new()
    {
        PersonLegajoId = Guid.NewGuid(),
        TenantId       = tenant,
        CUIT           = LegajoTestData.LegajoCuit,
        FullName       = LegajoTestData.LegajoFullName,
        FiscalRegime   = FiscalRegimes.Monotributo,
        CreatedAt      = DateTime.UtcNow,
        UpdatedAt      = DateTime.UtcNow
    };

    private static Document Doc(PersonLegajo legajo, string? status = LegajoStatuses.Uploaded, string hash = "h1",
                                bool deleted = false, string? tenant = null) => new()
    {
        DocumentId       = Guid.NewGuid(),
        TenantId         = tenant ?? legajo.TenantId,
        EntityId         = Guid.NewGuid(),
        PersonLegajoId   = legajo.PersonLegajoId,
        OriginalFileName = "documento.pdf",
        StoredFileName   = "x.pdf",
        StoragePath      = "p",
        MimeType         = "application/pdf",
        FileHashSHA256   = hash,
        DocumentType     = DocumentTypes.Ganancias,
        LegajoStatus     = status,
        IsDeleted        = deleted,
        UploadedAt       = DateTime.UtcNow,
        RetentionExpiresAt = DateTime.UtcNow.AddYears(7)
    };

    private static LegajoExtraction Run(Document document, bool current = true, string kind = LegajoExtractionKinds.Classification) => new()
    {
        ExtractionId   = Guid.NewGuid(),
        TenantId       = document.TenantId,
        PersonLegajoId = document.PersonLegajoId!.Value,
        DocumentId     = document.DocumentId,
        Kind           = kind,
        DocumentType   = DocumentTypes.Ganancias,
        SchemaVersion  = "1.0",
        PromptVersion  = "1.0",
        Model          = "claude-haiku-4-5",
        ReadMode       = LegajoReadModes.Document,
        Status         = LegajoExtractionStatuses.Succeeded,
        ResultJson     = "{}",
        InputHash      = "a",
        OutputHash     = "b",
        IsCurrent      = current,
        CreatedAt      = DateTime.UtcNow
    };

    // ── The claim predicate ─────────────────────────────────────────────────────

    [Fact]
    public void InStatus_MatchesOnlyThisDocument_ThisTenant_InTheFromStatus_NotDeleted()
    {
        var legajo = Legajo();
        var target = Doc(legajo, LegajoStatuses.Queued);
        var rows = new List<Document>
        {
            target,
            Doc(legajo, LegajoStatuses.Classifying),                 // already claimed
            Doc(legajo, LegajoStatuses.Queued, deleted: true),       // deleted
        };
        var sameIdOtherTenant = Doc(legajo, LegajoStatuses.Queued, tenant: LegajoTestData.OtherTenant);
        sameIdOtherTenant.DocumentId = target.DocumentId;
        rows.Add(sameIdOtherTenant);

        var claim = LegajoDocumentRepository.InStatus(target.DocumentId, LegajoTestData.Tenant, LegajoStatuses.Queued).Compile();

        Assert.Same(target, Assert.Single(rows.Where(claim).ToList()));

        // Once claimed, the same predicate no longer matches: a second claim finds nothing.
        target.LegajoStatus = LegajoStatuses.Classifying;
        Assert.DoesNotContain(rows, d => claim(d));
    }

    // ── IsCurrent swap ──────────────────────────────────────────────────────────

    [Fact]
    public async Task InsertRunAsync_SwapsTheCurrentRunOfTheSameDocumentAndKindOnly()
    {
        await using var db = NewContext();
        var legajo = Legajo();
        var document = Doc(legajo);
        var other = Doc(legajo);
        var first = Run(document);
        var otherKind = Run(document, kind: LegajoExtractionKinds.Extraction);
        var otherDocument = Run(other);
        db.LegajoExtractions.AddRange(first, otherKind, otherDocument);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var second = Run(document);
        await Repo(db).InsertRunAsync(second, CancellationToken.None);

        var stored = await db.LegajoExtractions.AsNoTracking().ToListAsync();
        Assert.False(stored.Single(r => r.ExtractionId == first.ExtractionId).IsCurrent);
        Assert.True(stored.Single(r => r.ExtractionId == second.ExtractionId).IsCurrent);
        Assert.True(stored.Single(r => r.ExtractionId == otherKind.ExtractionId).IsCurrent);
        Assert.True(stored.Single(r => r.ExtractionId == otherDocument.ExtractionId).IsCurrent);
    }

    [Fact]
    public async Task InsertRunAsync_ANonCurrentRun_LeavesTheCurrentOneAlone()
    {
        await using var db = NewContext();
        var document = Doc(Legajo());
        var current = Run(document);
        db.LegajoExtractions.Add(current);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Repo(db).InsertRunAsync(Run(document, current: false), CancellationToken.None);

        Assert.True((await db.LegajoExtractions.AsNoTracking().SingleAsync(r => r.ExtractionId == current.ExtractionId)).IsCurrent);
    }

    [Fact]
    public async Task GetCurrentClassificationsAsync_TenantScoped_CurrentOnly()
    {
        await using var db = NewContext();
        var document = Doc(Legajo());
        var old = Run(document, current: false);
        var current = Run(document);
        var foreign = Run(document);
        foreign.TenantId = LegajoTestData.OtherTenant;
        db.LegajoExtractions.AddRange(old, current, foreign);
        await db.SaveChangesAsync();

        var runs = await Repo(db).GetCurrentClassificationsAsync([document.DocumentId], LegajoTestData.Tenant, CancellationToken.None);

        Assert.Equal(current.ExtractionId, runs[document.DocumentId].ExtractionId);
    }

    // ── Consent ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetAndClearConsent_WriteTheThreeColumns_TenantScoped()
    {
        await using var db = NewContext();
        var legajo = Legajo();
        db.PersonLegajos.Add(legajo);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var repo = Repo(db);
        var authorization = Authorization(legajo);
        db.Documents.Add(authorization);
        await db.SaveChangesAsync();
        var documentId = authorization.DocumentId;
        var userId = Guid.NewGuid();

        Assert.False(await repo.SetConsentAsync(legajo.PersonLegajoId, LegajoTestData.OtherTenant, documentId, userId, DateTime.UtcNow, CancellationToken.None));
        Assert.True(await repo.SetConsentAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, documentId, userId, DateTime.UtcNow, CancellationToken.None));

        var stored = await db.PersonLegajos.AsNoTracking().SingleAsync();
        Assert.NotNull(stored.ConsentRecordedAt);
        Assert.Equal(documentId, stored.ConsentDocumentId);
        Assert.Equal(userId, stored.ConsentRecordedBy);
        Assert.True(await repo.HasConsentAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None));
        Assert.False(await repo.HasConsentAsync(legajo.PersonLegajoId, LegajoTestData.OtherTenant, CancellationToken.None));

        Assert.True(await repo.ClearConsentAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, DateTime.UtcNow, CancellationToken.None));
        stored = await db.PersonLegajos.AsNoTracking().SingleAsync();
        Assert.Null(stored.ConsentRecordedAt);
        Assert.Null(stored.ConsentDocumentId);
        Assert.Null(stored.ConsentRecordedBy);
        Assert.False(await repo.HasConsentAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None));
    }

    // ── Active consent: recorded AND its authorization document live ────────────

    private static Document Authorization(PersonLegajo legajo, bool deleted = false)
    {
        var document = Doc(legajo, LegajoStatuses.Ready, hash: "auth-" + Guid.NewGuid(), deleted: deleted);
        document.DocumentType = DocumentTypes.AutorizacionDatos;
        return document;
    }

    private static async Task<(PersonLegajo Legajo, Document Authorization)> SeedConsent(
        FinanciaDbContext db, bool deletedAuthorization = false, string? authorizationType = null, bool otherLegajo = false)
    {
        var legajo = Legajo();
        var owner = otherLegajo ? Legajo() : legajo;
        var authorization = Authorization(owner, deletedAuthorization);
        if (authorizationType is not null)
            authorization.DocumentType = authorizationType;
        legajo.ConsentRecordedAt = DateTime.UtcNow;
        legajo.ConsentDocumentId = authorization.DocumentId;
        db.PersonLegajos.Add(legajo);
        db.Documents.Add(authorization);
        await db.SaveChangesAsync();
        return (legajo, authorization);
    }

    [Fact]
    public async Task HasConsent_RecordedAgainstALiveAuthorization_IsActive()
    {
        await using var db = NewContext();
        var (legajo, _) = await SeedConsent(db);
        var repo = Repo(db);

        Assert.True(await repo.HasConsentAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None));
        Assert.True((await repo.GetLegajoSummaryAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None))!.HasActiveConsent);
    }

    [Fact]
    public async Task HasConsent_AuthorizationDeleted_IsNotActive()
    {
        await using var db = NewContext();
        var (legajo, _) = await SeedConsent(db, deletedAuthorization: true);
        var repo = Repo(db);

        Assert.False(await repo.HasConsentAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None));
        var summary = await repo.GetLegajoSummaryAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None);
        Assert.NotNull(summary!.ConsentRecordedAt);   // still recorded...
        Assert.False(summary.HasActiveConsent);        // ...but not active
    }

    [Theory]
    [InlineData(false, DocumentTypes.Ganancias)]   // points at a document that is not an authorization
    [InlineData(true, null)]                        // points at another legajo's authorization
    public async Task HasConsent_PointingAtTheWrongDocument_IsNotActive(bool otherLegajo, string? type)
    {
        await using var db = NewContext();
        var (legajo, _) = await SeedConsent(db, authorizationType: type, otherLegajo: otherLegajo);

        Assert.False(await Repo(db).HasConsentAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None));
    }

    [Fact]
    public async Task IsConsentDocumentInUse_OnlyForTheRecordedConsentDocument()
    {
        await using var db = NewContext();
        var (legajo, authorization) = await SeedConsent(db);
        var repo = Repo(db);

        Assert.True(await repo.IsConsentDocumentInUseAsync(authorization.DocumentId, LegajoTestData.Tenant, CancellationToken.None));
        Assert.False(await repo.IsConsentDocumentInUseAsync(authorization.DocumentId, LegajoTestData.OtherTenant, CancellationToken.None));
        Assert.False(await repo.IsConsentDocumentInUseAsync(Guid.NewGuid(), LegajoTestData.Tenant, CancellationToken.None));

        await repo.ClearConsentAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, DateTime.UtcNow, CancellationToken.None);
        Assert.False(await repo.IsConsentDocumentInUseAsync(authorization.DocumentId, LegajoTestData.Tenant, CancellationToken.None));
    }

    [Fact]
    public async Task GetEntityLegajoConsent_CompanyHasNoLegajo_PersonaHumanaReportsItsConsent()
    {
        await using var db = NewContext();
        var (legajo, authorization) = await SeedConsent(db);
        var company = new Entity { EntityId = Guid.NewGuid(), TenantId = LegajoTestData.Tenant, CUIT = "30-11111111-1", BusinessName = "EMPRESA SINTETICA SA" };
        var person = new Entity
        {
            EntityId = Guid.NewGuid(), TenantId = LegajoTestData.Tenant, CUIT = LegajoTestData.LegajoCuit,
            BusinessName = LegajoTestData.LegajoFullName, PersonType = PersonTypes.Humana, PersonLegajoId = legajo.PersonLegajoId
        };
        db.Entities.AddRange(company, person);
        await db.SaveChangesAsync();
        var repo = Repo(db);

        Assert.Equal((null, false), await repo.GetEntityLegajoConsentAsync(company.EntityId, LegajoTestData.Tenant, CancellationToken.None));
        Assert.Equal((legajo.PersonLegajoId, true), await repo.GetEntityLegajoConsentAsync(person.EntityId, LegajoTestData.Tenant, CancellationToken.None));

        db.ChangeTracker.Clear();
        var stored = await db.Documents.SingleAsync(d => d.DocumentId == authorization.DocumentId);
        stored.IsDeleted = true;
        db.ChangeTracker.DetectChanges();
        await db.SaveChangesAsync();

        Assert.Equal((legajo.PersonLegajoId, false), await repo.GetEntityLegajoConsentAsync(person.EntityId, LegajoTestData.Tenant, CancellationToken.None));
    }

    // ── Insert ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateLegajoDocumentAsync_Inserted_ReturnsNull()
    {
        await using var db = NewContext();
        var document = Doc(Legajo());

        Assert.Null(await Repo(db).CreateLegajoDocumentAsync(document, CancellationToken.None));
        Assert.True(await db.Documents.AnyAsync(d => d.DocumentId == document.DocumentId));
    }

    [Fact]
    public void UniqueViolation_RecognisesTheLegajoHashIndexByName()
    {
        // InMemory enforces no unique index; the mapping is by NAME in the provider message (#237).
        var violation = new DbUpdateException("x", new Exception(
            "Cannot insert duplicate key row in object 'dbo.Documents' with unique index 'UX_Documents_Legajo_Hash'."));
        var other = new DbUpdateException("x", new Exception("... unique index 'UQ_Entities_TenantId_CUIT' ..."));

        Assert.True(UniqueViolation.Is(violation, UniqueViolation.DocumentLegajoHash));
        Assert.False(UniqueViolation.Is(other, UniqueViolation.DocumentLegajoHash));
    }

    // ── Reads ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetLegajoSummaryAsync_TenantScoped_WithItsEntity()
    {
        await using var db = NewContext();
        var legajo = Legajo();
        var entity = new Entity
        {
            EntityId = Guid.NewGuid(), TenantId = LegajoTestData.Tenant, CUIT = LegajoTestData.LegajoCuit,
            BusinessName = LegajoTestData.LegajoFullName, PersonType = PersonTypes.Humana,
            PersonLegajoId = legajo.PersonLegajoId
        };
        db.PersonLegajos.Add(legajo);
        db.Entities.Add(entity);
        await db.SaveChangesAsync();

        var summary = await Repo(db).GetLegajoSummaryAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None);

        Assert.NotNull(summary);
        Assert.Equal(entity.EntityId, summary!.EntityId);
        Assert.Equal(FiscalRegimes.Monotributo, summary.FiscalRegime);
        Assert.Null(await Repo(db).GetLegajoSummaryAsync(legajo.PersonLegajoId, LegajoTestData.OtherTenant, CancellationToken.None));
    }

    [Fact]
    public async Task FindActiveByHashAsync_IgnoresDeletedAndOtherLegajos()
    {
        await using var db = NewContext();
        var legajo = Legajo();
        var otherLegajo = Legajo();
        var deleted = Doc(legajo, hash: "same", deleted: true);
        var elsewhere = Doc(otherLegajo, hash: "same");
        db.Documents.AddRange(deleted, elsewhere);
        await db.SaveChangesAsync();
        var repo = Repo(db);

        Assert.Null(await repo.FindActiveByHashAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, "same", CancellationToken.None));

        var live = Doc(legajo, hash: "same");
        db.Documents.Add(live);
        await db.SaveChangesAsync();

        Assert.Equal(live.DocumentId, await repo.FindActiveByHashAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, "same", CancellationToken.None));
        Assert.Null(await repo.FindActiveByHashAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, "", CancellationToken.None));
    }

    [Fact]
    public async Task GetWorkAsync_JoinsTheLegajo_TenantScoped()
    {
        await using var db = NewContext();
        var (legajo, _) = await SeedConsent(db);
        var document = Doc(legajo, LegajoStatuses.Queued);
        db.Documents.Add(document);
        await db.SaveChangesAsync();

        var work = await Repo(db).GetWorkAsync(document.DocumentId, LegajoTestData.Tenant, CancellationToken.None);

        Assert.NotNull(work);
        Assert.Equal(legajo.PersonLegajoId, work!.PersonLegajoId);
        Assert.Equal(LegajoTestData.LegajoCuit, work.LegajoCuit);
        Assert.True(work.HasActiveConsent);
        Assert.Null(await Repo(db).GetWorkAsync(document.DocumentId, LegajoTestData.OtherTenant, CancellationToken.None));
    }

    private static async Task<(Document StaleQueued, Document FreshQueued, Document StaleForeign, Document FreshClassifying)>
        SeedRecoverable(FinanciaDbContext db)
    {
        var legajo = Legajo();
        var foreignLegajo = Legajo(LegajoTestData.OtherTenant);
        var old = DateTime.UtcNow.AddHours(-1);

        var staleQueued = Doc(legajo, LegajoStatuses.Queued);
        staleQueued.LegajoStatusUpdatedAt = old;
        var freshQueued = Doc(legajo, LegajoStatuses.Queued);
        freshQueued.LegajoStatusUpdatedAt = DateTime.UtcNow;
        var staleForeign = Doc(foreignLegajo, LegajoStatuses.Classifying);
        staleForeign.LegajoStatusUpdatedAt = old;
        var freshClassifying = Doc(legajo, LegajoStatuses.Classifying);
        freshClassifying.LegajoStatusUpdatedAt = DateTime.UtcNow;
        var done = Doc(legajo, LegajoStatuses.Ready);
        done.LegajoStatusUpdatedAt = old;
        var deleted = Doc(legajo, LegajoStatuses.Queued, deleted: true);
        deleted.LegajoStatusUpdatedAt = old;
        db.Documents.AddRange(staleQueued, freshQueued, staleForeign, freshClassifying, done, deleted);
        await db.SaveChangesAsync();

        return (staleQueued, freshQueued, staleForeign, freshClassifying);
    }

    [Fact]
    public async Task GetRecoverable_Startup_EveryQueuedRowAndStaleClassifying_AcrossTenants()
    {
        await using var db = NewContext();
        var (staleQueued, freshQueued, staleForeign, _) = await SeedRecoverable(db);

        var rows = await Repo(db).GetRecoverableAsync(DateTime.UtcNow.AddMinutes(-10), null, LegajoExtractionPrompts.SupportedTypes, CancellationToken.None);

        Assert.Equal(
            new[] { staleQueued.DocumentId, freshQueued.DocumentId, staleForeign.DocumentId }.Order(),
            rows.Select(r => r.DocumentId).Order());
        Assert.Contains(rows, r => r.TenantId == LegajoTestData.OtherTenant && r.LegajoStatus == LegajoStatuses.Classifying);
    }

    [Fact]
    public async Task GetRecoverable_Sweep_OnlyRowsPastTheWindow()
    {
        await using var db = NewContext();
        var (staleQueued, _, staleForeign, _) = await SeedRecoverable(db);
        var cutoff = DateTime.UtcNow.AddMinutes(-10);

        var rows = await Repo(db).GetRecoverableAsync(cutoff, cutoff, LegajoExtractionPrompts.SupportedTypes, CancellationToken.None);

        Assert.Equal(
            new[] { staleQueued.DocumentId, staleForeign.DocumentId }.Order(),
            rows.Select(r => r.DocumentId).Order());
    }

    // ── Phase 2b ────────────────────────────────────────────────────────────────

    /// <summary>A legajo with ACTIVE consent: recorded against a live AutorizacionDatos document of its own.</summary>
    private static async Task<PersonLegajo> ConsentedLegajo(FinanciaDbContext db)
    {
        var legajo = Legajo();
        var authorization = Doc(legajo, LegajoStatuses.Ready, hash: "auth");
        authorization.DocumentType = DocumentTypes.AutorizacionDatos;
        legajo.ConsentRecordedAt = DateTime.UtcNow.AddDays(-1);
        legajo.ConsentDocumentId = authorization.DocumentId;
        db.PersonLegajos.Add(legajo);
        db.Documents.Add(authorization);
        await db.SaveChangesAsync();
        return legajo;
    }

    [Fact]
    public async Task GetRecoverable_ReadyForExtraction_OnlyExtractableTypesWithConsent_AndStaleExtracting()
    {
        await using var db = NewContext();
        var consented = await ConsentedLegajo(db);
        var without   = Legajo();
        db.PersonLegajos.Add(without);
        var old = DateTime.UtcNow.AddHours(-1);

        Document Make(PersonLegajo legajo, string status, string type, DateTime updatedAt)
        {
            var d = Doc(legajo, status, hash: Guid.NewGuid().ToString("N"));
            d.DocumentType = type;
            d.LegajoStatusUpdatedAt = updatedAt;
            db.Documents.Add(d);
            return d;
        }

        var bp            = Make(consented, LegajoStatuses.ReadyForExtraction, DocumentTypes.BienesPersonales, DateTime.UtcNow);
        var reibp         = Make(consented, LegajoStatuses.ReadyForExtraction, DocumentTypes.Reibp, old);   // no extractor
        var noConsent     = Make(without, LegajoStatuses.ReadyForExtraction, DocumentTypes.ManifestacionBienes, old);
        var staleExtract  = Make(without, LegajoStatuses.Extracting, DocumentTypes.BienesPersonales, old);
        var freshExtract  = Make(consented, LegajoStatuses.Extracting, DocumentTypes.BienesPersonales, DateTime.UtcNow);
        await db.SaveChangesAsync();

        var cutoff  = DateTime.UtcNow.AddMinutes(-10);
        var startup = await Repo(db).GetRecoverableAsync(cutoff, null, LegajoExtractionPrompts.SupportedTypes, CancellationToken.None);
        var sweep   = await Repo(db).GetRecoverableAsync(cutoff, cutoff, LegajoExtractionPrompts.SupportedTypes, CancellationToken.None);

        Assert.Equal(new[] { bp.DocumentId, staleExtract.DocumentId }.Order(), startup.Select(r => r.DocumentId).Order());
        Assert.Equal([staleExtract.DocumentId], sweep.Select(r => r.DocumentId));
        Assert.DoesNotContain(startup, r => r.DocumentId == reibp.DocumentId || r.DocumentId == noConsent.DocumentId
                                         || r.DocumentId == freshExtract.DocumentId);
    }

    [Fact]
    public async Task GetRecoverable_PicksUpThePhase3aTypesWaitingInReadyForExtraction_OnlyWithConsent()
    {
        // The first startup after the Phase 3a publish: documents classified long ago, waiting for an extractor.
        await using var db = NewContext();
        var consented = await ConsentedLegajo(db);
        var without   = Legajo();
        db.PersonLegajos.Add(without);

        var phase3a = new[] { DocumentTypes.Ganancias, DocumentTypes.DdjjIva, DocumentTypes.DdjjIibb,
                              DocumentTypes.CertificacionIngresos, DocumentTypes.Monotributo };
        var waiting = new List<Guid>();
        var blocked = new List<Guid>();
        foreach (var type in phase3a)
        {
            foreach (var (legajo, list) in new[] { (consented, waiting), (without, blocked) })
            {
                var d = Doc(legajo, LegajoStatuses.ReadyForExtraction, hash: Guid.NewGuid().ToString("N"));
                d.DocumentType = type;
                d.LegajoStatusUpdatedAt = DateTime.UtcNow.AddDays(-3);
                db.Documents.Add(d);
                list.Add(d.DocumentId);
            }
        }
        await db.SaveChangesAsync();

        var startup = await Repo(db).GetRecoverableAsync(DateTime.UtcNow.AddMinutes(-10), null,
                                                         LegajoExtractionPrompts.SupportedTypes, CancellationToken.None);

        Assert.Equal(waiting.Order(), startup.Select(r => r.DocumentId).Order());
        Assert.DoesNotContain(startup, r => blocked.Contains(r.DocumentId));
    }

    [Fact]
    public async Task CurrentExtraction_Reads_AreTenantScoped_AndIgnoreClassifications()
    {
        await using var db = NewContext();
        var legajo = Legajo();
        var document = Doc(legajo);
        var classification = Run(document);
        var extraction = Run(document, kind: LegajoExtractionKinds.Extraction);
        var superseded = Run(document, current: false, kind: LegajoExtractionKinds.Extraction);
        db.LegajoExtractions.AddRange(classification, extraction, superseded);
        await db.SaveChangesAsync();
        var repo = Repo(db);

        Assert.Equal(extraction.ExtractionId,
            (await repo.GetCurrentExtractionAsync(document.DocumentId, LegajoTestData.Tenant, CancellationToken.None))!.ExtractionId);
        Assert.Null(await repo.GetCurrentExtractionAsync(document.DocumentId, LegajoTestData.OtherTenant, CancellationToken.None));
        Assert.True(await repo.HasCurrentExtractionAsync(document.DocumentId, LegajoTestData.Tenant, CancellationToken.None));

        var map = await repo.GetCurrentExtractionsAsync([document.DocumentId], LegajoTestData.Tenant, CancellationToken.None);
        Assert.Equal(extraction.ExtractionId, map[document.DocumentId].ExtractionId);
    }

    [Fact]
    public async Task Corrections_ReadOldestFirst_TenantScoped()
    {
        await using var db = NewContext();
        var extractionId = Guid.NewGuid();
        var repo = Repo(db);

        LegajoCorrection Correction(DateTime at, string tenant = LegajoTestData.Tenant) => new()
        {
            CorrectionId = Guid.NewGuid(), TenantId = tenant, ExtractionId = extractionId, FieldPath = "/totals/totalBienes",
            OriginalValueJson = "1", CorrectedValueJson = "2", Reason = "Leido del original", CorrectedBy = Guid.NewGuid(),
            CorrectedAt = at
        };

        // Seeded directly: the write path (InsertCorrectionAndReopenAsync) runs a transaction and an
        // ExecuteUpdate, neither of which the InMemory provider supports; FakeLegajoDocumentRepository
        // models it for the controller tests.
        var later   = Correction(DateTime.UtcNow);
        var earlier = Correction(DateTime.UtcNow.AddMinutes(-5));
        db.LegajoCorrections.AddRange(later, earlier, Correction(DateTime.UtcNow, LegajoTestData.OtherTenant));
        await db.SaveChangesAsync();

        var read = await repo.GetCorrectionsAsync([extractionId], LegajoTestData.Tenant, CancellationToken.None);

        Assert.Equal([earlier.CorrectionId, later.CorrectionId], read.Select(c => c.CorrectionId));
    }

    [Fact]
    public async Task GetLegajoDocumentsAsync_NonDeleted_TenantScoped()
    {
        await using var db = NewContext();
        var legajo = Legajo();
        var live = Doc(legajo);
        db.Documents.AddRange(live, Doc(legajo, deleted: true), Doc(legajo, tenant: LegajoTestData.OtherTenant));
        await db.SaveChangesAsync();

        var documents = await Repo(db).GetLegajoDocumentsAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, CancellationToken.None);

        Assert.Equal(live.DocumentId, Assert.Single(documents).DocumentId);
    }
}
