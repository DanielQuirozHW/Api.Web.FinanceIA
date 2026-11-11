// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-31

using System.Security.Claims;
using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Covers PUT /api/billing/config on <see cref="BillingController"/>: the SuperAdmin write path.
/// Hand-rolled private sealed stubs, no mock library.
///
/// <para>
/// The behaviour worth pinning down is the preset-versus-custom resolution, because the endpoint
/// accepts two shapes through one body. A preset name supplies the limits; anything sent
/// explicitly overrides it; a label that matches no preset must carry both limits itself, since
/// the missing one has nowhere to come from. <c>PlanName</c> is always stored as given — the
/// column is an informational label, so an off-preset price under a familiar name is legitimate
/// and must not be rewritten to the preset's numbers.
/// </para>
///
/// <para>
/// The other property under test is that this write does NOT fail open. The read path resolves a
/// failure to the minimum plan on purpose; repeating that here would tell a SuperAdmin a plan was
/// saved when it was not.
/// </para>
/// </summary>
public sealed class BillingControllerConfigTests
{
    private const string Tenant = "tenant-1";
    private static readonly Guid Caller = new("11111111-1111-1111-1111-111111111111");

    // ── Stubs ───────────────────────────────────────────────────────────────────

    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private sealed class StubBillingConfigService : ITenantBillingConfigService
    {
        public Exception? ThrowOnUpdate { get; set; }
        public int    UpdateCallCount { get; private set; }
        public string?      TenantIdSeen  { get; private set; }
        public BillingPlan? PlanSeen      { get; private set; }
        public bool?        IsActiveSeen  { get; private set; }
        public string?      UpdatedBySeen { get; private set; }

        public Task<BillingPlanResolution> GetEffectiveAsync(string tenantId, CancellationToken ct)
            => Task.FromResult(new BillingPlanResolution(BillingPlans.Minimum, BillingPlanSource.NoConfiguration));

        public Task<TenantBillingConfig> UpdateAsync(
            string tenantId, BillingPlan plan, bool isActive, string updatedBy, CancellationToken ct)
        {
            UpdateCallCount++;

            if (ThrowOnUpdate is not null)
                return Task.FromException<TenantBillingConfig>(ThrowOnUpdate);

            TenantIdSeen  = tenantId;
            PlanSeen      = plan;
            IsActiveSeen  = isActive;
            UpdatedBySeen = updatedBy;

            return Task.FromResult(new TenantBillingConfig
            {
                BillingConfigId         = Guid.NewGuid(),
                TenantId                = tenantId,
                PlanName                = plan.PlanName,
                AnalysisMonthlyLimit    = plan.AnalysisMonthlyLimit,
                ChatTokensMonthlyLimit  = plan.ChatTokensMonthlyLimit,
                ChatContextBudgetTokens = plan.ChatContextBudgetTokens,
                // The real service writes the CSV form, so the stub does too — that is what makes the
                // entity's parsed ChatEnabledModels the thing under test on the way back out, rather
                // than the list the controller happened to hand in.
                ChatEnabledModelsRaw    = ChatModelCatalog.ToCsv(plan.ChatEnabledModels),
                IsActive                = isActive,
                UpdatedAt               = DateTime.UtcNow,
                UpdatedBy               = updatedBy
            });
        }
    }

    private sealed class StubBillingUsageService : IBillingUsageService
    {
        public Task<BillingUsageSnapshot> GetSnapshotAsync(string tenantId, CancellationToken ct)
            => throw new NotSupportedException("The config endpoint must not recount usage.");
    }

    private sealed class StubBillingAlertService : IBillingAlertService
    {
        public Task EvaluateAsync(string tenantId, BillingUsageSnapshot snapshot, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class StubAuditService : IAuditService
    {
        public int      CallCount    { get; private set; }
        public string?  Action       { get; private set; }
        public string?  EntityType   { get; private set; }
        public string?  EntityId     { get; private set; }
        public Guid?    UserId       { get; private set; }
        public string?  NewValueJson { get; private set; }

        public Task LogAsync(
            string tenantId, Guid? userId, string action, string severity = "Info",
            string? entityType = null, string? entityId = null, string? ipAddress = null,
            string? requestId = null, string? oldValueJson = null, string? newValueJson = null,
            CancellationToken ct = default)
        {
            CallCount++;
            Action       = action;
            EntityType   = entityType;
            EntityId     = entityId;
            UserId       = userId;
            NewValueJson = newValueJson;
            return Task.CompletedTask;
        }

        public Task LogSessionAsync(
            Guid tenantId, Guid? userId, string email, string eventType,
            string? ipAddress, string? userAgent, string? failureReason = null,
            CancellationToken ct = default)
            => Task.CompletedTask;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>TenantContext declares four required members, not just TenantId.</summary>
    private static TenantContext BuildTenant() => new()
    {
        TenantId               = Tenant,
        FirebaseProjectId      = "proj",
        FirebaseCredentialJson = "{}",
        RecaptchaSecretKey     = "secret"
    };

    private static BillingController BuildSut(
        StubBillingConfigService config,
        StubAuditService? audit = null,
        bool withTenant = true,
        bool withUserId = true)
    {
        var sut = new BillingController(
            new StubTenantContextAccessor { TenantContext = withTenant ? BuildTenant() : null },
            new StubBillingUsageService(),
            new StubBillingAlertService(),
            config,
            audit ?? new StubAuditService(),
            Options.Create(new BillingOptions()),
            null!,   // IScoringService — read only by GET usage, never by the config write path
            NullLogger<BillingController>.Instance);

        var claims = withUserId
            ? new[] { new Claim("userId", Caller.ToString()) }
            : [];

        sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
            }
        };
        return sut;
    }

    private static UpdateBillingConfigRequest Request(
        string planName = "200 análisis",
        int? analyses = null,
        long? chatTokens = null,
        int? contextBudget = null,
        bool? isActive = null,
        IReadOnlyList<string>? models = null) => new()
    {
        PlanName                = planName,
        AnalysisMonthlyLimit    = analyses,
        ChatTokensMonthlyLimit  = chatTokens,
        ChatContextBudgetTokens = contextBudget,
        ChatEnabledModels       = models,
        IsActive                = isActive
    };

    /// <summary>
    /// Deserialises a request body the way MVC would, so the CSV-or-array converter on
    /// <see cref="UpdateBillingConfigRequest.ChatEnabledModels"/> is exercised rather than bypassed by
    /// constructing the object directly. Web defaults, matching AddControllers().
    /// </summary>
    private static UpdateBillingConfigRequest Deserialize(string json) =>
        JsonSerializer.Deserialize<UpdateBillingConfigRequest>(
            json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static async Task<BillingConfigResponse> PutAsync(
        BillingController sut, UpdateBillingConfigRequest request)
    {
        var result = await sut.UpdateConfigAsync(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<BillingConfigResponse>(ok.Value);
    }

    private static async Task<ProblemDetails> ProblemAsync(
        BillingController sut, UpdateBillingConfigRequest request, int expectedStatus)
    {
        var result = await sut.UpdateConfigAsync(request, CancellationToken.None);
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(expectedStatus, objectResult.StatusCode);
        return Assert.IsType<ProblemDetails>(objectResult.Value);
    }

    // ── Preset path ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("50 análisis",  50,  2_000_000L, 40_000)]
    [InlineData("100 análisis", 100, 4_000_000L, 60_000)]
    [InlineData("200 análisis", 200, 8_000_000L, 80_000)]
    public async Task Put_WithAPresetNameAndNoLimits_AppliesThePresetsNumbers(
        string planName, int expectedAnalyses, long expectedTokens, int expectedContextBudget)
    {
        var config = new StubBillingConfigService();

        var body = await PutAsync(BuildSut(config), Request(planName));

        Assert.Equal(planName,              body.PlanName);
        Assert.Equal(expectedAnalyses,      body.AnalysisMonthlyLimit);
        Assert.Equal(expectedTokens,        body.ChatTokensMonthlyLimit);
        Assert.Equal(expectedContextBudget, body.ChatContextBudgetTokens);
    }

    /// <summary>Preset lookup is case-insensitive and trims, per BillingPlans.Find.</summary>
    [Fact]
    public async Task Put_MatchesAPresetCaseInsensitively_AndStoresTheLabelAsGiven()
    {
        var config = new StubBillingConfigService();

        var body = await PutAsync(BuildSut(config), Request("  200 Análisis  "));

        Assert.Equal(200, body.AnalysisMonthlyLimit);
        Assert.Equal("200 Análisis", body.PlanName);
    }

    // ── Custom path ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Put_WithBothExplicitLimitsAndAnUnknownLabel_StoresThemVerbatim()
    {
        var config = new StubBillingConfigService();

        var body = await PutAsync(
            BuildSut(config), Request("Plan negociado", analyses: 137, chatTokens: 5_500_000L));

        Assert.Equal("Plan negociado", body.PlanName);
        Assert.Equal(137,              body.AnalysisMonthlyLimit);
        Assert.Equal(5_500_000L,       body.ChatTokensMonthlyLimit);
    }

    /// <summary>
    /// The decided ambiguity rule. PlanName is an informational label that is never looked up to
    /// derive limits, so a tenant priced off-preset under a preset's name is a real commercial
    /// case — the explicit number wins and the label survives.
    /// </summary>
    [Fact]
    public async Task Put_WithAPresetNameAndExplicitLimits_TakesTheExplicitLimitsNotThePresets()
    {
        var config = new StubBillingConfigService();

        var body = await PutAsync(
            BuildSut(config), Request("100 análisis", analyses: 120, chatTokens: 9_000_000L));

        Assert.Equal("100 análisis", body.PlanName);
        Assert.Equal(120,            body.AnalysisMonthlyLimit);
        Assert.Equal(9_000_000L,     body.ChatTokensMonthlyLimit);
    }

    /// <summary>A preset fills only the gaps a partial body leaves.</summary>
    [Fact]
    public async Task Put_WithAPresetNameAndOneExplicitLimit_OverridesThatOneAndKeepsThePresetsOther()
    {
        var config = new StubBillingConfigService();

        var body = await PutAsync(BuildSut(config), Request("100 análisis", analyses: 120));

        Assert.Equal(120,        body.AnalysisMonthlyLimit);
        Assert.Equal(4_000_000L, body.ChatTokensMonthlyLimit);
        Assert.Equal(60_000,     body.ChatContextBudgetTokens);
    }

    /// <summary>The context budget follows the same override rule as the two monthly limits.</summary>
    [Fact]
    public async Task Put_WithAPresetNameAndAnExplicitContextBudget_TakesTheExplicitOne()
    {
        var config = new StubBillingConfigService();

        var body = await PutAsync(
            BuildSut(config), Request("100 análisis", contextBudget: 12_345));

        Assert.Equal("100 análisis", body.PlanName);
        Assert.Equal(100,            body.AnalysisMonthlyLimit);   // still the preset's
        Assert.Equal(4_000_000L,     body.ChatTokensMonthlyLimit); // still the preset's
        Assert.Equal(12_345,         body.ChatContextBudgetTokens);
    }

    /// <summary>
    /// The one place the context budget does NOT behave like the two monthly limits, and
    /// deliberately: it has a third fallback, so omitting it off-preset is not a 400. Requiring it
    /// would have broken every existing caller that sends just the two monthly limits for an
    /// off-preset plan, and BillingPlans.Minimum's budget is what an unconfigured tenant already
    /// resolves to.
    /// </summary>
    [Fact]
    public async Task Put_WithNoPresetMatchAndNoContextBudget_FallsBackToTheMinimumPlansBudget()
    {
        var config = new StubBillingConfigService();

        var body = await PutAsync(
            BuildSut(config), Request("Plan negociado", analyses: 137, chatTokens: 5_500_000L));

        Assert.Equal(BillingPlans.Minimum.ChatContextBudgetTokens, body.ChatContextBudgetTokens);
        Assert.Equal(40_000, body.ChatContextBudgetTokens);
    }

    // ── Neither preset nor limits ───────────────────────────────────────────────

    [Fact]
    public async Task Put_WithNoPresetMatchAndNoLimits_Returns400()
    {
        var config = new StubBillingConfigService();

        var problem = await ProblemAsync(BuildSut(config), Request("Plan inventado"), 400);

        Assert.Contains("plan predefinido", problem.Detail);
        Assert.Equal(0, config.UpdateCallCount);
    }

    [Theory]
    [InlineData(137, null)]
    [InlineData(null, 5_500_000L)]
    public async Task Put_WithNoPresetMatchAndOnlyOneLimit_Returns400(int? analyses, long? chatTokens)
    {
        var config = new StubBillingConfigService();

        await ProblemAsync(BuildSut(config), Request("Plan inventado", analyses, chatTokens), 400);

        Assert.Equal(0, config.UpdateCallCount);
    }

    // ── Validation ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Put_WithoutAPlanName_Returns400(string planName)
    {
        var config = new StubBillingConfigService();

        var problem = await ProblemAsync(BuildSut(config), Request(planName), 400);

        Assert.Equal("El nombre del plan es obligatorio.", problem.Detail);
        Assert.Equal(0, config.UpdateCallCount);
    }

    /// <summary>[PlanName] is NVARCHAR(100); a longer value would fail as a 500 truncation.</summary>
    [Fact]
    public async Task Put_WithAPlanNameLongerThanTheColumn_Returns400()
    {
        var config = new StubBillingConfigService();

        var problem = await ProblemAsync(
            BuildSut(config), Request(new string('x', 101), analyses: 10, chatTokens: 10L), 400);

        Assert.Contains("100 caracteres", problem.Detail);
        Assert.Equal(0, config.UpdateCallCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Put_WithANonPositiveAnalysisLimit_Returns400(int analyses)
    {
        var config = new StubBillingConfigService();

        var problem = await ProblemAsync(
            BuildSut(config), Request("Plan negociado", analyses, 5_500_000L), 400);

        Assert.Contains("mayor a cero", problem.Detail);
        Assert.Equal(0, config.UpdateCallCount);
    }

    [Fact]
    public async Task Put_WithANegativeChatTokenLimit_Returns400()
    {
        var config = new StubBillingConfigService();

        var problem = await ProblemAsync(
            BuildSut(config), Request("Plan negociado", 137, -1L), 400);

        Assert.Contains("negativo", problem.Detail);
        Assert.Equal(0, config.UpdateCallCount);
    }

    /// <summary>Zero is a real setting — a tenant with no chat allowance — not garbage.</summary>
    [Fact]
    public async Task Put_WithAZeroChatTokenLimit_IsAccepted()
    {
        var config = new StubBillingConfigService();

        var body = await PutAsync(BuildSut(config), Request("Plan sin chat", 137, 0L));

        Assert.Equal(0L, body.ChatTokensMonthlyLimit);
    }

    /// <summary>
    /// The context budget is stricter than the monthly pool above, and the asymmetry is the point:
    /// a zero monthly pool is a coherent thing to sell, a zero per-conversation context is a chat
    /// that cannot carry its own system prompt.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Put_WithANonPositiveContextBudget_Returns400(int contextBudget)
    {
        var config = new StubBillingConfigService();

        var problem = await ProblemAsync(
            BuildSut(config),
            Request("Plan negociado", analyses: 137, chatTokens: 5_500_000L, contextBudget: contextBudget),
            400);

        Assert.Equal("El presupuesto de contexto del chat debe ser mayor a cero.", problem.Detail);
        Assert.Equal(0, config.UpdateCallCount);
    }

    // ── IsActive ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Put_WithoutIsActive_DefaultsToActive()
    {
        var config = new StubBillingConfigService();

        var body = await PutAsync(BuildSut(config), Request());

        Assert.True(body.IsActive);
        Assert.True(config.IsActiveSeen);
    }

    [Fact]
    public async Task Put_WithIsActiveFalse_DeactivatesWithoutDeleting()
    {
        var config = new StubBillingConfigService();

        var body = await PutAsync(BuildSut(config), Request(isActive: false));

        Assert.False(body.IsActive);
        Assert.False(config.IsActiveSeen);
        Assert.Equal("200 análisis", body.PlanName);
    }

    // ── Caller identity and tenant scope ────────────────────────────────────────

    [Fact]
    public async Task Put_StampsUpdatedByWithTheCallersUserId()
    {
        var config = new StubBillingConfigService();

        var body = await PutAsync(BuildSut(config), Request());

        Assert.Equal(Caller.ToString(), config.UpdatedBySeen);
        Assert.Equal(Caller.ToString(), body.UpdatedBy);
        Assert.NotEqual(default, body.UpdatedAt);
    }

    /// <summary>
    /// The endpoint writes the tenant the caller is authenticated in — from TenantContext, never
    /// from the body. Targeting an arbitrary tenant is a separate, out-of-scope delivery.
    /// </summary>
    [Fact]
    public async Task Put_WritesTheTenantFromTenantContext()
    {
        var config = new StubBillingConfigService();

        await PutAsync(BuildSut(config), Request());

        Assert.Equal(Tenant, config.TenantIdSeen);
    }

    [Fact]
    public async Task Put_WithoutATenantContext_Returns500AndDoesNotWrite()
    {
        var config = new StubBillingConfigService();

        await ProblemAsync(BuildSut(config, withTenant: false), Request(), 500);

        Assert.Equal(0, config.UpdateCallCount);
    }

    [Fact]
    public async Task Put_WithoutAUserIdClaim_Returns401AndDoesNotWrite()
    {
        var config = new StubBillingConfigService();

        await ProblemAsync(BuildSut(config, withUserId: false), Request(), 401);

        Assert.Equal(0, config.UpdateCallCount);
    }

    // ── Failure surfacing ───────────────────────────────────────────────────────

    /// <summary>
    /// The read path resolves a failure to the minimum plan so enforcement can fail open. The
    /// write must not: a 200 here would tell the SuperAdmin a plan was saved when nothing was.
    /// </summary>
    [Fact]
    public async Task Put_WhenTheWriteFails_Returns500RatherThanSwallowingIt()
    {
        var config = new StubBillingConfigService
        {
            ThrowOnUpdate = new InvalidOperationException("db down")
        };
        var audit = new StubAuditService();

        var problem = await ProblemAsync(BuildSut(config, audit), Request(), 500);

        Assert.Equal("No se pudo guardar la configuración de facturación.", problem.Detail);

        // And nothing is audited as a change that never happened.
        Assert.Equal(0, audit.CallCount);
    }

    // ── Audit ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Put_AuditsTheChangeOnceWithTheActingUser()
    {
        var config = new StubBillingConfigService();
        var audit  = new StubAuditService();

        await PutAsync(BuildSut(config, audit), Request());

        Assert.Equal(1, audit.CallCount);
        Assert.Equal("Billing.ConfigUpdated", audit.Action);
        Assert.Equal("TenantBillingConfig",   audit.EntityType);
        Assert.Equal(Caller,                  audit.UserId);
        Assert.False(string.IsNullOrWhiteSpace(audit.EntityId));
    }

    /// <summary>
    /// The audit payload records the commercial numbers, and nothing that identifies a person.
    ///
    /// <para>
    /// Asserted on the PARSED document rather than the raw string: the default encoder escapes
    /// non-ASCII, so the accented plan label is stored as "200 an\u00E1lisis". That is correct
    /// JSON and reads back identically — matching on the raw text would only be testing the
    /// encoder.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Put_AuditPayloadCarriesThePlanAndLimits()
    {
        var config = new StubBillingConfigService();
        var audit  = new StubAuditService();

        await PutAsync(BuildSut(config, audit), Request("200 análisis"));

        Assert.NotNull(audit.NewValueJson);

        using var document = JsonDocument.Parse(audit.NewValueJson);
        var root = document.RootElement;

        Assert.Equal("200 análisis", root.GetProperty("PlanName").GetString());
        Assert.Equal(200,            root.GetProperty("AnalysisMonthlyLimit").GetInt32());
        Assert.Equal(8_000_000L,     root.GetProperty("ChatTokensMonthlyLimit").GetInt64());
        Assert.Equal(80_000,         root.GetProperty("ChatContextBudgetTokens").GetInt32());
        Assert.True(root.GetProperty("IsActive").GetBoolean());
    }

    // ── Chat model allow-list ───────────────────────────────────────────────────
    //
    // The one field on this endpoint that is not a NUMBER, and not an allowance: it is the set of
    // Claude models the tenant's analysts may pick between. Nothing meters it, and the interesting
    // failures are about what gets STORED rather than about what gets refused — an id that survives
    // the write but not the read would narrow a tenant's access with no error anywhere.

    [Fact]
    public async Task Put_WithAnExplicitModelList_StoresExactlyThoseModels()
    {
        var config = new StubBillingConfigService();

        var response = await PutAsync(
            BuildSut(config),
            Request("200 análisis", models: ["claude-haiku-4-5", "claude-sonnet-4-6"]));

        Assert.Equal(new[] { "claude-haiku-4-5", "claude-sonnet-4-6" }, config.PlanSeen!.ChatEnabledModels);
        Assert.Equal(new[] { "claude-haiku-4-5", "claude-sonnet-4-6" }, response.ChatEnabledModels);
    }

    [Fact]
    public async Task Put_WithModelsSentAsAJsonArray_ReadsThem()
    {
        // The Portal's spelling: a checkbox panel serialises to an array.
        var request = Deserialize(
            """{"planName":"200 análisis","chatEnabledModels":["claude-haiku-4-5","claude-sonnet-5"]}""");

        var response = await PutAsync(BuildSut(new StubBillingConfigService()), request);

        Assert.Equal(new[] { "claude-haiku-4-5", "claude-sonnet-5" }, response.ChatEnabledModels);
    }

    [Fact]
    public async Task Put_WithModelsSentAsACommaSeparatedString_ReadsThemToo()
    {
        // The operator's spelling: the value copied straight out of [ChatEnabledModels] or out of the
        // DDL script. Both forms have to reach the same stored state, or a correct-looking request
        // fails for a reason that is purely encoding.
        var request = Deserialize(
            """{"planName":"200 análisis","chatEnabledModels":"claude-haiku-4-5,claude-sonnet-5"}""");

        var response = await PutAsync(BuildSut(new StubBillingConfigService()), request);

        Assert.Equal(new[] { "claude-haiku-4-5", "claude-sonnet-5" }, response.ChatEnabledModels);
    }

    [Fact]
    public async Task Put_NormalisesTheStoredOrderToTheCatalogues()
    {
        // Not cosmetic: the FIRST enabled model is what a chat turn falls back to when the analyst
        // picks none, so a tenant's default must not depend on the order a SuperAdmin ticked boxes in.
        var response = await PutAsync(
            BuildSut(new StubBillingConfigService()),
            Request("200 análisis", models: ["claude-sonnet-5", "claude-haiku-4-5"]));

        Assert.Equal(new[] { "claude-haiku-4-5", "claude-sonnet-5" }, response.ChatEnabledModels);
    }

    [Fact]
    public async Task Put_WithAnUnknownModelId_IsRejectedAndNamesIt()
    {
        // Named back rather than dropped. ChatModelCatalog discards unrecognised ids on the READ path
        // — that is what retires a model without a data migration — so accepting one here would store
        // a value that vanishes on the next read, silently narrowing the tenant's access.
        var config = new StubBillingConfigService();

        var problem = await ProblemAsync(
            BuildSut(config),
            Request("200 análisis", models: ["claude-haiku-4-5", "claude-opus-9"]),
            400);

        Assert.Contains("claude-opus-9", problem.Detail!, StringComparison.Ordinal);
        Assert.Equal(0, config.UpdateCallCount);
    }

    [Fact]
    public async Task Put_WithADateSuffixedModelId_IsRejected()
    {
        // The specific typo worth a test of its own: model ids carry no date suffix, and appending
        // one names a model that does not exist. It looks plausible enough to survive review.
        var problem = await ProblemAsync(
            BuildSut(new StubBillingConfigService()),
            Request("200 análisis", models: ["claude-sonnet-4-6-20251114"]),
            400);

        Assert.Contains("claude-sonnet-4-6-20251114", problem.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Put_WithAnEmptyModelList_IsRejected()
    {
        // A tenant with no chat model at all is not a plan, it is a broken chat — and [] is far more
        // likely to be a client sending "unchanged" wrongly than an intention. Omitting the field is
        // how "unchanged" is actually expressed.
        var config = new StubBillingConfigService();

        var problem = await ProblemAsync(
            BuildSut(config), Request("200 análisis", models: []), 400);

        Assert.Equal("Debe habilitar al menos un modelo de chat.", problem.Detail);
        Assert.Equal(0, config.UpdateCallCount);
    }

    [Fact]
    public async Task Put_WithAnEmptyStringForTheModels_IsRejectedToo()
    {
        // The CSV spelling of the same mistake. The converter must not quietly turn "" into null and
        // let it fall through to the preset default — that would apply a plan the caller never asked
        // for and report success.
        var request = Deserialize("""{"planName":"200 análisis","chatEnabledModels":""}""");

        var problem = await ProblemAsync(BuildSut(new StubBillingConfigService()), request, 400);

        Assert.Equal("Debe habilitar al menos un modelo de chat.", problem.Detail);
    }

    [Fact]
    public async Task Put_WithoutTheModelField_FallsBackToThePresetsList()
    {
        // The compatibility guarantee: a caller written before this field existed must keep working
        // unchanged, which is why omitting it never produces a 400 the way an off-preset limit does.
        var request = Deserialize("""{"planName":"200 análisis"}""");

        var response = await PutAsync(BuildSut(new StubBillingConfigService()), request);

        Assert.Equal(BillingPlans.Plan200.ChatEnabledModels, response.ChatEnabledModels);
    }

    [Fact]
    public async Task Put_OffPresetWithoutTheModelField_FallsBackToTheMinimumsList()
    {
        // No preset to borrow from, and still no 400 — unlike the two monthly limits, this field
        // always has somewhere to fall back to.
        var response = await PutAsync(
            BuildSut(new StubBillingConfigService()),
            Request("Plan negociado", analyses: 137, chatTokens: 5_500_000L));

        Assert.Equal(BillingPlans.Minimum.ChatEnabledModels, response.ChatEnabledModels);
    }

    [Fact]
    public async Task Put_AuditPayloadCarriesTheModelList()
    {
        // A change to who may call which model is exactly the kind of thing an auditor asks about
        // after the fact, and the audit row is the only record of who made it.
        var audit = new StubAuditService();

        await PutAsync(
            BuildSut(new StubBillingConfigService(), audit),
            Request("200 análisis", models: ["claude-haiku-4-5"]));

        using var document = JsonDocument.Parse(audit.NewValueJson!);

        var models = document.RootElement.GetProperty("ChatEnabledModels");

        Assert.Equal(JsonValueKind.Array, models.ValueKind);
        Assert.Equal(1, models.GetArrayLength());
        Assert.Equal("claude-haiku-4-5", models[0].GetString());
    }
}
