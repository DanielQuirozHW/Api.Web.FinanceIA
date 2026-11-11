// Project : Api.Web.FinanceIA
// Created : 2026-10-31

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Models.Settings;

namespace Api.Web.FinanceIA.Controllers;

/// <summary>
/// The persona humana expected-documents matrix of the tenant: per fiscal regime, which legajo documents are
/// required, optional or not asked for, their alternatives and their recency cuts. Under the same
/// <c>api/bff/settings</c> prefix as the other tenant settings (e.g. <c>/sgr</c>).
/// </summary>
/// <remarks>
/// The matrix drives the legajo checklist (GET /api/person-legajos/{id}) and the coverage index of every NEW
/// evaluation and re-evaluation (engine 0.10), which freeze it; it never blocks an upload, a classification,
/// an analysis or a suggestion, and past analyses keep the coverage they froze.
/// </remarks>
[Authorize(AuthenticationSchemes = "Internal")]
[ApiController]
[Route("api/bff/settings/person-expected-documents")]
public sealed class PersonExpectedDocumentsSettingsController : ControllerBase
{
    private readonly IPersonExpectedDocumentsService _expectedDocuments;
    private readonly ITenantContextAccessor          _tenantContextAccessor;
    private readonly ILogger<PersonExpectedDocumentsSettingsController> _logger;

    /// <summary>Initializes a new instance of <see cref="PersonExpectedDocumentsSettingsController"/>.</summary>
    public PersonExpectedDocumentsSettingsController(
        IPersonExpectedDocumentsService expectedDocuments,
        ITenantContextAccessor          tenantContextAccessor,
        ILogger<PersonExpectedDocumentsSettingsController> logger)
    {
        _expectedDocuments     = expectedDocuments;
        _tenantContextAccessor = tenantContextAccessor;
        _logger                = logger;
    }

    /// <summary>
    /// The matrix in force — each regime with its source (<c>tenant</c> | <c>default</c>) and each line with its
    /// effective recency cut and age class — plus the code defaults in the same shape, the configurable
    /// document types and when the tenant last saved it.
    /// Requires Admin, RiskManager, Auditor or SuperAdmin.
    /// </summary>
    [HttpGet]
    [Authorize(Roles = Roles.TenantConfigReaders)]
    [ProducesResponseType(typeof(PersonExpectedDocumentsSettingsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAsync(CancellationToken cancellationToken)
    {
        var tenantId = ExtractTenantId();

        var view = await _expectedDocuments.GetViewAsync(tenantId, cancellationToken).ConfigureAwait(false);

        return Ok(view);
    }

    /// <summary>
    /// Stores the matrix — <c>{ "regimes": { … } }</c>, a regime left out keeping its code default — or, with
    /// <c>{ "reset": true }</c>, deletes it. Returns the same body as the GET.
    /// Requires Admin.
    /// </summary>
    /// <remarks>
    /// 400 <c>validation_error</c> with <c>errors[{field, message}]</c>, field paths such as
    /// <c>regimes.Monotributo[2].documentType</c>. Audited as <c>Settings.UpdatePersonExpectedDocuments</c> with
    /// the matrix before and after.
    /// </remarks>
    [HttpPut]
    [Authorize(Roles = Roles.Admin)]
    [ProducesResponseType(typeof(PersonExpectedDocumentsSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateAsync(
        [FromBody] UpdatePersonExpectedDocumentsRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
            return ValidationErrors([new ValidationError("regimes", "Se requiere el cuerpo de la solicitud.")]);

        var isReset = request.Reset == true;
        if (isReset && request.Regimes is not null)
            return ValidationErrors([new ValidationError("reset", "Enviá reset o regimes, no ambos.")]);

        if (!isReset)
        {
            var errors = PersonExpectedDocumentsMatrix.Validate(request.Regimes);
            if (errors.Count > 0)
                return Rejected(errors);
        }

        var userId    = ExtractUserId();
        var tenantId  = ExtractTenantId();
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var requestId = HttpContext.TraceIdentifier;

        try
        {
            var view = isReset
                ? await _expectedDocuments.ResetAsync(tenantId, userId, ipAddress, requestId, cancellationToken).ConfigureAwait(false)
                : await _expectedDocuments.UpdateAsync(tenantId, request.Regimes, userId, ipAddress, requestId, cancellationToken).ConfigureAwait(false);

            return Ok(view);
        }
        catch (ValidationException ex)
        {
            return Rejected(ex.Errors);
        }

        IActionResult Rejected(IReadOnlyCollection<ValidationError> errors)
        {
            // Counts only: the fields carry no PII, but the request is operator input.
            _logger.LogWarning(
                "Person expected documents update rejected for TenantId: {TenantId}: {ErrorCount} validation errors",
                ExtractTenantId(), errors.Count);
            return ValidationErrors(errors);
        }
    }

    /// <summary>The 400 shape DocumentsController uses: errorCode validation_error and errors[{field, message}].</summary>
    private ObjectResult ValidationErrors(IReadOnlyCollection<ValidationError> errors)
    {
        var pd = new ProblemDetails
        {
            Status   = 400,
            Title    = "Validation Error",
            Detail   = "Se produjeron uno o más errores de validación.",
            Type     = "https://serviciosfinancieros.api/errors/validation_error",
            Instance = HttpContext.Request.Path
        };
        pd.Extensions["errorCode"] = "validation_error";
        pd.Extensions["errors"]    = errors.Select(e => new { field = e.Field, message = e.Message }).ToList();
        return StatusCode(400, pd);
    }

    private Guid ExtractUserId()
    {
        var claim = User.FindFirst("userId")?.Value
                    ?? User.FindFirst("sub")?.Value;

        if (string.IsNullOrEmpty(claim) || !Guid.TryParse(claim, out var userId))
        {
            _logger.LogError(
                "UserId claim not found or invalid in JWT. Available claim types: {ClaimTypes}",
                string.Join(", ", User.Claims.Select(c => c.Type)));   // types only: values can be PII
            throw new UnauthorizedAccessException("UserId not found in JWT claims");
        }

        return userId;
    }

    private string ExtractTenantId()
    {
        return _tenantContextAccessor.TenantContext?.TenantId
               ?? User.FindFirst("tenantId")?.Value
               ?? throw new UnauthorizedAccessException("Tenant ID not found in claims");
    }
}
