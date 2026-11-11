// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-27

using System.Security.Claims;
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
/// Covers GET /api/analyses/{id}/bcra/cheques — the per-cheque rows that
/// <see cref="AnalysisController.GetContextAsync"/> stopped shipping.
///
/// <para>
/// Like /context this is a frozen point-in-time copy, so there is no refresh counterpart and nothing
/// here could consult BCRA even in principle: the repository read is the whole endpoint. What IS
/// load-bearing is the 404-vs-empty split. A missing result is a 404; a result that exists but froze
/// no snapshot — or froze one BCRA reported no cheques for, or one too old to carry the field, or
/// one that no longer parses — is a 200 carrying an empty array. Collapsing those would make "this
/// analysis has no cheques" indistinguishable from "wrong id", and the first is the common case.
/// </para>
///
/// <para>
/// The other thing under test is that the read stays narrow. This endpoint exists so a cheque table
/// stops costing the four frozen snapshots beside it, so the wider reads on the repository fail with
/// a named exception rather than quietly succeeding.
/// </para>
///
/// Hand-rolled private sealed stubs (no mock library). Only the repository this path reads is
/// stubbed; the rest of the ctor dependencies are passed as null! (the constructor performs plain
/// assignment).
/// </summary>
public sealed class AnalysisControllerBcraChequesTests
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
        public AnalysisBcraProjection? BcraToReturn { get; set; }

        public string? LastTenantId { get; private set; }
        public Guid?   LastResultId { get; private set; }

        public Task<AnalysisBcraProjection?> GetBcraSnapshotByIdAsync(
            Guid resultId, string tenantId, CancellationToken ct)
        {
            LastResultId = resultId;
            LastTenantId = tenantId;
            return Task.FromResult(BcraToReturn);
        }

        /// <summary>
        /// The full-row read — fourteen nvarchar(max) columns. This endpoint exists precisely so one
        /// table stops paying for them, so a call here is a regression and fails with a named
        /// exception.
        /// </summary>
        public Task<AnalysisResult?> GetByIdAsync(Guid resultId, string tenantId, CancellationToken ct)
            => throw new InvalidOperationException(
                "GET /api/analyses/{id}/bcra/cheques must read via GetBcraSnapshotByIdAsync.");

        /// <summary>
        /// The context projection — five blobs, LufeDataJson among them at 26KB on a representative
        /// row. Serving the cheque table through it would undo the point of splitting it out, so it
        /// fails loudly too.
        /// </summary>
        public Task<AnalysisContextProjection?> GetContextSnapshotByIdAsync(
            Guid resultId, string tenantId, CancellationToken ct)
            => throw new InvalidOperationException(
                "GET /api/analyses/{id}/bcra/cheques must not read the four frozen snapshots.");

        /// <summary>
        /// The detail projection — still seven nvarchar(max) columns, and it does not carry
        /// BcraDataJson at all, only two scalars extracted from it.
        /// </summary>
        public Task<AnalysisResultDetailProjection?> GetDetailByIdAsync(
            Guid resultId, string tenantId, CancellationToken ct)
            => throw new InvalidOperationException(
                "GET /api/analyses/{id}/bcra/cheques must not read the analysis detail projection.");

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

    /// <summary>
    /// A BCRA document with two cheque rows and the aggregates beside them, matching what
    /// <c>BcraService.MapCheques</c> writes. Two is enough to prove order and completeness; on a real
    /// row this array runs to thousands, which is why it moved here.
    /// </summary>
    private const string BcraJson = """
        {"deuda":{"identificacion":30712345671,"situacionMaxima":3,"montoTotal":48500.75,
        "entidadesInformantes":["BANCO NACION"],"estado":0,"fetchedAt":"2026-07-15T10:00:00Z"},
        "macro":{"inflacionMensual":2.5,"tasaReferencia":40.0,"fetchedAt":"2026-07-15T10:00:00Z"},
        "cambiario":{"tipoCambioOficial":950.0,"fetchedAt":"2026-07-15T10:00:00Z"},
        "cheques":{"identificacion":30712345671,"totalRechazados":2,"totalImpagos":1,
        "montoTotalRechazado":85000.0,"montoTotalImpago":35000.0,
        "causales":[{"causal":"SIN FONDOS SUFICIENTES","cantidad":2,"monto":85000.0,"cantidadImpagos":1}],
        "detalle":[{"causal":"SIN FONDOS SUFICIENTES","entidad":11,"nroCheque":10002,
        "fechaRechazo":"2026-06-11T00:00:00Z","monto":35000.0,"fechaPago":null,
        "ctaPersonal":false,"enRevision":false,"procesoJud":false,"pagado":false},
        {"causal":"SIN FONDOS SUFICIENTES","entidad":11,"nroCheque":10001,
        "fechaRechazo":"2026-05-02T00:00:00Z","monto":50000.0,"fechaPago":"2026-05-20T00:00:00Z",
        "ctaPersonal":false,"enRevision":false,"procesoJud":false,"pagado":true}],
        "estado":0,"fetchedAt":"2026-07-15T10:00:00Z"}}
        """;

    /// <summary>A snapshot whose cheques block exists but reports nothing — the NoChequesReported case.</summary>
    private const string BcraNoChequesJson = """
        {"deuda":{"identificacion":30712345671,"estado":0,"fetchedAt":"2026-07-15T10:00:00Z"},
        "cheques":{"identificacion":30712345671,"totalRechazados":0,"totalImpagos":0,
        "montoTotalRechazado":0.0,"montoTotalImpago":0.0,"causales":[],"detalle":[],
        "estado":1,"fetchedAt":"2026-07-15T10:00:00Z"}}
        """;

    /// <summary>A document with no cheques block at all, and one whose detalle is explicitly null —
    /// the shape #149 documents as "legacy stored data".</summary>
    private const string BcraNoChequesBlockJson  = """{"deuda":{"estado":0,"fetchedAt":"2026-07-15T10:00:00Z"}}""";
    private const string BcraNullDetalleJson     = """{"cheques":{"totalRechazados":4,"detalle":null,"estado":0,"fetchedAt":"2026-07-15T10:00:00Z"}}""";

    /// <summary>
    /// A snapshot with <paramref name="rows"/> cheques, one 1.000-peso impago per month in
    /// consecutive months, written <c>fechaRechazo</c> DESCENDING exactly as BcraService stores
    /// them — so the most recent row is first and carries the highest <c>nroCheque</c>.
    ///
    /// <para>
    /// Used to exceed <see cref="AnalysisBcraChequesResponse.RecientesCount"/>, which the two-row
    /// fixture cannot do. One cheque per month makes the expected evolution trivial to state:
    /// <c>rows</c> months, and a final live debt of 1.000 × <c>rows</c>.
    /// </para>
    /// </summary>
    private static string BuildBcraJsonWithRows(int rows)
    {
        var start = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // k is the row's position in the descending output: k=0 is the newest.
        var detalle = Enumerable.Range(0, rows).Select(k =>
        {
            var fecha = start.AddMonths(rows - 1 - k).ToString("yyyy-MM-dd'T'00:00:00'Z'");
            return $$"""
                {"causal":"SIN FONDOS SUFICIENTES","entidad":11,"nroCheque":{{rows - k}},
                "fechaRechazo":"{{fecha}}","monto":1000.0,"fechaPago":null,
                "ctaPersonal":false,"enRevision":false,"procesoJud":false,"pagado":false}
                """;
        });

        // $$$ / {{{ }}}: the JSON closes on "}}", which a $$ literal would read as an interpolation
        // delimiter rather than content.
        return $$$"""
            {"cheques":{"identificacion":30712345671,"totalRechazados":{{{rows}}},"totalImpagos":{{{rows}}},
            "montoTotalRechazado":{{{1000 * rows}}}.0,"montoTotalImpago":{{{1000 * rows}}}.0,"causales":[],
            "detalle":[{{{string.Join(",", detalle)}}}],
            "estado":0,"fetchedAt":"2026-07-15T10:00:00Z"}}
            """;
    }

    private static AnalysisBcraProjection Snap(Guid resultId, string? bcra) => new()
    {
        ResultId     = resultId,
        BcraDataJson = bcra
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

    // ── The rows come back — but only when asked for ──────────────────────────

    /// <summary>
    /// The whole array, in the order it was stored, with every field on each row intact — under
    /// <c>full=true</c>. This is the other half of the /context change: what came off that payload
    /// has to still be reachable, or the cheque table simply lost its data.
    /// </summary>
    [Fact]
    public async Task GetBcraCheques_WithFull_ReturnsEveryStoredRow()
    {
        var resultId = Guid.NewGuid();
        var repo = new StubAnalysisResultRepository { BcraToReturn = Snap(resultId, BcraJson) };
        var sut = BuildSut(repo);

        var response = await sut.GetBcraChequesAsync(resultId, full: true, CancellationToken.None);

        var body = Assert.IsType<AnalysisBcraChequesResponse>(Assert.IsType<OkObjectResult>(response).Value);

        Assert.NotNull(body.Cheques);
        Assert.Equal(2, body.Cheques.Count);

        // Stored order is preserved — fechaRechazo descending, as BcraService writes it.
        Assert.Equal(10002, body.Cheques[0].NroCheque);
        Assert.Equal(10001, body.Cheques[1].NroCheque);

        // Every field on a row survives, including the null that means "still impago".
        var impago = body.Cheques[0];
        Assert.Equal("SIN FONDOS SUFICIENTES", impago.Causal);
        Assert.Equal(11, impago.Entidad);
        Assert.Equal(35000.0m, impago.Monto);
        Assert.Null(impago.FechaPago);
        Assert.False(impago.Pagado);

        var pagado = body.Cheques[1];
        Assert.Equal(50000.0m, pagado.Monto);
        Assert.NotNull(pagado.FechaPago);
        Assert.True(pagado.Pagado);
    }

    /// <summary>
    /// full=true is ADDITIVE: it attaches the array without taking the summary away, so one response
    /// shape serves both callers and the "ver todos" path does not have to refetch what it already
    /// had. Same evolution figures as the default response asserts below.
    /// </summary>
    [Fact]
    public async Task GetBcraCheques_WithFull_StillCarriesTheSummaryBesideTheArray()
    {
        var resultId = Guid.NewGuid();
        var repo = new StubAnalysisResultRepository { BcraToReturn = Snap(resultId, BcraJson) };

        var response = await BuildSut(repo).GetBcraChequesAsync(resultId, full: true, CancellationToken.None);
        var body = Assert.IsType<AnalysisBcraChequesResponse>(Assert.IsType<OkObjectResult>(response).Value);

        Assert.NotNull(body.Cheques);
        Assert.Equal(2, body.Total);
        Assert.Equal(2, body.Recientes.Count);
        Assert.Equal([202605, 202606], body.Evolucion.Meses.Select(m => m.Mes));
    }

    // ── The default response is the summary ───────────────────────────────────

    /// <summary>
    /// The change this endpoint exists for now: by default the array does NOT ship. `cheques` is
    /// null — "not requested" — which is deliberately distinguishable from an empty array, because
    /// an empty array would claim the analysis has no cheques while `total` says it has two.
    /// </summary>
    [Fact]
    public async Task GetBcraCheques_ByDefault_OmitsTheArrayAndCarriesTheSummary()
    {
        var resultId = Guid.NewGuid();
        var repo = new StubAnalysisResultRepository { BcraToReturn = Snap(resultId, BcraJson) };

        var response = await BuildSut(repo).GetBcraChequesAsync(resultId, full: false, CancellationToken.None);
        var body = Assert.IsType<AnalysisBcraChequesResponse>(Assert.IsType<OkObjectResult>(response).Value);

        // The array is absent, and the count that replaces it is the TRUE one.
        Assert.Null(body.Cheques);
        Assert.Equal(2, body.Total);

        // The rows that do ship are the head of the stored descending order.
        Assert.Equal(2, body.Recientes.Count);
        Assert.Equal(10002, body.Recientes[0].NroCheque);
        Assert.Equal(10001, body.Recientes[1].NroCheque);

        // The series computed off the two fixture rows: 50.000 rejected in may 2026 and paid that
        // same month, then 35.000 rejected in june 2026 and still impago.
        Assert.Equal(
        [
            (202605, 1, 50000.0m, 50000.0m, 0m),
            (202606, 1, 35000.0m, 0m,       35000.0m)
        ], body.Evolucion.Meses.Select(m =>
            (m.Mes, m.CantidadRechazados, m.MontoRechazado, m.MontoRecuperado, m.SaldoNeto)));

        Assert.Equal(50000.0m, body.Evolucion.TotalRecuperado);
        Assert.Equal(35000.0m, body.Evolucion.SaldoNetoFinal);
        Assert.Equal(0, body.Evolucion.SinFechaRechazo.Cantidad);

        // Bounds come off fechaRechazo across every stored row, not just the ones in `recientes`.
        Assert.Equal(new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc),  body.DateBounds.Min);
        Assert.Equal(new DateTime(2026, 6, 11, 0, 0, 0, DateTimeKind.Utc), body.DateBounds.Max);
    }

    /// <summary>
    /// The cap is what makes the default response bounded. On a snapshot with more rows than
    /// <see cref="AnalysisBcraChequesResponse.RecientesCount"/>, `recientes` stops at the cap while
    /// `total` still reports the real count — so a caller holding only the head knows it is a head.
    /// The evolution is folded over ALL of them regardless, which is the whole saving.
    /// </summary>
    [Fact]
    public async Task GetBcraCheques_WithMoreRowsThanTheCap_TruncatesRecientesButNotTotalOrEvolucion()
    {
        const int stored = AnalysisBcraChequesResponse.RecientesCount + 17;

        var resultId = Guid.NewGuid();
        var repo = new StubAnalysisResultRepository { BcraToReturn = Snap(resultId, BuildBcraJsonWithRows(stored)) };

        var response = await BuildSut(repo).GetBcraChequesAsync(resultId, full: false, CancellationToken.None);
        var body = Assert.IsType<AnalysisBcraChequesResponse>(Assert.IsType<OkObjectResult>(response).Value);

        Assert.Equal(stored, body.Total);
        Assert.Equal(AnalysisBcraChequesResponse.RecientesCount, body.Recientes.Count);

        // The head of the stored order, not an arbitrary slice.
        Assert.Equal(stored, body.Recientes[0].NroCheque);

        // Every stored row reached the fold: the generator puts one 1.000-peso impago cheque in each
        // of `stored` consecutive months, so the final live debt is all of them.
        Assert.Equal(stored, body.Evolucion.Meses.Count);
        Assert.Equal(1000m * stored, body.Evolucion.SaldoNetoFinal);
    }

    /// <summary>
    /// The tenant the accessor resolved is the one the query ran under, and the id came through
    /// unchanged. Isolation on this endpoint is entirely the repository's tenant predicate, so the
    /// controller's job is to hand it the right tenant and never to second-guess the result.
    /// </summary>
    [Fact]
    public async Task GetBcraCheques_PassesTheResolvedTenantAndIdToTheQuery()
    {
        var resultId = Guid.NewGuid();
        var repo = new StubAnalysisResultRepository { BcraToReturn = Snap(resultId, BcraJson) };
        var sut = BuildSut(repo);

        await sut.GetBcraChequesAsync(resultId, full: false, CancellationToken.None);

        Assert.Equal(Tenant,   repo.LastTenantId);
        Assert.Equal(resultId, repo.LastResultId);
    }

    /// <summary>
    /// The endpoint reads ONE column. Touching the full row, the five-blob context projection or the
    /// detail projection would give back exactly what splitting this out was meant to save — all
    /// three throw a named exception on the stub, so this passing means none was called.
    ///
    /// <para>
    /// Asserted on BOTH modes: computing the evolution server-side must not have quietly acquired a
    /// second read to get at something the one projected column does not carry.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetBcraCheques_NeverReadsTheFullRowTheContextOrTheDetail(bool full)
    {
        var resultId = Guid.NewGuid();
        var repo = new StubAnalysisResultRepository { BcraToReturn = Snap(resultId, BcraJson) };

        var response = await BuildSut(repo).GetBcraChequesAsync(resultId, full, CancellationToken.None);

        Assert.IsType<OkObjectResult>(response);
    }

    // ── Empty is a 200, not a 404 ─────────────────────────────────────────────

    /// <summary>
    /// Five different ways to have no rows, all of them ordinary and none of them an error: the
    /// analysis froze no BCRA snapshot at all (no CUIT, or every BCRA call failed), BCRA reported no
    /// cheques for the CUIT, the stored document predates the <c>detalle</c> field, or the blob no
    /// longer parses. The caller cannot tell them apart and does not need to — it renders an empty
    /// table for all of them. A 404 here would say "wrong id", which is a different and wrong claim.
    ///
    /// <para>
    /// The summary must be WELL-FORMED rather than short-circuited, so the response shape does not
    /// depend on whether the snapshot had data: an empty series, null bounds, no recientes, total 0.
    /// A caller that reads <c>evolucion.meses</c> must never have to null-check it.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(null)]                          // froze no snapshot
    [InlineData(BcraNoChequesJson)]             // detalle is []
    [InlineData(BcraNoChequesBlockJson)]        // no cheques block at all
    [InlineData(BcraNullDetalleJson)]           // legacy stored data: detalle is null
    [InlineData("{ this is not json")]          // unparseable — degrade, do not throw
    public async Task GetBcraCheques_WithNoRows_Returns200AndAWellFormedEmptySummary(string? bcraJson)
    {
        var resultId = Guid.NewGuid();
        var repo = new StubAnalysisResultRepository { BcraToReturn = Snap(resultId, bcraJson) };
        var sut = BuildSut(repo);

        var response = await sut.GetBcraChequesAsync(resultId, full: false, CancellationToken.None);

        var body = Assert.IsType<AnalysisBcraChequesResponse>(Assert.IsType<OkObjectResult>(response).Value);

        Assert.NotNull(body.Recientes);
        Assert.Empty(body.Recientes);
        Assert.Equal(0, body.Total);

        Assert.NotNull(body.Evolucion);
        Assert.Empty(body.Evolucion.Meses);
        Assert.Equal(0m, body.Evolucion.TotalRecuperado);
        Assert.Equal(0m, body.Evolucion.SaldoNetoFinal);
        Assert.Equal(0,  body.Evolucion.SinFechaRechazo.Cantidad);
        Assert.Equal(0,  body.Evolucion.SinFechaPago.Cantidad);

        Assert.NotNull(body.DateBounds);
        Assert.Null(body.DateBounds.Min);
        Assert.Null(body.DateBounds.Max);

        // Still "not requested", not "none" — the flag governs this key, never the data.
        Assert.Null(body.Cheques);
    }

    /// <summary>
    /// The same no-rows snapshot under <c>full=true</c>: now the array IS requested, so it arrives —
    /// empty, which here truthfully means the analysis has no cheques. This is the one case where an
    /// empty <c>cheques</c> is the honest answer, and it is reachable only by asking.
    /// </summary>
    [Fact]
    public async Task GetBcraCheques_WithNoRowsAndFull_ReturnsAnEmptyArrayRatherThanNull()
    {
        var resultId = Guid.NewGuid();
        var repo = new StubAnalysisResultRepository { BcraToReturn = Snap(resultId, BcraNoChequesJson) };

        var response = await BuildSut(repo).GetBcraChequesAsync(resultId, full: true, CancellationToken.None);
        var body = Assert.IsType<AnalysisBcraChequesResponse>(Assert.IsType<OkObjectResult>(response).Value);

        Assert.NotNull(body.Cheques);
        Assert.Empty(body.Cheques);
        Assert.Equal(0, body.Total);
    }

    // ── Failure modes ─────────────────────────────────────────────────────────

    /// <summary>
    /// A result id that does not resolve in this tenant is a 404 with ProblemDetails — the repository
    /// returned null, meaning no such row, which is a different thing from a row with no cheques.
    /// Same wording as the sibling reads on this controller.
    /// </summary>
    [Fact]
    public async Task GetBcraCheques_UnknownId_Returns404()
    {
        var repo = new StubAnalysisResultRepository { BcraToReturn = null };
        var sut = BuildSut(repo);

        var response = await sut.GetBcraChequesAsync(Guid.NewGuid(), full: false, CancellationToken.None);

        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(response).Value);
        Assert.Equal(404, problem.Status);
        Assert.Equal("No se encontró el resultado del análisis.", problem.Detail);
    }

    /// <summary>
    /// An analysis of another tenant is indistinguishable from one that does not exist — the
    /// tenant-scoped query returns null for both, and so must this endpoint. A different response
    /// would confirm that the id exists somewhere in the system, and these rows carry a CUIT.
    /// </summary>
    [Fact]
    public async Task GetBcraCheques_AnalysisInAnotherTenant_Returns404()
    {
        // The tenant-isolated query found nothing; the controller cannot tell why, and must not try.
        var repo = new StubAnalysisResultRepository { BcraToReturn = null };

        var response = await BuildSut(repo).GetBcraChequesAsync(Guid.NewGuid(), full: false, CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(response).StatusCode);
        Assert.Equal(Tenant, repo.LastTenantId);
    }

    /// <summary>
    /// With no tenant resolved the endpoint fails before it queries anything. Reaching the repository
    /// without a tenant is how a cross-tenant read would start.
    /// </summary>
    [Fact]
    public async Task GetBcraCheques_WithoutTenantContext_Returns500AndNeverQueries()
    {
        var repo = new StubAnalysisResultRepository { BcraToReturn = Snap(Guid.NewGuid(), BcraJson) };

        var response = await BuildSut(repo, withTenant: false)
            .GetBcraChequesAsync(Guid.NewGuid(), full: false, CancellationToken.None);

        Assert.Equal(500, Assert.IsType<ObjectResult>(response).StatusCode);
        Assert.Null(repo.LastTenantId);
        Assert.Null(repo.LastResultId);
    }
}
