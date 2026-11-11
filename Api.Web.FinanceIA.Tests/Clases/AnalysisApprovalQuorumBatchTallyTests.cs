// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-23

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models.Financia;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// <see cref="IAnalysisApprovalQuorumService.GetTalliesAsync"/> — the batch read the approvals dashboard
/// uses — must report, for every analysis, EXACTLY what <see cref="IAnalysisApprovalQuorumService.GetTallyAsync"/>
/// reports for it alone. GetTallyAsync is the call GET /api/analyses/{id} and the decision path make,
/// so any drift here would show a manager "1 de 2" on the dashboard and "2 de 2" on the detail.
/// </summary>
/// <remarks>
/// One tenant holding every team shape the engine distinguishes, over REAL repositories, so both
/// paths read the same rows through their own queries.
/// </remarks>
public sealed class AnalysisApprovalQuorumBatchTallyTests
{
    [Fact]
    public async Task GetTallies_EqualsGetTally_ForEveryTeamShape()
    {
        await using var f = DashboardFixture.Create(nameof(GetTallies_EqualsGetTally_ForEveryTeamShape));

        var expectations = new List<(Guid AnalysisId, int Required, bool InForce, int Approved)>();

        // Analyst with no team at all → no quorum.
        var loner = f.AddUser(Roles.Analyst);
        var rmA   = f.AddUser(Roles.RiskManager);
        var (noTeam, _) = f.AddPreApproved(loner.UserId);
        f.AddVote(noTeam.AnalysisId, rmA.UserId);
        expectations.Add((noTeam.AnalysisId, 1, false, 1));

        // A team naming no managers → no quorum, whatever its configured number.
        var emptyTeam   = f.AddTeam(2, []);
        var emptyMember = f.AddUser(Roles.Analyst, emptyTeam.TeamId);
        var (onEmpty, _) = f.AddPreApproved(emptyMember.UserId);
        expectations.Add((onEmpty.AnalysisId, 1, false, 0));

        // Quorum 3 with two eligible managers → capped at 2; one counting vote, one Rejected that does not.
        var m1 = f.AddUser(Roles.RiskManager);
        var m2 = f.AddUser(Roles.Admin);
        var cappedTeam = f.AddTeam(3, [m1.UserId, m2.UserId]);
        var cappedMember = f.AddUser(Roles.Analyst, cappedTeam.TeamId);
        var (capped, _) = f.AddPreApproved(cappedMember.UserId);
        f.AddVote(capped.AnalysisId, m1.UserId);
        f.AddVote(capped.AnalysisId, m2.UserId, AnalysisVoteValues.Rejected);
        expectations.Add((capped.AnalysisId, 2, true, 1));

        // A team naming an Analyst and a deactivated RiskManager beside one active RiskManager →
        // sized from the one who can sign. Votes by the deactivated and by a demoted user drop out;
        // an OUTSIDER confirmer's vote counts.
        var namedAnalyst = f.AddUser(Roles.Analyst);
        var inactiveRm   = f.AddUser(Roles.RiskManager, isActive: false);
        var activeRm     = f.AddUser(Roles.RiskManager);
        var demoted      = f.AddUser(Roles.Analyst);
        var outsider     = f.AddUser(Roles.RiskManager);
        var mixedTeam    = f.AddTeam(2, [namedAnalyst.UserId, inactiveRm.UserId, activeRm.UserId]);
        var mixedMember  = f.AddUser(Roles.Analyst, mixedTeam.TeamId);
        var (mixed, _)   = f.AddPreApproved(mixedMember.UserId);
        f.AddVote(mixed.AnalysisId, inactiveRm.UserId);
        f.AddVote(mixed.AnalysisId, demoted.UserId);
        f.AddVote(mixed.AnalysisId, outsider.UserId);
        expectations.Add((mixed.AnalysisId, 1, true, 1));

        // The deprecated Teams.ManagerUserId alone still names a manager.
        var legacyRm     = f.AddUser(Roles.RiskManager);
        var legacyTeam   = f.AddTeam(1, [], legacyManagerId: legacyRm.UserId);
        var legacyMember = f.AddUser(Roles.Analyst, legacyTeam.TeamId);
        var (legacy, _)  = f.AddPreApproved(legacyMember.UserId);
        expectations.Add((legacy.AnalysisId, 1, true, 0));

        // A stored quorum of 0 is floored to 1.
        var z1 = f.AddUser(Roles.RiskManager);
        var z2 = f.AddUser(Roles.RiskManager);
        var zeroTeam   = f.AddTeam(0, [z1.UserId, z2.UserId]);
        var zeroMember = f.AddUser(Roles.Analyst, zeroTeam.TeamId);
        var (zero, _)  = f.AddPreApproved(zeroMember.UserId);
        expectations.Add((zero.AnalysisId, 1, true, 0));

        // Quorum 2 of 2, two counting votes, plus a vote recorded under ANOTHER tenant that must not count.
        var q1 = f.AddUser(Roles.RiskManager);
        var q2 = f.AddUser(Roles.RiskManager);
        var fullTeam   = f.AddTeam(2, [q1.UserId, q2.UserId]);
        var fullMember = f.AddUser(Roles.Analyst, fullTeam.TeamId);
        var (full, _)  = f.AddPreApproved(fullMember.UserId);
        f.AddVote(full.AnalysisId, q1.UserId);
        f.AddVote(full.AnalysisId, q2.UserId);
        f.AddVote(full.AnalysisId, rmA.UserId, tenant: DashboardFixture.Other);
        expectations.Add((full.AnalysisId, 2, true, 2));

        // A pre-approver whose row belongs to another tenant → no quorum, as on the single path.
        var foreignTeam   = f.AddTeam(2, [q1.UserId, q2.UserId], tenant: DashboardFixture.Other);
        var foreignAnalyst = f.AddUser(Roles.Analyst, foreignTeam.TeamId, tenant: DashboardFixture.Other);
        var (foreign, _)  = f.AddPreApproved(foreignAnalyst.UserId);
        expectations.Add((foreign.AnalysisId, 1, false, 0));

        // A team id with no row and no managers → no quorum.
        var orphan = f.AddUser(Roles.Analyst, teamId: Guid.NewGuid());
        var (orphaned, _) = f.AddPreApproved(orphan.UserId);
        expectations.Add((orphaned.AnalysisId, 1, false, 0));

        // A DEACTIVATED pre-approver still sizes the quorum from their team.
        var leaver = f.AddUser(Roles.Analyst, fullTeam.TeamId, isActive: false);
        var (left, _) = f.AddPreApproved(leaver.UserId);
        expectations.Add((left.AnalysisId, 2, true, 0));

        await f.SaveAsync();

        var analyses = f.Context.Analyses.Where(a => a.TenantId == DashboardFixture.Tenant).ToList();
        var quorum   = f.Quorum();

        var batch = await quorum.GetTalliesAsync(DashboardFixture.Tenant, analyses, CancellationToken.None);

        Assert.Equal(analyses.Count, batch.Count);
        foreach (var analysis in analyses)
        {
            var single = await quorum.GetTallyAsync(DashboardFixture.Tenant, analysis, CancellationToken.None);
            var many   = batch[analysis.AnalysisId];

            Assert.Equal(single.Required, many.Required);
            Assert.Equal(single.InForce,  many.InForce);
            Assert.Equal(single.ApproverUserIds.OrderBy(x => x), many.ApproverUserIds.OrderBy(x => x));
        }

        // And the fixture is not trivially "no quorum anywhere": each shape lands where the engine says.
        foreach (var (analysisId, required, inForce, approved) in expectations)
        {
            Assert.Equal(required, batch[analysisId].Required);
            Assert.Equal(inForce,  batch[analysisId].InForce);
            Assert.Equal(approved, batch[analysisId].Approved);
        }
    }

    [Fact]
    public async Task GetTallies_EmptyBatch_ReadsNothingAndReturnsEmpty()
    {
        await using var f = DashboardFixture.Create(nameof(GetTallies_EmptyBatch_ReadsNothingAndReturnsEmpty));

        var batch = await f.Quorum().GetTalliesAsync(DashboardFixture.Tenant, [], CancellationToken.None);

        Assert.Empty(batch);
    }
}
