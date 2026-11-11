// Project : Api.Web.FinanceIA
// Created : 2026-09-03

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Api.Web.FinanceIA.Models;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Calls Identity Platform's <c>accounts:signInWithPassword</c> to re-prove a user's first
/// factor. See <see cref="IIdentityPlatformSignInService"/> for why this is necessary.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a typed HttpClient. The BaseAddress is the Identity Toolkit host; the tenant's
/// Web API key is appended per call, since it varies by tenant (one Firebase project per tenant).
/// </para>
/// <para>
/// <b>No retry, deliberately.</b> Every other outbound integration here may retry, but this one
/// carries a password attempt: a retry doubles the failed-login count Identity Platform tracks
/// for the account and brings its own throttling forward, punishing the legitimate user for our
/// transient network error. A single attempt that reports <see cref="FirstFactorOutcome.LookupFailed"/>
/// is the safer failure.
/// </para>
/// <para>
/// <b>No ApiCallLogs row, deliberately.</b> Most outbound integrations here take
/// <c>IApiCallLogService</c>, but that table lives in the per-tenant BUSINESS database and its
/// <c>Service</c> column is constrained by a CHECK, so adding a discriminator for this call would
/// mean a DDL against every tenant database — for an auth-plane call whose entire feature
/// otherwise lives in the shared Default DB and never touches <c>FinanciaDbContext</c>. The
/// structured logging below reaches Application Insights and covers the observability need
/// without widening the deploy that far. (Two of the six existing vendors, BCRA and CASFOG,
/// write no rows either, so this is not a break with an otherwise uniform practice.)
/// </para>
/// </remarks>
public sealed class IdentityPlatformSignInService : IIdentityPlatformSignInService
{
    /// <summary>
    /// Identity Toolkit host. The v1 endpoint is the one that returns
    /// <c>mfaPendingCredential</c> for an MFA-enrolled account.
    /// </summary>
    public const string IdentityToolkitBaseAddress = "https://identitytoolkit.googleapis.com";

    private const string SignInPath = "/v1/accounts:signInWithPassword";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<IdentityPlatformSignInService> _logger;

    public IdentityPlatformSignInService(
        HttpClient httpClient,
        ILogger<IdentityPlatformSignInService> logger)
    {
        _httpClient = httpClient;
        _logger     = logger;
    }

    /// <inheritdoc />
    public async Task<FirstFactorOutcome> VerifyPasswordAsync(
        string email,
        string password,
        TenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        ArgumentNullException.ThrowIfNull(tenantContext);

        if (string.IsNullOrWhiteSpace(tenantContext.FirebaseWebApiKey))
        {
            _logger.LogError(
                "Identity Platform sign-in attempted for tenant {TenantId} with no WebApiKey configured. " +
                "Run the default-db DDL and populate TenantFirebaseConfig.WebApiKey for this tenant.",
                tenantContext.TenantId);

            return FirstFactorOutcome.LookupFailed;
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Query string, not a header: this is how Identity Toolkit takes the Web API key.
            var requestUri = $"{SignInPath}?key={Uri.EscapeDataString(tenantContext.FirebaseWebApiKey)}";

            using var response = await _httpClient
                .PostAsJsonAsync(
                    requestUri,
                    new SignInRequest(email, password, ReturnSecureToken: true),
                    SerializerOptions,
                    cancellationToken)
                .ConfigureAwait(false);

            var payload = await response.Content
                .ReadFromJsonAsync<SignInResponse>(SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            stopwatch.Stop();

            var outcome = Interpret(response.StatusCode, payload);

            // Outcome and timing only. The password, the API key and the pending credential
            // itself are never logged — the credential is first-factor proof and would be a
            // replayable secret in a log file.
            _logger.LogInformation(
                "Identity Platform first-factor check for tenant {TenantId} completed. " +
                "Outcome: {Outcome} | Status: {Status} | ElapsedMs: {ElapsedMs}",
                tenantContext.TenantId,
                outcome,
                (int)response.StatusCode,
                stopwatch.ElapsedMilliseconds);

            return outcome;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            stopwatch.Stop();

            // TaskCanceledException also covers the HttpClient timeout. A caller-driven
            // cancellation is rethrown rather than reported as a lookup failure.
            if (ex is TaskCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            _logger.LogError(ex,
                "Identity Platform first-factor check failed for tenant {TenantId}. " +
                "ElapsedMs: {ElapsedMs}",
                tenantContext.TenantId,
                stopwatch.ElapsedMilliseconds);

            return FirstFactorOutcome.LookupFailed;
        }
    }

    /// <summary>
    /// Maps an Identity Platform response onto a <see cref="FirstFactorOutcome"/>.
    /// </summary>
    /// <remarks>
    /// The MFA-required case is what this whole service exists to detect: Identity Platform
    /// answers 200 with an <c>mfaPendingCredential</c> and no ID token. Order matters — check
    /// the pending credential before the ID token, so a response somehow carrying both is read
    /// as the stronger (MFA-enrolled) statement.
    /// </remarks>
    private static FirstFactorOutcome Interpret(HttpStatusCode statusCode, SignInResponse? payload)
    {
        if (payload is null)
        {
            return FirstFactorOutcome.LookupFailed;
        }

        if (statusCode == HttpStatusCode.OK)
        {
            if (!string.IsNullOrWhiteSpace(payload.MfaPendingCredential))
            {
                return FirstFactorOutcome.MfaRequired;
            }

            if (!string.IsNullOrWhiteSpace(payload.IdToken))
            {
                return FirstFactorOutcome.NoMfaEnrolled;
            }

            // 200 with neither: an API contract change, not a credential verdict.
            return FirstFactorOutcome.LookupFailed;
        }

        var message = payload.Error?.Message ?? string.Empty;

        // Identity Platform's own credential-rejection codes. INVALID_LOGIN_CREDENTIALS is the
        // modern, deliberately ambiguous one — it covers both a wrong password and an unknown
        // email so the API does not leak which. The older, more specific codes are still
        // returned by some projects, so both generations are matched. StartsWith, because these
        // messages can carry a trailing detail such as " : INVALID_PASSWORD".
        var isCredentialRejection =
            message.StartsWith("INVALID_LOGIN_CREDENTIALS", StringComparison.Ordinal) ||
            message.StartsWith("INVALID_PASSWORD",          StringComparison.Ordinal) ||
            message.StartsWith("EMAIL_NOT_FOUND",           StringComparison.Ordinal) ||
            message.StartsWith("USER_DISABLED",             StringComparison.Ordinal) ||
            message.StartsWith("INVALID_EMAIL",             StringComparison.Ordinal);

        // Anything else on a non-200 — MISSING_PASSWORD, API key problems, quota, 5xx — is our
        // failure, not a statement about the caller's credentials. TOO_MANY_ATTEMPTS_TRY_LATER
        // lands here on purpose: it means Identity Platform throttled us, not that the password
        // was wrong, and reporting it as a rejection would mislead the user.
        return isCredentialRejection
            ? FirstFactorOutcome.PasswordRejected
            : FirstFactorOutcome.LookupFailed;
    }

    // ── Wire contracts ───────────────────────────────────────────────────────────

    private sealed record SignInRequest(
        string Email,
        string Password,
        bool ReturnSecureToken);

    private sealed record SignInResponse
    {
        /// <summary>Present when the account has NO second factor enrolled.</summary>
        public string? IdToken { get; init; }

        /// <summary>
        /// Present when the account HAS a second factor enrolled. Google documents it only as
        /// "an opaque string that functions as proof that the user has successfully passed the
        /// first factor check" — we test for its presence and never inspect or store it.
        /// </summary>
        public string? MfaPendingCredential { get; init; }

        [JsonPropertyName("error")]
        public SignInError? Error { get; init; }
    }

    private sealed record SignInError
    {
        public string? Message { get; init; }
    }
}
