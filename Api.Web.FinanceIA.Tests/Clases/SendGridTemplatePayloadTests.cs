// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-26

using System.Net;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// The exact request body <see cref="SendGridEmailService"/> sends for each of the three emails.
/// <para>
/// The "no branding" bodies are GOLDENS: they were captured from the sender as it stood before
/// per-tenant branding existed and are compared whole, character for character. A tenant with no
/// TenantSettings rows must keep sending exactly these. With branding, <c>companyName</c> and
/// <c>logoUrl</c> are appended to <c>dynamic_template_data</c>, and an unset one is OMITTED — never
/// sent as "" or null — so a template's <c>{{#if companyName}}</c> falls through.
/// </para>
/// </summary>
public sealed class SendGridTemplatePayloadTests
{
    private const string Tenant       = "TenantA";
    private const string InviteLink   = "https://portal.example.com/register/invite?token=abc_DEF-123";
    private const string ResetLink    = "https://portal.example.com/recover?mode=resetPassword&oobCode=OOB-1&lang=es-AR";
    private const string VerifyLink   = "https://portal.example.com/verify-email?mode=verifyEmail&oobCode=OOB-2";
    private const string Company      = "Movil SGR";
    private const string Logo         = "https://cdn.example.com/movil/logo.png";

    // ── Goldens: no tenant settings → today's bytes ──────────────────────────────

    private const string InvitationBuiltInDefaults =
        """{"from":{"name":"FinanceIA","email":"noreply@financia.app"},"personalizations":[{"to":[{"email":"invitee@example.com"}],"dynamic_template_data":{"inviterName":"Ana Pérez","invitationLink":"https://portal.example.com/register/invite?token=abc_DEF-123"}}],"template_id":"d-5f0dd166b22c41d98278e6ac28711be2"}""";

    private const string InvitationGlobalConfig =
        """{"from":{"name":"Global Name","email":"global@financia.app"},"personalizations":[{"to":[{"email":"invitee@example.com"}],"dynamic_template_data":{"inviterName":"Ana Pérez","invitationLink":"https://portal.example.com/register/invite?token=abc_DEF-123"}}],"template_id":"d-global-invite"}""";

    private const string ResetBuiltInDefaults =
        """{"from":{"name":"Financia","email":"noreply@financia.app"},"personalizations":[{"to":[{"email":"analyst@example.com"}],"dynamic_template_data":{"productName":"Financia","resetLink":"https://portal.example.com/recover?mode=resetPassword\u0026oobCode=OOB-1\u0026lang=es-AR","expiresInText":"1 hora"}}],"template_id":"d-global-reset"}""";

    private const string ResetGlobalConfig =
        """{"from":{"name":"Global Name","email":"global@financia.app"},"personalizations":[{"to":[{"email":"analyst@example.com"}],"dynamic_template_data":{"productName":"Financia","resetLink":"https://portal.example.com/recover?mode=resetPassword\u0026oobCode=OOB-1\u0026lang=es-AR","expiresInText":"1 hora"}}],"template_id":"d-global-reset"}""";

    private const string VerifyBuiltInDefaults =
        """{"from":{"name":"Financia","email":"noreply@financia.app"},"personalizations":[{"to":[{"email":"analyst@example.com"}],"dynamic_template_data":{"productName":"Financia","verifyLink":"https://portal.example.com/verify-email?mode=verifyEmail\u0026oobCode=OOB-2"}}],"template_id":"d-global-verify"}""";

    private const string VerifyGlobalConfig =
        """{"from":{"name":"Global Name","email":"global@financia.app"},"personalizations":[{"to":[{"email":"analyst@example.com"}],"dynamic_template_data":{"productName":"Financia","verifyLink":"https://portal.example.com/verify-email?mode=verifyEmail\u0026oobCode=OOB-2"}}],"template_id":"d-global-verify"}""";

    [Theory]
    [InlineData(false, InvitationBuiltInDefaults)]
    [InlineData(true, InvitationGlobalConfig)]
    public async Task Invitation_WithoutTenantSettings_IsTheGoldenBody(bool globalConfig, string expected)
    {
        var handler = new StubSendGridHandler();

        await NewSut(handler, globalConfig).SendInvitationEmailAsync(Invitation(), Tenant, CancellationToken.None);

        Assert.Equal(expected, handler.LastBody);
    }

    [Theory]
    [InlineData(false, ResetBuiltInDefaults)]
    [InlineData(true, ResetGlobalConfig)]
    public async Task PasswordReset_WithoutTenantSettings_IsTheGoldenBody(bool globalConfig, string expected)
    {
        var handler = new StubSendGridHandler();

        await NewSut(handler, globalConfig).SendPasswordResetEmailAsync(Reset(), Tenant, CancellationToken.None);

        Assert.Equal(expected, handler.LastBody);
    }

    [Theory]
    [InlineData(false, VerifyBuiltInDefaults)]
    [InlineData(true, VerifyGlobalConfig)]
    public async Task EmailVerification_WithoutTenantSettings_IsTheGoldenBody(bool globalConfig, string expected)
    {
        var handler = new StubSendGridHandler();

        await NewSut(handler, globalConfig).SendEmailVerificationEmailAsync(Verify(), Tenant, CancellationToken.None);

        Assert.Equal(expected, handler.LastBody);
    }

    [Fact]
    public async Task BlankBrandingValues_AreOmitted_LeavingTheGoldenBody()
    {
        var handler = new StubSendGridHandler();

        await NewSut(handler, globalConfig: false).SendInvitationEmailAsync(
            Invitation() with { CompanyName = "  ", LogoUrl = "" }, Tenant, CancellationToken.None);

        Assert.Equal(InvitationBuiltInDefaults, handler.LastBody);
    }

    // ── Branding ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Invitation_WithBranding_AppendsCompanyNameAndLogoUrl()
    {
        var handler = new StubSendGridHandler();

        await NewSut(handler, globalConfig: false).SendInvitationEmailAsync(
            Invitation() with { CompanyName = Company, LogoUrl = Logo }, Tenant, CancellationToken.None);

        Assert.Contains(
            """dynamic_template_data":{"inviterName":"Ana Pérez","invitationLink":"https://portal.example.com/register/invite?token=abc_DEF-123","companyName":"Movil SGR","logoUrl":"https://cdn.example.com/movil/logo.png"}""",
            handler.LastBody);
    }

    [Fact]
    public async Task PasswordReset_WithBranding_AppendsCompanyNameAndLogoUrl()
    {
        var handler = new StubSendGridHandler();

        await NewSut(handler, globalConfig: false).SendPasswordResetEmailAsync(
            Reset() with { CompanyName = Company, LogoUrl = Logo }, Tenant, CancellationToken.None);

        Assert.Contains(
            """dynamic_template_data":{"productName":"Financia","resetLink":"https://portal.example.com/recover?mode=resetPassword\u0026oobCode=OOB-1\u0026lang=es-AR","expiresInText":"1 hora","companyName":"Movil SGR","logoUrl":"https://cdn.example.com/movil/logo.png"}""",
            handler.LastBody);
    }

    [Fact]
    public async Task EmailVerification_WithBranding_AppendsCompanyNameAndLogoUrl()
    {
        var handler = new StubSendGridHandler();

        await NewSut(handler, globalConfig: false).SendEmailVerificationEmailAsync(
            Verify() with { CompanyName = Company, LogoUrl = Logo }, Tenant, CancellationToken.None);

        Assert.Contains(
            """dynamic_template_data":{"productName":"Financia","verifyLink":"https://portal.example.com/verify-email?mode=verifyEmail\u0026oobCode=OOB-2","companyName":"Movil SGR","logoUrl":"https://cdn.example.com/movil/logo.png"}""",
            handler.LastBody);
    }

    [Fact]
    public async Task CompanyNameWithoutLogo_SendsOnlyCompanyName()
    {
        var handler = new StubSendGridHandler();

        await NewSut(handler, globalConfig: false).SendEmailVerificationEmailAsync(
            Verify() with { CompanyName = Company }, Tenant, CancellationToken.None);

        Assert.Contains("\"companyName\":\"Movil SGR\"}", handler.LastBody);
        Assert.DoesNotContain("logoUrl", handler.LastBody);
    }

    // ── Invitation sender and template: tenant → SendGrid config → built-in ──────

    [Fact]
    public async Task Invitation_TenantSenderAndTemplate_BeatTheGlobalConfig()
    {
        var handler = new StubSendGridHandler();

        await NewSut(handler, globalConfig: true).SendInvitationEmailAsync(
            Invitation() with
            {
                FromEmail  = "no-reply@movil.example.com",
                FromName   = "Movil SGR",
                TemplateId = "d-tenant-invite"
            },
            Tenant,
            CancellationToken.None);

        Assert.StartsWith("""{"from":{"name":"Movil SGR","email":"no-reply@movil.example.com"}""", handler.LastBody);
        Assert.EndsWith("""template_id":"d-tenant-invite"}""", handler.LastBody);
        Assert.DoesNotContain("d-global-invite", handler.LastBody);
    }

    [Fact]
    public async Task Invitation_BlankTenantSenderAndTemplate_FallBackToTheGlobalConfig()
    {
        var handler = new StubSendGridHandler();

        await NewSut(handler, globalConfig: true).SendInvitationEmailAsync(
            Invitation() with { FromEmail = " ", FromName = "", TemplateId = " " }, Tenant, CancellationToken.None);

        Assert.Equal(InvitationGlobalConfig, handler.LastBody);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static InvitationEmail Invitation() => new()
    {
        ToEmail        = "invitee@example.com",
        InvitationLink = InviteLink,
        InviterName    = "Ana Pérez"
    };

    private static PasswordResetEmail Reset() => new()
    {
        ToEmail       = "analyst@example.com",
        ResetLink     = ResetLink,
        ProductName   = "Financia",
        ExpiresInText = "1 hora"
    };

    private static EmailVerificationEmail Verify() => new()
    {
        ToEmail     = "analyst@example.com",
        VerifyLink  = VerifyLink,
        ProductName = "Financia"
    };

    /// <summary>
    /// <paramref name="globalConfig"/> false: only the API key and the reset/verification templates
    /// (which have no built-in fallback), so every other value is the built-in default.
    /// </summary>
    private static SendGridEmailService NewSut(HttpMessageHandler handler, bool globalConfig)
    {
        var values = new Dictionary<string, string?>
        {
            ["SendGrid:ApiKey"]                     = "SG.test",
            ["SendGrid:PasswordResetTemplateId"]    = "d-global-reset",
            ["SendGrid:EmailVerificationTemplateId"] = "d-global-verify"
        };

        if (globalConfig)
        {
            values["SendGrid:FromEmail"]            = "global@financia.app";
            values["SendGrid:FromName"]             = "Global Name";
            values["SendGrid:InvitationTemplateId"] = "d-global-invite";
        }

        return new SendGridEmailService(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            NullLogger<SendGridEmailService>.Instance,
            handler);
    }

    private sealed class StubSendGridHandler : HttpMessageHandler
    {
        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }
}
