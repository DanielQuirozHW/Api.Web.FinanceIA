// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-05

using System.Security.Claims;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.BalanceCorrections;
using Api.Web.FinanceIA.Servicios.Privacy;
using Api.Web.FinanceIA.Tests.PersonAnalysis;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// K5: <see cref="AnalysisController.ChatAsync"/> never sends the evaluated company's identity to Claude.
/// The system prompt (whose executive summary the extraction wrote while reading the PDF), every replayed
/// turn of both roles and the analyst's current message are redacted at the boundary — while ChatMessages
/// keeps the text exactly as written. A turn that cannot be redacted is not sent at all.
/// Every name and CUIT here is SYNTHETIC.
/// </summary>
public sealed class AnalysisControllerChatRedactionTests
{
    private const string Tenant       = "tenant-1";
    private const string FirebaseUid  = "uid-1";
    private const string BusinessName = "Ejemplo Agro S.R.L.";
    private const string Cuit         = "30-99887766-5";

    private const string Summary =
        "Ejemplo Agro S.R.L. es una sociedad agropecuaria con CUIT 30-99887766-5; " +
        "EJEMPLO AGRO SRL mantiene una liquidez adecuada.";

    private static readonly Guid ResultId   = Guid.NewGuid();
    private static readonly Guid AnalysisId = Guid.NewGuid();
    private static readonly Guid EntityId   = Guid.NewGuid();

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

        public int ChatCalls { get; private set; }

        public Task<ClaudeChatResult> ChatAsync(string model, string systemPrompt, IReadOnlyList<ConversationMessage> history, string userMessage, CancellationToken ct, string? attachmentMarkdown = null)
        {
            ChatCalls++;
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

    private sealed class RecordingLogger : ILogger<AnalysisController>
    {
        public List<(LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> Values, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            Entries.Add((logLevel, formatter(state, exception), values, exception));
        }
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private sealed record Harness(
        AnalysisController           Sut,
        CapturingClaudeService       Claude,
        RecordingChatRepository      Chat,
        ChatEntityRepositoryStub     Entities,
        StubPersonAnalysisRepository Analyses,
        RecordingLogger              Logger);

    private static Harness BuildSut(string? lufeJson = null, string? balanceJson = null)
    {
        var claude   = new CapturingClaudeService();
        var chatRepo = new RecordingChatRepository();
        var logger   = new RecordingLogger();

        var entities = new ChatEntityRepositoryStub
        {
            Entity = new EntityDetailProjection
            {
                EntityId     = EntityId,
                TenantId     = Tenant,
                BusinessName = BusinessName,
                CUIT         = Cuit,
                TradeName    = "Campo Ejemplo",
                LufeDataJson = lufeJson
            }
        };

        var analyses = new StubPersonAnalysisRepository();
        analyses.EntityIds[AnalysisId] = EntityId;

        var resultRepo = new StubAnalysisResultRepository
        {
            ResultToReturn = new AnalysisResult
            {
                ResultId         = ResultId,
                AnalysisId       = AnalysisId,
                TenantId         = Tenant,
                ExecutiveSummary = Summary,
                ScoringNarrative = "El score refleja una solvencia media.",
                BalanceJson      = balanceJson
            }
        };

        var userRepo = new StubUserRepository
        {
            UserToReturn = new User { UserId = Guid.NewGuid(), DisplayName = "Analista" }
        };

        var sut = new AnalysisController(
            new StubTenantContextAccessor
            {
                TenantContext = new TenantContext
                {
                    TenantId               = Tenant,
                    FirebaseProjectId      = "proj",
                    FirebaseCredentialJson = "{}",
                    RecaptchaSecretKey     = "secret"
                }
            },
            null!,            // documentRepository
            resultRepo,       // analysisResultRepository
            null!,            // analysisRepository
            claude,           // claudeService
            new CashFlowReconciliationService(),
            new CashFlowNarrativeService(),
            null!,            // alertRatioReconciliationService
            entities,         // entityRepository
            userRepo,         // userRepository
            chatRepo,         // chatRepository
            null!,            // creditLineRepository
            null!,            // operationRepository
            null!,            // auditService
            new StubApiCallLogService(),
            null!,            // excelCashFlowParser
            null!,            // billingOptions
            new StubBillingConfigService(),
            null!,            // userApprovalLimitRepository
            null!,            // decisionNotifier
            null!,            // approvalQuorumService
            new Api.Web.FinanceIA.Tests.Legajo.FakeLegajoDocumentRepository(),
            null!,            // analysisPipeline
            null!,            // analysisJobQueue
            null!,            // analysisRunRegistry
            null!,            // analysisRunStateRepository
            analyses,
            logger);

        sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("uid", FirebaseUid) }, "Test"))
            }
        };

        return new Harness(sut, claude, chatRepo, entities, analyses, logger);
    }

    private static void SeedHistory(RecordingChatRepository chat)
    {
        chat.Seeded.Add(new ChatMessage
        {
            Role = "user", Content = "¿Cómo viene la deuda de Ejemplo Agro?", TenantId = Tenant
        });
        chat.Seeded.Add(new ChatMessage
        {
            Role = "assistant", TenantId = Tenant, InputTokens = 100, OutputTokens = 50,
            Content = "# Análisis de EJEMPLO AGRO S.R.L.\nLa deuda bancaria de Ejemplo Agro S.R.L. (CUIT 30998877665) es baja."
        });
    }

    private static void AssertNoIdentifier(string? text)
    {
        Assert.NotNull(text);
        foreach (var identifier in new[] { "Ejemplo Agro", "EJEMPLO AGRO", "30-99887766-5", "30998877665", "Campo Ejemplo" })
            Assert.DoesNotContain(identifier, text, StringComparison.OrdinalIgnoreCase);
    }

    // ── The request Claude receives ───────────────────────────────────────────

    [Fact]
    public async Task The_system_prompt_sent_to_Claude_carries_no_name_or_cuit_of_the_stored_summary()
    {
        var h = BuildSut();

        var response = await h.Sut.ChatAsync(ResultId, new ChatRequest { Message = "¿Qué ves?" }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(response);
        AssertNoIdentifier(h.Claude.CapturedSystemPrompt);
        Assert.Contains("RESUMEN EJECUTIVO (estados financieros): la empresa es una sociedad agropecuaria con CUIT [CUIT]; la empresa mantiene", h.Claude.CapturedSystemPrompt, StringComparison.Ordinal);
        Assert.Equal(EntityId, Assert.Single(h.Entities.RequestedEntityIds));
    }

    [Fact]
    public async Task A_rubro_label_carrying_the_name_or_cuit_reaches_Claude_redacted()
    {
        // CIFRAS DEL BALANCE prints rubro labels as read from the PDF, and a ledger label can name the
        // company. The block is inside the system prompt, so the boundary redaction must cover it.
        var balanceJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["assets"]        = 1_000_000d,
            ["amounts_unit"]  = "pesos",
            ["amounts_scale"] = 1d,
            ["current_liabilities_items"] = new[]
            {
                new BalanceLineItem { Label = "Préstamo Ejemplo Agro S.R.L. CUIT 30-99887766-5", Amount = 250_000d },
            },
        });
        var h = BuildSut(balanceJson: balanceJson);

        var response = await h.Sut.ChatAsync(ResultId, new ChatRequest { Message = "¿Cómo se financia?" }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(response);
        AssertNoIdentifier(h.Claude.CapturedSystemPrompt);
        Assert.Contains("Rubros del Pasivo Corriente:\n- Préstamo la empresa. CUIT [CUIT]: 250000\n", h.Claude.CapturedSystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replayed_turns_of_both_roles_are_redacted_in_the_request_but_not_in_ChatMessages()
    {
        var h = BuildSut();
        SeedHistory(h.Chat);

        await h.Sut.ChatAsync(ResultId, new ChatRequest { Message = "¿Y la liquidez?" }, CancellationToken.None);

        var history = Assert.IsAssignableFrom<IReadOnlyList<ConversationMessage>>(h.Claude.CapturedHistory);
        Assert.Equal(2, history.Count);
        Assert.Equal("user", history[0].Role);
        Assert.Equal("¿Cómo viene la deuda de la empresa?", history[0].Content);
        Assert.Equal("assistant", history[1].Role);
        Assert.Equal("# Análisis de la empresa\nLa deuda bancaria de la empresa (CUIT [CUIT]) es baja.", history[1].Content);

        // The stored rows are untouched: the analyst still reads the real name.
        Assert.Equal("¿Cómo viene la deuda de Ejemplo Agro?", h.Chat.Seeded[0].Content);
        Assert.Contains("EJEMPLO AGRO S.R.L.", h.Chat.Seeded[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_current_message_is_redacted_for_Claude_and_persisted_as_typed()
    {
        var h = BuildSut();
        const string typed = "Comparame Ejemplo Agro SRL (30-99887766-5) con el sector.";

        await h.Sut.ChatAsync(ResultId, new ChatRequest { Message = typed }, CancellationToken.None);

        Assert.Equal("Comparame la empresa ([CUIT]) con el sector.", h.Claude.CapturedUserMessage);

        var userTurn = h.Chat.Messages[0];
        Assert.Equal("user", userTurn.Role);
        Assert.Equal(typed, userTurn.Content);
        // The conversation title never reaches Claude, so it keeps the name too.
        Assert.StartsWith("Comparame Ejemplo Agro SRL", h.Chat.Created!.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Partners_and_the_registry_name_from_the_LUFE_snapshot_are_redacted_too()
    {
        const string lufe =
            "{\"Entidad\":{\"cuit\":30998877665,\"nombre\":\"EJEMPLO AGROPECUARIA SRL\"}," +
            "\"Autoridades\":[{\"cuit\":20112233446,\"denominacion\":\"PEREZ JUAN CARLOS\"}," +
            "{\"cuit\":30556677889,\"denominacion\":\"INVERSORA EJEMPLO SA\"}]}";
        var h = BuildSut(lufeJson: lufe);

        await h.Sut.ChatAsync(ResultId, new ChatRequest
        {
            Message = "¿Ejemplo Agropecuaria SRL depende de Juan Carlos Pérez o de Inversora Ejemplo S.A. (20112233446)?"
        }, CancellationToken.None);

        Assert.Equal(
            "¿La empresa depende de [persona vinculada] o de [persona vinculada] ([CUIT])?",
            h.Claude.CapturedUserMessage);
    }

    // ── Logging ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_log_carries_counts_only_never_text_or_identifiers()
    {
        var h = BuildSut();
        SeedHistory(h.Chat);

        await h.Sut.ChatAsync(ResultId, new ChatRequest { Message = "Hablame de Ejemplo Agro." }, CancellationToken.None);

        var entry = Assert.Single(h.Logger.Entries, e => e.Message.StartsWith("Chat redaction for result", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(
            $"Chat redaction for result {ResultId} tenant {Tenant} replaced 6 name, 0 person and 2 CUIT occurrence(s)",
            entry.Message);

        foreach (var logged in h.Logger.Entries)
        {
            AssertNoIdentifier(logged.Message);
            foreach (var value in logged.Values)
                AssertNoIdentifier(value.Value?.ToString() ?? string.Empty);
        }
    }

    // ── Fail closed ───────────────────────────────────────────────────────────

    public static TheoryData<string> FailureModes => new() { "entity_not_found", "lookup_failed", "analysis_not_found" };

    [Theory]
    [MemberData(nameof(FailureModes))]
    public async Task A_turn_that_cannot_be_redacted_is_not_sent_and_leaves_no_trace(string mode)
    {
        var h = BuildSut();
        switch (mode)
        {
            case "entity_not_found":   h.Entities.Entity = null; break;
            case "lookup_failed":      h.Entities.ThrowOnRead = new InvalidOperationException("Ejemplo Agro S.R.L. 30-99887766-5"); break;
            case "analysis_not_found": h.Analyses.MissingAnalyses.Add(AnalysisId); break;
        }

        var response = await h.Sut.ChatAsync(ResultId, new ChatRequest { Message = "¿Qué ves en Ejemplo Agro?" }, CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(response);
        Assert.Equal(502, problem.StatusCode);
        Assert.Equal("El servicio de chat de IA no está disponible temporalmente.", Assert.IsType<ProblemDetails>(problem.Value).Detail);

        Assert.Equal(0, h.Claude.ChatCalls);
        Assert.Empty(h.Chat.Messages);
        Assert.Null(h.Chat.Created);

        var error = Assert.Single(h.Logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("not sent: entity identifiers could not be applied", error.Message, StringComparison.Ordinal);
        Assert.StartsWith(mode, error.Values.Single(v => v.Key == "Reason").Value!.ToString(), StringComparison.Ordinal);
        Assert.Null(error.Exception);
        AssertNoIdentifier(error.Message);
    }

    // ── The pure halves ───────────────────────────────────────────────────────

    [Fact]
    public void RedactChatRequest_copies_every_turn_and_sums_the_counts()
    {
        Assert.True(EntityIdentifierRedactor.TryCreate(
            new EntityIdentifiers([BusinessName], [], [], [Cuit]), out var redactor));

        var history = new List<ConversationMessage>
        {
            new() { Role = "user",      Content = "Ejemplo Agro" },
            new() { Role = "assistant", Content = "Sin datos." }
        };

        var redacted = AnalysisController.RedactChatRequest(redactor!, "Empresa: Ejemplo Agro SRL, 30-99887766-5", history, "ok");

        Assert.NotNull(redacted);
        Assert.Equal("Empresa: la empresa, [CUIT]", redacted.SystemPrompt);
        Assert.Equal("La empresa", redacted.History[0].Content);
        Assert.Same(history[1].Content, redacted.History[1].Content);
        Assert.NotSame(history[0], redacted.History[0]);
        Assert.Equal("Ejemplo Agro", history[0].Content);
        Assert.Equal(new RedactionCounts(2, 0, 1), redacted.Counts);
    }

    [Fact]
    public void BuildChatEntityIdentifiers_reads_both_lufe_snapshots_and_splits_legal_from_natural_persons()
    {
        const string frozen  = "{\"Entidad\":{\"cuit\":30998877665,\"nombre\":\"EJEMPLO AGRO\"},\"Autoridades\":[{\"cuit\":20112233446,\"denominacion\":\"PEREZ JUAN\"}]}";
        const string current = "{\"Autoridades\":[{\"cuit\":30556677889,\"denominacion\":\"INVERSORA EJEMPLO SA\"},{\"denominacion\":\"GOMEZ ANA\"}]}";

        var identifiers = AnalysisController.BuildChatEntityIdentifiers(
            BusinessName, "Campo Ejemplo", Cuit, [frozen, current, null], out var unreadable);

        Assert.False(unreadable);
        Assert.Equal([BusinessName, "Campo Ejemplo", "EJEMPLO AGRO"], identifiers.CompanyNames);
        Assert.Equal(["INVERSORA EJEMPLO SA"], identifiers.RelatedCompanyNames);
        Assert.Equal(["PEREZ JUAN", "GOMEZ ANA"], identifiers.PersonNames);
        Assert.Equal([Cuit, "30998877665", "20112233446", "30556677889"], identifiers.Cuits);
    }

    [Fact]
    public void BuildChatEntityIdentifiers_skips_an_unreadable_snapshot_but_keeps_the_entity_identifiers()
    {
        var identifiers = AnalysisController.BuildChatEntityIdentifiers(
            BusinessName, null, Cuit, ["{not json"], out var unreadable);

        Assert.True(unreadable);
        Assert.Equal([BusinessName], identifiers.CompanyNames);
        Assert.Equal([Cuit], identifiers.Cuits);
    }

    [Fact]
    public async Task An_unreadable_lufe_snapshot_still_lets_the_turn_through_redacted()
    {
        var h = BuildSut(lufeJson: "{not json");

        var response = await h.Sut.ChatAsync(ResultId, new ChatRequest { Message = "Ejemplo Agro" }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(response);
        Assert.Equal("La empresa", h.Claude.CapturedUserMessage);
        Assert.Single(h.Logger.Entries, e => e.Level == LogLevel.Warning && e.Message.StartsWith("LUFE snapshot", StringComparison.Ordinal));
    }

    [Fact]
    public void The_chat_prompt_tells_the_model_to_name_the_company_only_as_la_empresa()
    {
        var prompt = AnalysisController.BuildChatSystemPrompt(
            new AnalysisResult(), [], hasAttachment: false, reliability: null, fiscalYear: null);

        Assert.Contains("Referite a la empresa evaluada únicamente como \"la empresa\"", prompt, StringComparison.Ordinal);
        Assert.Contains("[CUIT] y [persona vinculada]", prompt, StringComparison.Ordinal);
    }
}
