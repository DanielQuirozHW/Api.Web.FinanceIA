// Project : Api.Web.FinanceIA
// Created : 2026-09-10

using System.Globalization;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Default implementation of <see cref="IAnalysisDecisionNotifier"/>.
///
/// <para>
/// Registered Scoped, like the repositories it writes through. It is driven from
/// <c>AnalysisController.DecideAsync</c>, after the analysis has been persisted and the audit row
/// written — so by the time this runs the decision is already a fact, and nothing that happens here
/// can take it back.
/// </para>
///
/// <para>
/// THE FALLBACK IS THE DESIGN. Routing a pre-approval to "the manager of the analyst's team" has
/// three ways to come up empty: the analyst is in no team, the team has no manager, or the manager's
/// account has been deactivated. All three resolve to notifying every active RiskManager of the
/// tenant rather than to notifying nobody. Over-notifying is a nuisance; under-notifying loses the
/// hand-off the feature exists to deliver, and leaves an analysis sitting in 'PreApproved' with no
/// one aware of it.
/// </para>
///
/// <para>
/// SOURCEID IS THE RESULT ID. Every row it writes carries SourceType "Analysis" and, as SourceId,
/// the <c>AnalysisResults.ResultId</c> — not the <c>AnalysisId</c>. The Portal deep-links a
/// notification to /dashboard/analyses/{sourceId}, which is the ResultId-keyed route, and
/// <c>NotificationDto</c> exposes no second id, so the navigable id has to be SourceId itself. The
/// dedupe key moves with it: see the recipients loop.
/// </para>
/// </summary>
public sealed class AnalysisDecisionNotifier : IAnalysisDecisionNotifier
{
    /// <summary>An analyst pre-approved above their limit; a manager has to settle it.</summary>
    public const string PreApprovalPendingType = "ANALYSIS_PREAPPROVAL_PENDING";

    /// <summary>A manager confirmed a pre-approval.</summary>
    public const string ConfirmedType = "ANALYSIS_CONFIRMED";

    /// <summary>A manager sent a pre-approval back for rework.</summary>
    public const string ReturnedType = "ANALYSIS_RETURNED";

    /// <summary>An analysis was rejected — either a pre-approval or a direct decision.</summary>
    public const string RejectedType = "ANALYSIS_REJECTED";

    /// <summary>An analyst approved within their own limit; the manager is told for visibility only.</summary>
    public const string ApprovedFyiType = "ANALYSIS_APPROVED_FYI";

    /// <summary>Value stored in <see cref="Notification.SourceType"/> by every row below.</summary>
    public const string AnalysisSourceType = "Analysis";

    // The audit action names DecideAsync resolves to. Kept as constants here so the routing switch
    // and the controller cannot drift apart silently over a typo in a string literal.
    private const string PreApprovedAction = "Analysis.PreApproved";
    private const string ConfirmedAction   = "Analysis.Confirmed";
    private const string ReturnedAction    = "Analysis.ReturnedToReview";
    private const string RejectedAction    = "Analysis.Rejected";
    private const string ApprovedAction    = "Analysis.Approved";

    // Copy is es-AR: this text goes straight to an Argentine analyst in the notifications inbox.
    private static readonly CultureInfo EsAr = CultureInfo.GetCultureInfo("es-AR");

    private readonly INotificationRepository            _notificationRepository;
    private readonly ITeamRepository                    _teamRepository;
    private readonly IUserRepository                    _userRepository;
    private readonly IAnalysisResultRepository          _analysisResultRepository;
    private readonly ILogger<AnalysisDecisionNotifier>  _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="AnalysisDecisionNotifier"/>.
    /// </summary>
    public AnalysisDecisionNotifier(
        INotificationRepository           notificationRepository,
        ITeamRepository                   teamRepository,
        IUserRepository                   userRepository,
        IAnalysisResultRepository         analysisResultRepository,
        ILogger<AnalysisDecisionNotifier> logger)
    {
        _notificationRepository   = notificationRepository;
        _teamRepository           = teamRepository;
        _userRepository           = userRepository;
        _analysisResultRepository = analysisResultRepository;
        _logger                   = logger;
    }

    /// <inheritdoc />
    public async Task NotifyAsync(
        string            tenantId,
        Analysis          analysis,
        User              currentUser,
        string            auditAction,
        bool              wasPreApproved,
        string?           entityName,
        decimal?          amount,
        string?           currency,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || analysis is null || currentUser is null) return;

        try
        {
            var (type, recipients) = await ResolveRoutingAsync(
                tenantId, analysis, currentUser, auditAction, wasPreApproved, ct)
                .ConfigureAwait(false);

            if (type is null || recipients.Count == 0) return;

            // The id the notification POINTS AT, resolved once for the whole batch.
            //
            // Deliberately NOT analysis.AnalysisId. The Portal deep-links a notification to
            // /dashboard/analyses/{sourceId}, and that route — like GET /api/analyses/{id} and
            // PATCH /api/analyses/{id}/decision — is keyed by RESULT id. NotificationDto carries no
            // second id field, so the navigable one has to BE SourceId or the link 404s.
            //
            // One indexed set-based read, the same call AlertsController and CreditLinesController
            // make to build their own navigable ids.
            var resultMap = await _analysisResultRepository
                .GetResultIdsByAnalysisIdsAsync([analysis.AnalysisId], tenantId, ct)
                .ConfigureAwait(false);

            var navigableSourceId = analysis.AnalysisId;

            if (resultMap.TryGetValue(analysis.AnalysisId, out var resultId))
            {
                navigableSourceId = resultId;
            }
            else
            {
                // No AnalysisResult row — an analysis decided before it was ever scored. Falling back
                // to the AnalysisId keeps the hand-off in the manager's inbox with a link that does
                // not resolve, which beats emitting nothing at all. Ids only, as everywhere here.
                _logger.LogWarning(
                    "No AnalysisResult row for analysis {AnalysisId} tenant {TenantId}; the decision notification will carry the AnalysisId as its source and its deep link will not resolve.",
                    analysis.AnalysisId, tenantId);
            }

            var title = BuildTitle(type);
            var body  = BuildBody(type, entityName, amount, currency);
            var now   = DateTime.UtcNow;

            var notifications = new List<Notification>();

            foreach (var recipientId in recipients)
            {
                // Keyed on (user, type, SOURCE) rather than on a time window: the question is "has
                // this person already been told about THIS analysis?", and a period-based check
                // would suppress a legitimate notification about a different analysis of the same
                // type. See INotificationRepository for the full argument.
                //
                // The key is navigableSourceId and NOT analysis.AnalysisId, because it has to be the
                // value actually STORED in SourceId below — the two must move together or the check
                // matches nothing and every re-decision re-emits.
                var alreadyNotified = await _notificationRepository
                    .ExistsByTypeForUserAndSourceAsync(
                        tenantId, recipientId, type, navigableSourceId, ct)
                    .ConfigureAwait(false);

                if (alreadyNotified) continue;

                notifications.Add(new Notification
                {
                    // NotificationConfiguration maps NotificationId as ValueGeneratedNever —
                    // the app supplies it, exactly as BillingAlertService does.
                    NotificationId = Guid.NewGuid(),
                    TenantId       = tenantId,
                    UserId         = recipientId,
                    Type           = type,
                    Title          = title,
                    Body           = body,
                    SourceType     = AnalysisSourceType,
                    // The ResultId, not the AnalysisId — see where navigableSourceId is resolved.
                    SourceId       = navigableSourceId,
                    IsRead         = false,
                    CreatedAt      = now
                });
            }

            if (notifications.Count == 0) return;

            await _notificationRepository.CreateManyAsync(notifications, ct).ConfigureAwait(false);

            // Ids and counts only. The amount is a credit figure and the body names the company —
            // neither belongs in a log line, exactly as DecideAsync's own info line omits the amount.
            _logger.LogInformation(
                "Analysis decision notifications emitted: {AnalysisId} tenant {TenantId} type={Type} recipients={RecipientCount}",
                analysis.AnalysisId, tenantId, type, notifications.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller hung up. Not a failure of anything — Debug, so it never reads as a problem.
            _logger.LogDebug(
                "Analysis decision notification emission cancelled for analysis {AnalysisId} tenant {TenantId}.",
                analysis.AnalysisId, tenantId);
        }
        catch (Exception ex)
        {
            // Deliberately swallowed, and Warning rather than Error. The decision is ALREADY
            // committed and audited by the time this runs; what is lost is only the inbox copy.
            // The likeliest cause is a known, self-healing deploy window — docs/database/062, 063 or
            // 064 not yet run on this tenant's database, so [dbo].[Teams], [dbo].[Users].[TeamId] or
            // [dbo].[TeamManagers] does not exist. The notifications start appearing the moment the
            // DBA runs the DDL.
            _logger.LogWarning(
                ex,
                "Failed to emit decision notifications for analysis {AnalysisId} tenant {TenantId}. The decision was recorded and is unaffected. If this repeats, check that docs/database/062_teams_table.sql, 063_users_add_teamid.sql and 064_team_managers_table.sql have been run on this tenant's database.",
                analysis.AnalysisId, tenantId);
        }
    }

    /// <summary>
    /// Maps one decision transition to its notification type and its recipient list.
    /// </summary>
    /// <remarks>
    /// <paramref name="auditAction"/> drives the switch because it is the single value that already
    /// distinguishes all five outcomes — it is what the audit trail records, so the notification and
    /// the audit row can never disagree about what happened.
    ///
    /// <para>
    /// 'Analysis.Rejected' is the one action covering two different transitions, split by
    /// <paramref name="wasPreApproved"/>: rejecting a pre-approval concerns the analyst who proposed
    /// it, while rejecting outright concerns whoever requested the analysis in the first place.
    /// </para>
    /// </remarks>
    private async Task<(string? Type, IReadOnlyList<Guid> Recipients)> ResolveRoutingAsync(
        string            tenantId,
        Analysis          analysis,
        User              currentUser,
        string            auditAction,
        bool              wasPreApproved,
        CancellationToken ct)
    {
        switch (auditAction)
        {
            // ── 1. The analyst pre-approved; it needs a second signature ──────────
            case PreApprovedAction:
            {
                var managers = await ResolveManagerRecipientsAsync(
                    tenantId, currentUser, analysis.AnalysisId, ct).ConfigureAwait(false);

                // Deliberately NOT filtered against the caller. An Admin who is their own team's
                // manager is an odd configuration, but suppressing the notification would leave the
                // pre-approval with no trace in anyone's inbox at all.
                return (PreApprovalPendingType, managers);
            }

            // ── 2/3. The manager settled it — tell the analyst who proposed it ────
            case ConfirmedAction:
                return (ConfirmedType, Single(analysis.PreApprovedBy));

            case ReturnedAction:
                return (ReturnedType, Single(analysis.PreApprovedBy));

            // ── 4/6. Rejection: who cares depends on whether it was pre-approved ──
            case RejectedAction when wasPreApproved:
                return (RejectedType, Single(analysis.PreApprovedBy));

            case RejectedAction:
                // A direct rejection concerns whoever asked for the analysis — unless that is the
                // person who just rejected it, who does not need telling about their own act.
                return analysis.RequestedBy == currentUser.UserId
                    ? (null, [])
                    : (RejectedType, Single(analysis.RequestedBy));

            // ── 5. Direct approval within limit — visibility for the manager ──────
            case ApprovedAction:
            {
                var managers = await ResolveManagerRecipientsAsync(
                    tenantId, currentUser, analysis.AnalysisId, ct).ConfigureAwait(false);

                // Unlike case 1, a self-only result is dropped. This notification exists so somebody
                // ABOVE the approver can see what was committed; when the approver is the only
                // person it would reach — a RiskManager or Admin with nobody above them — it would
                // just be telling them what they already know.
                var others = managers.Where(id => id != currentUser.UserId).ToList();

                return others.Count == 0
                    ? (null, [])
                    : (ApprovedFyiType, others);
            }

            default:
                return (null, []);
        }
    }

    /// <summary>
    /// Resolves EVERY manager of the acting user's team, falling back to an escalation ladder
    /// whenever none can be established.
    /// </summary>
    /// <remarks>
    /// The fallback fires on ANY of four gaps — no team, no manager named on the team (in either
    /// [dbo].[TeamManagers] or the deprecated Teams.ManagerUserId), or EVERY named manager's account
    /// being inactive — and they are treated identically on purpose: from the analysis's point of
    /// view all of them mean "nobody specific is responsible for this", and the answer to that is to
    /// tell everyone who could be, not to tell no one. Note the fourth is a set condition now: one
    /// active manager out of three is enough to keep the ladder out of it.
    ///
    /// <para>
    /// THE LADDER: active RiskManagers first, and only if there are none, active Admins. An Admin
    /// IS a valid confirmer — <c>isConfirmer</c> in DecideAsync admits both roles — so a tenant that
    /// has not staffed a RiskManager still has someone who can action a pre-approval instead of it
    /// silently going nowhere. But the Admin is the LAST RESORT, never a habitual recipient: as long
    /// as one RiskManager exists the Admin hears nothing, because an inbox that fills with routine
    /// traffic stops being read, and this is the safety net that has to still work when it matters.
    /// </para>
    ///
    /// <para>
    /// Both rungs empty is a real possibility — a tenant with only Analysts — and it is a
    /// configuration problem, not a runtime error: nothing is emitted and a Warning names the
    /// analysis so it can be chased, with ids only and never an amount.
    /// </para>
    ///
    /// <para>
    /// Shared by case 1 (pre-approval pending) and case 5 (direct-approval FYI) so the two can never
    /// drift apart. Case 5 drops a self-only result AFTER this returns; that rule is deliberately
    /// not applied here, because case 1 must notify even the caller rather than leave a pre-approval
    /// with no trace at all.
    /// </para>
    ///
    /// <para>
    /// The roster read also supplies the active-and-in-tenant check on the named managers, so the
    /// happy path costs at most three queries — the manager set, the team row for the deprecated
    /// column, and the roster — and every fallback path exactly one.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<Guid>> ResolveManagerRecipientsAsync(
        string            tenantId,
        User              currentUser,
        Guid              analysisId,
        CancellationToken ct)
    {
        // The union-and-intersect that decides who counts as an active manager lives in
        // TeamManagerResolution, NOT here, since Phase 2: the quorum engine sizes a credit decision
        // from the same set, and two copies of this rule would drift. If the quorum counted a
        // manager this never told, an analysis would wait on a signature from someone who does not
        // know it is pending; if this told a manager the quorum did not count, their approval would
        // appear to do nothing. The three queries are unchanged.
        var managerSet = await TeamManagerResolution
            .ResolveActiveManagersAsync(_teamRepository, _userRepository, currentUser.TeamId, tenantId, ct)
            .ConfigureAwait(false);

        var roster = managerSet.Roster;

        if (managerSet.ActiveManagers.Count > 0)
            return managerSet.ActiveManagers.Select(manager => manager.UserId).ToList();

        // THE LADDER STAYS HERE, and is deliberately NOT part of the shared resolution. It answers
        // "who else could be told", which is a notification question; the quorum engine reads an
        // empty manager set as "no quorum is in force" and lets a single signature settle the
        // analysis, exactly as it did before Phase 2. Sharing the ladder would silently make every
        // RiskManager in the tenant part of somebody's quorum.
        //
        // There is no role-filtering repository method, so the roster — which already holds only
        // ACTIVE users — is filtered here.
        var riskManagers = ByRole(roster, Roles.RiskManager);
        if (riskManagers.Count > 0) return riskManagers;

        // Rung two, and only because rung one came up empty.
        var admins = ByRole(roster, Roles.Admin);
        if (admins.Count > 0) return admins;

        // Nobody at all who could confirm. Not an error and never a throw — a tenant staffed only
        // with Analysts is a configuration to fix, not a failed request. Ids only: no amount, no
        // notification body, matching every other log line on this path.
        _logger.LogWarning(
            "No RiskManager or Admin available to notify for analysis {AnalysisId} tenant {TenantId}",
            analysisId, tenantId);

        return [];
    }

    /// <summary>
    /// The active users of one roster holding a given role, de-duplicated by user id.
    /// </summary>
    private static List<Guid> ByRole(IReadOnlyList<User> roster, string role)
        => roster
            .Where(u => string.Equals(u.Role, role, StringComparison.Ordinal))
            .Select(u => u.UserId)
            .Distinct()
            .ToList();

    /// <summary>
    /// Wraps an optional recipient as a list, dropping it when there is nobody to notify.
    /// </summary>
    /// <remarks>
    /// <c>Analysis.PreApprovedBy</c> is nullable, and on a re-decided analysis it legitimately can
    /// be null — there is then no proposer to inform, which is a no-op rather than an error.
    /// </remarks>
    private static IReadOnlyList<Guid> Single(Guid? recipient)
        => recipient is Guid id ? [id] : [];

    /// <summary>
    /// The fixed title for one notification type.
    /// </summary>
    /// <remarks>
    /// Titles carry no interpolated values on purpose. [Title] is nvarchar(300) while [Body] is
    /// nvarchar(max), so keeping the company name and the amount in the body removes any chance of a
    /// long business name truncating the headline.
    /// </remarks>
    private static string BuildTitle(string type) => type switch
    {
        PreApprovalPendingType => "Análisis pendiente de aprobación",
        ConfirmedType          => "Tu pre-aprobación fue confirmada",
        ReturnedType           => "Tu pre-aprobación volvió a revisión",
        RejectedType           => "Análisis rechazado",
        ApprovedFyiType        => "Análisis aprobado por tu equipo",
        _                      => "Actualización de un análisis"
    };

    /// <summary>
    /// The body copy for one notification type, naming the company and the amount when there is one.
    /// </summary>
    private static string BuildBody(string type, string? entityName, decimal? amount, string? currency)
    {
        var company = string.IsNullOrWhiteSpace(entityName) ? "Una PyME" : entityName;
        var figure  = FormatAmount(amount, currency);

        return type switch
        {
            PreApprovalPendingType => figure is null
                ? $"{company}: un analista pre-aprobó el análisis y espera tu confirmación."
                : $"{company}: un analista pre-aprobó {figure} y espera tu confirmación.",

            ConfirmedType => figure is null
                ? $"{company}: se confirmó el análisis que pre-aprobaste."
                : $"{company}: se confirmó por {figure} el análisis que pre-aprobaste.",

            ReturnedType =>
                $"{company}: el análisis que pre-aprobaste volvió a revisión. Revisá los comentarios y vuelve a enviarlo.",

            RejectedType =>
                $"{company}: el análisis fue rechazado. Revisá los comentarios para ver el motivo.",

            ApprovedFyiType => figure is null
                ? $"{company}: un analista de tu equipo aprobó el análisis dentro de su límite."
                : $"{company}: un analista de tu equipo aprobó {figure} dentro de su límite.",

            _ => $"{company}: el estado del análisis cambió."
        };
    }

    /// <summary>
    /// Formats an amount for the copy, or returns null when there is no figure to name.
    /// </summary>
    /// <remarks>
    /// A rejection and a return commit nothing, so both arrive here with a null amount and the copy
    /// simply omits the figure rather than printing a zero that never existed.
    /// </remarks>
    private static string? FormatAmount(decimal? amount, string? currency)
    {
        if (amount is not > 0m) return null;

        var formatted = amount.Value.ToString("N2", EsAr);

        return string.IsNullOrWhiteSpace(currency)
            ? formatted
            : $"{currency} {formatted}";
    }
}
