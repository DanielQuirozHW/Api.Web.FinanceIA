// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-03

using System.Security.Claims;
using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Covers <see cref="AnalysisController.GetMessageAttachmentAsync"/> — the on-demand fetch that
/// serves the Markdown stored on one chat turn (scripts 055 and 056).
///
/// <para>
/// It exists because the conversation-history endpoint deliberately does NOT carry the Markdown:
/// that endpoint returns every turn and one stored attachment can be 200.000 characters, so it
/// reports <c>hasAttachment</c> and <c>attachmentFileName</c> and leaves the content to this route.
/// <c>AnalysisControllerChatHistoryTests</c> pins that half; this file pins the other.
/// </para>
///
/// <para>
/// The load-bearing tests here are the ACCESS ones. This is the only route in the API that returns a
/// stored attachment, and the attachment is the one payload the PII carve-out in CLAUDE.md scopes —
/// a cash-flow sheet usually carries the company name or CUIT in a title row. So the ownership chain
/// is asserted at every link: the result must belong to the tenant, the conversation to the result,
/// and the message to the conversation AND the tenant. A miss at any link must not reveal that the
/// row exists.
/// </para>
///
/// <para>
/// Hand-rolled private sealed stubs, mirroring <c>AnalysisControllerChatHistoryTests</c>: only the
/// dependencies this read path touches are wired, the rest of the ctor arguments are null! so a new
/// dependency in the path fails loudly rather than being quietly tolerated.
/// </para>
/// </summary>
public sealed class AnalysisControllerChatAttachmentFetchTests
{
    private const string Tenant      = "tenant-1";
    private const string FirebaseUid = "uid-1";
    private const string Markdown    = "## Flujo\n\n| Mes | Monto |\n| --- | --- |\n| Enero | 1500 |\n";
    private const string FileName    = "flujo-2026.xlsx";

    private const long MessageId = 4_100_200_300L;

    private static readonly Guid ResultId       = Guid.NewGuid();
    private static readonly Guid AnalysisId     = Guid.NewGuid();
    private static readonly Guid ConversationId = Guid.NewGuid();

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
    /// Holds a single stored turn and answers only for the exact (id, conversation, tenant) triple,
    /// mirroring the production predicate. Returning the row for a partial match would make the
    /// tenant-isolation tests below pass against a repository that does not isolate.
    /// </summary>
    private sealed class StubChatRepository : IChatRepository
    {
        public ChatMessage? Stored { get; set; }

        /// <summary>Null means "this conversation does not exist for this tenant".</summary>
        public ChatConversation? Conversation { get; set; } = new()
        {
            ConversationId = ConversationId,
            ResultId       = ResultId,
            AnalysisId     = AnalysisId,
            TenantId       = Tenant
        };

        public Task<ChatConversation?> GetConversationByIdForTenantAsync(Guid conversationId, string tenantId, CancellationToken ct)
            => Task.FromResult(Conversation is not null
                               && Conversation.ConversationId == conversationId
                               && Conversation.TenantId == tenantId
                                   ? Conversation
                                   : null);

        public Task<ChatMessage?> GetMessageByIdAsync(long chatMessageId, Guid conversationId, string tenantId, CancellationToken ct)
            => Task.FromResult(Stored is not null
                               && Stored.ChatMessageId == chatMessageId
                               && Stored.ConversationId == conversationId
                               && Stored.TenantId == tenantId
                                   ? Stored
                                   : null);

        public Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(Guid conversationId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<ChatConversation> CreateConversationAsync(ChatConversation conversation, CancellationToken ct) => throw new NotImplementedException();
        public Task<ChatConversation?> GetConversationByIdAsync(Guid conversationId, string tenantId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<ChatMessage> AddMessageAsync(ChatMessage message, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ChatConversation>> GetConversationsByResultAsync(Guid resultId, string tenantId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ChatConversation>> GetConversationsByResultForTenantAsync(Guid resultId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> RenameConversationAsync(Guid conversationId, string tenantId, Guid userId, string newTitle, CancellationToken ct) => throw new NotImplementedException();
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static TenantContext BuildTenant() => new()
    {
        TenantId               = Tenant,
        FirebaseProjectId      = "proj",
        FirebaseCredentialJson = "{}",
        RecaptchaSecretKey     = "secret"
    };

    /// <summary>The stored user turn as ChatAsync persists one.</summary>
    private static ChatMessage StoredTurn(
        string? markdown = Markdown, string? fileName = FileName, Guid? conversationId = null) => new()
    {
        ChatMessageId      = MessageId,
        ConversationId     = conversationId ?? ConversationId,
        TenantId           = Tenant,
        Role               = "user",
        Content            = "¿Cierra el flujo de fondos?",
        AttachmentMarkdown = markdown,
        AttachmentFileName = fileName
    };

    private static (AnalysisController Sut, StubChatRepository Chat) BuildSut(bool resultExists = true)
    {
        var chatRepo = new StubChatRepository();

        var resultRepo = new StubAnalysisResultRepository
        {
            ResultToReturn = resultExists
                ? new AnalysisResult { ResultId = ResultId, AnalysisId = AnalysisId, TenantId = Tenant }
                : null
        };

        var userRepo = new StubUserRepository
        {
            UserToReturn = new User { UserId = Guid.NewGuid(), DisplayName = "Analista" }
        };

        var sut = new AnalysisController(
            new StubTenantContextAccessor { TenantContext = BuildTenant() }, // tenantContextAccessor
            null!,            // documentRepository
            resultRepo,       // analysisResultRepository
            null!,            // analysisRepository
            null!,            // claudeService
            null!,            // cashFlowReconciliationService
            null!,            // cashFlowNarrativeService
            null!,            // alertRatioReconciliationService
            null!,            // entityRepository
            userRepo,         // userRepository
            chatRepo,         // chatRepository
            null!,            // creditLineRepository
            null!,            // operationRepository
            null!,            // auditService
            null!,            // apiCallLogService
            null!,            // excelCashFlowParser
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
            new Api.Web.FinanceIA.Tests.PersonAnalysis.StubPersonAnalysisRepository(),
            NullLogger<AnalysisController>.Instance);

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("uid", FirebaseUid) }, "Test"))
        };
        sut.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return (sut, chatRepo);
    }

    private static Task<IActionResult> FetchAsync(
        AnalysisController sut, Guid? resultId = null, Guid? conversationId = null, long? messageId = null)
        => sut.GetMessageAttachmentAsync(
            resultId ?? ResultId,
            conversationId ?? ConversationId,
            messageId ?? MessageId,
            CancellationToken.None);

    private static (int Status, string Detail) ReadProblem(IActionResult response)
    {
        var objectResult = Assert.IsType<ObjectResult>(response);
        var problem      = Assert.IsType<ProblemDetails>(objectResult.Value);

        return (objectResult.StatusCode ?? 0, problem.Detail ?? string.Empty);
    }

    // ── The happy path ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetMessageAttachmentAsync_returns_the_stored_markdown_and_its_name()
    {
        var (sut, chat) = BuildSut();
        chat.Stored = StoredTurn();

        var payload = Assert.IsType<ChatAttachmentContentResponse>(
            Assert.IsType<OkObjectResult>(await FetchAsync(sut)).Value);

        Assert.Equal(MessageId, payload.MessageId);
        Assert.Equal(FileName,  payload.FileName);
        Assert.Equal(Markdown,  payload.Markdown);
    }

    [Fact]
    public async Task GetMessageAttachmentAsync_serves_a_turn_stored_before_the_name_column_existed()
    {
        // The forward-only gap between the 055 and 056 deploys: Markdown kept, never labelled. The
        // viewer must still work — a null name is not a missing attachment.
        var (sut, chat) = BuildSut();
        chat.Stored = StoredTurn(fileName: null);

        var payload = Assert.IsType<ChatAttachmentContentResponse>(
            Assert.IsType<OkObjectResult>(await FetchAsync(sut)).Value);

        Assert.Null(payload.FileName);
        Assert.Equal(Markdown, payload.Markdown);
    }

    [Fact]
    public void The_payload_serializes_the_names_the_portal_reads()
    {
        // Program.cs registers converters but never a PropertyNamingPolicy, so the web default
        // (camelCase) is the wire contract — the same rule the history shape follows.
        var json = JsonSerializer.Serialize(
            new ChatAttachmentContentResponse { MessageId = MessageId, FileName = FileName, Markdown = Markdown },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"messageId\":4100200300", json, StringComparison.Ordinal);
        Assert.Contains("\"fileName\":\"flujo-2026.xlsx\"", json, StringComparison.Ordinal);
        Assert.Contains("\"markdown\":", json, StringComparison.Ordinal);
    }

    // ── 404: nothing to serve ─────────────────────────────────────────────────

    [Fact]
    public async Task GetMessageAttachmentAsync_404s_when_the_message_carries_no_attachment()
    {
        // The overwhelming majority of turns. NULL in the column is not an empty document.
        var (sut, chat) = BuildSut();
        chat.Stored = StoredTurn(markdown: null, fileName: null);

        var (status, detail) = ReadProblem(await FetchAsync(sut));

        Assert.Equal(404, status);
        Assert.Equal("No se encontró el adjunto de este mensaje.", detail);
    }

    [Fact]
    public async Task GetMessageAttachmentAsync_404s_for_an_unknown_message_id()
    {
        var (sut, chat) = BuildSut();
        chat.Stored = StoredTurn();

        var (status, detail) = ReadProblem(await FetchAsync(sut, messageId: MessageId + 1));

        Assert.Equal(404, status);

        // The SAME shape as "carries no attachment" above, deliberately: two different answers would
        // turn this route into a probe for which message ids exist.
        Assert.Equal("No se encontró el adjunto de este mensaje.", detail);
    }

    [Fact]
    public async Task GetMessageAttachmentAsync_404s_for_a_message_of_another_conversation()
    {
        // The id is a global BIGINT identity, not per-conversation, so a valid id from a DIFFERENT
        // conversation of the same tenant is the realistic mix-up. The repository predicate is scoped
        // by conversation precisely so it never resolves.
        var (sut, chat) = BuildSut();
        chat.Stored = StoredTurn(conversationId: Guid.NewGuid());

        Assert.Equal(404, ReadProblem(await FetchAsync(sut)).Status);
    }

    // ── 403: the ownership chain ──────────────────────────────────────────────

    [Fact]
    public async Task GetMessageAttachmentAsync_403s_when_the_result_belongs_to_another_tenant()
    {
        var (sut, chat) = BuildSut(resultExists: false);
        chat.Stored = StoredTurn();

        var (status, detail) = ReadProblem(await FetchAsync(sut));

        Assert.Equal(403, status);
        Assert.Equal("No se encontró el resultado del análisis o no pertenece a este tenant.", detail);
    }

    [Fact]
    public async Task GetMessageAttachmentAsync_403s_when_the_conversation_belongs_to_another_tenant()
    {
        var (sut, chat) = BuildSut();
        chat.Stored       = StoredTurn();
        chat.Conversation = null;

        var (status, detail) = ReadProblem(await FetchAsync(sut));

        Assert.Equal(403, status);
        Assert.Equal("No se encontró la conversación o no pertenece a este análisis.", detail);
    }

    [Fact]
    public async Task GetMessageAttachmentAsync_403s_when_the_conversation_belongs_to_another_analysis()
    {
        // A conversation of this tenant, but hanging off a different result. Reaching an attachment
        // through the wrong analysis would cross the boundary the Portal's routing assumes.
        var (sut, chat) = BuildSut();
        chat.Stored = StoredTurn();
        chat.Conversation = new ChatConversation
        {
            ConversationId = ConversationId,
            ResultId       = Guid.NewGuid(),
            AnalysisId     = AnalysisId,
            TenantId       = Tenant
        };

        var (status, detail) = ReadProblem(await FetchAsync(sut));

        Assert.Equal(403, status);
        Assert.Equal("No se encontró la conversación o no pertenece a este análisis.", detail);
    }

    [Fact]
    public async Task GetMessageAttachmentAsync_401s_when_the_caller_resolves_to_no_user()
    {
        var (sut, chat) = BuildSut();
        chat.Stored = StoredTurn();

        // A principal with no uid claim at all: ResolveCurrentUserAsync answers null before any
        // repository is reached.
        sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity())
            }
        };

        Assert.IsType<UnauthorizedResult>(await FetchAsync(sut));
    }

    // ── The attachment never leaks through a denial ───────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetMessageAttachmentAsync_never_puts_the_attachment_in_a_denial(bool resultExists)
    {
        // A 403 or 404 says what failed, never what was stored. The attachment is the one payload the
        // PII carve-out scopes, so it must not ride an error body any more than it rides a log line.
        var (sut, chat) = BuildSut(resultExists);
        chat.Stored = StoredTurn();
        if (resultExists) chat.Conversation = null;

        var response = await FetchAsync(sut);

        var json = JsonSerializer.Serialize(
            Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(response).Value),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.DoesNotContain("Enero", json, StringComparison.Ordinal);
        Assert.DoesNotContain(FileName, json, StringComparison.Ordinal);
    }
}
