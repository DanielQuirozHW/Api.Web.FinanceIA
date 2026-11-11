// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-10

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// Routing matrix for <see cref="AnalysisDecisionNotifier"/> — which decision transition reaches
/// which person.
///
/// <para>
/// This is the part a compiler cannot check. Every case here is a claim about WHO finds out that a
/// decision was made, and getting one wrong is silent: the analysis is still decided correctly and
/// the audit row is still right, but the hand-off lands in the wrong inbox or in none at all.
/// </para>
/// </summary>
public sealed class AnalysisDecisionNotifierTests
{
    private const string Tenant = "SgrOneClickSandboxFB2";

    // ── Case 1: pre-approval → the analyst's team manager ─────────────────────────

    [Fact]
    public async Task PreApproved_NotifiesTheManagerOfTheAnalystsTeam()
    {
        var manager = ActiveUser(Roles.RiskManager);
        var team    = TeamWithManager(manager.UserId);
        var analyst = ActiveUser(Roles.Analyst, teamId: team.TeamId);

        var notifications = new StubNotificationRepository();
        var sut = BuildSut(notifications, [team], [manager, analyst]);

        await sut.NotifyAsync(
            Tenant, NewAnalysis(), analyst, "Analysis.PreApproved",
            wasPreApproved: false, "ACME SA", 5_000_000m, "ARS", CancellationToken.None);

        var created = Assert.Single(notifications.Created);
        Assert.Equal(manager.UserId, created.UserId);
        Assert.Equal(AnalysisDecisionNotifier.PreApprovalPendingType, created.Type);
        Assert.Equal("Analysis", created.SourceType);
        Assert.False(created.IsRead);
    }

    /// <summary>
    /// The ways the team→manager lookup can come up empty all resolve to the same fallback. They are
    /// separate rows rather than one because each is a different bug if it regresses.
    /// </summary>
    /// <remarks>
    /// "team without a manager" now means BOTH sources empty: no [dbo].[TeamManagers] row and a null
    /// Teams.ManagerUserId. Since the notifier reads the union of the two, a team is only
    /// manager-less when neither has anything — which is exactly what TeamWithManager(null) plus no
    /// manager rows produces here.
    /// </remarks>
    [Theory]
    [InlineData("no team")]
    [InlineData("team without a manager")]
    [InlineData("manager deactivated")]
    public async Task PreApproved_FallsBackToEveryActiveRiskManager_WhenNoManagerResolves(string gap)
    {
        var riskManagerA = ActiveUser(Roles.RiskManager);
        var riskManagerB = ActiveUser(Roles.RiskManager);
        var admin        = ActiveUser(Roles.Admin);

        var inactiveManager = ActiveUser(Roles.RiskManager);

        var (team, analyst) = gap switch
        {
            "no team" =>
                ((Team?)null, ActiveUser(Roles.Analyst)),

            "team without a manager" =>
                (TeamWithManager(null), (User?)null),

            _ =>
                (TeamWithManager(inactiveManager.UserId), null),
        };

        var caller = analyst ?? ActiveUser(Roles.Analyst, teamId: team!.TeamId);

        // The roster only ever holds ACTIVE users, so leaving the deactivated manager out of it is
        // exactly what IUserRepository.GetActiveByTenantAsync would do in production.
        var roster = new List<User> { riskManagerA, riskManagerB, admin, caller };

        var notifications = new StubNotificationRepository();
        var sut = BuildSut(notifications, team is null ? [] : [team], roster);

        await sut.NotifyAsync(
            Tenant, NewAnalysis(), caller, "Analysis.PreApproved",
            wasPreApproved: false, "ACME SA", 5_000_000m, "ARS", CancellationToken.None);

        // Both RiskManagers, and ONLY them: the Admin is not a fallback recipient.
        Assert.Equal(2, notifications.Created.Count);
        Assert.Contains(notifications.Created, n => n.UserId == riskManagerA.UserId);
        Assert.Contains(notifications.Created, n => n.UserId == riskManagerB.UserId);
        Assert.DoesNotContain(notifications.Created, n => n.UserId == admin.UserId);
        Assert.DoesNotContain(notifications.Created, n => n.UserId == inactiveManager.UserId);
    }

    // ── Multiple managers per team (DDL 064) ─────────────────────────────────────

    /// <summary>
    /// The point of Phase 1: a team may name several managers and ALL of them are told. One manager
    /// is a single point of failure — they go on holiday and the pre-approval simply waits.
    /// </summary>
    [Fact]
    public async Task PreApproved_NotifiesEveryManagerOfTheAnalystsTeam()
    {
        var managerA = ActiveUser(Roles.RiskManager);
        var managerB = ActiveUser(Roles.RiskManager);
        var managerC = ActiveUser(Roles.Admin);

        // No legacy manager on the team — every manager comes from [dbo].[TeamManagers].
        var team    = TeamWithManager(null);
        var analyst = ActiveUser(Roles.Analyst, teamId: team.TeamId);

        // A RiskManager who is NOT on this team. If the notifier fell through to the ladder we would
        // see them, so their absence is what proves the manager set was used.
        var unrelatedRiskManager = ActiveUser(Roles.RiskManager);

        var notifications = new StubNotificationRepository();
        var sut = BuildSut(
            notifications,
            [team],
            [managerA, managerB, managerC, analyst, unrelatedRiskManager],
            managers:
            [
                ManagerRow(team.TeamId, managerA.UserId),
                ManagerRow(team.TeamId, managerB.UserId),
                ManagerRow(team.TeamId, managerC.UserId)
            ]);

        await sut.NotifyAsync(
            Tenant, NewAnalysis(), analyst, "Analysis.PreApproved",
            wasPreApproved: false, "ACME SA", 5_000_000m, "ARS", CancellationToken.None);

        Assert.Equal(3, notifications.Created.Count);
        Assert.Contains(notifications.Created, n => n.UserId == managerA.UserId);
        Assert.Contains(notifications.Created, n => n.UserId == managerB.UserId);
        Assert.Contains(notifications.Created, n => n.UserId == managerC.UserId);

        // The ladder did NOT run. Note managerC is an Admin and IS notified — not as a fallback rung
        // but because they were named a manager of this team, which is a different thing entirely.
        Assert.DoesNotContain(notifications.Created, n => n.UserId == unrelatedRiskManager.UserId);
    }

    /// <summary>
    /// A deactivated manager drops out of the set, and the REST still get told. The whole-set
    /// fallback must not fire just because one name went stale.
    /// </summary>
    [Fact]
    public async Task PreApproved_NotifiesOnlyTheActiveManagers()
    {
        var activeA  = ActiveUser(Roles.RiskManager);
        var activeB  = ActiveUser(Roles.RiskManager);
        var departed = ActiveUser(Roles.RiskManager);

        var team    = TeamWithManager(null);
        var analyst = ActiveUser(Roles.Analyst, teamId: team.TeamId);

        var unrelatedRiskManager = ActiveUser(Roles.RiskManager);

        var notifications = new StubNotificationRepository();

        // 'departed' is named a manager but is absent from the roster — which is exactly what
        // GetActiveByTenantAsync does to a deactivated account in production. The row in
        // [dbo].[TeamManagers] survives their deactivation; the notification must not.
        var sut = BuildSut(
            notifications,
            [team],
            [activeA, activeB, analyst, unrelatedRiskManager],
            managers:
            [
                ManagerRow(team.TeamId, activeA.UserId),
                ManagerRow(team.TeamId, activeB.UserId),
                ManagerRow(team.TeamId, departed.UserId)
            ]);

        await sut.NotifyAsync(
            Tenant, NewAnalysis(), analyst, "Analysis.PreApproved",
            wasPreApproved: false, "ACME SA", 5_000_000m, "ARS", CancellationToken.None);

        Assert.Equal(2, notifications.Created.Count);
        Assert.Contains(notifications.Created, n => n.UserId == activeA.UserId);
        Assert.Contains(notifications.Created, n => n.UserId == activeB.UserId);
        Assert.DoesNotContain(notifications.Created, n => n.UserId == departed.UserId);
        Assert.DoesNotContain(notifications.Created, n => n.UserId == unrelatedRiskManager.UserId);
    }

    /// <summary>
    /// The fourth gap, and the one the multi-manager change introduces: EVERY named manager is
    /// deactivated. Only then does the whole set count as empty and the ladder take over.
    /// </summary>
    [Fact]
    public async Task PreApproved_FallsBackToTheLadder_WhenEveryManagerIsDeactivated()
    {
        var departedA = ActiveUser(Roles.RiskManager);
        var departedB = ActiveUser(Roles.RiskManager);

        var team    = TeamWithManager(null);
        var analyst = ActiveUser(Roles.Analyst, teamId: team.TeamId);

        var riskManager = ActiveUser(Roles.RiskManager);

        var notifications = new StubNotificationRepository();

        // Neither departed manager is on the roster.
        var sut = BuildSut(
            notifications,
            [team],
            [analyst, riskManager],
            managers:
            [
                ManagerRow(team.TeamId, departedA.UserId),
                ManagerRow(team.TeamId, departedB.UserId)
            ]);

        await sut.NotifyAsync(
            Tenant, NewAnalysis(), analyst, "Analysis.PreApproved",
            wasPreApproved: false, "ACME SA", 5_000_000m, "ARS", CancellationToken.None);

        var created = Assert.Single(notifications.Created);
        Assert.Equal(riskManager.UserId, created.UserId);
    }

    /// <summary>
    /// The post-DDL-066 shape. That script copies Teams.ManagerUserId into [dbo].[TeamManagers], so
    /// the same person is legitimately in BOTH sources — and must still produce exactly one
    /// notification. This is the test that pins the Distinct().
    /// </summary>
    [Fact]
    public async Task TheLegacyManagerUserId_IsUnionedWithTeamManagers_AndDeduplicated()
    {
        var manager = ActiveUser(Roles.RiskManager);
        var team    = TeamWithManager(manager.UserId);   // the deprecated column
        var analyst = ActiveUser(Roles.Analyst, teamId: team.TeamId);

        var notifications = new StubNotificationRepository();
        var sut = BuildSut(
            notifications,
            [team],
            [manager, analyst],
            // ...and the same person again, as DDL 066 leaves them.
            managers: [ManagerRow(team.TeamId, manager.UserId)]);

        await sut.NotifyAsync(
            Tenant, NewAnalysis(), analyst, "Analysis.PreApproved",
            wasPreApproved: false, "ACME SA", 5_000_000m, "ARS", CancellationToken.None);

        var created = Assert.Single(notifications.Created);
        Assert.Equal(manager.UserId, created.UserId);
    }

    /// <summary>
    /// The skipped-066 tenant: the table is empty because the DATA migration never ran, but the
    /// deprecated column still names a manager. The union is what keeps that tenant working instead
    /// of silently degrading to the fan-out — and --verify-schema cannot catch it, because it
    /// compares columns and not rows.
    /// </summary>
    [Fact]
    public async Task TheLegacyManagerUserId_StillRoutes_WhenTeamManagersIsEmpty()
    {
        var manager = ActiveUser(Roles.RiskManager);
        var team    = TeamWithManager(manager.UserId);
        var analyst = ActiveUser(Roles.Analyst, teamId: team.TeamId);

        var unrelatedRiskManager = ActiveUser(Roles.RiskManager);

        var notifications = new StubNotificationRepository();

        // No manager rows at all — 066 was not run on this tenant's database.
        var sut = BuildSut(notifications, [team], [manager, analyst, unrelatedRiskManager]);

        await sut.NotifyAsync(
            Tenant, NewAnalysis(), analyst, "Analysis.PreApproved",
            wasPreApproved: false, "ACME SA", 5_000_000m, "ARS", CancellationToken.None);

        var created = Assert.Single(notifications.Created);
        Assert.Equal(manager.UserId, created.UserId);

        // And specifically NOT the fan-out, which is the degradation the union exists to prevent.
        Assert.DoesNotContain(notifications.Created, n => n.UserId == unrelatedRiskManager.UserId);
    }

    /// <summary>
    /// A manager named ONLY in the new table, with the deprecated column left null, is the shape
    /// Phase 2's management API will produce. The union has to work in that direction too.
    /// </summary>
    [Fact]
    public async Task AManagerNamedOnlyInTeamManagers_IsNotified()
    {
        var manager = ActiveUser(Roles.RiskManager);
        var team    = TeamWithManager(null);
        var analyst = ActiveUser(Roles.Analyst, teamId: team.TeamId);

        var unrelatedRiskManager = ActiveUser(Roles.RiskManager);

        var notifications = new StubNotificationRepository();
        var sut = BuildSut(
            notifications,
            [team],
            [manager, analyst, unrelatedRiskManager],
            managers: [ManagerRow(team.TeamId, manager.UserId)]);

        await sut.NotifyAsync(
            Tenant, NewAnalysis(), analyst, "Analysis.PreApproved",
            wasPreApproved: false, "ACME SA", 5_000_000m, "ARS", CancellationToken.None);

        var created = Assert.Single(notifications.Created);
        Assert.Equal(manager.UserId, created.UserId);
        Assert.DoesNotContain(notifications.Created, n => n.UserId == unrelatedRiskManager.UserId);
    }

    /// <summary>
    /// Tenant scoping on the new read. A manager row belonging to another tenant must not leak into
    /// this tenant's recipients — TeamId is globally unique, so the predicate is the only thing
    /// stopping it, and FinanciaDbContext has no global query filter to fall back on.
    /// </summary>
    [Fact]
    public async Task AManagerRowFromAnotherTenant_IsIgnored()
    {
        // An ANALYST on purpose. If this person were a RiskManager they would be a legitimate
        // recipient of the ladder below, and the test could not tell "the foreign row was ignored"
        // apart from "the fan-out picked them up anyway".
        var foreignManager = ActiveUser(Roles.Analyst);
        var team           = TeamWithManager(null);
        var analyst        = ActiveUser(Roles.Analyst, teamId: team.TeamId);

        var riskManager = ActiveUser(Roles.RiskManager);

        var foreignRow = ManagerRow(team.TeamId, foreignManager.UserId);
        foreignRow.TenantId = "SomeOtherTenantFB";

        var notifications = new StubNotificationRepository();
        var sut = BuildSut(
            notifications,
            [team],
            [foreignManager, analyst, riskManager],
            managers: [foreignRow]);

        await sut.NotifyAsync(
            Tenant, NewAnalysis(), analyst, "Analysis.PreApproved",
            wasPreApproved: false, "ACME SA", 5_000_000m, "ARS", CancellationToken.None);

        // The foreign row is invisible, so the team has no managers and the ladder takes over.
        var created = Assert.Single(notifications.Created);
        Assert.Equal(riskManager.UserId, created.UserId);
        Assert.DoesNotContain(notifications.Created, n => n.UserId == foreignManager.UserId);
    }

    /// <summary>
    /// Case 5 (direct approval, FYI) shares the manager resolution with case 1, so it fans out to
    /// every manager too — and still drops the approver alone, not the whole list.
    /// </summary>
    [Fact]
    public async Task DirectApproval_NotifiesEveryTeamManagerForVisibility()
    {
        var managerA = ActiveUser(Roles.RiskManager);
        var managerB = ActiveUser(Roles.RiskManager);

        var team    = TeamWithManager(null);
        var analyst = ActiveUser(Roles.Analyst, teamId: team.TeamId);

        var notifications = new StubNotificationRepository();
        var sut = BuildSut(
            notifications,
            [team],
            [managerA, managerB, analyst],
            managers:
            [
                ManagerRow(team.TeamId, managerA.UserId),
                ManagerRow(team.TeamId, managerB.UserId)
            ]);

        await sut.NotifyAsync(
            Tenant, NewAnalysis(), analyst, "Analysis.Approved",
            wasPreApproved: false, "ACME SA", 5_000_000m, "ARS", CancellationToken.None);

        Assert.Equal(2, notifications.Created.Count);
        Assert.All(notifications.Created,
            n => Assert.Equal(AnalysisDecisionNotifier.ApprovedFyiType, n.Type));
    }

    /// <summary>
    /// The self-skip on case 5 removes only the approver. A manager who approves their own team's
    /// analysis hears nothing; their fellow managers still do.
    /// </summary>
    [Fact]
    public async Task DirectApproval_DropsOnlyTheApprover_FromTheManagerSet()
    {
        var team = TeamWithManager(null);

        // The approver is themselves one of the team's managers.
        var approver  = ActiveUser(Roles.RiskManager, teamId: team.TeamId);
        var coManager = ActiveUser(Roles.RiskManager);

        var notifications = new StubNotificationRepository();
        var sut = BuildSut(
            notifications,
            [team],
            [approver, coManager],
            managers:
            [
                ManagerRow(team.TeamId, approver.UserId),
                ManagerRow(team.TeamId, coManager.UserId)
            ]);

        await sut.NotifyAsync(
            Tenant, NewAnalysis(), approver, "Analysis.Approved",
            wasPreApproved: false, "ACME SA", 5_000_000m, "ARS", CancellationToken.None);

        var created = Assert.Single(notifications.Created);
        Assert.Equal(coManager.UserId, created.UserId);
    }

    // ── The escalation ladder: RiskManager first, Admin only as last resort ──────

    /// <summary>
    /// Rung two. A tenant that has not staffed a RiskManager still has someone who can action the
    /// pre-approval — <c>isConfirmer</c> in DecideAsync admits Admin too — so it escalates rather
    /// than going nowhere.
    /// </summary>
    [Fact]
    public async Task PreApproved_EscalatesToAdmins_WhenTheTenantHasNoRiskManager()
    {
        var adminA  = ActiveUser(Roles.Admin);
        var adminB  = ActiveUser(Roles.Admin);
        var analyst = ActiveUser(Roles.Analyst);
        var auditor = ActiveUser(Roles.Auditor);

        var notifications = new StubNotificationRepository();
        var sut = BuildSut(notifications, [], [adminA, adminB, analyst, auditor]);

        await sut.NotifyAsync(
            Tenant, NewAnalysis(), analyst, "Analysis.PreApproved",
            wasPreApproved: false, "ACME SA", 5_000_000m, "ARS", CancellationToken.None);

        Assert.Equal(2, notifications.Created.Count);
        Assert.Contains(notifications.Created, n => n.UserId == adminA.UserId);
        Assert.Contains(notifications.Created, n => n.UserId == adminB.UserId);
        // The ladder escalates by ROLE, not to "everyone left".
        Assert.DoesNotContain(notifications.Created, n => n.UserId == analyst.UserId);
        Assert.DoesNotContain(notifications.Created, n => n.UserId == auditor.UserId);
    }

    /// <summary>The same ladder, reached from case 5 — the two share one resolver.</summary>
    [Fact]
    public async Task DirectApproval_EscalatesToAdmins_WhenTheTenantHasNoRiskManager()
    {
        var admin   = ActiveUser(Roles.Admin);
        var analyst = ActiveUser(Roles.Analyst);

        var notifications = new StubNotificationRepository();
        var sut = BuildSut(notifications, [], [admin, analyst]);

        await sut.NotifyAsync(
            Tenant, NewAnalysis(), analyst, "Analysis.Approved",
            wasPreApproved: false, "ACME SA", 250_000m, "ARS", CancellationToken.None);

        var created = Assert.Single(notifications.Created);
        Assert.Equal(admin.UserId, created.UserId);
        Assert.Equal(AnalysisDecisionNotifier.ApprovedFyiType, created.Type);
    }

    /// <summary>
    /// The rule that keeps the safety net useful: an Admin must NOT be a routine recipient. One
    /// RiskManager is enough to stop the ladder, in both cases.
    /// </summary>
    [Theory]
    [InlineData("Analysis.PreApproved")]
    [InlineData("Analysis.Approved")]
    public async Task Admins_AreNotNotified_WhenAnyRiskManagerExists(string auditAction)
    {
        var riskManager = ActiveUser(Roles.RiskManager);
        var admin       = ActiveUser(Roles.Admin);
        var analyst     = ActiveUser(Roles.Analyst);

        var notifications = new StubNotificationRepository();
        var sut = BuildSut(notifications, [], [riskManager, admin, analyst]);

        await sut.NotifyAsync(
            Tenant, NewAnalysis(), analyst, auditAction,
            wasPreApproved: false, "ACME SA", 250_000m, "ARS", CancellationToken.None);

        var created = Assert.Single(notifications.Created);
        Assert.Equal(riskManager.UserId, created.UserId);
    }

    /// <summary>Both rungs empty: nothing emitted, and above all no throw.</summary>
    [Theory]
    [InlineData("Analysis.PreApproved")]
    [InlineData("Analysis.Approved")]
    public async Task NoRiskManagerAndNoAdmin_EmitsNothingAndDoesNotThrow(string auditAction)
    {
        var analyst = ActiveUser(Roles.Analyst);
        var auditor = ActiveUser(Roles.Auditor);

        var notifications = new StubNotificationRepository();
        var sut = BuildSut(notifications, [], [analyst, auditor]);

        await sut.NotifyAsync(
            Tenant, NewAnalysis(), analyst, auditAction,
            wasPreApproved: false, "ACME SA", 250_000m, "ARS", CancellationToken.None);

        Assert.Empty(notifications.Created);
        Assert.Equal(0, notifications.CreateCallCount);
    }

    /// <summary>
    /// The self-skip still runs AFTER the ladder: an Admin approving directly in a tenant with no
    /// RiskManager escalates to the Admins, finds only themselves, and is not told about their own
    /// act. Case 1 keeps the opposite rule — see the test above it.
    /// </summary>
    [Fact]
    public async Task DirectApproval_EmitsNothing_WhenTheLadderResolvesToTheApproverAlone()
    {
        var soleAdmin = ActiveUser(Roles.Admin);

        var notifications = new StubNotificationRepository();
        var sut = BuildSut(notifications, [], [soleAdmin]);

        await sut.NotifyAsync(
            Tenant, NewAnalysis(), soleAdmin, "Analysis.Approved",
            wasPreApproved: false, "ACME SA", 250_000m, "ARS", CancellationToken.None);

        Assert.Empty(notifications.Created);
    }

    /// <summary>
    /// The ladder governs the FALLBACK only. A team with a live manager still routes straight to
    /// them, even when the tenant is full of RiskManagers and Admins who would otherwise qualify.
    /// </summary>
    [Fact]
    public async Task ANamedTeamManager_StillWinsOverTheLadder()
    {
        var manager     = ActiveUser(Roles.Analyst);   // not a RiskManager — the team names them
        var riskManager = ActiveUser(Roles.RiskManager);
        var admin       = ActiveUser(Roles.Admin);

        var team    = TeamWithManager(manager.UserId);
        var analyst = ActiveUser(Roles.Analyst, teamId: team.TeamId);

        var notifications = new StubNotificationRepository();
        var sut = BuildSut(notifications, [team], [manager, riskManager, admin, analyst]);

        await sut.NotifyAsync(
            Tenant, NewAnalysis(), analyst, "Analysis.PreApproved",
            wasPreApproved: false, "ACME SA", 5_000_000m, "ARS", CancellationToken.None);

        var created = Assert.Single(notifications.Created);
        Assert.Equal(manager.UserId, created.UserId);
    }

    // ── Cases 2/3/4: the manager settled it → the analyst who proposed ────────────

    [Theory]
    [InlineData("Analysis.Confirmed",        false, AnalysisDecisionNotifier.ConfirmedType)]
    [InlineData("Analysis.ReturnedToReview", false, AnalysisDecisionNotifier.ReturnedType)]
    [InlineData("Analysis.Rejected",         true,  AnalysisDecisionNotifier.RejectedType)]
    public async Task SettledPreApproval_NotifiesThePreApprover(
        string auditAction, bool wasPreApproved, string expectedType)
    {
        var proposer = ActiveUser(Roles.Analyst);
        var manager  = ActiveUser(Roles.RiskManager);

        var analysis = NewAnalysis();
        analysis.PreApprovedBy = proposer.UserId;

        var notifications = new StubNotificationRepository();
        var sut = BuildSut(notifications, [], [proposer, manager]);

        await sut.NotifyAsync(
            Tenant, analysis, manager, auditAction,
            // Confirmed and ReturnedToReview only ever arise on a pre-approved analysis; the flag
            // matters only for Rejected, which is the one action covering two transitions.
            wasPreApproved || auditAction != "Analysis.Rejected",
            "ACME SA", 5_000_000m, "ARS", CancellationToken.None);

        var created = Assert.Single(notifications.Created);
        Assert.Equal(proposer.UserId, created.UserId);
        Assert.Equal(expectedType, created.Type);
    }

    /// <summary>
    /// A re-decided analysis can legitimately carry no PreApprovedBy. That is a no-op, not a crash
    /// and not a notification addressed to Guid.Empty.
    /// </summary>
    [Fact]
    public async Task Confirmed_EmitsNothing_WhenThereIsNoPreApprover()
    {
        var manager = ActiveUser(Roles.RiskManager);

        var analysis = NewAnalysis();
        analysis.PreApprovedBy = null;

        var notifications = new StubNotificationRepository();
        var sut = BuildSut(notifications, [], [manager]);

        await sut.NotifyAsync(
            Tenant, analysis, manager, "Analysis.Confirmed",
            wasPreApproved: true, "ACME SA", 5_000_000m, "ARS", CancellationToken.None);

        Assert.Empty(notifications.Created);
        Assert.Equal(0, notifications.CreateCallCount);
    }

    // ── Case 5: direct approval within limit → the manager, for visibility ────────

    [Fact]
    public async Task DirectApproval_NotifiesTheTeamManagerForVisibility()
    {
        var manager = ActiveUser(Roles.RiskManager);
        var team    = TeamWithManager(manager.UserId);
        var analyst = ActiveUser(Roles.Analyst, teamId: team.TeamId);

        var notifications = new StubNotificationRepository();
        var sut = BuildSut(notifications, [team], [manager, analyst]);

        await sut.NotifyAsync(
            Tenant, NewAnalysis(), analyst, "Analysis.Approved",
            wasPreApproved: false, "ACME SA", 250_000m, "ARS", CancellationToken.None);

        var created = Assert.Single(notifications.Created);
        Assert.Equal(manager.UserId, created.UserId);
        Assert.Equal(AnalysisDecisionNotifier.ApprovedFyiType, created.Type);
    }

    /// <summary>
    /// The self-skip that distinguishes case 5 from case 1. A RiskManager approving directly, with
    /// no one above them, would otherwise be told about their own act.
    /// </summary>
    [Fact]
    public async Task DirectApproval_EmitsNothing_WhenTheApproverIsTheOnlyRecipient()
    {
        var soleRiskManager = ActiveUser(Roles.RiskManager);

        var notifications = new StubNotificationRepository();
        var sut = BuildSut(notifications, [], [soleRiskManager]);

        await sut.NotifyAsync(
            Tenant, NewAnalysis(), soleRiskManager, "Analysis.Approved",
            wasPreApproved: false, "ACME SA", 250_000m, "ARS", CancellationToken.None);

        Assert.Empty(notifications.Created);
    }

    /// <summary>
    /// Case 1 does NOT share case 5's self-skip: a pre-approval must leave a trace even in the odd
    /// configuration where the caller is their own team's manager.
    /// </summary>
    [Fact]
    public async Task PreApproved_StillNotifies_WhenTheCallerIsTheirOwnManager()
    {
        var admin = ActiveUser(Roles.Admin);
        var team  = TeamWithManager(admin.UserId);
        admin.TeamId = team.TeamId;

        var notifications = new StubNotificationRepository();
        var sut = BuildSut(notifications, [team], [admin]);

        await sut.NotifyAsync(
            Tenant, NewAnalysis(), admin, "Analysis.PreApproved",
            wasPreApproved: false, "ACME SA", 5_000_000m, "ARS", CancellationToken.None);

        var created = Assert.Single(notifications.Created);
        Assert.Equal(admin.UserId, created.UserId);
    }

    // ── Case 6: direct rejection → whoever requested the analysis ─────────────────

    [Fact]
    public async Task DirectRejection_NotifiesTheRequester()
    {
        var requester = ActiveUser(Roles.Analyst);
        var manager   = ActiveUser(Roles.RiskManager);

        var analysis = NewAnalysis();
        analysis.RequestedBy = requester.UserId;

        var notifications = new StubNotificationRepository();
        var sut = BuildSut(notifications, [], [requester, manager]);

        await sut.NotifyAsync(
            Tenant, analysis, manager, "Analysis.Rejected",
            wasPreApproved: false, "ACME SA", null, null, CancellationToken.None);

        var created = Assert.Single(notifications.Created);
        Assert.Equal(requester.UserId, created.UserId);
        Assert.Equal(AnalysisDecisionNotifier.RejectedType, created.Type);
    }

    [Fact]
    public async Task DirectRejection_EmitsNothing_WhenTheRejecterIsTheRequester()
    {
        var analyst = ActiveUser(Roles.Analyst);

        var analysis = NewAnalysis();
        analysis.RequestedBy = analyst.UserId;

        var notifications = new StubNotificationRepository();
        var sut = BuildSut(notifications, [], [analyst]);

        await sut.NotifyAsync(
            Tenant, analysis, analyst, "Analysis.Rejected",
            wasPreApproved: false, "ACME SA", null, null, CancellationToken.None);

        Assert.Empty(notifications.Created);
    }

    // ── Dedupe, de-duplication and fault isolation ───────────────────────────────

    /// <summary>
    /// The dedupe is keyed on the SOURCE, not on a time window — a second notification about a
    /// DIFFERENT analysis of the same type must still go out.
    /// </summary>
    [Fact]
    public async Task AlreadyNotifiedAboutThisAnalysis_IsSuppressed_ButAnotherAnalysisIsNot()
    {
        var manager = ActiveUser(Roles.RiskManager);
        var team    = TeamWithManager(manager.UserId);
        var analyst = ActiveUser(Roles.Analyst, teamId: team.TeamId);

        var alreadySeen = NewAnalysis();
        var freshOne    = NewAnalysis();

        // Both analyses have been scored, so both have a result row. The dedupe key is the RESULT id
        // because that is what SourceId stores — keying this triple on the AnalysisId would make the
        // suppression below silently stop working.
        var alreadySeenResultId = Guid.NewGuid();
        var freshOneResultId    = Guid.NewGuid();

        var results = new Dictionary<Guid, Guid>
        {
            [alreadySeen.AnalysisId] = alreadySeenResultId,
            [freshOne.AnalysisId]    = freshOneResultId,
        };

        var notifications = new StubNotificationRepository();
        notifications.Existing.Add(
            (manager.UserId, AnalysisDecisionNotifier.PreApprovalPendingType, alreadySeenResultId));

        var sut = BuildSut(notifications, [team], [manager, analyst], results);

        await sut.NotifyAsync(
            Tenant, alreadySeen, analyst, "Analysis.PreApproved",
            wasPreApproved: false, "ACME SA", 1m, "ARS", CancellationToken.None);

        Assert.Empty(notifications.Created);

        await sut.NotifyAsync(
            Tenant, freshOne, analyst, "Analysis.PreApproved",
            wasPreApproved: false, "OTRA SRL", 1m, "ARS", CancellationToken.None);

        var created = Assert.Single(notifications.Created);
        Assert.Equal(freshOneResultId, created.SourceId);
    }

    // ── SourceId: the id the Portal deep-links to ────────────────────────────────

    /// <summary>
    /// The bug this notifier had until now. The Portal navigates a decision notification to
    /// /dashboard/analyses/{sourceId}, and that route resolves an AnalysisResult by its RESULT id —
    /// GET /api/analyses/{id} and PATCH /api/analyses/{id}/decision are keyed the same way. Storing
    /// the AnalysisId there produced a link that 404s every time, and NotificationDto carries no
    /// second id the Portal could fall back to.
    /// </summary>
    [Fact]
    public async Task SourceId_IsTheResultId_SoTheDeepLinkResolves()
    {
        var manager = ActiveUser(Roles.RiskManager);
        var team    = TeamWithManager(manager.UserId);
        var analyst = ActiveUser(Roles.Analyst, teamId: team.TeamId);

        var analysis = NewAnalysis();
        var resultId = Guid.NewGuid();

        var notifications = new StubNotificationRepository();
        var sut = BuildSut(
            notifications, [team], [manager, analyst],
            new Dictionary<Guid, Guid> { [analysis.AnalysisId] = resultId });

        await sut.NotifyAsync(
            Tenant, analysis, analyst, "Analysis.PreApproved",
            wasPreApproved: false, "ACME SA", 5_000_000m, "ARS", CancellationToken.None);

        var created = Assert.Single(notifications.Created);
        Assert.Equal(resultId, created.SourceId);
        Assert.NotEqual(analysis.AnalysisId, created.SourceId);
        Assert.Equal("Analysis", created.SourceType);
    }

    /// <summary>
    /// The honest degraded path: an analysis with no AnalysisResult row — decided before it was ever
    /// scored — has no navigable id to offer. It keeps today's value and warns, because losing the
    /// hand-off entirely is strictly worse than a link that does not resolve.
    /// </summary>
    [Fact]
    public async Task SourceId_FallsBackToTheAnalysisId_AndWarns_WhenThereIsNoResultRow()
    {
        var manager = ActiveUser(Roles.RiskManager);
        var team    = TeamWithManager(manager.UserId);
        var analyst = ActiveUser(Roles.Analyst, teamId: team.TeamId);

        var analysis = NewAnalysis();

        var notifications = new StubNotificationRepository();
        var logger        = new RecordingLogger<AnalysisDecisionNotifier>();

        // An empty map is exactly what the repository returns for an analysis with no result row.
        var sut = BuildSut(
            notifications, [team], [manager, analyst],
            new Dictionary<Guid, Guid>(), logger);

        await sut.NotifyAsync(
            Tenant, analysis, analyst, "Analysis.PreApproved",
            wasPreApproved: false, "ACME SA", 5_000_000m, "ARS", CancellationToken.None);

        // The notification still goes out — that is the whole point of the fallback.
        var created = Assert.Single(notifications.Created);
        Assert.Equal(analysis.AnalysisId, created.SourceId);

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains(analysis.AnalysisId.ToString(), warning, StringComparison.Ordinal);
        // Ids only: the amount is a credit figure and must never reach a log line.
        Assert.DoesNotContain("5.000.000", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("ACME SA",   warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// Pins the two halves together. The dedupe query and the stored SourceId must use the SAME id:
    /// if the check were left keyed on the AnalysisId while the row stores the ResultId it would
    /// never match, and every re-decision would re-notify.
    /// </summary>
    [Fact]
    public async Task TheDedupeKey_IsTheStoredSourceId_NotTheAnalysisId()
    {
        var manager = ActiveUser(Roles.RiskManager);
        var team    = TeamWithManager(manager.UserId);
        var analyst = ActiveUser(Roles.Analyst, teamId: team.TeamId);

        var analysis = NewAnalysis();
        var resultId = Guid.NewGuid();

        var notifications = new StubNotificationRepository();
        // Seeded with the RESULT id — the value a previous run of this code would have stored.
        notifications.Existing.Add(
            (manager.UserId, AnalysisDecisionNotifier.PreApprovalPendingType, resultId));

        var sut = BuildSut(
            notifications, [team], [manager, analyst],
            new Dictionary<Guid, Guid> { [analysis.AnalysisId] = resultId });

        await sut.NotifyAsync(
            Tenant, analysis, analyst, "Analysis.PreApproved",
            wasPreApproved: false, "ACME SA", 5_000_000m, "ARS", CancellationToken.None);

        Assert.Empty(notifications.Created);
        Assert.Equal(0, notifications.CreateCallCount);
    }

    /// <summary>
    /// A roster carrying the same RiskManager twice — odd data, but nothing forbids it — must not
    /// produce two rows for one person.
    /// </summary>
    [Fact]
    public async Task DuplicateRecipients_AreCollapsedToOneNotification()
    {
        var riskManager = ActiveUser(Roles.RiskManager);
        var analyst     = ActiveUser(Roles.Analyst);

        var notifications = new StubNotificationRepository();
        var sut = BuildSut(notifications, [], [riskManager, riskManager, analyst]);

        await sut.NotifyAsync(
            Tenant, NewAnalysis(), analyst, "Analysis.PreApproved",
            wasPreApproved: false, "ACME SA", 1m, "ARS", CancellationToken.None);

        Assert.Single(notifications.Created);
    }

    /// <summary>
    /// The property the whole feature rests on: a notification failure is swallowed. The decision
    /// has already been persisted and audited by the time this runs, and must not be undone by it.
    /// </summary>
    [Fact]
    public async Task AFailingRepository_IsSwallowed_AndNeverThrows()
    {
        var riskManager = ActiveUser(Roles.RiskManager);
        var analyst     = ActiveUser(Roles.Analyst);

        var notifications = new StubNotificationRepository
        {
            ThrowOnCreate = new InvalidOperationException("Invalid object name 'dbo.Notifications'.")
        };

        var sut = BuildSut(notifications, [], [riskManager, analyst]);

        // The assertion is that this does not throw.
        await sut.NotifyAsync(
            Tenant, NewAnalysis(), analyst, "Analysis.PreApproved",
            wasPreApproved: false, "ACME SA", 1m, "ARS", CancellationToken.None);
    }

    /// <summary>A missing team table must not throw either — 062 may not have been run yet.</summary>
    [Fact]
    public async Task AFailingTeamLookup_IsSwallowed_AndNeverThrows()
    {
        var analyst = ActiveUser(Roles.Analyst, teamId: Guid.NewGuid());

        var teams = new StubTeamRepository
        {
            ThrowOnGet = new InvalidOperationException("Invalid object name 'dbo.Teams'.")
        };

        var sut = new AnalysisDecisionNotifier(
            new StubNotificationRepository(), teams,
            new StubUserRepository { Roster = [analyst] },
            new StubAnalysisResultRepository(),
            NullLogger<AnalysisDecisionNotifier>.Instance);

        await sut.NotifyAsync(
            Tenant, NewAnalysis(), analyst, "Analysis.PreApproved",
            wasPreApproved: false, "ACME SA", 1m, "ARS", CancellationToken.None);
    }

    /// <summary>An action this notifier does not route is simply ignored.</summary>
    [Fact]
    public async Task AnUnknownAuditAction_EmitsNothing()
    {
        var analyst = ActiveUser(Roles.Analyst);

        var notifications = new StubNotificationRepository();
        var sut = BuildSut(notifications, [], [analyst, ActiveUser(Roles.RiskManager)]);

        await sut.NotifyAsync(
            Tenant, NewAnalysis(), analyst, "Analysis.SomethingElse",
            wasPreApproved: false, "ACME SA", 1m, "ARS", CancellationToken.None);

        Assert.Empty(notifications.Created);
    }

    // ── Copy ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The title carries no interpolated values, so a long business name can never truncate it
    /// against [Title] nvarchar(300). The company and the figure live in the body.
    /// </summary>
    [Fact]
    public async Task TheCopyNamesTheCompanyInTheBody_AndKeepsTheTitleShort()
    {
        var manager = ActiveUser(Roles.RiskManager);
        var team    = TeamWithManager(manager.UserId);
        var analyst = ActiveUser(Roles.Analyst, teamId: team.TeamId);

        var longName = new string('A', 400);

        var notifications = new StubNotificationRepository();
        var sut = BuildSut(notifications, [team], [manager, analyst]);

        await sut.NotifyAsync(
            Tenant, NewAnalysis(), analyst, "Analysis.PreApproved",
            wasPreApproved: false, longName, 5_000_000m, "ARS", CancellationToken.None);

        var created = Assert.Single(notifications.Created);

        Assert.True(created.Title.Length <= 300, "Title must fit nvarchar(300).");
        Assert.DoesNotContain(longName, created.Title, StringComparison.Ordinal);
        Assert.Contains(longName, created.Body!, StringComparison.Ordinal);
        // es-AR: '.' groups thousands and ',' is the decimal separator.
        Assert.Contains("ARS 5.000.000,00", created.Body!, StringComparison.Ordinal);
    }

    /// <summary>A rejection commits nothing, so the copy names no figure rather than a zero.</summary>
    [Fact]
    public async Task TheCopyOmitsTheAmount_WhenTheDecisionCommitsNothing()
    {
        var requester = ActiveUser(Roles.Analyst);
        var manager   = ActiveUser(Roles.RiskManager);

        var analysis = NewAnalysis();
        analysis.RequestedBy = requester.UserId;

        var notifications = new StubNotificationRepository();
        var sut = BuildSut(notifications, [], [requester, manager]);

        await sut.NotifyAsync(
            Tenant, analysis, manager, "Analysis.Rejected",
            wasPreApproved: false, "ACME SA", null, null, CancellationToken.None);

        var created = Assert.Single(notifications.Created);
        Assert.DoesNotContain("0,00", created.Body!, StringComparison.Ordinal);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the notifier. <paramref name="results"/> is the AnalysisId → ResultId map the source-id
    /// lookup resolves against; the default is EMPTY, which is the fallback path — every test that
    /// asserts on SourceId therefore has to supply one explicitly and say which id it expects.
    /// </summary>
    private static AnalysisDecisionNotifier BuildSut(
        StubNotificationRepository        notifications,
        IReadOnlyList<Team>               teams,
        IReadOnlyList<User>               roster,
        IReadOnlyDictionary<Guid, Guid>?  results  = null,
        ILogger<AnalysisDecisionNotifier>? logger  = null,
        IReadOnlyList<TeamManager>?       managers = null)
        => new(
            notifications,
            new StubTeamRepository
            {
                Teams       = [.. teams],
                ManagerRows = managers is null ? [] : [.. managers]
            },
            new StubUserRepository { Roster = [.. roster] },
            new StubAnalysisResultRepository { Map = results ?? new Dictionary<Guid, Guid>() },
            logger ?? NullLogger<AnalysisDecisionNotifier>.Instance);

    /// <summary>
    /// One row of [dbo].[TeamManagers] — a manager named on a team through the new table rather than
    /// through the deprecated Teams.ManagerUserId column.
    /// </summary>
    private static TeamManager ManagerRow(Guid teamId, Guid userId) => new()
    {
        TeamId    = teamId,
        UserId    = userId,
        TenantId  = Tenant,
        CreatedAt = DateTime.UtcNow
    };

    private static User ActiveUser(string role, Guid? teamId = null) => new()
    {
        UserId      = Guid.NewGuid(),
        TenantId    = Tenant,
        DisplayName = role,
        Role        = role,
        IsActive    = true,
        TeamId      = teamId
    };

    private static Team TeamWithManager(Guid? managerUserId) => new()
    {
        TeamId        = Guid.NewGuid(),
        TenantId      = Tenant,
        Name          = "Equipo Norte",
        ManagerUserId = managerUserId
    };

    private static Analysis NewAnalysis() => new()
    {
        AnalysisId  = Guid.NewGuid(),
        TenantId    = Tenant,
        RequestedBy = Guid.NewGuid()
    };

    // ── Stubs (hand-rolled, no mocking library — the house convention) ───────────

    private sealed class StubNotificationRepository : INotificationRepository
    {
        /// <summary>(userId, type, sourceId) triples already on record.</summary>
        public HashSet<(Guid UserId, string Type, Guid SourceId)> Existing { get; } = new();

        public List<Notification> Created { get; } = new();

        public int CreateCallCount { get; private set; }

        public Exception? ThrowOnCreate { get; set; }

        public Task<bool> ExistsByTypeForUserAndSourceAsync(
            string tenantId, Guid userId, string type, Guid sourceId, CancellationToken ct)
            => Task.FromResult(Existing.Contains((userId, type, sourceId)));

        public Task CreateManyAsync(IEnumerable<Notification> notifications, CancellationToken ct)
        {
            CreateCallCount++;
            if (ThrowOnCreate is not null) return Task.FromException(ThrowOnCreate);

            Created.AddRange(notifications);
            return Task.CompletedTask;
        }

        public Task<bool> ExistsByTypeForUserInPeriodAsync(string tenantId, Guid userId, string type, DateTime periodStartUtc, DateTime periodEndUtc, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationPage> GetPageForUserAsync(string tenantId, Guid userId, NotificationQuery query, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> CountForUserAsync(string tenantId, Guid userId, NotificationQuery query, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> MarkAsUnreadAsync(Guid notificationId, string tenantId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> MarkManyAsReadAsync(string tenantId, Guid userId, IReadOnlyCollection<Guid> notificationIds, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> GetUnreadCountForUserAsync(string tenantId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> MarkAsReadAsync(Guid notificationId, string tenantId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> MarkAllAsReadForUserAsync(string tenantId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class StubTeamRepository : ITeamRepository
    {
        public List<Team> Teams { get; set; } = new();

        /// <summary>
        /// Stands in for [dbo].[TeamManagers] (DDL 064), as ROWS rather than a lookup, so the stub's
        /// query can carry the same tenant predicate the real repository does.
        /// </summary>
        public List<TeamManager> ManagerRows { get; set; } = new();

        public Exception? ThrowOnGet { get; set; }

        public Task<Team?> GetByIdAsync(Guid teamId, string tenantId, CancellationToken ct)
        {
            if (ThrowOnGet is not null) return Task.FromException<Team?>(ThrowOnGet);

            return Task.FromResult(
                Teams.FirstOrDefault(t => t.TeamId == teamId && t.TenantId == tenantId));
        }

        public Task<IReadOnlyList<Guid>> GetManagerUserIdsAsync(
            Guid teamId, string tenantId, CancellationToken ct)
        {
            // ThrowOnGet fails BOTH team reads, not just the legacy one. The notifier now touches
            // two repository methods to resolve managers, and a fault-isolation test that only broke
            // one of them would pass while the other silently carried the call — so the failure
            // injection has to cover this one too.
            if (ThrowOnGet is not null) return Task.FromException<IReadOnlyList<Guid>>(ThrowOnGet);

            // Mirrors the real query: tenant-scoped, projected to the id.
            IReadOnlyList<Guid> found = ManagerRows
                .Where(m => m.TeamId == teamId && m.TenantId == tenantId)
                .Select(m => m.UserId)
                .ToList();

            return Task.FromResult(found);
        }

        // The notifier resolves ONE team's managers, never a whole tenant's — that batch read serves
        // the Admin list endpoint. Throwing keeps a stray call visible.
        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> GetManagerUserIdsByTenantAsync(string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Team>> GetByTenantAsync(string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task CreateAsync(Team team, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateAsync(Team team, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> DeleteAsync(Guid teamId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        // The notifier must never WRITE a manager. Throwing means a stray call fails loudly here
        // rather than passing quietly.
        public Task ReplaceManagersAsync(Guid teamId, string tenantId, IReadOnlyCollection<Guid> userIds, Guid? createdBy, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class StubUserRepository : IUserRepository
    {
        /// <summary>Stands in for the tenant's ACTIVE roster — deactivated users are simply absent.</summary>
        public List<User> Roster { get; set; } = new();

        public Task<IReadOnlyList<User>> GetActiveByTenantAsync(string tenantId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<User>>(Roster);

        public Task<User?> GetByFirebaseUidAsync(string firebaseUid, CancellationToken ct) => throw new NotImplementedException();
        public Task<User?> GetByUserIdAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<User?> GetByEmailAsync(string email, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> ExistsByFirebaseUidAsync(string firebaseUid, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<User>> GetByIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct) => throw new NotImplementedException();
        public Task<User> CreateAsync(User user, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateLastLoginAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateOnboardingFlagsAsync(Guid userId, string? flagsJson, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateTeamAsync(Guid userId, Guid? teamId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> ClearTeamAsync(Guid teamId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
    }

    /// <summary>
    /// Stands in for the AnalysisId → ResultId lookup. Only that one method is reachable from the
    /// notifier; every other member throws, so a read that went anywhere near a full-row load would
    /// fail loudly rather than pass quietly.
    /// </summary>
    private sealed class StubAnalysisResultRepository : IAnalysisResultRepository
    {
        /// <summary>AnalysisId → ResultId. An analysis absent from it has no result row.</summary>
        public IReadOnlyDictionary<Guid, Guid> Map { get; set; } = new Dictionary<Guid, Guid>();

        public Task<IReadOnlyDictionary<Guid, Guid>> GetResultIdsByAnalysisIdsAsync(
            IEnumerable<Guid> analysisIds, string tenantId, CancellationToken ct)
        {
            // Mirrors the real query: only the ids asked for, and only those that have a result row.
            IReadOnlyDictionary<Guid, Guid> found = analysisIds
                .Distinct()
                .Where(Map.ContainsKey)
                .ToDictionary(id => id, id => Map[id]);

            return Task.FromResult(found);
        }

        public Task<AnalysisResult?> GetByIdAsync(Guid resultId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<AnalysisResult>> GetAllByTenantAsync(string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<AnalysisResult?> GetByDocumentIdAsync(Guid documentId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<AnalysisResult> CreateAsync(AnalysisResult result, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<AnalysisResult>> GetByEntityIdAsync(Guid entityId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<AnalysisResult> UpdateAsync(AnalysisResult result, CancellationToken ct) => throw new NotImplementedException();
        public Task<(IReadOnlyList<AnalysisListProjection> Items, int TotalCount)> GetListPageByTenantAsync(string tenantId, int skip, int take, CancellationToken ct) => throw new NotImplementedException();
        public Task<(IReadOnlyList<AnalysisListProjection> Items, int TotalCount)> GetListPageByEntityAsync(Guid entityId, string tenantId, int skip, int take, CancellationToken ct) => throw new NotImplementedException();
        public Task<AnalysisResultDetailProjection?> GetDetailByIdAsync(Guid resultId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<AnalysisContextProjection?> GetContextSnapshotByIdAsync(Guid resultId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<AnalysisBcraProjection?> GetBcraSnapshotByIdAsync(Guid resultId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<AnalysisRatiosProjection?> GetRatiosByIdAsync(Guid resultId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<AnalysisLabelProjection?> GetLabelByIdAsync(Guid resultId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
    }

    /// <summary>
    /// Captures Warning-level log calls. The fallback path warns in exactly one place, so one entry
    /// against an empty result map — and its CONTENT — is direct evidence of what was logged.
    /// </summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}
