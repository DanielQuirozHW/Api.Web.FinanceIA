// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-01

using System.Security.Claims;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Api.Web.FinanceIA.Servicios.BalanceCorrections;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Covers the PER-CONVERSATION context budget gate in
/// <see cref="AnalysisController.ChatAsync"/>: the ceiling that refuses the next turn of a
/// conversation whose prompt has already outgrown the tenant's plan, so the analyst is told to start
/// a new conversation instead of the prompt growing until the model window rejects it.
///
/// <para>
/// The gate is REACTIVE by design: it reads the input_tokens Anthropic already billed on the last
/// assistant turn, persisted in ChatMessages. Nothing here estimates anything, so every test states
/// the observed size as a literal on a seeded row.
/// </para>
///
/// <para>
/// Three guarantees are the reason this file exists, and none of them is visible from a 200/429
/// status alone. A refused turn must leave NO trace — no user message persisted, no Claude call —
/// or it strands a question in the transcript that never got an answer. A brand-new conversation
/// must never be gated, and that is asserted as "the billing service was never even asked", because
/// a ChatConversation row is INSERTED before the gate runs and a create-then-refuse would orphan it.
/// And a billing read that fails or comes back degraded must FAIL OPEN — GetEffectiveAsync answers
/// a failed read with the MINIMUM plan, so trusting its numbers alone would turn one blipped SELECT
/// into the strictest gate in the system.
/// </para>
///
/// <para>
/// The monthly chat-token quota is a different limit enforced elsewhere (BillingQuotaFilter, an
/// action filter that never runs on a direct method call) and is covered by
/// <c>BillingQuotaFilterTests</c>. What is asserted here is only that the two 429s are
/// distinguishable on the wire.
/// </para>
/// </summary>
public sealed class AnalysisControllerChatContextBudgetTests
{
    private const string Tenant      = "tenant-1";
    private const string FirebaseUid = "uid-1";

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
    /// <c>Seeded</c> is the persisted transcript the gate measures; <c>Messages</c> is what this turn
    /// wrote. Keeping them apart is what lets a test assert that a refused turn persisted NOTHING
    /// while the conversation it was refused against still has its history.
    /// </summary>
    private sealed class RecordingChatRepository : IChatRepository
    {
        public List<ChatMessage> Seeded   { get; } = new();
        public List<ChatMessage> Messages { get; } = new();
        public ChatConversation? Created  { get; private set; }

        public Task<ChatConversation> CreateConversationAsync(ChatConversation conversation, CancellationToken ct)
        {
            conversation.ConversationId = Guid.NewGuid();
            Created = conversation;
            return Task.FromResult(conversation);
        }

        public Task<ChatConversation?> GetConversationByIdAsync(Guid conversationId, string tenantId, Guid userId, CancellationToken ct)
            => Task.FromResult<ChatConversation?>(new ChatConversation
            {
                ConversationId = conversationId,
                ResultId       = ResultId,
                AnalysisId     = AnalysisId,
                TenantId       = tenantId
            });

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
        public Task<ChatConversation?> GetConversationByIdForTenantAsync(Guid conversationId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> RenameConversationAsync(Guid conversationId, string tenantId, Guid userId, string newTitle, CancellationToken ct) => throw new NotImplementedException();
        public Task<ChatMessage?> GetMessageByIdAsync(long chatMessageId, Guid conversationId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
    }

    /// <summary>
    /// Never called on a refused turn — <c>CapturedSystemPrompt</c> staying null IS the assertion
    /// that the gate spent no tokens.
    /// </summary>
    private sealed class CapturingClaudeService : IClaudeService
    {
        public Task<BalanceSuggestionReply> SuggestBalanceCorrectionsAsync(
            byte[] pdfBytes, BalanceSuggestionQuestion question, CancellationToken ct)
            => throw new NotImplementedException();

        public string? CapturedSystemPrompt { get; private set; }

        public Task<ClaudeChatResult> ChatAsync(string model, string systemPrompt, IReadOnlyList<ConversationMessage> history, string userMessage, CancellationToken ct, string? attachmentMarkdown = null)
        {
            CapturedSystemPrompt = systemPrompt;
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
    /// <c>GetEffectiveCallCount</c> now pins a different property than it used to. It once proved the
    /// new-conversation rule — the gate returned before it asked, rather than merely declining to act
    /// on the answer — but the plan is now resolved ONCE per turn by the caller and shared between
    /// the context gate and the chat-model allow-list, so the counter can no longer isolate the gate.
    /// What it pins instead is that sharing: exactly one read per chat turn, so a second read of the
    /// same row cannot creep back in on the hot path.
    /// </summary>
    private sealed class StubBillingConfigService : ITenantBillingConfigService
    {
        public BillingPlanResolution Resolution { get; set; } =
            new(new BillingPlan("50 análisis", 50, 2_000_000L, 40_000, ChatModelCatalog.AllIds), BillingPlanSource.Configured);

        public Exception? ThrowOnGetEffective { get; set; }
        public int GetEffectiveCallCount { get; private set; }

        public Task<BillingPlanResolution> GetEffectiveAsync(string tenantId, CancellationToken ct)
        {
            GetEffectiveCallCount++;

            return ThrowOnGetEffective is not null
                ? Task.FromException<BillingPlanResolution>(ThrowOnGetEffective)
                : Task.FromResult(Resolution);
        }

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

    private static ChatMessage UserTurn(string content) =>
        new() { Role = "user", Content = content };

    /// <summary>
    /// An assistant turn as ChatAsync persists one: the billed input_tokens included.
    /// <paramref name="hadAttachment"/> is the script 053 flag — true when the turn carried an
    /// ephemeral attachment, so its input_tokens counts a block no later prompt will replay.
    /// </summary>
    private static ChatMessage AssistantTurn(
        int? inputTokens, string role = "assistant", bool hadAttachment = false) =>
        new()
        {
            Role                   = role,
            Content                = "respuesta previa",
            InputTokens            = inputTokens,
            OutputTokens           = 20,
            HadEphemeralAttachment = hadAttachment
        };

    private static ChatRequest Ask(Guid? conversationId) =>
        new() { Message = "¿Y la deuda?", ConversationId = conversationId };

    private static (AnalysisController Sut, CapturingClaudeService Claude, RecordingChatRepository Chat, StubBillingConfigService Billing) BuildSut(
        bool enforce = true,
        int budget = 40_000,
        BillingPlanSource source = BillingPlanSource.Configured,
        Exception? throwOnGetEffective = null)
    {
        var claude   = new CapturingClaudeService();
        var chatRepo = new RecordingChatRepository();

        var billing = new StubBillingConfigService
        {
            Resolution          = new BillingPlanResolution(
                                      new BillingPlan("50 análisis", 50, 2_000_000L, budget, ChatModelCatalog.AllIds), source),
            ThrowOnGetEffective = throwOnGetEffective
        };

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
            claude,           // claudeService
            null!,            // cashFlowReconciliationService
            null!,            // cashFlowNarrativeService
            null!,            // alertRatioReconciliationService
            new ChatEntityRepositoryStub(), // entityRepository (K5: the chat strips its identifiers)
            userRepo,         // userRepository
            chatRepo,         // chatRepository
            null!,            // creditLineRepository
            null!,            // operationRepository
            null!,            // auditService
            new StubApiCallLogService(), // apiCallLogService
            null!,            // excelCashFlowParser
            Options.Create(new BillingOptions { EnforceChatContextBudget = enforce }), // billingOptions
            billing,          // tenantBillingConfigService
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

        return (sut, claude, chatRepo, billing);
    }

    private static ProblemDetails AssertContextRejection(IActionResult response)
    {
        var result = Assert.IsType<ObjectResult>(response);
        Assert.Equal(StatusCodes.Status429TooManyRequests, result.StatusCode);
        Assert.Contains("application/problem+json", result.ContentTypes);

        var problem = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal(StatusCodes.Status429TooManyRequests, problem.Status);
        return problem;
    }

    // ── The gate ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChatAsync_refuses_the_next_turn_once_the_conversation_is_over_its_context_budget()
    {
        var (sut, claude, chatRepo, _) = BuildSut(enforce: true, budget: 40_000);
        chatRepo.Seeded.Add(UserTurn("¿Cómo viene?"));
        chatRepo.Seeded.Add(AssistantTurn(41_000));

        var response = await sut.ChatAsync(ResultId, Ask(ConversationId), CancellationToken.None);

        var problem = AssertContextRejection(response);
        Assert.Equal("CHAT_CONTEXT_BUDGET_EXCEEDED", problem.Extensions["errorCode"]);
        Assert.Equal(41_000, problem.Extensions["used"]);
        Assert.Equal(40_000, problem.Extensions["budget"]);
        Assert.Equal("50 análisis", problem.Extensions["planName"]);
        Assert.Equal(
            "Esta conversación alcanzó su límite de contexto. Iniciá una nueva conversación para seguir.",
            problem.Detail);

        // THE guarantee: a refused turn leaves no trace and costs nothing.
        Assert.Empty(chatRepo.Messages);
        Assert.Null(claude.CapturedSystemPrompt);
    }

    [Fact]
    public async Task ChatAsync_treats_exactly_at_the_budget_as_over_it()
    {
        // >=, matching BillingQuotaFilter. The next turn can only be larger than this one.
        var (sut, claude, chatRepo, _) = BuildSut(enforce: true, budget: 40_000);
        chatRepo.Seeded.Add(AssistantTurn(40_000));

        var response = await sut.ChatAsync(ResultId, Ask(ConversationId), CancellationToken.None);

        AssertContextRejection(response);
        Assert.Empty(chatRepo.Messages);
        Assert.Null(claude.CapturedSystemPrompt);
    }

    [Fact]
    public async Task ChatAsync_answers_normally_one_token_under_the_budget()
    {
        var (sut, claude, chatRepo, _) = BuildSut(enforce: true, budget: 40_000);
        chatRepo.Seeded.Add(AssistantTurn(39_999));

        var response = await sut.ChatAsync(ResultId, Ask(ConversationId), CancellationToken.None);

        Assert.IsType<ChatResponse>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.NotNull(claude.CapturedSystemPrompt);
        Assert.Equal(2, chatRepo.Messages.Count);
        Assert.Equal("user",      chatRepo.Messages[0].Role);
        Assert.Equal("assistant", chatRepo.Messages[1].Role);
    }

    [Fact]
    public async Task ChatAsync_measures_but_does_not_block_while_the_switch_is_off()
    {
        // The shipped default. The figures are logged as evidence; the analyst is still answered.
        var (sut, claude, chatRepo, billing) = BuildSut(enforce: false, budget: 40_000);
        chatRepo.Seeded.Add(AssistantTurn(999_999));

        var response = await sut.ChatAsync(ResultId, Ask(ConversationId), CancellationToken.None);

        Assert.IsType<ChatResponse>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.NotNull(claude.CapturedSystemPrompt);
        Assert.Equal(2, chatRepo.Messages.Count);

        // It still MEASURED — that is the whole point of shipping it dark. Exactly ONE read per
        // turn, and that number is now load-bearing in its own right: the plan is resolved once and
        // shared by the gate and the chat-model allow-list, so a 2 here would mean someone
        // reintroduced a second read of the same row on every chat turn.
        Assert.Equal(1, billing.GetEffectiveCallCount);
    }

    // ── Never gating a conversation that has no billed turn ───────────────────

    [Fact]
    public async Task ChatAsync_never_gates_a_brand_new_conversation_however_small_the_budget()
    {
        // Load-bearing, not a courtesy: a ChatConversation row is INSERTED before the gate runs, so a
        // create-then-refuse would strand an empty conversation in the analyst's list.
        //
        // This used to assert the billing service was never ASKED, on the reasoning that the gate
        // returned on the empty transcript before reading the plan. That proxy is gone: the plan is
        // now read once per turn for the chat-model allow-list whether the gate needs it or not, so
        // the call count can no longer isolate the gate. What it stood for is asserted directly
        // below instead — with a budget of 1, the smallest that exists, the turn is still ANSWERED
        // and the conversation still created. The ordering the proxy pinned is documented as a
        // TRIPWIRE at the call site, where it belongs.
        var (sut, claude, chatRepo, billing) = BuildSut(enforce: true, budget: 1);

        var response = await sut.ChatAsync(ResultId, Ask(conversationId: null), CancellationToken.None);

        Assert.IsType<ChatResponse>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.NotNull(claude.CapturedSystemPrompt);
        Assert.NotNull(chatRepo.Created);
        Assert.Equal(2, chatRepo.Messages.Count);   // question and answer both persisted
        Assert.Equal(1, billing.GetEffectiveCallCount);
    }

    [Fact]
    public async Task ChatAsync_does_not_gate_on_a_turn_whose_size_came_from_an_attachment()
    {
        // End to end: the attachment turn was billed at 90.000 against a 40.000 budget, and before
        // script 053 that pinned the conversation permanently — the transcript it will actually
        // replay is the 9.000-token one, and no later turn can rebuild the 90.000. The analyst is
        // answered, and the gate falls back to the last CLEAN turn.
        var (sut, claude, chatRepo, _) = BuildSut(enforce: true, budget: 40_000);
        chatRepo.Seeded.Add(UserTurn("¿Cómo viene?"));
        chatRepo.Seeded.Add(AssistantTurn(9_000));
        chatRepo.Seeded.Add(UserTurn("mirá este flujo de fondos"));
        chatRepo.Seeded.Add(AssistantTurn(90_000, hadAttachment: true));

        var response = await sut.ChatAsync(ResultId, Ask(ConversationId), CancellationToken.None);

        Assert.IsType<ChatResponse>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.NotNull(claude.CapturedSystemPrompt);
        Assert.Equal(2, chatRepo.Messages.Count);   // question and answer both persisted
    }

    [Fact]
    public async Task ChatAsync_fails_open_when_the_only_billed_turn_carried_an_attachment()
    {
        // The under-report taken to its limit: skipping the only measurable row leaves nothing, so
        // the conversation is ungateable and the turn PROCEEDS — the same rule as a brand-new
        // conversation, asserted with the smallest budget that exists so nothing else can explain
        // the 200. That is the intended semantics: this gate is built to be wrong in the direction
        // that answers the analyst, never the one that refuses on a number it distrusts.
        var (sut, claude, chatRepo, _) = BuildSut(enforce: true, budget: 1);
        chatRepo.Seeded.Add(UserTurn("mirá este flujo de fondos"));
        chatRepo.Seeded.Add(AssistantTurn(90_000, hadAttachment: true));

        var response = await sut.ChatAsync(ResultId, Ask(ConversationId), CancellationToken.None);

        Assert.IsType<ChatResponse>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.NotNull(claude.CapturedSystemPrompt);
        Assert.Equal(2, chatRepo.Messages.Count);
    }

    [Fact]
    public async Task ChatAsync_never_gates_a_conversation_whose_turns_carry_no_token_counts()
    {
        // Assistant rows predating the InputTokens column, and user turns, which never carry one.
        // Nothing measurable means nothing gateable, whatever the budget — asserted as the turn being
        // answered rather than as a call count, for the reason given in the test above.
        var (sut, claude, chatRepo, billing) = BuildSut(enforce: true, budget: 1);
        chatRepo.Seeded.Add(UserTurn("¿Cómo viene?"));
        chatRepo.Seeded.Add(AssistantTurn(inputTokens: null));

        var response = await sut.ChatAsync(ResultId, Ask(ConversationId), CancellationToken.None);

        Assert.IsType<ChatResponse>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.NotNull(claude.CapturedSystemPrompt);
        Assert.Equal(1, billing.GetEffectiveCallCount);
    }

    // ── Fail open ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChatAsync_lets_the_turn_through_when_the_plan_read_was_degraded()
    {
        // GetEffectiveAsync answers a failed read with the MINIMUM plan rather than throwing, so
        // trusting its numbers alone would cut a 200-plan tenant off at the 50-plan budget over one
        // blipped SELECT. IsDegraded is the difference between "on the minimum plan" and "unknown".
        var (sut, claude, chatRepo, _) = BuildSut(
            enforce: true, budget: 40_000, source: BillingPlanSource.ReadFailed);
        chatRepo.Seeded.Add(AssistantTurn(999_999));

        var response = await sut.ChatAsync(ResultId, Ask(ConversationId), CancellationToken.None);

        Assert.IsType<ChatResponse>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.NotNull(claude.CapturedSystemPrompt);
        Assert.Equal(2, chatRepo.Messages.Count);
    }

    [Fact]
    public async Task ChatAsync_lets_the_turn_through_when_the_plan_read_throws()
    {
        // The interface says it never throws. A gate that refuses an analyst's question is not the
        // place to take that on faith.
        var (sut, claude, chatRepo, _) = BuildSut(
            enforce: true, budget: 40_000,
            throwOnGetEffective: new InvalidOperationException("boom"));
        chatRepo.Seeded.Add(AssistantTurn(999_999));

        var response = await sut.ChatAsync(ResultId, Ask(ConversationId), CancellationToken.None);

        Assert.IsType<ChatResponse>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.NotNull(claude.CapturedSystemPrompt);
        Assert.Equal(2, chatRepo.Messages.Count);
    }

    [Fact]
    public async Task ChatAsync_treats_a_zero_budget_as_the_gate_being_off()
    {
        // PUT /api/billing/config rejects <= 0, so this can only come from a DBA-seeded row — and a
        // DBA who wrote 0 meant "off", not "refuse every turn after the first".
        var (sut, claude, chatRepo, _) = BuildSut(enforce: true, budget: 0);
        chatRepo.Seeded.Add(AssistantTurn(999_999));

        var response = await sut.ChatAsync(ResultId, Ask(ConversationId), CancellationToken.None);

        Assert.IsType<ChatResponse>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.NotNull(claude.CapturedSystemPrompt);
    }

    // ── Distinguishable from the monthly quota 429 ────────────────────────────

    [Fact]
    public async Task ChatAsync_context_429_is_distinguishable_from_the_monthly_quota_429()
    {
        // Both limits answer 429 from the same endpoint and the Portal has to tell them apart to show
        // the right remedy: "esperá al mes que viene" vs "abrí una conversación nueva". The
        // discriminators are errorCode and type. The monthly shape's used/limit/periodStart/periodEnd
        // are deliberately NOT reused — a per-conversation ceiling has no period, and emitting half
        // that shape would have a monthly renderer caption this "este mes" beside a missing date.
        var (sut, _, chatRepo, _) = BuildSut(enforce: true, budget: 40_000);
        chatRepo.Seeded.Add(AssistantTurn(41_000));

        var response = await sut.ChatAsync(ResultId, Ask(ConversationId), CancellationToken.None);

        var problem = AssertContextRejection(response);

        Assert.Equal("CHAT_CONTEXT_BUDGET_EXCEEDED", problem.Extensions["errorCode"]);
        Assert.NotEqual("CHAT_QUOTA_EXCEEDED", problem.Extensions["errorCode"]);
        Assert.Equal("https://serviciosfinancieros.api/errors/chat_context_budget_exceeded", problem.Type);

        Assert.False(problem.Extensions.ContainsKey("limit"));
        Assert.False(problem.Extensions.ContainsKey("periodStart"));
        Assert.False(problem.Extensions.ContainsKey("periodEnd"));

        Assert.True(problem.Extensions.ContainsKey("budget"));
        Assert.Equal(ConversationId.ToString(), problem.Extensions["conversationId"]);
    }

    // ── The measurement itself, with no controller in the way ─────────────────

    [Fact]
    public void LastAssistantInputTokens_reads_the_most_recent_billed_assistant_turn()
    {
        var messages = new List<ChatMessage>
        {
            UserTurn("uno"),
            AssistantTurn(1_000),
            UserTurn("dos"),
            AssistantTurn(9_000)
        };

        Assert.Equal(9_000, AnalysisController.LastAssistantInputTokens(messages));
    }

    [Fact]
    public void LastAssistantInputTokens_ignores_the_trailing_user_turn()
    {
        // The last ROW is a user turn and carries no count; the last billed ANSWER is what sizes the
        // conversation.
        var messages = new List<ChatMessage>
        {
            AssistantTurn(9_000),
            UserTurn("y esto?")
        };

        Assert.Equal(9_000, AnalysisController.LastAssistantInputTokens(messages));
    }

    [Fact]
    public void LastAssistantInputTokens_falls_back_past_an_assistant_row_with_no_count()
    {
        // Under-reporting is the safe direction: an older turn is strictly smaller, so this can only
        // let a turn through, never refuse one it should not have.
        var messages = new List<ChatMessage>
        {
            AssistantTurn(9_000),
            AssistantTurn(inputTokens: null)
        };

        Assert.Equal(9_000, AnalysisController.LastAssistantInputTokens(messages));
    }

    [Fact]
    public void LastAssistantInputTokens_measures_a_clean_assistant_turn_as_it_always_did()
    {
        // The script 053 flag defaults to false, and false must mean exactly what no column at all
        // meant before it existed — including for the rows the ALTER backfilled.
        var messages = new List<ChatMessage>
        {
            UserTurn("uno"),
            AssistantTurn(9_000, hadAttachment: false)
        };

        Assert.Equal(9_000, AnalysisController.LastAssistantInputTokens(messages));
    }

    [Fact]
    public void LastAssistantInputTokens_skips_a_turn_that_carried_an_attachment()
    {
        // THE FIX. The 90.000 was billed for a request including ~67k tokens of spreadsheet that is
        // absent from every later prompt (it never enters Content, which is what history replays),
        // so it describes a context the next
        // turn cannot rebuild. Measuring by it would pin a conversation whose real transcript is the
        // 9.000-token one below it.
        var messages = new List<ChatMessage>
        {
            UserTurn("uno"),
            AssistantTurn(9_000),
            UserTurn("mirá este flujo de fondos"),
            AssistantTurn(90_000, hadAttachment: true)
        };

        Assert.Equal(9_000, AnalysisController.LastAssistantInputTokens(messages));
    }

    [Fact]
    public void LastAssistantInputTokens_returns_null_when_every_assistant_turn_carried_an_attachment()
    {
        // Nothing measurable left, so the conversation is UNGATEABLE — the same answer a brand-new
        // conversation gets, reached the same way, and it fails OPEN at the call site (asserted
        // end-to-end below). Refusing here instead would mean gating on the very number this skip
        // exists to distrust.
        var messages = new List<ChatMessage>
        {
            UserTurn("mirá este flujo de fondos"),
            AssistantTurn(90_000, hadAttachment: true)
        };

        Assert.Null(AnalysisController.LastAssistantInputTokens(messages));
    }

    [Fact]
    public void LastAssistantInputTokens_matches_the_role_case_insensitively()
    {
        // This controller writes it lowercase, but rows can also arrive from a DDL script.
        var messages = new List<ChatMessage> { AssistantTurn(9_000, role: "ASSISTANT") };

        Assert.Equal(9_000, AnalysisController.LastAssistantInputTokens(messages));
    }

    [Fact]
    public void LastAssistantInputTokens_returns_null_when_there_is_nothing_to_measure()
    {
        Assert.Null(AnalysisController.LastAssistantInputTokens(null));
        Assert.Null(AnalysisController.LastAssistantInputTokens(new List<ChatMessage>()));
        Assert.Null(AnalysisController.LastAssistantInputTokens(new List<ChatMessage> { UserTurn("sola") }));
    }
}
