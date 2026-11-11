// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-03

using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Xunit;
using static Api.Web.FinanceIA.Tests.AnalysisRuns.AnalysisRunListWorld;

namespace Api.Web.FinanceIA.Tests.AnalysisRuns;

/// <summary>
/// Covers <see cref="AnalysisRunListRepository"/> on an in-memory context: the tenant scope, each list's
/// status filter and window, the balance-only filter (legajo, deleted and non-balance documents never
/// appear), ordering and the cap, and the per-file attempt sums.
/// </summary>
public sealed class AnalysisRunListRepositoryTests : IDisposable
{
    private readonly AnalysisRunListWorld _world = new();
    private readonly AnalysisRunListRepository _sut;

    public AnalysisRunListRepositoryTests()
    {
        _sut = new AnalysisRunListRepository(_world.Db);
    }

    public void Dispose() => _world.Dispose();

    // ── Tenant isolation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryList_IsScopedToTheTenant()
    {
        var mine   = _world.AddEntity(TenantA);
        var theirs = _world.AddEntity(TenantB);

        var running   = _world.AddDocument(TenantA, mine, "Processing");
        var failed    = _world.AddDocument(TenantA, mine, "Failed", processedAt: Now.AddHours(-1));
        var completed = _world.AddDocument(TenantA, mine, "Completed", processedAt: Now.AddHours(-1));
        _world.AddAnalysis(completed, Now.AddHours(-2));

        _world.AddDocument(TenantB, theirs, "Processing");
        _world.AddDocument(TenantB, theirs, "Failed", processedAt: Now.AddHours(-1));
        var theirCompleted = _world.AddDocument(TenantB, theirs, "Completed", processedAt: Now.AddHours(-1));
        _world.AddAnalysis(theirCompleted, Now.AddHours(-2));

        Assert.Equal(running.DocumentId, Assert.Single(await _sut.GetRunningAsync(TenantA, 50, CancellationToken.None)).DocumentId);
        Assert.Equal(failed.DocumentId, Assert.Single(await _sut.GetFailedAsync(TenantA, Now.AddDays(-7), 50, CancellationToken.None)).DocumentId);
        Assert.Equal(completed.DocumentId, Assert.Single(await _sut.GetCompletedAsync(TenantA, Now.AddDays(-1), 50, CancellationToken.None)).DocumentId);
    }

    [Fact]
    public async Task EntityName_IsNeverReadFromAnotherTenantsEntity()
    {
        // A document whose EntityId happens to match another tenant's entity must not borrow its name.
        var theirs = _world.AddEntity(TenantB, "Ajena S.A.");
        _world.AddDocument(TenantA, theirs, "Processing");

        var row = Assert.Single(await _sut.GetRunningAsync(TenantA, 50, CancellationToken.None));

        Assert.Null(row.EntityName);
    }

    [Fact]
    public async Task FailureAudits_AndUserNames_AreScopedToTheTenant()
    {
        var documentId = Guid.NewGuid();
        _world.AddFailureAudit(TenantB, documentId, Now.AddHours(-1), Guid.NewGuid(), """{"reason":"stale_processing"}""");
        var stranger = _world.AddUser(TenantB, "Usuario Ajeno");

        Assert.Empty(await _sut.GetFailureAuditsAsync([documentId], TenantA, Now.AddDays(-7), CancellationToken.None));
        Assert.Empty(await _sut.GetUserDisplayNamesAsync([stranger.UserId], TenantA, CancellationToken.None));
    }

    // ── Filters and windows ──────────────────────────────────────────────────────

    [Fact]
    public async Task Running_IsProcessingOnly()
    {
        var entity = _world.AddEntity(TenantA);
        var processing = _world.AddDocument(TenantA, entity, "Processing");
        _world.AddDocument(TenantA, entity, "Pending");
        _world.AddDocument(TenantA, entity, "Failed", processedAt: Now.AddHours(-1));
        _world.AddDocument(TenantA, entity, "Completed", processedAt: Now.AddHours(-1));

        var row = Assert.Single(await _sut.GetRunningAsync(TenantA, 50, CancellationToken.None));

        Assert.Equal(processing.DocumentId, row.DocumentId);
        Assert.Equal(entity.BusinessName, row.EntityName);
        Assert.Equal("balance-2024.pdf", row.FileName);
    }

    [Fact]
    public async Task Failed_KeepsOnlyFailuresInsideTheWindow_NewestFirst()
    {
        var entity = _world.AddEntity(TenantA);
        var older  = _world.AddDocument(TenantA, entity, "Failed", processedAt: Now.AddDays(-6));
        var newer  = _world.AddDocument(TenantA, entity, "Failed", processedAt: Now.AddHours(-1));
        _world.AddDocument(TenantA, entity, "Failed", processedAt: Now.AddDays(-8));   // outside 7 days
        _world.AddDocument(TenantA, entity, "Failed", processedAt: null);              // no failure time
        _world.AddDocument(TenantA, entity, "Processing");

        var rows = await _sut.GetFailedAsync(TenantA, Now.AddDays(-7), 50, CancellationToken.None);

        Assert.Equal([newer.DocumentId, older.DocumentId], rows.Select(r => r.DocumentId));
    }

    [Fact]
    public async Task Completed_KeepsOnlyTheLast24Hours_WithTheLatestBalanceResult()
    {
        var entity = _world.AddEntity(TenantA);
        var recent = _world.AddDocument(TenantA, entity, "Completed", processedAt: Now.AddHours(-3));
        _world.AddAnalysis(recent, Now.AddDays(-5));                     // rejected, kept as history
        var latest = _world.AddAnalysis(recent, Now.AddHours(-4));       // the re-analysis

        var old = _world.AddDocument(TenantA, entity, "Completed", processedAt: Now.AddHours(-30));
        _world.AddAnalysis(old, Now.AddHours(-31));

        var row = Assert.Single(await _sut.GetCompletedAsync(TenantA, Now.AddHours(-24), 50, CancellationToken.None));

        Assert.Equal(recent.DocumentId, row.DocumentId);
        Assert.Equal(latest, row.ResultId);
        Assert.Equal(Now.AddHours(-3), row.ProcessedAt);
    }

    [Fact]
    public async Task Completed_WithoutAnAnalysis_HasNoResultId()
    {
        var entity = _world.AddEntity(TenantA);
        _world.AddDocument(TenantA, entity, "Completed", processedAt: Now.AddHours(-1));

        var row = Assert.Single(await _sut.GetCompletedAsync(TenantA, Now.AddHours(-24), 50, CancellationToken.None));

        Assert.Null(row.ResultId);
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("legajo-document")]
    [InlineData("legajo-status-only")]
    [InlineData("not-a-balance")]
    public async Task LegajoDeletedAndNonBalanceDocuments_AreInNoList(string kind)
    {
        var entity = _world.AddEntity(TenantA);
        foreach (var (status, processedAt) in new[] { ("Processing", (DateTime?)null), ("Failed", Now.AddHours(-1)), ("Completed", Now.AddHours(-1)) })
        {
            var document = kind switch
            {
                "deleted"            => _world.AddDocument(TenantA, entity, status, processedAt, isDeleted: true),
                "legajo-document"    => _world.AddDocument(TenantA, entity, status, processedAt,
                                            personLegajoId: Guid.NewGuid(), legajoStatus: LegajoStatuses.Failed),
                "legajo-status-only" => _world.AddDocument(TenantA, entity, status, processedAt, legajoStatus: LegajoStatuses.Queued),
                _                    => _world.AddDocument(TenantA, entity, status, processedAt, documentType: DocumentTypes.Otro)
            };
            _world.AddAnalysis(document, Now.AddHours(-2));
        }

        Assert.Empty(await _sut.GetRunningAsync(TenantA, 50, CancellationToken.None));
        Assert.Empty(await _sut.GetFailedAsync(TenantA, Now.AddDays(-7), 50, CancellationToken.None));
        Assert.Empty(await _sut.GetCompletedAsync(TenantA, Now.AddDays(-1), 50, CancellationToken.None));
    }

    [Fact]
    public async Task EachList_IsCapped()
    {
        var entity = _world.AddEntity(TenantA);
        for (var i = 0; i < 4; i++)
            _world.AddDocument(TenantA, entity, "Failed", processedAt: Now.AddMinutes(-i));

        var rows = await _sut.GetFailedAsync(TenantA, Now.AddDays(-7), 3, CancellationToken.None);

        Assert.Equal(3, rows.Count);
        Assert.Equal(Now, rows[0].ProcessedAt);
    }

    // ── Attempts, audits, names ──────────────────────────────────────────────────

    [Fact]
    public async Task AttemptSums_ArePerFile_IgnoringDeletedRowsAndOtherTenants()
    {
        var mine   = _world.AddEntity(TenantA);
        var theirs = _world.AddEntity(TenantB);
        _world.AddDocument(TenantA, mine, "Failed", fileHash: "abc", attempts: 1);
        _world.AddDocument(TenantA, mine, "Failed", fileHash: "abc", attempts: 1);         // re-upload, same bytes
        _world.AddDocument(TenantA, mine, "Failed", fileHash: "abc", attempts: 5, isDeleted: true);
        _world.AddDocument(TenantB, theirs, "Failed", fileHash: "abc", attempts: 7);
        _world.AddDocument(TenantA, mine, "Failed", fileHash: "def", attempts: 1);

        var sums = await _sut.SumExtractionAttemptsByHashesAsync(["abc", "def", "", "zzz"], TenantA, CancellationToken.None);

        Assert.Equal(2, sums["abc"]);
        Assert.Equal(1, sums["def"]);
        Assert.False(sums.ContainsKey("zzz"));
        Assert.False(sums.ContainsKey(""));
    }

    [Fact]
    public async Task FailureAudits_AreTheDocumentsAnalysisFailedRowsInTheWindow()
    {
        var documentId = Guid.NewGuid();
        var userId     = Guid.NewGuid();
        _world.AddFailureAudit(TenantA, documentId, Now.AddHours(-1), userId, """{"reason":"consent_required"}""");
        _world.AddFailureAudit(TenantA, documentId, Now.AddDays(-9), null, """{"reason":"stale_processing"}""");     // too old
        _world.AddFailureAudit(TenantA, documentId, Now.AddHours(-2), null, null, action: "Analysis.Completed");
        _world.AddFailureAudit(TenantA, documentId, Now.AddHours(-2), null, null, entityType: "Analysis");
        _world.AddFailureAudit(TenantA, Guid.NewGuid(), Now.AddHours(-1), null, null);                            // other document

        var row = Assert.Single(await _sut.GetFailureAuditsAsync([documentId], TenantA, Now.AddDays(-7), CancellationToken.None));

        Assert.Equal(documentId, row.DocumentId);
        Assert.Equal(userId, row.UserId);
        Assert.Equal("""{"reason":"consent_required"}""", row.NewValueJson);
    }

    [Fact]
    public async Task UserDisplayNames_ResolveTheTenantsUsers()
    {
        var user = _world.AddUser(TenantA, "Ana Analista");

        var names = await _sut.GetUserDisplayNamesAsync([user.UserId, Guid.NewGuid()], TenantA, CancellationToken.None);

        Assert.Equal("Ana Analista", Assert.Single(names).Value);
    }

    [Fact]
    public async Task Failed_ProjectsTheFiscalYear()
    {
        var entity = _world.AddEntity(TenantA);
        _world.AddDocument(TenantA, entity, "Failed", processedAt: Now.AddHours(-1), fiscalYear: 2023);

        var row = Assert.Single(await _sut.GetFailedAsync(TenantA, Now.AddDays(-7), 50, CancellationToken.None));

        Assert.Equal(2023, row.FiscalYear);
    }

    [Fact]
    public async Task FailureAudits_AlsoCarryTheAutoRetryOutcomeRows_EachWithItsAction()
    {
        var documentId = Guid.NewGuid();
        _world.AddFailureAudit(TenantA, documentId, Now.AddHours(-1), null, """{"outcome":"failed","reason":"claude_server_error"}""",
            action: "Analysis.AutoRetryFinished");
        _world.AddFailureAudit(TenantA, documentId, Now.AddHours(-2), null, """{"reason":"claude_server_error","delaySeconds":60}""",
            action: "Analysis.AutoRetryScheduled");
        _world.AddFailureAudit(TenantA, documentId, Now.AddHours(-3), null, null);

        var rows = await _sut.GetFailureAuditsAsync([documentId], TenantA, Now.AddDays(-7), CancellationToken.None);

        Assert.Equal(new[] { "Analysis.AutoRetryFinished", "Analysis.Failed" }, rows.Select(r => r.Action));
    }
}
