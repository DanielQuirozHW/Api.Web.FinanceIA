// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-26

using System.Security.Claims;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Tests.Clases;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Covers how <see cref="AlertsController.GetAlertsAsync"/> resolves its two display fields.
///
/// <para>
/// GET /api/alerts?entityId= took ~20s on a warm database, and the alerts themselves were never
/// the cost: the endpoint resolved EntityName by calling
/// <c>IEntityRepository.GetAllByTenantAsync</c> and ResultId by calling
/// <c>IAnalysisResultRepository.GetAllByTenantAsync</c> — two unprojected full-tenant reads, one
/// dragging all five <c>nvarchar(max)</c> source snapshots off every entity row, the other all
/// fourteen heavy columns off every AnalysisResult row. Both ran unconditionally, including on the
/// entityId path that returns a handful of alerts for one PyME.
/// </para>
///
/// <para>
/// The load-bearing tests here are <see cref="GetAlerts_NeverReadsTheWholeTenant"/> and
/// <see cref="GetAlerts_AsksOnlyForTheIdsTheAlertsActuallyName"/>. The rest pin the output, because
/// the whole point of the change is that the response did not move: same DTO, same fields, same
/// "Sistema" fallback. Same counter idiom as AnalysisControllerListProjectionTests — a call count
/// asserted to be zero, rather than a throwing stub, so a regression reads as a number and not as
/// a NotImplementedException from somewhere in the stack.
/// </para>
///
/// <para>
/// The last section covers the other thing the full-tenant GET does first: reconciling the cupo
/// event alerts (<see cref="ICupoAlertService"/>), and that its failure never fails the listing.
/// </para>
///
/// <para>
/// Hand-rolled private sealed stubs (no mock library). The user repository is read by the GET path only
/// to spell resolver names (one batched GetByIdsAsync) and by the PATCH/POST actions to resolve the
/// caller. Argument order mirrors the ctor signature. The structured fields of the Alertas redesign are
/// covered in the partial file beside this one.
/// </para>
/// </summary>
public sealed partial class AlertsControllerLookupScopeTests
{
    private const string Tenant      = "tenant-1";
    private const string FirebaseUid = "uid-1";

    // ── Stubs ─────────────────────────────────────────────────────────────────
    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private sealed class StubAlertRepository : IAlertRepository
    {
        public IReadOnlyList<Alert> AlertsToReturn { get; set; } = Array.Empty<Alert>();

        public Guid? LastEntityId { get; private set; }

        public int GetAllCallCount { get; private set; }

        public Task<IReadOnlyList<Alert>> GetAllByTenantAsync(string tenantId, CancellationToken ct)
        {
            GetAllCallCount++;
            return Task.FromResult(AlertsToReturn);
        }

        public Task<IReadOnlyList<Alert>> GetByEntityIdAsync(Guid entityId, string tenantId, CancellationToken ct)
        {
            LastEntityId = entityId;
            return Task.FromResult(AlertsToReturn);
        }

        public Task CreateManyAsync(IEnumerable<Alert> alerts, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Alert?> GetByIdAsync(Guid alertId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<bool> MarkAsReadAsync(Guid alertId, string tenantId, Guid userId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<bool> MarkAsResolvedAsync(Guid alertId, string tenantId, Guid userId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<int> SupersedePreviousEntityAlertsAsync(
            Guid entityId, string tenantId, Guid? excludeAnalysisId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<int> MarkAllAsReadAsync(string tenantId, Guid userId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<bool> ExistsByTypeInPeriodAsync(
            string tenantId, string alertType, DateTime periodStartUtc, DateTime periodEndUtc, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<Alert>> GetOpenByTypesForUpdateAsync(
            string tenantId, IReadOnlyCollection<string> alertTypes, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<Alert>> GetOpenByTypesCreatedBeforeForUpdateAsync(
            string tenantId, IReadOnlyCollection<string> alertTypes, DateTime createdBeforeUtc, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<(string AlertType, string MetricKey), Alert>> GetLatestByMetricKeysAsync(
            string tenantId, IReadOnlyCollection<string> alertTypes, IReadOnlyCollection<string> metricKeys, CancellationToken ct)
            => throw new NotImplementedException();
        public Task SaveReconciliationAsync(string tenantId, IReadOnlyCollection<Alert> toCreate, CancellationToken ct)
            => throw new NotImplementedException();

        public IReadOnlyList<PreviousAlertRow> PreviousToReturn { get; set; } = Array.Empty<PreviousAlertRow>();

        public int PreviousCallCount { get; private set; }

        public List<string> LastPreviousKeys { get; private set; } = [];

        public Task<IReadOnlyList<PreviousAlertRow>> GetPreviousByKeysAsync(
            string tenantId, IReadOnlyCollection<string> metricKeys, CancellationToken ct)
        {
            PreviousCallCount++;
            LastPreviousKeys = metricKeys.ToList();
            return Task.FromResult(PreviousToReturn);
        }

        public AlertUnresolveOutcome UnresolveOutcome { get; set; } = AlertUnresolveOutcome.Reopened;

        public (Guid AlertId, string TenantId)? LastUnresolve { get; private set; }

        public Task<AlertUnresolveOutcome> UnresolveAsync(Guid alertId, string tenantId, CancellationToken ct)
        {
            LastUnresolve = (alertId, tenantId);
            return Task.FromResult(UnresolveOutcome);
        }
    }

    /// <summary>
    /// Records each reconciliation and how many listing reads had happened by then, so a test can
    /// assert it ran BEFORE the list was read. Optionally throws, to prove the listing survives it.
    /// </summary>
    private sealed class RecordingCupoAlertService(StubAlertRepository alertRepo) : ICupoAlertService
    {
        public List<(string TenantId, int ListReadsSoFar)> Calls { get; } = [];

        public Exception? ThrowOnReconcile { get; set; }

        /// <summary>What the pass "computed"; null is a pass that was skipped or failed.</summary>
        public IReadOnlyDictionary<Guid, AlertFactsDto>? FactsToReturn { get; set; } = new Dictionary<Guid, AlertFactsDto>();

        public Task<IReadOnlyDictionary<Guid, AlertFactsDto>?> ReconcileAsync(string tenantId, DateTime nowUtc, CancellationToken ct)
        {
            Calls.Add((tenantId, alertRepo.GetAllCallCount));
            return ThrowOnReconcile is null
                ? Task.FromResult(FactsToReturn)
                : Task.FromException<IReadOnlyDictionary<Guid, AlertFactsDto>?>(ThrowOnReconcile);
        }
    }

    private sealed class StubEntityRepository : IEntityRepository
    {
        public IReadOnlyList<Entity> EntitiesToReturn { get; set; } = Array.Empty<Entity>();

        /// <summary>
        /// How often the endpoint asked for the whole tenant. Must stay 0: resolving a handful of
        /// names used to be done by loading every entity with all five of its nvarchar(max)
        /// snapshots.
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
                        CUIT         = e.CUIT,
                        PersonType   = e.PersonType
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

    /// <summary>
    /// GetByIdsAsync is counted (the GET's one batched name lookup) and returns every user it holds whose
    /// id was asked for, whatever its tenant — as the real one, which has no tenant predicate.
    /// GetByFirebaseUidAsync resolves the caller of the PATCH/POST actions.
    /// </summary>
    private sealed class StubUserRepository : IUserRepository
    {
        public List<User> Users { get; } = [];

        public User? Caller { get; set; } = new() { UserId = Guid.NewGuid(), TenantId = Tenant, FirebaseUid = FirebaseUid };

        public int GetByIdsCallCount { get; private set; }

        public List<Guid> LastIds { get; private set; } = [];

        public Task<IReadOnlyList<User>> GetByIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct)
        {
            GetByIdsCallCount++;
            LastIds = userIds.ToList();
            IReadOnlyList<User> found = Users.Where(u => LastIds.Contains(u.UserId)).ToList();
            return Task.FromResult(found);
        }

        public Task<User?> GetByFirebaseUidAsync(string firebaseUid, CancellationToken ct)
            => Task.FromResult(firebaseUid == FirebaseUid ? Caller : null);

        public Task<User?> GetByUserIdAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<User?> GetByEmailAsync(string email, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> ExistsByFirebaseUidAsync(string firebaseUid, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<User>> GetActiveByTenantAsync(string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<User> CreateAsync(User user, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateLastLoginAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateOnboardingFlagsAsync(Guid userId, string? flagsJson, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateTeamAsync(Guid userId, Guid? teamId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> ClearTeamAsync(Guid teamId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class StubAnalysisResultRepository : IAnalysisResultRepository
    {
        public IReadOnlyList<AnalysisResult> ResultsToReturn { get; set; } = Array.Empty<AnalysisResult>();

        /// <summary>
        /// How often the endpoint fell back to a full-row read. Counted rather than thrown so a test
        /// can assert zero and get a readable failure if that ever regresses.
        /// </summary>
        public int FullRowCallCount { get; private set; }

        public List<Guid> LastAnalysisLookupIds { get; private set; } = [];

        public Task<IReadOnlyDictionary<Guid, Guid>> GetResultIdsByAnalysisIdsAsync(
            IEnumerable<Guid> analysisIds, string tenantId, CancellationToken ct)
        {
            LastAnalysisLookupIds = analysisIds.ToList();
            var wanted = LastAnalysisLookupIds.ToHashSet();

            IReadOnlyDictionary<Guid, Guid> map = ResultsToReturn
                .Where(r => wanted.Contains(r.AnalysisId))
                .ToDictionary(r => r.AnalysisId, r => r.ResultId);

            return Task.FromResult(map);
        }

        public Task<IReadOnlyList<AnalysisResult>> GetAllByTenantAsync(string tenantId, CancellationToken ct)
        {
            FullRowCallCount++;
            return Task.FromResult(ResultsToReturn);
        }

        public Task<IReadOnlyList<AnalysisResult>> GetByEntityIdAsync(Guid entityId, string tenantId, CancellationToken ct)
        {
            FullRowCallCount++;
            return Task.FromResult(ResultsToReturn);
        }

        public Task<(IReadOnlyList<AnalysisListProjection> Items, int TotalCount)> GetListPageByTenantAsync(
            string tenantId, int skip, int take, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<(IReadOnlyList<AnalysisListProjection> Items, int TotalCount)> GetListPageByEntityAsync(
            Guid entityId, string tenantId, int skip, int take, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<AnalysisResult?> GetByIdAsync(Guid resultId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<AnalysisResult?> GetByDocumentIdAsync(Guid documentId, string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<AnalysisResult> CreateAsync(AnalysisResult result, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<AnalysisResult> UpdateAsync(AnalysisResult result, CancellationToken ct)
            => throw new NotImplementedException();
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

        public Task<AnalysisLabelProjection?> GetLabelByIdAsync(
            Guid resultId, string tenantId, CancellationToken ct)
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

    private static AlertsController BuildSut(
        StubAlertRepository          alertRepo,
        StubEntityRepository         entityRepo,
        StubAnalysisResultRepository resultRepo,
        ICupoAlertService?           cupoAlertService = null,
        StubUserRepository?          userRepo = null)
    {
        var sut = new AlertsController(
            new StubTenantContextAccessor { TenantContext = BuildTenant() }, // tenantContextAccessor
            alertRepo,   // alertRepository
            entityRepo,  // entityRepository
            userRepo ?? new StubUserRepository(), // userRepository — resolver names, and the PATCH/POST caller
            resultRepo,  // analysisResultRepository
            cupoAlertService ?? new RecordingCupoAlertService(alertRepo), // cupoAlertService
            NullLogger<AlertsController>.Instance);

        sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim("uid", FirebaseUid) }, "Test"))
            }
        };
        return sut;
    }

    private static Alert Alert(
        Guid? entityId = null, Guid? analysisId = null, string severity = "High", bool isResolved = false)
        => new()
        {
            AlertId     = Guid.NewGuid(),
            TenantId    = Tenant,
            EntityId    = entityId,
            AnalysisId  = analysisId,
            AlertType   = "HIGH_LEVERAGE",
            Severity    = severity,
            Title       = "Apalancamiento elevado",
            Description = "El pasivo supera el umbral configurado.",
            IsResolved  = isResolved,
            CreatedAt   = new DateTime(2026, 3, 4, 10, 0, 0, DateTimeKind.Utc)
        };

    /// <summary>
    /// An entity fixture with all five snapshots populated. Nothing here should ever reach the
    /// controller: the lookup projects three columns, and none of them is a snapshot.
    /// </summary>
    private static Entity Ent(Guid entityId, string businessName, string personType = PersonTypes.Juridica) => new()
    {
        EntityId       = entityId,
        TenantId       = Tenant,
        PersonType     = personType,
        BusinessName   = businessName,
        CUIT           = "30-12345678-9",
        FiscalDataJson = """{"a":1}""",
        LufeDataJson   = """{"b":2}""",
        BcraDataJson   = """{"c":3}""",
        CasfogDataJson = """{"d":4}""",
        NosisDataJson  = """{"e":5}"""
    };

    private static AnalysisResult Result(Guid analysisId, Guid resultId) => new()
    {
        ResultId   = resultId,
        AnalysisId = analysisId,
        TenantId   = Tenant
    };

    private static AlertsResponseDto BodyOf(ActionResult<AlertsResponseDto> result)
    {
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        return Assert.IsType<AlertsResponseDto>(ok.Value);
    }

    // ── The change ────────────────────────────────────────────────────────────

    /// <summary>
    /// THE test for this change. Neither display lookup may fall back to a full-tenant read — those
    /// were two unprojected blob scans per request, and they ran even on the entityId path.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetAlerts_NeverReadsTheWholeTenant(bool byEntity)
    {
        var entityId   = Guid.NewGuid();
        var analysisId = Guid.NewGuid();

        var alertRepo  = new StubAlertRepository { AlertsToReturn = [Alert(entityId, analysisId)] };
        var entityRepo = new StubEntityRepository { EntitiesToReturn = [Ent(entityId, "Metalúrgica del Sur S.A.")] };
        var resultRepo = new StubAnalysisResultRepository { ResultsToReturn = [Result(analysisId, Guid.NewGuid())] };

        await BuildSut(alertRepo, entityRepo, resultRepo)
            .GetAlertsAsync(byEntity ? entityId : null, CancellationToken.None);

        Assert.Equal(0, entityRepo.GetAllCallCount);
        Assert.Equal(0, resultRepo.FullRowCallCount);
    }

    /// <summary>
    /// The lookups are driven by the alerts in hand, not by the tenant. Entities and analyses the
    /// alerts never mention must not be asked for — that is the difference between an IN over a
    /// bounded set and a table scan.
    /// </summary>
    [Fact]
    public async Task GetAlerts_AsksOnlyForTheIdsTheAlertsActuallyName()
    {
        var wantedEntity   = Guid.NewGuid();
        var wantedAnalysis = Guid.NewGuid();
        var strangerEntity   = Guid.NewGuid();
        var strangerAnalysis = Guid.NewGuid();

        var alertRepo = new StubAlertRepository
        {
            // The same entity twice, so the distinct-ing is exercised, plus a system alert whose
            // null ids must not reach either lookup.
            AlertsToReturn =
            [
                Alert(wantedEntity, wantedAnalysis),
                Alert(wantedEntity, wantedAnalysis),
                Alert(entityId: null, analysisId: null)
            ]
        };
        var entityRepo = new StubEntityRepository
        {
            EntitiesToReturn = [Ent(wantedEntity, "Metalúrgica del Sur S.A."), Ent(strangerEntity, "Otra S.A.")]
        };
        var resultRepo = new StubAnalysisResultRepository
        {
            ResultsToReturn = [Result(wantedAnalysis, Guid.NewGuid()), Result(strangerAnalysis, Guid.NewGuid())]
        };

        await BuildSut(alertRepo, entityRepo, resultRepo)
            .GetAlertsAsync(wantedEntity, CancellationToken.None);

        Assert.Equal([wantedEntity], entityRepo.LastLookupIds);
        Assert.Equal([wantedAnalysis], resultRepo.LastAnalysisLookupIds);
    }

    /// <summary>
    /// No alerts means no ids, which must mean no lookup call reaches the database at all — and no
    /// exception on the way there. The repositories short-circuit an empty id list internally, but
    /// the controller must still hand them an empty enumerable rather than null.
    /// </summary>
    [Fact]
    public async Task GetAlerts_WhenThereAreNoAlerts_PassesEmptyLookupsAndReturnsZeroedCounts()
    {
        var alertRepo  = new StubAlertRepository();
        var entityRepo = new StubEntityRepository();
        var resultRepo = new StubAnalysisResultRepository();

        var body = BodyOf(await BuildSut(alertRepo, entityRepo, resultRepo)
            .GetAlertsAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Empty(entityRepo.LastLookupIds);
        Assert.Empty(resultRepo.LastAnalysisLookupIds);
        Assert.Equal(0, entityRepo.GetAllCallCount);
        Assert.Equal(0, resultRepo.FullRowCallCount);

        Assert.Empty(body.Alerts);
        Assert.Equal(0, body.TotalCount);
        Assert.Equal(0, body.CriticalCount);
        Assert.Equal(0, body.HighCount);
        Assert.Equal(0, body.MediumCount);
        Assert.Equal(0, body.LowCount);
        Assert.Equal(0, body.ResolvedCount);
    }

    // ── The output, unchanged ─────────────────────────────────────────────────

    /// <summary>
    /// EntityName still comes from the entity when the id resolves. It is now read off
    /// EntityNameProjection.BusinessName rather than off a full Entity, which is invisible here —
    /// deliberately, because that is the claim.
    /// </summary>
    [Fact]
    public async Task GetAlerts_EntityName_IsTheBusinessNameWhenTheIdResolves()
    {
        var entityId  = Guid.NewGuid();
        var alertRepo = new StubAlertRepository { AlertsToReturn = [Alert(entityId)] };

        var body = BodyOf(await BuildSut(
                alertRepo,
                new StubEntityRepository { EntitiesToReturn = [Ent(entityId, "Metalúrgica del Sur S.A.")] },
                new StubAnalysisResultRepository())
            .GetAlertsAsync(entityId, CancellationToken.None));

        Assert.Equal("Metalúrgica del Sur S.A.", Assert.Single(body.Alerts).EntityName);
    }

    /// <summary>
    /// The two ways an alert renders as "Sistema": it has no EntityId at all (a system-level alert),
    /// or it has one that the tenant does not own. Both fell back to "Sistema" before the change and
    /// must still.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetAlerts_EntityName_FallsBackToSistema(bool entityIdIsNull)
    {
        var alertRepo = new StubAlertRepository
        {
            AlertsToReturn = [Alert(entityId: entityIdIsNull ? null : Guid.NewGuid())]
        };

        var body = BodyOf(await BuildSut(alertRepo, new StubEntityRepository(), new StubAnalysisResultRepository())
            .GetAlertsAsync(null, CancellationToken.None));

        Assert.Equal("Sistema", Assert.Single(body.Alerts).EntityName);
    }

    /// <summary>
    /// ResultId is what the "Ver" action navigates by, so it must still be resolved from AnalysisId —
    /// and must still be null when the analysis has no result row, rather than defaulting to
    /// Guid.Empty.
    /// </summary>
    [Fact]
    public async Task GetAlerts_ResultId_ResolvesFromAnalysisId_AndIsNullWhenNoResultExists()
    {
        var resolved   = Guid.NewGuid();
        var unresolved = Guid.NewGuid();
        var resultId   = Guid.NewGuid();

        var alertRepo = new StubAlertRepository
        {
            AlertsToReturn = [Alert(analysisId: resolved), Alert(analysisId: unresolved), Alert(analysisId: null)]
        };
        var resultRepo = new StubAnalysisResultRepository { ResultsToReturn = [Result(resolved, resultId)] };

        var body = BodyOf(await BuildSut(alertRepo, new StubEntityRepository(), resultRepo)
            .GetAlertsAsync(null, CancellationToken.None));

        Assert.Equal(resultId, body.Alerts[0].ResultId);
        Assert.Null(body.Alerts[1].ResultId);
        Assert.Null(body.Alerts[2].ResultId);
    }

    /// <summary>
    /// The severity tallies and ResolvedCount are computed off the mapped DTOs, so they are the
    /// cheapest possible canary for the mapping having moved. Unchanged by this PR.
    /// </summary>
    [Fact]
    public async Task GetAlerts_StillReportsSeverityAndResolvedCounts()
    {
        var alertRepo = new StubAlertRepository
        {
            AlertsToReturn =
            [
                Alert(severity: "Critical"),
                Alert(severity: "High"),
                Alert(severity: "High"),
                Alert(severity: "Medium"),
                Alert(severity: "Low", isResolved: true)
            ]
        };

        var body = BodyOf(await BuildSut(alertRepo, new StubEntityRepository(), new StubAnalysisResultRepository())
            .GetAlertsAsync(null, CancellationToken.None));

        Assert.Equal(5, body.TotalCount);
        Assert.Equal(1, body.CriticalCount);
        Assert.Equal(2, body.HighCount);
        Assert.Equal(1, body.MediumCount);
        Assert.Equal(1, body.LowCount);
        Assert.Equal(1, body.ResolvedCount);
    }

    /// <summary>
    /// The entityId query parameter still selects the by-entity read rather than the tenant read.
    /// Nothing in this PR touches that branch, but it is the branch the slow page uses.
    /// </summary>
    [Fact]
    public async Task GetAlerts_WithEntityId_ReadsAlertsByEntity()
    {
        var entityId  = Guid.NewGuid();
        var alertRepo = new StubAlertRepository { AlertsToReturn = [Alert(entityId)] };

        await BuildSut(alertRepo, new StubEntityRepository(), new StubAnalysisResultRepository())
            .GetAlertsAsync(entityId, CancellationToken.None);

        Assert.Equal(entityId, alertRepo.LastEntityId);
    }

    // ── Cupo alert reconciliation ─────────────────────────────────────────────

    /// <summary>
    /// The full-tenant listing reconciles the cupo alerts for ITS tenant, and does it before reading
    /// the list, so the response already carries what the reconciliation wrote.
    /// </summary>
    [Fact]
    public async Task GetAlerts_FullListing_ReconcilesCupoAlertsBeforeReadingTheList()
    {
        var alertRepo = new StubAlertRepository();
        var cupoAlerts = new RecordingCupoAlertService(alertRepo);

        await BuildSut(alertRepo, new StubEntityRepository(), new StubAnalysisResultRepository(), cupoAlerts)
            .GetAlertsAsync(null, CancellationToken.None);

        var call = Assert.Single(cupoAlerts.Calls);
        Assert.Equal(Tenant, call.TenantId);
        Assert.Equal(0, call.ListReadsSoFar);
        Assert.Equal(1, alertRepo.GetAllCallCount);
    }

    /// <summary>The ?entityId= variant reads what is stored and reconciles nothing.</summary>
    [Fact]
    public async Task GetAlerts_WithEntityId_DoesNotReconcile()
    {
        var alertRepo = new StubAlertRepository();
        var cupoAlerts = new RecordingCupoAlertService(alertRepo);

        await BuildSut(alertRepo, new StubEntityRepository(), new StubAnalysisResultRepository(), cupoAlerts)
            .GetAlertsAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Empty(cupoAlerts.Calls);
    }

    /// <summary>
    /// FAULT ISOLATION. Even a reconciliation that breaks its never-throws contract cannot cost the
    /// analyst their alerts: the stored list is still served with a 200.
    /// </summary>
    [Fact]
    public async Task GetAlerts_WhenReconciliationThrows_StillReturns200WithTheList()
    {
        var alertRepo = new StubAlertRepository { AlertsToReturn = [Alert(), Alert(severity: "Medium")] };
        var cupoAlerts = new RecordingCupoAlertService(alertRepo)
        {
            ThrowOnReconcile = new InvalidOperationException("boom")
        };

        var body = BodyOf(await BuildSut(alertRepo, new StubEntityRepository(), new StubAnalysisResultRepository(), cupoAlerts)
            .GetAlertsAsync(null, CancellationToken.None));

        Assert.Equal(2, body.TotalCount);
        Assert.Equal(1, body.HighCount);
        Assert.Equal(1, body.MediumCount);
    }

    /// <summary>
    /// FAULT ISOLATION, end to end through the REAL pass: the billing close-out read fails inside
    /// <see cref="CupoAlertService"/>, and the analyst still gets the stored list with a 200.
    /// </summary>
    [Fact]
    public async Task GetAlerts_WhenTheBillingCloseOutFails_StillReturns200WithTheList()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"alerts-closeout-{Guid.NewGuid()}")
            .Options;
        await using var ctx = new FinanciaDbContext(options);
        var cupoAlerts = new CupoAlertService(
            new CreditLineRepository(ctx, NullLogger<CreditLineRepository>.Instance),
            new DashboardRepository(ctx),
            CupoAlertServiceTests.CloseOutFailingAlertRepository.Wrap(
                new AlertRepository(ctx, NullLogger<AlertRepository>.Instance)),
            NullLogger<CupoAlertService>.Instance);

        var alertRepo = new StubAlertRepository { AlertsToReturn = [Alert(), Alert(severity: "Medium")] };

        var body = BodyOf(await BuildSut(alertRepo, new StubEntityRepository(), new StubAnalysisResultRepository(), cupoAlerts)
            .GetAlertsAsync(null, CancellationToken.None));

        Assert.Equal(2, body.TotalCount);
        Assert.Equal(1, body.HighCount);
        Assert.Equal(1, body.MediumCount);
    }

    /// <summary>The request's own cancellation is not swallowed.</summary>
    [Fact]
    public async Task GetAlerts_WhenTheRequestIsCancelled_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var alertRepo = new StubAlertRepository();
        var cupoAlerts = new RecordingCupoAlertService(alertRepo)
        {
            ThrowOnReconcile = new OperationCanceledException(cts.Token)
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BuildSut(alertRepo, new StubEntityRepository(), new StubAnalysisResultRepository(), cupoAlerts)
                .GetAlertsAsync(null, cts.Token));
    }
}
