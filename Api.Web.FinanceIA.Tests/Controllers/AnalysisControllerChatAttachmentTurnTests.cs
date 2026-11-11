// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-28

using System.Security.Claims;
using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Api.Web.FinanceIA.Servicios.BalanceCorrections;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Covers what <see cref="AnalysisController.ChatAsync"/> does with an attachment that arrives on
/// the turn: it must reach the Claude call, it must be stored on the user turn, and it must not
/// touch anything else.
///
/// <para>
/// The load-bearing test is still the Content one. ChatMessages.Content is replayed verbatim as
/// history on every later turn, so writing the Markdown there would re-send the whole spreadsheet
/// for the rest of the conversation — and it would undo the payload trimming from the /context and
/// cheque work. Content must stay exactly what the analyst typed, which is why these tests assert
/// on the recorded ChatMessage rather than on the 200 the endpoint returns.
/// </para>
///
/// <para>
/// Script 055 added ChatMessages.AttachmentMarkdown so the analyst can read back what they attached
/// when they reopen the conversation. That is storage, NOT history: it is a different column from
/// Content, nothing rebuilds a prompt from it, and the tests below pin both halves — that the
/// Markdown lands there, and that Content and `history` are exactly as free of it as before.
/// </para>
///
/// <para>
/// Script 056 added ChatMessages.AttachmentFileName beside it, so the analyst can tell WHICH file a
/// turn carried. Its tests pin the invariant that makes the pair trustworthy — the name is derived
/// from the CAPPED Markdown, so the two are stored together or not at all — plus the leaf-name and
/// 255-character guards, and that the name reaches neither Content nor Claude. A file name is
/// exactly the kind of value that carries a company name, so that last one is the PII rule and not
/// only tidiness.
/// </para>
///
/// <para>
/// The second theme is the ceiling. /chat/attachment bounds what the PARSER can emit, but /chat
/// takes that Markdown back as an ordinary JSON string, so nothing stops a caller posting one the
/// parser never produced. The cap truncates rather than refusing — the parser applies no per-cell
/// length cap, so a legitimate workbook can exceed the ceiling and a 400 would dead-end an analyst
/// whose file the server had already accepted.
/// </para>
///
/// <para>
/// What the WIRE looks like is pinned separately in <c>ClaudeChatPayloadTests</c>, which captures
/// the serialised request body; this file stops at the IClaudeService boundary. Hand-rolled private
/// sealed stubs, mirroring <c>AnalysisControllerChatCashFlowTests</c>: only the dependencies the
/// chat path touches are stubbed, the rest of the ~23 ctor arguments are null!, and the argument
/// order mirrors the ctor signature.
/// </para>
/// </summary>
public sealed class AnalysisControllerChatAttachmentTurnTests
{
    private const string Tenant      = "tenant-1";
    private const string FirebaseUid = "uid-1";
    private const string Question    = "¿Cierra el flujo de fondos?";
    private const string Markdown    = "## Flujo\n\n| Mes | Monto |\n| --- | --- |\n| Enero | 1500 |\n";

    // The production ceiling. Duplicated because the constant is private to the controller — a
    // drift here shows up immediately as a failing boundary test rather than as silent coverage.
    private const int Ceiling = 200_000;

    private static readonly Guid ResultId   = Guid.NewGuid();
    private static readonly Guid AnalysisId = Guid.NewGuid();

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
    /// Records every persisted turn AND the created conversation, and unlike the cash-flow copy it
    /// can be SEEDED with prior turns — the attachment's ephemerality across turns is only
    /// observable if there is a history for it to be absent from.
    /// </summary>
    private sealed class RecordingChatRepository : IChatRepository
    {
        public List<ChatMessage> Messages { get; } = new();
        public List<ChatMessage> Seeded   { get; } = new();
        public ChatConversation? Created  { get; private set; }

        public Task<ChatConversation> CreateConversationAsync(ChatConversation conversation, CancellationToken ct)
        {
            conversation.ConversationId = Guid.NewGuid();
            Created = conversation;
            return Task.FromResult(conversation);
        }

        public Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(Guid conversationId, string tenantId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ChatMessage>>(Seeded);

        public Task<ChatMessage> AddMessageAsync(ChatMessage message, CancellationToken ct)
        {
            message.ChatMessageId = Messages.Count + 1;
            Messages.Add(message);
            return Task.FromResult(message);
        }

        public Task<IReadOnlyList<ChatConversation>> GetConversationsByResultAsync(Guid resultId, string tenantId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ChatConversation>> GetConversationsByResultForTenantAsync(Guid resultId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<ChatConversation?> GetConversationByIdAsync(Guid conversationId, string tenantId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<ChatConversation?> GetConversationByIdForTenantAsync(Guid conversationId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> RenameConversationAsync(Guid conversationId, string tenantId, Guid userId, string newTitle, CancellationToken ct) => throw new NotImplementedException();
        public Task<ChatMessage?> GetMessageByIdAsync(long chatMessageId, Guid conversationId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
    }

    /// <summary>
    /// Captures every argument the controller hands to Claude — the only way to assert the
    /// attachment reaches the LIVE call while being absent from everything that outlives it.
    /// </summary>
    private sealed class CapturingClaudeService : IClaudeService
    {
        public Task<BalanceSuggestionReply> SuggestBalanceCorrectionsAsync(
            byte[] pdfBytes, BalanceSuggestionQuestion question, CancellationToken ct)
            => throw new NotImplementedException();

        public string? CapturedUserMessage        { get; private set; }
        public string? CapturedAttachmentMarkdown { get; private set; }
        public string? CapturedSystemPrompt       { get; private set; }
        public IReadOnlyList<ConversationMessage>? CapturedHistory { get; private set; }

        public Task<ClaudeChatResult> ChatAsync(string model, string systemPrompt, IReadOnlyList<ConversationMessage> history, string userMessage, CancellationToken ct, string? attachmentMarkdown = null)
        {
            CapturedUserMessage        = userMessage;
            CapturedHistory            = history;
            CapturedAttachmentMarkdown = attachmentMarkdown;
            CapturedSystemPrompt       = systemPrompt;
            return Task.FromResult(new ClaudeChatResult("respuesta del modelo", 10, 20));
        }

        public Task<ClaudeAnalysisResult> AnalyzeDocumentAsync(byte[] pdfBytes, string documentType, int? fiscalYear, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<ClaudeNarrativeResult> GenerateScoringNarrativeAsync(ScoringResult scoring, RatiosData ratios, BcraData? bcraData, BcraMacroData? macroData, ScoreReliabilityResponse? reliability, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubApiCallLogService : IApiCallLogService
    {
        public Task LogAsync(
            string tenantId, string service, string endpoint, int? responseStatus,
            int? inputTokens, int? outputTokens, decimal? estimatedCostUsd, bool isSuccess,
            string? errorMessage, int durationMs, string? operation = null, string? model = null,
            Guid? userId = null, Guid? analysisId = null, long? chatMessageId = null, Guid? documentId = null,
            CancellationToken ct = default)
            => Task.CompletedTask;
    }

    /// <summary>
    /// The chat context gate resolves the tenant's plan through this. These tests seed no assistant
    /// turn carrying InputTokens, so the gate returns before it ever asks — but a real stub rather
    /// than null! keeps that a fact about the transcript instead of a NullReferenceException that
    /// the gate's fail-open catch would swallow silently.
    /// </summary>
    private sealed class StubBillingConfigService : ITenantBillingConfigService
    {
        public BillingPlanResolution Resolution { get; set; } =
            new(new BillingPlan("100 análisis", 100, 4_000_000L, 60_000, ChatModelCatalog.AllIds), BillingPlanSource.Configured);

        public Task<BillingPlanResolution> GetEffectiveAsync(string tenantId, CancellationToken ct)
            => Task.FromResult(Resolution);

        public Task<TenantBillingConfig> UpdateAsync(
            string tenantId, BillingPlan plan, bool isActive, string updatedBy, CancellationToken ct)
            => throw new NotSupportedException("The chat endpoint must not write billing configuration.");
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static TenantContext BuildTenant() => new()
    {
        TenantId               = Tenant,
        FirebaseProjectId      = "proj",
        FirebaseCredentialJson = "{}",
        RecaptchaSecretKey     = "secret"
    };

    private static (AnalysisController Sut, CapturingClaudeService Claude, RecordingChatRepository Chat) BuildSut()
    {
        var claude        = new CapturingClaudeService();
        var chatRepo      = new RecordingChatRepository();
        var billingConfig = new StubBillingConfigService();

        var resultRepo = new StubAnalysisResultRepository
        {
            // BalanceJson is deliberately absent: the cash-flow reading degrades to "No disponible"
            // without faulting (pinned in AnalysisControllerChatCashFlowTests), so nothing here
            // depends on it and these tests stay about the attachment.
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
            claude,           // claudeService
            new CashFlowReconciliationService(),  // cashFlowReconciliationService — real
            new CashFlowNarrativeService(),       // cashFlowNarrativeService — real
            null!,            // alertRatioReconciliationService
            new ChatEntityRepositoryStub(), // entityRepository (K5: the chat strips its identifiers)
            userRepo,         // userRepository
            chatRepo,         // chatRepository
            null!,            // creditLineRepository
            null!,            // operationRepository
            null!,            // auditService
            new StubApiCallLogService(), // apiCallLogService
            null!,            // excelCashFlowParser
            null!,            // billingOptions
            billingConfig,            // tenantBillingConfigService
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

        return (sut, claude, chatRepo);
    }

    private static ChatRequest Ask(string? attachment = null, string? fileName = null) => new()
    {
        Message            = Question,
        AttachmentMarkdown = attachment,
        AttachmentFileName = fileName
    };

    // ── Tests: the attachment never reaches Content ───────────────────────────

    [Fact]
    public async Task ChatAsync_persists_the_typed_message_only_and_never_the_attachment()
    {
        // THE test this whole change exists to satisfy. ChatMessages.Content is replayed as history
        // on every later turn, so a spreadsheet written here would be re-sent for the rest of the
        // conversation. Content must be the analyst's sentence and nothing else — script 055 stores
        // the Markdown in its own column precisely so this one can stay clean.
        var (sut, _, chatRepo) = BuildSut();

        var response = await sut.ChatAsync(ResultId, Ask(Markdown), CancellationToken.None);

        Assert.IsType<ChatResponse>(Assert.IsType<OkObjectResult>(response).Value);

        var userTurn = chatRepo.Messages[0];
        Assert.Equal("user", userTurn.Role);
        Assert.Equal(Question, userTurn.Content);
        Assert.DoesNotContain("Enero", userTurn.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("| Mes |", userTurn.Content, StringComparison.Ordinal);

        // And the conversation is otherwise intact — the assistant reply is still stored.
        Assert.Equal(2, chatRepo.Messages.Count);
        Assert.Equal("assistant", chatRepo.Messages[1].Role);
        Assert.Equal("respuesta del modelo", chatRepo.Messages[1].Content);
    }

    [Fact]
    public async Task ChatAsync_keeps_the_attachment_out_of_the_history_it_replays()
    {
        // Ephemerality across turns, not just within one. The history handed to Claude is rebuilt
        // from ChatMessages, so if the attachment ever reached the table it would reappear here.
        var (sut, claude, chatRepo) = BuildSut();
        chatRepo.Seeded.Add(new ChatMessage { Role = "user",      Content = "¿Y el flujo del año pasado?" });
        chatRepo.Seeded.Add(new ChatMessage { Role = "assistant", Content = "Fue positivo." });

        await sut.ChatAsync(ResultId, Ask(Markdown), CancellationToken.None);

        var history = Assert.IsAssignableFrom<IReadOnlyList<ConversationMessage>>(claude.CapturedHistory);
        Assert.Equal(2, history.Count);
        Assert.All(history, turn =>
            Assert.DoesNotContain("Enero", turn.Content, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChatAsync_titles_a_new_conversation_from_the_message_not_the_attachment()
    {
        // The title is derived from request.Message, and it is persisted. An attachment that leaked
        // into it would put spreadsheet content in the conversation list.
        var (sut, _, chatRepo) = BuildSut();

        await sut.ChatAsync(ResultId, Ask(Markdown), CancellationToken.None);

        var created = Assert.IsType<ChatConversation>(chatRepo.Created);
        Assert.Equal(Question, created.Title);
    }

    // ── Tests: the user turn stores the attachment (script 055) ───────────────

    [Fact]
    public async Task ChatAsync_persists_the_attachment_markdown_on_the_user_turn()
    {
        // The point of script 055. Without this column the analyst reopens the conversation and sees
        // a question with no visible subject, and a follow-up means re-uploading the same file.
        var (sut, _, chatRepo) = BuildSut();

        await sut.ChatAsync(ResultId, Ask(Markdown), CancellationToken.None);

        var userTurn = chatRepo.Messages[0];
        Assert.Equal("user", userTurn.Role);
        Assert.Equal(Markdown, userTurn.AttachmentMarkdown);

        // And Content is untouched by it — the two columns are the whole design. Content is what
        // history replays; the attachment column is not.
        Assert.Equal(Question, userTurn.Content);
    }

    [Fact]
    public async Task ChatAsync_persists_null_on_a_user_turn_with_no_attachment()
    {
        // NULL is the honest "no attachment", and it is what the overwhelming majority of turns
        // store. The column takes no default precisely so this stays distinguishable.
        var (sut, _, chatRepo) = BuildSut();

        await sut.ChatAsync(ResultId, Ask(), CancellationToken.None);

        Assert.All(chatRepo.Messages, m => Assert.Null(m.AttachmentMarkdown));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public async Task ChatAsync_persists_null_for_a_blank_attachment(string attachment)
    {
        // Same blank-is-absent rule the Claude call and the prompt flag already share, and it has to
        // be the same one: a Portal that always sends the field, empty when nothing was uploaded,
        // must not leave a row claiming an attachment that never existed.
        var (sut, _, chatRepo) = BuildSut();

        await sut.ChatAsync(ResultId, Ask(attachment), CancellationToken.None);

        Assert.All(chatRepo.Messages, m => Assert.Null(m.AttachmentMarkdown));
    }

    [Fact]
    public async Task ChatAsync_persists_the_capped_attachment_not_the_raw_one()
    {
        // The stored value is the SAME string Claude was handed, not request.AttachmentMarkdown.
        // Persisting the raw field would let a caller store an attachment larger than the one the
        // model was ever shown — a row the analyst reads back as authoritative that nothing in the
        // conversation ever saw.
        var (sut, claude, chatRepo) = BuildSut();
        var oversized = new string('x', 250_000);

        await sut.ChatAsync(ResultId, Ask(oversized), CancellationToken.None);

        var stored = Assert.IsType<string>(chatRepo.Messages[0].AttachmentMarkdown);
        Assert.Equal(claude.CapturedAttachmentMarkdown, stored);
        Assert.NotEqual(oversized, stored);
        Assert.EndsWith("filas que faltan.", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatAsync_never_stores_the_attachment_on_the_assistant_turn()
    {
        // The column belongs to the turn the upload arrived on. A value here would mean the
        // application wrote it in the wrong place — the mirror of the 053 assertion below, which
        // pins the billing flag to the assistant turn.
        var (sut, _, chatRepo) = BuildSut();

        await sut.ChatAsync(ResultId, Ask(Markdown), CancellationToken.None);

        var assistantTurn = chatRepo.Messages[1];
        Assert.Equal("assistant", assistantTurn.Role);
        Assert.Null(assistantTurn.AttachmentMarkdown);
    }

    [Fact]
    public async Task ChatAsync_stored_attachment_does_not_come_back_as_history()
    {
        // Stored is not replayed, and this is the assertion that says so. `history` is rebuilt from
        // Content alone, so a seeded PRIOR turn carrying a stored attachment must contribute its
        // sentence and nothing else to the next prompt.
        var (sut, claude, chatRepo) = BuildSut();
        chatRepo.Seeded.Add(new ChatMessage
        {
            Role               = "user",
            Content            = "¿Y el flujo del año pasado?",
            AttachmentMarkdown = Markdown
        });
        chatRepo.Seeded.Add(new ChatMessage { Role = "assistant", Content = "Fue positivo." });

        await sut.ChatAsync(ResultId, Ask(), CancellationToken.None);

        var history = Assert.IsAssignableFrom<IReadOnlyList<ConversationMessage>>(claude.CapturedHistory);
        Assert.All(history, turn =>
            Assert.DoesNotContain("Enero", turn.Content, StringComparison.Ordinal));

        // ...and this turn sent no attachment block of its own, so nothing reintroduced it.
        Assert.Null(claude.CapturedAttachmentMarkdown);
    }

    // ── Tests: the user turn stores the file name (script 056) ───────────────

    [Fact]
    public async Task ChatAsync_persists_the_attachment_file_name_beside_the_markdown()
    {
        // The point of script 056. Without it the analyst reopens the conversation, sees that SOME
        // spreadsheet rode this turn, and cannot tell which of the three versions of the cash flow
        // they tried it was.
        var (sut, _, chatRepo) = BuildSut();

        await sut.ChatAsync(ResultId, Ask(Markdown, "flujo-2026.xlsx"), CancellationToken.None);

        var userTurn = chatRepo.Messages[0];
        Assert.Equal("user", userTurn.Role);
        Assert.Equal("flujo-2026.xlsx", userTurn.AttachmentFileName);
        Assert.Equal(Markdown, userTurn.AttachmentMarkdown);

        // And it stayed a label: Content is still the analyst's sentence alone.
        Assert.Equal(Question, userTurn.Content);
        Assert.DoesNotContain("flujo-2026.xlsx", userTurn.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatAsync_persists_null_for_both_when_the_turn_carried_no_attachment()
    {
        var (sut, _, chatRepo) = BuildSut();

        await sut.ChatAsync(ResultId, Ask(), CancellationToken.None);

        Assert.All(chatRepo.Messages, m =>
        {
            Assert.Null(m.AttachmentMarkdown);
            Assert.Null(m.AttachmentFileName);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public async Task ChatAsync_does_not_store_a_file_name_for_a_blank_attachment(string attachment)
    {
        // THE INVARIANT the DDL's verification block checks: a name is never stored for content that
        // was not. The name is derived from the CAPPED Markdown, so a Portal that always sends the
        // file-name field cannot leave a row labelling a spreadsheet nothing kept.
        var (sut, _, chatRepo) = BuildSut();

        await sut.ChatAsync(ResultId, Ask(attachment, "flujo-2026.xlsx"), CancellationToken.None);

        Assert.All(chatRepo.Messages, m =>
        {
            Assert.Null(m.AttachmentMarkdown);
            Assert.Null(m.AttachmentFileName);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("planillas/")]
    public async Task ChatAsync_stores_null_when_the_attachment_has_no_usable_name(string fileName)
    {
        // The mirror case: content with no usable label. The Markdown is still kept — a nameless
        // attachment is exactly what every turn taken between the 055 and 056 deploys looks like, and
        // the viewer must still serve it. Note "planillas/" has a leaf of "", which is why the blank
        // test is applied AFTER taking the leaf and not only before.
        var (sut, _, chatRepo) = BuildSut();

        await sut.ChatAsync(ResultId, Ask(Markdown, fileName), CancellationToken.None);

        var userTurn = chatRepo.Messages[0];
        Assert.Equal(Markdown, userTurn.AttachmentMarkdown);
        Assert.Null(userTurn.AttachmentFileName);
    }

    [Theory]
    [InlineData("C:\\Users\\ana\\Documentos\\flujo-2026.xlsx")]
    [InlineData("/home/ana/documentos/flujo-2026.xlsx")]
    [InlineData("Documentos\\flujo-2026.xlsx")]
    public async Task ChatAsync_stores_only_the_leaf_of_a_file_name_carrying_a_path(string fileName)
    {
        // The browser sends a leaf name, but this field is ordinary JSON and nothing stops a client
        // posting a path. Storing one would put a caller-controlled directory in a column the Portal
        // renders back verbatim.
        //
        // BOTH separators, on every platform. These cases are the reason the controller does not use
        // Path.GetFileName: that follows the running platform, so the backslash cases would pass on
        // the Windows App Service and store the whole path on this ubuntu-latest CI runner — the
        // production behaviour would be exactly the one no test could see.
        var (sut, _, chatRepo) = BuildSut();

        await sut.ChatAsync(ResultId, Ask(Markdown, fileName), CancellationToken.None);

        Assert.Equal("flujo-2026.xlsx", chatRepo.Messages[0].AttachmentFileName);
    }

    [Fact]
    public async Task ChatAsync_trims_a_file_name_past_the_column_width_instead_of_refusing_the_turn()
    {
        // dbo.ChatMessages.AttachmentFileName is nvarchar(255). Sending the raw value would throw on
        // INSERT — on a turn whose question is about to cost a Claude call. Refusing it with a 400
        // would be worse: the file was already accepted and parsed, and the NAME is not why the
        // analyst asked. So it is cut, exactly like the Markdown it labels.
        var (sut, _, chatRepo) = BuildSut();
        var overlong = new string('n', 400) + ".xlsx";

        var response = await sut.ChatAsync(ResultId, Ask(Markdown, overlong), CancellationToken.None);

        Assert.IsType<ChatResponse>(Assert.IsType<OkObjectResult>(response).Value);

        var stored = Assert.IsType<string>(chatRepo.Messages[0].AttachmentFileName);
        Assert.Equal(255, stored.Length);
        Assert.EndsWith("…", stored, StringComparison.Ordinal);

        // The attachment itself is untouched by the name being long.
        Assert.Equal(Markdown, chatRepo.Messages[0].AttachmentMarkdown);
    }

    [Fact]
    public async Task ChatAsync_never_stores_the_file_name_on_the_assistant_turn()
    {
        // Same rule as the Markdown: the column belongs to the turn the upload arrived on.
        var (sut, _, chatRepo) = BuildSut();

        await sut.ChatAsync(ResultId, Ask(Markdown, "flujo-2026.xlsx"), CancellationToken.None);

        var assistantTurn = chatRepo.Messages[1];
        Assert.Equal("assistant", assistantTurn.Role);
        Assert.Null(assistantTurn.AttachmentFileName);
    }

    [Fact]
    public async Task ChatAsync_never_sends_the_file_name_to_claude()
    {
        // The name is a LABEL FOR THE ANALYST. The model is told a spreadsheet arrived, never what it
        // was called — and a file name is exactly the kind of value that carries a company name, so
        // this is the PII rule and not only tidiness.
        var (sut, claude, _) = BuildSut();

        await sut.ChatAsync(ResultId, Ask(Markdown, "Flujo ACME SRL 2026.xlsx"), CancellationToken.None);

        Assert.Equal(Markdown, claude.CapturedAttachmentMarkdown);
        Assert.Equal(Question, claude.CapturedUserMessage);
        Assert.DoesNotContain("ACME", claude.CapturedUserMessage!, StringComparison.Ordinal);
        Assert.DoesNotContain("ACME", claude.CapturedAttachmentMarkdown!, StringComparison.Ordinal);
        Assert.DoesNotContain("ACME",
            Assert.IsType<string>(claude.CapturedSystemPrompt), StringComparison.Ordinal);
    }

    // ── Tests: the assistant turn records that an attachment rode it ──────────

    [Fact]
    public async Task ChatAsync_flags_the_assistant_turn_that_carried_an_attachment()
    {
        // Script 053. The attachment stays out of the transcript, but the input_tokens Anthropic
        // billed for this turn counts it — and the per-conversation context gate reads that figure
        // on the NEXT turn. This one bit is what lets the gate know the number describes a prompt no
        // later turn will rebuild, so it skips the row instead of pinning the conversation on it.
        var (sut, _, chatRepo) = BuildSut();

        await sut.ChatAsync(ResultId, Ask(Markdown), CancellationToken.None);

        var assistantTurn = chatRepo.Messages[1];
        Assert.Equal("assistant", assistantTurn.Role);
        Assert.True(assistantTurn.HadEphemeralAttachment);

        // The USER turn is never flagged: it holds the attachment's Markdown (script 055), which is
        // not what the gate reads. A true here would mean the flag was written in the wrong place.
        Assert.False(chatRepo.Messages[0].HadEphemeralAttachment);
    }

    [Fact]
    public async Task ChatAsync_leaves_the_flag_false_on_a_turn_with_no_attachment()
    {
        // The ordinary turn, and the value every pre-053 row carries after the backfill: false means
        // "measurable", which is what the gate did with every row before the column existed.
        var (sut, _, chatRepo) = BuildSut();

        await sut.ChatAsync(ResultId, Ask(), CancellationToken.None);

        Assert.All(chatRepo.Messages, m => Assert.False(m.HadEphemeralAttachment));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public async Task ChatAsync_does_not_flag_the_turn_for_a_blank_attachment(string attachment)
    {
        // Same blank-is-absent rule as the Claude call below, and it has to be the same: a turn that
        // sent no attachment block was billed for no attachment block, so its count is measurable.
        // Flagging it would cost the gate a perfectly good measurement on every turn a Portal that
        // always sends the field takes.
        var (sut, _, chatRepo) = BuildSut();

        await sut.ChatAsync(ResultId, Ask(attachment), CancellationToken.None);

        Assert.All(chatRepo.Messages, m => Assert.False(m.HadEphemeralAttachment));
    }

    // ── Tests: the attachment does reach the live call ────────────────────────

    [Fact]
    public async Task ChatAsync_forwards_the_attachment_to_the_claude_service()
    {
        // The two must arrive as separate arguments. Merging them into one string would defeat the
        // block split that lets the model tell the question apart from the data.
        var (sut, claude, _) = BuildSut();

        await sut.ChatAsync(ResultId, Ask(Markdown), CancellationToken.None);

        Assert.Equal(Markdown, claude.CapturedAttachmentMarkdown);
        Assert.Equal(Question, claude.CapturedUserMessage);
    }

    [Fact]
    public async Task ChatAsync_without_an_attachment_passes_null_to_the_claude_service()
    {
        var (sut, claude, _) = BuildSut();

        await sut.ChatAsync(ResultId, Ask(), CancellationToken.None);

        Assert.Null(claude.CapturedAttachmentMarkdown);
        Assert.Equal(Question, claude.CapturedUserMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public async Task ChatAsync_treats_a_blank_attachment_as_absent(string attachment)
    {
        // A Portal that always sends the field, empty when nothing was uploaded, must not push the
        // turn onto the block path and start paying for the framing text.
        var (sut, claude, _) = BuildSut();

        var response = await sut.ChatAsync(ResultId, Ask(attachment), CancellationToken.None);

        Assert.IsType<OkObjectResult>(response);
        Assert.Null(claude.CapturedAttachmentMarkdown);
    }

    // ── Tests: the system prompt is told which turn this is ───────────────────
    //
    // The attachment is ephemeral, so on a follow-up turn the model holds nothing — and cannot tell
    // that anything is missing. Left uninformed it fabricates the spreadsheet: monthly disbursements,
    // dates, amounts, delivered as if read from the file. BuildChatSystemPrompt renders the two
    // branches (pinned in AnalysisControllerChatPromptTests); what these tests pin is that the flag
    // actually LEAVES the call site, which the DI-free builder tests structurally cannot show.

    private const string AttachmentPresentPrompt = "En ESTE turno el analista adjuntó una planilla";
    private const string AttachmentAbsentPrompt  = "En ESTE turno NO recibiste ninguna planilla adjunta.";

    [Fact]
    public async Task ChatAsync_tells_the_prompt_a_spreadsheet_arrived_on_this_turn()
    {
        var (sut, claude, _) = BuildSut();

        await sut.ChatAsync(ResultId, Ask(Markdown), CancellationToken.None);

        var prompt = Assert.IsType<string>(claude.CapturedSystemPrompt);
        Assert.Contains(AttachmentPresentPrompt,   prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(AttachmentAbsentPrompt, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatAsync_tells_the_prompt_no_spreadsheet_arrived_when_none_did()
    {
        var (sut, claude, _) = BuildSut();

        await sut.ChatAsync(ResultId, Ask(), CancellationToken.None);

        var prompt = Assert.IsType<string>(claude.CapturedSystemPrompt);
        Assert.Contains(AttachmentAbsentPrompt,      prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(AttachmentPresentPrompt, prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public async Task ChatAsync_treats_a_blank_attachment_as_absent_in_the_prompt_too(string attachment)
    {
        // The flag is derived with the SAME predicate ClaudeService uses to decide whether it emits
        // the attachment block. A blank attachment sends no block, so the prompt must not claim one
        // arrived — otherwise the model is told to read a spreadsheet that is not in its message.
        var (sut, claude, _) = BuildSut();

        await sut.ChatAsync(ResultId, Ask(attachment), CancellationToken.None);

        Assert.Null(claude.CapturedAttachmentMarkdown);
        Assert.Contains(AttachmentAbsentPrompt,
            Assert.IsType<string>(claude.CapturedSystemPrompt), StringComparison.Ordinal);
    }

    // ── Tests: the ceiling ────────────────────────────────────────────────────

    [Fact]
    public async Task ChatAsync_does_not_truncate_an_attachment_at_the_ceiling()
    {
        // The boundary. Exactly at the ceiling must pass through char for char — an off-by-one here
        // would silently append a truncation note to a file that was never actually cut.
        var (sut, claude, _) = BuildSut();
        var atCeiling = new string('x', Ceiling);

        await sut.ChatAsync(ResultId, Ask(atCeiling), CancellationToken.None);

        Assert.Equal(atCeiling, claude.CapturedAttachmentMarkdown);
    }

    [Fact]
    public async Task ChatAsync_truncates_an_attachment_past_the_ceiling_and_still_answers()
    {
        // Truncate, never refuse: the parser applies no per-cell cap, so /chat/attachment can
        // legitimately return Markdown above the ceiling and a 400 would dead-end an analyst whose
        // file the server had already accepted. The turn still completes, and the model is TOLD the
        // table is incomplete rather than left to reason over one that stops mid-row.
        var (sut, claude, chatRepo) = BuildSut();

        var response = await sut.ChatAsync(
            ResultId, Ask(new string('x', 250_000)), CancellationToken.None);

        Assert.IsType<ChatResponse>(Assert.IsType<OkObjectResult>(response).Value);

        var sent = Assert.IsType<string>(claude.CapturedAttachmentMarkdown);
        Assert.True(sent.Length > Ceiling && sent.Length < Ceiling + 500,
            $"expected the ceiling plus a short note, got {sent.Length} chars");
        Assert.EndsWith("filas que faltan.", sent, StringComparison.Ordinal);
        Assert.Contains("se truncó", sent, StringComparison.Ordinal);

        // Truncation is a degraded answer, never a lost turn.
        Assert.Equal(2, chatRepo.Messages.Count);
    }

    [Fact]
    public async Task ChatAsync_does_not_split_a_surrogate_pair_when_it_truncates()
    {
        // A cell can hold an emoji or a CJK glyph, and the parser never shortens a cell. Cutting
        // between the halves of a pair leaves a lone surrogate that lands in a JSON string one hop
        // later — a serialisation fault THERE is outside the ClaudeApiException catch, so it would
        // surface as a 500 with the analyst's turn already persisted and no assistant reply.
        var (sut, claude, _) = BuildSut();
        var straddling = new string('x', Ceiling - 1) + "\U0001F600" + new string('y', 5_000);

        await sut.ChatAsync(ResultId, Ask(straddling), CancellationToken.None);

        var sent = Assert.IsType<string>(claude.CapturedAttachmentMarkdown);

        // The pair was dropped whole rather than halved, so nothing unpaired survives...
        Assert.DoesNotContain(sent, char.IsSurrogate);

        // ...and the result is something System.Text.Json can actually serialise.
        Assert.Equal(sent, JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(sent)));
    }
}
