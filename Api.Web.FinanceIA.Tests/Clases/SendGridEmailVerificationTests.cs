// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-21

using System.Net;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// <see cref="SendGridEmailService.SendEmailVerificationEmailAsync"/>: the template comes from the
/// message, else <c>SendGrid:EmailVerificationTemplateId</c>, and never from a hardcoded id; the
/// payload carries <c>productName</c> and <c>verifyLink</c>; no log line carries the recipient or the link.
/// </summary>
public sealed class SendGridEmailVerificationTests
{
    private const string Tenant  = "TenantA";
    private const string Address = "analyst@example.com";
    private const string Link    = "https://portal.example.com/verify-email?mode=verifyEmail&oobCode=SECRET-OOB";

    [Fact]
    public async Task Accepted_SendsTheTemplateData_AndLogsNeitherAddressNorLink()
    {
        var handler = new StubSendGridHandler(HttpStatusCode.Accepted);
        var logger  = new CapturingLogger();

        await NewSut(handler, logger, ("SendGrid:EmailVerificationTemplateId", "d-global-verify"))
            .SendEmailVerificationEmailAsync(Message(templateId: null), Tenant, CancellationToken.None);

        Assert.Contains("d-global-verify", handler.LastBody);
        Assert.Contains("\"verifyLink\"", handler.LastBody);
        Assert.Contains("SECRET-OOB", handler.LastBody);
        Assert.Contains("\"productName\":\"Financia\"", handler.LastBody);
        Assert.Contains(Address, handler.LastBody);
        AssertNoSecretLogged(logger);
    }

    [Fact]
    public async Task TheMessagesTemplate_BeatsTheGlobalOne()
    {
        var handler = new StubSendGridHandler(HttpStatusCode.Accepted);

        await NewSut(handler, new CapturingLogger(), ("SendGrid:EmailVerificationTemplateId", "d-global-verify"))
            .SendEmailVerificationEmailAsync(Message(templateId: "d-tenant-verify"), Tenant, CancellationToken.None);

        Assert.Contains("d-tenant-verify", handler.LastBody);
        Assert.DoesNotContain("d-global-verify", handler.LastBody);
    }

    [Fact]
    public async Task NoTemplateAnywhere_Throws_AndSendsNothing()
    {
        var handler = new StubSendGridHandler(HttpStatusCode.Accepted);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewSut(handler, new CapturingLogger())
                .SendEmailVerificationEmailAsync(Message(templateId: null), Tenant, CancellationToken.None));

        Assert.Equal(string.Empty, handler.LastBody);
    }

    [Fact]
    public async Task Rejected_Throws_AndLogsNoSecretEither()
    {
        var logger = new CapturingLogger();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewSut(new StubSendGridHandler(HttpStatusCode.BadRequest), logger)
                .SendEmailVerificationEmailAsync(Message(templateId: "d-tenant-verify"), Tenant, CancellationToken.None));

        Assert.NotEmpty(logger.Entries);
        AssertNoSecretLogged(logger);
    }

    private static EmailVerificationEmail Message(string? templateId) => new()
    {
        ToEmail     = Address,
        VerifyLink  = Link,
        ProductName = "Financia",
        TemplateId  = templateId
    };

    private static SendGridEmailService NewSut(
        HttpMessageHandler handler, CapturingLogger logger, params (string Key, string Value)[] settings)
    {
        var values = settings.ToDictionary(s => s.Key, s => (string?)s.Value);
        values["SendGrid:ApiKey"] = "SG.test";
        return new SendGridEmailService(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(), logger, handler);
    }

    private static void AssertNoSecretLogged(CapturingLogger logger)
        => Assert.All(logger.Entries, e =>
        {
            Assert.DoesNotContain(Address, e, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SECRET-OOB", e);
        });

    private sealed class CapturingLogger : ILogger<SendGridEmailService>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var parts = new List<string> { formatter(state, exception) };
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                parts.AddRange(values.Select(v => v.Value?.ToString() ?? string.Empty));
            }

            if (exception is not null)
            {
                parts.Add(exception.ToString());
            }

            Entries.Add(string.Join(" | ", parts));
        }
    }

    private sealed class StubSendGridHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status);
        }
    }
}
