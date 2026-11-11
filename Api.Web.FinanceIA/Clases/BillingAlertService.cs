// Project : Api.Web.FinanceIA
// Created : 2026-08-31

using System.Globalization;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.Extensions.Options;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Default implementation of <see cref="IBillingAlertService"/>.
///
/// <para>
/// Registered Scoped, like the repository it writes through. It is driven from GET
/// /api/billing/usage rather than from the enforcement filter: when the analysis gate runs, the
/// analysis being gated has not been counted yet, so the snapshot there lags by one and a tenant
/// would be told it was at 99% while making the request that takes it to 100%. The usage endpoint
/// is the consistent trigger, and it is what the Portal polls — so the alert appears even for a
/// tenant that has stopped creating analyses.
/// </para>
///
/// <para>
/// Because that trigger is a polled GET, two properties carry the whole design: it must be
/// IDEMPOTENT (exactly one alert per tenant, type and calendar month, or a poll every 30 seconds
/// turns the inbox into a flood) and it must be CHEAP when nothing is due (a tenant under 80% must
/// not pay a database round-trip per poll).
/// </para>
/// </summary>
public sealed class BillingAlertService : IBillingAlertService
{
    /// <summary>Analysis quota at or above 80% of the plan.</summary>
    public const string AnalysisQuota80Type = "BILLING_ANALYSIS_QUOTA_80";

    /// <summary>Analysis quota at or above 100% of the plan.</summary>
    public const string AnalysisQuota100Type = "BILLING_ANALYSIS_QUOTA_100";

    /// <summary>Chat-token quota at or above 80% of the plan.</summary>
    public const string ChatQuota80Type = "BILLING_CHAT_QUOTA_80";

    /// <summary>Chat-token quota at or above 100% of the plan.</summary>
    public const string ChatQuota100Type = "BILLING_CHAT_QUOTA_100";

    /// <summary>
    /// Every type this service emits. Each one describes a single billing period, so once that period
    /// is over it describes nothing: <see cref="CupoAlertService"/> auto-resolves the open ones created
    /// before the current period started.
    /// </summary>
    public static readonly IReadOnlyList<string> AllTypes =
        [AnalysisQuota80Type, AnalysisQuota100Type, ChatQuota80Type, ChatQuota100Type];

    /// <summary>Metric key stored on the analysis-quota alerts.</summary>
    public const string AnalysesMetricKey = "analyses_percent";

    /// <summary>Metric key stored on the chat-quota alerts.</summary>
    public const string ChatTokensMetricKey = "chat_tokens_percent";

    private const decimal WarningThreshold = 80m;
    private const decimal LimitThreshold   = 100m;

    // dbo.Alerts.Severity is constrained by CK_Alerts_Severity to exactly these spellings, and
    // AlertsController counts severities by case-sensitive string equality. TitleCase is not a
    // style choice here — lowercase would be rejected by the DB and would count as zero in the UI.
    private const string WarningSeverity = "Medium";
    private const string LimitSeverity   = "High";

    // Copy is es-AR: this text goes straight to an Argentine analyst in the alerts inbox.
    private static readonly CultureInfo EsAr = CultureInfo.GetCultureInfo("es-AR");

    /// <summary>Value stored in <see cref="Notification.SourceType"/> by the fan-out below.</summary>
    public const string AlertSourceType = "Alert";

    private readonly IAlertRepository            _alertRepository;
    private readonly INotificationRepository     _notificationRepository;
    private readonly IUserRepository             _userRepository;
    private readonly IOptions<BillingOptions>    _billingOptions;
    private readonly ILogger<BillingAlertService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="BillingAlertService"/>.
    /// </summary>
    public BillingAlertService(
        IAlertRepository             alertRepository,
        INotificationRepository      notificationRepository,
        IUserRepository              userRepository,
        IOptions<BillingOptions>     billingOptions,
        ILogger<BillingAlertService> logger)
    {
        _alertRepository        = alertRepository;
        _notificationRepository = notificationRepository;
        _userRepository         = userRepository;
        _billingOptions         = billingOptions;
        _logger                 = logger;
    }

    /// <inheritdoc />
    public async Task EvaluateAsync(string tenantId, BillingUsageSnapshot snapshot, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || snapshot is null) return;

        try
        {
            var due = new List<Alert>();

            // ── Analysis quota — always evaluated. Those tiers are the agreed commercial numbers
            // and the gate blocks on them unconditionally, so the warning must be unconditional too.
            //
            // Unless the figure is not a fact: a fallback plan or a failed count is exactly the
            // situation where telling someone they are near a limit is worse than saying nothing.
            if (!snapshot.AnalysisQuotaIsUnreliable)
            {
                await AddIfDueAsync(
                    tenantId, snapshot, snapshot.AnalysesPercent,
                    AnalysisQuota80Type, AnalysisQuota100Type, AnalysesMetricKey,
                    "los análisis", due, ct)
                    .ConfigureAwait(false);
            }

            // ── Chat-token quota — only when the flag that makes the chat gate real is on.
            // Mirrors BillingQuotaFilter exactly. While Billing:EnforceChatTokens is false the
            // per-plan token figures are unagreed placeholders (see BillingOptions), and warning a
            // tenant about a limit that will not be enforced is noise about a number nobody stands
            // behind. Measured, not announced.
            if (_billingOptions.Value.EnforceChatTokens && !snapshot.ChatQuotaIsUnreliable)
            {
                await AddIfDueAsync(
                    tenantId, snapshot, snapshot.ChatTokensPercent,
                    ChatQuota80Type, ChatQuota100Type, ChatTokensMetricKey,
                    "los tokens de chat", due, ct)
                    .ConfigureAwait(false);
            }

            if (due.Count == 0) return;

            await _alertRepository.CreateManyAsync(due, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Emitted {AlertCount} billing quota alert(s) for tenant {TenantId}: {AlertTypes}",
                due.Count, tenantId, string.Join(", ", due.Select(a => a.AlertType)));

            // Secondary and strictly best-effort. The alerts above are the deliverable; the
            // per-user notifications are a delivery convenience on top of them, and
            // FanOutNotificationsAsync owns its own try/catch so it can never undo the line above.
            await FanOutNotificationsAsync(tenantId, snapshot, due, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller hung up mid-poll. Routine on an endpoint the Portal polls, and not a
            // failure of anything — Debug, so it never reads as a problem in the logs.
            _logger.LogDebug(
                "Billing quota alert evaluation cancelled for tenant {TenantId}.", tenantId);
        }
        catch (Exception ex)
        {
            // Deliberately swallowed. This runs inside a GET whose response is already computed;
            // an alert that fails to persist must never turn a working usage read into a 500.
            //
            // The likeliest cause by far is script 048 not having been run yet: dbo.Alerts.EntityId
            // is still NOT NULL in that case and rejects every system-level insert. That is a
            // Warning and not an Error precisely because the deploy order makes it a known,
            // self-healing window — the alerts start appearing the moment the DBA runs the DDL.
            _logger.LogWarning(
                ex,
                "Failed to evaluate or emit billing quota alerts for tenant {TenantId}. The usage response is unaffected. If this repeats, check that docs/database/048_alerts_entityid_nullable.sql has been run on this tenant's database.",
                tenantId);
        }
    }

    /// <summary>
    /// Fans the alerts just emitted out to every active user of the tenant, one notification each.
    /// </summary>
    /// <remarks>
    /// Alerts are per-tenant; notifications are per-user. A tenant with five analysts therefore
    /// turns one quota alert into five notifications, so each of them gets their own unread badge
    /// and their own read state.
    ///
    /// <para>
    /// FAULT ISOLATION. This has its own try/catch, nested inside EvaluateAsync's. That is not
    /// redundant: without it, a notification failure would be caught by the outer handler AFTER the
    /// alerts had already been written, and the log would blame the alert emission for something
    /// that succeeded. The rule this enforces is that notification trouble never costs the tenant
    /// its alerts, and never turns a working GET /usage into a 500.
    /// </para>
    ///
    /// <para>
    /// IDEMPOTENCY. Two independent guards. The outer one is free: EvaluateAsync only reaches this
    /// method when <c>due</c> is non-empty, and <see cref="AddIfDueAsync"/> only puts an alert in
    /// <c>due</c> the first time that tier fires in the period — so the polled GET does not arrive
    /// here every 30 seconds. The inner one is the per-user existence check below, which covers the
    /// case the outer guard cannot: the notification write failed after the alerts were committed
    /// (script 049 not yet run, say), and a later poll re-emits a different tier. Without it, that
    /// second poll would re-notify for the tier that already landed.
    /// </para>
    ///
    /// <para>
    /// Notification.Type carries the alert's own PER-TIER AlertType — BILLING_ANALYSIS_QUOTA_80 and
    /// BILLING_ANALYSIS_QUOTA_100 are distinct values, not a shared 'BILLING_QUOTA' constant. This
    /// is what lets the existence check work at all: it filters on the stored Type column, so the
    /// value written and the value checked have to be the same one. It also keeps the 80% and 100%
    /// notifications independent, exactly as the two alerts are — a user who was told at 80% must
    /// still be told at 100%, which a shared type would silently suppress.
    /// </para>
    /// </remarks>
    private async Task FanOutNotificationsAsync(
        string               tenantId,
        BillingUsageSnapshot snapshot,
        List<Alert>          due,
        CancellationToken    ct)
    {
        try
        {
            var users = await _userRepository
                .GetActiveByTenantAsync(tenantId, ct)
                .ConfigureAwait(false);

            if (users.Count == 0) return;

            var notifications = new List<Notification>();

            foreach (var user in users)
            {
                foreach (var alert in due)
                {
                    var alreadyNotified = await _notificationRepository
                        .ExistsByTypeForUserInPeriodAsync(
                            tenantId, user.UserId, alert.AlertType,
                            snapshot.PeriodStart, snapshot.PeriodEnd, ct)
                        .ConfigureAwait(false);

                    if (alreadyNotified) continue;

                    notifications.Add(new Notification
                    {
                        // NotificationConfiguration maps NotificationId as ValueGeneratedNever —
                        // the app supplies it, exactly as it does for AlertId.
                        NotificationId = Guid.NewGuid(),
                        TenantId       = tenantId,
                        UserId         = user.UserId,
                        Type           = alert.AlertType,
                        Title          = alert.Title,
                        Body           = alert.Description,
                        SourceType     = AlertSourceType,
                        SourceId       = alert.AlertId,
                        IsRead         = false,
                        CreatedAt      = DateTime.UtcNow
                    });
                }
            }

            if (notifications.Count == 0) return;

            await _notificationRepository.CreateManyAsync(notifications, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Fanned out {NotificationCount} billing quota notification(s) to {UserCount} user(s) for tenant {TenantId}",
                notifications.Count, users.Count, tenantId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller hung up mid-poll. Routine, and not a failure of anything.
            _logger.LogDebug(
                "Billing quota notification fan-out cancelled for tenant {TenantId}.", tenantId);
        }
        catch (Exception ex)
        {
            // Deliberately swallowed, and Warning rather than Error for the same reason as the
            // handler in EvaluateAsync: the likeliest cause is a known, self-healing deploy window
            // where docs/database/049_notifications_table.sql has not been run on this tenant's
            // database yet, so [dbo].[Notifications] does not exist. The alerts for this period
            // ARE already written at this point and the usage response is unaffected; what is lost
            // is only the per-user copy, and only until the DDL runs.
            _logger.LogWarning(
                ex,
                "Failed to fan out billing quota notifications for tenant {TenantId}. The alerts were already emitted and the usage response is unaffected. If this repeats, check that docs/database/049_notifications_table.sql has been run on this tenant's database.",
                tenantId);
        }
    }

    /// <summary>
    /// Works out which tier a percentage is in, checks it has not already fired this period, and
    /// appends the alert to <paramref name="due"/> when it is genuinely new.
    /// </summary>
    /// <remarks>
    /// Only the HIGHEST due tier is considered. At 100% this offers the _100 type and never the
    /// _80 one — the 80% warning was already emitted by an earlier poll, back when it was the
    /// highest tier due. That ordering is why an existing _80 must not suppress _100: they are
    /// distinct types and both are correct, in sequence, within one month.
    ///
    /// <para>
    /// Below 80% this returns before touching the database. That is the common case on a polled
    /// endpoint and the reason a healthy tenant pays nothing for this feature.
    /// </para>
    /// </remarks>
    private async Task AddIfDueAsync(
        string               tenantId,
        BillingUsageSnapshot snapshot,
        decimal              percent,
        string               warningType,
        string               limitType,
        string               metricKey,
        string               metricNoun,
        List<Alert>          due,
        CancellationToken    ct)
    {
        var (alertType, threshold, severity) = percent switch
        {
            >= LimitThreshold   => (limitType,   LimitThreshold,   LimitSeverity),
            >= WarningThreshold => (warningType, WarningThreshold, WarningSeverity),
            _                   => (string.Empty, 0m, string.Empty)
        };

        if (alertType.Length == 0) return;

        var alreadyEmitted = await _alertRepository
            .ExistsByTypeInPeriodAsync(tenantId, alertType, snapshot.PeriodStart, snapshot.PeriodEnd, ct)
            .ConfigureAwait(false);

        if (alreadyEmitted) return;

        due.Add(BuildAlert(tenantId, alertType, severity, threshold, percent, metricKey, metricNoun, snapshot));
    }

    /// <summary>
    /// Builds one system-level alert row.
    /// </summary>
    /// <remarks>
    /// EntityId and AnalysisId are null: this is about the tenant's plan, not about any company it
    /// analysed. AlertsController renders that as "Sistema". Note dbo.Alerts.EntityId only accepts
    /// null once script 048 has run.
    ///
    /// <para>
    /// MetricValue carries the PERCENTAGE, never the underlying count. The column is
    /// decimal(10,4) — it tops out just under 1,000,000 — so a chat-token figure in the millions
    /// would overflow it, while a percentage never can. ThresholdValue is the tier that fired, so
    /// the two columns read together as "82.35 against a threshold of 80".
    /// </para>
    /// </remarks>
    private static Alert BuildAlert(
        string               tenantId,
        string               alertType,
        string               severity,
        decimal              threshold,
        decimal              percent,
        string               metricKey,
        string               metricNoun,
        BillingUsageSnapshot snapshot)
    {
        // Rounded for the copy only. The stored MetricValue keeps the snapshot's own 2dp value —
        // the inbox should read "82%", not "82,35%", but the record should stay exact.
        var displayPercent = Math.Round(percent, 0, MidpointRounding.AwayFromZero).ToString(EsAr);

        var isAtLimit = threshold >= LimitThreshold;

        var title = isAtLimit
            ? "Límite del plan alcanzado"
            : "Consumo del plan al 80%";

        var description = isAtLimit
            ? $"Usaste el {displayPercent}% de {metricNoun} de tu plan ({snapshot.PlanName}) este mes. La cuota se reinicia el 1 del mes que viene."
            : $"Usaste el {displayPercent}% de {metricNoun} de tu plan ({snapshot.PlanName}) este mes.";

        return new Alert
        {
            AlertId    = Guid.NewGuid(), // AlertConfiguration maps AlertId as ValueGeneratedNever — the app supplies it.
            TenantId   = tenantId,
            EntityId   = null,
            AnalysisId = null,
            AlertType  = alertType,
            Severity   = severity,
            Title      = title,
            Description = description,
            MetricKey      = metricKey,
            MetricValue    = percent,
            ThresholdValue = threshold,
            IsRead         = false,
            IsResolved     = false,
            CreatedAt      = DateTime.UtcNow
        };
    }
}
