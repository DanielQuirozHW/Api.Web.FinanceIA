// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-08

using System.Security.Claims;
using System.Security.Cryptography;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.Concurrency;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Api.Web.FinanceIA.Tests.Legajo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// The company side of <see cref="DocumentsController"/> after the Análisis en curso follow-up: the upload is
/// idempotent for <see cref="DocumentsController.DuplicateUploadWindow"/> (QA Q32), and GET /api/documents and
/// GET /api/documents/{id} carry failureReason / causeGroup / attemptsRemaining (QA Q35). Synthetic data;
/// hand-written fakes.
/// </summary>
public sealed class DocumentsControllerCompanyDocumentsTests
{
    private const string Tenant = LegajoTestData.Tenant;

    private sealed class Harness
    {
        public List<Document> Rows { get; } = [];
        public ListDocumentRepository Documents { get; }
        public StubEntityRepository Entities { get; } = new();
        public InMemoryStorage Storage { get; } = new();
        public StubUserRepository Users { get; } = new();
        public RecordingAuditService Audit { get; } = new();
        public RecordingRunListRepository RunList { get; } = new();
        public KeyedAsyncLock Lock { get; } = new();
        public Entity Company { get; }
        public DocumentsController Sut { get; }

        public Harness(int maxAttempts = 2)
        {
            Documents = new ListDocumentRepository(Rows);
            Company = new Entity
            {
                EntityId   = Guid.NewGuid(),
                TenantId   = Tenant,
                PersonType = PersonTypes.Juridica
            };
            Entities.Entities.Add(Company);
            Sut = NewController(maxAttempts);
        }

        /// <summary>A second controller over the SAME fakes and lock: one per request, as DI scopes it.</summary>
        public DocumentsController NewController(int maxAttempts = 2) => new(
            new StubTenantContextAccessor(),
            Documents,
            Entities,
            Storage,
            Users,
            Audit,
            new FakeLegajoDocumentRepository(),
            new FakeFiadorRepository(),
            new RecordingPipelineQueue(),
            new RecordingReconciliationService(),
            Options.Create(new BillingOptions { MaxExtractionAttemptsPerDocument = maxAttempts }),
            RunList,
            Lock,
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

        public Entity AddCompany()
        {
            var entity = new Entity { EntityId = Guid.NewGuid(), TenantId = Tenant, PersonType = PersonTypes.Juridica };
            Entities.Entities.Add(entity);
            return entity;
        }

        public Task<IActionResult> UploadAsync(byte[] pdf, Guid? entityId = null, string type = DocumentTypes.Balance)
            => UploadWithAsync(Sut, pdf, entityId ?? Company.EntityId, type);

        public static Task<IActionResult> UploadWithAsync(DocumentsController sut, byte[] pdf, Guid entityId, string type)
            => sut.UploadAsync(PdfFile(pdf), entityId, type, 2024, personLegajoId: null, CancellationToken.None);

        /// <summary>A stored company document, as an earlier upload would have left it.</summary>
        public Document AddStored(
            string    status       = "Pending",
            byte[]?   pdf          = null,
            string?   hash         = null,
            string    type         = DocumentTypes.Balance,
            DateTime? uploadedAt   = null,
            Guid?     entityId     = null,
            DateTime? processedAt  = null,
            string?   lastError    = null,
            DateTime? lastFailedAt = null,
            Guid?     legajoId     = null,
            string?   legajoStatus = null)
        {
            var document = new Document
            {
                DocumentId             = Guid.NewGuid(),
                TenantId               = Tenant,
                EntityId               = entityId ?? Company.EntityId,
                UploadedBy             = Users.Analyst.UserId,
                OriginalFileName       = "balance.pdf",
                StoredFileName         = "stored.pdf",
                StoragePath            = "stored.pdf",
                MimeType               = "application/pdf",
                FileHashSHA256         = hash ?? HashOf(pdf ?? Pdf()),
                DocumentType           = type,
                FiscalYear             = 2024,
                ProcessingStatus       = status,
                UploadedAt             = uploadedAt ?? DateTime.UtcNow,
                ProcessedAt            = processedAt,
                LastExtractionError    = lastError,
                LastExtractionFailedAt = lastFailedAt,
                PersonLegajoId         = legajoId,
                LegajoStatus           = legajoStatus
            };
            Rows.Add(document);
            return document;
        }
    }

    private static IFormFile PdfFile(byte[] bytes)
        => new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "balance.pdf")
        {
            Headers     = new HeaderDictionary(),
            ContentType = "application/pdf"
        };

    private static byte[] Pdf(string text = LegajoPdfFactory.SampleText) => LegajoPdfFactory.NativePdf(text);

    private static string HashOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static DocumentResponse Created(IActionResult result)
        => Assert.IsType<DocumentResponse>(Assert.IsType<CreatedAtActionResult>(result).Value);

    private static DocumentResponse OkBody(IActionResult result)
        => Assert.IsType<DocumentResponse>(Assert.IsType<OkObjectResult>(result).Value);

    // ── Upload idempotency ──────────────────────────────────────────────────────

    [Fact]
    public async Task Upload_SameFileTwiceWithinWindow_SecondAnswers200WithFirstDocument_AndWritesNothing()
    {
        var h   = new Harness();
        var pdf = Pdf();

        var first  = Created(await h.UploadAsync(pdf));
        var second = OkBody(await h.UploadAsync(pdf));

        Assert.Equal(first.DocumentId, second.DocumentId);
        Assert.Single(h.Rows);
        Assert.Equal(1, h.Documents.CreateCalls);
        Assert.Equal(1, h.Storage.SaveCalls);
        Assert.Single(h.Audit.Entries, e => e.Action == "Document.Uploaded");
        Assert.Equal(0, h.Lock.Count);
    }

    [Fact]
    public async Task Upload_SameFileOutsideWindow_CreatesNewDocument()
    {
        var h   = new Harness();
        var pdf = Pdf();
        var old = h.AddStored(pdf: pdf,
            uploadedAt: DateTime.UtcNow - DocumentsController.DuplicateUploadWindow - TimeSpan.FromSeconds(5));

        var created = Created(await h.UploadAsync(pdf));

        Assert.NotEqual(old.DocumentId, created.DocumentId);
        Assert.Equal(2, h.Rows.Count);
        Assert.Equal(1, h.Storage.SaveCalls);
    }

    [Fact]
    public async Task Upload_SameFileForAnotherEntity_CreatesNewDocument()
    {
        var h     = new Harness();
        var pdf   = Pdf();
        var other = h.AddCompany();
        h.AddStored(pdf: pdf, entityId: other.EntityId);

        Created(await h.UploadAsync(pdf));

        Assert.Equal(2, h.Rows.Count);
    }

    [Fact]
    public async Task Upload_SameFileAsAnotherType_CreatesNewDocument()
    {
        var h   = new Harness();
        var pdf = Pdf();
        h.AddStored(pdf: pdf, type: DocumentTypes.Complemento);

        Created(await h.UploadAsync(pdf));

        Assert.Equal(2, h.Rows.Count);
    }

    [Fact]
    public async Task Upload_DeletedOrLegajoTwin_IsNotReused()
    {
        var h   = new Harness();
        var pdf = Pdf();
        h.AddStored(pdf: pdf).IsDeleted = true;
        h.AddStored(pdf: pdf, legajoId: Guid.NewGuid(), legajoStatus: LegajoStatuses.Uploaded);

        Created(await h.UploadAsync(pdf));

        Assert.Equal(3, h.Rows.Count);
    }

    [Fact]
    public async Task Upload_BlankStoredHash_NeverMatches()
    {
        var h = new Harness();
        h.AddStored(hash: "");

        Created(await h.UploadAsync(Pdf()));

        Assert.Equal(2, h.Rows.Count);
    }

    [Fact]
    public async Task Upload_HashComparedIgnoringCase()
    {
        var h   = new Harness();
        var pdf = Pdf();
        var stored = h.AddStored(hash: HashOf(pdf).ToUpperInvariant());

        Assert.Equal(stored.DocumentId, OkBody(await h.UploadAsync(pdf)).DocumentId);
    }

    [Fact]
    public async Task Upload_LookupFails_StillStoresTheUpload()
    {
        var h = new Harness();
        h.Documents.FindRecentThrows = new InvalidOperationException("db down");

        Created(await h.UploadAsync(Pdf()));

        Assert.Single(h.Rows);
        Assert.Equal(1, h.Storage.SaveCalls);
        Assert.Equal(0, h.Lock.Count);
    }

    [Fact]
    public async Task Upload_TwoConcurrentRequestsOfTheSameFile_CreateExactlyOneDocument()
    {
        var h   = new Harness();
        var pdf = Pdf();

        // The first request's blob write is slow: it blocks until the second request is already waiting.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saving  = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Storage.BeforeSave = async () =>
        {
            saving.TrySetResult();
            await release.Task.ConfigureAwait(false);
        };

        var firstTask = Harness.UploadWithAsync(h.NewController(), pdf, h.Company.EntityId, DocumentTypes.Balance);
        await saving.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var secondTask = Harness.UploadWithAsync(h.NewController(), pdf, h.Company.EntityId, DocumentTypes.Balance);
        await Task.Delay(100);
        Assert.False(secondTask.IsCompleted);   // waiting on the lock, not racing the insert

        release.SetResult();
        var results = await Task.WhenAll(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, h.Documents.CreateCalls);
        Assert.Equal(1, h.Storage.SaveCalls);
        Assert.Single(h.Rows);
        Assert.Equal(Created(results[0]).DocumentId, OkBody(results[1]).DocumentId);
        Assert.Equal(0, h.Lock.Count);
    }

    [Fact]
    public async Task Upload_DuplicateOfFailedDocument_CarriesTheFailureFlags()
    {
        var h   = new Harness(maxAttempts: 2);
        var pdf = Pdf();
        var failedAt = DateTime.UtcNow.AddSeconds(-10);
        var stored = h.AddStored("Failed", pdf: pdf, uploadedAt: DateTime.UtcNow.AddSeconds(-30), processedAt: failedAt,
            lastError: AnalysisRunFailureCodes.StoredClaudeNoFinancialStatements, lastFailedAt: failedAt);
        h.RunList.AttemptsByHash[stored.FileHashSHA256] = 0;

        var body = OkBody(await h.UploadAsync(pdf));

        Assert.Equal(AnalysisRunFailureReasons.NoFinancialStatements, body.FailureReason);
        Assert.Equal(AnalysisRunFailureReasons.Document, body.CauseGroup);
        Assert.Equal(2, body.AttemptsRemaining);
    }

    // ── Failure flags on GET ────────────────────────────────────────────────────

    [Fact]
    public async Task GetAll_FillsFlagsPerStatus_WithOneBatchedAttemptsRead()
    {
        var h = new Harness(maxAttempts: 3);
        var failedAt = DateTime.UtcNow.AddMinutes(-5);

        var noBalance = h.AddStored("Failed", hash: "aa", processedAt: failedAt,
            lastError: AnalysisRunFailureCodes.StoredClaudeNoFinancialStatements, lastFailedAt: failedAt);
        var timedOut  = h.AddStored("Failed", hash: "bb", processedAt: failedAt,
            lastError: AnalysisRunFailureCodes.StoredClaudeTimeout, lastFailedAt: failedAt);
        var stale     = h.AddStored("Failed", hash: "cc", processedAt: failedAt,
            lastError: AnalysisRunFailureCodes.StoredClaudeTimeout, lastFailedAt: failedAt.AddHours(-1));
        var pending   = h.AddStored("Pending", hash: "aa");
        var completed = h.AddStored("Completed", hash: "dd", processedAt: failedAt);
        var legajo    = h.AddStored("Pending", hash: "ee", legajoId: Guid.NewGuid(), legajoStatus: LegajoStatuses.Failed);

        h.RunList.AttemptsByHash["aa"] = 3;
        h.RunList.AttemptsByHash["bb"] = 1;

        var list = Assert.IsType<DocumentListResponse>(Assert.IsType<OkObjectResult>(
            await h.Sut.GetAllAsync(1, 20, h.Company.EntityId, CancellationToken.None)).Value);
        var byId = list.Items.ToDictionary(i => i.DocumentId);

        // ONE read, for the hashes of the non-Completed company rows only.
        var call = Assert.Single(h.RunList.SumCalls);
        Assert.Equal(["aa", "bb", "cc"], call.OrderBy(x => x, StringComparer.Ordinal));

        Assert.Equal(AnalysisRunFailureReasons.NoFinancialStatements, byId[noBalance.DocumentId].FailureReason);
        Assert.Equal(AnalysisRunFailureReasons.Document, byId[noBalance.DocumentId].CauseGroup);
        Assert.Equal(0, byId[noBalance.DocumentId].AttemptsRemaining);

        Assert.Equal(AnalysisRunFailureReasons.Timeout, byId[timedOut.DocumentId].FailureReason);
        Assert.Equal(AnalysisRunFailureReasons.Ours, byId[timedOut.DocumentId].CauseGroup);
        Assert.Equal(2, byId[timedOut.DocumentId].AttemptsRemaining);

        // A code from an earlier failure is not this failure's reason.
        Assert.Equal(AnalysisRunFailureReasons.Other, byId[stale.DocumentId].FailureReason);
        Assert.Equal(3, byId[stale.DocumentId].AttemptsRemaining);

        Assert.Null(byId[pending.DocumentId].FailureReason);
        Assert.Null(byId[pending.DocumentId].CauseGroup);
        Assert.Equal(0, byId[pending.DocumentId].AttemptsRemaining);   // same file as noBalance

        Assert.Null(byId[completed.DocumentId].FailureReason);
        Assert.Null(byId[completed.DocumentId].AttemptsRemaining);

        Assert.Null(byId[legajo.DocumentId].FailureReason);
        Assert.Null(byId[legajo.DocumentId].CauseGroup);
        Assert.Null(byId[legajo.DocumentId].AttemptsRemaining);
    }

    [Fact]
    public async Task GetAll_BudgetOff_AttemptsNull_AndNothingRead()
    {
        var h = new Harness(maxAttempts: 0);
        var failedAt = DateTime.UtcNow;
        h.AddStored("Failed", hash: "aa", processedAt: failedAt,
            lastError: AnalysisRunFailureCodes.StoredClaudeRefusal, lastFailedAt: failedAt);

        var item = Assert.Single(Assert.IsType<DocumentListResponse>(Assert.IsType<OkObjectResult>(
            await h.Sut.GetAllAsync(1, 20, null, CancellationToken.None)).Value).Items);

        Assert.Empty(h.RunList.SumCalls);
        Assert.Equal(AnalysisRunFailureReasons.UnreadableDocument, item.FailureReason);
        Assert.Equal(AnalysisRunFailureReasons.Document, item.CauseGroup);
        Assert.Null(item.AttemptsRemaining);
    }

    [Fact]
    public async Task GetAll_AttemptsReadFails_ListStillReturned_AttemptsNull()
    {
        var h = new Harness();
        var failedAt = DateTime.UtcNow;
        h.AddStored("Failed", hash: "aa", processedAt: failedAt,
            lastError: AnalysisRunFailureCodes.StoredClaudeServerError, lastFailedAt: failedAt);
        h.RunList.SumThrows = new InvalidOperationException("db down");

        var item = Assert.Single(Assert.IsType<DocumentListResponse>(Assert.IsType<OkObjectResult>(
            await h.Sut.GetAllAsync(1, 20, null, CancellationToken.None)).Value).Items);

        Assert.Equal(AnalysisRunFailureReasons.ServiceUnavailable, item.FailureReason);
        Assert.Null(item.AttemptsRemaining);
    }

    [Fact]
    public async Task GetById_FillsFlags()
    {
        var h = new Harness(maxAttempts: 2);
        var failedAt = DateTime.UtcNow;
        var doc = h.AddStored("Failed", hash: "aa", processedAt: failedAt,
            lastError: AnalysisRunFailureCodes.StoredClaudeApiError, lastFailedAt: failedAt);
        h.RunList.AttemptsByHash["aa"] = 1;

        var body = OkBody(await h.Sut.GetById(doc.DocumentId, CancellationToken.None));

        Assert.Equal(AnalysisRunFailureReasons.FileRejected, body.FailureReason);
        Assert.Equal(AnalysisRunFailureReasons.Document, body.CauseGroup);
        Assert.Equal(1, body.AttemptsRemaining);
        Assert.Single(h.RunList.SumCalls);
    }
}
