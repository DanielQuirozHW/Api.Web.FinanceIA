// Project : Api.Web.FinanceIA
// Created : 2026-08-31

using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Api.Web.FinanceIA.Controllers;

/// <summary>
/// Exposes a tenant's consumption against its billing plan.
/// All endpoints require an internal JWT (<c>Internal</c> scheme).
///
/// <para>
/// Separate from <c>UsageController</c> on purpose. That one reports AI token telemetry —
/// per-operation and per-day breakdowns for whoever is watching spend. This one reports a
/// commercial entitlement, is what the Portal polls to render a quota banner, and carries the
/// SuperAdmin write path that sets it.
/// </para>
/// </summary>
[ApiController]
[Route("api/billing")]
[Authorize(AuthenticationSchemes = "Internal")]
public sealed class BillingController : ControllerBase
{
    private readonly ITenantContextAccessor        _tenantContextAccessor;
    private readonly IBillingUsageService          _billingUsageService;
    private readonly IBillingAlertService          _billingAlertService;
    private readonly ITenantBillingConfigService   _billingConfigService;
    private readonly IAuditService                 _auditService;
    private readonly IOptions<BillingOptions>      _billingOptions;
    private readonly IScoringService               _scoringService;
    private readonly ILogger<BillingController>    _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="BillingController"/>.
    /// </summary>
    public BillingController(
        ITenantContextAccessor      tenantContextAccessor,
        IBillingUsageService        billingUsageService,
        IBillingAlertService        billingAlertService,
        ITenantBillingConfigService billingConfigService,
        IAuditService               auditService,
        IOptions<BillingOptions>    billingOptions,
        IScoringService             scoringService,
        ILogger<BillingController>  logger)
    {
        _tenantContextAccessor = tenantContextAccessor;
        _billingUsageService   = billingUsageService;
        _billingAlertService   = billingAlertService;
        _billingConfigService  = billingConfigService;
        _auditService          = auditService;
        _billingOptions        = billingOptions;
        _scoringService        = scoringService;
        _logger                = logger;
    }

    /// <summary>
    /// Returns the calling tenant's usage against its plan for the current UTC calendar month.
    /// </summary>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with <see cref="BillingUsageResponse"/>.</returns>
    /// <remarks>
    /// The same snapshot the enforcement filter gates on, so what this reports and what refuses
    /// an analysis are the one computation. The period always resets on the 1st at 00:00 UTC;
    /// there is no partial-month proration and no per-tenant billing anniversary.
    ///
    /// <para>
    /// It also carries <c>ChatContextBudgetTokens</c>, which is NOT a usage figure: it is the
    /// per-conversation ceiling from the tenant's plan, reported here so a client has the
    /// denominator without needing the SuperAdmin write endpoint. Reading the plan is open to every
    /// authenticated tenant user; only setting it is SuperAdmin.
    /// </para>
    ///
    /// <para>
    /// And <c>ChatEnabledModels</c>, which is not a figure at all: the ids of the Claude chat models
    /// this tenant may use, which is what the Portal populates its per-conversation model selector
    /// from. It rides this endpoint for the same reason as the budget above — it comes from the same
    /// plan resolution, and a client that already polls here for the quota banner should not need a
    /// second, SuperAdmin-only call to render a dropdown. Beside it rides
    /// <c>DefaultChatModel</c>, which of those ids to render pre-selected — stated rather than left
    /// to the client, because the list is ordered cheapest-first and its first entry is therefore
    /// not the product default.
    /// </para>
    ///
    /// <para>
    /// And <c>LevelCutoffs</c>, the tenant's CURRENT risk-level cutoffs — scoring configuration, not
    /// billing. It rides here because this is the one tenant read every role (Analyst included) may
    /// call and the Portal already polls: a client needs it to label rows whose score breakdown
    /// predates <c>levelCutoffs</c>, and must not fall back to hardcoded numbers instead. Its read
    /// is independent: a failure yields null and touches neither the figures nor the status code.
    /// </para>
    /// </remarks>
    [HttpGet("usage")]
    [ProducesResponseType(typeof(BillingUsageResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> GetUsageAsync(CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var snapshot = await _billingUsageService
            .GetSnapshotAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        // Emit the 80% / 100% threshold alerts this snapshot has just made due.
        //
        // This is the trigger for them because the Portal polls this endpoint, so the alert appears
        // even for a tenant that has stopped creating analyses and would otherwise never hit the
        // enforcement gate again.
        //
        // It stays on the request's own async flow rather than a background Task.Run: the work runs
        // through the request-scoped FinanciaDbContext, which is not thread-safe and is disposed
        // with the scope, so handing it to a detached task would race the disposal. Awaiting is
        // affordable because the service short-circuits below 80% without touching the database.
        //
        // EvaluateAsync never throws — it owns its own try/catch — so the response below is
        // unreachable from any alert failure, and neither the body nor the status changes.
        await _billingAlertService
            .EvaluateAsync(tenantId, snapshot, cancellationToken)
            .ConfigureAwait(false);

        var levelCutoffs = await ReadLevelCutoffsAsync(tenantId, cancellationToken).ConfigureAwait(false);

        return Ok(ToResponse(snapshot, _billingOptions.Value.EnforceChatTokens, levelCutoffs));
    }

    /// <summary>
    /// The tenant's current level cutoffs, or null when they could not be read. Isolated from the
    /// usage snapshot on purpose: a failure here never degrades the quota figures or the status code,
    /// and it is never papered over with the defaults — a tenant with custom cutoffs would then have
    /// its old rows labelled with numbers it does not use, which is the bug this field exists to end.
    /// </summary>
    private async Task<ScoreLevelCutoffs?> ReadLevelCutoffsAsync(string tenantId, CancellationToken ct)
    {
        try
        {
            return await _scoringService.GetLevelCutoffsAsync(tenantId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Could not read the score level cutoffs for tenant {TenantId}; billing usage returns levelCutoffs null",
                tenantId);
            return null;
        }
    }

    /// <summary>
    /// Sets the calling tenant's billing configuration. SuperAdmin only.
    /// </summary>
    /// <param name="request">Plan label, and either a preset match or explicit limits.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with <see cref="BillingConfigResponse"/>.</returns>
    /// <remarks>
    /// An idempotent upsert onto the tenant's single row (UQ_TenantBillingConfig_Tenant), so the
    /// same request applied twice leaves the same state.
    ///
    /// <para>
    /// It writes the configuration of the tenant the CALLER IS AUTHENTICATED IN — resolved from
    /// TenantContext, never from the body or the route. A cross-stack panel that targets an
    /// arbitrary tenant is a separate delivery; accepting a target tenant here would turn one
    /// stack's SuperAdmin into every stack's.
    /// </para>
    /// </remarks>
    [HttpPut("config")]
    [Authorize(Roles = Roles.SuperAdmin)]
    [ProducesResponseType(typeof(BillingConfigResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> UpdateConfigAsync(
        [FromBody] UpdateBillingConfigRequest request,
        CancellationToken cancellationToken)
    {
        var (tenantId, tenantError) = ResolveTenant();
        if (tenantError is not null) return tenantError;

        var (userId, userError) = ResolveUserId();
        if (userError is not null) return userError;

        if (string.IsNullOrWhiteSpace(request.PlanName))
        {
            return Problem(
                statusCode: 400, title: "Bad Request",
                detail: "El nombre del plan es obligatorio.");
        }

        var planName = request.PlanName.Trim();

        // [PlanName] is NVARCHAR(100); without this the write would fail as a 500 truncation.
        if (planName.Length > 100)
        {
            return Problem(
                statusCode: 400, title: "Bad Request",
                detail: "El nombre del plan no puede superar los 100 caracteres.");
        }

        if (request.AnalysisMonthlyLimit is <= 0)
        {
            return Problem(
                statusCode: 400, title: "Bad Request",
                detail: "El límite de análisis debe ser mayor a cero.");
        }

        if (request.ChatTokensMonthlyLimit is < 0)
        {
            return Problem(
                statusCode: 400, title: "Bad Request",
                detail: "El límite de tokens de chat no puede ser negativo.");
        }

        // Greater than zero, not merely non-negative like the monthly pool above. A zero monthly
        // pool is a coherent thing to sell (a tenant with no chat allowance at all); a zero
        // per-conversation context budget is not a plan, it is a chat that cannot carry its own
        // system prompt.
        if (request.ChatContextBudgetTokens is <= 0)
        {
            return Problem(
                statusCode: 400, title: "Bad Request",
                detail: "El presupuesto de contexto del chat debe ser mayor a cero.");
        }

        // Validated here rather than normalised away, and the two failures are kept apart because
        // they are different mistakes. An EMPTY list is a coherent request with an incoherent
        // outcome — a tenant whose analysts cannot chat at all — and is far likelier to be a client
        // sending [] for "unchanged" than an intention, so it is refused rather than stored.
        // (Omitting the field entirely IS how "unchanged" is expressed; see below.)
        if (request.ChatEnabledModels is { Count: 0 })
        {
            return Problem(
                statusCode: 400, title: "Bad Request",
                detail: "Debe habilitar al menos un modelo de chat.");
        }

        // An UNKNOWN id is a typo, and it is named back to the caller. ChatModelCatalog silently
        // drops anything it does not recognise on the READ path — that is what makes a retired model
        // disappear without a data migration — so accepting one here would store a value that
        // vanishes on the next read and narrow the tenant's access with no error anywhere.
        if (request.ChatEnabledModels is not null)
        {
            foreach (var modelId in request.ChatEnabledModels)
            {
                if (!ChatModelCatalog.Contains(modelId))
                {
                    return Problem(
                        statusCode: 400, title: "Bad Request",
                        detail: $"El modelo de chat '{modelId}' no existe.");
                }
            }
        }

        // Preset supplies the baseline; anything sent explicitly overrides it. PlanName is stored
        // as given and never rewritten to the preset's label, because the column is informational
        // and a tenant may be priced off-preset under a familiar name.
        var preset = BillingPlans.Find(planName);

        var analysisLimit   = request.AnalysisMonthlyLimit   ?? preset?.AnalysisMonthlyLimit;
        var chatTokensLimit = request.ChatTokensMonthlyLimit ?? preset?.ChatTokensMonthlyLimit;

        if (analysisLimit is null || chatTokensLimit is null)
        {
            return Problem(
                statusCode: 400, title: "Bad Request",
                detail: "Debe indicar un plan predefinido válido o los dos límites explícitos.");
        }

        // Resolved on the same preset-or-explicit rule as the two above, with one difference: it
        // has a third fallback, so it can never be null and never contributes to the 400 above.
        // Requiring it off-preset would have broken every existing caller that sends just the two
        // monthly limits, and the minimum plan's budget is the same thing an unconfigured tenant
        // already resolves to.
        var contextBudget = request.ChatContextBudgetTokens
                            ?? preset?.ChatContextBudgetTokens
                            ?? BillingPlans.Minimum.ChatContextBudgetTokens;

        // Same three-fallback shape as the budget above, so omitting the field never produces a 400
        // and a caller written before it existed keeps working. Every id is already known to be in
        // the catalogue by this point; SanitizeOrDefault is still applied because it also de-duplicates
        // and imposes the catalogue's order, and that order decides which model a chat turn falls
        // back to — the stored value should not depend on the order the caller happened to type.
        var enabledModels = ChatModelCatalog.SanitizeOrDefault(
            request.ChatEnabledModels is not null
                ? ChatModelCatalog.ToCsv(request.ChatEnabledModels)
                : ChatModelCatalog.ToCsv(preset?.ChatEnabledModels ?? BillingPlans.Minimum.ChatEnabledModels));

        var plan     = new BillingPlan(
            planName, analysisLimit.Value, chatTokensLimit.Value, contextBudget, enabledModels);
        var isActive = request.IsActive ?? true;

        TenantBillingConfig saved;

        try
        {
            saved = await _billingConfigService
                .UpdateAsync(tenantId, plan, isActive, userId.ToString(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Deliberately NOT fail-open. Fail-open belongs to the read path, where refusing a
            // paying customer over a database blip is the worse outcome; here, reporting success
            // for a plan change that did not happen is.
            _logger.LogError(
                ex,
                "Failed to update billing configuration for tenant {TenantId} by user {UserId}.",
                tenantId,
                userId);

            return Problem(
                statusCode: 500, title: "Internal Server Error",
                detail: "No se pudo guardar la configuración de facturación.");
        }

        // AuditService owns its try/catch and runs on its own DI scope, so awaiting it cannot fail
        // the write that already committed above.
        await _auditService.LogAsync(
            tenantId:     tenantId,
            userId:       userId,
            action:       "Billing.ConfigUpdated",
            severity:     "Info",
            entityType:   "TenantBillingConfig",
            entityId:     saved.BillingConfigId.ToString(),
            ipAddress:    HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:    HttpContext.TraceIdentifier,
            newValueJson: JsonSerializer.Serialize(new
            {
                saved.PlanName,
                saved.AnalysisMonthlyLimit,
                saved.ChatTokensMonthlyLimit,
                saved.ChatContextBudgetTokens,
                saved.ChatEnabledModels,
                saved.IsActive
            }),
            ct: cancellationToken).ConfigureAwait(false);

        return Ok(new BillingConfigResponse
        {
            PlanName                = saved.PlanName,
            AnalysisMonthlyLimit    = saved.AnalysisMonthlyLimit,
            ChatTokensMonthlyLimit  = saved.ChatTokensMonthlyLimit,
            ChatContextBudgetTokens = saved.ChatContextBudgetTokens,
            ChatEnabledModels       = saved.ChatEnabledModels,
            IsActive                = saved.IsActive,
            UpdatedAt               = saved.UpdatedAt,
            UpdatedBy               = saved.UpdatedBy
        });
    }

    /// <summary>
    /// Projects a snapshot onto the API shape, rendering both enums as strings.
    /// </summary>
    private static BillingUsageResponse ToResponse(
        BillingUsageSnapshot snapshot, bool chatTokensEnforced, ScoreLevelCutoffs? levelCutoffs) =>
        new()
        {
            PlanName                = snapshot.PlanName,
            PeriodStart             = snapshot.PeriodStart,
            PeriodEnd               = snapshot.PeriodEnd,
            AnalysesUsed            = snapshot.AnalysesUsed,
            AnalysesLimit           = snapshot.AnalysesLimit,
            AnalysesRemaining       = snapshot.AnalysesRemaining,
            AnalysesPercent         = snapshot.AnalysesPercent,
            ChatTokensUsed          = snapshot.ChatTokensUsed,
            ChatTokensLimit         = snapshot.ChatTokensLimit,
            ChatTokensRemaining     = snapshot.ChatTokensRemaining,
            ChatTokensPercent       = snapshot.ChatTokensPercent,
            ChatContextBudgetTokens = snapshot.ChatContextBudgetTokens,
            ChatEnabledModels       = snapshot.ChatEnabledModels,
            DefaultChatModel        = ResolveDefaultChatModel(snapshot.ChatEnabledModels),
            ChatTokensSource        = snapshot.ChatTokensSource.ToString(),
            ChatTokensEnforced      = chatTokensEnforced,
            IsDegraded              = snapshot.IsDegraded,
            DegradedReason          = snapshot.DegradedReason,
            LevelCutoffs            = levelCutoffs
        };

    /// <summary>
    /// The model a client should render pre-selected: the product default when the tenant is
    /// entitled to it, otherwise the first model it IS entitled to — so the answer is always a
    /// member of the list beside it, never an option the selector cannot show.
    ///
    /// <para>
    /// Stated here rather than left to the client, because the list is ordered cheapest-first and a
    /// client inferring the default from position picks the cheap model, not the intended one. It
    /// does NOT alter what a turn naming no model resolves to — that is still the first entry, in
    /// AnalysisController.
    /// </para>
    /// </summary>
    private static string ResolveDefaultChatModel(IReadOnlyList<string> enabledModels) =>
        enabledModels.Contains(ChatModelCatalog.Default.Id, StringComparer.Ordinal)
            ? ChatModelCatalog.Default.Id
            // Belt-and-braces, mirroring AnalysisController: SanitizeOrDefault guarantees a non-empty
            // list, so [0] is safe by construction. Written out anyway because the cost of being
            // wrong is an IndexOutOfRangeException on a quota read, and the cost of the check is one
            // comparison.
            : enabledModels.Count > 0 ? enabledModels[0] : ChatModelCatalog.Default.Id;

    /// <summary>
    /// Resolves the tenant for the current request, mirroring UsageController.
    /// </summary>
    private (string tenantId, IActionResult? error) ResolveTenant()
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in BillingController");
            return (string.Empty, Problem(
                statusCode: 500, title: "Internal Server Error",
                detail: "Tenant context not resolved."));
        }

        return (tenantContext.TenantId, null);
    }

    /// <summary>
    /// Resolves the calling user's id from the internal JWT, reading the same claims as
    /// <c>SettingsController.ExtractUserId</c>.
    /// </summary>
    /// <remarks>
    /// Returns a tuple rather than throwing, matching <see cref="ResolveTenant"/>: an
    /// UnauthorizedAccessException is not mapped anywhere in the pipeline, so throwing would
    /// surface a missing claim as a 500 instead of the 401 it is.
    /// </remarks>
    private (Guid userId, IActionResult? error) ResolveUserId()
    {
        var claim = User.FindFirst("userId")?.Value
                    ?? User.FindFirst("sub")?.Value;

        if (string.IsNullOrWhiteSpace(claim) || !Guid.TryParse(claim, out var userId))
        {
            // The claim value itself is never logged — it identifies a person.
            _logger.LogWarning("UserId claim missing or not a Guid in BillingController");
            return (Guid.Empty, Problem(
                statusCode: 401, title: "Unauthorized",
                detail: "No se pudo identificar al usuario."));
        }

        return (userId, null);
    }
}
