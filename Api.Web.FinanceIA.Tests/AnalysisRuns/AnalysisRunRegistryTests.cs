// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-19

using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Microsoft.Extensions.Options;
using Xunit;
using static Api.Web.FinanceIA.Tests.AnalysisRuns.AnalysisRunTestData;

namespace Api.Web.FinanceIA.Tests.AnalysisRuns;

/// <summary>Covers <see cref="AnalysisRunRegistry"/>: reference-counted tracking and admission limits.</summary>
public sealed class AnalysisRunRegistryTests
{
    private static AnalysisRunRegistry Build(int capacity = 100, int perTenant = 5) =>
        new(Options.Create(new AnalysisOptions { QueueCapacity = capacity, MaxQueuedPerTenant = perTenant }));

    [Fact]
    public void Tracking_IsReferenceCounted()
    {
        var registry = Build();
        var doc = Guid.NewGuid();

        registry.Track(doc);
        registry.Track(doc);
        registry.Release(doc);

        Assert.True(registry.IsTracked(doc));

        registry.Release(doc);
        Assert.False(registry.IsTracked(doc));

        registry.Release(doc); // an extra release is harmless
        Assert.False(registry.IsTracked(doc));
    }

    [Fact]
    public void Admission_TracksTheDocument_UntilReleased()
    {
        var registry = Build();
        var doc = Guid.NewGuid();

        Assert.Equal(AnalysisAdmission.Admitted, registry.TryAdmit(TenantA, doc));
        Assert.True(registry.IsTracked(doc));
        Assert.Equal(1, registry.AdmittedCount);

        registry.ReleaseAdmission(TenantA, doc);
        Assert.False(registry.IsTracked(doc));
        Assert.Equal(0, registry.AdmittedCount);
    }

    [Fact]
    public void Admission_RefusesPastThePerTenantCap_ButAdmitsAnotherTenant()
    {
        var registry = Build(perTenant: 2);

        Assert.Equal(AnalysisAdmission.Admitted,    registry.TryAdmit(TenantA, Guid.NewGuid()));
        Assert.Equal(AnalysisAdmission.Admitted,    registry.TryAdmit(TenantA, Guid.NewGuid()));
        Assert.Equal(AnalysisAdmission.TenantLimit, registry.TryAdmit(TenantA, Guid.NewGuid()));
        Assert.Equal(AnalysisAdmission.Admitted,    registry.TryAdmit(TenantB, Guid.NewGuid()));
    }

    [Fact]
    public void Admission_RefusesPastTheTotalCapacity_AndFreesOnRelease()
    {
        var registry = Build(capacity: 2, perTenant: 5);
        var first = Guid.NewGuid();

        Assert.Equal(AnalysisAdmission.Admitted,  registry.TryAdmit(TenantA, first));
        Assert.Equal(AnalysisAdmission.Admitted,  registry.TryAdmit(TenantB, Guid.NewGuid()));
        Assert.Equal(AnalysisAdmission.QueueFull, registry.TryAdmit(TenantB, Guid.NewGuid()));

        registry.ReleaseAdmission(TenantA, first);
        Assert.Equal(AnalysisAdmission.Admitted, registry.TryAdmit(TenantB, Guid.NewGuid()));
    }

    [Fact]
    public void AdmissionRelease_KeepsASynchronousRunsTrackingOfTheSameDocument()
    {
        var registry = Build();
        var doc = Guid.NewGuid();

        registry.Track(doc);                      // a synchronous run holds it
        registry.TryAdmit(TenantA, doc);          // and a background one too
        registry.ReleaseAdmission(TenantA, doc);

        Assert.True(registry.IsTracked(doc));
    }

    // ── Run info (GET /api/analyses/runs) ───────────────────────────────────────

    [Fact]
    public void RunInfo_LivesAsLongAsTheLastReference()
    {
        var registry = Build();
        var doc  = Guid.NewGuid();
        var info = new AnalysisRunInfo(TenantA, Guid.NewGuid(), new DateTime(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc));

        registry.Track(doc, info);
        registry.Track(doc);
        registry.Release(doc);

        Assert.Equal(info, registry.GetRunInfo(doc, TenantA));

        registry.Release(doc);
        Assert.Null(registry.GetRunInfo(doc, TenantA));
    }

    [Fact]
    public void RunInfo_IsNeverHandedToAnotherTenant()
    {
        var registry = Build();
        var doc = Guid.NewGuid();

        registry.Track(doc, new AnalysisRunInfo(TenantA, Guid.NewGuid(), DateTime.UtcNow));

        Assert.NotNull(registry.GetRunInfo(doc, TenantA));
        Assert.Null(registry.GetRunInfo(doc, TenantB));
    }

    [Fact]
    public void RunInfo_IsRecordedOnlyWhenAdmitted_AndDroppedOnRelease()
    {
        var registry = Build(perTenant: 1);
        var admitted = Guid.NewGuid();
        var refused  = Guid.NewGuid();

        Assert.Equal(AnalysisAdmission.Admitted,
            registry.TryAdmit(TenantA, admitted, new AnalysisRunInfo(TenantA, Guid.NewGuid(), DateTime.UtcNow)));
        Assert.Equal(AnalysisAdmission.TenantLimit,
            registry.TryAdmit(TenantA, refused, new AnalysisRunInfo(TenantA, Guid.NewGuid(), DateTime.UtcNow)));

        Assert.NotNull(registry.GetRunInfo(admitted, TenantA));
        Assert.Null(registry.GetRunInfo(refused, TenantA));

        registry.ReleaseAdmission(TenantA, admitted);
        Assert.Null(registry.GetRunInfo(admitted, TenantA));
    }

    [Fact]
    public void Tracking_WithoutInfo_HasNoInfo()
    {
        var registry = Build();
        var doc = Guid.NewGuid();

        registry.Track(doc);

        Assert.True(registry.IsTracked(doc));
        Assert.Null(registry.GetRunInfo(doc, TenantA));
    }

    [Fact]
    public void AutoRetryState_IsTenantScoped_MovesToRunning_AndEndsWithTheLastReference()
    {
        var registry = Build();
        var doc = Guid.NewGuid();
        var due = new DateTime(2026, 11, 9, 12, 1, 0, DateTimeKind.Utc);
        Assert.Equal(AnalysisAdmission.Admitted, registry.TryAdmit(TenantA, doc));

        registry.MarkAutoRetryScheduled(doc, TenantA, due);
        Assert.Equal(new AnalysisAutoRetryInfo(TenantA, AnalysisAutoRetryState.Scheduled, due), registry.GetAutoRetry(doc, TenantA));
        Assert.Null(registry.GetAutoRetry(doc, TenantB));

        registry.MarkAutoRetryRunning(doc, TenantA);
        Assert.Equal(new AnalysisAutoRetryInfo(TenantA, AnalysisAutoRetryState.Running, due), registry.GetAutoRetry(doc, TenantA));

        registry.ReleaseAdmission(TenantA, doc);
        Assert.Null(registry.GetAutoRetry(doc, TenantA));
    }
}
