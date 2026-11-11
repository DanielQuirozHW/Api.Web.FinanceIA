// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-22

using System.Security.Claims;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Api.Web.FinanceIA.Tests.AnalysisRuns;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// The HTTP surface of the re-score: POST {id}/rescore, GET {id}/rescores and the re-score fields of
/// GET {id}. The controller runs over the REAL repositories of a <see cref="RescoreTestWorld"/>, so the
/// detail is read back through the same projection production uses.
/// </summary>
public sealed partial class AnalysisControllerRescoreTests
{
    private const string FirebaseUid = "firebase-uid-analyst";
    private const string AnalystName = "Analista Uno";

    private readonly RescoreTestWorld _world = new();

    // ── GET /api/analyses/{id} ──────────────────────────────────────────────────

    [Fact]
    public async Task Detail_of_a_pending_balance_analysis_can_be_rescored_and_dates_its_sources()
    {
        var entity = await _world.SeedEntityAsync();
        var fresh  = await _world.RunPipelineAsync(entity);

        var detail = await GetDetailAsync(fresh.ResultId);

        Assert.True(detail.CanRescore);
        Assert.Null(detail.RescoreBlockedReason);
        Assert.Equal(0, detail.RescoreCount);
        Assert.Null(detail.RescoredAt);
        Assert.Null(detail.RescoredByName);
        Assert.Null(detail.Rescore);

        // Never re-scored: the BCRA was live at generation, the snapshots carry the entity's dates.
        var sources = detail.Sources!;
        Assert.Equal(["BCRA", "LUFE", "CASFOG", "ARCA", "NOSIS"], sources.Select(s => s.Source));
        var bcra = sources.Single(s => s.Source == RescoreSource.Bcra);
        Assert.True(bcra.Live);
        Assert.Equal(fresh.GeneratedAt, bcra.AsOf);
        Assert.Equal(entity.LufeDataUpdatedAt,   sources.Single(s => s.Source == RescoreSource.Lufe).AsOf);
        Assert.Equal(entity.CasfogDataUpdatedAt, sources.Single(s => s.Source == RescoreSource.Casfog).AsOf);
        Assert.Equal(entity.FiscalDataUpdatedAt, sources.Single(s => s.Source == RescoreSource.Arca).AsOf);
    }

    [Fact]
    public async Task Detail_after_a_rescore_names_who_ran_it_and_reads_the_exact_sources()
    {
        var entity = await _world.SeedEntityAsync();
        var fresh  = await _world.RunPipelineAsync(entity);
        var (sut, _) = BuildSut();

        var post = Assert.IsType<OkObjectResult>(await sut.RescoreAsync(fresh.ResultId, _world.Service(), CancellationToken.None));
        var rescoredAt = Assert.IsType<AnalysisResultResponse>(post.Value).Rescore!.RescoredAt;

        var detail = await GetDetailAsync(fresh.ResultId);

        Assert.Equal(1, detail.RescoreCount);
        Assert.Equal(rescoredAt, detail.RescoredAt);
        Assert.Equal(_world.UserId.ToString(), detail.RescoredBy);
        Assert.Equal(AnalystName, detail.RescoredByName);
        Assert.Equal(rescoredAt, detail.Sources!.Single(s => s.Source == RescoreSource.Bcra).AsOf);
        Assert.Null(detail.Rescore);
    }

    [Fact]
    public async Task Detail_reports_why_an_analysis_cannot_be_rescored()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, approved, _)   = await _world.SeedAnalysisAsync(entity, a => a.Status = AnalysisStatuses.Approved);
        var (_, processing, _) = await _world.SeedAnalysisAsync(entity, documentStatus: "Processing");
        var (_, returned, _)   = await _world.SeedAnalysisAsync(entity, a => a.PreApprovedBy = Guid.NewGuid());

        await AssertBlockedAsync(approved.ResultId,   RescoreErrorCodes.NotPending);
        await AssertBlockedAsync(processing.ResultId, RescoreErrorCodes.Processing);
        await AssertBlockedAsync(returned.ResultId,   RescoreErrorCodes.HasVotes);
    }

    // ── POST /api/analyses/{id}/rescore ─────────────────────────────────────────

    [Fact]
    public async Task Post_answers_the_detail_plus_the_rescore_block()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await _world.SeedAnalysisAsync(entity);
        var (sut, _) = BuildSut();

        var ok = Assert.IsType<OkObjectResult>(await sut.RescoreAsync(seeded.ResultId, _world.Service(), CancellationToken.None));

        var body = Assert.IsType<AnalysisResultResponse>(ok.Value);
        Assert.Equal(seeded.ResultId, body.ResultId);
        Assert.NotNull(body.Rescore);
        Assert.Equal(40m, body.Rescore.PreviousScore);
        Assert.Equal(body.AiSuggestedScore, body.Rescore.NewScore);
        Assert.Equal("Degraded", body.Rescore.PreviousEnrichmentStatus);
        Assert.Equal("Complete", body.Rescore.NewEnrichmentStatus);
        Assert.Equal(5, body.Rescore.Sources.Count);
        Assert.Equal("Complete", body.EnrichmentStatus);
        Assert.Equal(1, body.RescoreCount);
        Assert.Equal(0, _world.Claude.AnalyzeCalls);
    }

    [Fact]
    public async Task Post_with_the_BCRA_down_is_503_with_Retry_After()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await _world.SeedAnalysisAsync(entity);
        _world.Bcra.Throws = new HttpRequestException("down");
        var (sut, http) = BuildSut();

        var result = await sut.RescoreAsync(seeded.ResultId, _world.Service(), CancellationToken.None);

        var problem = AssertProblem(result, 503, RescoreErrorCodes.BcraUnavailable);
        Assert.Equal("El BCRA sigue sin responder. El análisis no se modificó; probá de nuevo en unos minutos.", problem.Detail);
        Assert.Equal("60", http.Response.Headers.RetryAfter.ToString());
    }

    [Fact]
    public async Task Post_inside_the_cooldown_is_429_with_Retry_After()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await _world.SeedAnalysisAsync(entity);
        var (first, _) = BuildSut();
        Assert.IsType<OkObjectResult>(await first.RescoreAsync(seeded.ResultId, _world.Service(), CancellationToken.None));

        _world.Clock.Advance(TimeSpan.FromSeconds(20));
        var (sut, http) = BuildSut();
        var result = await sut.RescoreAsync(seeded.ResultId, _world.Service(), CancellationToken.None);

        AssertProblem(result, 429, RescoreErrorCodes.TooManyRequests);
        Assert.Equal("100", http.Response.Headers.RetryAfter.ToString());
    }

    [Fact]
    public async Task Post_on_a_voted_analysis_is_409_with_its_errorCode()
    {
        var entity = await _world.SeedEntityAsync();
        var (analysis, seeded, _) = await _world.SeedAnalysisAsync(entity);
        await _world.AddVoteAsync(analysis.AnalysisId);
        var (sut, http) = BuildSut();

        var result = await sut.RescoreAsync(seeded.ResultId, _world.Service(), CancellationToken.None);

        AssertProblem(result, 409, RescoreErrorCodes.HasVotes);
        Assert.Equal(0, http.Response.Headers.RetryAfter.Count);
    }

    [Fact]
    public async Task Post_on_an_unknown_result_is_404()
    {
        var (sut, _) = BuildSut();

        var result = await sut.RescoreAsync(Guid.NewGuid(), _world.Service(), CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    // ── GET /api/analyses/{id}/rescores ─────────────────────────────────────────

    [Fact]
    public async Task History_lists_newest_first_with_names()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await _world.SeedAnalysisAsync(entity);

        var (sut, _) = BuildSut();
        Assert.IsType<OkObjectResult>(await sut.RescoreAsync(seeded.ResultId, _world.Service(), CancellationToken.None));
        _world.Clock.Advance(TimeSpan.FromMinutes(10));
        (sut, _) = BuildSut();
        Assert.IsType<OkObjectResult>(await sut.RescoreAsync(seeded.ResultId, _world.Service(), CancellationToken.None));

        (sut, _) = BuildSut();
        var ok = Assert.IsType<OkObjectResult>(await sut.GetRescoresAsync(seeded.ResultId, _world.Service(), CancellationToken.None));

        var items = Assert.IsAssignableFrom<IReadOnlyList<AnalysisRescoreHistoryResponse>>(ok.Value);
        Assert.Equal(2, items.Count);
        Assert.True(items[0].RescoredAt > items[1].RescoredAt);
        Assert.All(items, i => Assert.Equal(AnalystName, i.RescoredByName));
        Assert.Equal(40m, items[1].PreviousScore);
    }

    [Fact]
    public async Task History_of_an_unknown_result_is_404_and_of_a_never_rescored_one_is_empty()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await _world.SeedAnalysisAsync(entity);
        var (sut, _) = BuildSut();

        Assert.Equal(404, Assert.IsType<ObjectResult>(
            await sut.GetRescoresAsync(Guid.NewGuid(), _world.Service(), CancellationToken.None)).StatusCode);

        var ok = Assert.IsType<OkObjectResult>(await sut.GetRescoresAsync(seeded.ResultId, _world.Service(), CancellationToken.None));
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<AnalysisRescoreHistoryResponse>>(ok.Value));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    // ── Persona humana: the same endpoint re-evaluates ──────────────────────────

    [Fact]
    public async Task Detail_of_a_persona_analysis_reports_canRescore_by_the_decision_rules()
    {
        var entity = await _world.SeedEntityAsync();
        static void Persona(Analysis a) { a.AnalysisKind = AnalysisKinds.PersonaHumana; a.DocumentId = null; }
        var (_, pending, _)     = await _world.SeedAnalysisAsync(entity, Persona, r => r.BalanceJson = null);
        var (_, approved, _)    = await _world.SeedAnalysisAsync(entity, a => { Persona(a); a.Status = AnalysisStatuses.Approved; }, r => r.BalanceJson = null);
        var (_, preApproved, _) = await _world.SeedAnalysisAsync(entity, a => { Persona(a); a.PreApprovedBy = Guid.NewGuid(); }, r => r.BalanceJson = null);

        var detail = await GetDetailAsync(pending.ResultId);
        Assert.Equal(AnalysisKinds.PersonaHumana, detail.AnalysisKind);
        Assert.True(detail.CanRescore);
        Assert.Null(detail.RescoreBlockedReason);

        await AssertBlockedAsync(approved.ResultId,    RescoreErrorCodes.NotPending);
        await AssertBlockedAsync(preApproved.ResultId, RescoreErrorCodes.HasVotes);
    }

    [Fact]
    public async Task Post_a_persona_refusal_keeps_its_detail_and_extensions()
    {
        var (sut, _) = BuildSut();
        var service = new CannedRescoreService(AnalysisRescoreOutcome.Refused(new PersonEvaluationRefusal(
            409, "Conflict", PersonEvaluationErrorCodes.FiadorLegajoNotReady, "El fiador no está activo.",
            Guid.Parse("11111111-1111-1111-1111-111111111111"), PersonEvaluationErrorCodes.FiadorReasons.NotActive)));

        var problem = AssertProblem(await sut.RescoreAsync(Guid.NewGuid(), service, CancellationToken.None),
            409, PersonEvaluationErrorCodes.FiadorLegajoNotReady);

        Assert.Equal("Conflict", problem.Title);
        Assert.Equal("El fiador no está activo.", problem.Detail);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), problem.Extensions["fiadorId"]);
        Assert.Equal(PersonEvaluationErrorCodes.FiadorReasons.NotActive, problem.Extensions["reason"]);
    }

    [Fact]
    public async Task Post_a_persona_404_with_an_errorCode_is_not_mistaken_for_an_unknown_result()
    {
        var (sut, _) = BuildSut();
        var service = new CannedRescoreService(AnalysisRescoreOutcome.Refused(new PersonEvaluationRefusal(
            404, "Not Found", PersonEvaluationErrorCodes.EntityNotFound, "No se encontró la entidad.")));

        var problem = AssertProblem(await sut.RescoreAsync(Guid.NewGuid(), service, CancellationToken.None),
            404, PersonEvaluationErrorCodes.EntityNotFound);

        Assert.Equal("No se encontró la entidad.", problem.Detail);
    }

    [Fact]
    public async Task Post_and_history_of_a_balance_analysis_keep_their_exact_shape()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await _world.SeedAnalysisAsync(entity);
        var (sut, _) = BuildSut();

        var ok   = Assert.IsType<OkObjectResult>(await sut.RescoreAsync(seeded.ResultId, _world.Service(), CancellationToken.None));
        var body = Assert.IsType<AnalysisResultResponse>(ok.Value);
        Assert.Equal(
            ["previousScore", "newScore", "previousLevel", "newLevel", "previousEnrichmentStatus", "newEnrichmentStatus", "rescoredAt", "sources"],
            Keys(body.Rescore!));

        (sut, _) = BuildSut();
        var history = Assert.IsType<OkObjectResult>(await sut.GetRescoresAsync(seeded.ResultId, _world.Service(), CancellationToken.None));
        var item = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<AnalysisRescoreHistoryResponse>>(history.Value));
        Assert.DoesNotContain("previousSuggestion", Keys(item));
        Assert.DoesNotContain("newSuggestion", Keys(item));
    }

    [Fact]
    public async Task History_of_a_persona_analysis_carries_the_suggestions()
    {
        var (sut, _) = BuildSut();
        var service = new CannedRescoreService(history:
        [
            new AnalysisRescoreHistoryItem(Guid.NewGuid(), DateTime.UtcNow, _world.UserId, null, null, null, null,
                "Degraded", "Complete", PersonSuggestions.RechazoSugerido, PersonSuggestions.ParaDecisionDelAnalista)
        ]);

        var ok   = Assert.IsType<OkObjectResult>(await sut.GetRescoresAsync(Guid.NewGuid(), service, CancellationToken.None));
        var item = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<AnalysisRescoreHistoryResponse>>(ok.Value));

        Assert.Equal(PersonSuggestions.RechazoSugerido, item.PreviousSuggestion);
        Assert.Equal(PersonSuggestions.ParaDecisionDelAnalista, item.NewSuggestion);
        Assert.Contains("previousSuggestion", Keys(item));
        Assert.Null(item.PreviousScore);
    }

    /// <summary>The property names <paramref name="value"/> serialises with, as the API writes them.</summary>
    private static List<string> Keys<T>(T value)
        => System.Text.Json.JsonDocument
            .Parse(System.Text.Json.JsonSerializer.Serialize(value, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)))
            .RootElement.EnumerateObject().Select(p => p.Name).ToList();

    /// <summary>A re-score service that answers what the test says, for the HTTP mapping alone.</summary>
    private sealed class CannedRescoreService(
        AnalysisRescoreOutcome? outcome = null, IReadOnlyList<AnalysisRescoreHistoryItem>? history = null)
        : IAnalysisRescoreService
    {
        public Task<AnalysisRescoreOutcome> RescoreAsync(
            Guid resultId, string tenantId, Guid userId, string? ipAddress, string? requestId, CancellationToken ct)
            => Task.FromResult(outcome ?? throw new InvalidOperationException("no outcome configured"));

        public Task<AnalysisRescoreOutcome> CorrectBalanceAsync(
            Guid resultId, string tenantId, Guid userId, Api.Web.FinanceIA.Servicios.BalanceCorrections.BalanceCorrectionCommand command,
            string? ipAddress, string? requestId, CancellationToken ct)
            => Task.FromResult(outcome ?? throw new InvalidOperationException("no outcome configured"));

        public Task<IReadOnlyList<AnalysisRescoreHistoryItem>?> GetHistoryAsync(Guid resultId, string tenantId, CancellationToken ct)
            => Task.FromResult(history);
    }

    private async Task<AnalysisResultResponse> GetDetailAsync(Guid resultId)
    {
        var (sut, _) = BuildSut();
        var ok = Assert.IsType<OkObjectResult>(await sut.GetById(resultId, CancellationToken.None));
        return Assert.IsType<AnalysisResultResponse>(ok.Value);
    }

    private async Task AssertBlockedAsync(Guid resultId, string errorCode)
    {
        var detail = await GetDetailAsync(resultId);
        Assert.False(detail.CanRescore);
        Assert.Equal(errorCode, detail.RescoreBlockedReason);
    }

    private static ProblemDetails AssertProblem(IActionResult result, int status, string errorCode)
    {
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(status, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(errorCode, problem.Extensions["errorCode"]);
        Assert.EndsWith("/" + errorCode, problem.Type);
        return problem;
    }

    /// <summary>One request's controller: a fresh context, the real repositories, the world's documents.</summary>
    private (AnalysisController Sut, DefaultHttpContext Http) BuildSut()
    {
        var db    = _world.NewContext();
        var users = new NamedUserRepository(new User { UserId = _world.UserId, DisplayName = AnalystName, TenantId = RescoreTestWorld.Tenant });

        var sut = new AnalysisController(
            new TenantAccessor(),                                                          // tenantContextAccessor
            _world.Documents,                                                              // documentRepository
            new AnalysisResultRepository(db, NullLogger<AnalysisResultRepository>.Instance),
            new AnalysisRepository(db, NullLogger<AnalysisRepository>.Instance),
            null!,                                                                         // claudeService
            new CashFlowReconciliationService(),                                           // cashFlowReconciliationService
            new CashFlowNarrativeService(),                                                // cashFlowNarrativeService
            new AlertRatioReconciliationService(),                                         // alertRatioReconciliationService
            new EntityRepository(db, NullLogger<EntityRepository>.Instance),               // entityRepository
            users,                                                                         // userRepository
            null!,                                                                         // chatRepository
            new CreditLineRepository(db, NullLogger<CreditLineRepository>.Instance),       // creditLineRepository
            null!,                                                                         // operationRepository
            null!,                                                                         // auditService
            null!,                                                                         // apiCallLogService
            null!,                                                                         // excelCashFlowParser
            null!,                                                                         // billingOptions
            null!,                                                                         // tenantBillingConfigService
            null!,                                                                         // userApprovalLimitRepository
            null!,                                                                         // decisionNotifier
            null!,                                                                         // approvalQuorumService — never PreApproved here
            new Api.Web.FinanceIA.Tests.Legajo.FakeLegajoDocumentRepository(),
            null!,                                                                         // analysisPipeline
            null!,                                                                         // analysisJobQueue
            null!,                                                                         // analysisRunRegistry
            null!,                                                                         // analysisRunStateRepository
            new Api.Web.FinanceIA.Tests.PersonAnalysis.StubPersonAnalysisRepository(),
            NullLogger<AnalysisController>.Instance);

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("uid", FirebaseUid) }, "Test"))
        };
        sut.ControllerContext = new ControllerContext { HttpContext = http };
        return (sut, http);
    }

    private sealed class TenantAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; } = new()
        {
            TenantId               = RescoreTestWorld.Tenant,
            FirebaseProjectId      = "proj",
            FirebaseCredentialJson = "{}",
            RecaptchaSecretKey     = "secret"
        };
    }

    private sealed class NamedUserRepository(User user) : IUserRepository
    {
        public Task<User?> GetByFirebaseUidAsync(string firebaseUid, CancellationToken ct)
            => Task.FromResult<User?>(firebaseUid == FirebaseUid ? user : null);

        public Task<IReadOnlyList<User>> GetByIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<User>>(userIds.Contains(user.UserId) ? [user] : []);

        public Task<User?> GetByUserIdAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<User?> GetByEmailAsync(string email, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> ExistsByFirebaseUidAsync(string firebaseUid, CancellationToken ct) => throw new NotImplementedException();
        public Task<User> CreateAsync(User user, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateLastLoginAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateOnboardingFlagsAsync(Guid userId, string? flagsJson, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<User>> GetActiveByTenantAsync(string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateTeamAsync(Guid userId, Guid? teamId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> ClearTeamAsync(Guid teamId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
    }

    // ── Score reliability (score credibility 1b-2b) ─────────────────────────

    /// <summary>
    /// The re-score answers the verdict of the NEW score: the breakdown the re-score just wrote (facts
    /// included) judged at its RescoredAt — the same verdict a later GET returns.
    /// </summary>
    [Fact]
    public async Task Post_rescore_carries_the_score_reliability_of_the_new_score()
    {
        var entity = await _world.SeedEntityAsync();
        var (analysis, seeded, _) = await _world.SeedAnalysisAsync(entity);
        var (sut, _) = BuildSut();

        var ok   = Assert.IsType<OkObjectResult>(await sut.RescoreAsync(seeded.ResultId, _world.Service(), CancellationToken.None));
        var body = Assert.IsType<AnalysisResultResponse>(ok.Value);

        var persisted = (await _world.ReadResultAsync(seeded.ResultId))!;
        Assert.NotNull(persisted.RescoredAt);

        var expected = Api.Web.FinanceIA.Servicios.ScoreReliability.ScoreReliabilityAdapter.Evaluate(
            new Api.Web.FinanceIA.Servicios.ScoreReliability.ScoreReliabilityRow(
                persisted.AiSuggestedScore, persisted.EnrichmentStatus, analysis.AnalysisKind,
                analysis.FiscalYear, persisted.GeneratedAt, persisted.RescoredAt),
            persisted.ScoreBreakdownJson).Reliability;

        Assert.NotNull(body.ScoreReliability);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expected), System.Text.Json.JsonSerializer.Serialize(body.ScoreReliability));
        // The seeded row predates the facts; the re-score recorded them.
        Assert.DoesNotContain(body.ScoreReliability!.Reasons, r => r.Code == "facts_unavailable");

        var detail = await GetDetailAsync(seeded.ResultId);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(body.ScoreReliability), System.Text.Json.JsonSerializer.Serialize(detail.ScoreReliability));
    }
}
