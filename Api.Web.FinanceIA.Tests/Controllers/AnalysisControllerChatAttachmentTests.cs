// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-28

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;

using ClosedXML.Excel;

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Covers POST /api/analyses/{resultId}/chat/attachment: that a real workbook comes back as
/// Markdown, that everything else is refused with a message saying why, and — the part worth having
/// a controller test for rather than a parser test — that the endpoint stays ephemeral. Nothing is
/// persisted, the audit trail records the file's identity and never its contents, and a rejected
/// upload leaves the conversation it was headed for completely untouched.
///
/// <para>
/// The two-gate check is the security claim under test. An .xlsx is a ZIP, so the magic-byte gate
/// can only establish "this is a ZIP"; a .docx sails through it byte for byte and is stopped only
/// by opening the package as a spreadsheet. Both gates are exercised separately below.
/// </para>
///
/// <para>
/// Hand-rolled private sealed stubs (no mock library), the REAL parser and signature validator, and
/// the argument order of BuildSut mirrors the ctor signature with null! for everything this path
/// does not touch — same convention as <c>AnalysisControllerChatCashFlowTests</c>.
/// </para>
/// </summary>
public sealed class AnalysisControllerChatAttachmentTests
{
    private const string Tenant      = "tenant-1";
    private const string FirebaseUid = "uid-1";
    private const string XlsxMime    = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly Guid ResultId   = Guid.NewGuid();
    private static readonly Guid AnalysisId = Guid.NewGuid();
    private static readonly Guid UserId     = Guid.NewGuid();

    // ── Stubs ─────────────────────────────────────────────────────────────────

    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private sealed class StubAnalysisResultRepository : IAnalysisResultRepository
    {
        public AnalysisResult? ResultToReturn { get; set; }

        public Task<AnalysisResult?> GetByIdAsync(Guid resultId, string tenantId, CancellationToken ct)
            => Task.FromResult(ResultToReturn);

        public Task<IReadOnlyList<AnalysisResult>> GetAllByTenantAsync(string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<AnalysisResult?> GetByDocumentIdAsync(Guid documentId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<AnalysisResult> CreateAsync(AnalysisResult result, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<AnalysisResult>> GetByEntityIdAsync(Guid entityId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<AnalysisResult> UpdateAsync(AnalysisResult result, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, Guid>> GetResultIdsByAnalysisIdsAsync(IEnumerable<Guid> analysisIds, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<(IReadOnlyList<AnalysisListProjection> Items, int TotalCount)> GetListPageByTenantAsync(string tenantId, int skip, int take, CancellationToken ct) => throw new NotImplementedException();
        public Task<(IReadOnlyList<AnalysisListProjection> Items, int TotalCount)> GetListPageByEntityAsync(Guid entityId, string tenantId, int skip, int take, CancellationToken ct) => throw new NotImplementedException();
        public Task<AnalysisResultDetailProjection?> GetDetailByIdAsync(Guid resultId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<AnalysisContextProjection?> GetContextSnapshotByIdAsync(Guid resultId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<AnalysisBcraProjection?> GetBcraSnapshotByIdAsync(Guid resultId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<AnalysisRatiosProjection?> GetRatiosByIdAsync(Guid resultId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<AnalysisLabelProjection?> GetLabelByIdAsync(Guid resultId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class StubUserRepository : IUserRepository
    {
        public User? UserToReturn { get; set; }

        public Task<User?> GetByFirebaseUidAsync(string firebaseUid, CancellationToken ct)
            => Task.FromResult(UserToReturn);

        public Task<User?> GetByUserIdAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<User?> GetByEmailAsync(string email, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> ExistsByFirebaseUidAsync(string firebaseUid, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<User>> GetByIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct) => throw new NotImplementedException();
        public Task<User> CreateAsync(User user, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateLastLoginAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateOnboardingFlagsAsync(Guid userId, string? flagsJson, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<User>> GetActiveByTenantAsync(string tenantId, CancellationToken ct) => throw new NotImplementedException();
        // Team assignment (DDL 063) — unused by this suite.
        public Task UpdateTeamAsync(Guid userId, Guid? teamId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> ClearTeamAsync(Guid teamId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
    }

    /// <summary>
    /// Records every persisted turn so a test can prove the endpoint wrote nothing — the claim that
    /// makes "ephemeral" more than a comment.
    /// </summary>
    private sealed class RecordingChatRepository : IChatRepository
    {
        public List<ChatMessage> Messages { get; } = new();

        public Task<ChatMessage> AddMessageAsync(ChatMessage message, CancellationToken ct)
        {
            Messages.Add(message);
            return Task.FromResult(message);
        }

        public Task<ChatConversation> CreateConversationAsync(ChatConversation conversation, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(Guid conversationId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ChatConversation>> GetConversationsByResultAsync(Guid resultId, string tenantId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ChatConversation>> GetConversationsByResultForTenantAsync(Guid resultId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<ChatConversation?> GetConversationByIdAsync(Guid conversationId, string tenantId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<ChatConversation?> GetConversationByIdForTenantAsync(Guid conversationId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> RenameConversationAsync(Guid conversationId, string tenantId, Guid userId, string newTitle, CancellationToken ct) => throw new NotImplementedException();
        public Task<ChatMessage?> GetMessageByIdAsync(long chatMessageId, Guid conversationId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class RecordingAuditService : IAuditService
    {
        public List<(string Action, Guid? UserId, string? EntityType, string? EntityId, string? NewValueJson)> Entries { get; } = new();

        public Task LogAsync(
            string tenantId, Guid? userId, string action, string severity = "Info",
            string? entityType = null, string? entityId = null, string? ipAddress = null,
            string? requestId = null, string? oldValueJson = null, string? newValueJson = null,
            CancellationToken ct = default)
        {
            Entries.Add((action, userId, entityType, entityId, newValueJson));
            return Task.CompletedTask;
        }

        public Task LogSessionAsync(
            Guid tenantId, Guid? userId, string email, string eventType,
            string? ipAddress, string? userAgent, string? failureReason = null,
            CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private sealed class FakeFormFile : IFormFile
    {
        private readonly byte[] _bytes;

        public FakeFormFile(byte[] bytes, string fileName, string contentType, long? declaredLength = null)
        {
            _bytes      = bytes;
            FileName    = fileName;
            ContentType = contentType;
            Length      = declaredLength ?? bytes.LongLength;
        }

        public string ContentType { get; }
        public string ContentDisposition => $"form-data; name=\"file\"; filename=\"{FileName}\"";
        public IHeaderDictionary Headers => new HeaderDictionary();
        public long Length { get; }
        public string Name => "file";
        public string FileName { get; }

        public Stream OpenReadStream() => new MemoryStream(_bytes, writable: false);
        public void CopyTo(Stream target) => target.Write(_bytes);

        public Task CopyToAsync(Stream target, CancellationToken ct = default)
        {
            target.Write(_bytes);
            return Task.CompletedTask;
        }
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static TenantContext BuildTenant() => new()
    {
        TenantId               = Tenant,
        FirebaseProjectId      = "proj",
        FirebaseCredentialJson = "{}",
        RecaptchaSecretKey     = "secret"
    };

    private static byte[] XlsxBytes()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Flujo");
        ws.Cell("A1").Value = "Concepto";
        ws.Cell("B1").Value = "Enero";
        ws.Cell("A2").Value = "Cobranzas";
        ws.Cell("B2").Value = 1500;

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static byte[] DocxBytes()
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            doc.AddMainDocumentPart().Document = new DocumentFormat.OpenXml.Wordprocessing.Document(
                new DocumentFormat.OpenXml.Wordprocessing.Body());
        }
        return ms.ToArray();
    }

    /// <summary>
    /// <paramref name="resultExists"/> false makes the tenant-scoped lookup miss, which is how a
    /// result belonging to another tenant looks from in here; <paramref name="userExists"/> false
    /// makes the caller unresolvable.
    /// </summary>
    private static (AnalysisController Sut, RecordingAuditService Audit, RecordingChatRepository Chat) BuildSut(
        bool resultExists = true,
        bool userExists   = true,
        Api.Web.FinanceIA.Tests.PersonAnalysis.StubPersonAnalysisRepository? personAnalyses = null)
    {
        var audit    = new RecordingAuditService();
        var chatRepo = new RecordingChatRepository();

        var resultRepo = new StubAnalysisResultRepository
        {
            ResultToReturn = resultExists
                ? new AnalysisResult { ResultId = ResultId, AnalysisId = AnalysisId, TenantId = Tenant }
                : null
        };

        var userRepo = new StubUserRepository
        {
            UserToReturn = userExists ? new User { UserId = UserId, DisplayName = "Analista" } : null
        };

        var sut = new AnalysisController(
            new StubTenantContextAccessor { TenantContext = BuildTenant() }, // tenantContextAccessor
            null!,            // documentRepository
            resultRepo,       // analysisResultRepository
            null!,            // analysisRepository
            null!,            // claudeService — this endpoint never calls the model
            null!,            // cashFlowReconciliationService
            null!,            // cashFlowNarrativeService
            null!,            // alertRatioReconciliationService
            null!,            // entityRepository
            userRepo,         // userRepository
            chatRepo,         // chatRepository
            null!,            // creditLineRepository
            null!,            // operationRepository
            audit,            // auditService
            null!,            // apiCallLogService
            new ExcelCashFlowParser(NullLogger<ExcelCashFlowParser>.Instance), // excelCashFlowParser — real
            null!,            // billingOptions
            null!,            // tenantBillingConfigService
            null!,            // userApprovalLimitRepository
            null!,            // decisionNotifier
            null!,            // approvalQuorumService — this suite never reaches a quorum vote
            new Api.Web.FinanceIA.Tests.Legajo.FakeLegajoDocumentRepository(),
            null!,            // analysisPipeline
            null!,            // analysisJobQueue
            null!,            // analysisRunRegistry
            null!,            // analysisRunStateRepository
            personAnalyses ?? new Api.Web.FinanceIA.Tests.PersonAnalysis.StubPersonAnalysisRepository(),
            NullLogger<AnalysisController>.Instance);

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("uid", FirebaseUid) }, "Test"))
        };
        sut.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return (sut, audit, chatRepo);
    }

    private static (int Status, string Message) ReadValidationError(IActionResult response)
    {
        var objectResult = Assert.IsType<ObjectResult>(response);
        var problem      = Assert.IsType<ProblemDetails>(objectResult.Value);

        Assert.Equal("validation_error", problem.Extensions["errorCode"]);

        var errors  = Assert.IsAssignableFrom<System.Collections.IEnumerable>(problem.Extensions["errors"]);
        var first   = errors.Cast<object>().Single();
        var message = first.GetType().GetProperty("message")!.GetValue(first) as string;

        return (objectResult.StatusCode ?? 0, message ?? string.Empty);
    }

    // ── Persona humana analysis (DDL 072) ────────────────────────────────────

    [Fact]
    public async Task ChatAttachmentAsync_on_a_persona_analysis_is_409_and_parses_nothing()
    {
        var personAnalyses = new Api.Web.FinanceIA.Tests.PersonAnalysis.StubPersonAnalysisRepository();
        personAnalyses.Kinds[AnalysisId] = AnalysisKinds.PersonaHumana;
        var (sut, audit, _) = BuildSut(personAnalyses: personAnalyses);
        var file = new FakeFormFile(XlsxBytes(), "flujo.xlsx", XlsxMime);

        var response = await sut.ChatAttachmentAsync(ResultId, file, CancellationToken.None);

        var problem = Assert.IsType<ProblemDetails>(Assert.IsAssignableFrom<ObjectResult>(response).Value);
        Assert.Equal(409, problem.Status);
        Assert.Equal("chat_unavailable_for_person_analysis", problem.Extensions["errorCode"]);
        Assert.Empty(audit.Entries);
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChatAttachmentAsync_returns_the_workbook_as_markdown_with_its_counts()
    {
        var (sut, _, _) = BuildSut();
        var file = new FakeFormFile(XlsxBytes(), "flujo.xlsx", XlsxMime);

        var response = await sut.ChatAttachmentAsync(ResultId, file, CancellationToken.None);

        var body = Assert.IsType<ChatAttachmentResponse>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.Equal(1, body.SheetCount);
        Assert.Equal(2, body.RowCount);
        Assert.Equal(4, body.CellCount);
        Assert.False(body.Truncated);
        Assert.Null(body.Note);
        Assert.Contains("| Cobranzas | 1500 |", body.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatAttachmentAsync_audits_the_file_identity_and_never_its_contents()
    {
        var (sut, audit, _) = BuildSut();
        var bytes = XlsxBytes();
        var file  = new FakeFormFile(bytes, "flujo.xlsx", XlsxMime);

        await sut.ChatAttachmentAsync(ResultId, file, CancellationToken.None);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal("Analysis.ChatAttachment.Parsed", entry.Action);
        Assert.Equal(UserId, entry.UserId);
        Assert.Equal("AnalysisResult", entry.EntityType);
        Assert.Equal(ResultId.ToString(), entry.EntityId);

        var json   = JsonDocument.Parse(entry.NewValueJson!).RootElement;
        var sha256 = json.GetProperty("sha256").GetString()!;
        Assert.Equal("flujo.xlsx", json.GetProperty("fileName").GetString());
        Assert.Equal(64, sha256.Length); // lowercase hex SHA-256
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), sha256);
        Assert.Equal(1, json.GetProperty("sheetCount").GetInt32());
        Assert.False(json.GetProperty("truncated").GetBoolean());

        // The point of the whole record: it says WHAT was attached, never what it said.
        // sha256 is left out of this scan: ClosedXML stamps the build time into docProps, so the
        // hash changes every run and its 64 hex chars contain any 4-digit run like "1500" about
        // once in a thousand runs. It is pinned above to the hash of the uploaded bytes, so it
        // cannot be carrying the contents and leaving it out hides nothing.
        var auditWithoutHash = JsonSerializer.Serialize(
            json.EnumerateObject()
                .Where(p => p.Name != "sha256")
                .ToDictionary(p => p.Name, p => p.Value));
        Assert.DoesNotContain("Cobranzas", auditWithoutHash, StringComparison.Ordinal);
        Assert.DoesNotContain("1500", auditWithoutHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatAttachmentAsync_persists_nothing_to_the_conversation()
    {
        // Ephemeral is the whole design: the parse is decoupled from the chat call, so a successful
        // attachment must leave the conversation exactly as it found it.
        var (sut, _, chat) = BuildSut();
        var file = new FakeFormFile(XlsxBytes(), "flujo.xlsx", XlsxMime);

        await sut.ChatAttachmentAsync(ResultId, file, CancellationToken.None);

        Assert.Empty(chat.Messages);
    }

    // ── Rejections ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChatAttachmentAsync_rejects_an_empty_file()
    {
        var (sut, audit, _) = BuildSut();
        var file = new FakeFormFile([], "vacio.xlsx", XlsxMime);

        var (status, message) = ReadValidationError(
            await sut.ChatAttachmentAsync(ResultId, file, CancellationToken.None));

        Assert.Equal(400, status);
        Assert.Equal("Se requiere un archivo no vacío.", message);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task ChatAttachmentAsync_rejects_a_file_over_the_size_cap()
    {
        // Declared length only — the point is that the cap is checked before anything is read.
        var (sut, _, _) = BuildSut();
        var file = new FakeFormFile(
            XlsxBytes(), "grande.xlsx", XlsxMime,
            declaredLength: ExcelCashFlowParser.MaxFileSizeBytes + 1);

        var (status, message) = ReadValidationError(
            await sut.ChatAttachmentAsync(ResultId, file, CancellationToken.None));

        Assert.Equal(400, status);
        Assert.Contains("5 MB", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatAttachmentAsync_rejects_a_declared_content_type_that_is_not_xlsx()
    {
        var (sut, _, _) = BuildSut();
        var file = new FakeFormFile(XlsxBytes(), "flujo.xlsx", "application/pdf");

        var (status, message) = ReadValidationError(
            await sut.ChatAttachmentAsync(ResultId, file, CancellationToken.None));

        Assert.Equal(400, status);
        Assert.Equal("Solo se aceptan archivos .xlsx.", message);
    }

    [Fact]
    public async Task ChatAttachmentAsync_rejects_a_spoofed_content_type_by_reading_the_bytes()
    {
        // Correct header, wrong file: the declared Content-Type is trivially forged, so the bytes
        // decide. A PDF carries no ZIP header at offset 0 and dies at the signature gate.
        var (sut, _, _) = BuildSut();
        var pdf  = Encoding.ASCII.GetBytes("%PDF-1.7\nnot a spreadsheet");
        var file = new FakeFormFile(pdf, "flujo.xlsx", XlsxMime);

        var (status, message) = ReadValidationError(
            await sut.ChatAttachmentAsync(ResultId, file, CancellationToken.None));

        Assert.Equal(400, status);
        Assert.Equal("El archivo no es un .xlsx válido.", message);
    }

    [Fact]
    public async Task ChatAttachmentAsync_rejects_a_docx_that_passes_the_signature_gate()
    {
        // The reason two gates exist. A .docx is a ZIP, so its leading bytes are byte-for-byte what
        // an .xlsx opens with and the signature check cannot tell them apart. Only opening the
        // package as a spreadsheet does.
        var (sut, audit, _) = BuildSut();
        var file = new FakeFormFile(DocxBytes(), "flujo.xlsx", XlsxMime);

        var (status, message) = ReadValidationError(
            await sut.ChatAttachmentAsync(ResultId, file, CancellationToken.None));

        Assert.Equal(400, status);
        Assert.Equal("El archivo no es un libro de Excel (.xlsx) válido.", message);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task ChatAttachmentAsync_rejects_a_workbook_with_no_data()
    {
        var (sut, _, _) = BuildSut();

        byte[] bytes;
        using (var wb = new XLWorkbook())
        {
            wb.AddWorksheet("Vacía");
            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            bytes = ms.ToArray();
        }

        var (status, message) = ReadValidationError(
            await sut.ChatAttachmentAsync(ResultId, new FakeFormFile(bytes, "v.xlsx", XlsxMime), CancellationToken.None));

        Assert.Equal(400, status);
        Assert.Equal("El archivo no contiene ninguna hoja con datos.", message);
    }

    // ── Isolation ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChatAttachmentAsync_returns_403_when_the_result_is_not_this_tenants()
    {
        // 403 rather than 404, matching every other path on this controller: a 404 would confirm
        // which result ids exist.
        var (sut, audit, _) = BuildSut(resultExists: false);
        var file = new FakeFormFile(XlsxBytes(), "flujo.xlsx", XlsxMime);

        var response = await sut.ChatAttachmentAsync(Guid.NewGuid(), file, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(response);
        Assert.Equal(403, objectResult.StatusCode);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task ChatAttachmentAsync_returns_401_when_the_caller_cannot_be_resolved()
    {
        var (sut, audit, _) = BuildSut(userExists: false);
        var file = new FakeFormFile(XlsxBytes(), "flujo.xlsx", XlsxMime);

        var response = await sut.ChatAttachmentAsync(ResultId, file, CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(response);
        Assert.Empty(audit.Entries);
    }
}
