// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-03

using System.Net;
using System.Text;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Servicios;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Covers <see cref="IdentityPlatformSignInService"/> — the BFF's own first-factor check.
/// Hand-rolled <see cref="HttpMessageHandler"/> stub, no mock library.
///
/// <para>
/// The whole point of this service is the distinction between "Identity Platform said no" and
/// "we could not ask it", so that is what these tests pin. Collapsing the two would report an
/// outage as a credential rejection — telling a user with the correct password that it is wrong,
/// on the one endpoint they reach precisely because they have already lost their authenticator.
/// </para>
///
/// <para>
/// The success case is the counter-intuitive one and is worth naming: a response carrying
/// <c>mfaPendingCredential</c> and NO id token is what "the password is right" looks like here.
/// Firebase withholds the ID token until a second factor passes, so the pending credential is
/// the only first-factor proof that exists at this point in a sign-in.
/// </para>
/// </summary>
public sealed class IdentityPlatformSignInServiceTests
{
    private const string Tenant = "tenant-1";

    // ── Stubs ───────────────────────────────────────────────────────────────────

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _body;
        private readonly Exception? _throw;

        public string? RequestBodySeen { get; private set; }
        public Uri?    RequestUriSeen  { get; private set; }

        public StubHandler(HttpStatusCode statusCode, string body)
        {
            _statusCode = statusCode;
            _body       = body;
        }

        public StubHandler(Exception toThrow)
        {
            _statusCode = HttpStatusCode.OK;
            _body       = "{}";
            _throw      = toThrow;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUriSeen = request.RequestUri;

            if (request.Content is not null)
            {
                RequestBodySeen = await request.Content
                    .ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            if (_throw is not null)
            {
                throw _throw;
            }

            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }

    // ── Harness ─────────────────────────────────────────────────────────────────

    private static TenantContext NewTenantContext(string? webApiKey = "web-api-key") =>
        new()
        {
            TenantId               = Tenant,
            FirebaseProjectId      = "proj",
            FirebaseCredentialJson = "{}",
            RecaptchaSecretKey     = "secret",
            FirebaseWebApiKey      = webApiKey,
        };

    private static IdentityPlatformSignInService NewService(StubHandler handler) =>
        new(
            new HttpClient(handler)
            {
                BaseAddress = new Uri(IdentityPlatformSignInService.IdentityToolkitBaseAddress),
            },
            NullLogger<IdentityPlatformSignInService>.Instance);

    private static Task<FirstFactorOutcome> RunAsync(
        StubHandler handler, string? webApiKey = "web-api-key")
        => NewService(handler).VerifyPasswordAsync(
            "analyst@example.com", "correct-horse", NewTenantContext(webApiKey), CancellationToken.None);

    // ── Outcomes ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnMfaPendingCredential_MeansTheFirstFactorPassed()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            """{"mfaPendingCredential":"opaque-value","mfaInfo":[{"mfaEnrollmentId":"e1"}]}""");

        Assert.Equal(FirstFactorOutcome.MfaRequired, await RunAsync(handler));
    }

    [Fact]
    public async Task AnIdToken_MeansThePasswordIsRightButNoSecondFactorIsEnrolled()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            """{"idToken":"eyJ...","refreshToken":"r","localId":"uid-1"}""");

        Assert.Equal(FirstFactorOutcome.NoMfaEnrolled, await RunAsync(handler));
    }

    [Fact]
    public async Task BothPresent_IsReadAsMfaRequired()
    {
        // Defensive: if a response ever carried both, the MFA-enrolled reading is the stronger
        // statement and the one that must win, or a token could be issued for an account whose
        // second factor was never satisfied.
        var handler = new StubHandler(
            HttpStatusCode.OK,
            """{"idToken":"eyJ...","mfaPendingCredential":"opaque-value"}""");

        Assert.Equal(FirstFactorOutcome.MfaRequired, await RunAsync(handler));
    }

    [Theory]
    [InlineData("INVALID_LOGIN_CREDENTIALS")]
    [InlineData("INVALID_PASSWORD")]
    [InlineData("EMAIL_NOT_FOUND")]
    [InlineData("USER_DISABLED")]
    [InlineData("INVALID_EMAIL")]
    [InlineData("INVALID_PASSWORD : some trailing detail")]
    public async Task CredentialRejectionCodes_MapToPasswordRejected(string message)
    {
        // Built by concatenation rather than an interpolated raw string: the JSON ends in "}}",
        // which is the interpolation-closing delimiter in a $$"""...""" literal.
        var body = "{\"error\":{\"code\":400,\"message\":\"" + message + "\"}}";

        var handler = new StubHandler(HttpStatusCode.BadRequest, body);

        Assert.Equal(FirstFactorOutcome.PasswordRejected, await RunAsync(handler));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest,          """{"error":{"message":"API_KEY_INVALID"}}""")]
    [InlineData(HttpStatusCode.TooManyRequests,     """{"error":{"message":"TOO_MANY_ATTEMPTS_TRY_LATER"}}""")]
    [InlineData(HttpStatusCode.InternalServerError, """{"error":{"message":"INTERNAL_ERROR"}}""")]
    [InlineData(HttpStatusCode.BadRequest,          """{"error":{"message":"MISSING_PASSWORD"}}""")]
    public async Task NonCredentialFailures_MapToLookupFailed(HttpStatusCode status, string body)
    {
        // TOO_MANY_ATTEMPTS_TRY_LATER is the one that matters most: Google throttled US, which
        // says nothing about the password. Reporting it as a rejection would send a user with
        // the right credentials off to reset a password that was never wrong.
        Assert.Equal(FirstFactorOutcome.LookupFailed, await RunAsync(new StubHandler(status, body)));
    }

    [Fact]
    public async Task A200WithNeitherToken_IsALookupFailureNotAVerdict()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");

        Assert.Equal(FirstFactorOutcome.LookupFailed, await RunAsync(handler));
    }

    [Fact]
    public async Task ATransportFailure_IsALookupFailure()
    {
        var handler = new StubHandler(new HttpRequestException("connection reset"));

        Assert.Equal(FirstFactorOutcome.LookupFailed, await RunAsync(handler));
    }

    [Fact]
    public async Task UnparseableJson_IsALookupFailure()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "not json at all");

        Assert.Equal(FirstFactorOutcome.LookupFailed, await RunAsync(handler));
    }

    [Fact]
    public async Task AMissingWebApiKey_FailsClosedWithoutCallingOut()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK, """{"mfaPendingCredential":"opaque-value"}""");

        var outcome = await RunAsync(handler, webApiKey: null);

        Assert.Equal(FirstFactorOutcome.LookupFailed, outcome);
        Assert.Null(handler.RequestUriSeen);
    }

    // ── Wire format ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheRequest_SendsCamelCaseFieldsAndTheKeyInTheQueryString()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK, """{"mfaPendingCredential":"opaque-value"}""");

        await RunAsync(handler);

        // Identity Toolkit takes the Web API key as ?key=, and the body fields are camelCase.
        Assert.Contains("/v1/accounts:signInWithPassword", handler.RequestUriSeen!.ToString());
        Assert.Contains("key=web-api-key", handler.RequestUriSeen!.ToString());

        Assert.NotNull(handler.RequestBodySeen);
        Assert.Contains("\"email\":", handler.RequestBodySeen!);
        Assert.Contains("\"password\":", handler.RequestBodySeen!);
        Assert.Contains("\"returnSecureToken\":true", handler.RequestBodySeen!);
    }

    [Fact]
    public async Task CallerCancellation_PropagatesRatherThanBecomingALookupFailure()
    {
        // A cancelled request is not evidence about anything; swallowing it as LookupFailed would
        // turn a client disconnect into a logged Identity Platform outage.
        var handler = new StubHandler(new TaskCanceledException("cancelled"));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // ThrowsAny, not Throws: HttpClient surfaces cancellation as TaskCanceledException in
        // some paths and a bare OperationCanceledException in others, and the property under
        // test is "it propagates", not which subclass carries it.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NewService(handler).VerifyPasswordAsync(
                "analyst@example.com", "correct-horse", NewTenantContext(), cts.Token));
    }
}
