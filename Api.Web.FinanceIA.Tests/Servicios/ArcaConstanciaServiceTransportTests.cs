// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-28

using System.Net;
using Api.Web.FinanceIA.Models.Arca;
using Api.Web.FinanceIA.Servicios;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// <see cref="ArcaConstanciaService.GetPersonaByCuitAsync"/> over a canned SOAP answer. Production:
/// ARCA answers an unknown CUIT with the "No existe persona con ese Id" SOAP Fault over HTTP 500, which
/// used to throw before parsing and surface as a 502. That fault — and only that one — is "no person".
/// </summary>
public sealed class ArcaConstanciaServiceTransportTests
{
    private const string Cuit             = "20111111112";
    private const string CuitRepresentada = "30716327279";

    private sealed class RecordingLogger : ILogger<ArcaConstanciaService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private static Task<ArcaPersonaData?> LookupAsync(ArcaConstanciaService sut)
        => sut.GetPersonaByCuitAsync(Cuit, CuitRepresentada, CancellationToken.None);

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.OK)]
    public async Task TheNotFoundFault_IsNoPerson_WhateverTheStatus(HttpStatusCode status)
    {
        var logger = new RecordingLogger();
        var sut    = ArcaSoapFakes.Service(status, ArcaSoapFakes.FaultBody(ArcaSoapFakes.NotFoundFaultString), logger);

        Assert.Null(await LookupAsync(sut));
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains(Cuit) || e.Message.Contains("faultstring"));
    }

    public static TheoryData<HttpStatusCode, string> Failures() => new()
    {
        { HttpStatusCode.InternalServerError, ArcaSoapFakes.FaultBody("El token ha expirado") },
        { HttpStatusCode.InternalServerError, ArcaSoapFakes.FaultBody("La CUIT representada no se encuentra autorizada") },
        { HttpStatusCode.InternalServerError, ArcaSoapFakes.FaultBody("Computador no autorizado a acceder al servicio") },
        { HttpStatusCode.InternalServerError, ArcaSoapFakes.FaultBody("Error interno") },
        { HttpStatusCode.InternalServerError, "Internal Server Error" },                 // not XML
        { HttpStatusCode.ServiceUnavailable,  "" },
        { HttpStatusCode.BadGateway,          ArcaSoapFakes.FaultBody(ArcaSoapFakes.NotFoundFaultString).Replace("Fault", "Faulty") },   // the text, but not in a Fault
        { HttpStatusCode.OK,                  ArcaSoapFakes.FaultBody("El token ha expirado") },
        { HttpStatusCode.OK,                  """<soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"><soap:Body/></soap:Envelope>""" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task EveryOtherAnswer_IsAFailure(HttpStatusCode status, string body)
        => await Assert.ThrowsAsync<HttpRequestException>(
            () => LookupAsync(ArcaSoapFakes.Service(status, body)));
}
