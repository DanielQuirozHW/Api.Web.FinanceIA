// Project : Api.Web.FinanceIA
// Created : 2026-10-21

namespace Api.Web.FinanceIA.Models;

/// <summary>
/// Settings for POST api/bff/auth/email-verification, bound from the "EmailVerification"
/// configuration section. <see cref="DefaultPortalUrl"/>, <see cref="AllowedPortalOrigins"/> and
/// <see cref="DefaultTemplateId"/> are filled in Program.cs from <c>AppSettings:FrontendUrl</c>,
/// <c>Cors:AllowedOrigins</c> and <c>SendGrid:EmailVerificationTemplateId</c> so the classes below
/// Program.cs never read <c>IConfiguration</c>. Every value but the template has a working default.
/// </summary>
public sealed record EmailVerificationOptions
{
    /// <summary>
    /// How many verification requests one user may make within <see cref="PerUserWindowMinutes"/>.
    /// Default 3. Counted for every request, verified or not. Values below 1 read as 1.
    /// </summary>
    public int PerUserLimit { get; init; } = 3;

    /// <summary>Length of the per-user window, in minutes. Default 15. Values below 1 read as 1.</summary>
    public int PerUserWindowMinutes { get; init; } = 15;

    /// <summary>
    /// How many accepted requests may wait for the background worker. Default 100. Past it a request
    /// still answers 202 but is dropped with a Warning — the user just asks again.
    /// </summary>
    public int QueueCapacity { get; init; } = 100;

    /// <summary>Product name passed to the email template.</summary>
    public string ProductName { get; init; } = "Financia";

    /// <summary>The Portal route that applies the verification code (<c>applyActionCode</c>).</summary>
    public string VerifyPath { get; init; } = "/verify-email";

    /// <summary>Portal base URL when the tenant has no <c>portal.url</c> setting. From <c>AppSettings:FrontendUrl</c>.</summary>
    public string DefaultPortalUrl { get; set; } = string.Empty;

    /// <summary>
    /// Origins a tenant <c>portal.url</c> must belong to. From <c>Cors:AllowedOrigins</c>: the
    /// emailed link carries a live oobCode, so it may only ever point at a Portal we serve.
    /// </summary>
    public IReadOnlyCollection<string> AllowedPortalOrigins { get; set; } = [];

    /// <summary>
    /// SendGrid dynamic template id when the tenant has no <c>email.email_verification.template_id</c>
    /// setting. From <c>SendGrid:EmailVerificationTemplateId</c>. Deliberately NO in-code fallback:
    /// unset means "template not deployed yet", and the request ends as TemplateMissing.
    /// </summary>
    public string? DefaultTemplateId { get; set; }
}
