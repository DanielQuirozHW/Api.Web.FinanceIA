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
/// Covers the CALL-SITE wiring behind the chat prompt's LECTURA DEL FLUJO block: that
/// <see cref="AnalysisController.ChatAsync"/> actually runs the extract → reconcile → read pipeline
/// over the persisted BalanceJson and ships the result to Claude, and that a failure anywhere in
/// that pipeline costs the block and nothing else.
///
/// <para>
/// The fault-isolation half is the reason this file exists. By the time ChatAsync builds the prompt
/// the analyst's turn is ALREADY persisted and the call is not inside a try/catch, so an exception
/// from the narrative pipeline would strand a user message with no assistant reply and surface as a
/// 500. That guarantee is unprovable through the static prompt builder — it needs a real controller
/// and a narrative service that throws.
/// </para>
///
/// <para>
/// The enabling seam is <see cref="CapturingClaudeService"/>. The project's other IClaudeService
/// stubs throw on ChatAsync because no test had ever driven a chat turn; this one records the
/// system prompt, which is the only way to assert what actually reaches the Claude API.
/// </para>
///
/// <para>
/// Hand-rolled private sealed stubs (no mock library), and the REAL cash-flow services unless a
/// test is specifically about a failing one — so the expected sentences come from the production
/// implementation rather than being duplicated here. The block's RENDERING (header, ordering,
/// "No disponible", the disclaimer) is pinned separately in
/// <c>AnalysisControllerChatPromptTests</c>, which needs no DI. Only the dependencies the chat path
/// touches are stubbed; the rest of the ~23 ctor arguments are null! and the argument order mirrors
/// the ctor signature.
/// </para>
/// </summary>
public sealed class AnalysisControllerChatCashFlowTests
{
    private const string Tenant      = "tenant-1";
    private const string FirebaseUid = "uid-1";
    private const string Header      = "LECTURA DEL FLUJO (determinística):";

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
    /// Records every persisted turn in order, so a test can assert the conversation is intact —
    /// user turn AND assistant turn — rather than merely that the endpoint returned 200.
    /// </summary>
    private sealed class RecordingChatRepository : IChatRepository
    {
        public List<ChatMessage> Messages { get; } = new();

        public Task<ChatConversation> CreateConversationAsync(ChatConversation conversation, CancellationToken ct)
        {
            conversation.ConversationId = Guid.NewGuid();
            return Task.FromResult(conversation);
        }

        public Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(Guid conversationId, string tenantId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ChatMessage>>(Array.Empty<ChatMessage>());

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
    /// The seam that makes any of this observable: it CAPTURES the system prompt instead of
    /// throwing, which is the only way to assert what actually reaches the Claude API.
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

    /// <summary>
    /// Stands in for any failure inside the narrative pipeline. The interface documents that Read
    /// never throws — this asserts the chat endpoint does not RELY on that promise.
    /// </summary>
    private sealed class ThrowingCashFlowNarrativeService : ICashFlowNarrativeService
    {
        public IReadOnlyList<string> Read(CashFlowData cashFlow, CashFlowReconciliationResult reconciliation)
            => throw new InvalidOperationException("boom");
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static TenantContext BuildTenant() => new()
    {
        TenantId               = Tenant,
        FirebaseProjectId      = "proj",
        FirebaseCredentialJson = "{}",
        RecaptchaSecretKey     = "secret"
    };

    /// <summary>
    /// A BalanceJson blob shaped as ClaudeService persists it: scalars plus a cash_flow node
    /// serialised from the REAL CashFlowData, so a [JsonPropertyName] rename breaks this test
    /// instead of silently changing what the prompt ships.
    /// </summary>
    private static string BalanceBlobWith(CashFlowData cashFlow) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["assets"]      = 1_000_000d,
            ["liabilities"] =   400_000d,
            ["cash_flow"]   = cashFlow,
        });

    private static (AnalysisController Sut, CapturingClaudeService Claude, RecordingChatRepository Chat) BuildSut(
        string? balanceJson,
        ICashFlowNarrativeService? narrative = null)
    {
        var claude        = new CapturingClaudeService();
        var chatRepo      = new RecordingChatRepository();
        var billingConfig = new StubBillingConfigService();

        var resultRepo = new StubAnalysisResultRepository
        {
            ResultToReturn = new AnalysisResult
            {
                ResultId    = ResultId,
                AnalysisId  = AnalysisId,
                TenantId    = Tenant,
                BalanceJson = balanceJson
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
            new CashFlowReconciliationService(),                  // cashFlowReconciliationService — real
            narrative ?? new CashFlowNarrativeService(),          // cashFlowNarrativeService — real unless overridden
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

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChatAsync_ships_the_deterministic_reading_in_the_system_prompt()
    {
        // The wiring under test: extract from BalanceJson → recompute the reconciliation → read.
        // The EXPECTED sentences come from the REAL services, so this pins the pipeline without
        // duplicating CashFlowNarrativeService's wording spec, which lives in its own tests.
        var cashFlow = new CashFlowData
        {
            Present    = true,
            Operating  = -971_123_240,
            Investing  =  655_017_771,
            Components = new CashFlowComponents { AssetDisposals = 655_017_771 }
        };

        var expected = new CashFlowNarrativeService()
            .Read(cashFlow, new CashFlowReconciliationService().Reconcile(cashFlow));
        Assert.NotEmpty(expected); // the fixture must actually produce a reading, or this proves nothing

        var (sut, claude, _) = BuildSut(BalanceBlobWith(cashFlow));

        var response = await sut.ChatAsync(
            ResultId, new ChatRequest { Message = "¿Cómo fue el flujo?" }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(response);

        var prompt = Assert.IsType<string>(claude.CapturedSystemPrompt);
        Assert.Contains(Header, prompt, StringComparison.Ordinal);
        foreach (var sentence in expected)
            Assert.Contains(sentence, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatAsync_still_answers_when_the_narrative_service_throws()
    {
        // THE guarantee. The analyst's turn is already persisted by the time the prompt is built and
        // the call is not inside a try/catch, so an unhandled throw here would leave a user message
        // with no assistant reply and return 500. The reading is the only thing allowed to be lost.
        var cashFlow = new CashFlowData { Present = true, Operating = 558_367_419 };

        var (sut, claude, chatRepo) = BuildSut(
            BalanceBlobWith(cashFlow), narrative: new ThrowingCashFlowNarrativeService());

        var response = await sut.ChatAsync(
            ResultId, new ChatRequest { Message = "¿Y el flujo?" }, CancellationToken.None);

        var body = Assert.IsType<ChatResponse>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.Equal("respuesta del modelo", body.Message);

        // Degrades to the header + "No disponible", never to a missing block.
        var prompt = Assert.IsType<string>(claude.CapturedSystemPrompt);
        Assert.Contains(Header + "\n" + "No disponible", prompt, StringComparison.Ordinal);

        // And the conversation is intact: both turns persisted, nothing stranded.
        Assert.Equal(2, chatRepo.Messages.Count);
        Assert.Equal("user",      chatRepo.Messages[0].Role);
        Assert.Equal("assistant", chatRepo.Messages[1].Role);
    }

    [Fact]
    public async Task ChatAsync_still_emits_the_header_when_the_analysis_predates_cash_flow()
    {
        // A BalanceJson with no cash_flow key at all — i.e. every analysis older than the feature.
        var (sut, claude, _) = BuildSut("""{"assets":1000,"liabilities":400}""");

        var response = await sut.ChatAsync(
            ResultId, new ChatRequest { Message = "hola" }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(response);
        Assert.Contains(Header + "\n" + "No disponible",
            Assert.IsType<string>(claude.CapturedSystemPrompt), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]             // never analysed / nothing stored
    [InlineData("")]               // stored empty
    [InlineData("   ")]            // stored whitespace
    [InlineData("{ not json")]     // malformed — TryExtractCashFlow swallows the JsonException
    // Valid JSON with a NON-OBJECT root. These parse cleanly and then throw
    // InvalidOperationException on the first property read — not a JsonException, so they used to
    // escape the RAW block's unprotected TryExtractCashFlow call and surface as a 500 with the
    // analyst's turn already persisted. TryExtractCashFlow now checks the root's ValueKind.
    [InlineData("null")]           // literal null
    [InlineData("[1,2,3]")]        // top-level array
    public async Task ChatAsync_survives_an_absent_or_malformed_balance_blob(string? balanceJson)
    {
        var (sut, claude, chatRepo) = BuildSut(balanceJson);

        var response = await sut.ChatAsync(
            ResultId, new ChatRequest { Message = "hola" }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(response);
        Assert.Contains(Header + "\n" + "No disponible",
            Assert.IsType<string>(claude.CapturedSystemPrompt), StringComparison.Ordinal);
        Assert.Equal(2, chatRepo.Messages.Count);
    }

    [Fact]
    public async Task A_statement_marked_not_present_reads_as_no_disponible_without_faulting()
    {
        // present:false is the sentinel ClaudeService stores when the balance carries no cash-flow
        // statement. It is a normal outcome, not a fault: Read gates on it and returns nothing.
        var (sut, claude, _) = BuildSut(BalanceBlobWith(new CashFlowData { Present = false }));

        var response = await sut.ChatAsync(
            ResultId, new ChatRequest { Message = "hola" }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(response);
        Assert.Contains(Header + "\n" + "No disponible",
            Assert.IsType<string>(claude.CapturedSystemPrompt), StringComparison.Ordinal);
    }
}
