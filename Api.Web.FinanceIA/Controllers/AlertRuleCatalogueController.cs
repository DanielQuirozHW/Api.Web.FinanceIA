// Project : Api.Web.FinanceIA
// Created : 2026-08-16

using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Web.FinanceIA.Controllers;

/// <summary>
/// Per-tenant configuration of the deterministic alert rule catalogue ([dbo].[AlertRuleCatalogue]),
/// the closed rule set <see cref="IAlertRuleEngine"/> evaluates.
///
/// READ is open to every role: which rules exist and at what threshold is the analyst's working
/// information — it is what explains a catalogue alert on a report they are deciding on.
/// WRITE is Admin only and always audited: a threshold change alters which alerts fire, and
/// therefore what the SGR decides on.
///
/// There is NO create and NO delete, by design. The code set is closed because a rule needs an
/// engine implementation; a catalogue row without one would be a configurable rule that silently
/// never fires. <c>Code</c> is immutable through this API for the same reason.
/// </summary>
[ApiController]
[Route("api/alert-rules")]
[Route("api/bff/alert-rules")]
[Authorize(AuthenticationSchemes = "Internal")]
public sealed class AlertRuleCatalogueController : ControllerBase
{
    /// <summary>
    /// Allowed severities, lowercase, pinned by CK_AlertRuleCatalogue_Severity in DDL 038.
    /// Validated here so a bad value is a clear 400 rather than a raw SQL constraint violation.
    /// </summary>
    private static readonly string[] ValidSeverities = ["low", "medium", "high", "critical"];

    // DECIMAL(10,4): 10 significant digits of which 4 are decimals, so |value| < 10^6.
    private const decimal MaxThresholdExclusive = 1_000_000m;
    private const int     ThresholdScale        = 4;

    private readonly ITenantContextAccessor                  _tenantContextAccessor;
    private readonly IAlertRuleCatalogueRepository           _catalogueRepository;
    private readonly IUserRepository                         _userRepository;
    private readonly IAuditService                           _auditService;
    private readonly ILogger<AlertRuleCatalogueController>   _logger;

    public AlertRuleCatalogueController(
        ITenantContextAccessor                 tenantContextAccessor,
        IAlertRuleCatalogueRepository          catalogueRepository,
        IUserRepository                        userRepository,
        IAuditService                          auditService,
        ILogger<AlertRuleCatalogueController>  logger)
    {
        _tenantContextAccessor = tenantContextAccessor;
        _catalogueRepository   = catalogueRepository;
        _userRepository        = userRepository;
        _auditService          = auditService;
        _logger                = logger;
    }

    // ── GET /api/alert-rules ───────────────────────────────────────────────────

    /// <summary>
    /// Returns every catalogue rule for the current tenant, enabled or not, ordered by code.
    /// </summary>
    /// <response code="200">Catalogue rules with enabled count.</response>
    /// <response code="401">Unauthorized.</response>
    [HttpGet]
    [Authorize(Roles = Roles.All)]
    [ProducesResponseType(typeof(AlertRulesResponseDto), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    public async Task<ActionResult<AlertRulesResponseDto>> GetRulesAsync(
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return (ObjectResult)error;

        var rules = await _catalogueRepository
            .GetAllAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        // One lookup for the whole catalogue rather than one per row: this returns every rule for
        // the tenant, and rows commonly share the same editor. Distinct because of that sharing;
        // GetByIdsAsync short-circuits an empty set without a round trip, so a catalogue that is
        // still entirely seeded — every UpdatedBy null — costs nothing.
        var editorIds = rules
            .Where(r => r.UpdatedBy.HasValue)
            .Select(r => r.UpdatedBy!.Value)
            .Distinct()
            .ToList();

        var editors = await _userRepository
            .GetByIdsAsync(editorIds, cancellationToken)
            .ConfigureAwait(false);
        var editorNames = editors.ToDictionary(u => u.UserId, u => u.DisplayName);

        var dtos = rules.Select(r => Map(r, editorNames)).ToList();

        _logger.LogInformation(
            "Listed {Count} alert rules ({EnabledCount} enabled) for tenant {TenantId}",
            dtos.Count, dtos.Count(r => r.Enabled), tenantId);

        return Ok(new AlertRulesResponseDto
        {
            Rules        = dtos,
            TotalCount   = dtos.Count,
            EnabledCount = dtos.Count(r => r.Enabled),
        });
    }

    // ── PATCH /api/alert-rules/{ruleId} ────────────────────────────────────────

    /// <summary>
    /// Updates a rule's Enabled, Severity and/or ThresholdValue. Partial: an omitted field is left
    /// unchanged, which is distinct from an explicit null (<c>"thresholdValue": null</c> clears it).
    /// </summary>
    /// <response code="200">Updated rule.</response>
    /// <response code="400">Validation failed.</response>
    /// <response code="401">Unauthorized or user not found.</response>
    /// <response code="404">Rule not found for this tenant.</response>
    [HttpPatch("{ruleId:guid}")]
    [Authorize(Roles = Roles.Admin)]
    [ProducesResponseType(typeof(AlertRuleDto), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    [ProducesResponseType(typeof(ProblemDetails), 404)]
    public async Task<IActionResult> UpdateRuleAsync(
        Guid ruleId,
        [FromBody] UpdateAlertRuleRequest request,
        CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var (userId, displayName, userError) = await ResolveCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (userError is not null) return userError;

        if (!request.Enabled.IsSet && !request.Severity.IsSet && !request.ThresholdValue.IsSet)
            return Problem(statusCode: 400, title: "Bad Request",
                detail: "Se requiere al menos un campo para actualizar.");

        // Read before write: needed for the tenant check (404), for the fields the payload omits,
        // for the code the implemented-rule guard tests, and for the audit entry's old values.
        var existing = await _catalogueRepository
            .GetByIdAsync(ruleId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró la regla.");

        var enabled   = request.Enabled.Or(existing.Enabled);
        var severity  = request.Severity.Or(existing.Severity);
        var threshold = request.ThresholdValue.Or(existing.ThresholdValue);

        if (severity is null || !ValidSeverities.Contains(severity, StringComparer.Ordinal))
            return Problem(statusCode: 400, title: "Bad Request",
                detail: $"La severidad no es válida. Debe ser una de: {string.Join(", ", ValidSeverities)}.");

        // Every threshold rule below judges the SUBMITTED value, never the resolved one. Judging the
        // resolved value would reject a patch that says nothing about the threshold — {"enabled":
        // true} on a rule whose stored threshold is already invalid — stranding that rule as
        // uneditable behind an error about a field the caller never mentioned. Such a row is
        // reachable: until this endpoint existed the catalogue was edited by hand in SQL, and
        // nothing in the schema pins a threshold to the rule that reads it. Clearing to null stays
        // legal for every rule, which is why the pattern requires a non-null submitted value.
        if (request.ThresholdValue.IsSet && request.ThresholdValue.Value is { } submitted)
        {
            // A rule the engine never reads a threshold for would accept the value, show it on the
            // screen and ignore it on every analysis — the analyst would believe she had changed
            // something. Refused, rather than stored as decoration.
            if (!AlertRuleEngine.ThresholdUnits.TryGetValue(existing.Code, out var unit))
                return Problem(statusCode: 400, title: "Bad Request",
                    detail: $"La regla {existing.Code} no utiliza umbral: no puede asignársele un valor.");

            if (Math.Abs(submitted) >= MaxThresholdExclusive ||
                decimal.Round(submitted, ThresholdScale) != submitted)
                return Problem(statusCode: 400, title: "Bad Request",
                    detail: "El umbral debe tener como máximo 6 dígitos enteros y 4 decimales.");

            if (unit == RuleThresholdUnit.Proportion && (submitted <= 0m || submitted > 1m))
                return Problem(statusCode: 400, title: "Bad Request",
                    detail: "El umbral debe expresarse como proporción entre 0 y 1 " +
                            "(por ejemplo 0,55 para 55 %).");
        }

        // Enabling a code the engine does not implement is refused rather than stored: the engine
        // would log-and-skip it on every analysis, leaving a rule that reads as active and never
        // fires. A disabled unknown row stays editable — only turning it ON is the lie.
        if (enabled && !AlertRuleEngine.ImplementedCodes.Contains(existing.Code))
            return Problem(statusCode: 400, title: "Bad Request",
                detail: $"La regla {existing.Code} no está implementada por el motor y no puede habilitarse.");

        var updated = await _catalogueRepository
            .UpdateAsync(ruleId, tenantId, enabled, severity, threshold, userId, cancellationToken)
            .ConfigureAwait(false);
        // Null means the row vanished between the read and the write — treat it exactly like the
        // read miss rather than reporting a success that did not happen.
        if (updated is null)
            return Problem(statusCode: 404, title: "Not Found", detail: "No se encontró la regla.");

        // Before/after are recorded because a threshold change alters which alerts fire and
        // therefore what the SGR decides on. Code is on both sides so the entry is readable
        // without joining back to the catalogue.
        //
        // AWAITED, unlike the fire-and-forget audit calls elsewhere in this codebase: this is the
        // record that justifies the UpdatedBy column, so the write is observed rather than left on
        // a discarded task whose failure — and whose exception — nobody would ever see.
        // IAuditService is contractually non-throwing and logs its own failures, so awaiting cannot
        // turn a lost audit entry into a failed request. CancellationToken.None, not the request
        // token: the entry must be written even if the client has already disconnected.
        await _auditService.LogAsync(
            tenantId:     tenantId,
            userId:       userId,
            action:       "AlertRule.Updated",
            severity:     "Info",
            entityType:   "AlertRuleCatalogue",
            entityId:     ruleId.ToString(),
            ipAddress:    HttpContext.Connection.RemoteIpAddress?.ToString(),
            requestId:    HttpContext.TraceIdentifier,
            oldValueJson: JsonSerializer.Serialize(new
            {
                existing.Code, existing.Enabled, existing.Severity, existing.ThresholdValue,
            }),
            newValueJson: JsonSerializer.Serialize(new
            {
                updated.Code, updated.Enabled, updated.Severity, updated.ThresholdValue,
            }),
            ct: CancellationToken.None).ConfigureAwait(false);

        _logger.LogInformation(
            "Alert rule {Code} ({RuleId}) updated by {UserId} tenant {TenantId}",
            updated.Code, ruleId, userId, tenantId);

        // The editor is the caller by construction — UpdateAsync just wrote their id into
        // UpdatedBy — so the name is already in hand and the response costs no extra query.
        // Should the two ever fail to match, the lookup misses and the name comes back null
        // rather than reporting the wrong person.
        return Ok(Map(updated, new Dictionary<Guid, string> { [userId] = displayName }));
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    /// <param name="userNames">
    /// Display names by user id, resolved once by the caller. Passed in rather than looked up here
    /// so a listing resolves every editor in a single query instead of one per row; a missing key
    /// is the same signal as a null id and yields a null name.
    /// </param>
    private static AlertRuleDto Map(
        AlertRuleDefinition rule,
        IReadOnlyDictionary<Guid, string> userNames) => new()
    {
        RuleId                = rule.RuleId,
        Code                  = rule.Code,
        Enabled               = rule.Enabled,
        Severity              = rule.Severity,
        ThresholdValue        = rule.ThresholdValue,
        // Same source the PATCH guard consults, so what the screen renders and what the API accepts
        // cannot drift: absent from the map means the rule takes no threshold.
        ThresholdUnit         = AlertRuleEngine.ThresholdUnits.TryGetValue(rule.Code, out var unit)
                                    ? unit.ToWire()
                                    : null,
        DisplayNameEs         = rule.DisplayNameEs,
        DescriptionTemplateEs = rule.DescriptionTemplateEs,
        Implemented           = AlertRuleEngine.ImplementedCodes.Contains(rule.Code),
        CreatedAt             = rule.CreatedAt,
        UpdatedAt             = rule.UpdatedAt,
        UpdatedBy             = rule.UpdatedBy,
        UpdatedByName         = rule.UpdatedBy is { } editorId &&
                                userNames.TryGetValue(editorId, out var editorName)
                                    ? editorName
                                    : null,
    };

    private (string tenantId, IActionResult? error) ResolveTenant()
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in AlertRuleCatalogueController");
            return (string.Empty, Problem(
                statusCode: 500, title: "Internal Server Error",
                detail: "Tenant context not resolved."));
        }

        return (tenantContext.TenantId, null);
    }

    /// <summary>
    /// Resolves the calling user from the JWT. Returns the display name alongside the id because
    /// the PATCH response reports the editor by name, and the editor is this same user — carrying
    /// it out of the row already read here spares the response a second lookup.
    /// </summary>
    private async Task<(Guid userId, string displayName, IActionResult? error)> ResolveCurrentUserAsync(
        CancellationToken ct)
    {
        var firebaseUid = User.FindFirst("uid")?.Value
            ?? User.FindFirst("sub")?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrWhiteSpace(firebaseUid))
            return (Guid.Empty, string.Empty, Unauthorized());

        var user = await _userRepository
            .GetByFirebaseUidAsync(firebaseUid, ct)
            .ConfigureAwait(false);

        if (user is null)
            return (Guid.Empty, string.Empty, Unauthorized());

        return (user.UserId, user.DisplayName, null);
    }
}
