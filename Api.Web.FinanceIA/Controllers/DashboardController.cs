// Project : Api.Web.FinanceIA
// Created : 2026-10-23

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Web.FinanceIA.Controllers;

/// <summary>
/// The role dashboards: read-only summaries the Portal cannot build from GET /api/analyses, which is
/// tenant-wide, filters by entity only and pages by GeneratedAt. my-work and approvals are scoped to the
/// caller; portfolio, oversight and operations to the tenant.
/// </summary>
/// <remarks>
/// <para>
/// READ-ONLY. Nothing here writes, audits or notifies. Every figure comes from the rules the detail
/// and the decision endpoint already use — see <see cref="DashboardService"/>.
/// </para>
/// <para>
/// Each section is computed independently: one that fails is null and named in
/// <c>degradedSections</c>, and the response is still a 200. The JSON shapes are in
/// <c>Models/Financia/DashboardContracts.cs</c>; field values are stored codes, never Spanish copy.
/// </para>
/// </remarks>
[ApiController]
[Route("api/dashboard")]
[Route("api/bff/dashboard")]
[Authorize(AuthenticationSchemes = "Internal")]
public sealed class DashboardController : ControllerBase
{
    private readonly ITenantContextAccessor        _tenantContextAccessor;
    private readonly IUserRepository               _userRepository;
    private readonly IDashboardService             _dashboardService;
    private readonly ILogger<DashboardController>  _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="DashboardController"/>.
    /// </summary>
    public DashboardController(
        ITenantContextAccessor       tenantContextAccessor,
        IUserRepository              userRepository,
        IDashboardService            dashboardService,
        ILogger<DashboardController> logger)
    {
        _tenantContextAccessor = tenantContextAccessor;
        _userRepository        = userRepository;
        _dashboardService      = dashboardService;
        _logger                = logger;
    }

    // ── GET /api/dashboard/my-work ────────────────────────────────────────────────

    /// <summary>
    /// The caller's own work: their analyses waiting for a decision, the ones returned for rework,
    /// how many of the pending ones degraded on BCRA and can be re-scored, their pre-approvals still
    /// waiting for signatures, and their latest analyses.
    /// </summary>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with <see cref="MyWorkResponse"/>.</returns>
    /// <remarks>
    /// <see cref="Roles.Deciders"/> — the roles that create and decide analyses. Auditor and SuperAdmin
    /// have no work of their own here. Everything is scoped to the CALLER, so no role on the database
    /// row widens or narrows what is read.
    /// </remarks>
    [HttpGet("my-work")]
    [Authorize(Roles = Roles.Deciders)]
    [ProducesResponseType(typeof(MyWorkResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    [ProducesResponseType(typeof(ProblemDetails), 403)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> GetMyWorkAsync(CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var currentUser = await ResolveCurrentUserAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (currentUser is null) return Unauthorized();

        var response = await _dashboardService
            .GetMyWorkAsync(tenantId, currentUser, DateTime.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        return Ok(response);
    }

    // ── GET /api/dashboard/approvals ──────────────────────────────────────────────

    /// <summary>
    /// What waits for the caller's signature — every pre-approval of the tenant they can still vote
    /// on — and what they decided in the last 30 days.
    /// </summary>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with <see cref="ApprovalsResponse"/>, or 403 when the caller cannot confirm.</returns>
    /// <remarks>
    /// Gated EXACTLY like PATCH /api/analyses/{id}/decision: <see cref="Roles.Deciders"/> on the token
    /// is the outer door, and whether the caller is a confirmer is read off their DATABASE row with
    /// <see cref="Roles.IsConfirmer"/>. A role changed in Settings does not reach an issued token, so a
    /// claim-only gate would list pre-approvals to someone the decision endpoint now refuses — or hide
    /// them from someone it now accepts.
    /// </remarks>
    [HttpGet("approvals")]
    [Authorize(Roles = Roles.Deciders)]
    [ProducesResponseType(typeof(ApprovalsResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    [ProducesResponseType(typeof(ProblemDetails), 403)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> GetApprovalsAsync(CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var currentUser = await ResolveCurrentUserAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (currentUser is null) return Unauthorized();

        if (!Roles.IsConfirmer(currentUser.Role))
        {
            _logger.LogInformation(
                "Dashboard approvals refused for user {UserId} tenant {TenantId}: not a confirmer on the user row.",
                currentUser.UserId, tenantId);

            var problem = Problem(statusCode: 403, title: "Forbidden",
                detail: "Sólo un RiskManager o un Admin puede ver las pre-aprobaciones que esperan su firma.");
            ((ProblemDetails)problem.Value!).Extensions["errorCode"] = "confirmer_role_required";
            return problem;
        }

        var response = await _dashboardService
            .GetApprovalsAsync(tenantId, currentUser, DateTime.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        return Ok(response);
    }

    // ── GET /api/dashboard/portfolio ──────────────────────────────────────────────

    /// <summary>
    /// The tenant's live portfolio: otorgado, utilizado and disponible of the Active cupos, by risk
    /// bucket, top exposures, sectors, maturities, BCRA signals and the cupos at 90% utilization or more;
    /// the cupos by current BCRA situación, the PyMEs due for their annual review and, for a tenant in
    /// creditline.mode "sgr", the Fondo de Riesgo usage. Every amount is per currency, except sgr's, which
    /// is ARS with USD converted at the tenant's own rate.
    /// </summary>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with <see cref="PortfolioResponse"/>.</returns>
    /// <remarks>
    /// <see cref="Roles.TenantConfigReaders"/> — Admin, RiskManager and Auditor: a tenant-wide read with no
    /// work of the caller's in it, so no user row is resolved and nothing is narrowed by it.
    /// </remarks>
    [HttpGet("portfolio")]
    [Authorize(Roles = Roles.TenantConfigReaders)]
    [ProducesResponseType(typeof(PortfolioResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    [ProducesResponseType(typeof(ProblemDetails), 403)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> GetPortfolioAsync(CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var response = await _dashboardService
            .GetPortfolioAsync(tenantId, DateTime.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        return Ok(response);
    }

    // ── GET /api/dashboard/oversight ──────────────────────────────────────────────

    /// <summary>
    /// The tenant's review work: the PendingReview queue of every analyst, approvals against the
    /// suggestion, recent decisions and re-scores, and the throughput of this and last Argentine month.
    /// </summary>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with <see cref="OversightResponse"/>.</returns>
    /// <remarks><see cref="Roles.TenantConfigReaders"/>, tenant-wide, as portfolio.</remarks>
    [HttpGet("oversight")]
    [Authorize(Roles = Roles.TenantConfigReaders)]
    [ProducesResponseType(typeof(OversightResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    [ProducesResponseType(typeof(ProblemDetails), 403)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> GetOversightAsync(CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var response = await _dashboardService
            .GetOversightAsync(tenantId, DateTime.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        return Ok(response);
    }

    // ── GET /api/dashboard/operations ─────────────────────────────────────────────

    /// <summary>
    /// Integration health from ApiCallLogs over the last 24 hours, and how old the source snapshots of
    /// the entities with an Active cupo are.
    /// </summary>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>200 OK with <see cref="OperationsResponse"/>.</returns>
    /// <remarks>
    /// <see cref="Roles.Admin"/> only. Plan usage and pending invitations are not repeated here: the Portal
    /// reads GET /api/billing/usage and GET /api/bff/settings/invitations?status=pending.
    /// </remarks>
    [HttpGet("operations")]
    [Authorize(Roles = Roles.Admin)]
    [ProducesResponseType(typeof(OperationsResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    [ProducesResponseType(typeof(ProblemDetails), 403)]
    [ProducesResponseType(typeof(ProblemDetails), 500)]
    public async Task<IActionResult> GetOperationsAsync(CancellationToken cancellationToken)
    {
        var (tenantId, error) = ResolveTenant();
        if (error is not null) return error;

        var response = await _dashboardService
            .GetOperationsAsync(tenantId, DateTime.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        return Ok(response);
    }

    // ── Private helpers ───────────────────────────────────────────────────────────

    private (string tenantId, IActionResult? error) ResolveTenant()
    {
        var tenantContext = _tenantContextAccessor.TenantContext;
        if (tenantContext is null)
        {
            _logger.LogWarning("TenantContext not available in DashboardController");
            return (string.Empty, Problem(
                statusCode: 500, title: "Internal Server Error",
                detail: "Tenant context not resolved."));
        }

        return (tenantContext.TenantId, null);
    }

    /// <summary>
    /// Resolves the calling user from the JWT, following AnalysisController's helper — plus a tenant
    /// check, because GetByFirebaseUidAsync carries no tenant predicate and every section below is
    /// keyed by this user's id. A row of another tenant is treated as no user at all.
    /// </summary>
    private async Task<User?> ResolveCurrentUserAsync(string tenantId, CancellationToken ct)
    {
        var firebaseUid = User.FindFirst("uid")?.Value
            ?? User.FindFirst("sub")?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrWhiteSpace(firebaseUid))
            return null;

        var user = await _userRepository.GetByFirebaseUidAsync(firebaseUid, ct).ConfigureAwait(false);
        if (user is null)
            return null;

        if (!string.Equals(user.TenantId, tenantId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Dashboard caller {UserId} does not belong to tenant {TenantId}; refused.",
                user.UserId, tenantId);
            return null;
        }

        return user;
    }
}
