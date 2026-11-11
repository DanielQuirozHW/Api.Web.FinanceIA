// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-06

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Arca;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// POST /api/entities/lookup-arca — the Portal's autofill for a new PyME — answers a NORMALIZED
/// razonSocial (BusinessNameNormalizer), while the fiscal payload the client sends back on create
/// (stored as FiscalDataJson) is passed through exactly as ARCA returned it. Synthetic data.
/// </summary>
public sealed class EntitiesControllerArcaLookupNameTests
{
    private const string Tenant = "tenant-arca-name";
    private const string Cuit   = "30711111118";

    [Theory]
    [InlineData("\"SILO CHICO S.A.\"",                   "SILO CHICO S.A.")]
    [InlineData("  «\"Agro   Norte S.A.\"» ",     "Agro Norte S.A.")]
    [InlineData("SOCIEDAD \"LA ESPERANZA\" S.A.",         "SOCIEDAD \"LA ESPERANZA\" S.A.")]
    [InlineData("\"\"",                                  "DESCONOCIDO")]
    public async Task Lookup_ReturnsTheNormalizedRazonSocial(string arcaName, string expected)
    {
        var sut = BuildSut(Persona(arcaName));

        var result = await sut.LookupEntityFromArca(new ArcaLookupByCuitRequest { Cuit = Cuit }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(expected, Assert.IsType<ArcaLookupResponse>(ok.Value).RazonSocial);
    }

    /// <summary>
    /// Only the name shown for autofill changes: the fiscal snapshot (FiscalDataJson on create) is
    /// ARCA's own object, untouched apart from the key-status stamp the lookup always added.
    /// </summary>
    [Fact]
    public async Task Lookup_LeavesTheFiscalPayloadAsReceived()
    {
        var fiscal = new ArcaFiscalData(
            FechaContratoSocial: null, EsSucesion: false, MesCierre: 12,
            Actividades: [], Impuestos: [], Regimenes: [], FiscalRegime: "ResponsableInscripto");
        var sut = BuildSut(Persona("\"SILO CHICO S.A.\"") with { FiscalData = fiscal });

        var result = await sut.LookupEntityFromArca(new ArcaLookupByCuitRequest { Cuit = Cuit }, CancellationToken.None);

        var body = Assert.IsType<ArcaLookupResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.NotNull(body.Fiscal);
        Assert.Equal(12, body.Fiscal!.MesCierre);
        Assert.Equal("ResponsableInscripto", body.Fiscal.FiscalRegime);
        Assert.Equal("SILO CHICO S.A.", body.RazonSocial);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ArcaPersonaData Persona(string razonSocial) => new(
        Cuit: Cuit, TipoPersona: "JURIDICA", EstadoClave: "ACTIVO", Nombre: null, Apellido: null,
        RazonSocial: razonSocial, TipoDocumento: null, NumeroDocumento: null, FechaNacimiento: null,
        MesCierre: 12, IdActividadPrincipal: null, DescripcionActividadPrincipal: null,
        PeriodoActividadPrincipal: null, FormaJuridica: null, DomicilioFiscal: null, DomicilioLegal: null);

    private static EntitiesController BuildSut(ArcaPersonaData persona)
    {
        var db = new FinanciaDbContext(new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"arca-lookup-name-{Guid.NewGuid()}").Options);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"Tenants:{Tenant}:Cuit"] = "30-99999999-7" })
            .Build();

        return new EntitiesController(
            new StubTenantContextAccessor(),
            new EntityRepository(db, NullLogger<EntityRepository>.Instance),
            null!,                              // IArcaPadronService
            new StubArcaConstanciaService(persona),
            null!,                              // IArcaWsaaService
            new NoOpApiCallLogService(),
            null!,                              // IAuditService
            null!,                              // ILufeService — LUFE is disabled by the profile below
            null!,                              // IBcraService
            null!,                              // ICasfogService
            null!,                              // INosisService
            new NoLufeIntegrationAccess(),
            null!,                              // IDocumentRepository
            null!,                              // IDocumentStorageService
            null!,                              // IUserRepository
            null!,                              // ICreditLineRepository
            null!,                              // IAnalysisRepository
            null!,                              // INoteRepository
            null!,                              // IPersonLegajoRepository
            null!,                              // INosisReportRepository
            null!,                              // INosisManagerReportParser
            null!,                              // KeyedAsyncLock
            config,
            NullLogger<EntitiesController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; } = new()
        {
            TenantId               = Tenant,
            FirebaseProjectId      = "proj",
            FirebaseCredentialJson = "{}",
            RecaptchaSecretKey     = "secret"
        };
    }

    private sealed class StubArcaConstanciaService(ArcaPersonaData persona) : IArcaConstanciaService
    {
        public Task<ArcaPersonaData?> GetPersonaByCuitAsync(
            string cuit, string cuitRepresentada, CancellationToken cancellationToken = default)
            => Task.FromResult<ArcaPersonaData?>(persona);
    }

    private sealed class NoLufeIntegrationAccess : IIntegrationAccessService
    {
        public Task<TenantProfile> ResolveEntitlementsAsync(string tenantId, CancellationToken ct)
            => Task.FromResult(new TenantProfile("non_sgr", LufeEnabled: false, CasfogEnabled: false, NosisEnabled: false));

        public string? GetLufeApiKey(string tenantId) => null;
        public bool IsNosisConfigured() => false;
        public bool IsCasfogConfigured() => false;
        public Task<OptionalIntegrationAccess> ResolveUsableAsync(string tenantId, CancellationToken ct)
            => throw new NotSupportedException("lookup-arca does not resolve usable integrations.");
    }

    private sealed class NoOpApiCallLogService : IApiCallLogService
    {
        public Task LogAsync(string tenantId, string service, string endpoint, int? responseStatus,
            int? inputTokens, int? outputTokens, decimal? estimatedCostUsd, bool isSuccess, string? errorMessage,
            int durationMs, string? operation = null, string? model = null, Guid? userId = null,
            Guid? analysisId = null, long? chatMessageId = null, Guid? documentId = null, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
