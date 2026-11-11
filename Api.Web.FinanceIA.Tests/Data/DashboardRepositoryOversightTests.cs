// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-23

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Tests.Clases;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// The oversight and operations reads of <see cref="Api.Web.FinanceIA.Data.Repositories.DashboardRepository"/>
/// on the in-memory provider, each with the other tenant seeded beside it.
/// </summary>
public sealed class DashboardRepositoryOversightTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTime Now = DashboardFixture.Now;
    private const string Other = DashboardFixture.Other;

    [Fact]
    public async Task TeamQueue_IsEveryAnalystsPendingReview_OldestFirst_Capped_WithTotalAndPerRequester()
    {
        await using var f = DashboardFixture.Create(nameof(TeamQueue_IsEveryAnalystsPendingReview_OldestFirst_Capped_WithTotalAndPerRequester));
        var ana = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var (_, oldest) = f.AddAnalysis(ana, result: r => r.GeneratedAt = Now.AddDays(-9));
        var (_, mid)    = f.AddAnalysis(bob, analysis: a => a.ReviewedBy = Guid.NewGuid(), result: r => r.GeneratedAt = Now.AddDays(-5));
        f.AddAnalysis(ana, result: r => r.GeneratedAt = Now.AddDays(-1));
        f.AddAnalysis(ana, analysis: a => a.Status = AnalysisStatuses.Approved);
        f.AddPreApproved(bob);
        f.AddAnalysis(ana, tenant: Other);
        f.AddPreApproved(bob, tenant: Other);
        await f.SaveAsync();

        var repository = f.Repository();
        var (items, total) = await repository.GetTeamQueueAsync(DashboardFixture.Tenant, 2, Ct);
        var byRequester    = await repository.GetTeamQueueByRequesterAsync(DashboardFixture.Tenant, Ct);

        Assert.Equal(3, total);
        Assert.Equal([oldest.ResultId, mid.ResultId], items.Select(i => i.ResultId));
        Assert.Equal((ana, 2, 0, Now.AddDays(-9)), Tuple(byRequester.Single(r => r.UserId == ana)));
        Assert.Equal((bob, 0, 1, Now.AddDays(-5)), Tuple(byRequester.Single(r => r.UserId == bob)));
        Assert.Equal(1, await repository.CountPreApprovedAsync(DashboardFixture.Tenant, Ct));

        static (Guid, int, int, DateTime) Tuple(DashboardRequesterQueueRow r) => (r.UserId, r.Pending, r.Returned, r.OldestGeneratedAt);
    }

    [Fact]
    public async Task AgainstSuggestionCandidates_AreApprovedInTheWindow_HighOrCriticalBalance_OrAnyPersona()
    {
        await using var f = DashboardFixture.Create(nameof(AgainstSuggestionCandidates_AreApprovedInTheWindow_HighOrCriticalBalance_OrAnyPersona));
        var since = Now.AddDays(-90);
        void Approved(string level, DateTime at, string kind = AnalysisKinds.Balance, string tenant = DashboardFixture.Tenant)
            => f.AddAnalysis(Guid.NewGuid(),
                analysis: a => { a.Status = AnalysisStatuses.Approved; a.ReviewedBy = Guid.NewGuid(); a.ReviewedAt = at; a.AnalysisKind = kind; },
                result: r => r.AiSuggestedLevel = level, tenant: tenant);

        Approved("Critical", Now.AddDays(-1));
        Approved("High", Now.AddDays(-2));
        Approved("Medium", Now.AddDays(-1));
        Approved("Low", Now.AddDays(-3), AnalysisKinds.PersonaHumana);
        Approved("Critical", since.AddSeconds(-1));
        Approved("Critical", Now.AddDays(-1), tenant: Other);
        f.AddAnalysis(Guid.NewGuid(), result: r => r.AiSuggestedLevel = "Critical");   // PendingReview
        await f.SaveAsync();

        var rows = await f.Repository().GetAgainstSuggestionCandidatesAsync(DashboardFixture.Tenant, since, Ct);

        Assert.Equal(["Critical", "High", "Low"], rows.Select(r => r.AiSuggestedLevel));
        Assert.Equal(AnalysisKinds.PersonaHumana, rows[^1].AnalysisKind);
    }

    [Fact]
    public async Task RecentDecisions_AreTheRowsWhoseActIsProven_NewestFirst_WithNotesAndDecisionAmount()
    {
        await using var f = DashboardFixture.Create(nameof(RecentDecisions_AreTheRowsWhoseActIsProven_NewestFirst_WithNotesAndDecisionAmount));
        var decider = Guid.NewGuid();
        var (approved, _) = f.AddAnalysis(Guid.NewGuid(), analysis: a =>
        {
            a.Status = AnalysisStatuses.Approved; a.ReviewedBy = decider; a.ReviewedAt = Now.AddHours(-1);
            a.ReviewNotes = "ok"; a.DecisionAmount = 500m; a.DecisionCurrency = "USD";
        });
        var (returned, _) = f.AddAnalysis(Guid.NewGuid(), analysis: a => { a.ReviewedBy = decider; a.ReviewedAt = Now.AddHours(-2); });
        f.AddAnalysis(Guid.NewGuid(), analysis: a => { a.Status = AnalysisStatuses.PreApproved; a.ReviewedBy = decider; a.ReviewedAt = Now; });
        f.AddAnalysis(Guid.NewGuid(), analysis: a => { a.Status = AnalysisStatuses.Rejected; a.ReviewedBy = decider; a.ReviewedAt = Now; }, tenant: Other);
        await f.SaveAsync();

        var rows = await f.Repository().GetRecentDecisionsAsync(DashboardFixture.Tenant, 20, Ct);

        Assert.Equal([approved.AnalysisId, returned.AnalysisId], rows.Select(r => r.AnalysisId));
        Assert.Equal(("ok", 500m, "USD"), (rows[0].ReviewNotes, rows[0].DecisionAmount, rows[0].DecisionCurrency));
        Assert.Single(await f.Repository().GetRecentDecisionsAsync(DashboardFixture.Tenant, 1, Ct));
    }

    [Fact]
    public async Task ReviewNotes_AreTenantScoped()
    {
        await using var f = DashboardFixture.Create(nameof(ReviewNotes_AreTenantScoped));
        var (mine, _)    = f.AddAnalysis(Guid.NewGuid(), analysis: a => a.ReviewNotes = "mine");
        var (foreign, _) = f.AddAnalysis(Guid.NewGuid(), analysis: a => a.ReviewNotes = "foreign", tenant: Other);
        await f.SaveAsync();

        var notes = await f.Repository().GetReviewNotesAsync(DashboardFixture.Tenant, [mine.AnalysisId, foreign.AnalysisId], Ct);

        Assert.Equal("mine", Assert.Single(notes).Value);
    }

    [Fact]
    public async Task RecentRescores_NewestFirst_Capped_InThisTenant()
    {
        await using var f = DashboardFixture.Create(nameof(RecentRescores_NewestFirst_Capped_InThisTenant));
        var (analysis, result) = f.AddAnalysis(Guid.NewGuid(), analysis: a => a.AnalysisKind = AnalysisKinds.PersonaHumana);
        var older = f.AddRescore(result, Guid.NewGuid(), Now.AddDays(-2));
        var newer = f.AddRescore(result, Guid.NewGuid(), Now.AddDays(-1));
        f.AddRescore(result, Guid.NewGuid(), Now, tenant: Other);
        await f.SaveAsync();

        var all = await f.Repository().GetRecentRescoresAsync(DashboardFixture.Tenant, 10, Ct);

        Assert.Equal([newer.RescoreId, older.RescoreId], all.Select(r => r.RescoreId));
        Assert.All(all, r => Assert.Equal(((Guid?)analysis.EntityId, AnalysisKinds.PersonaHumana), (r.EntityId, r.AnalysisKind)));
        Assert.Single(await f.Repository().GetRecentRescoresAsync(DashboardFixture.Tenant, 1, Ct));
    }

    [Fact]
    public async Task RecentRescores_WhoseAnalysisIsInAnotherTenant_StillList_WithNoEntity()
    {
        await using var f = DashboardFixture.Create(nameof(RecentRescores_WhoseAnalysisIsInAnotherTenant_StillList_WithNoEntity));
        var (_, foreign) = f.AddAnalysis(Guid.NewGuid(), tenant: Other);
        var crossed = f.AddRescore(foreign, Guid.NewGuid(), Now);
        await f.SaveAsync();

        var row = Assert.Single(await f.Repository().GetRecentRescoresAsync(DashboardFixture.Tenant, 10, Ct));

        Assert.Equal(crossed.RescoreId, row.RescoreId);
        Assert.Null(row.EntityId);
        Assert.Null(row.AnalysisKind);
    }

    [Fact]
    public async Task MonthReads_UseTheHalfOpenWindow_OnCreatedAt_InThisTenant()
    {
        await using var f = DashboardFixture.Create(nameof(MonthReads_UseTheHalfOpenWindow_OnCreatedAt_InThisTenant));
        var start = new DateTime(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc);
        var end   = new DateTime(2026, 11, 1, 3, 0, 0, DateTimeKind.Utc);
        f.AddAnalysis(Guid.NewGuid(), analysis: a => a.CreatedAt = start);
        f.AddAnalysis(Guid.NewGuid(), analysis: a => { a.CreatedAt = start.AddDays(1); a.Status = AnalysisStatuses.Approved; a.ReviewedAt = start.AddDays(2); });
        f.AddAnalysis(Guid.NewGuid(), analysis: a => { a.CreatedAt = start.AddDays(1); a.Status = AnalysisStatuses.Rejected; a.ReviewedAt = start.AddDays(1).AddHours(5); });
        f.AddAnalysis(Guid.NewGuid(), analysis: a => { a.CreatedAt = start.AddDays(1); a.Status = AnalysisStatuses.PendingReview; a.ReviewedAt = start.AddDays(3); });
        f.AddAnalysis(Guid.NewGuid(), analysis: a => a.CreatedAt = end);
        f.AddAnalysis(Guid.NewGuid(), analysis: a => a.CreatedAt = start.AddTicks(-1));
        f.AddAnalysis(Guid.NewGuid(), analysis: a => a.CreatedAt = start.AddDays(1), tenant: Other);
        await f.SaveAsync();

        var byStatus = await f.Repository().GetCreatedByStatusAsync(DashboardFixture.Tenant, start, end, Ct);
        var times    = await f.Repository().GetDecisionTimesAsync(DashboardFixture.Tenant, start, end, Ct);

        Assert.Equal(4, byStatus.Sum(s => s.Count));
        Assert.Equal(2, byStatus.Single(s => s.Status == AnalysisStatuses.PendingReview).Count);
        Assert.Equal(2, times.Count);   // the returned PendingReview one is not decided
    }

    [Fact]
    public async Task ApiCalls_AggregatePerServiceAndOutcomeInTheWindow_InThisTenant()
    {
        await using var f = DashboardFixture.Create(nameof(ApiCalls_AggregatePerServiceAndOutcomeInTheWindow_InThisTenant));
        var from = Now.AddHours(-24);
        f.AddApiCall("LUFE", true, Now.AddHours(-1), 100, responseStatus: 200);
        f.AddApiCall("LUFE", true, Now.AddHours(-3), 300, responseStatus: 200);
        f.AddApiCall("LUFE", false, Now.AddHours(-2), null, responseStatus: 404);
        f.AddApiCall("LUFE", false, Now.AddHours(-4), null, responseStatus: 404);
        f.AddApiCall("LUFE", false, Now.AddHours(-6), null, responseStatus: 500);
        f.AddApiCall("LUFE", false, Now.AddHours(-8), null, responseStatus: 200, errorMessage: "Empty (HTTP 200)");
        f.AddApiCall("LUFE", false, Now.AddHours(-9), null, responseStatus: 200, errorMessage: "No legajo (HTTP 200)");
        f.AddApiCall("LUFE", false, Now.AddHours(-10), null, responseStatus: 200, errorMessage: "Parse error");
        f.AddApiCall("LUFE", false, from.AddSeconds(-1), responseStatus: 500);
        f.AddApiCall("LUFE", true, Now, 1, tenant: Other, responseStatus: 200);
        f.AddApiCall("ARCA", false, Now.AddHours(-5), responseStatus: 502, errorMessage: null);
        f.AddApiCall("ARCA", false, Now.AddHours(-7), responseStatus: 502, errorMessage: "timeout");
        f.AddApiCall("NOSIS", true, Now.AddHours(-5), 40, responseStatus: 200);
        await f.SaveAsync();

        var outcomes  = await f.Repository().GetApiCallOutcomesAsync(DashboardFixture.Tenant, from, Now, Ct);
        var durations = await f.Repository().GetApiCallDurationsAsync(DashboardFixture.Tenant, from, Now, Ct);

        Assert.Equal(
            [
                ("ARCA",  false, (int?)502, false, false, 1, Now.AddHours(-5)),
                ("ARCA",  false, (int?)502, true,  false, 1, Now.AddHours(-7)),
                ("LUFE",  false, (int?)200, true,  false, 1, Now.AddHours(-10)),   // "Parse error"
                ("LUFE",  false, (int?)200, true,  true,  2, Now.AddHours(-8)),    // "Empty …" + "No legajo …"
                ("LUFE",  false, (int?)404, true,  false, 2, Now.AddHours(-2)),
                ("LUFE",  false, (int?)500, true,  false, 1, Now.AddHours(-6)),
                ("LUFE",  true,  (int?)200, false, false, 2, Now.AddHours(-1)),
                ("NOSIS", true,  (int?)200, false, false, 1, Now.AddHours(-5))
            ],
            outcomes
                .Select(o => (o.Service, o.IsSuccess, o.ResponseStatus, o.HasErrorMessage, o.HasLufeNoDataMarker, o.Calls, o.LastCalledAt))
                .OrderBy(o => o.Service, StringComparer.Ordinal).ThenBy(o => o.IsSuccess).ThenBy(o => o.ResponseStatus)
                .ThenBy(o => o.HasErrorMessage).ThenBy(o => o.HasLufeNoDataMarker));
        Assert.Equal([100, 300], durations.Where(d => d.Service == "LUFE").Select(d => d.DurationMs).Order());
    }

    [Fact]
    public async Task Freshness_CountsOnlyEntitiesWithAnActiveCupo_InThisTenant_IntoTheFourBands()
    {
        await using var f = DashboardFixture.Create(nameof(Freshness_CountsOnlyEntitiesWithAnActiveCupo_InThisTenant_IntoTheFourBands));
        var cut30 = Now.AddDays(-30);
        var cut90 = Now.AddDays(-90);
        Entity Live(DateTime? bcra)
        {
            var e = f.AddEntity();
            e.BcraDataUpdatedAt = bcra;
            f.AddCreditLine(e.EntityId);
            return e;
        }

        Live(Now.AddDays(-1));
        Live(cut30);            // exactly 30 days: the middle band
        Live(cut90);            // exactly 90 days: the middle band
        Live(cut90.AddSeconds(-1));
        Live(null);
        var suspendedOnly = f.AddEntity();
        suspendedOnly.BcraDataUpdatedAt = Now;
        f.AddCreditLine(suspendedOnly.EntityId, status: "Suspended");
        var foreign = f.AddEntity(tenant: Other);
        f.AddCreditLine(foreign.EntityId, tenant: Other);
        var crossed = f.AddEntity();               // a tenant-1 entity whose only Active cupo is tenant-2's row
        f.AddCreditLine(crossed.EntityId, tenant: Other);
        await f.SaveAsync();

        var (count, sources) = await f.Repository().GetFreshnessAsync(DashboardFixture.Tenant, cut30, cut90, Ct);

        Assert.Equal(5, count);
        var bcra = Assert.Single(sources, s => s.Source == "BCRA");
        Assert.Equal((1, 2, 1, 1), (bcra.Under30Days, bcra.Days30To90, bcra.Over90Days, bcra.Never));
        Assert.Equal(["ARCA", "LUFE", "BCRA", "CASFOG", "NOSIS"], sources.Select(s => s.Source));
        Assert.All(sources.Where(s => s.Source != "BCRA"), s => Assert.Equal(5, s.Never));
    }

    [Fact]
    public async Task Freshness_WithNoActiveCupo_IsZeroEverywhere()
    {
        await using var f = DashboardFixture.Create(nameof(Freshness_WithNoActiveCupo_IsZeroEverywhere));
        await f.SaveAsync();

        var (count, sources) = await f.Repository().GetFreshnessAsync(DashboardFixture.Tenant, Now.AddDays(-30), Now.AddDays(-90), Ct);

        Assert.Equal(0, count);
        Assert.Equal(5, sources.Count);
        Assert.All(sources, s => Assert.Equal(0, s.Under30Days + s.Days30To90 + s.Over90Days + s.Never));
    }
}
