// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-23

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// <see cref="DashboardService.GetOversightAsync"/>: the tenant-wide queue, approvals against the
/// suggestion (derived as approvals derives it), the acts recentDecisions names, re-scores, and Argentine
/// months with their decision-time percentiles.
/// </summary>
public sealed class DashboardServiceOversightTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTime Now = DashboardFixture.Now;
    private const string Tenant = DashboardFixture.Tenant;

    private static Task<OversightResponse> OversightAsync(DashboardFixture f, DateTime? now = null, IDashboardRepository? repository = null)
        => f.Service(repository).GetOversightAsync(Tenant, now ?? Now, Ct);

    [Fact]
    public async Task TeamQueue_ListsEveryAnalyst_WithNamesReturnedFlagAndAge_PlusPerRequesterAndPreApproved()
    {
        await using var f = DashboardFixture.Create(nameof(TeamQueue_ListsEveryAnalyst_WithNamesReturnedFlagAndAge_PlusPerRequesterAndPreApproved));
        var ana = f.AddUser(Roles.Analyst, name: "Ana");
        var bob = f.AddUser(Roles.Analyst, name: "Bob");
        var reviewer = f.AddUser(Roles.RiskManager);
        var entity = f.AddEntity(name: "PyME Uno");
        var (_, oldest) = f.AddAnalysis(ana.UserId, entity.EntityId, a => a.RequestedAmount = 5_000m, r => r.GeneratedAt = Now.AddDays(-6).AddHours(-1));
        var (_, back)   = f.AddAnalysis(bob.UserId, entity.EntityId, a => a.ReviewedBy = reviewer.UserId, r => r.GeneratedAt = Now.AddDays(-2));
        f.AddAnalysis(ana.UserId, entity.EntityId, result: r => r.GeneratedAt = Now.AddHours(-1));
        f.AddPreApproved(bob.UserId);
        f.AddPreApproved(bob.UserId);
        await f.SaveAsync();

        var queue = (await OversightAsync(f)).TeamQueue!;

        Assert.Equal(3, queue.TotalCount);
        Assert.Equal(2, queue.PreApprovedCount);
        var first = queue.Items[0];
        Assert.Equal((oldest.ResultId, "Ana", false, 6, 5_000m, "PyME Uno"), (first.ResultId, first.RequestedByName, first.Returned, first.AgeDays, first.RequestedAmount, first.EntityName));
        Assert.True(queue.Items.Single(i => i.ResultId == back.ResultId).Returned);

        Assert.Equal(["Ana", "Bob"], queue.ByRequester.Select(r => r.Name));
        Assert.Equal((2, 0, 6), (queue.ByRequester[0].Pending, queue.ByRequester[0].Returned, queue.ByRequester[0].OldestAgeDays));
        Assert.Equal((0, 1, 2), (queue.ByRequester[1].Pending, queue.ByRequester[1].Returned, queue.ByRequester[1].OldestAgeDays));
    }

    [Fact]
    public async Task TeamQueue_IsCappedAtFifty_WithTheWholeCount()
    {
        await using var f = DashboardFixture.Create(nameof(TeamQueue_IsCappedAtFifty_WithTheWholeCount));
        for (var i = 0; i < 53; i++)
            f.AddAnalysis(Guid.NewGuid());
        await f.SaveAsync();

        var queue = (await OversightAsync(f)).TeamQueue!;

        Assert.Equal((53, 50), (queue.TotalCount, queue.Items.Count));
    }

    [Fact]
    public async Task AgainstSuggestion_IsHighOrCriticalBalance_OrRechazoSugeridoPersona_ApprovedInNinetyDays()
    {
        await using var f = DashboardFixture.Create(nameof(AgainstSuggestion_IsHighOrCriticalBalance_OrRechazoSugeridoPersona_ApprovedInNinetyDays));
        var decider = f.AddUser(Roles.RiskManager, name: "Rita");
        (Analysis Analysis, AnalysisResult Result) Approved(string? level, int daysAgo, string kind = AnalysisKinds.Balance)
            => f.AddAnalysis(Guid.NewGuid(), analysis: a =>
            {
                a.AnalysisKind = kind; a.Status = AnalysisStatuses.Approved; a.ReviewedBy = decider.UserId;
                a.ReviewedAt = Now.AddDays(-daysAgo); a.ReviewNotes = "lo apruebo igual";
                a.DecisionAmount = 250m; a.DecisionCurrency = "USD";
            }, result: r => r.AiSuggestedLevel = level);

        var critical = Approved("Critical", 1);
        var high     = Approved("High", 5);
        Approved("Medium", 1);
        Approved("Critical", 91);
        var rechazo = Approved(null, 3, AnalysisKinds.PersonaHumana);
        f.AddSolicitante(rechazo.Analysis.AnalysisId, PersonFilterOutcomes.Rechazo);
        var alerta = Approved(null, 2, AnalysisKinds.PersonaHumana);
        f.AddSolicitante(alerta.Analysis.AnalysisId, PersonFilterOutcomes.Alerta);
        Approved(null, 2, AnalysisKinds.PersonaHumana);   // no solicitante: no suggestion
        await f.SaveAsync();

        var against = (await OversightAsync(f)).AgainstSuggestion!;

        Assert.Equal(3, against.TotalCount);
        Assert.Equal([critical.Result.ResultId, rechazo.Result.ResultId, high.Result.ResultId], against.Items.Select(i => i.ResultId));
        var item = against.Items[0];
        Assert.Equal((DashboardAgainstSuggestionReasons.AiLevel, "Rita", "lo apruebo igual", 250m, "USD"),
            (item.Reason, item.DecidedByName, item.ReviewNotes, item.DecisionAmount, item.DecisionCurrency));
        Assert.Equal((DashboardAgainstSuggestionReasons.PersonSuggestion, PersonSuggestions.RechazoSugerido),
            (against.Items[1].Reason, against.Items[1].PersonSuggestion));
    }

    [Fact]
    public async Task AgainstSuggestion_IsCappedAtTwenty_WithTheWholeCount()
    {
        await using var f = DashboardFixture.Create(nameof(AgainstSuggestion_IsCappedAtTwenty_WithTheWholeCount));
        for (var i = 0; i < 22; i++)
        {
            f.AddAnalysis(Guid.NewGuid(), analysis: a => { a.Status = AnalysisStatuses.Approved; a.ReviewedBy = Guid.NewGuid(); a.ReviewedAt = Now.AddHours(-i); },
                result: r => r.AiSuggestedLevel = "High");
        }
        await f.SaveAsync();

        var against = (await OversightAsync(f)).AgainstSuggestion!;

        Assert.Equal((22, 20), (against.TotalCount, against.Items.Count));
    }

    [Fact]
    public async Task RecentDecisions_NameTheAct_ConfirmedWhenAPreApprovalIsOnTheRow()
    {
        await using var f = DashboardFixture.Create(nameof(RecentDecisions_NameTheAct_ConfirmedWhenAPreApprovalIsOnTheRow));
        var decider = f.AddUser(Roles.Admin, name: "Ada");
        void Decided(string status, int hoursAgo, bool preApproved = false)
            => f.AddAnalysis(Guid.NewGuid(), analysis: a =>
            {
                a.Status = status; a.ReviewedBy = decider.UserId; a.ReviewedAt = Now.AddHours(-hoursAgo); a.ReviewNotes = status;
                if (preApproved) { a.PreApprovedBy = Guid.NewGuid(); a.PreApprovedAt = Now.AddDays(-2); }
            });

        Decided(AnalysisStatuses.Approved, 1, preApproved: true);
        Decided(AnalysisStatuses.Approved, 2);
        Decided(AnalysisStatuses.Rejected, 3);
        Decided(AnalysisStatuses.PendingReview, 4);
        Decided(AnalysisStatuses.PreApproved, 0);   // the act cannot be told from the row
        await f.SaveAsync();

        var decisions = (await OversightAsync(f)).RecentDecisions!;

        Assert.Equal([DashboardActs.Confirmed, DashboardActs.Approved, DashboardActs.Rejected, DashboardActs.Returned],
            decisions.Select(d => d.Act));
        Assert.All(decisions, d => Assert.Equal("Ada", d.DecidedByName));
        Assert.Equal([AnalysisStatuses.Approved, AnalysisStatuses.Approved, AnalysisStatuses.Rejected, AnalysisStatuses.PendingReview],
            decisions.Select(d => d.ReviewNotes));
    }

    [Fact]
    public async Task RecentRescores_CarryBothScoresLevelsAndStatuses_AndWhoRescored()
    {
        await using var f = DashboardFixture.Create(nameof(RecentRescores_CarryBothScoresLevelsAndStatuses_AndWhoRescored));
        var analyst = f.AddUser(Roles.Analyst, name: "Rafa");
        var entity  = f.AddEntity(name: "PyME Uno");
        var (_, result) = f.AddAnalysis(analyst.UserId, entity.EntityId);
        for (var i = 0; i < 12; i++)
            f.AddRescore(result, analyst.UserId, Now.AddHours(-i));
        await f.SaveAsync();

        var rescores = (await OversightAsync(f)).RecentRescores!;

        Assert.Equal(10, rescores.Count);
        var latest = rescores[0];
        Assert.Equal((Now, "Rafa", 40m, 60m, "Medium", "High", "Degraded", "Complete", result.ResultId),
            (latest.RescoredAt, latest.RescoredByName, latest.PreviousScore, latest.NewScore, latest.PreviousLevel, latest.NewLevel,
             latest.PreviousEnrichmentStatus, latest.NewEnrichmentStatus, latest.ResultId));
        Assert.All(rescores, r => Assert.Equal(
            (AnalysisKinds.Balance, (Guid?)entity.EntityId, "PyME Uno", entity.CUIT),
            (r.AnalysisKind, r.EntityId, r.EntityName, r.EntityCuit)));
    }

    [Fact]
    public async Task RecentRescores_OfAPersonaAnalysis_CarryTheSuggestionsBeforeAndAfter_ABalanceOneNone()
    {
        // Read from PreviousValuesJson exactly as GET {id}/rescores reads them (JSON_VALUE on the suggestions node).
        await using var f = DashboardFixture.Create(nameof(RecentRescores_OfAPersonaAnalysis_CarryTheSuggestionsBeforeAndAfter_ABalanceOneNone));
        var analyst = f.AddUser(Roles.Analyst, name: "Rafa");
        var entity  = f.AddEntity(name: "Persona Sintetica");
        var (_, persona) = f.AddAnalysis(analyst.UserId, entity.EntityId, a => a.AnalysisKind = AnalysisKinds.PersonaHumana);
        var personaRescore = f.AddRescore(persona, analyst.UserId, Now);
        personaRescore.PreviousScore = null;
        personaRescore.NewScore      = null;
        personaRescore.PreviousLevel = null;
        personaRescore.NewLevel      = null;
        personaRescore.PreviousValuesJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            kind        = AnalysisKinds.PersonaHumana,
            suggestions = new { previous = PersonSuggestions.RechazoSugerido, @new = PersonSuggestions.ParaDecisionDelAnalista }
        });
        var (_, balance) = f.AddAnalysis(analyst.UserId, entity.EntityId);
        f.AddRescore(balance, analyst.UserId, Now.AddHours(-1));
        await f.SaveAsync();

        var rescores = (await OversightAsync(f)).RecentRescores!;

        Assert.Equal(2, rescores.Count);
        var personaItem = rescores[0];
        Assert.Equal(AnalysisKinds.PersonaHumana, personaItem.AnalysisKind);
        Assert.Equal(PersonSuggestions.RechazoSugerido, personaItem.PreviousSuggestion);
        Assert.Equal(PersonSuggestions.ParaDecisionDelAnalista, personaItem.NewSuggestion);
        Assert.Null(personaItem.PreviousScore);

        var balanceItem = rescores[1];
        Assert.Equal(AnalysisKinds.Balance, balanceItem.AnalysisKind);
        Assert.Null(balanceItem.PreviousSuggestion);
        Assert.Null(balanceItem.NewSuggestion);
        Assert.Equal((40m, 60m), (balanceItem.PreviousScore, balanceItem.NewScore));
    }

    [Fact]
    public async Task RecentRescores_WhoseAnalysisIsInAnotherTenant_ShowNoEntity()
    {
        await using var f = DashboardFixture.Create(nameof(RecentRescores_WhoseAnalysisIsInAnotherTenant_ShowNoEntity));
        var foreignEntity = f.AddEntity(tenant: DashboardFixture.Other, name: "Ajena SA");
        var (_, foreign)  = f.AddAnalysis(Guid.NewGuid(), foreignEntity.EntityId, tenant: DashboardFixture.Other);
        var crossed       = f.AddRescore(foreign, Guid.NewGuid(), Now);
        // A tenant-1 analysis naming tenant-2's entity: the id is the row's, the name is not ours to show.
        var (_, mine)     = f.AddAnalysis(Guid.NewGuid(), foreignEntity.EntityId);
        var pointing      = f.AddRescore(mine, Guid.NewGuid(), Now.AddHours(-1));
        await f.SaveAsync();

        var rescores = (await OversightAsync(f)).RecentRescores!;

        Assert.Equal([crossed.RescoreId, pointing.RescoreId], rescores.Select(r => r.RescoreId));
        Assert.Equal(((string?)null, (Guid?)null, (string?)null, (string?)null),
            (rescores[0].AnalysisKind, rescores[0].EntityId, rescores[0].EntityName, rescores[0].EntityCuit));
        Assert.Equal(((Guid?)foreignEntity.EntityId, (string?)null, (string?)null),
            (rescores[1].EntityId, rescores[1].EntityName, rescores[1].EntityCuit));
    }

    [Fact]
    public async Task MonthStats_UseArgentineMonths_AnInstantAt0100UtcOnTheFirstBelongsToThePreviousMonth()
    {
        await using var f = DashboardFixture.Create(nameof(MonthStats_UseArgentineMonths_AnInstantAt0100UtcOnTheFirstBelongsToThePreviousMonth));
        // 01:00 UTC on 1 October is 22:00 on 30 September in Buenos Aires.
        f.AddAnalysis(Guid.NewGuid(), analysis: a => a.CreatedAt = new DateTime(2026, 10, 1, 1, 0, 0, DateTimeKind.Utc));
        f.AddAnalysis(Guid.NewGuid(), analysis: a => a.CreatedAt = new DateTime(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc));
        await f.SaveAsync();

        var stats = (await OversightAsync(f)).MonthStats!;

        Assert.Equal([(2026, 10), (2026, 9)], stats.Select(s => (s.Year, s.Month)));
        Assert.Equal([1, 1], stats.Select(s => s.Created));
        Assert.Equal(new DateTime(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc), stats[0].PeriodStartUtc);
    }

    [Fact]
    public async Task MonthStats_InJanuary_ThePreviousMonthIsDecemberOfThePreviousYear()
    {
        await using var f = DashboardFixture.Create(nameof(MonthStats_InJanuary_ThePreviousMonthIsDecemberOfThePreviousYear));
        await f.SaveAsync();

        var stats = (await OversightAsync(f, new DateTime(2027, 1, 10, 12, 0, 0, DateTimeKind.Utc))).MonthStats!;

        Assert.Equal([(2027, 1), (2026, 12)], stats.Select(s => (s.Year, s.Month)));
    }

    [Fact]
    public async Task MonthStats_DecisionHours_NullWithNone_TheValueWithOne_InterpolatedWithMany()
    {
        await using var f = DashboardFixture.Create(nameof(MonthStats_DecisionHours_NullWithNone_TheValueWithOne_InterpolatedWithMany));
        var created = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
        void Decided(DateTime createdAt, double hours, string status = AnalysisStatuses.Approved)
            => f.AddAnalysis(Guid.NewGuid(), analysis: a => { a.CreatedAt = createdAt; a.Status = status; a.ReviewedBy = Guid.NewGuid(); a.ReviewedAt = createdAt.AddHours(hours); });

        foreach (var hours in new[] { 1.0, 2, 3, 4, 10 })
            Decided(created, hours, hours == 10 ? AnalysisStatuses.Rejected : AnalysisStatuses.Approved);
        f.AddAnalysis(Guid.NewGuid(), analysis: a => a.CreatedAt = created);   // undecided
        Decided(new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc), 7);      // one decided in September
        await f.SaveAsync();

        var stats = (await OversightAsync(f)).MonthStats!;
        var october = stats[0];
        var september = stats[1];

        Assert.Equal((6, 5), (october.Created, october.Decided));
        Assert.Equal(3.0, october.MedianHoursToDecision);
        Assert.Equal(7.6, october.P90HoursToDecision);   // 4 + (10 - 4) * 0.6
        Assert.Equal((1, 7.0, 7.0), (september.Decided, september.MedianHoursToDecision, september.P90HoursToDecision));
        Assert.Equal(5, october.ByStatus.Single(s => s.Status == AnalysisStatuses.Approved).Count + october.ByStatus.Single(s => s.Status == AnalysisStatuses.Rejected).Count);

        await using var empty = DashboardFixture.Create(nameof(MonthStats_DecisionHours_NullWithNone_TheValueWithOne_InterpolatedWithMany) + "-empty");
        await empty.SaveAsync();
        var none = (await OversightAsync(empty)).MonthStats![0];
        Assert.Equal((0, 0, null, null), (none.Created, none.Decided, none.MedianHoursToDecision, none.P90HoursToDecision));
    }

    [Theory]
    [InlineData(new double[0], 0.5, null)]
    [InlineData(new[] { 5.0 }, 0.9, 5.0)]
    [InlineData(new[] { 1.0, 2.0 }, 0.5, 1.5)]
    [InlineData(new[] { 1.0, 2.0, 3.0, 4.0 }, 0.9, 3.7)]
    public void Percentile_InterpolatesLinearly(double[] sorted, double p, double? expected)
    {
        Assert.Equal(expected, DashboardService.Percentile(sorted, p));
    }

    [Fact]
    public async Task IsTenantIsolated()
    {
        await using var f = DashboardFixture.Create(nameof(IsTenantIsolated));
        var other = DashboardFixture.Other;
        var (_, result) = f.AddAnalysis(Guid.NewGuid(), tenant: other);
        f.AddPreApproved(Guid.NewGuid(), tenant: other);
        f.AddAnalysis(Guid.NewGuid(), analysis: a => { a.Status = AnalysisStatuses.Approved; a.ReviewedBy = Guid.NewGuid(); a.ReviewedAt = Now; },
            result: r => r.AiSuggestedLevel = "Critical", tenant: other);
        f.AddRescore(result, Guid.NewGuid(), Now, tenant: other);
        await f.SaveAsync();

        var response = await OversightAsync(f);

        Assert.Equal((0, 0), (response.TeamQueue!.TotalCount, response.TeamQueue.PreApprovedCount));
        Assert.Empty(response.TeamQueue.ByRequester);
        Assert.Equal(0, response.AgainstSuggestion!.TotalCount);
        Assert.Empty(response.RecentDecisions!);
        Assert.Empty(response.RecentRescores!);
        Assert.All(response.MonthStats!, s => Assert.Equal(0, s.Created));
    }

    [Fact]
    public async Task AFailingSection_IsNullAndListed_TheRestStillReturns()
    {
        await using var f = DashboardFixture.Create(nameof(AFailingSection_IsNullAndListed_TheRestStillReturns));
        f.AddAnalysis(Guid.NewGuid());
        await f.SaveAsync();

        var failing  = new FailingDashboardRepository(f.Repository(), nameof(IDashboardRepository.GetDecisionTimesAsync));
        var response = await OversightAsync(f, repository: failing);

        Assert.Equal([DashboardSections.MonthStats], response.DegradedSections);
        Assert.Null(response.MonthStats);
        Assert.Equal(1, response.TeamQueue!.TotalCount);
        Assert.NotNull(response.AgainstSuggestion);
        Assert.NotNull(response.RecentDecisions);
        Assert.NotNull(response.RecentRescores);
    }

    [Fact]
    public async Task Cancellation_IsNotASectionFailure()
    {
        await using var f = DashboardFixture.Create(nameof(Cancellation_IsNotASectionFailure));
        await f.SaveAsync();

        var cancelling = new FailingDashboardRepository(f.Repository(), nameof(IDashboardRepository.GetRecentRescoresAsync))
        {
            Throw = () => new OperationCanceledException()
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() => OversightAsync(f, repository: cancelling));
    }
}
