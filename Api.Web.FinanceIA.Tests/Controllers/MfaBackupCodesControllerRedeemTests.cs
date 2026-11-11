// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-03

using System.Reflection;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Covers POST /api/mfa/backup-codes/redeem — the anonymous 2FA recovery login.
/// Hand-rolled private sealed stubs, no mock library.
///
/// <para>
/// This endpoint is unusual enough to be worth stating plainly: it is the only one in the API
/// that authenticates from a request body, because a user stuck at
/// <c>auth/multi-factor-auth-required</c> holds no token Firebase will vouch for. That makes the
/// ORDER of its checks the security property, not an implementation detail — so most of these
/// tests assert what did NOT happen. The stubs throw <see cref="NotSupportedException"/> from
/// any method that must not be reached, which turns a reordering regression into a failing test
/// rather than a silently weaker endpoint.
/// </para>
///
/// <para>
/// The two properties being pinned: a backup code is never touched until the password has been
/// proven (otherwise anyone knowing an email could burn or probe a stranger's codes), and a
/// custom token — redeemable for a full session — is never minted until every check has passed.
/// Alongside those, every rejection must be indistinguishable, or an anonymous endpoint becomes
/// an account-enumeration and password oracle.
/// </para>
/// </summary>
public sealed class MfaBackupCodesControllerRedeemTests
{
    private const string Tenant = "tenant-1";
    private const string Email  = "analyst@example.com";
    private const string Uid    = "firebase-uid-1";

    /// <summary>The [dbo].[Users] row the uid resolves to, in the BUSINESS database.</summary>
    private static readonly Guid UserId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    // ── Stubs ───────────────────────────────────────────────────────────────────

    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private sealed class StubRecaptchaVerifier : IRecaptchaVerifier
    {
        public bool   Success { get; set; } = true;
        public double Score   { get; set; } = 0.9;
        public int    CallCount { get; private set; }

        public Task<RecaptchaResponse> VerifyAsync(
            string token, TenantContext tenantContext, string? remoteIp, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(new RecaptchaResponse { Success = Success, Score = Score });
        }
    }

    private sealed class StubSignInService : IIdentityPlatformSignInService
    {
        public FirstFactorOutcome Outcome { get; set; } = FirstFactorOutcome.MfaRequired;
        public int     CallCount     { get; private set; }
        public string? PasswordSeen  { get; private set; }

        public Task<FirstFactorOutcome> VerifyPasswordAsync(
            string email, string password, TenantContext tenantContext, CancellationToken ct)
        {
            CallCount++;
            PasswordSeen = password;
            return Task.FromResult(Outcome);
        }
    }

    private sealed class StubCustomTokenIssuer : IFirebaseCustomTokenIssuer
    {
        public string? UidToReturn   { get; set; } = Uid;
        public bool    AllowMinting  { get; set; }
        public int     MintCallCount { get; private set; }
        public string? MintedForUid  { get; private set; }

        public Task<string?> GetUidByEmailAsync(
            string email, TenantContext tenantContext, CancellationToken ct)
            => Task.FromResult(UidToReturn);

        public Task<string> CreateCustomTokenAsync(
            string firebaseUid, TenantContext tenantContext, CancellationToken ct)
        {
            if (!AllowMinting)
            {
                throw new NotSupportedException(
                    "A custom token must not be minted unless every check has passed.");
            }

            MintCallCount++;
            MintedForUid = firebaseUid;
            return Task.FromResult("custom-token-value");
        }
    }

    private sealed class StubBackupCodeService : IMfaBackupCodeService
    {
        public bool AllowValidate { get; set; }
        public bool IsCodeValid   { get; set; }
        public int  Remaining     { get; set; } = 9;
        public int  ValidateCallCount { get; private set; }
        public string? TenantSeen { get; private set; }
        public Guid?   UserSeen   { get; private set; }

        public Task<IReadOnlyList<string>> GenerateAsync(
            string tenantId, Guid userId, int count, CancellationToken ct)
            => throw new NotSupportedException("Redemption must never issue new codes.");

        public Task<bool> ValidateAsync(
            string tenantId, Guid userId, string code, string? ipAddress, CancellationToken ct)
        {
            if (!AllowValidate)
            {
                throw new NotSupportedException(
                    "A backup code must not be touched before the first factor is proven.");
            }

            ValidateCallCount++;
            TenantSeen = tenantId;
            UserSeen   = userId;
            return Task.FromResult(IsCodeValid);
        }

        public Task<int> RemainingCountAsync(string tenantId, Guid userId, CancellationToken ct)
            => Task.FromResult(Remaining);
    }

    /// <summary>
    /// The business-database half of the redemption: the uid Identity Platform vouched for is
    /// resolved to a [dbo].[Users] row, and the codes are keyed by that row.
    /// </summary>
    private sealed class StubUserRepository : IUserRepository
    {
        /// <summary>The user returned for any uid, or null to simulate "no FinancIA account".</summary>
        public User? Result { get; set; }

        public int  CallCount  { get; private set; }
        public string? UidSeen { get; private set; }

        public Task<User?> GetByFirebaseUidAsync(string firebaseUid, CancellationToken ct)
        {
            CallCount++;
            UidSeen = firebaseUid;
            return Task.FromResult(Result);
        }

        // Nothing else in this flow touches the repository. Throwing rather than returning a
        // default keeps an accidental new call visible.
        public Task<User?> GetByUserIdAsync(Guid userId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<User?> GetByEmailAsync(string email, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<bool> ExistsByFirebaseUidAsync(string firebaseUid, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<User>> GetByIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<User>> GetActiveByTenantAsync(string tenantId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<User> CreateAsync(User user, CancellationToken ct)
            => throw new NotSupportedException();

        public Task UpdateLastLoginAsync(Guid userId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task UpdateOnboardingFlagsAsync(Guid userId, string? flagsJson, CancellationToken ct)
            => throw new NotSupportedException();
        // Team assignment (DDL 063) — unused by this suite.
        public Task UpdateTeamAsync(Guid userId, Guid? teamId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> ClearTeamAsync(Guid teamId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
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

    /// <summary>
    /// The FinancIA user the uid resolves to. Its TenantId is what the codes are keyed by —
    /// deliberately NOT the X-Tenant-ID of the request.
    /// </summary>
    private static User NewUser(string tenantId = Tenant) =>
        new()
        {
            UserId      = UserId,
            TenantId    = tenantId,
            FirebaseUid = Uid,
            Email       = Email,
            DisplayName = "Analyst",
            Role        = "Analyst",
            IsActive    = true,
        };

    private static MfaBackupCodesController NewController(
        StubBackupCodeService   backupCodes,
        StubSignInService       signIn,
        StubCustomTokenIssuer   issuer,
        StubRecaptchaVerifier   recaptcha,
        TenantContext?          tenantContext,
        StubUserRepository?     users = null)
    {
        var controller = new MfaBackupCodesController(
            backupCodes,
            users ?? new StubUserRepository { Result = NewUser() },
            new StubTenantContextAccessor { TenantContext = tenantContext },
            recaptcha,
            signIn,
            issuer,
            NullLogger<MfaBackupCodesController>.Instance);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };

        return controller;
    }

    private static RedeemBackupCodeRequest ValidRequest() =>
        new(Email, "correct-horse", "ABCD-2345", "recaptcha-token");

    private static int StatusOf(IActionResult result) => result switch
    {
        ObjectResult objectResult => objectResult.StatusCode ?? 0,
        StatusCodeResult statusCodeResult => statusCodeResult.StatusCode,
        _ => 0,
    };

    // ── Happy path ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Redeem_WithEverythingValid_MintsACustomTokenForTheResolvedUid()
    {
        var backupCodes = new StubBackupCodeService
        {
            AllowValidate = true,
            IsCodeValid   = true,
            Remaining     = 7,
        };
        var issuer = new StubCustomTokenIssuer { AllowMinting = true };

        var controller = NewController(
            backupCodes, new StubSignInService(), issuer,
            new StubRecaptchaVerifier(), NewTenantContext());

        var result = await controller.RedeemAsync(ValidRequest(), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<RedeemBackupCodeResponse>(ok.Value);

        Assert.Equal("custom-token-value", body.CustomToken);
        Assert.Equal(7, body.Remaining);

        // The uid must come from the verified email, never from the request body...
        Assert.Equal(Uid, issuer.MintedForUid);
        Assert.Equal(1, issuer.MintCallCount);

        // ...and the code must be keyed by the FinancIA user that uid resolves to, under that
        // user's own tenant.
        Assert.Equal(UserId, backupCodes.UserSeen);
        Assert.Equal(Tenant, backupCodes.TenantSeen);
    }

    [Fact]
    public async Task Redeem_SetsNoStore_SoTheTokenIsNeverCached()
    {
        var controller = NewController(
            new StubBackupCodeService { AllowValidate = true, IsCodeValid = true },
            new StubSignInService(),
            new StubCustomTokenIssuer { AllowMinting = true },
            new StubRecaptchaVerifier(),
            NewTenantContext());

        await controller.RedeemAsync(ValidRequest(), CancellationToken.None);

        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
        Assert.Equal("no-cache", controller.Response.Headers.Pragma.ToString());
    }

    // ── Ordering: the security property ─────────────────────────────────────────

    [Fact]
    public async Task Redeem_WithWrongPassword_NeverTouchesTheBackupCode()
    {
        // AllowValidate stays false: the stub throws if the code is reached at all. Without the
        // password gate first, anyone knowing an email could burn a stranger's codes.
        var backupCodes = new StubBackupCodeService();
        var signIn      = new StubSignInService { Outcome = FirstFactorOutcome.PasswordRejected };

        var controller = NewController(
            backupCodes, signIn, new StubCustomTokenIssuer(),
            new StubRecaptchaVerifier(), NewTenantContext());

        var result = await controller.RedeemAsync(ValidRequest(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status401Unauthorized, StatusOf(result));
        Assert.Equal(0, backupCodes.ValidateCallCount);
    }

    [Fact]
    public async Task Redeem_WithNoFinanciaUserForTheUid_NeverTouchesTheBackupCode()
    {
        // The uid passed the first factor but has no [dbo].[Users] row in the business database.
        // The codes hang off that row (FK_MfaBackupCodes_Users), so there is nothing to look at —
        // and the rejection must be the same generic 401 as every other one, or the endpoint
        // starts telling a caller which Firebase accounts have FinancIA accounts.
        var backupCodes = new StubBackupCodeService();   // AllowValidate false: throws if reached
        var issuer      = new StubCustomTokenIssuer();   // AllowMinting  false: throws if reached
        var users       = new StubUserRepository { Result = null };

        var controller = NewController(
            backupCodes, new StubSignInService(), issuer,
            new StubRecaptchaVerifier(), NewTenantContext(), users);

        var result = await controller.RedeemAsync(ValidRequest(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status401Unauthorized, StatusOf(result));
        Assert.Equal(0, backupCodes.ValidateCallCount);
        Assert.Equal(0, issuer.MintCallCount);

        // ...and the lookup used the uid Identity Platform vouched for, not the request body.
        Assert.Equal(1, users.CallCount);
        Assert.Equal(Uid, users.UidSeen);
    }

    [Fact]
    public async Task Redeem_KeysTheCodeByTheUsersOwnTenant_NotTheRequestHeader()
    {
        // A user whose Users row belongs to another tenant than the one resolved from
        // X-Tenant-ID (a cross-tenant SuperAdmin, say). The codes must follow the USER, or a set
        // generated under one header would be unredeemable under another — a lockout on the very
        // endpoint a locked-out user reaches for.
        var backupCodes = new StubBackupCodeService { AllowValidate = true, IsCodeValid = true };
        var users       = new StubUserRepository { Result = NewUser("otro-tenant") };

        var controller = NewController(
            backupCodes, new StubSignInService(), new StubCustomTokenIssuer { AllowMinting = true },
            new StubRecaptchaVerifier(), NewTenantContext(), users);

        var result = await controller.RedeemAsync(ValidRequest(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("otro-tenant", backupCodes.TenantSeen);
        Assert.Equal(UserId, backupCodes.UserSeen);
    }

    [Fact]
    public async Task Redeem_WithWrongBackupCode_NeverMintsAToken()
    {
        // AllowMinting stays false: the issuer throws if a token is minted for a bad code.
        var backupCodes = new StubBackupCodeService { AllowValidate = true, IsCodeValid = false };
        var issuer      = new StubCustomTokenIssuer();

        var controller = NewController(
            backupCodes, new StubSignInService(), issuer,
            new StubRecaptchaVerifier(), NewTenantContext());

        var result = await controller.RedeemAsync(ValidRequest(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status401Unauthorized, StatusOf(result));
        Assert.Equal(1, backupCodes.ValidateCallCount);
        Assert.Equal(0, issuer.MintCallCount);
    }

    [Fact]
    public async Task Redeem_WithFailedRecaptcha_RunsNothingDownstream()
    {
        var backupCodes = new StubBackupCodeService();
        var signIn      = new StubSignInService();

        var controller = NewController(
            backupCodes, signIn, new StubCustomTokenIssuer(),
            new StubRecaptchaVerifier { Success = false }, NewTenantContext());

        var result = await controller.RedeemAsync(ValidRequest(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, StatusOf(result));
        Assert.Equal(0, signIn.CallCount);
        Assert.Equal(0, backupCodes.ValidateCallCount);
    }

    [Fact]
    public async Task Redeem_WithLowRecaptchaScore_IsRejected()
    {
        var signIn = new StubSignInService();

        var controller = NewController(
            new StubBackupCodeService(), signIn, new StubCustomTokenIssuer(),
            new StubRecaptchaVerifier { Success = true, Score = 0.1 }, NewTenantContext());

        var result = await controller.RedeemAsync(ValidRequest(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, StatusOf(result));
        Assert.Equal(0, signIn.CallCount);
    }

    // ── The first-factor outcomes ───────────────────────────────────────────────

    [Fact]
    public async Task Redeem_WhenNoSecondFactorIsEnrolled_IsRejected()
    {
        // A correct password on an account with no TOTP has no 2FA to recover from. Minting a
        // session here would make this endpoint an alternative sign-in route.
        var signIn = new StubSignInService { Outcome = FirstFactorOutcome.NoMfaEnrolled };

        var controller = NewController(
            new StubBackupCodeService(), signIn, new StubCustomTokenIssuer(),
            new StubRecaptchaVerifier(), NewTenantContext());

        var result = await controller.RedeemAsync(ValidRequest(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status401Unauthorized, StatusOf(result));
    }

    [Fact]
    public async Task Redeem_WhenIdentityPlatformIsUnreachable_Answers502NotARejection()
    {
        // An outage must never be reported as "your credentials are wrong" — that would tell a
        // user with the correct password to go and reset it.
        var signIn = new StubSignInService { Outcome = FirstFactorOutcome.LookupFailed };

        var controller = NewController(
            new StubBackupCodeService(), signIn, new StubCustomTokenIssuer(),
            new StubRecaptchaVerifier(), NewTenantContext());

        var result = await controller.RedeemAsync(ValidRequest(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status502BadGateway, StatusOf(result));
    }

    [Fact]
    public async Task Redeem_WhenTheUidCannotBeResolved_IsRejectedWithoutTouchingTheCode()
    {
        var backupCodes = new StubBackupCodeService();
        var issuer      = new StubCustomTokenIssuer { UidToReturn = null };

        var controller = NewController(
            backupCodes, new StubSignInService(), issuer,
            new StubRecaptchaVerifier(), NewTenantContext());

        var result = await controller.RedeemAsync(ValidRequest(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status401Unauthorized, StatusOf(result));
        Assert.Equal(0, backupCodes.ValidateCallCount);
    }

    // ── Failures must be indistinguishable ──────────────────────────────────────

    [Fact]
    public async Task Redeem_AnswersEveryRejectionIdentically()
    {
        // Wrong password, no enrolled factor, unknown account, wrong code. If any of these were
        // distinguishable by status or message, an anonymous caller could enumerate accounts and
        // test passwords against this endpoint.
        var bodies = new List<(int Status, string? Detail)>();

        async Task CollectAsync(
            StubSignInService signIn, StubCustomTokenIssuer issuer, StubBackupCodeService codes)
        {
            var controller = NewController(
                codes, signIn, issuer, new StubRecaptchaVerifier(), NewTenantContext());

            var result = await controller.RedeemAsync(ValidRequest(), CancellationToken.None);
            var problem = Assert.IsType<ObjectResult>(result).Value as ProblemDetails;

            bodies.Add((StatusOf(result), problem?.Detail));
        }

        await CollectAsync(
            new StubSignInService { Outcome = FirstFactorOutcome.PasswordRejected },
            new StubCustomTokenIssuer(), new StubBackupCodeService());

        await CollectAsync(
            new StubSignInService { Outcome = FirstFactorOutcome.NoMfaEnrolled },
            new StubCustomTokenIssuer(), new StubBackupCodeService());

        await CollectAsync(
            new StubSignInService(),
            new StubCustomTokenIssuer { UidToReturn = null }, new StubBackupCodeService());

        await CollectAsync(
            new StubSignInService(),
            new StubCustomTokenIssuer(),
            new StubBackupCodeService { AllowValidate = true, IsCodeValid = false });

        Assert.Equal(4, bodies.Count);
        Assert.Single(bodies.Select(b => b.Status).Distinct());
        Assert.Single(bodies.Select(b => b.Detail).Distinct());
        Assert.Equal(StatusCodes.Status401Unauthorized, bodies[0].Status);
    }

    // ── Configuration and input guards ──────────────────────────────────────────

    [Fact]
    public async Task Redeem_WithNoWebApiKeyConfigured_Answers503AndCallsNothing()
    {
        var signIn    = new StubSignInService();
        var recaptcha = new StubRecaptchaVerifier();

        var controller = NewController(
            new StubBackupCodeService(), signIn, new StubCustomTokenIssuer(),
            recaptcha, NewTenantContext(webApiKey: null));

        var result = await controller.RedeemAsync(ValidRequest(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, StatusOf(result));
        Assert.Equal(0, recaptcha.CallCount);
        Assert.Equal(0, signIn.CallCount);
    }

    [Theory]
    [InlineData(null, "pw", "code", "captcha")]
    [InlineData(Email, null, "code", "captcha")]
    [InlineData(Email, "pw", null, "captcha")]
    [InlineData(Email, "pw", "code", null)]
    [InlineData("", "pw", "code", "captcha")]
    [InlineData(Email, "pw", "   ", "captcha")]
    public async Task Redeem_WithAMissingField_Answers400BeforeAnyExternalCall(
        string? email, string? password, string? code, string? captcha)
    {
        var signIn    = new StubSignInService();
        var recaptcha = new StubRecaptchaVerifier();

        var controller = NewController(
            new StubBackupCodeService(), signIn, new StubCustomTokenIssuer(),
            recaptcha, NewTenantContext());

        var result = await controller.RedeemAsync(
            new RedeemBackupCodeRequest(email, password, code, captcha),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        Assert.Equal(0, recaptcha.CallCount);
        Assert.Equal(0, signIn.CallCount);
    }

    [Fact]
    public async Task Redeem_WithNoTenantResolved_DoesNotReachFirebase()
    {
        var signIn = new StubSignInService();

        var controller = NewController(
            new StubBackupCodeService(), signIn, new StubCustomTokenIssuer(),
            new StubRecaptchaVerifier(), tenantContext: null);

        var result = await controller.RedeemAsync(ValidRequest(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status500InternalServerError, StatusOf(result));
        Assert.Equal(0, signIn.CallCount);
    }

    // ── The action must be anonymous ────────────────────────────────────────────

    [Fact]
    public void RedeemAction_IsMarkedAllowAnonymous()
    {
        // The controller carries a class-level [Authorize]. If this attribute were ever dropped,
        // the endpoint would start demanding the very session its callers cannot have, and the
        // recovery path would silently stop working for everyone who needs it.
        var method = typeof(MfaBackupCodesController)
            .GetMethod(nameof(MfaBackupCodesController.RedeemAsync));

        Assert.NotNull(method);
        Assert.NotEmpty(method!.GetCustomAttributes<AllowAnonymousAttribute>(inherit: false));
    }

    [Fact]
    public void SiblingActions_AreNotAnonymous()
    {
        // The counterpart to the test above: only redeem opts out. generate/validate/remaining
        // still read the uid from the principal and must keep requiring one.
        foreach (var name in new[] { "GenerateAsync", "ValidateAsync", "RemainingAsync" })
        {
            var method = typeof(MfaBackupCodesController).GetMethod(name);

            Assert.NotNull(method);
            Assert.Empty(method!.GetCustomAttributes<AllowAnonymousAttribute>(inherit: false));
        }
    }
}
