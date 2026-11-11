// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-06

using Api.Web.FinanceIA.Clases;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// Pins the filter values GET /api/notifications accepts. A new emitter type that is not added to
/// <see cref="NotificationCatalog"/> would be unfilterable (400) — this fails first, and the Portal's
/// filter list is written against exactly these values.
/// </summary>
public sealed class NotificationCatalogTests
{
    [Fact]
    public void KnownTypes_AreTheElevenEmittedTypes()
    {
        Assert.Equal(
            new[]
            {
                "ANALYSIS_APPROVED_FYI", "ANALYSIS_COMPLETED", "ANALYSIS_CONFIRMED", "ANALYSIS_FAILED",
                "ANALYSIS_PREAPPROVAL_PENDING", "ANALYSIS_REJECTED", "ANALYSIS_RETURNED",
                "BILLING_ANALYSIS_QUOTA_100", "BILLING_ANALYSIS_QUOTA_80",
                "BILLING_CHAT_QUOTA_100", "BILLING_CHAT_QUOTA_80",
            },
            NotificationCatalog.KnownTypes.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void KnownTypes_IncludeEveryBillingType()
    {
        Assert.All(BillingAlertService.AllTypes, t => Assert.Contains(t, NotificationCatalog.KnownTypes));
    }

    [Fact]
    public void KnownSourceTypes_AreAlertAnalysisAndAnalysisRun()
    {
        Assert.Equal(
            new[] { "Alert", "Analysis", "AnalysisRun" },
            NotificationCatalog.KnownSourceTypes.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Both analysis emitters write "Analysis" with a ResultId; the entityId filter resolves that one
    /// value through AnalysisResults, so the two constants must never diverge.
    /// </summary>
    [Fact]
    public void BothAnalysisEmitters_WriteTheSameSourceType()
    {
        Assert.Equal(AnalysisCompletionNotifier.AnalysisSourceType, AnalysisDecisionNotifier.AnalysisSourceType);
        Assert.Equal(NotificationCatalog.AnalysisSource, AnalysisDecisionNotifier.AnalysisSourceType);
    }
}
