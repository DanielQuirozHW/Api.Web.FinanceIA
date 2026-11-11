// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-28

using System.Net;
using System.Text;
using Api.Web.FinanceIA.Models.Arca;
using Api.Web.FinanceIA.Servicios;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// A real <see cref="ArcaConstanciaService"/> over a canned SOAP answer, so a test drives the transport,
/// the parser and the callers together — the path a stubbed IArcaConstanciaService skips. Hand-rolled
/// HttpMessageHandler and WSAA stubs, per the repo's no-mocking-library convention.
/// </summary>
internal static class ArcaSoapFakes
{
    /// <summary>The fault ARCA answers an unknown CUIT with, as captured in production (HTTP 500).</summary>
    public const string NotFoundFaultString = "No existe persona con ese Id";

    public static string FaultBody(string faultString) => $"""
        <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
          <soap:Body>
            <soap:Fault>
              <faultcode>soap:Server</faultcode>
              <faultstring>{faultString}</faultstring>
            </soap:Fault>
          </soap:Body>
        </soap:Envelope>
        """;

    public static ArcaConstanciaService Service(
        HttpStatusCode status, string body, ILogger<ArcaConstanciaService>? logger = null)
        => new(
            new HttpClient(new CannedHandler(status, body)) { BaseAddress = new Uri("https://arca.test/") },
            logger ?? NullLogger<ArcaConstanciaService>.Instance,
            new StubWsaaService());

    private sealed class CannedHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/xml")
            });
    }

    private sealed class StubWsaaService : IArcaWsaaService
    {
        public Task<WsaaTicket> GetTicketAsync(string serviceId, CancellationToken cancellationToken = default)
            => Task.FromResult(new WsaaTicket("token", "sign", DateTime.UtcNow.AddHours(1)));
    }
}
