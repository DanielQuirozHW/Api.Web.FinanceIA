// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-08

using System.Security.Claims;
using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Tests.Servicios.ScoreReliability;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Covers the analysis DETAIL endpoint <see cref="AnalysisController.GetById"/>: the provenance
/// projection, the read-time reconciliation, and the PROJECTED read.
///
/// <para>
/// The provenance half: the cupo(s) an analysis originated are surfaced under
/// <see cref="AnalysisResultResponse.OriginatedCreditLines"/> — always a (possibly empty) array,
/// never null, and regardless of cupo status (including terminal 'Superseded', since this is
/// provenance, not a liveness check).
/// </para>
///
/// <para>
/// The projection half: this endpoint reads <c>GetDetailByIdAsync</c>, not the full-row
/// <c>GetByIdAsync</c>, so the four frozen Contexto snapshots are never selected and come back null
/// — while everything else, BCRA included, is unchanged. The stub's <c>GetByIdAsync</c> throws, so a
/// regression back to the full-row read fails loudly rather than merely being slow.
/// </para>
///
/// Hand-rolled private sealed stubs (no mock library). Only the repositories the GetById path reads
/// are stubbed; the rest of the ~18 ctor dependencies are passed as null! (the constructor performs
/// plain assignment). Argument order mirrors the ctor signature.
/// </summary>
public sealed class AnalysisControllerGetByIdTests
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
        public AnalysisResultDetailProjection? DetailToReturn { get; set; }

        public Task<AnalysisResultDetailProjection?> GetDetailByIdAsync(
            Guid resultId, string tenantId, CancellationToken ct)
            => Task.FromResult(DetailToReturn);

        /// <summary>
        /// The FULL-row read. GetById stopped using it: a full row drags all fourteen
        /// <c>nvarchar(max)</c> columns off disk, four of them the frozen Contexto snapshots this
        /// endpoint no longer ships. Any call here is a regression back to that, so it fails loudly
        /// with a named exception rather than a bare NotImplementedException.
        /// </summary>
        public Task<AnalysisResult?> GetByIdAsync(Guid resultId, string tenantId, CancellationToken ct)
            => throw new InvalidOperationException(
                "GET /api/analyses/{id} must read via GetDetailByIdAsync, not the full-row GetByIdAsync.");

        public Task<AnalysisContextProjection?> GetContextSnapshotByIdAsync(
            Guid resultId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<AnalysisBcraProjection?> GetBcraSnapshotByIdAsync(
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
        public Task<IReadOnlyDictionary<Guid, Guid>> GetResultIdsByAnalysisIdsAsync(IEnumerable<Guid> analysisIds, string tenantId, CancellationToken ct)
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

    private sealed class StubAnalysisRepository : IAnalysisRepository
    {
        public Task<BalanceBaseFiguresProjection?> GetBalanceBaseFiguresAsync(Guid analysisId, string tenantId, CancellationToken ct)
            => Task.FromResult<BalanceBaseFiguresProjection?>(null);

        public Task<IReadOnlyDictionary<Guid, EntityLatestScoreProjection>> GetLatestScoreByEntityIdsAsync(
            IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct) => throw new NotImplementedException();

        // The quorum-crossing write (DDL 067). Throws because no test in this suite confirms a
        // pre-approval, so an unexpected call must fail loudly rather than pass silently.
        public Task<bool> TryConfirmFromPreApprovedAsync(
            Guid analysisId, string tenantId, Guid reviewedBy, DateTime reviewedAt,
            decimal? decisionAmount, string? decisionCurrency, string? confirmedOperationsJson,
            string? reviewNotes, CancellationToken ct)
            => throw new NotImplementedException();
        public Analysis? AnalysisToReturn { get; set; }

        public Task<Analysis?> GetByIdAsync(Guid analysisId, CancellationToken ct)
            => Task.FromResult(AnalysisToReturn);

        public Task<Analysis> CreateAsync(Analysis analysis, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<Analysis>> GetByIdsAsync(IEnumerable<Guid> analysisIds, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Analysis?> GetLatestByDocumentIdAsync(Guid documentId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Analysis> UpdateAsync(Analysis analysis, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, string>> GetLatestStatusByEntityIdsAsync(IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<int> CountByTenantInPeriodAsync(string tenantId, DateTime periodStartUtc, DateTime periodEndUtc, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class StubEntityRepository : IEntityRepository
    {
        public Entity? EntityToReturn { get; set; }

        public Task<Entity?> GetByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => Task.FromResult(EntityToReturn);

        public Task<IReadOnlyList<Entity>> GetAllByTenantAsync(string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<bool> ExistsByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Entity?> GetByCuitAsync(string cuit, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Entity> CreateAsync(Entity entity, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Entity> UpdateAsync(Entity entity, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<(IReadOnlyList<EntityListProjection> Items, int TotalCount)> GetListPageByTenantAsync(
            string tenantId, int skip, int take, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, EntityNameProjection>> GetNameLookupByIdsAsync(
            IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntityDetailProjection?> GetDetailByIdAsync(
            Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntitySnapshotProjection?> GetBcraSnapshotAsync(
            Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntitySnapshotProjection?> GetCasfogSnapshotAsync(
            Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<EntitySnapshotProjection?> GetLufeSnapshotAsync(
            Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<EntitySnapshotProjection?> GetNosisSnapshotAsync(
            Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<EntityNameProjection?> GetNameByIdAsync(
            Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubUserRepository : IUserRepository
    {
        public User? UserToReturn { get; set; }

        /// <summary>Users the batched actor-name lookup can resolve, keyed by UserId.</summary>
        public Dictionary<Guid, User> Directory { get; } = new();

        /// <summary>How many times the batched lookup ran. GetById must need exactly one.</summary>
        public int GetByIdsCallCount { get; private set; }

        public Task<User?> GetByFirebaseUidAsync(string firebaseUid, CancellationToken ct)
            => Task.FromResult(UserToReturn);
        public Task<User?> GetByUserIdAsync(Guid userId, CancellationToken ct)
            => Task.FromResult(UserToReturn);

        public Task<IReadOnlyList<User>> GetByIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct)
        {
            GetByIdsCallCount++;
            IReadOnlyList<User> found = userIds
                .Where(Directory.ContainsKey)
                .Select(id => Directory[id])
                .ToList();
            return Task.FromResult(found);
        }

        public Task<User?> GetByEmailAsync(string email, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> ExistsByFirebaseUidAsync(string firebaseUid, CancellationToken ct) => throw new NotImplementedException();
        public Task<User> CreateAsync(User user, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateLastLoginAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateOnboardingFlagsAsync(Guid userId, string? flagsJson, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<User>> GetActiveByTenantAsync(string tenantId, CancellationToken ct) => throw new NotImplementedException();
        // Team assignment (DDL 063) — unused by this suite.
        public Task UpdateTeamAsync(Guid userId, Guid? teamId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> ClearTeamAsync(Guid teamId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class StubCreditLineRepository : ICreditLineRepository
    {
        // Drives the provenance projection on GetById.
        public IReadOnlyList<CreditLine> OriginatedToReturn { get; set; } = Array.Empty<CreditLine>();

        public Task<IReadOnlyList<CreditLine>> GetByOriginAnalysisAsync(Guid analysisId, string tenantId, CancellationToken ct)
            => Task.FromResult(OriginatedToReturn);

        public Task<IReadOnlyList<CreditLine>> GetNonTerminalByEntityAsync(Guid entityId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<CreditLine?> GetActiveByEntityCurrencyAsync(Guid entityId, string currency, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<CreditLine?> GetByIdAsync(Guid creditLineId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task CreateAsync(CreditLine line, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<CreditLine?> RaiseAsync(
            Guid creditLineId, string tenantId, decimal newAmount, Guid analysisId,
            DateTime? expectedLastRaisedAt, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlySet<Guid>> GetEntityIdsWithNonTerminalLineAsync(IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, MainCreditLineProjection>> GetMainActiveByEntityIdsAsync(
            IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<Guid>> GetIdsByOriginAnalysisAsync(Guid analysisId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<Guid>> GetActiveIdsByOriginAnalysisAsync(Guid analysisId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<CreditLine?> GetBlockingByEntityCurrencyAsync(Guid entityId, string currency, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<bool> SetStatusAsync(Guid creditLineId, string tenantId, string expectedStatus, string newStatus, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<CupoAlertSource>> GetActiveForCupoAlertsAsync(string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<PymeReviewSource>> GetLatestApprovedAnalysesOfActiveEntitiesAsync(string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static TenantContext BuildTenant() => new()
    {
        TenantId               = Tenant,
        FirebaseProjectId      = "proj",
        FirebaseCredentialJson = "{}",
        RecaptchaSecretKey     = "secret"
    };

    private static Analysis BuildAnalysis(Guid analysisId, Guid entityId) => new()
    {
        AnalysisId  = analysisId,
        TenantId    = Tenant,
        EntityId    = entityId,
        Status      = "Approved",
        RequestedBy = Guid.NewGuid()
    };

    /// <summary>
    /// The PROJECTED detail row — what <c>GetDetailByIdAsync</c> returns. There is deliberately no
    /// full <see cref="AnalysisResult"/> fixture here any more: the four frozen snapshot columns do
    /// not exist on this type, so no test on this path can accidentally depend on them.
    /// </summary>
    private static AnalysisResultDetailProjection BuildDetail(Guid analysisId) => new()
    {
        ResultId   = Guid.NewGuid(),
        AnalysisId = analysisId,
        TenantId   = Tenant
    };

    private static CreditLine Cupo(string status, string currency, decimal amount) => new()
    {
        CreditLineId = Guid.NewGuid(),
        TenantId     = Tenant,
        EntityId     = Guid.NewGuid(),
        Amount       = amount,
        Currency     = currency,
        Status       = status
    };

    private static AnalysisController BuildSut(
        StubAnalysisResultRepository resultRepo,
        StubAnalysisRepository       analysisRepo,
        StubCreditLineRepository     creditLineRepo,
        StubUserRepository?          users = null,
        Api.Web.FinanceIA.Tests.PersonAnalysis.StubPersonAnalysisRepository? personAnalyses = null)
    {
        var entityRepo = new StubEntityRepository { EntityToReturn = new Entity { EntityId = Guid.NewGuid(), TenantId = Tenant } };
        var userRepo   = users ?? new StubUserRepository { UserToReturn = new User { UserId = Guid.NewGuid(), DisplayName = "Someone" } };

        var sut = new AnalysisController(
            new StubTenantContextAccessor { TenantContext = BuildTenant() }, // tenantContextAccessor
            null!,            // documentRepository
            resultRepo,       // analysisResultRepository
            analysisRepo,     // analysisRepository
            null!,            // claudeService
            new CashFlowReconciliationService(),   // cashFlowReconciliationService
            new CashFlowNarrativeService(),        // cashFlowNarrativeService
            new AlertRatioReconciliationService(), // alertRatioReconciliationService — real, stateless (see PR #125); existing tests never populate BalanceJson so this is a no-op for them
            entityRepo,       // entityRepository
            userRepo,         // userRepository
            null!,            // chatRepository
            creditLineRepo,   // creditLineRepository
            null!,            // operationRepository
            null!,            // auditService
            null!,            // apiCallLogService
            null!,            // excelCashFlowParser
            null!,            // billingOptions
            null!,            // tenantBillingConfigService
            null!,            // userApprovalLimitRepository
            null!,            // decisionNotifier
            // A REAL stub, not null!: GetById now reads an approval tally on a pre-approved
            // analysis. The read is fault-isolated, so null! would still pass — by exercising the
            // failure path, which would make a genuinely broken tally look identical to a working one.
            new StubAnalysisApprovalQuorumService(),
            new Api.Web.FinanceIA.Tests.Legajo.FakeLegajoDocumentRepository(),
            null!,            // analysisPipeline
            null!,            // analysisJobQueue
            null!,            // analysisRunRegistry
            null!,            // analysisRunStateRepository
            personAnalyses ?? new Api.Web.FinanceIA.Tests.PersonAnalysis.StubPersonAnalysisRepository(),
            NullLogger<AnalysisController>.Instance);

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("uid", FirebaseUid) }, "Test"))
        };
        sut.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return sut;
    }

    // ── Tests ───────────────────────────────────────────────────────────────
    /// <summary>
    /// The detail carries the originating cupo(s) — and includes a terminal 'Superseded' one, since
    /// this is provenance, not a liveness check.
    /// </summary>
    [Fact]
    public async Task GetById_WithOriginatedCupos_ReturnsThemIncludingSuperseded()
    {
        var analysisId = Guid.NewGuid();
        var entityId   = Guid.NewGuid();
        var detail     = BuildDetail(analysisId);
        var resultRepo   = new StubAnalysisResultRepository { DetailToReturn = detail };
        var analysisRepo = new StubAnalysisRepository { AnalysisToReturn = BuildAnalysis(analysisId, entityId) };
        var creditLineRepo = new StubCreditLineRepository
        {
            OriginatedToReturn = new[]
            {
                Cupo("Active", "ARS", 20000000.00m),
                Cupo("Superseded", "USD", 5000.00m),
            }
        };
        var sut = BuildSut(resultRepo, analysisRepo, creditLineRepo);

        var response = await sut.GetById(detail.ResultId, CancellationToken.None);

        var body = Assert.IsType<AnalysisResultResponse>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.NotNull(body.OriginatedCreditLines);
        Assert.Equal(2, body.OriginatedCreditLines!.Count);
        Assert.Contains(body.OriginatedCreditLines, c => c.Status == "Superseded");
        var ars = Assert.Single(body.OriginatedCreditLines, c => c.Currency == "ARS");
        Assert.Equal("Active", ars.Status);
        Assert.Equal(20000000.00m, ars.Amount);
    }

    /// <summary>
    /// A persona humana analysis (DDL 072): the detail says so and carries the frozen evaluation of the
    /// solicitante, read from AnalysisParticipants — never recomputed. A balance analysis carries none.
    /// </summary>
    [Fact]
    public async Task GetById_PersonaAnalysis_CarriesItsKindAndTheFrozenEvaluation()
    {
        var analysisId = Guid.NewGuid();
        var detail     = BuildDetail(analysisId);
        var analysis   = BuildAnalysis(analysisId, Guid.NewGuid());
        analysis.Status       = "PendingReview";
        analysis.AnalysisKind = AnalysisKinds.PersonaHumana;
        analysis.DocumentId   = null;
        detail.ValidationStatus = PersonAnalysesController.StoredValidationStatus;

        var filters = new List<PersonFilterResult>
        {
            new() { Id = "bcra_cheques_impagos", Outcome = PersonFilterOutcomes.Rechazo, Evidence = "1 cheque", Source = "BCRA" }
        };
        var personAnalyses = new Api.Web.FinanceIA.Tests.PersonAnalysis.StubPersonAnalysisRepository();
        personAnalyses.Participants.Add(new AnalysisParticipant
        {
            ParticipantId        = Guid.NewGuid(),
            TenantId             = Tenant,
            AnalysisId           = analysisId,
            PersonLegajoId       = Guid.NewGuid(),
            Role                 = ParticipantRoles.Solicitante,
            AmountAtCharge       = 1_000m,
            Currency             = "ARS",
            FiltersJson          = Api.Web.FinanceIA.Servicios.PersonEvaluation.PersonEvaluationJson.Serialize(filters),
            IndicatorsJson       = "[]",
            CoverageJson         = "{}",
            DocumentVersionsJson = "{}",
            EvaluationVersion    = "0.1"
        });

        var sut = BuildSut(new StubAnalysisResultRepository { DetailToReturn = detail },
                           new StubAnalysisRepository { AnalysisToReturn = analysis },
                           new StubCreditLineRepository { OriginatedToReturn = Array.Empty<CreditLine>() },
                           personAnalyses: personAnalyses);

        var body = Assert.IsType<AnalysisResultResponse>(
            Assert.IsType<OkObjectResult>(await sut.GetById(detail.ResultId, CancellationToken.None)).Value);

        Assert.Equal(AnalysisKinds.PersonaHumana, body.AnalysisKind);
        Assert.Equal(Guid.Empty, body.DocumentId);
        // Score credibility 1b-2b: another engine, so no score-reliability verdict.
        Assert.Null(body.ScoreReliability);
        Assert.NotNull(body.PersonEvaluation);
        Assert.Equal(PersonSuggestions.RechazoSugerido, body.PersonEvaluation!.Suggestion);
        Assert.Equal("bcra_cheques_impagos", Assert.Single(body.PersonEvaluation.Filters).Id);
        // The row stores "Passed" (the constraint forces one of three values); the Portal read it as
        // "Estado de validación: Aprobado" plus an "Estado actualizado · Aprobado" event. Not applicable: empty.
        Assert.Equal(string.Empty, body.ValidationStatus);
        // The default coverage of 100 % rules out a sola firma; the Portal presets the form from these.
        Assert.False(body.SolaFirmaAllowed);
        Assert.Equal(100, body.MinCounterGuaranteeCoveragePct);
        Assert.False(body.PersonEvaluation.DecisionRules!.SolaFirmaAllowed);
        Assert.Equal(100, body.PersonEvaluation.DecisionRules.MinCounterGuaranteeCoveragePct);
    }

    /// <summary>
    /// Engine 0.6 retired pf_plazo_estimado_deuda_meses: an analysis frozen by 0.5 — its parameters with the plazo
    /// and none of the 0.6 members, its cuota indicator with the plazo input — still reads, whole, as it was frozen.
    /// </summary>
    [Fact]
    public async Task GetById_PersonaAnalysisFrozenBefore06_StillReadsAsItWasFrozen()
    {
        var analysisId = Guid.NewGuid();
        var detail     = BuildDetail(analysisId);
        var analysis   = BuildAnalysis(analysisId, Guid.NewGuid());
        analysis.Status       = "PendingReview";
        analysis.AnalysisKind = AnalysisKinds.PersonaHumana;
        analysis.DocumentId   = null;
        detail.ValidationStatus = PersonAnalysesController.StoredValidationStatus;

        var personAnalyses = new Api.Web.FinanceIA.Tests.PersonAnalysis.StubPersonAnalysisRepository();
        personAnalyses.Participants.Add(new AnalysisParticipant
        {
            ParticipantId        = Guid.NewGuid(),
            TenantId             = Tenant,
            AnalysisId           = analysisId,
            PersonLegajoId       = Guid.NewGuid(),
            Role                 = ParticipantRoles.Solicitante,
            AmountAtCharge       = 1_000m,
            Currency             = "ARS",
            FiltersJson          = "[]",
            IndicatorsJson       = Api.Web.FinanceIA.Servicios.PersonEvaluation.PersonEvaluationJson.Serialize(
                                       new[] { Api.Web.FinanceIA.Tests.PersonAnalysis.PersonEvaluationPre06Fixtures.CuotaIngreso() }),
            CoverageJson         = "{}",
            DocumentVersionsJson = Api.Web.FinanceIA.Tests.PersonAnalysis.PersonEvaluationPre06Fixtures.SourcesJson(),
            EvaluationVersion    = "0.5"
        });

        var sut = BuildSut(new StubAnalysisResultRepository { DetailToReturn = detail },
                           new StubAnalysisRepository { AnalysisToReturn = analysis },
                           new StubCreditLineRepository { OriginatedToReturn = Array.Empty<CreditLine>() },
                           personAnalyses: personAnalyses);

        var body = Assert.IsType<AnalysisResultResponse>(
            Assert.IsType<OkObjectResult>(await sut.GetById(detail.ResultId, CancellationToken.None)).Value);

        var evaluation = body.PersonEvaluation!;
        Assert.Equal("0.5", evaluation.EvaluationVersion);
        Assert.NotNull(evaluation.Sources);
        Assert.Equal(24, evaluation.Sources!.Parameters.PlazoEstimadoDeudaMeses);
        Assert.Equal(1.3m, evaluation.Sources.Parameters.DscrVerde);
        Assert.Null(evaluation.Sources.Parameters.CuotaEstimadaPctSaldo);
        var cuota = Assert.Single(evaluation.Indicators);
        Assert.Equal("24", cuota.Inputs["plazoEstimadoDeudaMeses"]);
        Assert.Contains("en 24 meses", cuota.Evidence);
    }

    /// <summary>
    /// M10 Phase 5a-2: the fiadores' frozen rows and their aggregate coverage — the value the shared reader
    /// computes, the same object on personEvaluation and on its decision rules — with the name read live.
    /// </summary>
    [Fact]
    public async Task GetById_PersonaAnalysis_CarriesItsFiadoresAndTheAggregateCoverage()
    {
        var analysisId = Guid.NewGuid();
        var detail     = BuildDetail(analysisId);
        var analysis   = BuildAnalysis(analysisId, Guid.NewGuid());
        analysis.AnalysisKind = AnalysisKinds.PersonaHumana;
        analysis.DocumentId   = null;

        var personAnalyses = new Api.Web.FinanceIA.Tests.PersonAnalysis.StubPersonAnalysisRepository();
        var solicitante = new AnalysisParticipant
        {
            ParticipantId = Guid.NewGuid(), TenantId = Tenant, AnalysisId = analysisId, PersonLegajoId = Guid.NewGuid(),
            Role = ParticipantRoles.Solicitante, AmountAtCharge = 1_000_000m, Currency = "ARS",
            FiltersJson = "[]", IndicatorsJson = "[]", CoverageJson = "{}", DocumentVersionsJson = "{}", EvaluationVersion = "0.5"
        };
        var fiador = Api.Web.FinanceIA.Tests.PersonAnalysis.PersonFiadorFixtures.FiadorRow(analysisId, Tenant, 1_000_000m, 1_200_000m);
        personAnalyses.Participants.Add(solicitante);
        personAnalyses.Participants.Add(fiador);
        personAnalyses.FiadorNames[fiador.FiadorId!.Value] = "FIADOR SINTETICO";
        personAnalyses.FiadorCuits[fiador.FiadorId!.Value] = "20111222333";

        var sut = BuildSut(new StubAnalysisResultRepository { DetailToReturn = detail },
                           new StubAnalysisRepository { AnalysisToReturn = analysis },
                           new StubCreditLineRepository { OriginatedToReturn = Array.Empty<CreditLine>() },
                           personAnalyses: personAnalyses);

        var body = Assert.IsType<AnalysisResultResponse>(
            Assert.IsType<OkObjectResult>(await sut.GetById(detail.ResultId, CancellationToken.None)).Value);

        var evaluation = body.PersonEvaluation!;
        var view = Assert.Single(evaluation.Fiadores!);
        Assert.Equal(fiador.FiadorId, view.FiadorId);
        Assert.Equal("FIADOR SINTETICO", view.Name);
        Assert.Equal("20111222333", view.Cuit);
        Assert.Equal(GuaranteeTypes.Fianza, view.GuaranteeType);
        Assert.Single(view.Coverage!.Assets);

        var expected = Api.Web.FinanceIA.Servicios.PersonEvaluation.PersonEvaluationJson
            .DecisionRules(new Dictionary<string, decimal>(), solicitante, [fiador]).CounterGuaranteeCoverage;
        var options = Api.Web.FinanceIA.Servicios.PersonEvaluation.PersonEvaluationJson.Options;
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expected, options),
                     System.Text.Json.JsonSerializer.Serialize(evaluation.CounterGuaranteeCoverage, options));
        Assert.Equal(0.6m, evaluation.CounterGuaranteeCoverage!.Ratio);
        Assert.Same(evaluation.CounterGuaranteeCoverage, evaluation.DecisionRules!.CounterGuaranteeCoverage);
        Assert.True(evaluation.DecisionRules.CounterGuaranteeBelowMin);
    }

    [Fact]
    public async Task GetById_PersonaAnalysis_WithoutASolicitante_StillCarriesTheSolaFirmaRule()
    {
        // PersonEvaluation is null without a solicitante, which is why the rule is also top-level.
        var analysisId = Guid.NewGuid();
        var detail     = BuildDetail(analysisId);
        var analysis   = BuildAnalysis(analysisId, Guid.NewGuid());
        analysis.AnalysisKind = AnalysisKinds.PersonaHumana;
        analysis.DocumentId   = null;
        var personAnalyses = new Api.Web.FinanceIA.Tests.PersonAnalysis.StubPersonAnalysisRepository();
        personAnalyses.Config["pf_contragarantia_cobertura_min_pct"] = 0m;

        var sut = BuildSut(new StubAnalysisResultRepository { DetailToReturn = detail },
                           new StubAnalysisRepository { AnalysisToReturn = analysis },
                           new StubCreditLineRepository { OriginatedToReturn = Array.Empty<CreditLine>() },
                           personAnalyses: personAnalyses);

        var body = Assert.IsType<AnalysisResultResponse>(
            Assert.IsType<OkObjectResult>(await sut.GetById(detail.ResultId, CancellationToken.None)).Value);

        Assert.Null(body.PersonEvaluation);
        Assert.True(body.SolaFirmaAllowed);
        Assert.Equal(0, body.MinCounterGuaranteeCoveragePct);
    }

    [Fact]
    public async Task GetById_BalanceAnalysis_HasNoPersonEvaluation()
    {
        var analysisId = Guid.NewGuid();
        var detail     = BuildDetail(analysisId);
        detail.ValidationStatus = "Passed";
        var personAnalyses = new Api.Web.FinanceIA.Tests.PersonAnalysis.StubPersonAnalysisRepository();
        var sut = BuildSut(new StubAnalysisResultRepository { DetailToReturn = detail },
                           new StubAnalysisRepository { AnalysisToReturn = BuildAnalysis(analysisId, Guid.NewGuid()) },
                           new StubCreditLineRepository { OriginatedToReturn = Array.Empty<CreditLine>() },
                           personAnalyses: personAnalyses);

        var body = Assert.IsType<AnalysisResultResponse>(
            Assert.IsType<OkObjectResult>(await sut.GetById(detail.ResultId, CancellationToken.None)).Value);

        Assert.Equal(AnalysisKinds.Balance, body.AnalysisKind);
        Assert.Null(body.PersonEvaluation);
        Assert.Equal("Passed", body.ValidationStatus);   // the balance check still reaches the Portal
        Assert.Null(body.SolaFirmaAllowed);
        Assert.Null(body.MinCounterGuaranteeCoveragePct);
    }

    /// <summary>
    /// When the analysis originated no cupo, the field is an EMPTY array — never null, never omitted —
    /// so the client has one case to handle instead of three.
    /// </summary>
    [Fact]
    public async Task GetById_WhenNoOriginatedCupo_ReturnsEmptyArray()
    {
        var analysisId = Guid.NewGuid();
        var entityId   = Guid.NewGuid();
        var detail     = BuildDetail(analysisId);
        var resultRepo   = new StubAnalysisResultRepository { DetailToReturn = detail };
        var analysisRepo = new StubAnalysisRepository { AnalysisToReturn = BuildAnalysis(analysisId, entityId) };
        var creditLineRepo = new StubCreditLineRepository { OriginatedToReturn = Array.Empty<CreditLine>() };
        var sut = BuildSut(resultRepo, analysisRepo, creditLineRepo);

        var response = await sut.GetById(detail.ResultId, CancellationToken.None);

        var body = Assert.IsType<AnalysisResultResponse>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.NotNull(body.OriginatedCreditLines);
        Assert.Empty(body.OriginatedCreditLines!);
    }

    /// <summary>
    /// GetById's read-time reconciliation (mirrors TriggerAsync's write-time repair, see PR #125
    /// and the verifiable-percentages v2.15 follow-up) also applies to anomalies: a wrong-
    /// denominator percentage inside a persisted anomaly string is corrected in the returned
    /// response, the same fix already covered for RiskAlerts/ExecutiveSummary.
    /// </summary>
    [Fact]
    public async Task GetById_AnomalyWithWrongPercentage_ReturnsCorrectedText()
    {
        var analysisId = Guid.NewGuid();
        var entityId    = Guid.NewGuid();
        var detail      = BuildDetail(analysisId);
        detail.BalanceJson = """{"assets": 12239430766.71}""";
        detail.Anomalies   =
            """["Los bienes de cambio ascienden a $6.236.534.182,74, representando el 55,4% del activo total de $12.239.430.766,71."]""";

        var resultRepo     = new StubAnalysisResultRepository { DetailToReturn = detail };
        var analysisRepo   = new StubAnalysisRepository { AnalysisToReturn = BuildAnalysis(analysisId, entityId) };
        var creditLineRepo = new StubCreditLineRepository();
        var sut = BuildSut(resultRepo, analysisRepo, creditLineRepo);

        var response = await sut.GetById(detail.ResultId, CancellationToken.None);

        var body = Assert.IsType<AnalysisResultResponse>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.Contains("51,0% del activo total", body.Anomalies);
        Assert.DoesNotContain("55,4%", body.Anomalies);
    }

    // ── The projected read ────────────────────────────────────────────────────

    /// <summary>
    /// All FIVE stored source payloads come back NULL — the four frozen Contexto snapshots and, as
    /// of the BCRA split, <c>bcraData</c> too. Not because parsing failed: the query never selected
    /// those columns. The keys stay present in the payload because nulls are serialised. This is the
    /// half of the change the Portal absorbs by fetching GET {id}/context when the user opens that
    /// section.
    ///
    /// <para>
    /// <c>bcraSummary</c> is asserted NON-null in the same breath, and that pairing is the point. The
    /// Score section at the top of the page renders BCRA on load, so nulling <c>bcraData</c> without
    /// putting the two fields it reads somewhere would empty the top of the detail. This is what
    /// catches that.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GetById_LeavesTheFiveSourcePayloadsNull_ButShipsTheBcraSummary()
    {
        var analysisId = Guid.NewGuid();
        var entityId   = Guid.NewGuid();
        var detail     = BuildDetail(analysisId);
        detail.BcraDeudaEstadoRaw          = "0";
        detail.BcraDeudaSituacionMaximaRaw = "2";

        var resultRepo     = new StubAnalysisResultRepository { DetailToReturn = detail };
        var analysisRepo   = new StubAnalysisRepository { AnalysisToReturn = BuildAnalysis(analysisId, entityId) };
        var creditLineRepo = new StubCreditLineRepository();
        var sut = BuildSut(resultRepo, analysisRepo, creditLineRepo);

        var response = await sut.GetById(detail.ResultId, CancellationToken.None);

        var body = Assert.IsType<AnalysisResultResponse>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.Null(body.Lufe);
        Assert.Null(body.Fiscal);
        Assert.Null(body.Casfog);
        Assert.Null(body.Nosis);
        Assert.Null(body.BcraData);

        Assert.NotNull(body.BcraSummary);
        Assert.NotNull(body.BcraSummary.Deuda);
        Assert.Equal(BcraDeudaConsultaEstado.DebtFound, body.BcraSummary.Deuda.Estado);
        Assert.Equal(2, body.BcraSummary.Deuda.SituacionMaxima);
    }

    /// <summary>
    /// The wire shape, pinned. Two things have to hold for the Portal's
    /// <c>bcraSummary?.deuda ?? bcraData?.deuda</c> fallback to work, and neither is visible from the
    /// object graph:
    /// <list type="bullet">
    /// <item>the keys are camelCase — <c>deuda</c>, <c>estado</c>, <c>situacionMaxima</c> — matching
    /// what <c>bcraData.deuda</c> emits, which gets there by explicit [JsonPropertyName] while this
    /// type gets there by the pipeline's naming policy;</item>
    /// <item><c>estado</c> is a NUMBER, not <c>"LookupFailed"</c>. No JsonStringEnumConverter is
    /// registered in this project, so typing the field as the enum is a naming convenience rather
    /// than a wire change — but adding a converter later would silently break every client reading
    /// it, and this is what would fail.</item>
    /// </list>
    /// Serialised with the framework's own web defaults, since that is what the MVC pipeline applies
    /// on the way out; asserting on the object instead would prove neither point.
    /// </summary>
    [Fact]
    public async Task GetById_BcraSummarySerialisesWithCamelCaseKeysAndANumericEstado()
    {
        var analysisId = Guid.NewGuid();
        var detail     = BuildDetail(analysisId);
        detail.BcraDeudaEstadoRaw          = "2";      // LookupFailed
        detail.BcraDeudaSituacionMaximaRaw = null;

        var resultRepo   = new StubAnalysisResultRepository { DetailToReturn = detail };
        var analysisRepo = new StubAnalysisRepository { AnalysisToReturn = BuildAnalysis(analysisId, Guid.NewGuid()) };
        var sut = BuildSut(resultRepo, analysisRepo, new StubCreditLineRepository());

        var response = await sut.GetById(detail.ResultId, CancellationToken.None);

        var body = Assert.IsType<AnalysisResultResponse>(Assert.IsType<OkObjectResult>(response).Value);
        var json = JsonSerializer.Serialize(body.BcraSummary, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal("""{"deuda":{"estado":2,"situacionMaxima":null}}""", json);
    }

    /// <summary>
    /// A populated estado beside a null situación is the NORMAL shape for a CUIT with no reported
    /// debt — the stored field is <c>int?</c> and BcraService leaves it unset — so the summary must
    /// survive it rather than collapsing to null.
    /// </summary>
    [Fact]
    public async Task GetById_NoSituacionMaxima_StillShipsTheEstado()
    {
        var analysisId = Guid.NewGuid();
        var detail     = BuildDetail(analysisId);
        detail.BcraDeudaEstadoRaw          = "1";      // NoDebtReported
        detail.BcraDeudaSituacionMaximaRaw = null;

        var resultRepo   = new StubAnalysisResultRepository { DetailToReturn = detail };
        var analysisRepo = new StubAnalysisRepository { AnalysisToReturn = BuildAnalysis(analysisId, Guid.NewGuid()) };
        var sut = BuildSut(resultRepo, analysisRepo, new StubCreditLineRepository());

        var response = await sut.GetById(detail.ResultId, CancellationToken.None);

        var body = Assert.IsType<AnalysisResultResponse>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.NotNull(body.BcraSummary?.Deuda);
        Assert.Equal(BcraDeudaConsultaEstado.NoDebtReported, body.BcraSummary.Deuda.Estado);
        Assert.Null(body.BcraSummary.Deuda.SituacionMaxima);
    }

    /// <summary>
    /// No estado means no <c>deuda</c> node — no BCRA blob at all, or one that carries none. The
    /// whole summary is null, and specifically NOT an object whose estado defaulted to
    /// <c>DebtFound</c>, which would report a debt lookup that never happened.
    /// </summary>
    [Fact]
    public async Task GetById_NoBcraDeuda_BcraSummaryIsNull()
    {
        var analysisId = Guid.NewGuid();
        var detail     = BuildDetail(analysisId);
        detail.BcraDeudaEstadoRaw          = null;
        detail.BcraDeudaSituacionMaximaRaw = null;

        var resultRepo   = new StubAnalysisResultRepository { DetailToReturn = detail };
        var analysisRepo = new StubAnalysisRepository { AnalysisToReturn = BuildAnalysis(analysisId, Guid.NewGuid()) };
        var sut = BuildSut(resultRepo, analysisRepo, new StubCreditLineRepository());

        var response = await sut.GetById(detail.ResultId, CancellationToken.None);

        var body = Assert.IsType<AnalysisResultResponse>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.Null(body.BcraSummary);
        Assert.Null(body.BcraData);
    }

    /// <summary>
    /// The other half: everything the detail shipped before is still shipped. A shape pin over the
    /// fields the projection carries, so dropping a column from
    /// <c>AnalysisResultDetailProjection</c> — or forgetting to map one in
    /// <c>MapToDetailResponse</c> — fails here instead of silently emptying the page.
    /// </summary>
    [Fact]
    public async Task GetById_StillReturnsEverythingElse()
    {
        var analysisId = Guid.NewGuid();
        var entityId   = Guid.NewGuid();
        var detail     = BuildDetail(analysisId);
        detail.GeneratedAt        = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
        detail.RatiosJson         = """{"currentRatio": 1.8}""";
        detail.BalanceJson        = """{"assets": 1000}""";
        detail.ScoreBreakdownJson = """{"sectorProfile": "Manufactura"}""";
        detail.ExecutiveSummary   = "Resumen ejecutivo.";
        detail.ScoringNarrative   = "Narrativa de scoring.";
        detail.RiskAlerts         = """[{"code":"LIQ","description":"Liquidez ajustada."}]""";
        detail.CatalogueAlerts    = """[{"code":"CAT-1"}]""";
        detail.Anomalies          = """["Una anomalía."]""";
        detail.AiSuggestedScore   = 62.5m;
        detail.AiSuggestedLevel   = "Medium";
        detail.AiInputTokens      = 1234;
        detail.AiOutputTokens     = 567;
        detail.ValidationStatus   = "Valid";
        detail.EnrichmentStatus   = "Degraded";
        detail.EnrichmentNotes    = "BCRA no respondió.";

        var resultRepo     = new StubAnalysisResultRepository { DetailToReturn = detail };
        var analysisRepo   = new StubAnalysisRepository { AnalysisToReturn = BuildAnalysis(analysisId, entityId) };
        var creditLineRepo = new StubCreditLineRepository();
        var sut = BuildSut(resultRepo, analysisRepo, creditLineRepo);

        var response = await sut.GetById(detail.ResultId, CancellationToken.None);

        var body = Assert.IsType<AnalysisResultResponse>(Assert.IsType<OkObjectResult>(response).Value);

        Assert.Equal(detail.ResultId,          body.ResultId);
        Assert.Equal(analysisId,               body.AnalysisId);
        Assert.Equal(Tenant,                   body.TenantId);
        Assert.Equal(entityId,                 body.EntityId);
        Assert.Equal(detail.GeneratedAt,       body.GeneratedAt);
        Assert.Equal(detail.RatiosJson,        body.RatiosJson);
        Assert.Equal(detail.BalanceJson,       body.BalanceJson);
        Assert.Equal(detail.ExecutiveSummary,  body.ExecutiveSummary);
        Assert.Equal(detail.ScoringNarrative,  body.ScoringNarrative);
        Assert.Equal(detail.RiskAlerts,        body.RiskAlerts);
        Assert.Equal(detail.CatalogueAlerts,   body.CatalogueAlerts);
        Assert.Equal(detail.Anomalies,         body.Anomalies);
        Assert.Equal(62.5m,                    body.AiSuggestedScore);
        Assert.Equal("Medium",                 body.AiSuggestedLevel);
        Assert.Equal("Medium",                 body.HighestAlertLevel);
        Assert.Equal(1234,                     body.AiInputTokens);
        Assert.Equal(567,                      body.AiOutputTokens);
        Assert.Equal("Valid",                  body.ValidationStatus);
        Assert.Equal("Degraded",               body.EnrichmentStatus);
        Assert.Equal("BCRA no respondió.",     body.EnrichmentNotes);
        Assert.NotNull(body.ScoreBreakdown);

        // Set by ApplyDetailChannels from the response, so it inherits any read-time repair.
        Assert.Equal(body.RiskAlerts,          body.Observations);

        // Permanently null on the detail — superseded by AiSuggestedScore.
        Assert.Null(body.ConfidenceScore);
    }

    // ── extractionIntegrity (read from BalanceJson.integrity) ─────────────────

    private async Task<AnalysisResultResponse> GetWithBalanceJsonAsync(string? balanceJson)
    {
        var analysisId = Guid.NewGuid();
        var detail     = BuildDetail(analysisId);
        detail.BalanceJson = balanceJson;

        var sut = BuildSut(
            new StubAnalysisResultRepository { DetailToReturn = detail },
            new StubAnalysisRepository { AnalysisToReturn = BuildAnalysis(analysisId, Guid.NewGuid()) },
            new StubCreditLineRepository());

        var response = await sut.GetById(detail.ResultId, CancellationToken.None);
        return Assert.IsType<AnalysisResultResponse>(Assert.IsType<OkObjectResult>(response).Value);
    }

    [Fact]
    public async Task GetById_ExposesExtractionIntegrity_ForA218PlusResult()
    {
        var body = await GetWithBalanceJsonAsync("""
            {"assets": 1000, "integrity": {
              "passed": false,
              "checks": [
                {"name": "assets_components", "passed": true},
                {"name": "current_assets_items", "passed": false},
                {"name": "column_swap", "passed": false},
                {"name": "column_swap", "passed": false}],
              "skipped": [],
              "affectedRatios": ["liquidez_corriente", "prueba_acida"],
              "extraction": {"method": "image-oriented", "secondPassRan": true, "secondPassTriggers": ["integrity"],
                             "keptPass": "second", "keptReason": "fewer_structure_failures", "rotatedPages": 1}}}
            """);

        var integrity = Assert.IsType<ExtractionIntegrityResponse>(body.ExtractionIntegrity);
        Assert.False(integrity.Passed);
        Assert.Equal(["current_assets_items", "column_swap"], integrity.FailedChecks);
        Assert.Equal(["liquidez_corriente", "prueba_acida"], integrity.AffectedRatios);
        Assert.True(integrity.SecondPassRan);
        Assert.Equal("second", integrity.KeptPass);
    }

    [Fact]
    public async Task GetById_ExtractionIntegrityIsNull_ForAResultWithoutIntegrity()
    {
        var body = await GetWithBalanceJsonAsync("""{"assets": 1000}""");

        Assert.Null(body.ExtractionIntegrity);
    }

    [Fact]
    public async Task GetById_ExtractionIntegrityIsNull_ForAMalformedBalanceJson_AndTheReadStillSucceeds()
    {
        var body = await GetWithBalanceJsonAsync("{ not json");

        Assert.Null(body.ExtractionIntegrity);
        Assert.Equal("{ not json", body.BalanceJson);
    }

    // ── Actor names: one batched lookup, "Usuario" fallback ────────────────────

    [Fact]
    public async Task GetById_ResolvesAllThreeActorNames_InOneBatchedLookup()
    {
        // Three actors — requester, reviewer, pre-approver — must cost ONE query. This endpoint used
        // to issue a separate single-id read per actor, and adding the pre-approver would have made
        // that three round trips on every detail open.
        var analysisId = Guid.NewGuid();
        var entityId   = Guid.NewGuid();
        var analysis   = BuildAnalysis(analysisId, entityId);

        var requester   = new User { UserId = analysis.RequestedBy, DisplayName = "La Analista" };
        var reviewer    = new User { UserId = Guid.NewGuid(),       DisplayName = "El Risk Manager" };
        var preApprover = new User { UserId = Guid.NewGuid(),       DisplayName = "El Analista" };

        analysis.ReviewedBy          = reviewer.UserId;
        analysis.PreApprovedBy       = preApprover.UserId;
        analysis.PreApprovedAmount   = 900_000m;
        analysis.PreApprovedCurrency = "ARS";
        analysis.PreApprovedAt       = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        analysis.DecisionAmount      = 750_000m;
        analysis.DecisionCurrency    = "ARS";

        var users = new StubUserRepository { UserToReturn = requester };
        users.Directory[requester.UserId]   = requester;
        users.Directory[reviewer.UserId]    = reviewer;
        users.Directory[preApprover.UserId] = preApprover;

        var detail     = BuildDetail(analysisId);
        var resultRepo = new StubAnalysisResultRepository { DetailToReturn = detail };
        var sut = BuildSut(resultRepo,
                           new StubAnalysisRepository { AnalysisToReturn = analysis },
                           new StubCreditLineRepository(),
                           users);

        var response = await sut.GetById(detail.ResultId, CancellationToken.None);

        var ok   = Assert.IsType<OkObjectResult>(response);
        var body = Assert.IsType<AnalysisResultResponse>(ok.Value);

        Assert.Equal(1, users.GetByIdsCallCount);
        Assert.Equal("La Analista",     body.RequestedByName);
        Assert.Equal("El Risk Manager", body.ReviewedByName);
        Assert.Equal("El Analista",     body.PreApprovedByName);

        // Both halves of the two-signature record travel on the detail payload.
        Assert.Equal(preApprover.UserId.ToString(), body.PreApprovedBy);
        Assert.Equal(900_000m, body.PreApprovedAmount);
        Assert.Equal(750_000m, body.DecisionAmount);
        Assert.Equal("ARS",    body.DecisionCurrency);
    }

    [Fact]
    public async Task GetById_ExposesBothOperationRecords_SoTheConfirmerCanReadTheProposal()
    {
        // The detail view is where a RiskManager reads what the analyst proposed BEFORE confirming
        // it. Without this the proposal would only ever reach the client on the decision response —
        // i.e. after the decision it was meant to inform.
        var analysisId = Guid.NewGuid();
        var analysis   = BuildAnalysis(analysisId, Guid.NewGuid());

        analysis.PreApprovedBy          = Guid.NewGuid();
        analysis.PreApprovedAmount      = 900_000m;
        analysis.PreApprovedCurrency    = "ARS";
        analysis.ProposedOperationsJson = JsonSerializer.Serialize(new[]
        {
            new AnalysisDecisionOperation { Type = 5, Amount = 900_000m, Currency = "ARS", ASolaFirma = true }
        });
        // Still only pre-approved, so nothing is committed yet and the confirmed record is empty.
        Assert.Null(analysis.ConfirmedOperationsJson);

        var detail     = BuildDetail(analysisId);
        var resultRepo = new StubAnalysisResultRepository { DetailToReturn = detail };
        var sut = BuildSut(resultRepo,
                           new StubAnalysisRepository { AnalysisToReturn = analysis },
                           new StubCreditLineRepository(),
                           new StubUserRepository());

        var response = await sut.GetById(detail.ResultId, CancellationToken.None);

        var ok   = Assert.IsType<OkObjectResult>(response);
        var body = Assert.IsType<AnalysisResultResponse>(ok.Value);

        var proposed = Assert.Single(body.ProposedOperations!);
        Assert.Equal(5, proposed.Type);
        Assert.Equal(900_000m, proposed.Amount);
        Assert.True(proposed.ASolaFirma);
        Assert.Null(body.ConfirmedOperations);
    }

    [Fact]
    public async Task GetById_WithUnresolvableActor_FallsBackToUsuario()
    {
        // A deactivated or deleted account must not blank the field. "Usuario" is the same fallback
        // the chat conversation list and the credit-line payloads use.
        var analysisId = Guid.NewGuid();
        var analysis   = BuildAnalysis(analysisId, Guid.NewGuid());
        analysis.ReviewedBy = Guid.NewGuid();

        var users  = new StubUserRepository();   // empty directory: nothing resolves
        var detail = BuildDetail(analysisId);
        var sut = BuildSut(new StubAnalysisResultRepository { DetailToReturn = detail },
                           new StubAnalysisRepository { AnalysisToReturn = analysis },
                           new StubCreditLineRepository(),
                           users);

        var response = await sut.GetById(detail.ResultId, CancellationToken.None);

        var ok   = Assert.IsType<OkObjectResult>(response);
        var body = Assert.IsType<AnalysisResultResponse>(ok.Value);

        Assert.Equal("Usuario", body.RequestedByName);
        Assert.Equal("Usuario", body.ReviewedByName);
        // Nobody pre-approved this one, so the field stays null rather than becoming "Usuario":
        // an absent act is not an unresolvable someone.
        Assert.Null(body.PreApprovedByName);
        Assert.Null(body.PreApprovedBy);
    }

    /// <summary>
    /// Score credibility 1b-2a: the detail echoes the breakdown whole, so the new
    /// <c>reliabilityFacts</c> block and a per-ratio <c>measured</c> reach the Portal unchanged.
    /// </summary>
    [Fact]
    public async Task GetById_EchoesTheReliabilityFactsBlock()
    {
        var analysisId = Guid.NewGuid();
        var detail     = BuildDetail(analysisId);
        detail.ScoreBreakdownJson =
            """
            {"breakdown":{"roe":{"score":50,"status":"Atencion","excluded":false,"effectiveWeight":0.10,"measured":false}},
             "reliabilityFacts":{"version":1,"bcraSituacionUnreadable":false,"macroImputed":true,"sectorFallback":false,"configSource":"defaults","configHash":"0f1e2d3c4b5a6978","ratiosSource":"computed","fiscalYear":2024,"bcra":{"deudaEstado":"DebtFound","periodoConsultado":"202609","chequesEvaluated":true},"lufe":{"ventasEstado":"carried_forward","ventasUltimoPeriodo":"202608"}}}
            """;

        var sut = BuildSut(
            new StubAnalysisResultRepository { DetailToReturn = detail },
            new StubAnalysisRepository { AnalysisToReturn = BuildAnalysis(analysisId, Guid.NewGuid()) },
            new StubCreditLineRepository());

        var response = await sut.GetById(detail.ResultId, CancellationToken.None);

        var body = Assert.IsType<AnalysisResultResponse>(Assert.IsType<OkObjectResult>(response).Value);
        var breakdown = Assert.IsType<System.Text.Json.JsonElement>(body.ScoreBreakdown);
        var facts = breakdown.GetProperty("reliabilityFacts");
        Assert.Equal("carried_forward", facts.GetProperty("lufe").GetProperty("ventasEstado").GetString());
        Assert.False(breakdown.GetProperty("breakdown").GetProperty("roe").GetProperty("measured").GetBoolean());
    }

    // ── Score reliability (score credibility 1b-2b) ─────────────────────────

    /// <summary>
    /// The detail's half of the parity guarantee: the shared fixture row, read through GET by id, yields
    /// exactly the verdict the adapter gives it — the same one the list and the dashboard return. Its
    /// balance_stale depends on RescoredAt, so a detail that judged at GeneratedAt would fail here.
    /// </summary>
    [Fact]
    public async Task GetById_CarriesTheScoreReliabilityOfTheParityRow()
    {
        var analysisId = Guid.NewGuid();
        var detail     = BuildDetail(analysisId);
        detail.AiSuggestedScore   = ScoreReliabilityFixture.ParityScore;
        detail.EnrichmentStatus   = ScoreReliabilityFixture.ParityEnrichment;
        detail.GeneratedAt        = ScoreReliabilityFixture.ParityGeneratedAt;
        detail.RescoredAt         = ScoreReliabilityFixture.ParityRescoredAt;
        detail.ScoreBreakdownJson = ScoreReliabilityFixture.ParityBreakdownJson();
        var analysis = BuildAnalysis(analysisId, Guid.NewGuid());
        analysis.FiscalYear = ScoreReliabilityFixture.ParityFiscalYear;

        var sut = BuildSut(new StubAnalysisResultRepository { DetailToReturn = detail },
                           new StubAnalysisRepository { AnalysisToReturn = analysis },
                           new StubCreditLineRepository { OriginatedToReturn = Array.Empty<CreditLine>() });

        var body = Assert.IsType<AnalysisResultResponse>(
            Assert.IsType<OkObjectResult>(await sut.GetById(detail.ResultId, CancellationToken.None)).Value);

        Assert.NotNull(body.ScoreReliability);
        Assert.Equal(ScoreReliabilityFixture.Wire(ScoreReliabilityFixture.ParityExpected()),
                     ScoreReliabilityFixture.Wire(body.ScoreReliability));
        Assert.Equal(ScoreReliabilityFixture.ParityCodes, body.ScoreReliability!.Reasons.Select(r => r.Code));
    }

    /// <summary>A breakdown that does not parse never fails the detail: it reads as facts_unavailable.</summary>
    [Fact]
    public async Task GetById_WithAnUnreadableBreakdown_StillAnswers_WithFactsUnavailable()
    {
        var analysisId = Guid.NewGuid();
        var detail     = BuildDetail(analysisId);
        detail.AiSuggestedScore   = 40m;
        detail.EnrichmentStatus   = "Complete";
        detail.GeneratedAt        = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        detail.ScoreBreakdownJson = "{\"breakdown\": [";
        var analysis = BuildAnalysis(analysisId, Guid.NewGuid());
        analysis.FiscalYear = 2025;

        var sut = BuildSut(new StubAnalysisResultRepository { DetailToReturn = detail },
                           new StubAnalysisRepository { AnalysisToReturn = analysis },
                           new StubCreditLineRepository { OriginatedToReturn = Array.Empty<CreditLine>() });

        var body = Assert.IsType<AnalysisResultResponse>(
            Assert.IsType<OkObjectResult>(await sut.GetById(detail.ResultId, CancellationToken.None)).Value);

        Assert.Equal(ScoreReliabilityLevels.WithCaveats, body.ScoreReliability!.Level);
        Assert.Equal("facts_unavailable", Assert.Single(body.ScoreReliability.Reasons).Code);
    }

    // ── Decision requirements (score credibility 1b-5, review-gate-calm) ────────

    /// <summary>
    /// GET by id's decisionRequirements per level: only provisional and none ask an approval for the
    /// acknowledgement (with_caveats asks nothing), and only failed figures ask for a note — the figures-review
    /// note on an approval, a 20+ character reason on a rejection.
    /// </summary>
    [Theory]
    [InlineData("complete",         ScoreReliabilityLevels.Complete,    false, false)]
    [InlineData("with_caveats",     ScoreReliabilityLevels.WithCaveats, false, false)]
    [InlineData("provisional",      ScoreReliabilityLevels.Provisional, true,  false)]
    [InlineData("none",             ScoreReliabilityLevels.None,        true,  false)]
    [InlineData("integrity_failed", ScoreReliabilityLevels.Provisional, true,  true)]
    public async Task GetById_DecisionRequirements_FollowTheLevel(
        string row, string expectedLevel, bool acknowledgeCaveats, bool figuresFailed)
    {
        var analysisId = Guid.NewGuid();
        var detail     = BuildDetail(analysisId);
        detail.AiSuggestedScore   = row == "none" ? null : 42.5m;
        detail.EnrichmentStatus   = "Complete";
        detail.GeneratedAt        = ScoreReliabilityFixture.ScoreDate;
        detail.ScoreBreakdownJson = row switch
        {
            "provisional"      => ScoreReliabilityFixture.Json(   // integrity never verified: provisional, no figure failed
                                      facts: ScoreReliabilityFixture.CleanFacts() with
                                      {
                                          Integrity = ScoreReliabilityFixture.CleanFacts().Integrity! with { Verified = false },
                                      }),
            "with_caveats"     => ScoreReliabilityFixture.Json(
                                      facts: ScoreReliabilityFixture.CleanFacts() with { MacroImputed = true }),
            "integrity_failed" => ScoreReliabilityFixture.Json(
                                      ScoreReliabilityFixture.FlooredScoring("liquidez_corriente"),
                                      ScoreReliabilityFixture.CleanFacts() with
                                      {
                                          Integrity = ScoreReliabilityFixture.FailedIntegrity(["liquidez_corriente"]),
                                      }),
            _                  => ScoreReliabilityFixture.Json(facts: ScoreReliabilityFixture.CleanFacts()),
        };
        var analysis = BuildAnalysis(analysisId, Guid.NewGuid());
        analysis.FiscalYear = 2025;

        var sut = BuildSut(new StubAnalysisResultRepository { DetailToReturn = detail },
                           new StubAnalysisRepository { AnalysisToReturn = analysis },
                           new StubCreditLineRepository { OriginatedToReturn = Array.Empty<CreditLine>() });

        var body = Assert.IsType<AnalysisResultResponse>(
            Assert.IsType<OkObjectResult>(await sut.GetById(detail.ResultId, CancellationToken.None)).Value);

        Assert.Equal(expectedLevel, body.ScoreReliability!.Level);
        var requirements = body.DecisionRequirements!;
        Assert.Equal(acknowledgeCaveats, requirements.AcknowledgeCaveats);
        Assert.Equal(figuresFailed,      requirements.FiguresReviewNote);
        Assert.Equal(figuresFailed,      requirements.RejectionReasonRequired);

        // On the wire, camelCase, beside the two it joins.
        var wire = JsonSerializer.SerializeToElement(requirements);
        Assert.Equal(figuresFailed, wire.GetProperty("rejectionReasonRequired").GetBoolean());
        Assert.Equal(acknowledgeCaveats, wire.GetProperty("acknowledgeCaveats").GetBoolean());
    }
}
