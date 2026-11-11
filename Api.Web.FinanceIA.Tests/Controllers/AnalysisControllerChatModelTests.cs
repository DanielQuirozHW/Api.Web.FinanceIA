// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-02

using System.Security.Claims;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.Exceptions;
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
/// Covers how POST /api/analyses/{resultId}/chat picks the model that answers a turn: the analyst's
/// optional <c>ChatRequest.Model</c> filtered against the tenant's <c>ChatEnabledModels</c>
/// allow-list, falling back to the list's first entry.
///
/// <para>
/// The resolution lives in the controller and not in ClaudeService on purpose — the tenant context
/// and the billing resolution are here — so this is the only place the analyst's REQUEST and the
/// tenant's ENTITLEMENT meet, and the only place a test can prove they meet correctly.
/// </para>
///
/// <para>
/// The recording assertions matter as much as the resolution ones. Before this delivery the chat
/// wrote the literal "claude-sonnet-4-6" into <c>ChatMessages.Model</c> and into two ApiCallLogs
/// rows, which was true only while nothing else could be called. Left alone, those literals would
/// have made the transcript and the cost telemetry describe a model that never ran — a failure that
/// produces no error anywhere and is invisible until someone reconciles a bill.
/// </para>
///
/// Hand-rolled private sealed stubs, no mock library.
/// </summary>
public sealed class AnalysisControllerChatModelTests
{
    private const string Tenant      = "tenant-1";
    private const string FirebaseUid = "uid-1";

    private static readonly Guid ResultId       = Guid.NewGuid();
    private static readonly Guid AnalysisId     = Guid.NewGuid();
    private static readonly Guid ConversationId = Guid.NewGuid();

    private static readonly string Haiku45  = ChatModelCatalog.Haiku45.Id;
    private static readonly string Sonnet46 = ChatModelCatalog.Sonnet46.Id;
    private static readonly string Sonnet5  = ChatModelCatalog.Sonnet5.Id;

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

    private sealed class RecordingChatRepository : IChatRepository
    {
        public List<ChatMessage> Messages { get; } = new();

        public Task<ChatConversation> CreateConversationAsync(ChatConversation conversation, CancellationToken ct)
        {
            conversation.ConversationId = Guid.NewGuid();
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

        // Empty: a conversation with no billed assistant turn cannot be context-gated, so that gate
        // returns before reading the plan and never interferes with what these tests measure.
        public Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(Guid conversationId, string tenantId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ChatMessage>>([]);

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
    /// Records the model handed to it, and can be told to fail — the failure path writes an
    /// ApiCallLogs row of its own, which used to carry the same hardcoded literal as the success one.
    /// </summary>
    private sealed class CapturingClaudeService : IClaudeService
    {
        public Task<BalanceSuggestionReply> SuggestBalanceCorrectionsAsync(
            byte[] pdfBytes, BalanceSuggestionQuestion question, CancellationToken ct)
            => throw new NotImplementedException();

        public string?    CapturedModel { get; private set; }
        public string?    CapturedSystemPrompt { get; private set; }
        public Exception? ThrowOnChat   { get; set; }
        public int        InputTokens   { get; set; } = 1_000_000;
        public int        OutputTokens  { get; set; } = 1_000_000;

        public Task<ClaudeChatResult> ChatAsync(string model, string systemPrompt, IReadOnlyList<ConversationMessage> history, string userMessage, CancellationToken ct, string? attachmentMarkdown = null)
        {
            CapturedModel        = model;
            CapturedSystemPrompt = systemPrompt;

            return ThrowOnChat is not null
                ? Task.FromException<ClaudeChatResult>(ThrowOnChat)
                : Task.FromResult(new ClaudeChatResult("respuesta del modelo", InputTokens, OutputTokens));
        }

        public Task<ClaudeAnalysisResult> AnalyzeDocumentAsync(byte[] pdfBytes, string documentType, int? fiscalYear, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<ClaudeNarrativeResult> GenerateScoringNarrativeAsync(ScoringResult scoring, RatiosData ratios, BcraData? bcraData, BcraMacroData? macroData, ScoreReliabilityResponse? reliability, CancellationToken ct)
            => throw new NotImplementedException();
    }

    /// <summary>Captures the model and cost the chat reports to [dbo].[ApiCallLogs].</summary>
    private sealed class RecordingApiCallLogService : IApiCallLogService
    {
        public string?  LastModel   { get; private set; }
        public decimal? LastCostUsd { get; private set; }
        public bool?    LastSuccess { get; private set; }
        public string?  LastError   { get; private set; }

        public Task LogAsync(
            string tenantId, string service, string endpoint, int? responseStatus,
            int? inputTokens, int? outputTokens, decimal? estimatedCostUsd, bool isSuccess,
            string? errorMessage, int durationMs, string? operation = null, string? model = null,
            Guid? userId = null, Guid? analysisId = null, long? chatMessageId = null, Guid? documentId = null,
            CancellationToken ct = default)
        {
            LastModel   = model;
            LastCostUsd = estimatedCostUsd;
            LastSuccess = isSuccess;
            LastError   = errorMessage;
            return Task.CompletedTask;
        }
    }

    private sealed class StubBillingConfigService : ITenantBillingConfigService
    {
        public BillingPlanResolution Resolution { get; set; } =
            new(BillingPlans.Minimum, BillingPlanSource.Configured);

        public Exception? ThrowOnGetEffective { get; set; }

        public Task<BillingPlanResolution> GetEffectiveAsync(string tenantId, CancellationToken ct)
            => ThrowOnGetEffective is not null
                ? Task.FromException<BillingPlanResolution>(ThrowOnGetEffective)
                : Task.FromResult(Resolution);

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

    private static ChatRequest Ask(string? model = null) =>
        new() { Message = "¿Y la deuda?", ConversationId = ConversationId, Model = model };

    private static (AnalysisController Sut, CapturingClaudeService Claude, RecordingChatRepository Chat, RecordingApiCallLogService Logs) BuildSut(
        IReadOnlyList<string>? enabled = null,
        Exception? throwOnGetEffective = null,
        Exception? throwOnChat = null,
        Api.Web.FinanceIA.Tests.PersonAnalysis.StubPersonAnalysisRepository? personAnalyses = null,
        AnalysisResult? result = null)
    {
        var claude   = new CapturingClaudeService { ThrowOnChat = throwOnChat };
        var chatRepo = new RecordingChatRepository();
        var logs     = new RecordingApiCallLogService();

        var billing = new StubBillingConfigService
        {
            Resolution = new BillingPlanResolution(
                new BillingPlan("50 análisis", 50, 2_000_000L, 40_000, enabled ?? ChatModelCatalog.AllIds),
                BillingPlanSource.Configured),
            ThrowOnGetEffective = throwOnGetEffective
        };

        var resultRepo = new StubAnalysisResultRepository
        {
            ResultToReturn = result ?? new AnalysisResult
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
            logs,             // apiCallLogService
            null!,            // excelCashFlowParser
            Options.Create(new BillingOptions()),  // billingOptions
            billing,          // tenantBillingConfigService
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

        sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim("uid", FirebaseUid) }, "Test"))
            }
        };

        return (sut, claude, chatRepo, logs);
    }

    // ── Persona humana analysis (DDL 072): no chat in M10 Phase 4a ───────────

    [Fact]
    public async Task A_persona_analysis_refuses_the_chat_before_any_history_or_model_call()
    {
        var personAnalyses = new Api.Web.FinanceIA.Tests.PersonAnalysis.StubPersonAnalysisRepository();
        personAnalyses.Kinds[AnalysisId] = AnalysisKinds.PersonaHumana;
        var (sut, claude, chat, logs) = BuildSut(personAnalyses: personAnalyses);

        var response = await sut.ChatAsync(ResultId, Ask(), CancellationToken.None);

        var problem = Assert.IsType<ProblemDetails>(Assert.IsAssignableFrom<ObjectResult>(response).Value);
        Assert.Equal(409, problem.Status);
        Assert.Equal("chat_unavailable_for_person_analysis", problem.Extensions["errorCode"]);
        Assert.Null(claude.CapturedModel);
        Assert.Empty(chat.Messages);
        Assert.Null(logs.LastModel);
    }

    [Fact]
    public async Task A_balance_analysis_still_reaches_the_model()
    {
        var personAnalyses = new Api.Web.FinanceIA.Tests.PersonAnalysis.StubPersonAnalysisRepository();
        var (sut, claude, _, _) = BuildSut(personAnalyses: personAnalyses);

        await sut.ChatAsync(ResultId, Ask(), CancellationToken.None);

        Assert.NotNull(claude.CapturedModel);
        Assert.Equal(1, personAnalyses.KindReads);
    }

    [Fact]
    public async Task The_system_prompt_states_the_rows_reliability_with_the_fiscal_year_from_the_same_query()
    {
        // Score credibility 1b-3. The analysis's fiscal year (2023) arrives ONLY through the person-refusal
        // query — the facts say 2025 — so a stale-balance line naming 2023 proves it was used, and one read
        // proves no second query was added for it.
        var personAnalyses = new Api.Web.FinanceIA.Tests.PersonAnalysis.StubPersonAnalysisRepository();
        personAnalyses.FiscalYears[AnalysisId] = 2023;
        var result = new AnalysisResult
        {
            ResultId           = ResultId,
            AnalysisId         = AnalysisId,
            TenantId           = Tenant,
            AiSuggestedScore   = 42.5m,
            AiSuggestedLevel   = "Medium",
            EnrichmentStatus   = "Complete",
            GeneratedAt        = Api.Web.FinanceIA.Tests.Servicios.ScoreReliability.ScoreReliabilityFixture.ScoreDate,
            ScoreBreakdownJson = Api.Web.FinanceIA.Tests.Servicios.ScoreReliability.ScoreReliabilityFixture.Json(
                facts: Api.Web.FinanceIA.Tests.Servicios.ScoreReliability.ScoreReliabilityFixture.CleanFacts()),
        };
        var (sut, claude, _, _) = BuildSut(personAnalyses: personAnalyses, result: result);

        await sut.ChatAsync(ResultId, Ask(), CancellationToken.None);

        Assert.Equal(1, personAnalyses.KindReads);
        Assert.Contains("CALIDAD DE LOS DATOS:", claude.CapturedSystemPrompt, StringComparison.Ordinal);
        Assert.Contains("El balance del ejercicio 2023 tiene al menos 30 meses de antigüedad",
                        claude.CapturedSystemPrompt, StringComparison.Ordinal);
    }

    // ── Resolving the effective model ─────────────────────────────────────────

    [Fact]
    public async Task A_requested_model_the_tenant_is_entitled_to_is_the_one_that_answers()
    {
        var (sut, claude, _, _) = BuildSut();

        var response = await sut.ChatAsync(ResultId, Ask(Haiku45), CancellationToken.None);

        Assert.IsType<OkObjectResult>(response);
        Assert.Equal(Haiku45, claude.CapturedModel);
    }

    [Fact]
    public async Task A_turn_that_names_no_model_uses_the_first_one_the_tenant_has_enabled()
    {
        // "First" is the catalogue's order, not the order the column was written in — SanitizeOrDefault
        // imposes it precisely so this default is deterministic.
        var (sut, claude, _, _) = BuildSut(enabled: [Sonnet46, Sonnet5]);

        await sut.ChatAsync(ResultId, Ask(), CancellationToken.None);

        Assert.Equal(Sonnet46, claude.CapturedModel);
    }

    [Fact]
    public async Task A_requested_model_the_tenant_is_not_entitled_to_falls_back_rather_than_failing()
    {
        // NOT a 400, deliberately. The Portal can hold a list a SuperAdmin has since narrowed, and
        // refusing an otherwise valid question over a stale dropdown value is the worse outcome.
        var (sut, claude, chat, _) = BuildSut(enabled: [Sonnet46]);

        var response = await sut.ChatAsync(ResultId, Ask(Sonnet5), CancellationToken.None);

        Assert.IsType<OkObjectResult>(response);
        Assert.Equal(Sonnet46, claude.CapturedModel);

        // The turn really was answered — the user turn and the assistant turn are both persisted.
        Assert.Equal(2, chat.Messages.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("gpt-4")]
    [InlineData("claude-sonnet-4-6-20251114")]
    public async Task A_blank_or_nonexistent_model_is_treated_exactly_like_naming_none(string requested)
    {
        // One rule, several inputs: an id outside the tenant's list, an id outside the catalogue, and
        // no id at all all land on the same fallback. There is no separate error path to get wrong.
        var (sut, claude, _, _) = BuildSut(enabled: [Sonnet5]);

        var response = await sut.ChatAsync(ResultId, Ask(requested), CancellationToken.None);

        Assert.IsType<OkObjectResult>(response);
        Assert.Equal(Sonnet5, claude.CapturedModel);
    }

    [Fact]
    public async Task A_requested_model_is_matched_after_trimming()
    {
        var (sut, claude, _, _) = BuildSut();

        await sut.ChatAsync(ResultId, Ask("  claude-haiku-4-5  "), CancellationToken.None);

        Assert.Equal(Haiku45, claude.CapturedModel);
    }

    [Fact]
    public async Task A_failed_billing_read_answers_with_the_default_model_rather_than_failing_the_turn()
    {
        // The analyst's question is already persisted by this point, so a throw here would strand it
        // unanswered behind a 500. The default is what the chat called before models were
        // configurable — and the analyst's pick is deliberately NOT honoured, because there is no
        // entitlement left to check it against.
        var (sut, claude, _, _) = BuildSut(
            throwOnGetEffective: new InvalidOperationException("db down"));

        var response = await sut.ChatAsync(ResultId, Ask(Sonnet5), CancellationToken.None);

        Assert.IsType<OkObjectResult>(response);
        Assert.Equal(ChatModelCatalog.Default.Id, claude.CapturedModel);
    }

    // ── Recording what actually ran ───────────────────────────────────────────

    [Fact]
    public async Task The_persisted_assistant_turn_records_the_model_that_answered()
    {
        // Not the literal "claude-sonnet-4-6" this used to write. The column is per-turn because a
        // conversation may mix models, and the history endpoint reports it back to the analyst.
        var (sut, _, chat, _) = BuildSut();

        await sut.ChatAsync(ResultId, Ask(Sonnet5), CancellationToken.None);

        var assistant = Assert.Single(chat.Messages.Where(m => m.Role == "assistant"));
        Assert.Equal(Sonnet5, assistant.Model);
    }

    [Fact]
    public async Task The_assistant_turn_records_the_fallback_model_when_the_pick_was_not_honoured()
    {
        // The transcript must say what RAN, not what was asked for — otherwise the one record of a
        // silent fallback would itself be wrong.
        var (sut, _, chat, _) = BuildSut(enabled: [Haiku45]);

        await sut.ChatAsync(ResultId, Ask(Sonnet5), CancellationToken.None);

        var assistant = Assert.Single(chat.Messages.Where(m => m.Role == "assistant"));
        Assert.Equal(Haiku45, assistant.Model);
    }

    [Fact]
    public async Task The_success_api_call_log_records_the_model_that_answered()
    {
        var (sut, _, _, logs) = BuildSut();

        await sut.ChatAsync(ResultId, Ask(Haiku45), CancellationToken.None);

        Assert.True(logs.LastSuccess);
        Assert.Equal(Haiku45, logs.LastModel);
    }

    [Fact]
    public async Task The_failure_api_call_log_records_the_model_that_was_attempted()
    {
        // The failure path writes its own row, and it carried the same hardcoded literal. A 502 that
        // blames the wrong model is worse than one that names none.
        var (sut, _, _, logs) = BuildSut(
            enabled: [Sonnet5],
            throwOnChat: new ClaudeApiException("boom", 500));

        var response = await sut.ChatAsync(ResultId, Ask(Sonnet5), CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(response);
        Assert.Equal(StatusCodes.Status502BadGateway, problem.StatusCode);

        Assert.False(logs.LastSuccess);
        Assert.Equal(Sonnet5, logs.LastModel);
    }

    [Fact]
    public async Task The_failure_api_call_log_records_the_api_reason_not_the_client_text()
    {
        var failure = new ClaudeApiException(
            "Claude chat API error: HTTP 400 invalid_request_error: bad [req_c1].", 400);
        var (sut, _, _, logs) = BuildSut(enabled: [Sonnet5], throwOnChat: failure);

        var response = await sut.ChatAsync(ResultId, Ask(Sonnet5), CancellationToken.None);

        Assert.Equal(StatusCodes.Status502BadGateway, Assert.IsType<ObjectResult>(response).StatusCode);
        Assert.Equal(failure.DiagnosticMessage, logs.LastError);
        Assert.NotEqual(failure.Message, logs.LastError);
    }

    // ── Cost ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("claude-haiku-4-5",  6)]    // 1 MTok in @ $1 + 1 MTok out @ $5
    [InlineData("claude-sonnet-4-6", 18)]   // 1 MTok in @ $3 + 1 MTok out @ $15
    [InlineData("claude-sonnet-5",   12)]   // 1 MTok in @ $2 + 1 MTok out @ $10
    public async Task The_logged_cost_uses_the_rates_of_the_model_that_actually_ran(
        string requested, decimal expectedUsd)
    {
        // The arithmetic used to be Sonnet 4.6's $3/$15 written inline, correct only while nothing
        // else could be called. Left alone it would over-report a Haiku turn threefold — and nothing
        // would error, so the figure would just quietly be wrong in the billing telemetry.
        var (sut, _, _, logs) = BuildSut();

        await sut.ChatAsync(ResultId, Ask(requested), CancellationToken.None);

        Assert.Equal(expectedUsd, logs.LastCostUsd);
    }

    [Fact]
    public async Task The_logged_cost_follows_the_fallback_and_not_the_request()
    {
        // The pairing that matters: when the pick is not honoured, the cost must be priced against
        // what ran. Sonnet 5 was asked for at $2/$10; Haiku answered at $1/$5.
        var (sut, _, _, logs) = BuildSut(enabled: [Haiku45]);

        await sut.ChatAsync(ResultId, Ask(Sonnet5), CancellationToken.None);

        Assert.Equal(6m, logs.LastCostUsd);
    }
}
