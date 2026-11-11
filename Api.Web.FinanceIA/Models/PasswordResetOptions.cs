// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Models;

/// <summary>
/// Settings for POST api/bff/auth/password-reset, bound from the "PasswordReset" configuration
/// section. <see cref="DefaultPortalUrl"/> and <see cref="AllowedPortalOrigins"/> are filled in
/// Program.cs from <c>AppSettings:FrontendUrl</c> and <c>Cors:AllowedOrigins</c> so the classes
/// below Program.cs never read <c>IConfiguration</c>. Every value has a working default.
/// </summary>
public sealed record PasswordResetOptions
{
    /// <summary>Minimum reCAPTCHA V3 score accepted. Default 0.5, the value every other anonymous endpoint uses.</summary>
    public double RecaptchaMinScore { get; init; } = 0.5;

    /// <summary>
    /// How many reset requests one address may make per tenant within
    /// <see cref="PerEmailWindowMinutes"/>. Default 3. Counted for every address, known or not, so
    /// the limit itself reveals nothing. Values below 1 read as 1.
    /// </summary>
    public int PerEmailLimit { get; init; } = 3;

    /// <summary>Length of the per-email window, in minutes. Default 15. Values below 1 read as 1.</summary>
    public int PerEmailWindowMinutes { get; init; } = 15;

    /// <summary>
    /// How many accepted requests may wait for the background worker. Default 100. Past it a request
    /// still answers 202 but is dropped with a Warning — the caller just asks again.
    /// </summary>
    public int QueueCapacity { get; init; } = 100;

    /// <summary>
    /// Link lifetime as shown in the email. Firebase password-reset oobCodes expire after one hour
    /// and the Admin SDK cannot change that, so this is a statement of fact, not a setting to tune.
    /// </summary>
    public string LinkExpiryText { get; init; } = "1 hora";

    /// <summary>Product name passed to the email template.</summary>
    public string ProductName { get; init; } = "Financia";

    /// <summary>The Portal route that runs steps 2–3 of the recovery flow.</summary>
    public string RecoverPath { get; init; } = "/recover";

    /// <summary>Portal base URL when the tenant has no <c>portal.url</c> setting. From <c>AppSettings:FrontendUrl</c>.</summary>
    public string DefaultPortalUrl { get; set; } = string.Empty;

    /// <summary>
    /// Origins a tenant <c>portal.url</c> must belong to. From <c>Cors:AllowedOrigins</c>: the
    /// emailed link carries a live oobCode, so it may only ever point at a Portal we serve.
    /// </summary>
    public IReadOnlyCollection<string> AllowedPortalOrigins { get; set; } = [];
}
