// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-25

using System.Security.Claims;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Tests.Servicios.ScoreReliability;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Covers the narrow LIST projection on <see cref="AnalysisController.GetAllAsync"/>: GET
/// /api/analyses returns <see cref="AnalysisListItemResponse"/> rows carrying only what the table
/// renders, instead of the full <see cref="AnalysisResultResponse"/> it used to reuse (which shipped
/// balanceJson, ratiosJson, riskAlerts, anomalies, executiveSummary, scoringNarrative and five
/// deserialised enrichment snapshots on every row of every page).
///
/// The four scoring values are FLATTENED out of ScoreBreakdownJson, which is no longer echoed. The
/// absent-vs-measured-zero distinction on appliedFloor/bcraTrendDelta is load-bearing and has its
/// own pair of tests — the engine writes those as non-nullable numbers, so 0 is a real measurement
/// while a missing key means the analysis predates that scoring feature.
///
/// Hand-rolled private sealed stubs (no mock library). Only the repositories the list path reads are
/// stubbed; the rest of the ~22 ctor dependencies are passed as null! (the constructor performs
/// plain assignment). Argument order mirrors the ctor signature.
/// </summary>
public sealed class AnalysisControllerListProjectionTests
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
        /// <summary>
        /// Fixtures are full <see cref="AnalysisResult"/> rows, because that is what the table holds.
        /// <see cref="Page"/> then projects and pages them exactly as
        /// <c>AnalysisResultRepository.BuildListPage</c> does, so a fixture carrying garbage in every
        /// heavy blob still proves something here: those columns cannot reach the controller, because
        /// the projection is where they get dropped. That the real SQL never *reads* them is proved
        /// separately, against the emitted query, in AnalysisResultRepositoryListPageTests.
        /// </summary>
        public IReadOnlyList<AnalysisResult> RowsToReturn { get; set; } = Array.Empty<AnalysisResult>();

        public int? LastSkip { get; private set; }
        public int? LastTake { get; private set; }
        public Guid? LastEntityId { get; private set; }

        /// <summary>
        /// How often the list path fell back to a full-row read. Counted rather than thrown so a
        /// test can assert zero and get a readable failure if that ever regresses.
        /// </summary>
        public int FullRowCallCount { get; private set; }

        public Task<(IReadOnlyList<AnalysisListProjection> Items, int TotalCount)> GetListPageByTenantAsync(
            string tenantId, int skip, int take, CancellationToken ct)
            => Task.FromResult(Page(skip, take));

        public Task<(IReadOnlyList<AnalysisListProjection> Items, int TotalCount)> GetListPageByEntityAsync(
            Guid entityId, string tenantId, int skip, int take, CancellationToken ct)
        {
            LastEntityId = entityId;
            return Task.FromResult(Page(skip, take));
        }

        private (IReadOnlyList<AnalysisListProjection> Items, int TotalCount) Page(int skip, int take)
        {
            LastSkip = skip;
            LastTake = take;

            IReadOnlyList<AnalysisListProjection> items = RowsToReturn
                .Select(r => new AnalysisListProjection
                {
                    ResultId           = r.ResultId,
                    AnalysisId         = r.AnalysisId,
                    GeneratedAt        = r.GeneratedAt,
                    AiSuggestedScore   = r.AiSuggestedScore,
                    AiSuggestedLevel   = r.AiSuggestedLevel,
                    EnrichmentStatus   = r.EnrichmentStatus,
                    RescoredAt         = r.RescoredAt,
                    ScoreBreakdownJson = r.ScoreBreakdownJson
                })
                .OrderByDescending(p => p.GeneratedAt)
                .ThenBy(p => p.ResultId)
                .Skip(skip)
                .Take(take)
                .ToList();

            return (items, RowsToReturn.Count);
        }

        public Task<IReadOnlyList<AnalysisResult>> GetAllByTenantAsync(string tenantId, CancellationToken ct)
        {
            FullRowCallCount++;
            return Task.FromResult(RowsToReturn);
        }

        public Task<IReadOnlyList<AnalysisResult>> GetByEntityIdAsync(Guid entityId, string tenantId, CancellationToken ct)
        {
            FullRowCallCount++;
            return Task.FromResult(RowsToReturn);
        }

        public Task<AnalysisResult?> GetByIdAsync(Guid resultId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<AnalysisResult?> GetByDocumentIdAsync(Guid documentId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<AnalysisResult> CreateAsync(AnalysisResult result, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<AnalysisResult> UpdateAsync(AnalysisResult result, CancellationToken ct)
            => throw new NotImplementedException();
        // The DETAIL reads. The list endpoint must never reach either of them.
        public Task<AnalysisResultDetailProjection?> GetDetailByIdAsync(
            Guid resultId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<AnalysisContextProjection?> GetContextSnapshotByIdAsync(
            Guid resultId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<AnalysisBcraProjection?> GetBcraSnapshotByIdAsync(
            Guid resultId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<AnalysisRatiosProjection?> GetRatiosByIdAsync(
            Guid resultId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, Guid>> GetResultIdsByAnalysisIdsAsync(IEnumerable<Guid> analysisIds, string tenantId, CancellationToken ct)
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
        public IReadOnlyList<Analysis> AnalysesToReturn { get; set; } = Array.Empty<Analysis>();

        public Task<IReadOnlyList<Analysis>> GetByIdsAsync(IEnumerable<Guid> analysisIds, CancellationToken ct)
            => Task.FromResult(AnalysesToReturn);

        public Task<Analysis?> GetByIdAsync(Guid analysisId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Analysis> CreateAsync(Analysis analysis, CancellationToken ct)
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
        public IReadOnlyList<Entity> EntitiesToReturn { get; set; } = Array.Empty<Entity>();

        /// <summary>
        /// How often the list asked for the whole tenant. Must stay 0: resolving ≤pageSize names
        /// used to be done by loading every entity with all five of its nvarchar(max) snapshots.
        /// </summary>
        public int GetAllCallCount { get; private set; }

        public List<Guid> LastLookupIds { get; private set; } = [];

        public Task<IReadOnlyList<Entity>> GetAllByTenantAsync(string tenantId, CancellationToken ct)
        {
            GetAllCallCount++;
            return Task.FromResult(EntitiesToReturn);
        }

        public Task<IReadOnlyDictionary<Guid, EntityNameProjection>> GetNameLookupByIdsAsync(
            IEnumerable<Guid> entityIds, string tenantId, CancellationToken ct)
        {
            LastLookupIds = entityIds.ToList();
            var wanted = LastLookupIds.ToHashSet();

            IReadOnlyDictionary<Guid, EntityNameProjection> map = EntitiesToReturn
                .Where(e => wanted.Contains(e.EntityId))
                .ToDictionary(
                    e => e.EntityId,
                    e => new EntityNameProjection
                    {
                        EntityId     = e.EntityId,
                        BusinessName = e.BusinessName,
                        CUIT         = e.CUIT
                    });

            return Task.FromResult(map);
        }

        public Task<Entity?> GetByIdAsync(Guid entityId, string tenantId, CancellationToken ct)
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
        public IReadOnlyList<User> UsersToReturn { get; set; } = Array.Empty<User>();

        public Task<IReadOnlyList<User>> GetByIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct)
            => Task.FromResult(UsersToReturn);

        public Task<User?> GetByFirebaseUidAsync(string firebaseUid, CancellationToken ct) => throw new NotImplementedException();
        public Task<User?> GetByUserIdAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
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

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static TenantContext BuildTenant() => new()
    {
        TenantId               = Tenant,
        FirebaseProjectId      = "proj",
        FirebaseCredentialJson = "{}",
        RecaptchaSecretKey     = "secret"
    };

    private static AnalysisController BuildSut(
        StubAnalysisResultRepository resultRepo,
        StubAnalysisRepository       analysisRepo,
        StubEntityRepository         entityRepo,
        StubUserRepository           userRepo,
        bool                         withTenant = true)
    {
        var sut = new AnalysisController(
            new StubTenantContextAccessor { TenantContext = withTenant ? BuildTenant() : null }, // tenantContextAccessor
            null!,            // documentRepository
            resultRepo,       // analysisResultRepository
            analysisRepo,     // analysisRepository
            null!,            // claudeService
            null!,            // cashFlowReconciliationService — the list path never reconciles
            null!,            // cashFlowNarrativeService
            null!,            // alertRatioReconciliationService
            entityRepo,       // entityRepository
            userRepo,         // userRepository
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

    /// <summary>
    /// One analysis, fully wired: result + analysis + entity + the two users. Returns the SUT and
    /// the ids so a test can assert against them.
    /// </summary>
    private static (AnalysisController sut, AnalysisResult result, Guid entityId, Guid requestedBy, Guid reviewedBy)
        BuildOneRow(Action<AnalysisResult>? tweak = null, Action<Analysis>? tweakAnalysis = null)
    {
        var analysisId  = Guid.NewGuid();
        var entityId    = Guid.NewGuid();
        var requestedBy = Guid.NewGuid();
        var reviewedBy  = Guid.NewGuid();

        var result = new AnalysisResult
        {
            ResultId         = Guid.NewGuid(),
            AnalysisId       = analysisId,
            TenantId         = Tenant,
            AiSuggestedScore = 72.50m,
            AiSuggestedLevel = "High",
            GeneratedAt      = new DateTime(2026, 3, 4, 10, 0, 0, DateTimeKind.Utc)
        };
        tweak?.Invoke(result);

        var analysis = new Analysis
        {
            AnalysisId      = analysisId,
            TenantId        = Tenant,
            EntityId        = entityId,
            DocumentId      = Guid.NewGuid(),
            RequestedBy     = requestedBy,
            ReviewedBy      = reviewedBy,
            RequestedAmount = 15_000_000.00m,
            Status          = "Approved",
            FiscalYear      = 2025,
            FinalRiskScore  = 68.00m,
            FinalRiskLevel  = "Medium",
            DecisionAmount   = 12_500_000.00m,
            DecisionCurrency = "ARS"
        };
        tweakAnalysis?.Invoke(analysis);

        var sut = BuildSut(
            new StubAnalysisResultRepository { RowsToReturn = new[] { result } },
            new StubAnalysisRepository { AnalysesToReturn = new[] { analysis } },
            new StubEntityRepository
            {
                EntitiesToReturn = new[]
                {
                    new Entity
                    {
                        EntityId     = entityId,
                        TenantId     = Tenant,
                        CUIT         = "30-71234567-1",
                        BusinessName = "Metalúrgica del Sur S.A."
                    }
                }
            },
            new StubUserRepository
            {
                UsersToReturn = new[]
                {
                    new User { UserId = requestedBy, DisplayName = "Ana Gómez" },
                    new User { UserId = reviewedBy,  DisplayName = "Luis Paz"  }
                }
            });

        return (sut, result, entityId, requestedBy, reviewedBy);
    }

    private static async Task<AnalysisListResponse> ListAsync(AnalysisController sut)
    {
        var response = await sut.GetAllAsync(cancellationToken: CancellationToken.None);
        return Assert.IsType<AnalysisListResponse>(Assert.IsType<OkObjectResult>(response).Value);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every field the list view renders survives the projection. This is the regression guard for
    /// "we trimmed the DTO and quietly dropped a column the table needed".
    /// </summary>
    [Fact]
    public async Task GetAll_LightRow_CarriesEveryProjectedField()
    {
        var (sut, result, entityId, requestedBy, reviewedBy) = BuildOneRow();

        var body = await ListAsync(sut);

        var row = Assert.Single(body.Items);
        Assert.Equal(result.ResultId, row.ResultId);
        Assert.Equal(result.AnalysisId, row.AnalysisId);
        Assert.Equal(entityId, row.EntityId);
        Assert.Equal("Metalúrgica del Sur S.A.", row.EntityName);
        Assert.Equal("30-71234567-1", row.EntityCuit);
        Assert.Equal(2025, row.FiscalYear);
        Assert.Equal(15_000_000.00m, row.RequestedAmount);
        Assert.Equal("Approved", row.Status);
        Assert.Equal(new DateTime(2026, 3, 4, 10, 0, 0, DateTimeKind.Utc), row.GeneratedAt);
        Assert.Equal(72.50m, row.AiSuggestedScore);
        Assert.Equal("High", row.AiSuggestedLevel);
        Assert.Equal(68.00m, row.FinalRiskScore);
        Assert.Equal("Medium", row.FinalRiskLevel);
        Assert.Equal(requestedBy.ToString(), row.RequestedBy);
        Assert.Equal("Ana Gómez", row.RequestedByName);
        Assert.Equal(reviewedBy.ToString(), row.ReviewedBy);
        Assert.Equal("Luis Paz", row.ReviewedByName);
        Assert.Equal(12_500_000.00m, row.DecisionAmount);
        Assert.Equal("ARS", row.DecisionCurrency);
    }

    /// <summary>
    /// The decision rides the row so the Portal can offer only analyses whose approved amount covers
    /// a raise. Null when nothing was committed — undecided, rejected, or approved before DDL 058 —
    /// and emitted as null rather than dropped.
    /// </summary>
    [Fact]
    public async Task GetAll_UndecidedAnalysis_CarriesNullDecision()
    {
        var (sut, _, _, _, _) = BuildOneRow(tweakAnalysis: a =>
        {
            a.Status           = "PendingReview";
            a.DecisionAmount   = null;
            a.DecisionCurrency = null;
        });

        var body = await ListAsync(sut);

        var row = Assert.Single(body.Items);
        Assert.Null(row.DecisionAmount);
        Assert.Null(row.DecisionCurrency);

        var json = System.Text.Json.JsonSerializer.Serialize(
            row, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("\"decisionAmount\":null", json, StringComparison.Ordinal);
        Assert.Contains("\"decisionCurrency\":null", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// The wire contract, locked by reflection. Catches a heavy blob creeping back onto the list row
    /// AND a rendered field being silently dropped — the two failure modes of a projection DTO.
    /// </summary>
    [Fact]
    public void AnalysisListItemResponse_ExposesExactlyTheProjectedProperties()
    {
        var actual = typeof(AnalysisListItemResponse)
            .GetProperties()
            .Select(p => p.Name)
            .ToHashSet();

        var expected = new HashSet<string>
        {
            nameof(AnalysisListItemResponse.ResultId),
            nameof(AnalysisListItemResponse.AnalysisId),
            nameof(AnalysisListItemResponse.AnalysisKind),   // DDL 072: a light string, not a blob
            nameof(AnalysisListItemResponse.EntityId),
            nameof(AnalysisListItemResponse.EntityName),
            nameof(AnalysisListItemResponse.EntityCuit),
            nameof(AnalysisListItemResponse.FiscalYear),
            nameof(AnalysisListItemResponse.RequestedAmount),
            nameof(AnalysisListItemResponse.Status),
            nameof(AnalysisListItemResponse.GeneratedAt),
            nameof(AnalysisListItemResponse.AiSuggestedScore),
            nameof(AnalysisListItemResponse.ConfidenceScore),
            nameof(AnalysisListItemResponse.AiSuggestedLevel),
            nameof(AnalysisListItemResponse.FinalRiskScore),
            nameof(AnalysisListItemResponse.FinalRiskLevel),
            nameof(AnalysisListItemResponse.RequestedBy),
            nameof(AnalysisListItemResponse.RequestedByName),
            nameof(AnalysisListItemResponse.ReviewedBy),
            nameof(AnalysisListItemResponse.ReviewedByName),
            nameof(AnalysisListItemResponse.DecisionAmount),
            nameof(AnalysisListItemResponse.DecisionCurrency),
            nameof(AnalysisListItemResponse.SectorProfile),
            nameof(AnalysisListItemResponse.CriticalRatiosCount),
            nameof(AnalysisListItemResponse.AppliedFloor),
            nameof(AnalysisListItemResponse.ScoreBeforeFloor),
            nameof(AnalysisListItemResponse.BcraTrendDelta),
            nameof(AnalysisListItemResponse.ScoreReliability)   // 1b-2b: a computed verdict, no blob
        };

        Assert.Equal(expected, actual);

        // ...and the envelope actually ships the light row, not the detail DTO.
        Assert.Equal(
            typeof(IReadOnlyList<AnalysisListItemResponse>),
            typeof(AnalysisListResponse).GetProperty(nameof(AnalysisListResponse.Items))!.PropertyType);
    }

    /// <summary>
    /// The five values the list needs are lifted out of the persisted blob; the blob itself is not
    /// echoed. criticalRatiosCount is DERIVED — the blob stores the ratio names, not a count.
    /// </summary>
    [Fact]
    public async Task GetAll_ScoreBreakdown_FlattensTheFiveValues()
    {
        var (sut, _, _, _, _) = BuildOneRow(r => r.ScoreBreakdownJson =
            """{"scoreRatios":70,"sectorProfile":"Industria","criticalRatios":["leverage","endeudamiento"],"appliedFloor":65.0,"scoreBeforeFloor":38.0,"bcraTrendDelta":2}""");

        var body = await ListAsync(sut);

        var row = Assert.Single(body.Items);
        Assert.Equal("Industria", row.SectorProfile);
        Assert.Equal(2, row.CriticalRatiosCount);
        Assert.Equal(65.0m, row.AppliedFloor);
        Assert.Equal(38.0m, row.ScoreBeforeFloor);
        Assert.Equal(2, row.BcraTrendDelta);
    }

    /// <summary>
    /// scoreBeforeFloor rides on the row for ONE reason: appliedFloor alone cannot say whether the
    /// floor bound. The engine reports the floor computed from the critical ratios even when the
    /// composite already exceeded it, so a row can carry a non-zero appliedFloor with nothing having
    /// moved. Both cases are pinned here, because the Portal renders off this pair.
    /// </summary>
    [Theory]
    [InlineData(38.0, 65.0, true)]   // composite below the floor → raised to it
    [InlineData(90.25, 70.0, false)] // composite already above it → floor computed, never applied
    public async Task GetAll_ScoreBreakdown_CarriesEnoughToTellWhetherTheFloorBound(
        double scoreBeforeFloor, double appliedFloor, bool expectedBound)
    {
        // InvariantCulture: a comma-decimal host would emit "90,25" and produce a blob that does not
        // parse, which would silently null both fields and make the assertion pass for the wrong reason.
        string floor  = appliedFloor.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string before = scoreBeforeFloor.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var (sut, _, _, _, _) = BuildOneRow(r => r.ScoreBreakdownJson =
            $$"""{"criticalRatios":["leverage"],"appliedFloor":{{floor}},"scoreBeforeFloor":{{before}}}""");

        var body = await ListAsync(sut);

        var row = Assert.Single(body.Items);
        Assert.Equal(expectedBound, row.ScoreBeforeFloor < row.AppliedFloor);
    }

    /// <summary>
    /// A measured zero is NOT absence. The engine writes appliedFloor and bcraTrendDelta as
    /// non-nullable numbers, so 0 means "ran, nothing triggered" — it must survive as 0, not
    /// collapse to null. This is the test that stops someone "simplifying" those nullables away.
    /// </summary>
    [Fact]
    public async Task GetAll_ScoreBreakdownWithZeroes_DistinguishesMeasuredZeroFromAbsent()
    {
        var (sut, _, _, _, _) = BuildOneRow(r => r.ScoreBreakdownJson =
            """{"sectorProfile":null,"criticalRatios":[],"appliedFloor":0,"bcraTrendDelta":0}""");

        var body = await ListAsync(sut);

        var row = Assert.Single(body.Items);
        Assert.Null(row.SectorProfile);
        Assert.Equal(0, row.CriticalRatiosCount);
        Assert.Equal(0m, row.AppliedFloor);
        Assert.Equal(0, row.BcraTrendDelta);
    }

    /// <summary>
    /// The mirror of the previous test: an analysis persisted before the floor/trend/sector features
    /// shipped has no such KEY, and null is the only honest answer. criticalRatiosCount is the
    /// deliberate exception — no count key has ever existed, so absent collapses to 0.
    /// </summary>
    [Fact]
    public async Task GetAll_ScoreBreakdownMissingTheKeys_LeavesTheNullablesNullAndCountZero()
    {
        var (sut, _, _, _, _) = BuildOneRow(r => r.ScoreBreakdownJson = """{"scoreRatios":70}""");

        var body = await ListAsync(sut);

        var row = Assert.Single(body.Items);
        Assert.Null(row.SectorProfile);
        Assert.Equal(0, row.CriticalRatiosCount);
        Assert.Null(row.AppliedFloor);
        Assert.Null(row.BcraTrendDelta);
    }

    /// <summary>
    /// A blob that does not parse degrades the four values and never fails the page.
    /// </summary>
    [Fact]
    public async Task GetAll_MalformedScoreBreakdownJson_StillReturns200WithEmptyValues()
    {
        var (sut, _, _, _, _) = BuildOneRow(r => r.ScoreBreakdownJson = "{not json");

        var body = await ListAsync(sut);

        var row = Assert.Single(body.Items);
        Assert.Null(row.SectorProfile);
        Assert.Equal(0, row.CriticalRatiosCount);
        Assert.Null(row.AppliedFloor);
        Assert.Null(row.BcraTrendDelta);
    }

    /// <summary>
    /// Pre-scoring analyses have no blob at all.
    /// </summary>
    [Fact]
    public async Task GetAll_NullScoreBreakdownJson_LeavesTheFourValuesEmpty()
    {
        var (sut, _, _, _, _) = BuildOneRow(r => r.ScoreBreakdownJson = null);

        var body = await ListAsync(sut);

        var row = Assert.Single(body.Items);
        Assert.Null(row.SectorProfile);
        Assert.Equal(0, row.CriticalRatiosCount);
        Assert.Null(row.AppliedFloor);
        Assert.Null(row.BcraTrendDelta);
    }

    /// <summary>
    /// confidenceScore has been pinned to null on every row since the scoring engine replaced it
    /// with aiSuggestedScore. The key is kept so the projection stays a strict subset of what
    /// clients already parse — see the DTO. Read aiSuggestedScore instead.
    /// </summary>
    [Fact]
    public async Task GetAll_ConfidenceScore_IsAlwaysNull()
    {
        var (sut, _, _, _, _) = BuildOneRow();

        var body = await ListAsync(sut);

        var row = Assert.Single(body.Items);
        Assert.Null(row.ConfidenceScore);
        Assert.Equal(72.50m, row.AiSuggestedScore);
    }

    /// <summary>
    /// The point of the change: a row whose every heavy column is unparseable garbage still lists
    /// cleanly, because the list path no longer reads any of them.
    /// </summary>
    [Fact]
    public async Task GetAll_RowWithGarbageDetailBlobs_MapsFineBecauseNothingReadsThem()
    {
        var (sut, result, _, _, _) = BuildOneRow(r =>
        {
            r.RatiosJson       = "{not json";
            r.BalanceJson      = "{not json";
            r.RiskAlerts       = "{not json";
            r.Anomalies        = "{not json";
            r.ExecutiveSummary = "{not json";
            r.ScoringNarrative = "{not json";
            r.BcraDataJson     = "{not json";
            r.LufeDataJson     = "{not json";
            r.FiscalDataJson   = "{not json";
            r.CasfogDataJson   = "{not json";
            r.NosisDataJson    = "{not json";
        });

        var body = await ListAsync(sut);

        var row = Assert.Single(body.Items);
        Assert.Equal(result.ResultId, row.ResultId);
        Assert.Equal("Approved", row.Status);
        Assert.Equal("High", row.AiSuggestedLevel);
    }

    /// <summary>
    /// Paging guards are untouched by the projection, and Total still counts the whole result set.
    /// </summary>
    [Fact]
    public async Task GetAll_PagingGuards_ClampAndTotalCountsTheFullSet()
    {
        var rows = Enumerable.Range(0, 3)
            .Select(_ => new AnalysisResult { ResultId = Guid.NewGuid(), AnalysisId = Guid.NewGuid(), TenantId = Tenant })
            .ToArray();

        var sut = BuildSut(
            new StubAnalysisResultRepository { RowsToReturn = rows },
            new StubAnalysisRepository(),
            new StubEntityRepository(),
            new StubUserRepository());

        var response = await sut.GetAllAsync(page: 0, pageSize: 0, cancellationToken: CancellationToken.None);
        var body = Assert.IsType<AnalysisListResponse>(Assert.IsType<OkObjectResult>(response).Value);

        Assert.Equal(1, body.Page);
        Assert.Equal(20, body.PageSize);
        Assert.Equal(3, body.Total);
        Assert.Equal(3, body.Items.Count);
    }

    /// <summary>
    /// Paging is pushed to the database: the controller hands the repository the real skip/take and
    /// never asks for a full-row read. Before this change it loaded the entire tenant — every row,
    /// every nvarchar(max) column — and called Skip/Take on the resulting List.
    /// </summary>
    [Fact]
    public async Task GetAll_PagesAtTheDatabase_AndNeverLoadsTheWholeTenant()
    {
        var rows = Enumerable.Range(1, 5)
            .Select(d => new AnalysisResult
            {
                ResultId    = Guid.NewGuid(),
                AnalysisId  = Guid.NewGuid(),
                TenantId    = Tenant,
                GeneratedAt = new DateTime(2026, 3, d, 10, 0, 0, DateTimeKind.Utc)
            })
            .ToArray();

        var resultRepo = new StubAnalysisResultRepository { RowsToReturn = rows };
        var sut = BuildSut(resultRepo, new StubAnalysisRepository(), new StubEntityRepository(), new StubUserRepository());

        var response = await sut.GetAllAsync(page: 3, pageSize: 2, cancellationToken: CancellationToken.None);
        var body = Assert.IsType<AnalysisListResponse>(Assert.IsType<OkObjectResult>(response).Value);

        Assert.Equal(4, resultRepo.LastSkip);   // (3 - 1) * 2
        Assert.Equal(2, resultRepo.LastTake);
        Assert.Equal(0, resultRepo.FullRowCallCount);
        Assert.Equal(5, body.Total);
        Assert.Equal(3, body.Page);
    }

    /// <summary>
    /// The entityId filter routes to the by-entity page, still at the database, still never falling
    /// back to a full-row read.
    /// </summary>
    [Fact]
    public async Task GetAll_WithEntityFilter_UsesTheByEntityPageQuery()
    {
        var entityId   = Guid.NewGuid();
        var resultRepo = new StubAnalysisResultRepository();
        var sut = BuildSut(resultRepo, new StubAnalysisRepository(), new StubEntityRepository(), new StubUserRepository());

        await sut.GetAllAsync(page: 2, pageSize: 10, entityId: entityId, cancellationToken: CancellationToken.None);

        Assert.Equal(entityId, resultRepo.LastEntityId);
        Assert.Equal(10, resultRepo.LastSkip);
        Assert.Equal(10, resultRepo.LastTake);
        Assert.Equal(0, resultRepo.FullRowCallCount);
    }

    /// <summary>
    /// Entity names are resolved by id, for the page only. This used to call GetAllByTenantAsync and
    /// filter in memory — a second full-tenant scan, dragging all five of every entity's
    /// nvarchar(max) snapshots along, to answer a question about at most pageSize ids.
    /// </summary>
    [Fact]
    public async Task GetAll_EntityNames_AreLookedUpById_NotByLoadingTheTenant()
    {
        var analysisId = Guid.NewGuid();
        var entityId   = Guid.NewGuid();

        var entityRepo = new StubEntityRepository
        {
            EntitiesToReturn = new[]
            {
                new Entity
                {
                    EntityId     = entityId,
                    TenantId     = Tenant,
                    CUIT         = "30-71234567-1",
                    BusinessName = "Metalúrgica del Sur S.A."
                },
                // A second entity of the same tenant that this page does NOT reference. It must not
                // be fetched, which is exactly what an id-scoped lookup buys over a tenant-wide one.
                new Entity
                {
                    EntityId     = Guid.NewGuid(),
                    TenantId     = Tenant,
                    CUIT         = "30-99999999-9",
                    BusinessName = "No corresponde a esta página"
                }
            }
        };

        var sut = BuildSut(
            new StubAnalysisResultRepository
            {
                RowsToReturn = new[]
                {
                    new AnalysisResult
                    {
                        ResultId    = Guid.NewGuid(),
                        AnalysisId  = analysisId,
                        TenantId    = Tenant,
                        GeneratedAt = new DateTime(2026, 3, 4, 10, 0, 0, DateTimeKind.Utc)
                    }
                }
            },
            new StubAnalysisRepository
            {
                AnalysesToReturn = new[]
                {
                    new Analysis
                    {
                        AnalysisId = analysisId,
                        TenantId   = Tenant,
                        EntityId   = entityId,
                        DocumentId = Guid.NewGuid()
                    }
                }
            },
            entityRepo,
            new StubUserRepository());

        var body = await ListAsync(sut);

        Assert.Equal(0, entityRepo.GetAllCallCount);
        Assert.Equal([entityId], entityRepo.LastLookupIds);

        // ...and the names still land on the row.
        var row = Assert.Single(body.Items);
        Assert.Equal("Metalúrgica del Sur S.A.", row.EntityName);
        Assert.Equal("30-71234567-1", row.EntityCuit);
    }

    /// <summary>
    /// The tenant guard still fires before any repository is touched.
    /// </summary>
    [Fact]
    public async Task GetAll_WhenTenantContextMissing_Returns500()
    {
        // The result repository throws on every member it is asked for beyond the two list reads,
        // so reaching it at all would fail loudly rather than silently returning an empty page.
        var sut = BuildSut(
            new StubAnalysisResultRepository(),
            new StubAnalysisRepository(),
            new StubEntityRepository(),
            new StubUserRepository(),
            withTenant: false);

        var response = await sut.GetAllAsync(cancellationToken: CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(response);
        Assert.Equal(500, problem.StatusCode);
    }

    /// <summary>
    /// Score credibility 1b-2a: a breakdown carrying <c>reliabilityFacts</c> and a per-ratio
    /// <c>measured</c> still flattens to the same five values — the extra keys are ignored.
    /// </summary>
    [Fact]
    public async Task GetAll_ScoreBreakdownWithReliabilityFacts_StillFlattensTheFiveValues()
    {
        var (sut, _, _, _, _) = BuildOneRow(r => r.ScoreBreakdownJson =
            """
            {"breakdown":{"roe":{"score":50,"status":"Atencion","excluded":false,"effectiveWeight":0.10,"measured":false}},
             "sectorProfile":"Industria","criticalRatios":["leverage"],"appliedFloor":65.0,"scoreBeforeFloor":38.0,"bcraTrendDelta":2,
             "reliabilityFacts":{"version":1,"bcraSituacionUnreadable":false,"macroImputed":true,"sectorFallback":false,"configSource":"defaults","configHash":"0f1e2d3c4b5a6978","ratiosSource":"computed","fiscalYear":2024,"bcra":{"deudaEstado":"DebtFound","periodoConsultado":"202609","chequesEvaluated":true},"lufe":{"ventasEstado":"carried_forward","ventasUltimoPeriodo":"202608"}}}
            """);

        var body = await ListAsync(sut);

        var row = Assert.Single(body.Items);
        Assert.Equal("Industria", row.SectorProfile);
        Assert.Equal(1, row.CriticalRatiosCount);
        Assert.Equal(65.0m, row.AppliedFloor);
        Assert.Equal(38.0m, row.ScoreBeforeFloor);
        Assert.Equal(2, row.BcraTrendDelta);
    }

    // ── Score reliability (score credibility 1b-2b) ─────────────────────────

    /// <summary>
    /// The list's half of the parity guarantee: the shared fixture row, through GET /api/analyses, gets the
    /// same verdict as the detail and the dashboard — from the projection's own EnrichmentStatus and
    /// RescoredAt and the list's single parse of the breakdown.
    /// </summary>
    [Fact]
    public async Task GetAll_CarriesTheScoreReliabilityOfTheParityRow()
    {
        var (sut, _, _, _, _) = BuildOneRow(
            r =>
            {
                r.AiSuggestedScore   = ScoreReliabilityFixture.ParityScore;
                r.EnrichmentStatus   = ScoreReliabilityFixture.ParityEnrichment;
                r.GeneratedAt        = ScoreReliabilityFixture.ParityGeneratedAt;
                r.RescoredAt         = ScoreReliabilityFixture.ParityRescoredAt;
                r.ScoreBreakdownJson = ScoreReliabilityFixture.ParityBreakdownJson();
            },
            a => a.FiscalYear = ScoreReliabilityFixture.ParityFiscalYear);

        var row = Assert.Single((await ListAsync(sut)).Items);

        Assert.Equal(ScoreReliabilityFixture.Wire(ScoreReliabilityFixture.ParityExpected()),
                     ScoreReliabilityFixture.Wire(row.ScoreReliability));
    }

    [Fact]
    public async Task GetAll_PersonaRow_HasNoScoreReliability()
    {
        var (sut, _, _, _, _) = BuildOneRow(
            r => { r.AiSuggestedScore = null; r.AiSuggestedLevel = null; },
            a => a.AnalysisKind = AnalysisKinds.PersonaHumana);

        Assert.Null(Assert.Single((await ListAsync(sut)).Items).ScoreReliability);
    }

    [Fact]
    public async Task GetAll_ScoredRowWithoutABreakdown_IsFactsUnavailable_AndUnscoredRowIsNone()
    {
        var (scored, _, _, _, _) = BuildOneRow(r => r.ScoreBreakdownJson = null);
        var (unscored, _, _, _, _) = BuildOneRow(r => { r.AiSuggestedScore = null; r.ScoreBreakdownJson = null; });

        var scoredRow   = Assert.Single((await ListAsync(scored)).Items);
        var unscoredRow = Assert.Single((await ListAsync(unscored)).Items);

        Assert.Equal(ScoreReliabilityLevels.WithCaveats, scoredRow.ScoreReliability!.Level);
        Assert.Equal("facts_unavailable", Assert.Single(scoredRow.ScoreReliability.Reasons).Code);
        Assert.Equal(ScoreReliabilityLevels.None, unscoredRow.ScoreReliability!.Level);
        Assert.Equal("score_unavailable", Assert.Single(unscoredRow.ScoreReliability.Reasons).Code);
    }

    /// <summary>A breakdown that does not parse never fails the page: the row is listed, its verdict reads facts_unavailable.</summary>
    [Fact]
    public async Task GetAll_UnreadableBreakdown_StillListsTheRow_WithFactsUnavailable()
    {
        var (sut, result, _, _, _) = BuildOneRow(r => r.ScoreBreakdownJson = "{\"breakdown\": [");

        var row = Assert.Single((await ListAsync(sut)).Items);

        Assert.Equal(result.ResultId, row.ResultId);
        Assert.Null(row.SectorProfile);
        Assert.Equal(ScoreReliabilityLevels.WithCaveats, row.ScoreReliability!.Level);
        Assert.Equal("facts_unavailable", Assert.Single(row.ScoreReliability.Reasons).Code);
    }

    /// <summary>
    /// The list parses the breakdown once, into its own narrow shape. That shape must carry EXACTLY the keys
    /// the reliability adapter reads (plus its five flattened ones) — no more, so the list stays narrow, and no
    /// fewer, or the list's verdict would silently diverge from the detail's.
    /// </summary>
    [Fact]
    public void ListScoreBreakdown_CarriesExactlyTheReliabilityKeysPlusTheFiveFlattenedOnes()
    {
        static HashSet<string> JsonNames(Type type) => type
            .GetProperties()
            .Select(p => p.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute), false)
                          .Cast<System.Text.Json.Serialization.JsonPropertyNameAttribute>()
                          .Single().Name)
            .ToHashSet();

        var listKeys        = JsonNames(typeof(AnalysisController.ListScoreBreakdown));
        var reliabilityKeys = JsonNames(typeof(Api.Web.FinanceIA.Servicios.ScoreReliability.ScoreReliabilityBreakdown));

        Assert.Equal(
            reliabilityKeys.Concat(["sectorProfile", "criticalRatios", "appliedFloor", "scoreBeforeFloor", "bcraTrendDelta"]).ToHashSet(),
            listKeys);
    }
}
