// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-02

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
/// Covers the token counts on the GET history shape returned by
/// <see cref="AnalysisController.GetConversationMessagesAsync"/>.
///
/// <para>
/// The two chat shapes have to agree. POST returns <see cref="ChatResponse"/> with the input_tokens
/// Anthropic billed on that turn, so the Portal can draw its context ring while the analyst types. But
/// the Portal also draws that ring when an EXISTING conversation is reopened, and until now the history
/// shape dropped the counts even though ChatMessages carries them and the context-budget gate already
/// reads them. A history message with no count is not "zero context used" — it blanks the ring.
/// </para>
///
/// <para>
/// So what is asserted here is the pair: the counts survive the projection, AND a turn that never had
/// one (every user turn, plus assistant rows predating the column) comes back NULL rather than 0. The
/// difference is the whole contract — 0 is a legible ring reading and would be a lie.
/// </para>
///
/// <para>
/// Script 055 adds the same kind of contract for the attachment: <c>hasAttachment</c> is a MARKER,
/// and the stored Markdown must never appear on this shape. One attachment can be 200.000 characters
/// and this endpoint returns every turn of the conversation, so shipping it here would make the
/// payload unbounded — the opposite of the trimming this file's other tests exist to defend.
/// </para>
/// </summary>
public sealed class AnalysisControllerChatHistoryTests
{
    private const string Tenant      = "tenant-1";
    private const string FirebaseUid = "uid-1";
    private const string Attachment  = "## Flujo\n\n| Mes | Monto |\n| --- | --- |\n| Enero | 1500 |\n";

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
    /// The history endpoint reads the conversation TENANT-scoped (any analyst of the tenant may open
    /// it, regardless of author), so this stub implements the ForTenant overload — not the per-user one.
    /// </summary>
    private sealed class StubChatRepository : IChatRepository
    {
        public List<ChatMessage> Seeded { get; } = new();

        public Task<ChatConversation?> GetConversationByIdForTenantAsync(Guid conversationId, string tenantId, CancellationToken ct)
            => Task.FromResult<ChatConversation?>(new ChatConversation
            {
                ConversationId = conversationId,
                ResultId       = ResultId,
                AnalysisId     = AnalysisId,
                TenantId       = tenantId
            });

        public Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(Guid conversationId, string tenantId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ChatMessage>>(Seeded);

        public Task<ChatConversation> CreateConversationAsync(ChatConversation conversation, CancellationToken ct) => throw new NotImplementedException();
        public Task<ChatConversation?> GetConversationByIdAsync(Guid conversationId, string tenantId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<ChatMessage> AddMessageAsync(ChatMessage message, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ChatConversation>> GetConversationsByResultAsync(Guid resultId, string tenantId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ChatConversation>> GetConversationsByResultForTenantAsync(Guid resultId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> RenameConversationAsync(Guid conversationId, string tenantId, Guid userId, string newTitle, CancellationToken ct) => throw new NotImplementedException();
        public Task<ChatMessage?> GetMessageByIdAsync(long chatMessageId, Guid conversationId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static TenantContext BuildTenant() => new()
    {
        TenantId               = Tenant,
        FirebaseProjectId      = "proj",
        FirebaseCredentialJson = "{}",
        RecaptchaSecretKey     = "secret"
    };

    /// <summary>A user turn as ChatAsync persists one: no token counts, ever.</summary>
    private static ChatMessage UserTurn(
        string content, DateTime createdAt,
        string? attachment = null, string? fileName = null, long chatMessageId = 0) =>
        new()
        {
            ChatMessageId      = chatMessageId,
            Role               = "user",
            Content            = content,
            CreatedAt          = createdAt,
            AttachmentMarkdown = attachment,
            AttachmentFileName = fileName
        };

    /// <summary>An assistant turn as ChatAsync persists one: the billed counts included.</summary>
    private static ChatMessage AssistantTurn(
        string content, int? inputTokens, int? outputTokens, DateTime createdAt, long chatMessageId = 0) =>
        new()
        {
            ChatMessageId = chatMessageId,
            Role         = "assistant",
            Content      = content,
            InputTokens  = inputTokens,
            OutputTokens = outputTokens,
            CreatedAt    = createdAt
        };

    /// <summary>
    /// Only the four collaborators this endpoint actually reaches are wired; the rest stay null! so a
    /// silent new dependency in the read path fails loudly rather than being quietly tolerated.
    /// </summary>
    private static (AnalysisController Sut, StubChatRepository Chat) BuildSut()
    {
        var chatRepo = new StubChatRepository();

        var resultRepo = new StubAnalysisResultRepository
        {
            ResultToReturn = new AnalysisResult
            {
                ResultId   = ResultId,
                AnalysisId = AnalysisId,
                TenantId   = Tenant
            }
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

    private static async Task<ChatHistoryResponse> GetHistoryAsync(AnalysisController sut)
    {
        var response = await sut.GetConversationMessagesAsync(ResultId, ConversationId, CancellationToken.None);

        return Assert.IsType<ChatHistoryResponse>(Assert.IsType<OkObjectResult>(response).Value);
    }

    // ── The counts survive the projection ─────────────────────────────────────

    [Fact]
    public async Task GetConversationMessagesAsync_returns_the_token_counts_of_a_billed_assistant_turn()
    {
        var (sut, chat) = BuildSut();
        chat.Seeded.Add(AssistantTurn("respuesta", 12_345, 678, new DateTime(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc)));

        var history = await GetHistoryAsync(sut);

        var message = Assert.Single(history.Messages);
        Assert.Equal(12_345, message.InputTokens);
        Assert.Equal(678,    message.OutputTokens);
    }

    [Fact]
    public async Task GetConversationMessagesAsync_leaves_a_turn_with_no_counts_null_rather_than_zero()
    {
        // User turns never carry a count, and assistant rows predating the column do not either. Zero
        // is a legible ring reading — emitting it would report "no context used" for a full transcript.
        var (sut, chat) = BuildSut();
        chat.Seeded.Add(UserTurn("¿Cómo viene?", new DateTime(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc)));
        chat.Seeded.Add(AssistantTurn("respuesta vieja", null, null, new DateTime(2026, 9, 2, 10, 0, 1, DateTimeKind.Utc)));

        var history = await GetHistoryAsync(sut);

        Assert.Equal(2, history.Messages.Count);
        Assert.All(history.Messages, m =>
        {
            Assert.Null(m.InputTokens);
            Assert.Null(m.OutputTokens);
        });
    }

    [Fact]
    public async Task GetConversationMessagesAsync_keeps_the_existing_fields_and_the_transcript_order()
    {
        // The counts are ADDITIVE: everything the Portal already renders has to come back unchanged.
        var askedAt    = new DateTime(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc);
        var answeredAt = new DateTime(2026, 9, 2, 10, 0, 5, DateTimeKind.Utc);

        var (sut, chat) = BuildSut();
        chat.Seeded.Add(UserTurn("¿Y la deuda?", askedAt));
        chat.Seeded.Add(AssistantTurn("La deuda es baja.", 900, 40, answeredAt));

        var history = await GetHistoryAsync(sut);

        Assert.Equal(ConversationId, history.ConversationId);
        Assert.Equal(2, history.Messages.Count);

        Assert.Equal("user",         history.Messages[0].Role);
        Assert.Equal("¿Y la deuda?", history.Messages[0].Content);
        Assert.Equal(askedAt,        history.Messages[0].CreatedAt);
        Assert.Null(history.Messages[0].InputTokens);

        Assert.Equal("assistant",         history.Messages[1].Role);
        Assert.Equal("La deuda es baja.", history.Messages[1].Content);
        Assert.Equal(answeredAt,          history.Messages[1].CreatedAt);
        Assert.Equal(900, history.Messages[1].InputTokens);
        Assert.Equal(40,  history.Messages[1].OutputTokens);
    }

    // ── The attachment marker, never the attachment (script 055) ──────────────

    [Fact]
    public async Task GetConversationMessagesAsync_marks_the_turn_that_carried_an_attachment()
    {
        var (sut, chat) = BuildSut();
        chat.Seeded.Add(UserTurn("¿Cierra el flujo?", new DateTime(2026, 9, 3, 10, 0, 0, DateTimeKind.Utc),
            attachment: Attachment));
        chat.Seeded.Add(AssistantTurn("Cierra.", 900, 40, new DateTime(2026, 9, 3, 10, 0, 1, DateTimeKind.Utc)));

        var history = await GetHistoryAsync(sut);

        Assert.True(history.Messages[0].HasAttachment);
        Assert.False(history.Messages[1].HasAttachment);
    }

    [Fact]
    public async Task GetConversationMessagesAsync_never_ships_the_attachment_markdown_itself()
    {
        // The marker is a marker. One stored attachment can be 200.000 characters and this endpoint
        // returns EVERY turn of the conversation, so putting the Markdown on this shape would make
        // the history payload unbounded — the opposite of the trimming the /context work did. It is
        // fetched on demand instead.
        var (sut, chat) = BuildSut();
        chat.Seeded.Add(UserTurn("¿Cierra el flujo?", new DateTime(2026, 9, 3, 10, 0, 0, DateTimeKind.Utc),
            attachment: Attachment, fileName: "flujo-2026.xlsx"));

        var history = await GetHistoryAsync(sut);

        var message = Assert.Single(history.Messages);
        Assert.Equal("¿Cierra el flujo?", message.Content);
        Assert.DoesNotContain("Enero", message.Content, StringComparison.Ordinal);

        // Nothing anywhere on the serialised payload, not just on Content.
        var json = JsonSerializer.Serialize(history, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("Enero", json, StringComparison.Ordinal);
        Assert.Contains("\"hasAttachment\":true", json, StringComparison.Ordinal);

        // Script 056: the NAME does ship, and is the only thing about the file that does. It is tens
        // of bytes against the Markdown's possible 200.000, and without it the marker above cannot
        // tell one version of a cash flow from the next.
        Assert.Contains("\"attachmentFileName\":\"flujo-2026.xlsx\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("| Mes |", json, StringComparison.Ordinal);
    }

    // ── The name and the handle to fetch the content (script 056) ─────────────

    [Fact]
    public async Task GetConversationMessagesAsync_returns_the_stored_attachment_file_name()
    {
        var (sut, chat) = BuildSut();
        chat.Seeded.Add(UserTurn("¿Cierra el flujo?", new DateTime(2026, 9, 3, 10, 0, 0, DateTimeKind.Utc),
            attachment: Attachment, fileName: "flujo-2026.xlsx"));
        chat.Seeded.Add(AssistantTurn("Cierra.", 900, 40, new DateTime(2026, 9, 3, 10, 0, 1, DateTimeKind.Utc)));

        var history = await GetHistoryAsync(sut);

        Assert.Equal("flujo-2026.xlsx", history.Messages[0].AttachmentFileName);
        Assert.Null(history.Messages[1].AttachmentFileName);
    }

    [Fact]
    public async Task GetConversationMessagesAsync_leaves_the_file_name_null_on_a_turn_that_carried_nothing()
    {
        var (sut, chat) = BuildSut();
        chat.Seeded.Add(UserTurn("¿Cómo viene?", new DateTime(2026, 9, 3, 10, 0, 0, DateTimeKind.Utc)));

        var history = await GetHistoryAsync(sut);

        var message = Assert.Single(history.Messages);
        Assert.False(message.HasAttachment);
        Assert.Null(message.AttachmentFileName);
    }

    [Fact]
    public async Task GetConversationMessagesAsync_serves_a_turn_stored_between_the_055_and_056_deploys()
    {
        // The forward-only gap: Markdown kept by 055, before anything recorded a name. The marker
        // must still be true — a null label is not a missing attachment.
        var (sut, chat) = BuildSut();
        chat.Seeded.Add(UserTurn("¿Cierra el flujo?", new DateTime(2026, 9, 3, 10, 0, 0, DateTimeKind.Utc),
            attachment: Attachment, fileName: null));

        var history = await GetHistoryAsync(sut);

        var message = Assert.Single(history.Messages);
        Assert.True(message.HasAttachment);
        Assert.Null(message.AttachmentFileName);
    }

    [Fact]
    public async Task GetConversationMessagesAsync_returns_the_message_id_the_attachment_endpoint_takes()
    {
        // Without this the Portal has a marker and a name but no handle: the on-demand fetch is keyed
        // by ChatMessageId, so the history list has to carry it.
        var (sut, chat) = BuildSut();
        chat.Seeded.Add(UserTurn("¿Cierra el flujo?", new DateTime(2026, 9, 3, 10, 0, 0, DateTimeKind.Utc),
            attachment: Attachment, fileName: "flujo-2026.xlsx", chatMessageId: 4_100_200_300L));
        chat.Seeded.Add(AssistantTurn("Cierra.", 900, 40, new DateTime(2026, 9, 3, 10, 0, 1, DateTimeKind.Utc),
            chatMessageId: 4_100_200_301L));

        var history = await GetHistoryAsync(sut);

        // A long, not an int: ChatMessageId is a BIGINT identity, and a value past int.MaxValue must
        // survive the projection rather than overflow it.
        Assert.Equal(4_100_200_300L, history.Messages[0].MessageId);
        Assert.Equal(4_100_200_301L, history.Messages[1].MessageId);

        var json = JsonSerializer.Serialize(history, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"messageId\":4100200300", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetConversationMessagesAsync_marks_an_ordinary_turn_as_carrying_nothing()
    {
        // NULL in the column is false on the wire — every turn written before script 055, and every
        // turn since that carried no file.
        var (sut, chat) = BuildSut();
        chat.Seeded.Add(UserTurn("¿Cómo viene?", new DateTime(2026, 9, 3, 10, 0, 0, DateTimeKind.Utc)));

        var history = await GetHistoryAsync(sut);

        Assert.False(Assert.Single(history.Messages).HasAttachment);
    }

    // ── The wire contract matches the POST shape ──────────────────────────────

    [Fact]
    public void The_history_message_serializes_the_counts_exactly_like_the_flat_chat_response()
    {
        // The Portal reads one ring off two endpoints, so the property names have to match on the wire,
        // not just in C#. Program.cs registers converters but never a PropertyNamingPolicy, so the web
        // default (camelCase) is what both shapes get.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var historyJson = JsonSerializer.Serialize(
            new ChatHistoryMessage
            {
                Role         = "assistant",
                Content      = "respuesta",
                InputTokens  = 12_345,
                OutputTokens = 678
            }, options);

        var chatJson = JsonSerializer.Serialize(
            new ChatResponse { Message = "respuesta", InputTokens = 12_345, OutputTokens = 678 },
            options);

        Assert.Contains("\"inputTokens\":12345",  historyJson);
        Assert.Contains("\"outputTokens\":678",   historyJson);
        Assert.Contains("\"inputTokens\":12345",  chatJson);
        Assert.Contains("\"outputTokens\":678",   chatJson);
    }

    [Fact]
    public void A_history_message_with_no_counts_serializes_them_as_null_not_zero()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var json = JsonSerializer.Serialize(
            new ChatHistoryMessage { Role = "user", Content = "¿Cómo viene?" }, options);

        Assert.Contains("\"inputTokens\":null",  json);
        Assert.Contains("\"outputTokens\":null", json);
        Assert.DoesNotContain("\"inputTokens\":0",  json);
        Assert.DoesNotContain("\"outputTokens\":0", json);
    }
}
