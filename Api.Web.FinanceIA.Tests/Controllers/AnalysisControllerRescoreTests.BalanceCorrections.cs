// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-02

using System.Text.Json;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Tests.AnalysisRuns;
using Api.Web.FinanceIA.Tests.BalanceCorrections;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// The HTTP surface of the balance correction (Phase 10-F3a): POST and GET {id}/balance-corrections and
/// the <c>balanceCorrections</c> block of GET {id}, over the same world and controller as the re-score.
/// </summary>
public sealed partial class AnalysisControllerRescoreTests
{
    // ── POST /api/analyses/{id}/balance-corrections ─────────────────────────────

    [Fact]
    public async Task Correction_answers_the_rescore_body_with_the_balance_corrections_block()
    {
        var seeded = await SeedFinexAsync();
        var (sut, _) = BuildSut();

        var result = await sut.CorrectBalanceAsync(seeded.ResultId, FinexRequest(), _world.Service(), CancellationToken.None);

        var ok       = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<AnalysisResultResponse>(ok.Value);
        Assert.NotNull(response.Rescore);
        Assert.Equal(response.AiSuggestedScore, response.Rescore!.NewScore);

        var corrections = response.BalanceCorrections!;
        Assert.Equal(1, corrections.Count);
        Assert.Equal(_world.Clock.Now.UtcDateTime, corrections.LastAt);
        Assert.Equal(AnalystName, corrections.LastByName);
        Assert.Equal(BalanceCorrectionsResponse.CorrectedAfterReadingNote, corrections.Note);

        Assert.True(response.ExtractionIntegrity!.Passed);
        Assert.Contains(response.ScoreReliability!.Reasons, r => r.Code == "manual_correction");
    }

    [Fact]
    public async Task An_invalid_request_is_400_with_every_field_path_and_reads_nothing()
    {
        var seeded = await SeedFinexAsync();
        var (sut, _) = BuildSut();
        var request  = new BalanceCorrectionRequest
        {
            Reason      = "corto",
            Corrections = [Item("balance_check.current_assets_items[1].amount", "0"), Item("equity", "\"1\"")]
        };

        var result = await sut.CorrectBalanceAsync(seeded.ResultId, request, _world.Service(), CancellationToken.None);

        var problem = AssertProblem(result, 400, "validation_error");
        Assert.Equal(["reason", "corrections[0].path", "corrections[1].value"], ErrorFields(problem));
        Assert.Equal(0, _world.Bcra.Calls);
        Assert.Equal(seeded.BalanceJson, (await _world.ReadResultAsync(seeded.ResultId))!.BalanceJson);
    }

    [Fact]
    public async Task An_unchanged_figure_is_400_sin_cambios_on_its_value_field()
    {
        var seeded = await SeedFinexAsync();
        var (sut, _) = BuildSut();
        var request  = new BalanceCorrectionRequest
        {
            Reason      = BalanceCorrectionFixtures.Reason,
            Corrections = [Item("equity", "300000000")]
        };

        var problem = AssertProblem(
            await sut.CorrectBalanceAsync(seeded.ResultId, request, _world.Service(), CancellationToken.None), 400, "validation_error");

        var error = Assert.Single(Errors(problem));
        Assert.Equal("corrections[0].value", error.Field);
        Assert.Contains("sin cambios", error.Message);
    }

    [Fact]
    public async Task A_persona_humana_analysis_is_409_person_analysis_not_correctable()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, persona, _) = await _world.SeedAnalysisAsync(entity,
            a => { a.AnalysisKind = AnalysisKinds.PersonaHumana; a.DocumentId = null; },
            r => r.BalanceJson = null);
        var (sut, _) = BuildSut();

        var problem = AssertProblem(
            await sut.CorrectBalanceAsync(persona.ResultId, FinexRequest(), _world.Service(), CancellationToken.None),
            409, RescoreErrorCodes.PersonAnalysisNotCorrectable);
        Assert.Equal(RescoreEligibility.Detail(RescoreErrorCodes.PersonAnalysisNotCorrectable), problem.Detail);
    }

    [Fact]
    public async Task A_decided_analysis_is_the_rescores_409()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, decided, _) = await _world.SeedAnalysisAsync(entity,
            a => a.Status = AnalysisStatuses.Approved,
            r => r.BalanceJson = BalanceCorrectionFixtures.FinexBalanceJson());
        var (sut, _) = BuildSut();

        AssertProblem(
            await sut.CorrectBalanceAsync(decided.ResultId, FinexRequest(), _world.Service(), CancellationToken.None),
            409, RescoreErrorCodes.NotPending);
    }

    [Fact]
    public async Task The_cooldown_is_429_with_retry_after()
    {
        var seeded = await SeedFinexAsync(r => r.RescoredAt = _world.Clock.Now.UtcDateTime.AddSeconds(-30));
        var (sut, http) = BuildSut();

        var problem = AssertProblem(
            await sut.CorrectBalanceAsync(seeded.ResultId, FinexRequest(), _world.Service(), CancellationToken.None),
            429, RescoreErrorCodes.TooManyRequests);

        Assert.Equal("90", http.Response.Headers.RetryAfter.ToString());
        Assert.Equal(90, problem.Extensions["retryAfter"]);
    }

    [Fact]
    public async Task An_unknown_result_is_404()
    {
        var (sut, _) = BuildSut();

        var result = await sut.CorrectBalanceAsync(Guid.NewGuid(), FinexRequest(), _world.Service(), CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    // ── GET /api/analyses/{id}/balance-corrections ──────────────────────────────

    [Fact]
    public async Task The_history_lists_each_corrected_figure_newest_first_with_the_editor()
    {
        var seeded = await SeedFinexAsync();
        Assert.IsType<OkObjectResult>(await BuildSut().Sut.CorrectBalanceAsync(
            seeded.ResultId, FinexRequest(), _world.Service(), CancellationToken.None));
        _world.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.IsType<OkObjectResult>(await BuildSut().Sut.CorrectBalanceAsync(
            seeded.ResultId,
            new BalanceCorrectionRequest { Reason = BalanceCorrectionFixtures.Reason, Corrections = [Item("prepaid_expenses", "1500.5")] },
            _world.Service(), CancellationToken.None));

        var items = await HistoryAsync(seeded.ResultId);

        Assert.Equal(["prepaid_expenses", BalanceCorrectionFixtures.MisreadPath], items.Select(i => i.FieldPath));
        Assert.Null(items[0].OldValue);
        Assert.Equal(1500.5, items[0].NewValue);
        Assert.Equal(BalanceCorrectionFixtures.MisreadAmount, items[1].OldValue);
        Assert.Equal(0d, items[1].NewValue);
        Assert.All(items, i => Assert.Equal(AnalystName, i.EditedByName));
        Assert.All(items, i => Assert.Equal(_world.UserId.ToString(), i.EditedBy));
        Assert.All(items, i => Assert.Equal(BalanceCorrectionFixtures.Reason, i.Reason));

        // The detail counts distinct figures.
        Assert.Equal(2, (await GetDetailAsync(seeded.ResultId)).BalanceCorrections!.Count);
    }

    [Fact]
    public async Task The_history_is_empty_when_nothing_was_corrected_and_404_for_an_unknown_result()
    {
        var seeded = await SeedFinexAsync();

        Assert.Empty(await HistoryAsync(seeded.ResultId));

        var (sut, _) = BuildSut();
        var db       = _world.NewContext();
        var missing  = await sut.GetBalanceCorrectionsAsync(
            Guid.NewGuid(),
            new AnalysisRescoreRepository(db, NullLogger<AnalysisRescoreRepository>.Instance),
            new AnalysisEditRepository(db),
            CancellationToken.None);
        Assert.Equal(404, Assert.IsType<ObjectResult>(missing).StatusCode);
    }

    // ── GET /api/analyses/{id} ──────────────────────────────────────────────────

    [Fact]
    public async Task The_detail_has_no_balance_corrections_block_until_one_is_made()
    {
        var seeded = await SeedFinexAsync();

        Assert.Null((await GetDetailAsync(seeded.ResultId)).BalanceCorrections);
    }

    [Fact]
    public void The_list_item_never_carries_the_block()
        => Assert.Null(typeof(AnalysisListItemResponse).GetProperty(nameof(AnalysisResultResponse.BalanceCorrections)));

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private async Task<AnalysisResult> SeedFinexAsync(Action<AnalysisResult>? configure = null)
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await _world.SeedAnalysisAsync(entity, configureResult: r =>
        {
            r.BalanceJson = BalanceCorrectionFixtures.FinexBalanceJson();
            configure?.Invoke(r);
        });
        return seeded;
    }

    private async Task<List<BalanceCorrectionHistoryResponse>> HistoryAsync(Guid resultId)
    {
        var (sut, _) = BuildSut();
        var db       = _world.NewContext();
        var ok       = Assert.IsType<OkObjectResult>(await sut.GetBalanceCorrectionsAsync(
            resultId,
            new AnalysisRescoreRepository(db, NullLogger<AnalysisRescoreRepository>.Instance),
            new AnalysisEditRepository(db),
            CancellationToken.None));
        return Assert.IsType<List<BalanceCorrectionHistoryResponse>>(ok.Value);
    }

    private static BalanceCorrectionRequest FinexRequest() => new()
    {
        Reason      = BalanceCorrectionFixtures.Reason,
        Corrections = [Item(BalanceCorrectionFixtures.MisreadPath, "0")]
    };

    private static BalanceCorrectionItemRequest Item(string path, string valueJson)
        => new() { Path = path, Value = JsonDocument.Parse(valueJson).RootElement.Clone() };

    private static List<(string Field, string Message)> Errors(ProblemDetails problem)
        => JsonDocument.Parse(JsonSerializer.Serialize(problem.Extensions["errors"]))
            .RootElement.EnumerateArray()
            .Select(e => (e.GetProperty("field").GetString()!, e.GetProperty("message").GetString()!))
            .ToList();

    private static List<string> ErrorFields(ProblemDetails problem) => Errors(problem).Select(e => e.Field).ToList();
}
