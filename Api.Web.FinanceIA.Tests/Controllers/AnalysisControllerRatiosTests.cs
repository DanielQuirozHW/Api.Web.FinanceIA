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
/// Covers GET /api/analyses/{id}/ratios — the two fields the PyME detail's "Ratios calculados por
/// FinancIA" panel was fetching the entire analysis detail to read.
///
/// <para>
/// The contract test here is <see cref="GetRatios_ReturnsRatiosJsonAndScoreBreakdownVerbatim"/>. The
/// endpoint's whole justification is that the Portal swaps the URL and changes NOTHING about its
/// parsing, so it is not enough that the two fields are present — they must serialise byte-for-byte
/// as GET /api/analyses/{id} serialises them. That is why the assertion re-serialises
/// <c>ScoreBreakdown</c> and compares raw JSON rather than poking at properties: the value is a
/// <c>JsonElement</c> whose keys must survive untouched by the camelCase policy, and a typed DTO
/// would have re-cased them and dropped the ones it did not declare.
/// </para>
///
/// <para>
/// The other load-bearing behaviour is the 404-vs-null split — a missing result is a 404, while a
/// result that exists but was never scored is a 200 carrying nulls. Collapsing those would make
/// "this analysis predates the scoring engine" indistinguishable from "wrong id".
/// </para>
///
/// Hand-rolled private sealed stubs (no mock library). Only the repository this path reads is stubbed;
/// the rest of the ctor dependencies are passed as null! (the constructor performs plain assignment).
/// </summary>
public sealed class AnalysisControllerRatiosTests
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
        public AnalysisRatiosProjection? RatiosToReturn { get; set; }

        public Task<AnalysisRatiosProjection?> GetRatiosByIdAsync(
            Guid resultId, string tenantId, CancellationToken ct)
            => Task.FromResult(RatiosToReturn);

        /// <summary>
        /// The full-row read. This endpoint exists precisely so the ratios panel stops paying for the
        /// whole row, so a call here is a regression and fails with a named exception.
        /// </summary>
        public Task<AnalysisResult?> GetByIdAsync(Guid resultId, string tenantId, CancellationToken ct)
            => throw new InvalidOperationException(
                "GET /api/analyses/{id}/ratios must read via GetRatiosByIdAsync.");

        /// <summary>
        /// The detail projection — still nine nvarchar(max) columns wide. Reading the ratios panel
        /// through it is the exact defect this endpoint removes, so it fails loudly too.
        /// </summary>
        public Task<AnalysisResultDetailProjection?> GetDetailByIdAsync(
            Guid resultId, string tenantId, CancellationToken ct)
            => throw new InvalidOperationException(
                "GET /api/analyses/{id}/ratios must not read the analysis detail projection.");

        public Task<AnalysisContextProjection?> GetContextSnapshotByIdAsync(
            Guid resultId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<AnalysisBcraProjection?> GetBcraSnapshotByIdAsync(
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

    // Compact, no whitespace, so the round-trip comparison below is an exact string equality.
    // Deliberately mixes camelCase keys, a nested object, a decimal and an array — every shape the
    // stored breakdown blob actually contains — so a re-casing or key-dropping regression shows.
    private const string BreakdownJson =
        """{"scoreRatios":68.5,"scoreBcra":72,"breakdown":{"liquidez":{"score":72,"status":"Saludable"},"endeudamiento":{"score":41,"status":"Critico"}},"criticalRatios":["endeudamiento"],"appliedFloor":null,"sectorProfile":"industria"}""";

    private const string RatiosJson =
        """{"liquidez_corriente":1.42,"endeudamiento":2.87,"roe":0.13}""";

    private static AnalysisRatiosProjection Proj(Guid resultId, string? ratios, string? breakdown) => new()
    {
        ResultId           = resultId,
        RatiosJson         = ratios,
        ScoreBreakdownJson = breakdown
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

    // ── The contract ──────────────────────────────────────────────────────────

    /// <summary>
    /// THE contract test. <c>ratiosJson</c> is echoed as the exact stored string — the Portal parses
    /// it itself — and <c>scoreBreakdown</c> re-serialises to the stored blob VERBATIM: same keys,
    /// same casing, same nesting, same numbers, including keys no C# type declares. This is what
    /// makes the Portal's <c>scoreBreakdown?.breakdown</c> lookup keep working after it swaps the
    /// URL, and it is why the DTO's property is <c>object?</c> rather than a typed model.
    /// </summary>
    [Fact]
    public async Task GetRatios_ReturnsRatiosJsonAndScoreBreakdownVerbatim()
    {
        var resultId = Guid.NewGuid();
        var repo = new StubAnalysisResultRepository
        {
            RatiosToReturn = Proj(resultId, RatiosJson, BreakdownJson)
        };
        var sut = BuildSut(repo);

        var response = await sut.GetRatiosAsync(resultId, CancellationToken.None);

        var body = Assert.IsType<AnalysisRatiosResponse>(Assert.IsType<OkObjectResult>(response).Value);

        // Echoed, never parsed on this side.
        Assert.Equal(RatiosJson, body.RatiosJson);

        // Round-trips with every key intact — the camelCase policy must not touch a JsonElement.
        Assert.NotNull(body.ScoreBreakdown);
        Assert.Equal(BreakdownJson, JsonSerializer.Serialize(body.ScoreBreakdown));
    }

    /// <summary>
    /// An analysis that exists but was never scored is a 200 carrying two nulls — NOT a 404. This is
    /// the ordinary case for analyses predating the scoring engine.
    /// </summary>
    [Fact]
    public async Task GetRatios_WhenAnalysisHasNeither_Returns200WithNulls()
    {
        var resultId = Guid.NewGuid();
        var repo = new StubAnalysisResultRepository
        {
            RatiosToReturn = Proj(resultId, null, null)
        };
        var sut = BuildSut(repo);

        var response = await sut.GetRatiosAsync(resultId, CancellationToken.None);

        var body = Assert.IsType<AnalysisRatiosResponse>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.Null(body.RatiosJson);
        Assert.Null(body.ScoreBreakdown);
    }

    /// <summary>
    /// The partial case: ratios were computed but the row predates the scoring engine, so there is no
    /// breakdown. The one that exists comes back and the other stays null.
    /// </summary>
    [Fact]
    public async Task GetRatios_WithRatiosButNoBreakdown_ReturnsRatiosAndNullBreakdown()
    {
        var resultId = Guid.NewGuid();
        var repo = new StubAnalysisResultRepository
        {
            RatiosToReturn = Proj(resultId, RatiosJson, null)
        };
        var sut = BuildSut(repo);

        var response = await sut.GetRatiosAsync(resultId, CancellationToken.None);

        var body = Assert.IsType<AnalysisRatiosResponse>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.Equal(RatiosJson, body.RatiosJson);
        Assert.Null(body.ScoreBreakdown);
    }

    // ── Failure modes ─────────────────────────────────────────────────────────

    /// <summary>
    /// A result id that does not resolve in this tenant is a 404 with ProblemDetails — the repository
    /// returned null, meaning no such row, which is a different thing from a row with no ratios.
    /// </summary>
    [Fact]
    public async Task GetRatios_UnknownId_Returns404()
    {
        var repo = new StubAnalysisResultRepository { RatiosToReturn = null };
        var sut = BuildSut(repo);

        var response = await sut.GetRatiosAsync(Guid.NewGuid(), CancellationToken.None);

        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(response).Value);
        Assert.Equal(404, problem.Status);
        Assert.Equal("No se encontró el resultado del análisis.", problem.Detail);
    }

    /// <summary>
    /// Fault isolation, inherited from the detail's own helper: an unparseable breakdown blob leaves
    /// that field null and must not take the ratios down with it. Same degradation the detail
    /// response already has, since both go through TryDeserializeBreakdown.
    /// </summary>
    [Fact]
    public async Task GetRatios_WithUnparseableBreakdown_NullsOnlyThatField()
    {
        var resultId = Guid.NewGuid();
        var repo = new StubAnalysisResultRepository
        {
            RatiosToReturn = Proj(resultId, RatiosJson, """{"breakdown":""")
        };
        var sut = BuildSut(repo);

        var response = await sut.GetRatiosAsync(resultId, CancellationToken.None);

        var body = Assert.IsType<AnalysisRatiosResponse>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.Equal(RatiosJson, body.RatiosJson);
        Assert.Null(body.ScoreBreakdown);
    }
}
