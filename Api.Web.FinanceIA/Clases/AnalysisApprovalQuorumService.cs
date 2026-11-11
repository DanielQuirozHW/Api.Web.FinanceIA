// Project : Api.Web.FinanceIA
// Created : 2026-09-16

using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Default implementation of <see cref="IAnalysisApprovalQuorumService"/>.
/// Registered Scoped, like the repositories it reads and writes through.
/// </summary>
public sealed class AnalysisApprovalQuorumService : IAnalysisApprovalQuorumService
{
    /// <summary>
    /// The quorum when none is in force. One signature — exactly what confirmed an analysis before
    /// this service existed, which is what makes a team without managers a strict no-op.
    /// </summary>
    private const int NoQuorum = 1;

    private readonly IAnalysisApprovalVoteRepository               _voteRepository;
    private readonly IAnalysisRepository                           _analysisRepository;
    private readonly ITeamRepository                               _teamRepository;
    private readonly IUserRepository                               _userRepository;
    private readonly ILogger<AnalysisApprovalQuorumService>        _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="AnalysisApprovalQuorumService"/>.
    /// </summary>
    public AnalysisApprovalQuorumService(
        IAnalysisApprovalVoteRepository         voteRepository,
        IAnalysisRepository                     analysisRepository,
        ITeamRepository                         teamRepository,
        IUserRepository                         userRepository,
        ILogger<AnalysisApprovalQuorumService>  logger)
    {
        _voteRepository     = voteRepository;
        _analysisRepository = analysisRepository;
        _teamRepository     = teamRepository;
        _userRepository     = userRepository;
        _logger             = logger;
    }

    /// <inheritdoc />
    public async Task<(ApprovalVoteOutcome Outcome, ApprovalTally Tally)> VoteAndTryConfirmAsync(
        string            tenantId,
        Analysis          analysis,
        User              voter,
        decimal?          decisionAmount,
        string?           decisionCurrency,
        string?           confirmedOperationsJson,
        string?           reviewNotes,
        DateTime          nowUtc,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(voter);

        // ── STEP 1: COMMIT THE VOTE, THEN COUNT. THE ORDER IS THE CONCURRENCY ARGUMENT ──────
        //
        // Do not merge these two into one round trip, and do not move the count above the write.
        // Everything that stops two simultaneous voters from BOTH deciding "not yet, someone else
        // will finish this" rests on each of them having committed its own vote before it counts:
        //
        //   A commits at t1 and counts at t2 > t1;  B commits at t3 and counts at t4 > t3.
        //   For A to miss B's vote:  t2 < t3.   For B to miss A's vote:  t4 < t1.
        //   Chaining: t2 < t3 < t4 < t1 < t2 — a contradiction.
        //
        // So at least one of them sees the full tally, and no analysis can sit at "1 of 2" while two
        // approvals are on record. The argument needs only that a statement sees what was committed
        // before it STARTED, which holds under READ COMMITTED and equally under the read-committed
        // snapshot isolation Azure SQL enables by default — the snapshot is taken at statement start.
        //
        // A repeat vote returns false here, and that is inside the argument rather than an exception
        // to it: this manager's vote was already committed by their earlier attempt, so the premise
        // "their vote is on record before they count" holds all the more.
        var recorded = await _voteRepository
            .RecordAsync(
                new AnalysisApprovalVote
                {
                    VoteId     = Guid.NewGuid(),
                    AnalysisId = analysis.AnalysisId,
                    UserId     = voter.UserId,
                    TenantId   = tenantId,
                    Vote       = AnalysisVoteValues.Approved,
                    VotedAt    = nowUtc
                },
                ct)
            .ConfigureAwait(false);

        if (!recorded)
        {
            _logger.LogInformation(
                "Approval vote for analysis {AnalysisId} tenant {TenantId} by {VoterId} was already on record; the tally is unchanged.",
                analysis.AnalysisId, tenantId, voter.UserId);
        }

        var tally = await BuildTallyAsync(tenantId, analysis, ct).ConfigureAwait(false);

        if (!tally.IsSatisfied)
        {
            _logger.LogInformation(
                "Approval vote recorded below quorum for analysis {AnalysisId} tenant {TenantId}: {Approved} of {Required}",
                analysis.AnalysisId, tenantId, tally.Approved, tally.Required);

            return (ApprovalVoteOutcome.Pending, tally);
        }

        // ── STEP 2: CROSS THE QUORUM, EXACTLY ONCE ──────────────────────────────────────────
        //
        // More than one voter CAN observe a satisfied tally — with quorum 2 and votes A, B, C, both
        // B and C see enough. That is what the guarded UPDATE is for: it matches a row only while
        // the analysis is still PreApproved, so the second crosser affects 0 rows and reports the
        // analysis as already settled instead of overwriting the first one's signature, audit row
        // and notification with its own.
        var crossed = await _analysisRepository
            .TryConfirmFromPreApprovedAsync(
                analysis.AnalysisId,
                tenantId,
                voter.UserId,
                nowUtc,
                decisionAmount,
                decisionCurrency,
                confirmedOperationsJson,
                reviewNotes,
                ct)
            .ConfigureAwait(false);

        if (!crossed)
        {
            // Someone else settled it first. The write bypassed the change tracker either way, so the
            // in-memory entity is stale; refresh it from the row that actually won so the caller's
            // response describes the truth rather than this request's intentions.
            var settled = await _analysisRepository
                .GetByIdAsync(analysis.AnalysisId, ct)
                .ConfigureAwait(false);

            if (settled is not null && string.Equals(settled.TenantId, tenantId, StringComparison.Ordinal))
            {
                CopySettledStateOnto(analysis, settled);
            }

            return (ApprovalVoteOutcome.AlreadyConfirmed, tally);
        }

        // ExecuteUpdateAsync bypasses the change tracker, so the entity the caller holds — and will
        // build its response, audit row and notification from — does not know what was just written.
        // Mirror exactly the columns the guarded UPDATE named, and nothing else.
        analysis.Status                  = AnalysisStatuses.Approved;
        analysis.ReviewedBy              = voter.UserId;
        analysis.ReviewedAt              = nowUtc;
        analysis.DecisionAmount          = decisionAmount;
        analysis.DecisionCurrency        = decisionCurrency;
        analysis.ConfirmedOperationsJson = confirmedOperationsJson;
        if (reviewNotes is not null) analysis.ReviewNotes = reviewNotes;

        _logger.LogInformation(
            "Quorum satisfied for analysis {AnalysisId} tenant {TenantId}: {Approved} of {Required} signatures",
            analysis.AnalysisId, tenantId, tally.Approved, tally.Required);

        return (ApprovalVoteOutcome.Confirmed, tally);
    }

    /// <inheritdoc />
    public async Task<(bool Withdrawn, ApprovalTally Tally)> WithdrawAsync(
        string tenantId, Analysis analysis, User voter, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(voter);

        // Scoped to the caller's own id inside the repository, so withdrawing somebody else's
        // signature is not expressible rather than merely refused.
        var withdrawn = await _voteRepository
            .WithdrawAsync(analysis.AnalysisId, voter.UserId, tenantId, ct)
            .ConfigureAwait(false);

        var tally = await BuildTallyAsync(tenantId, analysis, ct).ConfigureAwait(false);

        if (withdrawn)
        {
            _logger.LogInformation(
                "Approval withdrawn for analysis {AnalysisId} tenant {TenantId} by {VoterId}: now {Approved} of {Required}",
                analysis.AnalysisId, tenantId, voter.UserId, tally.Approved, tally.Required);
        }

        return (withdrawn, tally);
    }

    /// <inheritdoc />
    public async Task RecordVetoAsync(
        string tenantId, Analysis analysis, User voter, DateTime nowUtc, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(voter);

        // A veto needs no tally and answers to no quorum — the caller writes 'Rejected' regardless.
        // The row exists so the trail shows WHO vetoed alongside the approvals that were standing
        // when they did, which is the question an auditor asks about a refused credit.
        //
        // OTHER managers' approvals are deliberately NOT discarded: they are the record of who had
        // signed. A later re-pre-approval wipes them on the way in, so they can never pre-fill a
        // fresh quorum.
        //
        // THIS manager's own earlier approval IS removed first, and it has to be. One manager gets
        // one row — that is the unique constraint — so without the withdrawal the insert below would
        // be refused as a duplicate and the veto would vanish from the trail, leaving the person who
        // refused the credit recorded as having approved it. Changing your mind replaces your vote;
        // it does not add a second one.
        await _voteRepository
            .WithdrawAsync(analysis.AnalysisId, voter.UserId, tenantId, ct)
            .ConfigureAwait(false);

        await _voteRepository
            .RecordAsync(
                new AnalysisApprovalVote
                {
                    VoteId     = Guid.NewGuid(),
                    AnalysisId = analysis.AnalysisId,
                    UserId     = voter.UserId,
                    TenantId   = tenantId,
                    Vote       = AnalysisVoteValues.Rejected,
                    VotedAt    = nowUtc
                },
                ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DiscardVotesAsync(string tenantId, Guid analysisId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        await _voteRepository
            .DeleteByAnalysisAsync(analysisId, tenantId, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<ApprovalTally> GetTallyAsync(string tenantId, Analysis analysis, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(analysis);

        return BuildTallyAsync(tenantId, analysis, ct);
    }

    /// <summary>
    /// Resolves the effective quorum and the approvals that count toward it.
    /// </summary>
    /// <remarks>
    /// SIZING AND ELIGIBILITY COME FROM DIFFERENT SETS, and that separation is the design:
    /// <list type="bullet">
    /// <item>HOW MANY signatures — from the analyst's team's eligible managers, capped and floored.</item>
    /// <item>WHOSE signature counts — any active confirmer in the tenant.</item>
    /// </list>
    /// Making the team's own managers the only valid voters would strand an analysis whenever they
    /// were unavailable, with no escape hatch; exempting an outsider from the quorum instead would
    /// let any RiskManager settle a two-signature analysis alone. Sizing from the team and counting
    /// from the tenant keeps the number the team asked for without either failure.
    /// </remarks>
    private async Task<ApprovalTally> BuildTallyAsync(
        string tenantId, Analysis analysis, CancellationToken ct)
    {
        var (requiredSignatures, inForce, roster) =
            await ResolveEffectiveQuorumAsync(tenantId, analysis, ct).ConfigureAwait(false);

        var approverIds = await _voteRepository
            .GetApproverUserIdsAsync(analysis.AnalysisId, tenantId, ct)
            .ConfigureAwait(false);

        return CountTally(requiredSignatures, inForce, EligibleVoterIds(roster), approverIds);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, ApprovalTally>> GetTalliesAsync(
        string tenantId, IReadOnlyCollection<Analysis> analyses, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(analyses);

        if (analyses.Count == 0)
            return new Dictionary<Guid, ApprovalTally>();

        // THE SAME ARITHMETIC AS BuildTallyAsync, over rows read once for the whole batch. Every
        // read below is the set-based twin of one the single-analysis path makes per analysis, and
        // every decision on top of them goes through the same pure helpers — SizeQuorum,
        // EligibleVoterIds, CountTally and TeamManagerResolution's own intersection — so the two
        // cannot disagree about a number. Five queries whatever the batch size.
        var roster     = await _userRepository.GetActiveByTenantAsync(tenantId, ct).ConfigureAwait(false);
        var rosterById = TeamManagerResolution.IndexRoster(roster);
        var eligible   = EligibleVoterIds(roster);

        var preApproverIds = analyses
            .Where(a => a.PreApprovedBy.HasValue)
            .Select(a => a.PreApprovedBy!.Value)
            .Distinct()
            .ToList();

        // GetByIdsAsync, like GetByUserIdAsync on the single path, carries no tenant predicate and no
        // IsActive filter: a DEACTIVATED analyst's pre-approval still needs their team's signatures.
        // The tenant check is written here, as it is there.
        var preApprovers = (await _userRepository.GetByIdsAsync(preApproverIds, ct).ConfigureAwait(false))
            .Where(u => string.Equals(u.TenantId, tenantId, StringComparison.Ordinal))
            .GroupBy(u => u.UserId)
            .ToDictionary(g => g.Key, g => g.First());

        var teamsById = (await _teamRepository.GetByTenantAsync(tenantId, ct).ConfigureAwait(false))
            .GroupBy(t => t.TeamId)
            .ToDictionary(g => g.Key, g => g.First());

        var managersByTeam = await _teamRepository
            .GetManagerUserIdsByTenantAsync(tenantId, ct)
            .ConfigureAwait(false);

        var approversByAnalysis = await _voteRepository
            .GetApproverUserIdsByAnalysesAsync(analyses.Select(a => a.AnalysisId).Distinct().ToList(), tenantId, ct)
            .ConfigureAwait(false);

        var tallies = new Dictionary<Guid, ApprovalTally>();
        foreach (var analysis in analyses)
        {
            if (tallies.ContainsKey(analysis.AnalysisId)) continue;

            User? analyst = analysis.PreApprovedBy is Guid analystId
                ? preApprovers.GetValueOrDefault(analystId)
                : null;

            // Mirrors TeamManagerResolution.ResolveActiveManagersAsync: no team id, no managers; a team
            // id whose row is gone in this tenant still contributes its [dbo].[TeamManagers] rows, and
            // the deprecated Teams.ManagerUserId is unioned in when the row carries one.
            Team? team = null;
            var managerUserIds = new List<Guid>();
            if (analyst?.TeamId is Guid teamId)
            {
                if (managersByTeam.TryGetValue(teamId, out var named)) managerUserIds.AddRange(named);

                team = teamsById.GetValueOrDefault(teamId);
                if (team?.ManagerUserId is Guid legacyManagerId) managerUserIds.Add(legacyManagerId);
            }

            var activeManagers  = TeamManagerResolution.IntersectActiveManagers(managerUserIds, rosterById);
            var (required, inForce) = SizeQuorum(team, activeManagers);

            var approverIds = approversByAnalysis.TryGetValue(analysis.AnalysisId, out var ids)
                ? ids
                : (IReadOnlyList<Guid>)[];

            tallies[analysis.AnalysisId] = CountTally(required, inForce, eligible, approverIds);
        }

        return tallies;
    }

    /// <summary>
    /// Who may sign at all: active (the roster is already IsActive- and tenant-filtered) AND in a
    /// role that can confirm. Roles.IsConfirmer is the SAME predicate DecideAsync gates the
    /// endpoint with, so the tally can never count a signature the endpoint would have refused.
    /// </summary>
    private static HashSet<Guid> EligibleVoterIds(IReadOnlyList<User> roster)
        => roster
            .Where(user => Roles.IsConfirmer(user.Role))
            .Select(user => user.UserId)
            .ToHashSet();

    /// <summary>
    /// The approvals that count. The intersection is what makes a deactivated or demoted signatory
    /// drop out of the arithmetic while their row stays on the record for the audit trail.
    /// </summary>
    private static ApprovalTally CountTally(
        int required, bool inForce, HashSet<Guid> eligibleVoterIds, IReadOnlyList<Guid> approverIds)
    {
        var counting = approverIds
            .Where(eligibleVoterIds.Contains)
            .Distinct()
            .ToList();

        return new ApprovalTally(required, counting, inForce);
    }

    /// <summary>
    /// The effective quorum for one analysis, plus the active roster it was resolved against.
    /// </summary>
    private async Task<(int Required, bool InForce, IReadOnlyList<User> Roster)>
        ResolveEffectiveQuorumAsync(string tenantId, Analysis analysis, CancellationToken ct)
    {
        // THE TEAM IS THE ANALYST'S, NOT THE CALLER'S. The caller is a manager settling somebody
        // else's proposal, so reading their own team would size the quorum from the wrong roster —
        // this is the one place that must NOT mirror AnalysisDecisionNotifier, which resolves the
        // team of the acting user because there the acting user IS the analyst.
        User? analyst = null;

        if (analysis.PreApprovedBy is Guid analystId)
        {
            analyst = await _userRepository.GetByUserIdAsync(analystId, ct).ConfigureAwait(false);

            // GetByUserIdAsync is keyed by a globally unique id and carries no tenant predicate, so
            // the scope check is written here — the same reasoning, and the same remedy, as
            // TeamsController.ValidateManagerAsync.
            if (analyst is not null
                && !string.Equals(analyst.TenantId, tenantId, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Pre-approver {UserId} of analysis {AnalysisId} does not belong to tenant {TenantId}; no quorum applies.",
                    analystId, analysis.AnalysisId, tenantId);

                analyst = null;
            }
        }

        // Deliberately resolved through GetByUserIdAsync rather than plucked out of the active
        // roster: an analyst who has since been DEACTIVATED is absent from the roster, and reading
        // their team from it would silently collapse the quorum to 1. A departed analyst's
        // pre-approval must still need the signatures their team asked for.
        var managerSet = await TeamManagerResolution
            .ResolveActiveManagersAsync(_teamRepository, _userRepository, analyst?.TeamId, tenantId, ct)
            .ConfigureAwait(false);

        // Off the very row the resolution above already read, so the quorum costs no extra query.
        var (required, inForce) = SizeQuorum(managerSet.Team, managerSet.ActiveManagers);

        return (required, inForce, managerSet.Roster);
    }

    /// <summary>
    /// Sizes the effective quorum from a team row and its ACTIVE managers. Pure, and shared by the
    /// single-analysis and the batch read so both size a quorum by the same rule.
    /// </summary>
    private static (int Required, bool InForce) SizeQuorum(Team? team, IReadOnlyList<User> activeManagers)
    {
        // Sizing counts only managers who could actually sign. [dbo].[TeamManagers] is not
        // role-filtered — a team may name an Analyst — and counting one would set a quorum that
        // nobody is able to reach, leaving the analysis at "1 of 2" permanently.
        var eligibleManagerCount = activeManagers.Count(user => Roles.IsConfirmer(user.Role));

        if (eligibleManagerCount == 0)
        {
            // No team, no named manager, none of them still active, or none of them able to confirm.
            // All four mean the same thing — nobody specific is responsible for this analysis — and
            // the answer is today's behaviour: one signature settles it.
            return (NoQuorum, false);
        }

        var configured = team?.ApprovalQuorum ?? NoQuorum;

        // Capped by the managers who exist, so a quorum of 3 never strands a team of 2 — "never
        // blocks" is the rule. Floored at 1, because DDL 065 shipped no CHECK constraint and a 0 or
        // a negative in that column would otherwise confirm an analysis on no signatures at all.
        var required = Math.Max(1, Math.Min(configured, eligibleManagerCount));

        return (required, true);
    }

    /// <summary>
    /// Copies the settled decision record off the row that won a simultaneous crossing.
    /// </summary>
    /// <remarks>
    /// Only the columns the guarded UPDATE writes. The pre-approval record is untouched because that
    /// UPDATE does not touch it either — on a confirmation it is the first of the two signatures.
    /// </remarks>
    private static void CopySettledStateOnto(Analysis target, Analysis settled)
    {
        target.Status                  = settled.Status;
        target.ReviewedBy              = settled.ReviewedBy;
        target.ReviewedAt              = settled.ReviewedAt;
        target.DecisionAmount          = settled.DecisionAmount;
        target.DecisionCurrency        = settled.DecisionCurrency;
        target.ConfirmedOperationsJson = settled.ConfirmedOperationsJson;
        target.ReviewNotes             = settled.ReviewNotes;
    }
}
