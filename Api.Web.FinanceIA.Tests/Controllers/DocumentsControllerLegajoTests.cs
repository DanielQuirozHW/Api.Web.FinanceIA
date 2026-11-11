// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-28

using System.Reflection;
using System.Security.Claims;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.Concurrency;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Tests.Legajo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// <see cref="DocumentsController"/> in M10 Phase 2a: the legajo branch of the upload (validations,
/// dedupe 409, page quality, Uploaded / Queued / Ready), the legacy upload unchanged, and the two new
/// legajo endpoints — reprocess and classification confirm. Synthetic data; hand-written fakes.
/// </summary>
public sealed class DocumentsControllerLegajoTests
{
    private sealed class Harness
    {
        public FakeLegajoDocumentRepository Legajos { get; } = new();
        public ListDocumentRepository Documents { get; }
        public StubEntityRepository Entities { get; } = new();
        public InMemoryStorage Storage { get; } = new();
        public StubUserRepository Users { get; } = new();
        public RecordingAuditService Audit { get; } = new();
        public RecordingPipelineQueue Queue { get; } = new();
        public RecordingReconciliationService Reconciliation { get; } = new();
        public FakeFiadorRepository Fiadores { get; } = new();
        public DocumentsController Sut { get; }

        public Harness(int maxAttempts = 2)
        {
            Documents = new ListDocumentRepository(Legajos.Documents);
            Sut = new DocumentsController(
                new StubTenantContextAccessor(),
                Documents,
                Entities,
                Storage,
                Users,
                Audit,
                Legajos,
                Fiadores,
                Queue,
                Reconciliation,
                Options.Create(new BillingOptions { MaxExtractionAttemptsPerDocument = maxAttempts }),
                new RecordingRunListRepository(),
                new KeyedAsyncLock(),
                NullLogger<DocumentsController>.Instance)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(
                            [new Claim("uid", LegajoTestData.FirebaseUid)], "Test"))
                    }
                }
            };
        }

        /// <summary>A persona humana entity linked to a fresh legajo.</summary>
        public (Entity Entity, PersonLegajo Legajo) PersonaHumana(bool consent)
        {
            var entityId = Guid.NewGuid();
            var legajo = Legajos.AddLegajo(consent: consent, entityId: entityId);
            var entity = new Entity
            {
                EntityId       = entityId,
                TenantId       = LegajoTestData.Tenant,
                PersonType     = PersonTypes.Humana,
                PersonLegajoId = legajo.PersonLegajoId
            };
            Entities.Entities.Add(entity);
            return (entity, legajo);
        }

        /// <summary>
        /// A company (no legajo of its own) with one fiador persona humana whose legajo was linked by
        /// POST …/fiadores/{fiadorId}/legajo — linked to no entity, as a fiador's legajo is.
        /// </summary>
        public (Entity Pyme, PersonLegajo FiadorLegajo, Fiador Fiador) PymeWithFiador(bool consent, string status = "Active")
        {
            var pyme = new Entity
            {
                EntityId   = Guid.NewGuid(),
                TenantId   = LegajoTestData.Tenant,
                PersonType = PersonTypes.Juridica
            };
            Entities.Entities.Add(pyme);

            var legajo = Legajos.AddLegajo(consent: consent);
            var fiador = new Fiador
            {
                FiadorId       = Guid.NewGuid(),
                TenantId       = LegajoTestData.Tenant,
                EntityId       = pyme.EntityId,
                CUIT           = LegajoTestData.LegajoCuit,
                Origen         = "Manual",
                Status         = status,
                PersonLegajoId = legajo.PersonLegajoId
            };
            Fiadores.Fiadores.Add(fiador);
            return (pyme, legajo, fiador);
        }

        public Task<IActionResult> UploadAsync(byte[] pdf, Guid entityId, string type, Guid? legajoId)
            => Sut.UploadAsync(PdfFile(pdf), entityId, type, 2025, legajoId, CancellationToken.None);
    }

    private static IFormFile PdfFile(byte[] bytes, string name = "documento.pdf")
        => new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name)
        {
            Headers     = new HeaderDictionary(),
            ContentType = "application/pdf"
        };

    private static byte[] Pdf(string text = LegajoPdfFactory.SampleText) => LegajoPdfFactory.NativePdf(text);

    private static DocumentResponse Created(IActionResult result)
        => Assert.IsType<DocumentResponse>(Assert.IsType<CreatedAtActionResult>(result).Value);

    private static ProblemDetails Problem(IActionResult result, int status)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(status, objectResult.StatusCode);
        return Assert.IsAssignableFrom<ProblemDetails>(objectResult.Value);
    }

    // ── Legajo upload: validations ──────────────────────────────────────────────

    [Fact]
    public async Task LegajoUpload_LegacyType_IsRejected()
    {
        var h = new Harness();
        var (entity, legajo) = h.PersonaHumana(consent: true);
        var before = h.Legajos.Documents.Count;

        Problem(await h.UploadAsync(Pdf(), entity.EntityId, DocumentTypes.Balance, legajo.PersonLegajoId), 400);
        Assert.Equal(before, h.Legajos.Documents.Count);
    }

    [Fact]
    public async Task LegajoUpload_UnknownEntity_404()
    {
        var h = new Harness();
        var (_, legajo) = h.PersonaHumana(consent: true);

        Problem(await h.UploadAsync(Pdf(), Guid.NewGuid(), DocumentTypes.Ganancias, legajo.PersonLegajoId), 404);
    }

    [Fact]
    public async Task LegajoUpload_UnknownLegajo_404()
    {
        var h = new Harness();
        var (entity, _) = h.PersonaHumana(consent: true);

        Problem(await h.UploadAsync(Pdf(), entity.EntityId, DocumentTypes.Ganancias, Guid.NewGuid()), 404);
    }

    [Fact]
    public async Task LegajoUpload_LegajoOfAnotherTenant_404()
    {
        var h = new Harness();
        var (entity, _) = h.PersonaHumana(consent: true);
        var foreign = h.Legajos.AddLegajo(tenantId: LegajoTestData.OtherTenant, consent: true);

        Problem(await h.UploadAsync(Pdf(), entity.EntityId, DocumentTypes.Ganancias, foreign.PersonLegajoId), 404);
    }

    [Fact]
    public async Task LegajoUpload_LegajoNotLinkedToTheEntity_400()
    {
        var h = new Harness();
        var (entity, _) = h.PersonaHumana(consent: true);
        var (_, otherLegajo) = h.PersonaHumana(consent: true);
        var before = h.Legajos.Documents.Count;

        var problem = Problem(await h.UploadAsync(Pdf(), entity.EntityId, DocumentTypes.Ganancias, otherLegajo.PersonLegajoId), 400);
        Assert.Equal("El legajo indicado no corresponde a la entidad.", problem.Detail);
        Assert.Equal(before, h.Legajos.Documents.Count);
    }

    // ── Legajo upload: a fiador's legajo through the PyME (M10 Phase 5) ─────────

    [Fact]
    public async Task LegajoUpload_ActiveFiadorsLegajo_ThroughThePyme_IsAccepted_WithThePymesEntityId()
    {
        var h = new Harness();
        var (pyme, fiadorLegajo, _) = h.PymeWithFiador(consent: true);

        var response = Created(await h.UploadAsync(Pdf(), pyme.EntityId, DocumentTypes.Ganancias, fiadorLegajo.PersonLegajoId));

        // Documents.EntityId is NOT NULL: the document hangs off the PyME and carries the fiador's legajo.
        var stored = h.Legajos.Get(response.DocumentId);
        Assert.Equal(pyme.EntityId, stored.EntityId);
        Assert.Equal(fiadorLegajo.PersonLegajoId, stored.PersonLegajoId);
        Assert.Equal(fiadorLegajo.PersonLegajoId, response.PersonLegajoId);

        // The fiador's OWN consent queues it, exactly as for the solicitante.
        Assert.Equal(LegajoStatuses.Queued, stored.LegajoStatus);
        Assert.Single(h.Queue.Items);
    }

    [Fact]
    public async Task LegajoUpload_FiadorsLegajo_WithoutItsOwnConsent_StaysUploaded_NotQueued()
    {
        var h = new Harness();
        var (pyme, fiadorLegajo, _) = h.PymeWithFiador(consent: false);

        var response = Created(await h.UploadAsync(Pdf(), pyme.EntityId, DocumentTypes.Ganancias, fiadorLegajo.PersonLegajoId));

        Assert.Equal(LegajoStatuses.Uploaded, response.LegajoStatus);
        Assert.Empty(h.Queue.Items);
    }

    [Fact]
    public async Task LegajoUpload_RemovedFiadorsLegajo_IsStillRefused_400()
    {
        var h = new Harness();
        var (pyme, fiadorLegajo, _) = h.PymeWithFiador(consent: true, status: "Removed");
        var before = h.Legajos.Documents.Count;

        var problem = Problem(await h.UploadAsync(Pdf(), pyme.EntityId, DocumentTypes.Ganancias, fiadorLegajo.PersonLegajoId), 400);

        Assert.Equal("El legajo indicado no corresponde a la entidad.", problem.Detail);
        Assert.Equal(before, h.Legajos.Documents.Count);
    }

    [Fact]
    public async Task LegajoUpload_FiadorsLegajo_ThroughAnotherPyme_IsRefused_400()
    {
        var h = new Harness();
        var (_, fiadorLegajo, _) = h.PymeWithFiador(consent: true);
        var (otherEntity, _) = h.PersonaHumana(consent: true);
        var before = h.Legajos.Documents.Count;

        var problem = Problem(await h.UploadAsync(Pdf(), otherEntity.EntityId, DocumentTypes.Ganancias, fiadorLegajo.PersonLegajoId), 400);

        Assert.Equal("El legajo indicado no corresponde a la entidad.", problem.Detail);
        Assert.Equal(before, h.Legajos.Documents.Count);
    }

    [Fact]
    public async Task LegajoUpload_SameFileTwiceInTheLegajo_409_WithTheExistingDocumentId()
    {
        var h = new Harness();
        var (entity, legajo) = h.PersonaHumana(consent: false);
        var pdf = Pdf();

        var first = Created(await h.UploadAsync(pdf, entity.EntityId, DocumentTypes.Ganancias, legajo.PersonLegajoId));
        var conflict = Problem(await h.UploadAsync(pdf, entity.EntityId, DocumentTypes.Ganancias, legajo.PersonLegajoId), 409);

        Assert.Equal(first.DocumentId, conflict.Extensions["documentId"]);
        Assert.Equal("duplicate_document", conflict.Extensions["errorCode"]);
        Assert.Single(h.Legajos.Documents);
    }

    [Fact]
    public async Task LegajoUpload_SameFileAfterTheFirstWasDeleted_IsAccepted()
    {
        var h = new Harness();
        var (entity, legajo) = h.PersonaHumana(consent: false);
        var pdf = Pdf();

        var first = Created(await h.UploadAsync(pdf, entity.EntityId, DocumentTypes.Ganancias, legajo.PersonLegajoId));
        h.Legajos.Get(first.DocumentId).IsDeleted = true;

        Created(await h.UploadAsync(pdf, entity.EntityId, DocumentTypes.Ganancias, legajo.PersonLegajoId));
    }

    // ── Legajo upload: statuses and page quality ────────────────────────────────

    [Fact]
    public async Task LegajoUpload_WithoutConsent_Uploaded_NotQueued_PagesMeasured()
    {
        var h = new Harness();
        var (entity, legajo) = h.PersonaHumana(consent: false);

        var response = Created(await h.UploadAsync(Pdf(), entity.EntityId, "ganancias", legajo.PersonLegajoId));

        Assert.Equal(LegajoStatuses.Uploaded, response.LegajoStatus);
        Assert.Equal(legajo.PersonLegajoId, response.PersonLegajoId);
        Assert.Equal(DocumentTypes.Ganancias, response.DocumentType);   // canonical spelling
        Assert.Equal(1, response.PageCount);
        Assert.Equal(PdfTypes.Native, response.PdfType);
        Assert.Empty(h.Queue.Items);

        var stored = h.Legajos.Get(response.DocumentId);
        Assert.Contains("\"verdict\":\"Good\"", stored.PageQualityJson);
        Assert.NotNull(stored.LegajoStatusUpdatedAt);
        Assert.Contains(h.Audit.Entries, e => e.Action == "Document.Uploaded");
    }

    [Fact]
    public async Task LegajoUpload_WithConsent_Queued_AndEnqueued()
    {
        var h = new Harness();
        var (entity, legajo) = h.PersonaHumana(consent: true);

        var response = Created(await h.UploadAsync(Pdf(), entity.EntityId, DocumentTypes.Ganancias, legajo.PersonLegajoId));

        Assert.Equal(LegajoStatuses.Queued, response.LegajoStatus);
        Assert.Equal(response.DocumentId, Assert.Single(h.Queue.Items).DocumentId);
        Assert.Equal(LegajoStatuses.Queued, h.Legajos.Get(response.DocumentId).LegajoStatus);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LegajoUpload_Authorization_GoesStraightToReady_NeverQueued(bool consent)
    {
        var h = new Harness();
        var (entity, legajo) = h.PersonaHumana(consent: consent);

        var response = Created(await h.UploadAsync(Pdf(), entity.EntityId, DocumentTypes.AutorizacionDatos, legajo.PersonLegajoId));

        Assert.Equal(LegajoStatuses.Ready, response.LegajoStatus);
        Assert.Empty(h.Queue.Items);
    }

    [Fact]
    public async Task LegajoUpload_ScannedPdf_PdfTypeScanned()
    {
        var h = new Harness();
        var (entity, legajo) = h.PersonaHumana(consent: false);

        var response = Created(await h.UploadAsync(LegajoPdfFactory.BlankPdf(2), entity.EntityId, DocumentTypes.ManifestacionBienes, legajo.PersonLegajoId));

        Assert.Equal(PdfTypes.Scanned, response.PdfType);
        Assert.Equal(2, response.PageCount);
    }

    [Fact]
    public async Task LegajoUpload_LostDuplicateRace_409_WithTheWinnersId()
    {
        // The pre-check found nothing, but UX_Documents_Legajo_Hash rejected the insert: a double-click.
        var h = new Harness();
        var (entity, legajo) = h.PersonaHumana(consent: true);
        var winner = Guid.NewGuid();
        h.Legajos.RaceWinnerOnInsert = winner;

        var conflict = Problem(await h.UploadAsync(Pdf(), entity.EntityId, DocumentTypes.Ganancias, legajo.PersonLegajoId), 409);

        Assert.Equal("duplicate_document", conflict.Extensions["errorCode"]);
        Assert.Equal(winner, conflict.Extensions["documentId"]);
        Assert.Empty(h.Queue.Items);
    }

    [Fact]
    public async Task LegajoUpload_ConsentRecordedWhileTheUploadWasInFlight_IsQueued()
    {
        // The legajo had no consent when the upload started; a concurrent consent POST recorded it
        // before the insert finished. Consent is read AFTER the insert, so the document is queued.
        var h = new Harness();
        var (entity, legajo) = h.PersonaHumana(consent: false);
        h.Legajos.ConsentOverride = () => true;

        var response = Created(await h.UploadAsync(Pdf(), entity.EntityId, DocumentTypes.Ganancias, legajo.PersonLegajoId));

        Assert.Equal(LegajoStatuses.Queued, response.LegajoStatus);
        Assert.Single(h.Queue.Items);
    }

    [Fact]
    public async Task LegajoUpload_ConsentRecordedAgainstADeletedAuthorization_IsNotActive_NotQueued()
    {
        var h = new Harness();
        var (entity, legajo) = h.PersonaHumana(consent: true);
        h.Legajos.Get(legajo.ConsentDocumentId!.Value).IsDeleted = true;

        var response = Created(await h.UploadAsync(Pdf(), entity.EntityId, DocumentTypes.Ganancias, legajo.PersonLegajoId));

        Assert.Equal(LegajoStatuses.Uploaded, response.LegajoStatus);
        Assert.Empty(h.Queue.Items);
    }

    // ── Delete: the consent document ────────────────────────────────────────────

    [Fact]
    public async Task Delete_TheConsentDocumentOfAnActiveConsent_409_UntilRevoked()
    {
        var h = new Harness();
        var legajo = h.Legajos.AddLegajo(consent: true);
        var authorizationId = legajo.ConsentDocumentId!.Value;

        var conflict = Problem(await h.Sut.DeleteAsync(authorizationId, CancellationToken.None), 409);

        Assert.Equal("consent_document_in_use", conflict.Extensions["errorCode"]);
        Assert.Equal("Revocá la autorización antes de eliminar este documento.", conflict.Detail);
        Assert.False(h.Legajos.Get(authorizationId).IsDeleted);

        await h.Legajos.ClearConsentAsync(legajo.PersonLegajoId, LegajoTestData.Tenant, DateTime.UtcNow, CancellationToken.None);

        Assert.IsType<NoContentResult>(await h.Sut.DeleteAsync(authorizationId, CancellationToken.None));
        Assert.True(h.Legajos.Get(authorizationId).IsDeleted);
    }

    [Fact]
    public async Task Delete_AnyOtherLegajoDocument_StillWorks()
    {
        var h = new Harness();
        var legajo = h.Legajos.AddLegajo(consent: true);
        var ganancias = h.Legajos.AddDocument(legajo, DocumentTypes.Ganancias, LegajoStatuses.ReadyForExtraction);

        Assert.IsType<NoContentResult>(await h.Sut.DeleteAsync(ganancias.DocumentId, CancellationToken.None));
        Assert.True(ganancias.IsDeleted);
    }

    [Fact]
    public async Task Delete_ALegajoDocument_RebuildsItsLegajo()
    {
        var h = new Harness();
        var legajo = h.Legajos.AddLegajo(consent: true);
        var iva = h.Legajos.AddDocument(legajo, DocumentTypes.DdjjIva, LegajoStatuses.Ready);

        Assert.IsType<NoContentResult>(await h.Sut.DeleteAsync(iva.DocumentId, CancellationToken.None));

        Assert.Equal((legajo.PersonLegajoId, LegajoTestData.Tenant, "delete"), Assert.Single(h.Reconciliation.Triggers));
    }

    [Fact]
    public async Task Delete_ACompanyDocument_DoesNotRebuild()
    {
        var h = new Harness();
        var legajo = h.Legajos.AddLegajo(consent: true);
        var balance = h.Legajos.AddDocument(legajo, DocumentTypes.Balance, null);
        balance.PersonLegajoId = null;

        Assert.IsType<NoContentResult>(await h.Sut.DeleteAsync(balance.DocumentId, CancellationToken.None));

        Assert.Empty(h.Reconciliation.Triggers);
    }

    // ── Legacy upload is unchanged ──────────────────────────────────────────────

    [Fact]
    public async Task LegacyUpload_StillAcceptsOnlyTheFourLegacyTypes_WithTheSameMessage()
    {
        var h = new Harness();
        var entityId = Guid.NewGuid();
        h.Entities.Entities.Add(new Entity { EntityId = entityId, TenantId = LegajoTestData.Tenant });

        var problem = Problem(await h.UploadAsync(Pdf(), entityId, DocumentTypes.Ganancias, legajoId: null), 400);

        var error = Assert.Single((IEnumerable<object>)problem.Extensions["errors"]!);
        Assert.Contains("documentType debe ser uno de: Balance, Complemento, Certificado, Otro.", error.ToString());
    }

    [Fact]
    public async Task LegacyUpload_Balance_StoresNoLegajoColumns_AndTouchesNoLegajoCode()
    {
        var h = new Harness();
        var entityId = Guid.NewGuid();
        h.Entities.Entities.Add(new Entity { EntityId = entityId, TenantId = LegajoTestData.Tenant });

        var response = Created(await h.UploadAsync(Pdf(), entityId, "balance", legajoId: null));

        var stored = h.Legajos.Get(response.DocumentId);
        Assert.Equal("balance", stored.DocumentType);   // stored as given, exactly as before
        Assert.Null(stored.PersonLegajoId);
        Assert.Null(stored.LegajoStatus);
        Assert.Null(stored.PageCount);
        Assert.Null(stored.PdfType);
        Assert.Null(stored.PageQualityJson);
        Assert.Equal("Pending", stored.ProcessingStatus);
        Assert.Empty(h.Queue.Items);
        Assert.Empty(h.Legajos.Transitions);
        Assert.Null(response.LegajoStatus);
    }

    // ── Size ceiling: PdfSizeLimits.MaxPdfBytes (25 MB), legacy and legajo alike ─────────────────

    private static long Megabytes(double mb) => (long)(mb * 1024 * 1024);

    /// <summary>A real PDF padded with trailing newlines to exactly <paramref name="size"/> bytes.</summary>
    private static byte[] PdfOfSize(long size)
    {
        var pdf   = Pdf();
        var bytes = new byte[size];
        pdf.CopyTo(bytes, 0);
        Array.Fill(bytes, (byte)'\n', pdf.Length, bytes.Length - pdf.Length);
        return bytes;
    }

    /// <summary>
    /// A file that DECLARES <paramref name="declaredLength"/> bytes over a small real PDF: the size check
    /// runs before anything reads the stream, so a refusal never needs the bytes.
    /// </summary>
    private static IFormFile DeclaredPdf(long declaredLength)
    {
        var pdf = Pdf();
        return new FormFile(new MemoryStream(pdf), 0, declaredLength, "file", "documento.pdf")
        {
            Headers     = new HeaderDictionary(),
            ContentType = "application/pdf"
        };
    }

    [Theory]
    [InlineData(24.9)]
    [InlineData(25.0)]   // exactly PdfSizeLimits.MaxPdfBytes
    public async Task LegacyUpload_UpToTheCeiling_IsAccepted(double mb)
    {
        var h = new Harness();
        var entityId = Guid.NewGuid();
        h.Entities.Entities.Add(new Entity { EntityId = entityId, TenantId = LegajoTestData.Tenant });

        var response = Created(await h.UploadAsync(PdfOfSize(Megabytes(mb)), entityId, DocumentTypes.Balance, legajoId: null));

        Assert.Equal(Megabytes(mb), h.Legajos.Get(response.DocumentId).FileSizeBytes);
        Assert.Equal(Megabytes(mb), Assert.Single(h.Storage.Files).Value.LongLength);
    }

    [Fact]
    public async Task LegajoUpload_JustUnderTheCeiling_IsAccepted()
    {
        var h = new Harness();
        var (entity, legajo) = h.PersonaHumana(consent: false);

        var response = Created(await h.UploadAsync(PdfOfSize(Megabytes(24.9)), entity.EntityId, DocumentTypes.Ganancias, legajo.PersonLegajoId));

        Assert.Equal(LegajoStatuses.Uploaded, response.LegajoStatus);
        Assert.Equal(Megabytes(24.9), h.Legajos.Get(response.DocumentId).FileSizeBytes);
    }

    [Theory]
    [InlineData(25.1, false)]
    [InlineData(25.1, true)]
    [InlineData(25.0 + 1.0 / (1024 * 1024), false)]   // one byte over
    [InlineData(25.0 + 1.0 / (1024 * 1024), true)]
    public async Task Upload_OverTheCeiling_Is400_WithTheSameValidationProblem_AndStoresNothing(double mb, bool intoLegajo)
    {
        var h = new Harness();
        var (entity, legajo) = h.PersonaHumana(consent: true);
        var before = h.Legajos.Documents.Count;

        var result = await h.Sut.UploadAsync(
            DeclaredPdf(Megabytes(mb)), entity.EntityId,
            intoLegajo ? DocumentTypes.Ganancias : DocumentTypes.Balance, 2025,
            intoLegajo ? legajo.PersonLegajoId : null, CancellationToken.None);

        var problem = Problem(result, 400);
        Assert.Equal("Validation Error", problem.Title);
        Assert.Equal("https://serviciosfinancieros.api/errors/validation_error", problem.Type);
        Assert.Equal("validation_error", problem.Extensions["errorCode"]);
        var error = Assert.Single((IEnumerable<object>)problem.Extensions["errors"]!).ToString();
        Assert.Contains("field = file", error);
        Assert.Contains("El archivo supera el tamaño máximo permitido de 25 MB.", error);
        Assert.Equal(before, h.Legajos.Documents.Count);
        Assert.Empty(h.Storage.Files);
        Assert.Empty(h.Queue.Items);
    }

    [Fact]
    public void Upload_RequestBodyLimit_LeavesRoomForTheMultipartEnvelope_AndStaysUnderTheServerDefaults()
    {
        var attribute = typeof(DocumentsController)
            .GetMethod(nameof(DocumentsController.UploadAsync))!
            .GetCustomAttribute<RequestSizeLimitAttribute>();

        var limit = ((IRequestSizeLimitMetadata)attribute!).MaxRequestBodySize;

        Assert.Equal(PdfSizeLimits.MaxUploadRequestBytes, limit);
        // The body carries the file plus boundaries and form fields: a limit equal to the file's would
        // refuse a file under the ceiling before the action's own ProblemDetails could answer.
        Assert.True(limit > PdfSizeLimits.MaxPdfBytes);
        // Kestrel's and IIS's default request limit (maxAllowedContentLength); no web.config raises it.
        Assert.True(limit <= 30_000_000);
        Assert.Equal(25L * 1024 * 1024, PdfSizeLimits.MaxPdfBytes);
    }

    // ── Reprocess ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reprocess_Failed_BackToQueued_Enqueued_Audited()
    {
        var h = new Harness();
        var legajo = h.Legajos.AddLegajo(consent: true);
        var document = h.Legajos.AddDocument(legajo, DocumentTypes.Ganancias, LegajoStatuses.Failed);

        var result = await h.Sut.ReprocessAsync(document.DocumentId, CancellationToken.None);

        Assert.Equal(202, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(LegajoStatuses.Queued, document.LegajoStatus);
        Assert.Single(h.Queue.Items);
        Assert.Contains(h.Audit.Entries, e => e.Action == "Document.Reprocessed");
    }

    [Fact]
    public async Task Reprocess_ExtractionFailure_BackToReadyForExtraction_NeverToClassification()
    {
        var h = new Harness();
        var legajo = h.Legajos.AddLegajo(consent: true);
        var document = h.Legajos.AddDocument(legajo, DocumentTypes.BienesPersonales, LegajoStatuses.Failed);
        document.LastExtractionError = "extraction_claude_timeout";

        var result = await h.Sut.ReprocessAsync(document.DocumentId, CancellationToken.None);

        Assert.Equal(202, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(LegajoStatuses.ReadyForExtraction, document.LegajoStatus);
        Assert.Equal(document.DocumentId, Assert.Single(h.Queue.Items).DocumentId);
        Assert.Contains(h.Legajos.Transitions, t => t is { From: LegajoStatuses.Failed, To: LegajoStatuses.ReadyForExtraction });
    }

    [Fact]
    public async Task Reprocess_ClassificationFailure_BackToQueued()
    {
        var h = new Harness();
        var document = h.Legajos.AddDocument(h.Legajos.AddLegajo(consent: true), DocumentTypes.BienesPersonales, LegajoStatuses.Failed);
        document.LastExtractionError = "claude_timeout";

        await h.Sut.ReprocessAsync(document.DocumentId, CancellationToken.None);

        Assert.Equal(LegajoStatuses.Queued, document.LegajoStatus);
    }

    [Theory]
    [InlineData(LegajoStatuses.Uploaded)]
    [InlineData(LegajoStatuses.NeedsReview)]
    [InlineData(LegajoStatuses.Blocked)]
    public async Task Reprocess_NotFailed_409(string status)
    {
        var h = new Harness();
        var document = h.Legajos.AddDocument(h.Legajos.AddLegajo(consent: true), DocumentTypes.Ganancias, status);

        Problem(await h.Sut.ReprocessAsync(document.DocumentId, CancellationToken.None), 409);
        Assert.Empty(h.Queue.Items);
    }

    [Fact]
    public async Task Reprocess_AttemptBudgetSpent_409()
    {
        var h = new Harness(maxAttempts: 2);
        h.Documents.ExtractionAttemptsBudgetSpent = 2;
        var document = h.Legajos.AddDocument(h.Legajos.AddLegajo(consent: true), DocumentTypes.Ganancias, LegajoStatuses.Failed);

        Problem(await h.Sut.ReprocessAsync(document.DocumentId, CancellationToken.None), 409);
        Assert.Equal(LegajoStatuses.Failed, document.LegajoStatus);
    }

    [Fact]
    public async Task Reprocess_CompanyDocument_400()
    {
        var h = new Harness();
        var document = h.Legajos.AddDocument(h.Legajos.AddLegajo(), DocumentTypes.Balance, null);
        document.PersonLegajoId = null;

        Problem(await h.Sut.ReprocessAsync(document.DocumentId, CancellationToken.None), 400);
    }

    // ── Classification confirm ──────────────────────────────────────────────────

    private static ConfirmClassificationRequest Confirm(string type = DocumentTypes.Ganancias, string reason = "Revisado contra el original.")
        => new() { DocumentType = type, Reason = reason, PeriodEnd = new DateOnly(2025, 12, 31) };

    [Fact]
    public async Task Confirm_NeedsReview_ReadyForExtraction_Audited()
    {
        var h = new Harness();
        var document = h.Legajos.AddDocument(h.Legajos.AddLegajo(), DocumentTypes.Otro, LegajoStatuses.NeedsReview);

        var result = await h.Sut.ConfirmClassificationAsync(document.DocumentId, Confirm(), CancellationToken.None);

        var response = Assert.IsType<DocumentResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(LegajoStatuses.ReadyForExtraction, response.LegajoStatus);
        Assert.Equal(DocumentTypes.Ganancias, response.DocumentType);
        Assert.Equal(new DateOnly(2025, 12, 31), response.PeriodEnd);
        Assert.Equal(DocumentTypes.Ganancias, document.DocumentType);

        var audit = Assert.Single(h.Audit.Entries, e => e.Action == "Document.ClassificationConfirmed");
        Assert.Contains("Revisado contra el original.", audit.NewJson);
    }

    [Fact]
    public async Task Confirm_EnqueuesTheDocumentForExtraction()
    {
        var h = new Harness();
        var document = h.Legajos.AddDocument(h.Legajos.AddLegajo(), DocumentTypes.Otro, LegajoStatuses.NeedsReview);

        await h.Sut.ConfirmClassificationAsync(document.DocumentId, Confirm(DocumentTypes.BienesPersonales), CancellationToken.None);

        Assert.Equal(document.DocumentId, Assert.Single(h.Queue.Items).DocumentId);
    }

    [Fact]
    public async Task Confirm_NeedsReviewOfAnExtraction_409()
    {
        var h = new Harness();
        var legajo = h.Legajos.AddLegajo();
        var document = h.Legajos.AddDocument(legajo, DocumentTypes.BienesPersonales, LegajoStatuses.NeedsReview);
        h.Legajos.Runs.Add(new LegajoExtraction
        {
            ExtractionId = Guid.NewGuid(), TenantId = document.TenantId, PersonLegajoId = legajo.PersonLegajoId,
            DocumentId = document.DocumentId, Kind = LegajoExtractionKinds.Extraction, IsCurrent = true
        });

        Problem(await h.Sut.ConfirmClassificationAsync(document.DocumentId, Confirm(DocumentTypes.BienesPersonales), CancellationToken.None), 409);
        Assert.Equal(LegajoStatuses.NeedsReview, document.LegajoStatus);
        Assert.Empty(h.Queue.Items);
    }

    [Fact]
    public async Task Confirm_Blocked_409_AndTheDocumentStaysBlocked()
    {
        var h = new Harness();
        var document = h.Legajos.AddDocument(h.Legajos.AddLegajo(), DocumentTypes.Ganancias, LegajoStatuses.Blocked);

        var problem = Problem(await h.Sut.ConfirmClassificationAsync(document.DocumentId, Confirm(), CancellationToken.None), 409);

        Assert.Contains("solo puede eliminarse", problem.Detail);
        Assert.Equal(LegajoStatuses.Blocked, document.LegajoStatus);
    }

    [Theory]
    [InlineData(LegajoStatuses.Uploaded)]
    [InlineData(LegajoStatuses.ReadyForExtraction)]
    [InlineData(LegajoStatuses.Failed)]
    public async Task Confirm_OnlyFromNeedsReview(string status)
    {
        var h = new Harness();
        var document = h.Legajos.AddDocument(h.Legajos.AddLegajo(), DocumentTypes.Ganancias, status);

        Problem(await h.Sut.ConfirmClassificationAsync(document.DocumentId, Confirm(), CancellationToken.None), 409);
    }

    [Theory]
    [InlineData("")]
    [InlineData("    ")]
    [InlineData("ok")]
    public async Task Confirm_ReasonRequired(string reason)
    {
        var h = new Harness();
        var document = h.Legajos.AddDocument(h.Legajos.AddLegajo(), DocumentTypes.Ganancias, LegajoStatuses.NeedsReview);

        Problem(await h.Sut.ConfirmClassificationAsync(document.DocumentId, Confirm(reason: reason), CancellationToken.None), 400);
        Assert.Equal(LegajoStatuses.NeedsReview, document.LegajoStatus);
    }

    [Theory]
    [InlineData(DocumentTypes.Balance)]
    [InlineData(DocumentTypes.AutorizacionDatos)]
    [InlineData("Inventado")]
    public async Task Confirm_TypeMustBeALegajoTypeOtherThanTheAuthorization(string type)
    {
        var h = new Harness();
        var document = h.Legajos.AddDocument(h.Legajos.AddLegajo(), DocumentTypes.Ganancias, LegajoStatuses.NeedsReview);

        Problem(await h.Sut.ConfirmClassificationAsync(document.DocumentId, Confirm(type: type), CancellationToken.None), 400);
    }

    // ── Stable errorCodes (Portal #326): status, title and detail unchanged ─────

    private static void Coded(ProblemDetails problem, string title, string errorCode, string detail)
    {
        Assert.Equal(title, problem.Title);
        Assert.Equal(detail, problem.Detail);
        Assert.Equal(errorCode, problem.Extensions["errorCode"]);
    }

    [Fact]
    public async Task Reprocess_CompanyDocument_400_NotLegajoDocument()
    {
        var h = new Harness();
        var document = h.Legajos.AddDocument(h.Legajos.AddLegajo(), DocumentTypes.Balance, null);
        document.PersonLegajoId = null;

        Coded(Problem(await h.Sut.ReprocessAsync(document.DocumentId, CancellationToken.None), 400),
            "Bad Request", "not_legajo_document", "Solo se reprocesan documentos de un legajo de persona humana.");
    }

    [Fact]
    public async Task Reprocess_NotFailed_409_InvalidStatus()
    {
        var h = new Harness();
        var document = h.Legajos.AddDocument(h.Legajos.AddLegajo(consent: true), DocumentTypes.Ganancias, LegajoStatuses.NeedsReview);

        Coded(Problem(await h.Sut.ReprocessAsync(document.DocumentId, CancellationToken.None), 409),
            "Conflict", "invalid_status", "Solo puede reprocesarse un documento en estado Failed.");
    }

    [Fact]
    public async Task Reprocess_AttemptBudgetSpent_409_AttemptsExhausted()
    {
        var h = new Harness(maxAttempts: 2);
        h.Documents.ExtractionAttemptsBudgetSpent = 2;
        var document = h.Legajos.AddDocument(h.Legajos.AddLegajo(consent: true), DocumentTypes.Ganancias, LegajoStatuses.Failed);

        Coded(Problem(await h.Sut.ReprocessAsync(document.DocumentId, CancellationToken.None), 409),
            "Conflict", "attempts_exhausted", "El archivo agotó los intentos de procesamiento permitidos.");
    }

    [Fact]
    public async Task Reprocess_LostTheTransition_409_StatusChanged_NothingQueued()
    {
        var h = new Harness();
        var document = h.Legajos.AddDocument(h.Legajos.AddLegajo(consent: true), DocumentTypes.Ganancias, LegajoStatuses.Failed);
        h.Legajos.BeforeTransition = () => document.LegajoStatus = LegajoStatuses.Queued;   // another reprocess won

        Coded(Problem(await h.Sut.ReprocessAsync(document.DocumentId, CancellationToken.None), 409),
            "Conflict", "status_changed", "El documento cambió de estado; recargue e intente nuevamente.");
        Assert.Empty(h.Queue.Items);
    }

    [Fact]
    public async Task Confirm_CompanyDocument_400_NotLegajoDocument()
    {
        var h = new Harness();
        var document = h.Legajos.AddDocument(h.Legajos.AddLegajo(), DocumentTypes.Ganancias, LegajoStatuses.NeedsReview);
        document.PersonLegajoId = null;

        Coded(Problem(await h.Sut.ConfirmClassificationAsync(document.DocumentId, Confirm(), CancellationToken.None), 400),
            "Bad Request", "not_legajo_document", "Solo se confirman documentos de un legajo de persona humana.");
    }

    [Fact]
    public async Task Confirm_Blocked_409_DocumentBlocked()
    {
        var h = new Harness();
        var document = h.Legajos.AddDocument(h.Legajos.AddLegajo(), DocumentTypes.Ganancias, LegajoStatuses.Blocked);

        Coded(Problem(await h.Sut.ConfirmClassificationAsync(document.DocumentId, Confirm(), CancellationToken.None), 409),
            "Conflict", "document_blocked",
            "El documento está bloqueado porque corresponde a otra persona. No puede confirmarse; solo puede eliminarse.");
    }

    [Fact]
    public async Task Confirm_NotNeedsReview_409_InvalidStatus()
    {
        var h = new Harness();
        var document = h.Legajos.AddDocument(h.Legajos.AddLegajo(), DocumentTypes.Ganancias, LegajoStatuses.ReadyForExtraction);

        Coded(Problem(await h.Sut.ConfirmClassificationAsync(document.DocumentId, Confirm(), CancellationToken.None), 409),
            "Conflict", "invalid_status", "Solo puede confirmarse un documento en estado NeedsReview.");
    }

    [Fact]
    public async Task Confirm_NeedsReviewOfAnExtraction_409_ClassificationAlreadyConfirmed()
    {
        var h = new Harness();
        var legajo = h.Legajos.AddLegajo();
        var document = h.Legajos.AddDocument(legajo, DocumentTypes.BienesPersonales, LegajoStatuses.NeedsReview);
        h.Legajos.Runs.Add(new LegajoExtraction
        {
            ExtractionId = Guid.NewGuid(), TenantId = document.TenantId, PersonLegajoId = legajo.PersonLegajoId,
            DocumentId = document.DocumentId, Kind = LegajoExtractionKinds.Extraction, IsCurrent = true
        });

        Coded(Problem(await h.Sut.ConfirmClassificationAsync(document.DocumentId, Confirm(DocumentTypes.BienesPersonales), CancellationToken.None), 409),
            "Conflict", "classification_already_confirmed",
            "La clasificación ya fue confirmada; el documento espera la revisión de su extracción.");
    }

    [Fact]
    public async Task Confirm_LostTheTransition_409_StatusChanged_NothingQueued()
    {
        var h = new Harness();
        var document = h.Legajos.AddDocument(h.Legajos.AddLegajo(), DocumentTypes.Ganancias, LegajoStatuses.NeedsReview);
        h.Legajos.BeforeConfirm = () => document.LegajoStatus = LegajoStatuses.ReadyForExtraction;   // another confirm won

        Coded(Problem(await h.Sut.ConfirmClassificationAsync(document.DocumentId, Confirm(), CancellationToken.None), 409),
            "Conflict", "status_changed", "El documento cambió de estado; recargue e intente nuevamente.");
        Assert.Empty(h.Queue.Items);
    }

    // ── List endpoint carries the additive fields ───────────────────────────────

    [Fact]
    public async Task GetAll_ByEntity_IncludesLegajoDocumentsWithTheNewFields()
    {
        var h = new Harness();
        var (entity, legajo) = h.PersonaHumana(consent: false);
        var uploaded = Created(await h.UploadAsync(Pdf(), entity.EntityId, DocumentTypes.Ganancias, legajo.PersonLegajoId));

        var list = Assert.IsType<DocumentListResponse>(Assert.IsType<OkObjectResult>(
            await h.Sut.GetAllAsync(1, 20, entity.EntityId, CancellationToken.None)).Value);

        var item = Assert.Single(list.Items);
        Assert.Equal(uploaded.DocumentId, item.DocumentId);
        Assert.Equal(legajo.PersonLegajoId, item.PersonLegajoId);
        Assert.Equal(LegajoStatuses.Uploaded, item.LegajoStatus);
    }
}
