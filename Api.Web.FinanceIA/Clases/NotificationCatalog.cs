// Project : Api.Web.FinanceIA
// Created : 2026-11-06

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// The closed sets of <c>Notification.Type</c> and <c>Notification.SourceType</c> values the
/// notifications listing accepts as filters.
/// </summary>
/// <remarks>
/// Built FROM the emitters' own constants, never from string literals, so a filter value and the value
/// an emitter writes cannot drift apart. A new emitter type is one more entry here — the
/// <c>NotificationCatalogTests</c> pin the current set, so forgetting it fails a test rather than
/// silently answering 400 to the Portal's new filter.
/// </remarks>
public static class NotificationCatalog
{
    /// <summary>Every type an emitter writes today (exact, case-sensitive codes).</summary>
    public static readonly IReadOnlySet<string> KnownTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        BillingAlertService.AnalysisQuota80Type,
        BillingAlertService.AnalysisQuota100Type,
        BillingAlertService.ChatQuota80Type,
        BillingAlertService.ChatQuota100Type,
        AnalysisCompletionNotifier.CompletedType,
        AnalysisCompletionNotifier.FailedType,
        AnalysisDecisionNotifier.PreApprovalPendingType,
        AnalysisDecisionNotifier.ConfirmedType,
        AnalysisDecisionNotifier.ReturnedType,
        AnalysisDecisionNotifier.RejectedType,
        AnalysisDecisionNotifier.ApprovedFyiType,
    };

    /// <summary>
    /// "Analysis": SourceId is an <c>AnalysisResults.ResultId</c> (written by the completion and decision
    /// notifiers, which must agree — a test pins that they do).
    /// </summary>
    public const string AnalysisSource = AnalysisCompletionNotifier.AnalysisSourceType;

    /// <summary>"AnalysisRun": SourceId is the <c>Documents.DocumentId</c> of the failed run.</summary>
    public const string AnalysisRunSource = AnalysisCompletionNotifier.AnalysisRunSourceType;

    /// <summary>"Alert": SourceId is an <c>Alerts.AlertId</c> (billing quota alerts, tenant-wide, no PyME).</summary>
    public const string AlertSource = BillingAlertService.AlertSourceType;

    /// <summary>Every source type an emitter writes today.</summary>
    public static readonly IReadOnlySet<string> KnownSourceTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        AlertSource,
        AnalysisSource,
        AnalysisRunSource,
    };
}
