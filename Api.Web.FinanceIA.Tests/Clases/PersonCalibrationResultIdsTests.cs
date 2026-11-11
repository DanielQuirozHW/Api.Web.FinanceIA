// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-31

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models.Financia;
using static Api.Web.FinanceIA.Tests.Clases.PersonCalibrationFixture;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// The ids the calibration board hands the Portal so it can link a case or a re-evaluation to the analysis:
/// the RESULT id (what GET /api/analyses/{id} takes, not the analysis id) and a re-evaluation's entity.
/// </summary>
public sealed class PersonCalibrationResultIdsTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static Task<PersonCalibrationResponse> BoardAsync(PersonCalibrationFixture f)
        => f.Service().GetBoardAsync(DashboardFixture.Tenant, October, null, DashboardFixture.Now, Ct);

    /// <summary>An approval against a Rechazo sugerido: always a flagged case.</summary>
    private static (Analysis Analysis, AnalysisResult Result) AddFlagged(
        PersonCalibrationFixture f, Guid analyst, Guid? entityId = null, Action<AnalysisResult>? result = null)
    {
        var (analysis, analysisResult) = f.Dashboard.AddAnalysis(analyst, entityId, a =>
        {
            a.AnalysisKind = AnalysisKinds.PersonaHumana;
            a.Status       = AnalysisStatuses.Approved;
            a.CreatedAt    = new DateTime(2026, 10, 10, 15, 0, 0, DateTimeKind.Utc);
        }, result);
        f.AddSolicitante(analysis.AnalysisId, [Filter("bcra_situacion", PersonFilterOutcomes.Rechazo)]);
        return (analysis, analysisResult);
    }

    [Fact]
    public async Task FlaggedCase_CarriesTheResultId_NotTheAnalysisId()
    {
        await using var f = PersonCalibrationFixture.Create(nameof(FlaggedCase_CarriesTheResultId_NotTheAnalysisId));
        var analyst = f.Dashboard.AddUser(Roles.Analyst);
        var (analysis, result) = AddFlagged(f, analyst.UserId);
        await f.SaveAsync();

        var flagged = Assert.Single((await BoardAsync(f)).Decisions!.FlaggedCases.Items);

        Assert.Equal(analysis.AnalysisId, flagged.AnalysisId);
        Assert.Equal(result.ResultId, flagged.ResultId);
        Assert.NotEqual(flagged.AnalysisId, flagged.ResultId);
    }

    [Fact]
    public async Task FlaggedCase_NeverTakesAResultRowOfAnotherTenant()
    {
        await using var f = PersonCalibrationFixture.Create(nameof(FlaggedCase_NeverTakesAResultRowOfAnotherTenant));
        var analyst = f.Dashboard.AddUser(Roles.Analyst);
        // This tenant's analysis, but its only result row is stamped with another tenant.
        var (analysis, _) = AddFlagged(f, analyst.UserId, result: r => r.TenantId = DashboardFixture.Other);
        await f.SaveAsync();

        var flagged = Assert.Single((await BoardAsync(f)).Decisions!.FlaggedCases.Items);

        Assert.Equal(analysis.AnalysisId, flagged.AnalysisId);
        Assert.Null(flagged.ResultId);
    }

    [Fact]
    public async Task FlaggedCases_StillIgnoreBalanceAnalyses_WithTheirResults()
    {
        await using var f = PersonCalibrationFixture.Create(nameof(FlaggedCases_StillIgnoreBalanceAnalyses_WithTheirResults));
        var analyst = f.Dashboard.AddUser(Roles.Analyst);
        var (persona, personaResult) = AddFlagged(f, analyst.UserId);

        // A balance analysis in range, approved, with a result and a rechazo-looking solicitante row.
        var (balance, _) = f.Dashboard.AddAnalysis(analyst.UserId, analysis: a =>
        {
            a.Status    = AnalysisStatuses.Approved;
            a.CreatedAt = new DateTime(2026, 10, 10, 15, 0, 0, DateTimeKind.Utc);
        });
        f.AddSolicitante(balance.AnalysisId, [Filter("bcra_situacion", PersonFilterOutcomes.Rechazo)]);
        await f.SaveAsync();

        var board = await BoardAsync(f);

        Assert.Equal(1, board.Summary!.Total);
        var flagged = Assert.Single(board.Decisions!.FlaggedCases.Items);
        Assert.Equal((persona.AnalysisId, personaResult.ResultId), (flagged.AnalysisId, flagged.ResultId));
    }

    [Fact]
    public async Task Reevaluation_CarriesItsResultId_AndTheAnalysisEntity()
    {
        await using var f = PersonCalibrationFixture.Create(nameof(Reevaluation_CarriesItsResultId_AndTheAnalysisEntity));
        var analyst = f.Dashboard.AddUser(Roles.Analyst);
        var entity  = f.Dashboard.AddEntity();

        var (analysis, result) = f.AddPersonAnalysis(analyst.UserId, entityId: entity.EntityId);
        f.AddSolicitante(analysis.AnalysisId);
        f.AddRescore(result, analyst.UserId, DashboardFixture.Now.AddDays(-1), PersonSuggestions.RechazoSugerido, PersonSuggestions.ParaDecisionDelAnalista);
        await f.SaveAsync();

        var recent = Assert.Single((await BoardAsync(f)).Reevaluations!.Recent);

        Assert.Equal(analysis.AnalysisId, recent.AnalysisId);
        Assert.Equal(result.ResultId, recent.ResultId);
        Assert.Equal(entity.EntityId, recent.EntityId);
    }

    [Fact]
    public async Task Reevaluations_OfAnotherTenant_NeverReachTheBoard()
    {
        await using var f = PersonCalibrationFixture.Create(nameof(Reevaluations_OfAnotherTenant_NeverReachTheBoard));
        var analyst = f.Dashboard.AddUser(Roles.Analyst);
        var (analysis, result) = f.AddPersonAnalysis(analyst.UserId);
        f.AddSolicitante(analysis.AnalysisId);
        // A row of another tenant pointing at this tenant's analysis: the tenant predicate keeps it out.
        f.Dashboard.AddRescore(result, analyst.UserId, DashboardFixture.Now.AddDays(-1), tenant: DashboardFixture.Other);
        await f.SaveAsync();

        var reevaluations = (await BoardAsync(f)).Reevaluations!;

        Assert.Equal(0, reevaluations.TotalReevaluations);
        Assert.Empty(reevaluations.Recent);
    }

    [Fact]
    public async Task Export_WritesTheResultId_RightAfterTheAnalysisId()
    {
        await using var f = PersonCalibrationFixture.Create(nameof(Export_WritesTheResultId_RightAfterTheAnalysisId));
        var analyst = f.Dashboard.AddUser(Roles.Analyst);
        var (withResult, result) = AddFlagged(f, analyst.UserId);
        var (foreignResult, _) = AddFlagged(f, analyst.UserId, result: r => r.TenantId = DashboardFixture.Other);
        await f.SaveAsync();

        var file  = await f.Service().ExportAsync(DashboardFixture.Tenant, October, null, Ct);
        var lines = PersonCalibrationServiceTests.Csv(file);

        Assert.Equal(["analysisId", "resultId"], lines[0][..2]);
        Assert.Equal(result.ResultId.ToString(), lines.Single(l => l[0] == withResult.AnalysisId.ToString())[1]);
        Assert.Equal("", lines.Single(l => l[0] == foreignResult.AnalysisId.ToString())[1]);
    }
}
