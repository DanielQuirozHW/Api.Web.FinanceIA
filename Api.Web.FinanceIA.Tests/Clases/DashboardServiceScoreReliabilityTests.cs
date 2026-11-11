// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-02

using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Tests.Servicios.ScoreReliability;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// Score credibility 1b-2b on the dashboard: every item that shows a score carries the verdict the detail
/// would give the same row — the dashboard's half of the parity guarantee — read in one page-sized query.
/// </summary>
public sealed class DashboardServiceScoreReliabilityTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static readonly string Expected = ScoreReliabilityFixture.Wire(ScoreReliabilityFixture.ParityExpected());

    /// <summary>Makes the seeded pair the shared parity row.</summary>
    private static void UseParityRow(Analysis analysis, AnalysisResult result)
    {
        analysis.FiscalYear       = ScoreReliabilityFixture.ParityFiscalYear;
        result.AiSuggestedScore   = ScoreReliabilityFixture.ParityScore;
        result.EnrichmentStatus   = ScoreReliabilityFixture.ParityEnrichment;
        result.GeneratedAt        = ScoreReliabilityFixture.ParityGeneratedAt;
        result.RescoredAt         = ScoreReliabilityFixture.ParityRescoredAt;
        result.ScoreBreakdownJson = ScoreReliabilityFixture.ParityBreakdownJson();
    }

    [Fact]
    public async Task WaitingForMe_CarriesTheParityVerdict()
    {
        await using var f = DashboardFixture.Create(nameof(WaitingForMe_CarriesTheParityVerdict));
        var me      = f.AddUser(Roles.RiskManager);
        var analyst = f.AddUser(Roles.Analyst);
        var (analysis, result) = f.AddPreApproved(analyst.UserId);
        UseParityRow(analysis, result);
        await f.SaveAsync();

        var response = await f.Service().GetApprovalsAsync(DashboardFixture.Tenant, me, DashboardFixture.Now, Ct);

        var item = Assert.Single(response.WaitingForMe!.Items);
        Assert.Equal(Expected, ScoreReliabilityFixture.Wire(item.ScoreReliability));
    }

    [Fact]
    public async Task MyWork_PendingAndRecent_CarryTheParityVerdict()
    {
        await using var f = DashboardFixture.Create(nameof(MyWork_PendingAndRecent_CarryTheParityVerdict));
        var me = f.AddUser(Roles.Analyst);
        var (analysis, result) = f.AddAnalysis(me.UserId);
        UseParityRow(analysis, result);
        await f.SaveAsync();

        var response = await f.Service().GetMyWorkAsync(DashboardFixture.Tenant, me, DashboardFixture.Now, Ct);

        Assert.Equal(Expected, ScoreReliabilityFixture.Wire(Assert.Single(response.Pending!.Items).ScoreReliability));
        Assert.Equal(Expected, ScoreReliabilityFixture.Wire(Assert.Single(response.Recent!).ScoreReliability));
    }

    [Fact]
    public async Task MyWork_Returned_CarriesTheParityVerdict()
    {
        await using var f = DashboardFixture.Create(nameof(MyWork_Returned_CarriesTheParityVerdict));
        var me = f.AddUser(Roles.Analyst);
        var rm = f.AddUser(Roles.RiskManager);
        var (analysis, result) = f.AddAnalysis(me.UserId, analysis: a =>
        {
            a.ReviewedBy = rm.UserId;
            a.ReviewedAt = DashboardFixture.Now.AddHours(-3);
        });
        UseParityRow(analysis, result);
        await f.SaveAsync();

        var response = await f.Service().GetMyWorkAsync(DashboardFixture.Tenant, me, DashboardFixture.Now, Ct);

        Assert.Equal(Expected, ScoreReliabilityFixture.Wire(Assert.Single(response.Returned!.Items).ScoreReliability));
    }

    [Fact]
    public async Task MyWork_AwaitingSignature_CarriesTheParityVerdict()
    {
        await using var f = DashboardFixture.Create(nameof(MyWork_AwaitingSignature_CarriesTheParityVerdict));
        var me = f.AddUser(Roles.Analyst);
        var (analysis, result) = f.AddPreApproved(me.UserId, DashboardFixture.Now.AddDays(-1));
        UseParityRow(analysis, result);
        await f.SaveAsync();

        var response = await f.Service().GetMyWorkAsync(DashboardFixture.Tenant, me, DashboardFixture.Now, Ct);

        Assert.Equal(Expected, ScoreReliabilityFixture.Wire(Assert.Single(response.AwaitingSignature!.Items).ScoreReliability));
    }

    [Fact]
    public async Task Oversight_TeamQueue_CarriesTheParityVerdict()
    {
        await using var f = DashboardFixture.Create(nameof(Oversight_TeamQueue_CarriesTheParityVerdict));
        var analyst = f.AddUser(Roles.Analyst);
        var (analysis, result) = f.AddAnalysis(analyst.UserId);
        UseParityRow(analysis, result);
        await f.SaveAsync();

        var response = await f.Service().GetOversightAsync(DashboardFixture.Tenant, DashboardFixture.Now, Ct);

        Assert.Equal(Expected, ScoreReliabilityFixture.Wire(Assert.Single(response.TeamQueue!.Items).ScoreReliability));
    }

    [Fact]
    public async Task WaitingForMe_PersonaRowHasNoVerdict_AndAnUnreadableBreakdownIsFactsUnavailable()
    {
        await using var f = DashboardFixture.Create(nameof(WaitingForMe_PersonaRowHasNoVerdict_AndAnUnreadableBreakdownIsFactsUnavailable));
        var me      = f.AddUser(Roles.RiskManager);
        var analyst = f.AddUser(Roles.Analyst);
        var (persona, personaResult) = f.AddPreApproved(analyst.UserId, kind: AnalysisKinds.PersonaHumana);
        personaResult.AiSuggestedScore = null;
        var (_, broken) = f.AddPreApproved(analyst.UserId);
        broken.ScoreBreakdownJson = "{\"breakdown\": [";
        await f.SaveAsync();

        var response = await f.Service().GetApprovalsAsync(DashboardFixture.Tenant, me, DashboardFixture.Now, Ct);

        var items = response.WaitingForMe!.Items;
        Assert.Null(items.Single(i => i.AnalysisId == persona.AnalysisId).ScoreReliability);
        var brokenVerdict = items.Single(i => i.ResultId == broken.ResultId).ScoreReliability!;
        Assert.Equal(ScoreReliabilityLevels.WithCaveats, brokenVerdict.Level);
        Assert.Equal("facts_unavailable", Assert.Single(brokenVerdict.Reasons).Code);
    }

    [Fact]
    public async Task TheVerdictSerialisesAsCamelCaseOnTheItem()
    {
        await using var f = DashboardFixture.Create(nameof(TheVerdictSerialisesAsCamelCaseOnTheItem));
        var me      = f.AddUser(Roles.RiskManager);
        var analyst = f.AddUser(Roles.Analyst);
        var (analysis, result) = f.AddPreApproved(analyst.UserId);
        UseParityRow(analysis, result);
        await f.SaveAsync();

        var item = Assert.Single((await f.Service().GetApprovalsAsync(DashboardFixture.Tenant, me, DashboardFixture.Now, Ct)).WaitingForMe!.Items);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(item, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var verdict = json.RootElement.GetProperty("scoreReliability");
        Assert.Equal("with_caveats", verdict.GetProperty("level").GetString());
        Assert.Equal("6", verdict.GetProperty("policyVersion").GetString());  // policy 6: a column mix is not a presentation
        var reason = verdict.GetProperty("reasons")[0];
        Assert.True(reason.TryGetProperty("code", out _));
        Assert.True(reason.TryGetProperty("severity", out _));
        Assert.True(reason.TryGetProperty("message", out _));
    }

    // ── Fault isolation: the lookup failing never hides the section ─────────────

    private static void AssertFactsUnavailable(ScoreReliabilityResponse? verdict)
    {
        Assert.NotNull(verdict);
        Assert.Equal(ScoreReliabilityLevels.WithCaveats, verdict!.Level);
        Assert.Equal("facts_unavailable", Assert.Single(verdict.Reasons).Code);
    }

    [Fact]
    public async Task ReliabilityLookupFailure_KeepsWaitingForMe_WithEveryBalanceRowAsFactsUnavailable()
    {
        await using var f = DashboardFixture.Create(nameof(ReliabilityLookupFailure_KeepsWaitingForMe_WithEveryBalanceRowAsFactsUnavailable));
        var me      = f.AddUser(Roles.RiskManager);
        var analyst = f.AddUser(Roles.Analyst);
        var (first, firstResult) = f.AddPreApproved(analyst.UserId);
        UseParityRow(first, firstResult);        // facts that would otherwise be read: none of them may leak in
        firstResult.EnrichmentStatus = nameof(BcraEnrichmentOutcome.Complete);
        var (second, _) = f.AddPreApproved(analyst.UserId);
        await f.SaveAsync();

        var failing = new FailingDashboardRepository(
            f.Repository(), nameof(Api.Web.FinanceIA.Data.Repositories.IDashboardRepository.GetScoreReliabilitySourcesAsync));

        var response = await f.Service(failing).GetApprovalsAsync(DashboardFixture.Tenant, me, DashboardFixture.Now, Ct);

        Assert.DoesNotContain(DashboardSections.WaitingForMe, response.DegradedSections);
        var items = response.WaitingForMe!.Items;
        Assert.Equal(new[] { first.AnalysisId, second.AnalysisId }.ToHashSet(), items.Select(i => i.AnalysisId).ToHashSet());
        Assert.All(items, i => AssertFactsUnavailable(i.ScoreReliability));
    }

    [Fact]
    public async Task ReliabilityLookupFailure_KeepsMyWorkPending_WithItsRowAsFactsUnavailable()
    {
        await using var f = DashboardFixture.Create(nameof(ReliabilityLookupFailure_KeepsMyWorkPending_WithItsRowAsFactsUnavailable));
        var me = f.AddUser(Roles.Analyst);
        var (pending, _) = f.AddAnalysis(me.UserId);
        await f.SaveAsync();

        var failing = new FailingDashboardRepository(
            f.Repository(), nameof(Api.Web.FinanceIA.Data.Repositories.IDashboardRepository.GetScoreReliabilitySourcesAsync));

        var response = await f.Service(failing).GetMyWorkAsync(DashboardFixture.Tenant, me, DashboardFixture.Now, Ct);

        Assert.DoesNotContain(DashboardSections.Pending, response.DegradedSections);
        Assert.DoesNotContain(DashboardSections.Recent, response.DegradedSections);
        var item = Assert.Single(response.Pending!.Items);
        Assert.Equal(pending.AnalysisId, item.AnalysisId);
        AssertFactsUnavailable(item.ScoreReliability);
        AssertFactsUnavailable(Assert.Single(response.Recent!).ScoreReliability);
    }

    [Fact]
    public async Task ReliabilityLookupCancellation_StillPropagates()
    {
        await using var f = DashboardFixture.Create(nameof(ReliabilityLookupCancellation_StillPropagates));
        var me      = f.AddUser(Roles.RiskManager);
        var analyst = f.AddUser(Roles.Analyst);
        f.AddPreApproved(analyst.UserId);
        await f.SaveAsync();

        var cancelling = new FailingDashboardRepository(
            f.Repository(), nameof(Api.Web.FinanceIA.Data.Repositories.IDashboardRepository.GetScoreReliabilitySourcesAsync))
        {
            Throw = () => new OperationCanceledException()
        };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => f.Service(cancelling).GetApprovalsAsync(DashboardFixture.Tenant, me, DashboardFixture.Now, Ct));
    }
}
