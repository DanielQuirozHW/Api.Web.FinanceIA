// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-26

using System.Security.Claims;
using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Covers GET /api/analyses/{id}/context — the four frozen source snapshots that
/// <see cref="AnalysisController.GetById"/> stopped shipping.
///
/// <para>
/// These are point-in-time copies taken when the analysis ran, not the live entity values, so unlike
/// the entity per-source endpoints there is no refresh counterpart and nothing here could consult an
/// external gateway even in principle: the repository read is the whole endpoint. What IS load-bearing
/// is the 404-vs-null split — a missing result is a 404, while a result that exists but froze no
/// snapshot is a 200 carrying nulls. Collapsing those two would make "this analysis predates the
/// integration" indistinguishable from "wrong id", and NOSIS is in the first state on every row today.
/// </para>
///
/// <para>
/// The other thing under test is fault isolation: the four blobs are parsed independently, so garbage
/// in one leaves the other three intact. They were persisted verbatim from the entity and are never
/// re-validated, so one unparseable snapshot must not empty the whole section.
/// </para>
///
/// Hand-rolled private sealed stubs (no mock library). Only the repository this path reads is stubbed;
/// the rest of the ctor dependencies are passed as null! (the constructor performs plain assignment).
/// </summary>
public sealed class AnalysisControllerContextTests
{
    private const string Tenant      = "tenant-1";
    private const string FirebaseUid = "uid-1";

    // ── Stubs ─────────────────────────────────────────────────────────────────

    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private sealed class StubAnalysisResultRepository : IAnalysisResultRepository
    {
        public AnalysisContextProjection? ContextToReturn { get; set; }

        public Task<AnalysisContextProjection?> GetContextSnapshotByIdAsync(
            Guid resultId, string tenantId, CancellationToken ct)
            => Task.FromResult(ContextToReturn);

        /// <summary>
        /// The BCRA-only read behind GET {id}/bcra/cheques. /context must not reach for it: the
        /// cheque rows are the one thing it no longer serves, so a call here would mean it had gone
        /// back to assembling them.
        /// </summary>
        public Task<AnalysisBcraProjection?> GetBcraSnapshotByIdAsync(
            Guid resultId, string tenantId, CancellationToken ct)
            => throw new InvalidOperationException(
                "GET /api/analyses/{id}/context does not serve cheques.detalle.");

        /// <summary>
        /// The full-row read. This endpoint exists precisely so that neither it nor the detail pays
        /// for the whole row, so a call here is a regression and fails with a named exception.
        /// </summary>
        public Task<AnalysisResult?> GetByIdAsync(Guid resultId, string tenantId, CancellationToken ct)
            => throw new InvalidOperationException(
                "GET /api/analyses/{id}/context must read via GetContextSnapshotByIdAsync.");

        public Task<AnalysisResultDetailProjection?> GetDetailByIdAsync(
            Guid resultId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<AnalysisRatiosProjection?> GetRatiosByIdAsync(
            Guid resultId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<AnalysisResult>> GetAllByTenantAsync(string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<AnalysisResult?> GetByDocumentIdAsync(Guid documentId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<AnalysisResult> CreateAsync(AnalysisResult result, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<AnalysisResult>> GetByEntityIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<AnalysisResult> UpdateAsync(AnalysisResult result, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, Guid>> GetResultIdsByAnalysisIdsAsync(
            IEnumerable<Guid> analysisIds, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<(IReadOnlyList<AnalysisListProjection> Items, int TotalCount)> GetListPageByTenantAsync(
            string tenantId, int skip, int take, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<(IReadOnlyList<AnalysisListProjection> Items, int TotalCount)> GetListPageByEntityAsync(
            Guid entityId, string tenantId, int skip, int take, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<AnalysisLabelProjection?> GetLabelByIdAsync(
            Guid resultId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static TenantContext BuildTenant() => new()
    {
        TenantId               = Tenant,
        FirebaseProjectId      = "proj",
        FirebaseCredentialJson = "{}",
        RecaptchaSecretKey     = "secret"
    };

    private const string LufeJson   = """{"entidad":{"cuit":30712345671,"nombre":"Metalúrgica del Sur S.A."}}""";
    private const string FiscalJson = """{"mesCierre":12,"actividades":[],"impuestos":[],"regimenes":[]}""";
    private const string CasfogJson = """{"garantias":{"cantidadGarantiasVigentes":3,"saldoBrutoGarantiasVigentes":150000.50}}""";
    private const string NosisJson  = """{"identificacion":"30-71234567-1","razonSocial":"Metalúrgica del Sur S.A."}""";

    /// <summary>
    /// A BCRA document with every collection the Contexto tab renders populated — the reporting
    /// banks, the per-entity detail, the situación history, the cheque causales and the flat cheque
    /// list. Small here, enormous in production (2930 cheques on a real row), which is the whole
    /// reason it moved to this endpoint. Deliberately includes <c>deuda.estado</c> and
    /// <c>deuda.situacionMaxima</c>: the detail keeps serving those two, so they are still present
    /// here rather than carved out.
    /// </summary>
    private const string BcraJson = """
        {"deuda":{"identificacion":30712345671,"denominacion":"Metalúrgica del Sur S.A.",
        "situacionMaxima":3,"montoTotal":48500.75,
        "entidadesInformantes":["BANCO NACION","BANCO GALICIA","BANCO SANTANDER"],
        "periodoConsultado":"202607",
        "historialSituaciones":[{"periodo":"202607","situacionMaxima":3},{"periodo":"202606","situacionMaxima":2}],
        "entidadesDetalle":[{"entidad":"BANCO NACION","situacion":3,"monto":30000.5,"diasAtrasoPago":95,
        "refinanciaciones":false,"situacionJuridica":false,"enRevision":false,"procesoJud":false}],
        "tieneProcesoJudicial":false,"tieneSituacionJuridica":false,"enRevision":false,
        "maxDiasAtraso":95,"estado":0,"fetchedAt":"2026-07-15T10:00:00Z"},
        "macro":{"inflacionMensual":2.5,"tasaReferencia":40.0,"fetchedAt":"2026-07-15T10:00:00Z"},
        "cambiario":{"tipoCambioOficial":950.0,"fetchedAt":"2026-07-15T10:00:00Z"},
        "cheques":{"identificacion":30712345671,"totalRechazados":2,"totalImpagos":1,
        "montoTotalRechazado":85000.0,"montoTotalImpago":35000.0,
        "causales":[{"causal":"SIN FONDOS SUFICIENTES","cantidad":2,"monto":85000.0,"cantidadImpagos":1}],
        "detalle":[{"causal":"SIN FONDOS SUFICIENTES","entidad":11,"nroCheque":10001,
        "fechaRechazo":"2026-05-02T00:00:00Z","monto":50000.0,"fechaPago":"2026-05-20T00:00:00Z",
        "ctaPersonal":false,"enRevision":false,"procesoJud":false,"pagado":true},
        {"causal":"SIN FONDOS SUFICIENTES","entidad":11,"nroCheque":10002,
        "fechaRechazo":"2026-06-11T00:00:00Z","monto":35000.0,"fechaPago":null,
        "ctaPersonal":false,"enRevision":false,"procesoJud":false,"pagado":false}],
        "estado":0,"fetchedAt":"2026-07-15T10:00:00Z"}}
        """;

    private static AnalysisContextProjection Snap(
        Guid resultId, string? lufe, string? fiscal, string? casfog, string? nosis, string? bcra) => new()
    {
        ResultId       = resultId,
        LufeDataJson   = lufe,
        FiscalDataJson = fiscal,
        CasfogDataJson = casfog,
        NosisDataJson  = nosis,
        BcraDataJson   = bcra
    };

    private static AnalysisController BuildSut(
        StubAnalysisResultRepository resultRepo,
        bool withTenant = true)
    {
        var sut = new AnalysisController(
            new StubTenantContextAccessor { TenantContext = withTenant ? BuildTenant() : null },
            null!,            // documentRepository
            resultRepo,       // analysisResultRepository
            null!,            // analysisRepository
            null!,            // claudeService
            null!,            // cashFlowReconciliationService
            null!,            // cashFlowNarrativeService
            null!,            // alertRatioReconciliationService
            null!,            // entityRepository
            null!,            // userRepository
            null!,            // chatRepository
            null!,            // creditLineRepository
            null!,            // operationRepository
            null!,            // auditService
            null!,            // apiCallLogService
            null!,            // excelCashFlowParser
            null!,            // billingOptions
            null!,            // tenantBillingConfigService
            null!,            // userApprovalLimitRepository
            null!,            // decisionNotifier
            null!,            // approvalQuorumService — this suite never reaches a quorum vote
            new Api.Web.FinanceIA.Tests.Legajo.FakeLegajoDocumentRepository(),
            null!,            // analysisPipeline
            null!,            // analysisJobQueue
            null!,            // analysisRunRegistry
            null!,            // analysisRunStateRepository
            new Api.Web.FinanceIA.Tests.PersonAnalysis.StubPersonAnalysisRepository(),
            NullLogger<AnalysisController>.Instance);

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("uid", FirebaseUid) }, "Test"))
        };
        sut.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return sut;
    }

    // ── The stored snapshots come back ────────────────────────────────────────

    /// <summary>
    /// All five stored payloads are returned, parsed, under the same field names the detail payload
    /// used to carry them — so the Portal reuses its existing mapping unchanged.
    /// </summary>
    [Fact]
    public async Task GetContext_ReturnsTheFiveParsedPayloads()
    {
        var resultId = Guid.NewGuid();
        var repo = new StubAnalysisResultRepository
        {
            ContextToReturn = Snap(resultId, LufeJson, FiscalJson, CasfogJson, NosisJson, BcraJson)
        };
        var sut = BuildSut(repo);

        var response = await sut.GetContextAsync(resultId, CancellationToken.None);

        var body = Assert.IsType<AnalysisContextResponse>(Assert.IsType<OkObjectResult>(response).Value);

        Assert.NotNull(body.Lufe);
        Assert.Equal("Metalúrgica del Sur S.A.", body.Lufe!.Entidad?.Nombre);

        Assert.NotNull(body.Fiscal);
        Assert.Equal(12, body.Fiscal!.MesCierre);

        Assert.NotNull(body.Casfog);
        Assert.Equal(3, body.Casfog!.Garantias.CantidadGarantiasVigentes);

        Assert.NotNull(body.Nosis);
        Assert.Equal("Metalúrgica del Sur S.A.", body.Nosis!.RazonSocial);

        Assert.NotNull(body.Bcra);
        Assert.Equal(BcraDeudaConsultaEstado.DebtFound, body.Bcra!.Deuda?.Estado);
    }

    /// <summary>
    /// The BCRA blob arrives whole EXCEPT <c>cheques.detalle</c>, which moved to
    /// GET {id}/bcra/cheques. Every collection the Contexto tab renders on open is asserted by
    /// count, because the failure this guards against is silent: a change that dropped
    /// <c>causales</c> or <c>entidadesInformantes</c> along with the cheque rows would still produce
    /// a non-null object and a page that renders zeros.
    ///
    /// <para>
    /// The equality assertion is the contract, and its fixed point moved with the change: what this
    /// endpoint emits must be byte-identical to the stored blob deserialised and then had
    /// <c>detalle</c> nulled — nothing else may differ. Before the split it compared against the
    /// blob untouched; keeping the comparison whole-object rather than field-by-field is what makes
    /// "only the array changed" an assertion rather than a claim. <c>bcra</c> is a typed
    /// <c>BcraData</c>, like the four snapshots beside it, so the fixed point is the typed
    /// round-trip rather than the stored text.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GetContext_ReturnsTheBcraBlobExceptChequeDetalle()
    {
        var resultId = Guid.NewGuid();
        var repo = new StubAnalysisResultRepository
        {
            ContextToReturn = Snap(resultId, null, null, null, null, BcraJson)
        };
        var sut = BuildSut(repo);

        var response = await sut.GetContextAsync(resultId, CancellationToken.None);

        var body = Assert.IsType<AnalysisContextResponse>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.NotNull(body.Bcra);

        // Nothing the Contexto tab renders on open was dropped on the way through.
        Assert.Equal(3, body.Bcra!.Deuda?.EntidadesInformantes?.Count);
        Assert.Equal(2, body.Bcra.Deuda?.HistorialSituaciones?.Count);
        Assert.Single(body.Bcra.Deuda?.EntidadesDetalle!);
        Assert.Single(body.Bcra.Cheques?.Causales!);
        Assert.Equal(95, body.Bcra.Deuda?.MaxDiasAtraso);
        Assert.Equal(2.5, body.Bcra.Macro?.InflacionMensual);
        Assert.Equal(950.0, body.Bcra.Cambiario?.TipoCambioOficial);

        // The cheques block is present with its aggregates; only the per-cheque array is gone.
        Assert.NotNull(body.Bcra.Cheques);
        Assert.Null(body.Bcra.Cheques!.Detalle);
        Assert.Equal(2, body.Bcra.Cheques.TotalRechazados);
        Assert.Equal(1, body.Bcra.Cheques.TotalImpagos);
        Assert.Equal(85000.0m, body.Bcra.Cheques.MontoTotalRechazado);
        Assert.Equal(35000.0m, body.Bcra.Cheques.MontoTotalImpago);

        // …and what it emits is the stored blob with that one array nulled, and nothing else changed.
        var storedMinusDetalle = JsonSerializer.Deserialize<BcraData>(
            BcraJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        storedMinusDetalle!.Cheques!.Detalle = null;

        Assert.Equal(
            JsonSerializer.Serialize(storedMinusDetalle),
            JsonSerializer.Serialize(body.Bcra));
    }

    /// <summary>
    /// The forbidden-marker half, mirroring the idiom #149 used on the chat prompt: no per-cheque
    /// field may survive anywhere in the SERIALISED response, not merely in the one property the
    /// assertions above reach for. <c>nroCheque</c> is unique to <c>BcraChequeDetalle</c>, so its
    /// absence is proof no row leaked through another path — and the positive assertions pin the
    /// aggregates that must NOT have gone with them.
    /// </summary>
    [Fact]
    public async Task GetContext_SerialisedPayloadCarriesNoPerChequeRow()
    {
        var resultId = Guid.NewGuid();
        var repo = new StubAnalysisResultRepository
        {
            ContextToReturn = Snap(resultId, null, null, null, null, BcraJson)
        };
        var sut = BuildSut(repo);

        var response = await sut.GetContextAsync(resultId, CancellationToken.None);

        var body = Assert.IsType<AnalysisContextResponse>(Assert.IsType<OkObjectResult>(response).Value);
        var json = JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        // The rows, and every field only they carry, are gone.
        Assert.DoesNotContain("nroCheque",    json, StringComparison.Ordinal);
        Assert.DoesNotContain("fechaRechazo", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ctaPersonal",  json, StringComparison.Ordinal);
        Assert.DoesNotContain("10001",        json, StringComparison.Ordinal);
        Assert.DoesNotContain("10002",        json, StringComparison.Ordinal);

        // Every aggregate beside them survives, as does the causal breakdown and the rest of deuda.
        Assert.Contains("totalRechazados",         json, StringComparison.Ordinal);
        Assert.Contains("montoTotalRechazado",     json, StringComparison.Ordinal);
        Assert.Contains("causales",                json, StringComparison.Ordinal);
        Assert.Contains("SIN FONDOS SUFICIENTES",  json, StringComparison.Ordinal);
        Assert.Contains("entidadesInformantes",    json, StringComparison.Ordinal);
        Assert.Contains("tipoCambioOficial",       json, StringComparison.Ordinal);
    }

    /// <summary>
    /// An analysis that exists but stored none of the five is a 200 carrying five nulls — NOT a 404.
    /// This is the ordinary case for analyses that predate a source's integration, and for NOSIS it
    /// is the case on every row today. For BCRA it is what an entity without a CUIT looks like.
    /// </summary>
    [Fact]
    public async Task GetContext_WhenNoSnapshotWasFrozen_Returns200WithNulls()
    {
        var resultId = Guid.NewGuid();
        var repo = new StubAnalysisResultRepository
        {
            ContextToReturn = Snap(resultId, null, null, null, null, null)
        };
        var sut = BuildSut(repo);

        var response = await sut.GetContextAsync(resultId, CancellationToken.None);

        var body = Assert.IsType<AnalysisContextResponse>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.Null(body.Lufe);
        Assert.Null(body.Fiscal);
        Assert.Null(body.Casfog);
        Assert.Null(body.Nosis);
        Assert.Null(body.Bcra);
    }

    /// <summary>
    /// The partial case, which is the norm: LUFE, Fiscal and BCRA were stored, CASFOG and NOSIS were
    /// not. The three that exist are parsed and the two that do not stay null.
    /// </summary>
    [Fact]
    public async Task GetContext_WithOnlySomeSnapshots_ReturnsThoseAndNullsTheRest()
    {
        var resultId = Guid.NewGuid();
        var repo = new StubAnalysisResultRepository
        {
            ContextToReturn = Snap(resultId, LufeJson, FiscalJson, null, null, BcraJson)
        };
        var sut = BuildSut(repo);

        var response = await sut.GetContextAsync(resultId, CancellationToken.None);

        var body = Assert.IsType<AnalysisContextResponse>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.NotNull(body.Lufe);
        Assert.NotNull(body.Fiscal);
        Assert.NotNull(body.Bcra);
        Assert.Null(body.Casfog);
        Assert.Null(body.Nosis);
    }

    // ── Failure modes ─────────────────────────────────────────────────────────

    /// <summary>
    /// A result id that does not resolve in this tenant is a 404 with ProblemDetails — the repository
    /// returned null, meaning no such row, which is a different thing from a row with no snapshots.
    /// </summary>
    [Fact]
    public async Task GetContext_UnknownId_Returns404()
    {
        var repo = new StubAnalysisResultRepository { ContextToReturn = null };
        var sut = BuildSut(repo);

        var response = await sut.GetContextAsync(Guid.NewGuid(), CancellationToken.None);

        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(response).Value);
        Assert.Equal(404, problem.Status);
        Assert.Equal("No se encontró el resultado del análisis.", problem.Detail);
    }

    /// <summary>
    /// Fault isolation. These blobs are stored once and never re-validated, so one that no longer
    /// parses must leave its own field null and the other four untouched — not empty the whole
    /// Contexto section.
    /// </summary>
    [Fact]
    public async Task GetContext_WithOneUnparseableSnapshot_NullsOnlyThatField()
    {
        var resultId = Guid.NewGuid();
        var repo = new StubAnalysisResultRepository
        {
            ContextToReturn = Snap(resultId, "{ this is not json", FiscalJson, CasfogJson, NosisJson, BcraJson)
        };
        var sut = BuildSut(repo);

        var response = await sut.GetContextAsync(resultId, CancellationToken.None);

        var body = Assert.IsType<AnalysisContextResponse>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.Null(body.Lufe);        // the broken one
        Assert.NotNull(body.Fiscal);   // the other four survive
        Assert.NotNull(body.Casfog);
        Assert.NotNull(body.Nosis);
        Assert.NotNull(body.Bcra);
    }

    /// <summary>
    /// The same isolation in the other direction, and the one that matters most now that BCRA is the
    /// heaviest field here: a corrupt BCRA blob must cost the BCRA tab and nothing else. It is also
    /// the case the detail no longer shares — the detail gets its two Score fields from SQL Server,
    /// so a blob too broken to deserialise here does not blank the top of the page.
    /// </summary>
    [Fact]
    public async Task GetContext_WithUnparseableBcra_NullsOnlyBcra()
    {
        var resultId = Guid.NewGuid();
        var repo = new StubAnalysisResultRepository
        {
            ContextToReturn = Snap(resultId, LufeJson, FiscalJson, CasfogJson, NosisJson, "{ not json at all")
        };
        var sut = BuildSut(repo);

        var response = await sut.GetContextAsync(resultId, CancellationToken.None);

        var body = Assert.IsType<AnalysisContextResponse>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.Null(body.Bcra);        // the broken one
        Assert.NotNull(body.Lufe);     // the other four survive
        Assert.NotNull(body.Fiscal);
        Assert.NotNull(body.Casfog);
        Assert.NotNull(body.Nosis);
    }
}
