// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-23

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// <see cref="DashboardService"/> over real repositories: the eligibility rules it borrows (canRescore,
/// the self-confirmation exclusion, the quorum), the act derivation of decidedByMe, tenant isolation,
/// and the section isolation that keeps one failure from costing the whole response.
/// </summary>
public sealed class DashboardServiceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    // ── my-work ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task MyWork_Pending_CarriesTheItemAndTheDetailsCanRescore()
    {
        await using var f = DashboardFixture.Create(nameof(MyWork_Pending_CarriesTheItemAndTheDetailsCanRescore));
        var me     = f.AddUser(Roles.Analyst);
        var entity = f.AddEntity(name: "Metalúrgica Sur SA");
        var ready  = f.AddDocument(entity.EntityId, "Completed");
        var busy   = f.AddDocument(entity.EntityId, "Processing");

        var (eligible, eligibleResult) = f.AddAnalysis(me.UserId, entity.EntityId, a =>
        {
            a.DocumentId      = ready.DocumentId;
            a.RequestedAmount = 5_000_000m;
            a.CreatedAt       = DashboardFixture.Now.AddDays(-3).AddHours(-2);
        }, r => r.AiSuggestedLevel = "High");
        var (processing, _) = f.AddAnalysis(me.UserId, entity.EntityId, a => a.DocumentId = busy.DocumentId);
        var (noBalance, _)  = f.AddAnalysis(me.UserId, entity.EntityId, result: r => r.BalanceJson = null);
        var (persona, _)    = f.AddAnalysis(me.UserId, entity.EntityId, a => a.AnalysisKind = AnalysisKinds.PersonaHumana);
        await f.SaveAsync();

        var response = await f.Service().GetMyWorkAsync(DashboardFixture.Tenant, me, DashboardFixture.Now, Ct);

        Assert.Empty(response.DegradedSections);
        Assert.Equal(DashboardFixture.Now, response.GeneratedAt);
        Assert.NotNull(response.Pending);
        Assert.Equal(4, response.Pending.TotalCount);
        Assert.Equal(4, response.Counts.Pending);

        var item = response.Pending.Items.Single(i => i.AnalysisId == eligible.AnalysisId);
        Assert.Equal(eligibleResult.ResultId, item.ResultId);
        Assert.Equal(AnalysisKinds.Balance, item.AnalysisKind);
        Assert.Equal(entity.EntityId, item.EntityId);
        Assert.Equal("Metalúrgica Sur SA", item.EntityName);
        Assert.Equal(entity.CUIT, item.EntityCuit);
        Assert.Equal(5_000_000m, item.RequestedAmount);
        Assert.Equal("High", item.AiSuggestedLevel);
        Assert.Equal(50m, item.AiSuggestedScore);
        Assert.Equal(3, item.AgeDays);
        Assert.Equal(nameof(BcraEnrichmentOutcome.Complete), item.EnrichmentStatus);
        Assert.True(item.CanRescore);

        Assert.False(response.Pending.Items.Single(i => i.AnalysisId == processing.AnalysisId).CanRescore);
        Assert.False(response.Pending.Items.Single(i => i.AnalysisId == noBalance.AnalysisId).CanRescore);
        // A pending persona analysis is re-evaluated in place by the same endpoint, so its button is live too.
        Assert.True(response.Pending.Items.Single(i => i.AnalysisId == persona.AnalysisId).CanRescore);
        // High sorts first.
        Assert.Equal(eligible.AnalysisId, response.Pending.Items[0].AnalysisId);
    }

    [Fact]
    public async Task MyWork_BcraDegraded_CountsOnlyDegradedOnesThatCanBeRescored()
    {
        await using var f = DashboardFixture.Create(nameof(MyWork_BcraDegraded_CountsOnlyDegradedOnesThatCanBeRescored));
        var me     = f.AddUser(Roles.Analyst);
        var entity = f.AddEntity();
        var busy   = f.AddDocument(entity.EntityId, "Processing");
        const string degraded = nameof(BcraEnrichmentOutcome.Degraded);

        f.AddAnalysis(me.UserId, entity.EntityId, result: r => r.EnrichmentStatus = degraded);
        f.AddAnalysis(me.UserId, entity.EntityId, result: r => r.EnrichmentStatus = degraded);
        f.AddAnalysis(me.UserId, entity.EntityId, a => a.DocumentId = busy.DocumentId, r => r.EnrichmentStatus = degraded);
        f.AddAnalysis(me.UserId, entity.EntityId, result: r => { r.EnrichmentStatus = degraded; r.BalanceJson = ""; });
        f.AddAnalysis(me.UserId, entity.EntityId);
        await f.SaveAsync();

        var response = await f.Service().GetMyWorkAsync(DashboardFixture.Tenant, me, DashboardFixture.Now, Ct);

        Assert.Equal(2, response.Counts.BcraDegraded);
        Assert.Equal(5, response.Counts.Pending);
        // The flag on the items agrees with the badge.
        Assert.Equal(2, response.Pending!.Items.Count(i => i.EnrichmentStatus == degraded && i.CanRescore));
    }

    [Fact]
    public async Task MyWork_Returned_NamesTheReviewerAndCarriesTheNotes()
    {
        await using var f = DashboardFixture.Create(nameof(MyWork_Returned_NamesTheReviewerAndCarriesTheNotes));
        var me = f.AddUser(Roles.Analyst);
        var rm = f.AddUser(Roles.RiskManager, name: "Lucía Gómez");
        var (returned, _) = f.AddAnalysis(me.UserId, analysis: a =>
        {
            a.PreApprovedBy = me.UserId;
            a.PreApprovedAt = DashboardFixture.Now.AddDays(-2);
            a.ReviewedBy    = rm.UserId;
            a.ReviewedAt    = DashboardFixture.Now.AddHours(-3);
            a.ReviewNotes   = "Actualizar la deuda bancaria.";
        });
        await f.SaveAsync();

        var response = await f.Service().GetMyWorkAsync(DashboardFixture.Tenant, me, DashboardFixture.Now, Ct);

        var item = Assert.Single(response.Returned!.Items);
        Assert.Equal(returned.AnalysisId, item.AnalysisId);
        Assert.Equal("Lucía Gómez", item.ReviewedByName);
        Assert.Equal(DashboardFixture.Now.AddHours(-3), item.ReviewedAt);
        Assert.Equal("Actualizar la deuda bancaria.", item.ReviewNotes);
        Assert.Equal(1, response.Counts.Returned);
        // A returned analysis is not pending: the two sets never overlap.
        Assert.Equal(0, response.Counts.Pending);
    }

    [Fact]
    public async Task MyWork_AwaitingSignature_ShowsTheTallyOnlyWhenAQuorumIsInForce()
    {
        await using var f = DashboardFixture.Create(nameof(MyWork_AwaitingSignature_ShowsTheTallyOnlyWhenAQuorumIsInForce));
        var m1   = f.AddUser(Roles.RiskManager);
        var m2   = f.AddUser(Roles.RiskManager);
        var team = f.AddTeam(2, [m1.UserId, m2.UserId]);
        var me   = f.AddUser(Roles.Analyst, team.TeamId);
        var loner = f.AddUser(Roles.Analyst);

        var (mine, _) = f.AddPreApproved(me.UserId, DashboardFixture.Now.AddDays(-1));
        f.AddVote(mine.AnalysisId, m1.UserId);
        var (lonely, _) = f.AddPreApproved(loner.UserId);
        await f.SaveAsync();

        var withQuorum = await f.Service().GetMyWorkAsync(DashboardFixture.Tenant, me, DashboardFixture.Now, Ct);
        var item = Assert.Single(withQuorum.AwaitingSignature!.Items);
        Assert.Equal(mine.AnalysisId, item.AnalysisId);
        Assert.Equal(1, item.ApprovalCount);
        Assert.Equal(2, item.ApprovalQuorum);
        Assert.Equal(1_000_000m, item.PreApprovedAmount);
        Assert.Equal("ARS", item.PreApprovedCurrency);
        Assert.Equal(1, withQuorum.Counts.AwaitingSignature);

        var noQuorum = await f.Service().GetMyWorkAsync(DashboardFixture.Tenant, loner, DashboardFixture.Now, Ct);
        var lonelyItem = Assert.Single(noQuorum.AwaitingSignature!.Items);
        Assert.Equal(lonely.AnalysisId, lonelyItem.AnalysisId);
        Assert.Null(lonelyItem.ApprovalCount);
        Assert.Null(lonelyItem.ApprovalQuorum);
    }

    [Fact]
    public async Task MyWork_Recent_IsCappedAtEight()
    {
        await using var f = DashboardFixture.Create(nameof(MyWork_Recent_IsCappedAtEight));
        var me = f.AddUser(Roles.Analyst);
        for (var i = 0; i < 12; i++)
        {
            var days = i;
            f.AddAnalysis(me.UserId, analysis: a => a.CreatedAt = DashboardFixture.Now.AddDays(-days));
        }
        await f.SaveAsync();

        var response = await f.Service().GetMyWorkAsync(DashboardFixture.Tenant, me, DashboardFixture.Now, Ct);

        Assert.Equal(8, response.Recent!.Count);
        Assert.Equal(DashboardFixture.Now, response.Recent[0].CreatedAt);
        Assert.Equal(12, response.Counts.Pending);
    }

    [Fact]
    public async Task MyWork_IsTenantIsolated()
    {
        // The same user id with rows in another tenant: none of them may surface.
        await using var f = DashboardFixture.Create(nameof(MyWork_IsTenantIsolated));
        var me = f.AddUser(Roles.Analyst);
        var rm = f.AddUser(Roles.RiskManager);
        f.AddAnalysis(me.UserId, tenant: DashboardFixture.Other);
        f.AddAnalysis(me.UserId, analysis: a => { a.ReviewedBy = rm.UserId; a.ReviewedAt = DashboardFixture.Now; }, tenant: DashboardFixture.Other);
        f.AddPreApproved(me.UserId, tenant: DashboardFixture.Other);
        f.AddAnalysis(me.UserId, result: r => r.EnrichmentStatus = nameof(BcraEnrichmentOutcome.Degraded), tenant: DashboardFixture.Other);
        await f.SaveAsync();

        var response = await f.Service().GetMyWorkAsync(DashboardFixture.Tenant, me, DashboardFixture.Now, Ct);

        Assert.Empty(response.DegradedSections);
        Assert.Equal(0, response.Counts.Pending);
        Assert.Equal(0, response.Counts.Returned);
        Assert.Equal(0, response.Counts.BcraDegraded);
        Assert.Equal(0, response.Counts.AwaitingSignature);
        Assert.Empty(response.Recent!);
    }

    [Fact]
    public async Task MyWork_AFailingSection_IsNullAndListed_TheRestStillReturns()
    {
        await using var f = DashboardFixture.Create(nameof(MyWork_AFailingSection_IsNullAndListed_TheRestStillReturns));
        var me = f.AddUser(Roles.Analyst);
        var rm = f.AddUser(Roles.RiskManager);
        f.AddAnalysis(me.UserId);
        f.AddAnalysis(me.UserId, analysis: a => { a.ReviewedBy = rm.UserId; a.ReviewedAt = DashboardFixture.Now; });
        f.AddPreApproved(me.UserId);
        await f.SaveAsync();

        var failing = new FailingDashboardRepository(f.Repository(), nameof(IDashboardRepository.GetReturnedAsync));
        var response = await f.Service(failing).GetMyWorkAsync(DashboardFixture.Tenant, me, DashboardFixture.Now, Ct);

        Assert.Equal([DashboardSections.Returned], response.DegradedSections);
        Assert.Null(response.Returned);
        Assert.Null(response.Counts.Returned);

        Assert.Equal(1, response.Counts.Pending);
        Assert.Equal(1, response.Counts.AwaitingSignature);
        Assert.Equal(0, response.Counts.BcraDegraded);
        Assert.Equal(3, response.Recent!.Count);
    }

    [Fact]
    public async Task MyWork_EverySectionFailing_StillAnswers()
    {
        await using var f = DashboardFixture.Create(nameof(MyWork_EverySectionFailing_StillAnswers));
        var me = f.AddUser(Roles.Analyst);
        await f.SaveAsync();

        var failing = new FailingDashboardRepository(f.Repository(),
            nameof(IDashboardRepository.GetMyPendingAsync), nameof(IDashboardRepository.GetReturnedAsync),
            nameof(IDashboardRepository.GetMyPendingDegradedAsync), nameof(IDashboardRepository.GetAwaitingMySignatureAsync),
            nameof(IDashboardRepository.GetMyRecentAsync));
        var response = await f.Service(failing).GetMyWorkAsync(DashboardFixture.Tenant, me, DashboardFixture.Now, Ct);

        Assert.Equal(
            [DashboardSections.Pending, DashboardSections.Returned, DashboardSections.BcraDegraded,
             DashboardSections.AwaitingSignature, DashboardSections.Recent],
            response.DegradedSections);
        Assert.Null(response.Counts.Pending);
        Assert.Null(response.Counts.BcraDegraded);
        Assert.Null(response.Pending);
        Assert.Null(response.Recent);
    }

    [Fact]
    public async Task MyWork_Cancellation_IsNotASectionFailure()
    {
        await using var f = DashboardFixture.Create(nameof(MyWork_Cancellation_IsNotASectionFailure));
        var me = f.AddUser(Roles.Analyst);
        await f.SaveAsync();

        var cancelling = new FailingDashboardRepository(f.Repository(), nameof(IDashboardRepository.GetMyPendingAsync))
        {
            Throw = () => new OperationCanceledException()
        };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => f.Service(cancelling).GetMyWorkAsync(DashboardFixture.Tenant, me, DashboardFixture.Now, Ct));
    }

    // ── approvals ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Approvals_WaitingForMe_ExcludesWhatIAlreadyApproved_NotWhatAColleagueDid()
    {
        await using var f = DashboardFixture.Create(nameof(Approvals_WaitingForMe_ExcludesWhatIAlreadyApproved_NotWhatAColleagueDid));
        var me        = f.AddUser(Roles.RiskManager);
        var colleague = f.AddUser(Roles.Admin);
        var analyst   = f.AddUser(Roles.Analyst);

        var (signedByMe, _)        = f.AddPreApproved(analyst.UserId);
        var (signedByColleague, _) = f.AddPreApproved(analyst.UserId);
        var (unsigned, _)          = f.AddPreApproved(analyst.UserId);
        f.AddVote(signedByMe.AnalysisId, me.UserId);
        f.AddVote(signedByColleague.AnalysisId, colleague.UserId);
        f.AddPreApproved(analyst.UserId, tenant: DashboardFixture.Other);
        await f.SaveAsync();

        var response = await f.Service().GetApprovalsAsync(DashboardFixture.Tenant, me, DashboardFixture.Now, Ct);

        Assert.Equal(
            new[] { signedByColleague.AnalysisId, unsigned.AnalysisId }.ToHashSet(),
            response.WaitingForMe!.Items.Select(i => i.AnalysisId).ToHashSet());
        Assert.Equal(2, response.Counts.WaitingForMe);
    }

    [Fact]
    public async Task Approvals_SelfConfirmation_ExcludesOnlyWhatDecideAsyncRefuses()
    {
        await using var f = DashboardFixture.Create(nameof(Approvals_SelfConfirmation_ExcludesOnlyWhatDecideAsyncRefuses));
        var me    = f.AddUser(Roles.RiskManager);
        var other = f.AddUser(Roles.RiskManager);

        // Persona humana, suggested rejection, flag on by default, pre-approved by ME → 403 on PATCH → excluded.
        var (myRejection, _) = f.AddPreApproved(me.UserId, kind: AnalysisKinds.PersonaHumana);
        f.AddSolicitante(myRejection.AnalysisId, PersonFilterOutcomes.Rechazo);

        // The same, pre-approved by someone else → I may confirm it → included.
        var (theirRejection, _) = f.AddPreApproved(other.UserId, kind: AnalysisKinds.PersonaHumana);
        f.AddSolicitante(theirRejection.AnalysisId, PersonFilterOutcomes.Rechazo);

        // Persona humana pre-approved by me WITHOUT a suggested rejection → no rule applies → included.
        var (myClean, _) = f.AddPreApproved(me.UserId, kind: AnalysisKinds.PersonaHumana);
        f.AddSolicitante(myClean.AnalysisId, PersonFilterOutcomes.Ok);

        // A BALANCE analysis pre-approved by me (a confirmer since promoted) → DecideAsync has no such rule → included.
        var (myBalance, _) = f.AddPreApproved(me.UserId);
        await f.SaveAsync();

        var response = await f.Service().GetApprovalsAsync(DashboardFixture.Tenant, me, DashboardFixture.Now, Ct);
        var ids = response.WaitingForMe!.Items.Select(i => i.AnalysisId).ToHashSet();

        Assert.DoesNotContain(myRejection.AnalysisId, ids);
        Assert.Contains(theirRejection.AnalysisId, ids);
        Assert.Contains(myClean.AnalysisId, ids);
        Assert.Contains(myBalance.AnalysisId, ids);
        Assert.Equal(3, response.Counts.WaitingForMe);
        Assert.Equal(1, response.Counts.SuggestedRejections);
    }

    [Fact]
    public async Task Approvals_SelfConfirmation_WithTheTenantFlagOff_IsNotExcluded()
    {
        await using var f = DashboardFixture.Create(nameof(Approvals_SelfConfirmation_WithTheTenantFlagOff_IsNotExcluded));
        var me = f.AddUser(Roles.RiskManager);
        f.AddConfig("pf_rechazo_sugerido_requiere_preaprobacion", 0m);
        f.AddConfig("pf_rechazo_sugerido_requiere_preaprobacion", 1m, DashboardFixture.Other);
        var (myRejection, _) = f.AddPreApproved(me.UserId, kind: AnalysisKinds.PersonaHumana);
        f.AddSolicitante(myRejection.AnalysisId, PersonFilterOutcomes.Rechazo);
        await f.SaveAsync();

        var response = await f.Service().GetApprovalsAsync(DashboardFixture.Tenant, me, DashboardFixture.Now, Ct);

        var item = Assert.Single(response.WaitingForMe!.Items);
        Assert.Equal(myRejection.AnalysisId, item.AnalysisId);
        Assert.Equal(PersonSuggestions.RechazoSugerido, item.PersonSuggestion);
        Assert.Equal(1, response.Counts.SuggestedRejections);
    }

    [Fact]
    public async Task Approvals_PersonSuggestion_IsDerivedFromTheSolicitantesFilters()
    {
        await using var f = DashboardFixture.Create(nameof(Approvals_PersonSuggestion_IsDerivedFromTheSolicitantesFilters));
        var me      = f.AddUser(Roles.Admin);
        var analyst = f.AddUser(Roles.Analyst);

        var (rejection, _)   = f.AddPreApproved(analyst.UserId, kind: AnalysisKinds.PersonaHumana);
        var (analystCall, _) = f.AddPreApproved(analyst.UserId, kind: AnalysisKinds.PersonaHumana);
        var (noSolicitante, _) = f.AddPreApproved(analyst.UserId, kind: AnalysisKinds.PersonaHumana);
        var (balance, _)     = f.AddPreApproved(analyst.UserId);
        f.AddSolicitante(rejection.AnalysisId, PersonFilterOutcomes.Rechazo);
        f.AddSolicitante(analystCall.AnalysisId, PersonFilterOutcomes.Alerta);
        await f.SaveAsync();

        var response = await f.Service().GetApprovalsAsync(DashboardFixture.Tenant, me, DashboardFixture.Now, Ct);
        var byId = response.WaitingForMe!.Items.ToDictionary(i => i.AnalysisId);

        Assert.Equal(PersonSuggestions.RechazoSugerido, byId[rejection.AnalysisId].PersonSuggestion);
        Assert.Equal(PersonSuggestions.ParaDecisionDelAnalista, byId[analystCall.AnalysisId].PersonSuggestion);
        Assert.Null(byId[noSolicitante.AnalysisId].PersonSuggestion);
        Assert.Null(byId[balance.AnalysisId].PersonSuggestion);
        Assert.Equal(1, response.Counts.SuggestedRejections);
    }

    [Fact]
    public async Task Approvals_WaitingForMe_OldestFirst_Capped_WithTotalCount()
    {
        await using var f = DashboardFixture.Create(nameof(Approvals_WaitingForMe_OldestFirst_Capped_WithTotalCount));
        var me      = f.AddUser(Roles.RiskManager);
        var analyst = f.AddUser(Roles.Analyst);
        for (var i = 0; i < 55; i++) f.AddPreApproved(analyst.UserId, DashboardFixture.Now.AddHours(-i));
        await f.SaveAsync();

        var response = await f.Service().GetApprovalsAsync(DashboardFixture.Tenant, me, DashboardFixture.Now, Ct);

        Assert.Equal(55, response.WaitingForMe!.TotalCount);
        Assert.Equal(55, response.Counts.WaitingForMe);
        Assert.Equal(50, response.WaitingForMe.Items.Count);
        Assert.Equal(DashboardFixture.Now.AddHours(-54), response.WaitingForMe.Items[0].PreApprovedAt);
        Assert.True(response.WaitingForMe.Items.Zip(response.WaitingForMe.Items.Skip(1))
            .All(p => p.First.PreApprovedAt <= p.Second.PreApprovedAt));
    }

    [Fact]
    public async Task Approvals_WaitingForMe_CarriesTallyNamesAgeAndInMyTeam()
    {
        await using var f = DashboardFixture.Create(nameof(Approvals_WaitingForMe_CarriesTallyNamesAgeAndInMyTeam));
        var me        = f.AddUser(Roles.RiskManager);
        var colleague = f.AddUser(Roles.RiskManager);
        var myTeam    = f.AddTeam(2, [me.UserId, colleague.UserId]);
        var legacyTeam = f.AddTeam(1, [], legacyManagerId: me.UserId);
        var otherTeam = f.AddTeam(1, [colleague.UserId]);
        var mine      = f.AddUser(Roles.Analyst, myTeam.TeamId, name: "Ana Pérez");
        var legacy    = f.AddUser(Roles.Analyst, legacyTeam.TeamId);
        var theirs    = f.AddUser(Roles.Analyst, otherTeam.TeamId);
        var entity    = f.AddEntity(name: "Lácteos del Oeste SRL");

        var (inTeam, _)   = f.AddPreApproved(mine.UserId, DashboardFixture.Now.AddDays(-4).AddHours(-1), entity.EntityId);
        var (viaLegacy, _) = f.AddPreApproved(legacy.UserId);
        var (outside, _)  = f.AddPreApproved(theirs.UserId);
        f.AddVote(inTeam.AnalysisId, colleague.UserId);
        await f.SaveAsync();

        var response = await f.Service().GetApprovalsAsync(DashboardFixture.Tenant, me, DashboardFixture.Now, Ct);
        var byId = response.WaitingForMe!.Items.ToDictionary(i => i.AnalysisId);

        var item = byId[inTeam.AnalysisId];
        Assert.True(item.InMyTeam);
        Assert.Equal(1, item.ApprovalCount);
        Assert.Equal(2, item.ApprovalQuorum);
        Assert.Equal(mine.UserId, item.PreApprovedBy);
        Assert.Equal("Ana Pérez", item.PreApprovedByName);
        Assert.Equal("Lácteos del Oeste SRL", item.EntityName);
        Assert.Equal(entity.CUIT, item.EntityCuit);
        Assert.Equal(4, item.AgeDays);
        Assert.Equal(1_000_000m, item.PreApprovedAmount);
        Assert.Equal("ARS", item.PreApprovedCurrency);
        Assert.Equal("Medium", item.AiSuggestedLevel);

        Assert.True(byId[viaLegacy.AnalysisId].InMyTeam);
        // Not my team, and still listed: any confirmer's vote counts.
        Assert.False(byId[outside.AnalysisId].InMyTeam);
    }

    [Fact]
    public async Task Approvals_DecidedByMe_DerivesOnlyTheActsTheRowsProve()
    {
        await using var f = DashboardFixture.Create(nameof(Approvals_DecidedByMe_DerivesOnlyTheActsTheRowsProve));
        var me      = f.AddUser(Roles.RiskManager);
        var analyst = f.AddUser(Roles.Analyst);
        var now     = DashboardFixture.Now;

        // A vote still waiting for the quorum.
        var (voted, _) = f.AddPreApproved(analyst.UserId);
        f.AddVote(voted.AnalysisId, me.UserId, votedAt: now.AddHours(-1));

        // The vote that crossed the quorum: vote and confirmation share the instant → Confirmed.
        var (confirmed, _) = f.AddAnalysis(analyst.UserId, analysis: a =>
        {
            a.Status = AnalysisStatuses.Approved; a.PreApprovedBy = analyst.UserId; a.PreApprovedAt = now.AddDays(-2);
            a.ReviewedBy = me.UserId; a.ReviewedAt = now.AddHours(-2);
        });
        f.AddVote(confirmed.AnalysisId, me.UserId, votedAt: now.AddHours(-2));

        // A one-step approval of an analysis that was never pre-approved.
        var (approved, _) = f.AddAnalysis(analyst.UserId, analysis: a =>
        {
            a.Status = AnalysisStatuses.Approved; a.ReviewedBy = me.UserId; a.ReviewedAt = now.AddHours(-3);
        });

        // A veto: the Rejected vote row and the rejection.
        var (vetoed, _) = f.AddAnalysis(analyst.UserId, analysis: a =>
        {
            a.Status = AnalysisStatuses.Rejected; a.PreApprovedBy = analyst.UserId; a.PreApprovedAt = now.AddDays(-2);
            a.ReviewedBy = me.UserId; a.ReviewedAt = now.AddHours(-4);
        });
        f.AddVote(vetoed.AnalysisId, me.UserId, AnalysisVoteValues.Rejected, now.AddHours(-4));

        // A return for rework.
        var (returned, _) = f.AddAnalysis(analyst.UserId, analysis: a =>
        {
            a.PreApprovedBy = analyst.UserId; a.PreApprovedAt = now.AddDays(-2);
            a.ReviewedBy = me.UserId; a.ReviewedAt = now.AddHours(-5);
        });

        // Unknowable: back in PreApproved with my ReviewedBy from an earlier act → dropped.
        var (superseded, _) = f.AddPreApproved(analyst.UserId);
        superseded.ReviewedBy = me.UserId;
        superseded.ReviewedAt = now.AddHours(-6);

        // Outside the 30-day window → dropped.
        f.AddAnalysis(analyst.UserId, analysis: a =>
        {
            a.Status = AnalysisStatuses.Rejected; a.ReviewedBy = me.UserId; a.ReviewedAt = now.AddDays(-31);
        });
        await f.SaveAsync();

        var response = await f.Service().GetApprovalsAsync(DashboardFixture.Tenant, me, now, Ct);
        var acts = response.DecidedByMe!;

        Assert.Equal(
            [voted.AnalysisId, confirmed.AnalysisId, approved.AnalysisId, vetoed.AnalysisId, returned.AnalysisId],
            acts.Select(a => a.AnalysisId).ToList());
        Assert.Equal(
            [DashboardActs.ApprovalVote, DashboardActs.Confirmed, DashboardActs.Approved, DashboardActs.Rejected, DashboardActs.Returned],
            acts.Select(a => a.Act).ToList());
        Assert.Equal(now.AddHours(-1), acts[0].ActedAt);
        Assert.Equal(AnalysisStatuses.PreApproved, acts[0].Status);
        Assert.Equal(AnalysisStatuses.PendingReview, acts[4].Status);
    }

    [Fact]
    public async Task Approvals_DecidedByMe_IsCappedAtTenNewestFirst()
    {
        await using var f = DashboardFixture.Create(nameof(Approvals_DecidedByMe_IsCappedAtTenNewestFirst));
        var me      = f.AddUser(Roles.RiskManager);
        var analyst = f.AddUser(Roles.Analyst);
        for (var i = 0; i < 14; i++)
        {
            var hours = i;
            f.AddAnalysis(analyst.UserId, analysis: a =>
            {
                a.Status = AnalysisStatuses.Rejected; a.ReviewedBy = me.UserId; a.ReviewedAt = DashboardFixture.Now.AddHours(-hours);
            });
        }
        await f.SaveAsync();

        var response = await f.Service().GetApprovalsAsync(DashboardFixture.Tenant, me, DashboardFixture.Now, Ct);

        Assert.Equal(10, response.DecidedByMe!.Count);
        Assert.Equal(DashboardFixture.Now, response.DecidedByMe[0].ActedAt);
        Assert.Equal(DashboardFixture.Now.AddHours(-9), response.DecidedByMe[9].ActedAt);
    }

    [Fact]
    public async Task Approvals_AFailingSection_IsNullAndListed_TheRestStillReturns()
    {
        await using var f = DashboardFixture.Create(nameof(Approvals_AFailingSection_IsNullAndListed_TheRestStillReturns));
        var me      = f.AddUser(Roles.RiskManager);
        var analyst = f.AddUser(Roles.Analyst);
        f.AddPreApproved(analyst.UserId);
        f.AddAnalysis(analyst.UserId, analysis: a =>
        {
            a.Status = AnalysisStatuses.Rejected; a.ReviewedBy = me.UserId; a.ReviewedAt = DashboardFixture.Now;
        });
        await f.SaveAsync();

        var failing = new FailingDashboardRepository(f.Repository(), nameof(IDashboardRepository.GetPreApprovedAsync));
        var response = await f.Service(failing).GetApprovalsAsync(DashboardFixture.Tenant, me, DashboardFixture.Now, Ct);

        Assert.Equal([DashboardSections.WaitingForMe], response.DegradedSections);
        Assert.Null(response.WaitingForMe);
        Assert.Null(response.Counts.WaitingForMe);
        Assert.Null(response.Counts.SuggestedRejections);
        Assert.Single(response.DecidedByMe!);
    }
}
