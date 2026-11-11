// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-23

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Tests.Clases;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// The dashboard's queries against the EF Core in-memory provider: which rows each section selects,
/// in which order, how the cap and the total relate, and that no row of another tenant ever leaks in.
/// </summary>
public sealed class DashboardRepositoryTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task MyPending_SortsByRiskLevelThenOldest_AndSelectsOnlyUndecidedOwnRows()
    {
        await using var f = DashboardFixture.Create(nameof(MyPending_SortsByRiskLevelThenOldest_AndSelectsOnlyUndecidedOwnRows));
        var me    = f.AddUser(Roles.Analyst);
        var other = f.AddUser(Roles.Analyst);
        var rm    = f.AddUser(Roles.RiskManager);

        var lowOld      = Pending(f, me.UserId, "Low",      daysAgo: 9);
        var nullLevel   = Pending(f, me.UserId, null,       daysAgo: 8);
        var criticalNew = Pending(f, me.UserId, "Critical", daysAgo: 1);
        var criticalOld = Pending(f, me.UserId, "Critical", daysAgo: 5);
        var high        = Pending(f, me.UserId, "High",     daysAgo: 2);
        var medium      = Pending(f, me.UserId, "Medium",   daysAgo: 3);

        // Not mine, already returned, not pending, another tenant: all excluded.
        Pending(f, other.UserId, "Critical", daysAgo: 1);
        f.AddAnalysis(me.UserId, analysis: a => { a.ReviewedBy = rm.UserId; a.ReviewedAt = DashboardFixture.Now; });
        f.AddAnalysis(me.UserId, analysis: a => a.Status = AnalysisStatuses.Approved);
        f.AddAnalysis(me.UserId, tenant: DashboardFixture.Other);
        await f.SaveAsync();

        var (items, total) = await f.Repository().GetMyPendingAsync(DashboardFixture.Tenant, me.UserId, 20, Ct);

        Assert.Equal(6, total);
        Assert.Equal(
            [criticalOld, criticalNew, high, medium, lowOld, nullLevel],
            items.Select(i => i.ResultId).ToList());
    }

    [Fact]
    public async Task MyPending_CapsTheItemsButCountsTheWholeSet()
    {
        await using var f = DashboardFixture.Create(nameof(MyPending_CapsTheItemsButCountsTheWholeSet));
        var me = f.AddUser(Roles.Analyst);
        for (var i = 0; i < 25; i++) Pending(f, me.UserId, "Medium", daysAgo: i);
        await f.SaveAsync();

        var (items, total) = await f.Repository().GetMyPendingAsync(DashboardFixture.Tenant, me.UserId, 20, Ct);

        Assert.Equal(25, total);
        Assert.Equal(20, items.Count);
        // Oldest first within the level: the 20 kept are the 20 oldest.
        Assert.True(items.Zip(items.Skip(1)).All(p => p.First.GeneratedAt <= p.Second.GeneratedAt));
        Assert.Equal(DashboardFixture.Now.AddDays(-24), items[0].GeneratedAt);
    }

    [Theory]
    [InlineData("""{"a":1}""", true)]
    [InlineData(null, false)]
    [InlineData("   ", false)]
    public async Task MyPending_CarriesTheDetailsHasBalanceTest(string? balanceJson, bool expected)
    {
        await using var f = DashboardFixture.Create($"{nameof(MyPending_CarriesTheDetailsHasBalanceTest)}-{expected}-{balanceJson?.Length}");
        var me = f.AddUser(Roles.Analyst);
        f.AddAnalysis(me.UserId, result: r => r.BalanceJson = balanceJson);
        await f.SaveAsync();

        var (items, _) = await f.Repository().GetMyPendingAsync(DashboardFixture.Tenant, me.UserId, 20, Ct);

        Assert.Equal(expected, Assert.Single(items).HasBalance);
    }

    [Fact]
    public async Task MyPendingDegraded_KeepsOnlyDegradedEnrichment()
    {
        await using var f = DashboardFixture.Create(nameof(MyPendingDegraded_KeepsOnlyDegradedEnrichment));
        var me = f.AddUser(Roles.Analyst);
        var (degraded, _) = f.AddAnalysis(me.UserId, result: r => r.EnrichmentStatus = nameof(BcraEnrichmentOutcome.Degraded));
        f.AddAnalysis(me.UserId, result: r => r.EnrichmentStatus = nameof(BcraEnrichmentOutcome.NotApplicable));
        f.AddAnalysis(me.UserId);
        f.AddAnalysis(me.UserId, result: r => r.EnrichmentStatus = nameof(BcraEnrichmentOutcome.Degraded), tenant: DashboardFixture.Other);
        await f.SaveAsync();

        var rows = await f.Repository().GetMyPendingDegradedAsync(DashboardFixture.Tenant, me.UserId, Ct);

        var row = Assert.Single(rows);
        Assert.Equal(degraded.AnalysisId, row.AnalysisId);
        Assert.True(row.HasBalance);
    }

    [Fact]
    public async Task Returned_IsPendingReviewWithAReviewer_RequestedOrPreApprovedByMe_NewestFirst()
    {
        await using var f = DashboardFixture.Create(nameof(Returned_IsPendingReviewWithAReviewer_RequestedOrPreApprovedByMe_NewestFirst));
        var me    = f.AddUser(Roles.Analyst);
        var other = f.AddUser(Roles.Analyst);
        var rm    = f.AddUser(Roles.RiskManager);

        var (requested, _) = f.AddAnalysis(me.UserId, analysis: a => Returned(a, rm.UserId, hoursAgo: 5, notes: "Falta el balance 2024."));
        var (preApproved, _) = f.AddAnalysis(other.UserId, analysis: a =>
        {
            Returned(a, rm.UserId, hoursAgo: 1, notes: null);
            a.PreApprovedBy = me.UserId;
            a.PreApprovedAt = DashboardFixture.Now.AddDays(-1);
        });

        // Undecided (not returned), decided, somebody else's, another tenant: all excluded.
        f.AddAnalysis(me.UserId);
        f.AddAnalysis(me.UserId, analysis: a => { Returned(a, rm.UserId, 2, null); a.Status = AnalysisStatuses.Rejected; });
        f.AddAnalysis(other.UserId, analysis: a => Returned(a, rm.UserId, 2, null));
        f.AddAnalysis(me.UserId, analysis: a => Returned(a, rm.UserId, 2, null), tenant: DashboardFixture.Other);
        await f.SaveAsync();

        var (items, total) = await f.Repository().GetReturnedAsync(DashboardFixture.Tenant, me.UserId, 20, Ct);

        Assert.Equal(2, total);
        Assert.Equal([preApproved.AnalysisId, requested.AnalysisId], items.Select(i => i.AnalysisId).ToList());
        Assert.Equal("Falta el balance 2024.", items[1].ReviewNotes);
        Assert.Equal(rm.UserId, items[1].ReviewedBy);
    }

    [Fact]
    public async Task AwaitingMySignature_IsMyPreApprovals_OldestFirst()
    {
        await using var f = DashboardFixture.Create(nameof(AwaitingMySignature_IsMyPreApprovals_OldestFirst));
        var me    = f.AddUser(Roles.Analyst);
        var other = f.AddUser(Roles.Analyst);

        var (newer, _) = f.AddPreApproved(me.UserId, DashboardFixture.Now.AddDays(-1));
        var (older, _) = f.AddPreApproved(me.UserId, DashboardFixture.Now.AddDays(-4));
        f.AddPreApproved(other.UserId);
        f.AddPreApproved(me.UserId, tenant: DashboardFixture.Other);
        await f.SaveAsync();

        var (items, total) = await f.Repository().GetAwaitingMySignatureAsync(DashboardFixture.Tenant, me.UserId, 20, Ct);

        Assert.Equal(2, total);
        Assert.Equal([older.AnalysisId, newer.AnalysisId], items.Select(i => i.AnalysisId).ToList());
    }

    [Fact]
    public async Task Recent_IsMyLatestInAnyStatus_NewestFirst_Capped()
    {
        await using var f = DashboardFixture.Create(nameof(Recent_IsMyLatestInAnyStatus_NewestFirst_Capped));
        var me = f.AddUser(Roles.Analyst);
        var statuses = new[] { AnalysisStatuses.PendingReview, AnalysisStatuses.PreApproved, AnalysisStatuses.Approved, AnalysisStatuses.Rejected };
        for (var i = 0; i < 10; i++)
        {
            var days = i;
            f.AddAnalysis(me.UserId, analysis: a => { a.CreatedAt = DashboardFixture.Now.AddDays(-days); a.Status = statuses[days % 4]; });
        }
        f.AddAnalysis(me.UserId, analysis: a => a.CreatedAt = DashboardFixture.Now, tenant: DashboardFixture.Other);
        await f.SaveAsync();

        var items = await f.Repository().GetMyRecentAsync(DashboardFixture.Tenant, me.UserId, 8, Ct);

        Assert.Equal(8, items.Count);
        Assert.Equal(DashboardFixture.Now, items[0].CreatedAt);
        Assert.Equal(DashboardFixture.Now.AddDays(-7), items[7].CreatedAt);
        Assert.Equal(statuses.ToHashSet(), items.Select(i => i.Status).ToHashSet());
    }

    [Fact]
    public async Task AnAnalysisWithoutAResultRow_IsInvisible()
    {
        await using var f = DashboardFixture.Create(nameof(AnAnalysisWithoutAResultRow_IsInvisible));
        var me = f.AddUser(Roles.Analyst);
        var (analysis, result) = f.AddAnalysis(me.UserId);
        f.Context.AnalysisResults.Remove(result);
        await f.SaveAsync();

        var (items, total) = await f.Repository().GetMyPendingAsync(DashboardFixture.Tenant, me.UserId, 20, Ct);

        Assert.Equal(0, total);
        Assert.Empty(items);
        Assert.Empty(await f.Repository().GetByAnalysisIdsAsync(DashboardFixture.Tenant, [analysis.AnalysisId], Ct));
    }

    [Fact]
    public async Task ResultRowOfAnotherTenant_DoesNotJoin()
    {
        // The two sides of the join each carry the tenant predicate: a result row stamped with another
        // tenant must not complete an analysis of this one.
        await using var f = DashboardFixture.Create(nameof(ResultRowOfAnotherTenant_DoesNotJoin));
        var me = f.AddUser(Roles.Analyst);
        f.AddAnalysis(me.UserId, result: r => r.TenantId = DashboardFixture.Other);
        await f.SaveAsync();

        var (_, total) = await f.Repository().GetMyPendingAsync(DashboardFixture.Tenant, me.UserId, 20, Ct);

        Assert.Equal(0, total);
    }

    [Fact]
    public async Task PreApproved_IsTheWholeTenantSet_OldestFirst()
    {
        await using var f = DashboardFixture.Create(nameof(PreApproved_IsTheWholeTenantSet_OldestFirst));
        var a1 = f.AddUser(Roles.Analyst);
        var a2 = f.AddUser(Roles.Analyst);
        var (second, _) = f.AddPreApproved(a1.UserId, DashboardFixture.Now.AddDays(-1));
        var (first, _)  = f.AddPreApproved(a2.UserId, DashboardFixture.Now.AddDays(-3));
        f.AddAnalysis(a1.UserId);
        f.AddPreApproved(a1.UserId, tenant: DashboardFixture.Other);
        await f.SaveAsync();

        var rows = await f.Repository().GetPreApprovedAsync(DashboardFixture.Tenant, Ct);

        Assert.Equal([first.AnalysisId, second.AnalysisId], rows.Select(r => r.AnalysisId).ToList());
    }

    [Fact]
    public async Task ApprovedByMe_CountsOnlyMyApprovedVotesInThisTenant()
    {
        await using var f = DashboardFixture.Create(nameof(ApprovedByMe_CountsOnlyMyApprovedVotesInThisTenant));
        var me = f.AddUser(Roles.RiskManager);
        var colleague = f.AddUser(Roles.RiskManager);
        var analyst = f.AddUser(Roles.Analyst);
        var (mine, _)     = f.AddPreApproved(analyst.UserId);
        var (theirs, _)   = f.AddPreApproved(analyst.UserId);
        var (rejected, _) = f.AddPreApproved(analyst.UserId);
        var (foreign, _)  = f.AddPreApproved(analyst.UserId);
        f.AddVote(mine.AnalysisId, me.UserId);
        f.AddVote(theirs.AnalysisId, colleague.UserId);
        f.AddVote(rejected.AnalysisId, me.UserId, AnalysisVoteValues.Rejected);
        f.AddVote(foreign.AnalysisId, me.UserId, tenant: DashboardFixture.Other);
        await f.SaveAsync();

        var ids = await f.Repository().GetAnalysisIdsApprovedByAsync(
            DashboardFixture.Tenant, me.UserId,
            [mine.AnalysisId, theirs.AnalysisId, rejected.AnalysisId, foreign.AnalysisId], Ct);

        Assert.Equal([mine.AnalysisId], ids.ToList());
    }

    [Fact]
    public async Task SolicitanteFilters_PicksTheEarliestSolicitante_InThisTenant()
    {
        await using var f = DashboardFixture.Create(nameof(SolicitanteFilters_PicksTheEarliestSolicitante_InThisTenant));
        var analysisId = Guid.NewGuid();
        var foreignId  = Guid.NewGuid();
        f.AddSolicitante(analysisId, PersonFilterOutcomes.Ok, createdAt: DashboardFixture.Now.AddDays(-1));
        f.AddSolicitante(analysisId, PersonFilterOutcomes.Rechazo, createdAt: DashboardFixture.Now.AddDays(-5));
        f.AddSolicitante(foreignId, PersonFilterOutcomes.Rechazo, tenant: DashboardFixture.Other);
        await f.SaveAsync();

        var filters = await f.Repository().GetSolicitanteFiltersAsync(DashboardFixture.Tenant, [analysisId, foreignId], Ct);

        var json = Assert.Single(filters).Value;
        Assert.Contains(PersonFilterOutcomes.Rechazo, json);
    }

    [Fact]
    public async Task DocumentStatuses_AreTenantScoped()
    {
        await using var f = DashboardFixture.Create(nameof(DocumentStatuses_AreTenantScoped));
        var mine    = f.AddDocument(Guid.NewGuid(), "Processing");
        var foreign = f.AddDocument(Guid.NewGuid(), "Processing", DashboardFixture.Other);
        await f.SaveAsync();

        var statuses = await f.Repository().GetDocumentStatusesAsync(DashboardFixture.Tenant, [mine.DocumentId, foreign.DocumentId], Ct);

        Assert.Equal("Processing", Assert.Single(statuses).Value);
        Assert.True(statuses.ContainsKey(mine.DocumentId));
    }

    [Fact]
    public async Task VotesAndReviews_RespectTheWindowAndTheTenant()
    {
        await using var f = DashboardFixture.Create(nameof(VotesAndReviews_RespectTheWindowAndTheTenant));
        var me = f.AddUser(Roles.RiskManager);
        var analyst = f.AddUser(Roles.Analyst);
        var since = DashboardFixture.Now.AddDays(-30);

        var (recentVote, _) = f.AddPreApproved(analyst.UserId);
        var (oldVote, _)    = f.AddPreApproved(analyst.UserId);
        f.AddVote(recentVote.AnalysisId, me.UserId, votedAt: DashboardFixture.Now.AddDays(-2));
        f.AddVote(oldVote.AnalysisId, me.UserId, votedAt: DashboardFixture.Now.AddDays(-31));
        f.AddVote(Guid.NewGuid(), me.UserId, tenant: DashboardFixture.Other);

        var (recentReview, _) = f.AddAnalysis(analyst.UserId, analysis: a =>
        {
            a.Status = AnalysisStatuses.Rejected; a.ReviewedBy = me.UserId; a.ReviewedAt = DashboardFixture.Now.AddDays(-3);
        });
        f.AddAnalysis(analyst.UserId, analysis: a =>
        {
            a.Status = AnalysisStatuses.Rejected; a.ReviewedBy = me.UserId; a.ReviewedAt = DashboardFixture.Now.AddDays(-40);
        });
        f.AddAnalysis(analyst.UserId, analysis: a =>
        {
            a.Status = AnalysisStatuses.Rejected; a.ReviewedBy = me.UserId; a.ReviewedAt = DashboardFixture.Now;
        }, tenant: DashboardFixture.Other);
        await f.SaveAsync();

        var votes   = await f.Repository().GetVotesByUserSinceAsync(DashboardFixture.Tenant, me.UserId, since, Ct);
        var reviews = await f.Repository().GetReviewedByUserSinceAsync(DashboardFixture.Tenant, me.UserId, since, Ct);

        Assert.Equal(recentVote.AnalysisId, Assert.Single(votes).AnalysisId);
        Assert.Equal(recentReview.AnalysisId, Assert.Single(reviews).AnalysisId);
    }

    private static Guid Pending(DashboardFixture f, Guid requestedBy, string? level, int daysAgo)
    {
        var (_, result) = f.AddAnalysis(
            requestedBy,
            analysis: a => a.CreatedAt = DashboardFixture.Now.AddDays(-daysAgo),
            result: r => r.AiSuggestedLevel = level);
        return result.ResultId;
    }

    private static void Returned(Analysis a, Guid reviewer, int hoursAgo, string? notes)
    {
        a.ReviewedBy  = reviewer;
        a.ReviewedAt  = DashboardFixture.Now.AddHours(-hoursAgo);
        a.ReviewNotes = notes;
    }
}
