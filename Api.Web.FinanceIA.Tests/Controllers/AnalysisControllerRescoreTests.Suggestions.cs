// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-03

using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.BalanceCorrections;
using Api.Web.FinanceIA.Tests.AnalysisRuns;
using Api.Web.FinanceIA.Tests.BalanceCorrections;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// The HTTP surface of "Sugerir corrección" (Phase 10-F4a): POST {id}/balance-corrections/suggestions, and
/// the <c>origin</c> an accepted suggestion carries through POST and GET {id}/balance-corrections.
/// </summary>
public sealed partial class AnalysisControllerRescoreTests
{
    // One cache across the calls of a test, as the singleton IMemoryCache is across requests.
    private readonly MemoryCache _suggestionCache = new(new MemoryCacheOptions());

    // ── POST /api/analyses/{id}/balance-corrections/suggestions ─────────────────

    [Fact]
    public void Refresh_is_an_optional_query_parameter()
    {
        var method = typeof(AnalysisController).GetMethod(nameof(AnalysisController.SuggestBalanceCorrectionsAsync))!;

        var refresh = Assert.Single(method.GetParameters(), p => p.Name == "refresh");
        Assert.Equal(typeof(bool), refresh.ParameterType);
        Assert.Single(refresh.GetCustomAttributes(typeof(FromQueryAttribute), false));
    }

    [Fact]
    public async Task A_second_open_is_served_from_the_cache_and_refresh_asks_again()
    {
        var seeded = await SeedFinexAsync();
        _world.Claude.Suggestion = _ => BalanceSuggestionFixtures.Reply(
            BalanceSuggestionFixtures.FinexFixAnswer(), [BalanceSuggestionFixtures.Image(1, 1, RenderedImage.FullHalf)]);
        var (sut, _) = BuildSut();

        var first  = await sut.SuggestBalanceCorrectionsAsync(seeded.ResultId, refresh: false, SuggestionService(), CancellationToken.None);
        var second = await sut.SuggestBalanceCorrectionsAsync(seeded.ResultId, refresh: false, SuggestionService(), CancellationToken.None);

        Assert.Null(Assert.IsType<BalanceCorrectionSuggestionResponse>(Assert.IsType<OkObjectResult>(first).Value).CachedAt);
        Assert.NotNull(Assert.IsType<BalanceCorrectionSuggestionResponse>(Assert.IsType<OkObjectResult>(second).Value).CachedAt);
        Assert.Single(_world.Claude.SuggestionCalls);

        var refreshed = await sut.SuggestBalanceCorrectionsAsync(seeded.ResultId, refresh: true, SuggestionService(), CancellationToken.None);

        Assert.Null(Assert.IsType<BalanceCorrectionSuggestionResponse>(Assert.IsType<OkObjectResult>(refreshed).Value).CachedAt);
        Assert.Equal(2, _world.Claude.SuggestionCalls.Count);
    }

    [Fact]
    public void The_suggestion_route_is_post_for_analysis_creators()
    {
        var method = typeof(AnalysisController).GetMethod(nameof(AnalysisController.SuggestBalanceCorrectionsAsync))!;

        var post = Assert.Single(method.GetCustomAttributes(typeof(HttpPostAttribute), false).Cast<HttpPostAttribute>());
        Assert.Equal("{id:guid}/balance-corrections/suggestions", post.Template);
        var authorize = Assert.Single(method.GetCustomAttributes(typeof(AuthorizeAttribute), false).Cast<AuthorizeAttribute>());
        Assert.Equal(Roles.AnalysisCreators, authorize.Roles);
    }

    [Fact]
    public async Task A_suggestion_answers_200_with_the_verified_proposal()
    {
        var seeded = await SeedFinexAsync();
        _world.Claude.Suggestion = _ => BalanceSuggestionFixtures.Reply(
            BalanceSuggestionFixtures.FinexFixAnswer(), [BalanceSuggestionFixtures.Image(1, 1, RenderedImage.FullHalf)]);
        var (sut, _) = BuildSut();

        var result = await sut.SuggestBalanceCorrectionsAsync(seeded.ResultId, refresh: false, SuggestionService(), CancellationToken.None);

        var response = Assert.IsType<BalanceCorrectionSuggestionResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(BalanceSuggestionOutcomes.Fixes, response.Outcome);
        Assert.Equal(BalanceCorrectionFixtures.MisreadPath, Assert.Single(response.Suggestions).Path);

        // Nothing applied.
        Assert.Equal(seeded.BalanceJson, (await _world.ReadResultAsync(seeded.ResultId))!.BalanceJson);
    }

    [Fact]
    public async Task A_balance_that_adds_up_is_409_suggestion_not_applicable()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, sound, _) = await _world.SeedAnalysisAsync(entity,
            configureResult: r => r.BalanceJson = BalanceCorrectionFixtures.CorrectedFigures());
        var (sut, _) = BuildSut();

        var problem = AssertProblem(
            await sut.SuggestBalanceCorrectionsAsync(sound.ResultId, refresh: false, SuggestionService(), CancellationToken.None),
            409, RescoreErrorCodes.SuggestionNotApplicable);
        Assert.False(string.IsNullOrWhiteSpace(problem.Detail));
        Assert.Empty(_world.Claude.SuggestionCalls);
    }

    [Fact]
    public async Task A_persona_humana_analysis_is_409_with_the_corrections_detail()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, persona, _) = await _world.SeedAnalysisAsync(entity,
            a => { a.AnalysisKind = AnalysisKinds.PersonaHumana; a.DocumentId = null; },
            r => r.BalanceJson = null);
        var (sut, _) = BuildSut();

        var problem = AssertProblem(
            await sut.SuggestBalanceCorrectionsAsync(persona.ResultId, refresh: false, SuggestionService(), CancellationToken.None),
            409, RescoreErrorCodes.PersonAnalysisNotCorrectable);
        Assert.Equal(RescoreEligibility.Detail(RescoreErrorCodes.PersonAnalysisNotCorrectable), problem.Detail);
    }

    [Fact]
    public async Task A_model_failure_is_502_suggestion_unavailable_without_details()
    {
        var seeded = await SeedFinexAsync();
        _world.Claude.Suggestion = _ => new BalanceSuggestionReply
        {
            Model = BalanceSuggestionFixtures.Model, RequestSent = true, HttpStatus = 529, FailureCode = "http_529"
        };
        var (sut, _) = BuildSut();

        var problem = AssertProblem(
            await sut.SuggestBalanceCorrectionsAsync(seeded.ResultId, refresh: false, SuggestionService(), CancellationToken.None),
            502, RescoreErrorCodes.SuggestionUnavailable);
        Assert.DoesNotContain("529", problem.Detail);
        Assert.DoesNotContain("http_", problem.Detail);
    }

    [Fact]
    public async Task An_unknown_result_is_404_for_the_suggestion()
    {
        var (sut, _) = BuildSut();

        var result = await sut.SuggestBalanceCorrectionsAsync(Guid.NewGuid(), refresh: false, SuggestionService(), CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    // ── origin ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_accepted_suggestion_stores_origin_ai_suggestion_and_the_history_exposes_it()
    {
        var seeded = await SeedFinexAsync();
        var request = FinexRequest();
        request.Origin = BalanceCorrectionOrigins.AiSuggestion;

        Assert.IsType<OkObjectResult>(await BuildSut().Sut.CorrectBalanceAsync(
            seeded.ResultId, request, _world.Service(), CancellationToken.None));
        _world.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.IsType<OkObjectResult>(await BuildSut().Sut.CorrectBalanceAsync(
            seeded.ResultId,
            new BalanceCorrectionRequest { Reason = BalanceCorrectionFixtures.Reason, Corrections = [Item("prepaid_expenses", "1500.5")] },
            _world.Service(), CancellationToken.None));

        var stored = JsonNode.Parse((await _world.ReadResultAsync(seeded.ResultId))!.BalanceJson!)!["manual_corrections"]!.AsArray();
        Assert.Equal([BalanceCorrectionOrigins.AiSuggestion, BalanceCorrectionOrigins.Manual],
            stored.Select(e => e!["origin"]!.GetValue<string>()));

        var history = await HistoryAsync(seeded.ResultId);
        Assert.Equal(["prepaid_expenses", BalanceCorrectionFixtures.MisreadPath], history.Select(h => h.FieldPath));
        Assert.Equal([BalanceCorrectionOrigins.Manual, BalanceCorrectionOrigins.AiSuggestion], history.Select(h => h.Origin));

        // Still a manual correction for the reliability verdict.
        Assert.Contains((await GetDetailAsync(seeded.ResultId)).ScoreReliability!.Reasons, r => r.Code == "manual_correction");
    }

    [Fact]
    public async Task An_unknown_origin_is_400_on_the_origin_field_and_reads_nothing()
    {
        var seeded  = await SeedFinexAsync();
        var request = FinexRequest();
        request.Origin = "robot";
        var (sut, _) = BuildSut();

        var problem = AssertProblem(
            await sut.CorrectBalanceAsync(seeded.ResultId, request, _world.Service(), CancellationToken.None),
            400, "validation_error");

        Assert.Equal(["origin"], ErrorFields(problem));
        Assert.DoesNotContain("robot", Errors(problem).Single().Message);
        Assert.Equal(0, _world.Bcra.Calls);
    }

    [Fact]
    public async Task A_history_row_without_a_trail_origin_reads_as_manual()
    {
        var seeded = await SeedFinexAsync();
        Assert.IsType<OkObjectResult>(await BuildSut().Sut.CorrectBalanceAsync(
            seeded.ResultId, FinexRequest(), _world.Service(), CancellationToken.None));

        // An entry written before `origin` existed.
        await using (var db = _world.NewContext())
        {
            var row  = db.AnalysisResults.Single(r => r.ResultId == seeded.ResultId);
            var root = JsonNode.Parse(row.BalanceJson!)!.AsObject();
            root["manual_corrections"]![0]!.AsObject().Remove("origin");
            row.BalanceJson = root.ToJsonString();
            await db.SaveChangesAsync();
        }

        Assert.Equal(BalanceCorrectionOrigins.Manual, Assert.Single(await HistoryAsync(seeded.ResultId)).Origin);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private BalanceCorrectionSuggestionService SuggestionService()
    {
        var db = _world.NewContext();
        return new BalanceCorrectionSuggestionService(
            new AnalysisResultRepository(db, NullLogger<AnalysisResultRepository>.Instance),
            new AnalysisRepository(db, NullLogger<AnalysisRepository>.Instance),
            _world.Documents,
            new AnalysisRescoreRepository(db, NullLogger<AnalysisRescoreRepository>.Instance),
            new FakeStorage(),
            _world.Claude,
            _world.ApiCalls,
            new BalanceIntegrityService(),
            _suggestionCache,
            new BalanceSuggestionInFlight(),
            NullLogger<BalanceCorrectionSuggestionService>.Instance);
    }
}
