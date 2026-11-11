// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-28

using System.Net;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Arca;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Tests.Clases;
using Api.Web.FinanceIA.Tests.Servicios;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// POST /api/entities/lookup-arca over the REAL <see cref="ArcaConstanciaService"/> and a canned SOAP
/// answer. Production (SgrOneClickSandboxFB2, 5 rows): ARCA answers an unknown CUIT with the "No existe
/// persona con ese Id" SOAP Fault over HTTP 500, which surfaced as 502 "no disponible". It is now the
/// existing 404 and the not-found ApiCallLogs row; every other fault and a plain 5xx stay a 502 and a
/// failure row.
/// </summary>
public sealed class EntitiesControllerArcaLookupFaultTests
{
    private const string Tenant = "tenant-arca-fault";
    private const string Cuit   = "20111111112";

    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private sealed class RecordingApiCallLogService : IApiCallLogService
    {
        public List<(string Service, int? ResponseStatus, bool IsSuccess, string? ErrorMessage)> Calls { get; } = [];

        public Task LogAsync(string tenantId, string service, string endpoint, int? responseStatus,
            int? inputTokens, int? outputTokens, decimal? estimatedCostUsd, bool isSuccess, string? errorMessage,
            int durationMs, string? operation = null, string? model = null, Guid? userId = null,
            Guid? analysisId = null, long? chatMessageId = null, Guid? documentId = null, CancellationToken ct = default)
        {
            Calls.Add((service, responseStatus, isSuccess, errorMessage));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    private sealed record Harness(
        EntitiesController Sut, RecordingApiCallLogService ApiCallLog, RecordingLogger<EntitiesController> Logger);

    /// <summary>
    /// Only what lookup-arca touches before its ARCA outcome is decided: the duplicate-CUIT check (a real
    /// repository on an empty in-memory DB), the tenant CUIT, the ARCA service and the ApiCallLogs writer.
    /// </summary>
    private static Harness BuildHarness(IArcaConstanciaService arca)
    {
        var dbContext = new FinanciaDbContext(new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"arca-lookup-fault-{Guid.NewGuid()}").Options);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"Tenants:{Tenant}:Cuit"] = "30-99999999-7" })
            .Build();
        var apiCallLog = new RecordingApiCallLogService();
        var logger     = new RecordingLogger<EntitiesController>();

        var sut = new EntitiesController(
            new StubTenantContextAccessor
            {
                TenantContext = new TenantContext
                {
                    TenantId               = Tenant,
                    FirebaseProjectId      = "proj",
                    FirebaseCredentialJson = "{}",
                    RecaptchaSecretKey     = "secret"
                }
            },
            new EntityRepository(dbContext, Microsoft.Extensions.Logging.Abstractions.NullLogger<EntityRepository>.Instance),
            null!,          // IArcaPadronService
            arca,           // IArcaConstanciaService — the real service over a canned answer
            null!,          // IArcaWsaaService
            apiCallLog,     // IApiCallLogService
            null!,          // IAuditService
            null!,          // ILufeService
            null!,          // IBcraService
            null!,          // ICasfogService
            null!,          // INosisService
            null!,          // IIntegrationAccessService — reached only after ARCA found a persona
            null!,          // IDocumentRepository
            null!,          // IDocumentStorageService
            null!,          // IUserRepository
            null!,          // ICreditLineRepository
            null!,          // IAnalysisRepository
            null!,          // INoteRepository
            null!,          // IPersonLegajoRepository
            null!,          // INosisReportRepository
            null!,          // INosisManagerReportParser
            null!,          // KeyedAsyncLock
            config,
            logger)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        return new Harness(sut, apiCallLog, logger);
    }

    private static Task<IActionResult> LookupAsync(Harness h)
        => h.Sut.LookupEntityFromArca(new ArcaLookupByCuitRequest { Cuit = Cuit }, CancellationToken.None);

    private static ProblemDetails Problem(IActionResult result, int status)
    {
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(status, obj.StatusCode);
        return Assert.IsType<ProblemDetails>(obj.Value);
    }

    [Fact]
    public async Task TheNotFoundFaultOverHttp500_Returns404_AndTheNotFoundRow()
    {
        var h = BuildHarness(ArcaSoapFakes.Service(
            HttpStatusCode.InternalServerError, ArcaSoapFakes.FaultBody(ArcaSoapFakes.NotFoundFaultString)));

        var result = await LookupAsync(h);

        Assert.Equal(
            "No se encontró ninguna entidad en ARCA con el CUIT ingresado.",
            Problem(result, 404).Detail);
        var call = Assert.Single(h.ApiCallLog.Calls);
        Assert.Equal(("ARCA", (int?)502, false, (string?)null), call);
        Assert.True(ApiCallLogRowMarkers.IsNoData(call.Service, call.IsSuccess, call.ResponseStatus, call.ErrorMessage));
        Assert.DoesNotContain(h.Logger.Messages, m => m.Contains(Cuit));
    }

    public static TheoryData<HttpStatusCode, string> Failures() => new()
    {
        { HttpStatusCode.InternalServerError, ArcaSoapFakes.FaultBody("El token ha expirado") },
        { HttpStatusCode.InternalServerError, ArcaSoapFakes.FaultBody("La CUIT representada no se encuentra autorizada") },
        { HttpStatusCode.InternalServerError, ArcaSoapFakes.FaultBody("Computador no autorizado a acceder al servicio") },
        { HttpStatusCode.InternalServerError, "Internal Server Error" },
        { HttpStatusCode.ServiceUnavailable,  "" },
        { HttpStatusCode.OK,                  ArcaSoapFakes.FaultBody("El token ha expirado") },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task AnyOtherFaultOrA5xx_Returns502_AndAFailureRow(HttpStatusCode status, string body)
    {
        var h = BuildHarness(ArcaSoapFakes.Service(status, body));

        var result = await LookupAsync(h);

        Assert.Equal("El servicio de ARCA no está disponible temporalmente.", Problem(result, 502).Detail);
        var call = Assert.Single(h.ApiCallLog.Calls);
        Assert.Equal(("ARCA", (int?)502, false), (call.Service, call.ResponseStatus, call.IsSuccess));
        Assert.NotNull(call.ErrorMessage);
        Assert.False(ApiCallLogRowMarkers.IsNoData(call.Service, call.IsSuccess, call.ResponseStatus, call.ErrorMessage));
    }
}
