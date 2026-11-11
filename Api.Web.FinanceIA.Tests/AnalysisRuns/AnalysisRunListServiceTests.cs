// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-03

using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using static Api.Web.FinanceIA.Tests.AnalysisRuns.AnalysisRunListWorld;

namespace Api.Web.FinanceIA.Tests.AnalysisRuns;

/// <summary>
/// Covers <see cref="AnalysisRunListService"/> over the real repositories on an in-memory context:
/// the failure code and requester per failed row, canRetry against the attempts cap, consent and the
/// caller's role, the registry's run info, and that no stored reason text ever reaches the response.
/// </summary>
public sealed class AnalysisRunListServiceTests : IDisposable
{
    private const int MaxAttempts = 2;

    private readonly AnalysisRunListWorld _world = new();
    private readonly AnalysisRunRegistry  _registry =
        new(Options.Create(new AnalysisOptions { QueueCapacity = 10, MaxQueuedPerTenant = 5 }));

    public void Dispose() => _world.Dispose();

    // ── Lists and windows ────────────────────────────────────────────────────────

    [Fact]
    public async Task TheFailedWindow_FollowsDays_AndCompletedIsAlways24Hours()
    {
        var entity = _world.AddEntity(TenantA);
        var two    = _world.AddDocument(TenantA, entity, "Failed", processedAt: Now.AddDays(-2));
        _world.AddDocument(TenantA, entity, "Failed", processedAt: Now.AddDays(-4));
        var done   = _world.AddDocument(TenantA, entity, "Completed", processedAt: Now.AddHours(-23));
        var result = _world.AddAnalysis(done, Now.AddHours(-24));
        _world.AddDocument(TenantA, entity, "Completed", processedAt: Now.AddHours(-25));

        var response = await BuildSut().GetRunsAsync(TenantA, 3, true, Now, CancellationToken.None);

        Assert.Equal(3, response.WindowDays);
        Assert.Equal(Now, response.GeneratedAt);
        Assert.Equal(two.DocumentId, Assert.Single(response.Failed).DocumentId);
        var completed = Assert.Single(response.Completed);
        Assert.Equal(result, completed.ResultId);
        Assert.Equal(Now.AddHours(-23), completed.FinishedAt);
        Assert.Equal(entity.BusinessName, completed.EntityName);
    }

    [Fact]
    public async Task ACompletedDocumentWithoutAResult_IsLeftOut()
    {
        var entity = _world.AddEntity(TenantA);
        _world.AddDocument(TenantA, entity, "Completed", processedAt: Now.AddHours(-1));

        var response = await BuildSut().GetRunsAsync(TenantA, 7, true, Now, CancellationToken.None);

        Assert.Empty(response.Completed);
    }

    [Fact]
    public async Task AnotherTenantsRows_NeverAppear()
    {
        var theirs = _world.AddEntity(TenantB);
        _world.AddDocument(TenantB, theirs, "Processing");
        _world.AddDocument(TenantB, theirs, "Failed", processedAt: Now.AddHours(-1));
        var done = _world.AddDocument(TenantB, theirs, "Completed", processedAt: Now.AddHours(-1));
        _world.AddAnalysis(done, Now.AddHours(-2));

        var response = await BuildSut().GetRunsAsync(TenantA, 7, true, Now, CancellationToken.None);

        Assert.Empty(response.Running);
        Assert.Empty(response.Failed);
        Assert.Empty(response.Completed);
    }

    // ── Running: the registry's info, this tenant only ───────────────────────────

    [Fact]
    public async Task Running_ShowsTheRequesterAndStart_OnlyForARunThisInstanceHolds()
    {
        var entity  = _world.AddEntity(TenantA);
        var analyst = _world.AddUser(TenantA, "Ana Analista");
        var held    = _world.AddDocument(TenantA, entity, "Processing", uploadedAt: Now.AddDays(-3));
        var orphan  = _world.AddDocument(TenantA, entity, "Processing", uploadedAt: Now.AddDays(-2));
        _registry.Track(held.DocumentId, new AnalysisRunInfo(TenantA, analyst.UserId, Now.AddMinutes(-5)));

        var response = await BuildSut().GetRunsAsync(TenantA, 7, true, Now, CancellationToken.None);

        Assert.Equal([held.DocumentId, orphan.DocumentId], response.Running.Select(r => r.DocumentId));
        Assert.Equal(Now.AddMinutes(-5), response.Running[0].StartedAt);
        Assert.Equal("Ana Analista", response.Running[0].RequestedBy);
        Assert.Null(response.Running[1].StartedAt);
        Assert.Null(response.Running[1].RequestedBy);
    }

    [Fact]
    public async Task Running_RegistryInfoOfAnotherTenant_IsNotShown()
    {
        var entity   = _world.AddEntity(TenantA);
        var document = _world.AddDocument(TenantA, entity, "Processing");
        _registry.Track(document.DocumentId, new AnalysisRunInfo(TenantB, Guid.NewGuid(), Now));

        var row = Assert.Single((await BuildSut().GetRunsAsync(TenantA, 7, true, Now, CancellationToken.None)).Running);

        Assert.Null(row.StartedAt);
        Assert.Null(row.RequestedBy);
    }

    // ── Failed: code, requester, and the raw text never leaving ──────────────────

    [Fact]
    public async Task Failed_CodeAndRequester_ComeFromThisFailure()
    {
        var entity  = _world.AddEntity(TenantA);
        var analyst = _world.AddUser(TenantA, "Ana Analista");
        var failedAt = Now.AddHours(-1);

        var quota = _world.AddDocument(TenantA, entity, "Failed", processedAt: failedAt,
            lastError: "claude_rate_limited", lastFailedAt: failedAt.AddMilliseconds(4));
        _world.AddFailureAudit(TenantA, quota.DocumentId, failedAt.AddMilliseconds(9), analyst.UserId, null);

        var stale = _world.AddDocument(TenantA, entity, "Failed", processedAt: failedAt.AddMinutes(-1),
            lastError: "claude_api_error", lastFailedAt: Now.AddDays(-3));
        _world.AddFailureAudit(TenantA, stale.DocumentId, failedAt.AddMinutes(-1).AddMilliseconds(5), null,
            """{"reason":"stale_processing"}""");

        var response = await BuildSut().GetRunsAsync(TenantA, 7, true, Now, CancellationToken.None);

        var first = response.Failed[0];
        Assert.Equal(quota.DocumentId, first.DocumentId);
        Assert.Equal(AnalysisRunFailureCodes.Quota, first.FailureCode);
        Assert.Equal("Ana Analista", first.RequestedBy);
        Assert.Equal(failedAt, first.FailedAt);
        Assert.Null(first.StartedAt);

        var second = response.Failed[1];
        Assert.Equal(AnalysisRunFailureCodes.Interrupted, second.FailureCode);
        Assert.Null(second.RequestedBy);
    }

    [Fact]
    public async Task Failed_TheStoredReasonText_NeverReachesTheResponse()
    {
        const string sentinel = "SqlException_SENTINEL_9f2c";
        var entity = _world.AddEntity(TenantA);
        var failedAt = Now.AddHours(-1);
        var document = _world.AddDocument(TenantA, entity, "Failed", processedAt: failedAt,
            lastError: sentinel, lastFailedAt: failedAt.AddMilliseconds(1));
        _world.AddFailureAudit(TenantA, document.DocumentId, failedAt.AddMilliseconds(2), null,
            $$"""{"reason":"{{sentinel}}_AUDIT"}""");

        var response = await BuildSut().GetRunsAsync(TenantA, 7, true, Now, CancellationToken.None);
        var json     = JsonSerializer.Serialize(response);

        Assert.Equal(AnalysisRunFailureCodes.Other, Assert.Single(response.Failed).FailureCode);
        Assert.DoesNotContain("SENTINEL", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SqlException", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLog_CarriesCountsAndTheTenant_NoNamesOrReasons()
    {
        var logger = new RecordingLogger<AnalysisRunListService>();
        var entity = _world.AddEntity(TenantA, "Nombre Confidencial S.A.");
        _world.AddDocument(TenantA, entity, "Failed", processedAt: Now.AddHours(-1),
            lastError: "claude_api_error", lastFailedAt: Now.AddHours(-1), fileName: "secreto.pdf");

        await BuildSut(logger: logger).GetRunsAsync(TenantA, 7, true, Now, CancellationToken.None);

        var line = Assert.Single(logger.Messages);
        Assert.Contains(TenantA, line, StringComparison.Ordinal);
        Assert.DoesNotContain("Confidencial", line, StringComparison.Ordinal);
        Assert.DoesNotContain("secreto", line, StringComparison.Ordinal);
        Assert.DoesNotContain("claude_api_error", line, StringComparison.Ordinal);
    }

    // ── canRetry ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(MaxAttempts - 1, true)]
    [InlineData(MaxAttempts, false)]
    [InlineData(MaxAttempts + 1, false)]
    public async Task CanRetry_FollowsTheAttemptsCap_PerFile(int attempts, bool expected)
    {
        var entity = _world.AddEntity(TenantA);
        var failed = _world.AddDocument(TenantA, entity, "Failed", processedAt: Now.AddHours(-1),
            fileHash: "same-bytes", attempts: attempts > 0 ? 1 : 0);
        // The rest of the budget was spent on an earlier upload of the SAME file.
        if (attempts > 1)
            _world.AddDocument(TenantA, entity, "Failed", processedAt: Now.AddDays(-20), fileHash: "same-bytes", attempts: attempts - 1);

        var response = await BuildSut().GetRunsAsync(TenantA, 7, true, Now, CancellationToken.None);

        Assert.Equal(expected, response.Failed.Single(f => f.DocumentId == failed.DocumentId).CanRetry);
    }

    [Fact]
    public async Task CanRetry_WithNoCapConfigured_IsUnlimited()
    {
        var entity = _world.AddEntity(TenantA);
        _world.AddDocument(TenantA, entity, "Failed", processedAt: Now.AddHours(-1), attempts: 50);

        var response = await BuildSut(maxAttempts: 0).GetRunsAsync(TenantA, 7, true, Now, CancellationToken.None);

        Assert.True(Assert.Single(response.Failed).CanRetry);
    }

    [Fact]
    public async Task CanRetry_IsFalse_ForACallerWhoCannotTrigger()
    {
        var entity = _world.AddEntity(TenantA);
        _world.AddDocument(TenantA, entity, "Failed", processedAt: Now.AddHours(-1));

        var response = await BuildSut().GetRunsAsync(TenantA, 7, callerCanTrigger: false, Now, CancellationToken.None);

        Assert.False(Assert.Single(response.Failed).CanRetry);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task CanRetry_ForAPersonaHumana_FollowsActiveConsent(bool consentActive, bool expected)
    {
        var person = _world.AddPersonEntity(TenantA, consentActive);
        _world.AddDocument(TenantA, person, "Failed", processedAt: Now.AddHours(-1));

        var response = await BuildSut().GetRunsAsync(TenantA, 7, true, Now, CancellationToken.None);

        Assert.Equal(expected, Assert.Single(response.Failed).CanRetry);
    }

    [Fact]
    public async Task CanRetry_AnUnreadableBudget_FailsOpen_LikeTheTrigger()
    {
        var entity = _world.AddEntity(TenantA);
        _world.AddDocument(TenantA, entity, "Failed", processedAt: Now.AddHours(-1), attempts: MaxAttempts);
        var repository = new ThrowingSumsRepository(new AnalysisRunListRepository(_world.Db));

        var response = await BuildSut(repository: repository).GetRunsAsync(TenantA, 7, true, Now, CancellationToken.None);

        Assert.True(Assert.Single(response.Failed).CanRetry);
    }

    [Fact]
    public void CanTrigger_IsTheAnalysisCreatorRoles()
    {
        Assert.True(AnalysisRunListService.CanTrigger(r => r == Roles.Analyst));
        Assert.True(AnalysisRunListService.CanTrigger(r => r == Roles.RiskManager));
        Assert.True(AnalysisRunListService.CanTrigger(r => r == Roles.Admin));
        Assert.False(AnalysisRunListService.CanTrigger(r => r == Roles.Auditor));
        Assert.False(AnalysisRunListService.CanTrigger(r => r == Roles.SuperAdmin));
    }

    // ── failureReason / causeGroup / attemptsRemaining ───────────────────────────

    [Theory]
    [InlineData("claude_no_financial_statements", "no_financial_statements", "document", "model_error")]
    [InlineData("claude_refusal",                 "unreadable_document",     "document", "model_error")]
    [InlineData("claude_unreadable_answer",       "unreadable_document",     "document", "model_error")]
    [InlineData("claude_api_error",               "file_rejected",           "document", "model_error")]
    [InlineData("claude_timeout",                 "timeout",                 "ours",     "model_error")]
    [InlineData("claude_server_error",            "service_unavailable",     "ours",     "model_error")]
    [InlineData("claude_network_error",           "service_unavailable",     "ours",     "model_error")]
    [InlineData("claude_rate_limited",            "service_unavailable",     "ours",     "quota")]
    [InlineData("claude_auth_error",              "service_unavailable",     "ours",     "model_error")]
    [InlineData("claude_unparseable_answer",      "reading_failed",          "ours",     "model_error")]
    [InlineData("claude_max_tokens",              "reading_failed",          "ours",     "model_error")]
    [InlineData("NullReferenceException",         "other",                   "ours",     "other")]
    public async Task Failed_TheDocumentCode_GivesReasonAndGroup_FailureCodeUnchanged(
        string stored, string expectedReason, string expectedGroup, string expectedFailureCode)
    {
        var entity   = _world.AddEntity(TenantA);
        var failedAt = Now.AddHours(-1);
        _world.AddDocument(TenantA, entity, "Failed", processedAt: failedAt,
            lastError: stored, lastFailedAt: failedAt.AddMilliseconds(3));

        var item = Assert.Single(
            (await BuildSut().GetRunsAsync(TenantA, 7, true, Now, CancellationToken.None)).Failed);

        Assert.Equal(expectedReason, item.FailureReason);
        Assert.Equal(expectedGroup, item.CauseGroup);
        Assert.Equal(expectedFailureCode, item.FailureCode);
        Assert.True(item.CanRetry);
    }

    [Theory]
    [InlineData("""{"reason":"stale_processing"}""", "interrupted", "interrupted")]
    [InlineData("""{"reason":"consent_required"}""", "consent_required", "consent_required")]
    [InlineData("""{"reason":"document_deleted"}""", "other", "other")]
    public async Task Failed_TheAuditReason_GivesReasonAndGroup(string auditJson, string expectedReason, string expectedFailureCode)
    {
        var entity   = _world.AddEntity(TenantA);
        var failedAt = Now.AddHours(-1);
        var document = _world.AddDocument(TenantA, entity, "Failed", processedAt: failedAt);
        _world.AddFailureAudit(TenantA, document.DocumentId, failedAt.AddMilliseconds(5), null, auditJson);

        var item = Assert.Single(
            (await BuildSut().GetRunsAsync(TenantA, 7, true, Now, CancellationToken.None)).Failed);

        Assert.Equal(expectedReason, item.FailureReason);
        Assert.Equal(AnalysisRunFailureReasons.Ours, item.CauseGroup);
        Assert.Equal(expectedFailureCode, item.FailureCode);
    }

    [Fact]
    public async Task Failed_NoTraceAtAll_IsOtherAndOurs()
    {
        var entity = _world.AddEntity(TenantA);
        _world.AddDocument(TenantA, entity, "Failed", processedAt: Now.AddHours(-1));

        var item = Assert.Single(
            (await BuildSut().GetRunsAsync(TenantA, 7, true, Now, CancellationToken.None)).Failed);

        Assert.Equal(AnalysisRunFailureReasons.Other, item.FailureReason);
        Assert.Equal(AnalysisRunFailureReasons.Ours, item.CauseGroup);
        Assert.Equal(AnalysisRunFailureCodes.Other, item.FailureCode);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 1)]
    [InlineData(2, 0)]
    public async Task AttemptsRemaining_IsTheFilesBudgetLeft(int spent, int expected)
    {
        var entity = _world.AddEntity(TenantA);
        _world.AddDocument(TenantA, entity, "Failed", processedAt: Now.AddHours(-1), fileHash: "bytes", attempts: spent);

        var item = Assert.Single(
            (await BuildSut().GetRunsAsync(TenantA, 7, true, Now, CancellationToken.None)).Failed);

        Assert.Equal(expected, item.AttemptsRemaining);
        Assert.Equal(spent < MaxAttempts, item.CanRetry);
    }

    [Fact]
    public async Task AttemptsRemaining_TwoDocumentsSharingAHash_BothReportTheSummedRemainder()
    {
        var entity = _world.AddEntity(TenantA);
        var first  = _world.AddDocument(TenantA, entity, "Failed", processedAt: Now.AddHours(-2), fileHash: "same", attempts: 1);
        var second = _world.AddDocument(TenantA, entity, "Failed", processedAt: Now.AddHours(-1), fileHash: "same", attempts: 1);
        var other  = _world.AddDocument(TenantA, entity, "Failed", processedAt: Now.AddHours(-3), fileHash: "other", attempts: 0);
        // Another tenant's upload of the same bytes never counts.
        _world.AddDocument(TenantB, _world.AddEntity(TenantB), "Failed", processedAt: Now.AddHours(-1), fileHash: "same", attempts: 5);

        var response = await BuildSut(maxAttempts: 3).GetRunsAsync(TenantA, 7, true, Now, CancellationToken.None);

        Assert.Equal(1, response.Failed.Single(f => f.DocumentId == first.DocumentId).AttemptsRemaining);
        Assert.Equal(1, response.Failed.Single(f => f.DocumentId == second.DocumentId).AttemptsRemaining);
        Assert.Equal(3, response.Failed.Single(f => f.DocumentId == other.DocumentId).AttemptsRemaining);
    }

    [Fact]
    public async Task AttemptsRemaining_WithTheBudgetOff_IsNull_AndNothingIsRead()
    {
        var entity = _world.AddEntity(TenantA);
        _world.AddDocument(TenantA, entity, "Failed", processedAt: Now.AddHours(-1), attempts: 7);
        var repository = new CountingRepository(new AnalysisRunListRepository(_world.Db));

        var item = Assert.Single(
            (await BuildSut(maxAttempts: 0, repository: repository).GetRunsAsync(TenantA, 7, true, Now, CancellationToken.None)).Failed);

        Assert.Null(item.AttemptsRemaining);
        Assert.True(item.CanRetry);
        Assert.Equal(0, repository.SumCalls);
    }

    [Fact]
    public async Task AttemptsRemaining_ABlankHash_IsNull()
    {
        var entity = _world.AddEntity(TenantA);
        _world.AddDocument(TenantA, entity, "Failed", processedAt: Now.AddHours(-1), fileHash: "", attempts: 1);

        var item = Assert.Single(
            (await BuildSut().GetRunsAsync(TenantA, 7, true, Now, CancellationToken.None)).Failed);

        Assert.Null(item.AttemptsRemaining);
    }

    [Fact]
    public async Task AttemptsRemaining_AnUnreadableBudget_IsNull_WhileCanRetryFailsOpen()
    {
        var entity = _world.AddEntity(TenantA);
        _world.AddDocument(TenantA, entity, "Failed", processedAt: Now.AddHours(-1), attempts: MaxAttempts);
        var repository = new ThrowingSumsRepository(new AnalysisRunListRepository(_world.Db));

        var item = Assert.Single(
            (await BuildSut(repository: repository).GetRunsAsync(TenantA, 7, true, Now, CancellationToken.None)).Failed);

        Assert.Null(item.AttemptsRemaining);
        Assert.True(item.CanRetry);
    }

    [Fact]
    public async Task AttemptsRemaining_IsReported_ToACallerWhoCannotTrigger()
    {
        var entity = _world.AddEntity(TenantA);
        _world.AddDocument(TenantA, entity, "Failed", processedAt: Now.AddHours(-1), attempts: 1);

        var item = Assert.Single(
            (await BuildSut().GetRunsAsync(TenantA, 7, callerCanTrigger: false, Now, CancellationToken.None)).Failed);

        Assert.Equal(1, item.AttemptsRemaining);
        Assert.False(item.CanRetry);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AttemptsRemaining_ManyFailedRows_AreReadInOneQuery(bool callerCanTrigger)
    {
        var entity = _world.AddEntity(TenantA);
        for (var i = 0; i < 6; i++)
            _world.AddDocument(TenantA, entity, "Failed", processedAt: Now.AddHours(-1 - i), fileHash: $"hash-{i % 3}", attempts: 1);
        var repository = new CountingRepository(new AnalysisRunListRepository(_world.Db));

        var response = await BuildSut(repository: repository)
            .GetRunsAsync(TenantA, 7, callerCanTrigger, Now, CancellationToken.None);

        Assert.Equal(6, response.Failed.Count);
        Assert.All(response.Failed, f => Assert.Equal(0, f.AttemptsRemaining)); // two uploads of each hash
        Assert.Equal(1, repository.SumCalls);
    }

    [Fact]
    public async Task Failed_TheNewFields_NeverCarryTheStoredText()
    {
        var entity   = _world.AddEntity(TenantA);
        var failedAt = Now.AddHours(-1);
        _world.AddDocument(TenantA, entity, "Failed", processedAt: failedAt,
            lastError: "claude_no_financial_statements", lastFailedAt: failedAt.AddMilliseconds(1));

        var json = JsonSerializer.Serialize(await BuildSut().GetRunsAsync(TenantA, 7, true, Now, CancellationToken.None));

        Assert.DoesNotContain("claude_", json, StringComparison.Ordinal);
        Assert.Contains("\"FailureReason\":\"no_financial_statements\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_CarriesTheDocumentsFiscalYear_ForOneClickRetry()
    {
        var entity   = _world.AddEntity(TenantA);
        var failedAt = Now.AddHours(-1);
        var withYear = _world.AddDocument(TenantA, entity, "Failed", processedAt: failedAt, fiscalYear: 2024,
            fileHash: "hash-with-year");
        var noYear   = _world.AddDocument(TenantA, entity, "Failed", processedAt: failedAt.AddMinutes(-1),
            fileHash: "hash-no-year");

        var response = await BuildSut().GetRunsAsync(TenantA, 7, true, Now, CancellationToken.None);
        var byId     = response.Failed.ToDictionary(f => f.DocumentId);

        Assert.Equal(2024, byId[withYear.DocumentId].FiscalYear);
        Assert.Null(byId[noYear.DocumentId].FiscalYear);
        Assert.True(byId[withYear.DocumentId].CanRetry);   // canRetry semantics unchanged

        // The wire name is camelCase, as ASP.NET Core's default (web) serializer writes it.
        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"fiscalYear\":2024", json, StringComparison.Ordinal);
        Assert.Contains("\"fiscalYear\":null", json, StringComparison.Ordinal);
    }

    private AnalysisRunListService BuildSut(
        int maxAttempts = MaxAttempts,
        IAnalysisRunListRepository? repository = null,
        ILogger<AnalysisRunListService>? logger = null)
        => new(
            repository ?? new AnalysisRunListRepository(_world.Db),
            new LegajoDocumentRepository(_world.Db, NullLogger<LegajoDocumentRepository>.Instance),
            _registry,
            Options.Create(new BillingOptions { MaxExtractionAttemptsPerDocument = maxAttempts }),
            logger ?? NullLogger<AnalysisRunListService>.Instance);

    /// <summary>The real repository, except that the per-file attempt read always fails.</summary>
    private sealed class ThrowingSumsRepository(IAnalysisRunListRepository inner) : IAnalysisRunListRepository
    {
        public Task<IReadOnlyList<AnalysisRunDocumentProjection>> GetRunningAsync(string tenantId, int take, CancellationToken ct)
            => inner.GetRunningAsync(tenantId, take, ct);

        public Task<IReadOnlyList<AnalysisRunDocumentProjection>> GetFailedAsync(string tenantId, DateTime sinceUtc, int take, CancellationToken ct)
            => inner.GetFailedAsync(tenantId, sinceUtc, take, ct);

        public Task<IReadOnlyList<AnalysisRunDocumentProjection>> GetCompletedAsync(string tenantId, DateTime sinceUtc, int take, CancellationToken ct)
            => inner.GetCompletedAsync(tenantId, sinceUtc, take, ct);

        public Task<IReadOnlyDictionary<string, int>> SumExtractionAttemptsByHashesAsync(
            IReadOnlyCollection<string> fileHashes, string tenantId, CancellationToken ct)
            => throw new InvalidOperationException("database unreachable");

        public Task<IReadOnlyList<AnalysisRunFailureAuditProjection>> GetFailureAuditsAsync(
            IReadOnlyCollection<Guid> documentIds, string tenantId, DateTime sinceUtc, CancellationToken ct)
            => inner.GetFailureAuditsAsync(documentIds, tenantId, sinceUtc, ct);

        public Task<IReadOnlyDictionary<Guid, string>> GetUserDisplayNamesAsync(
            IReadOnlyCollection<Guid> userIds, string tenantId, CancellationToken ct)
            => inner.GetUserDisplayNamesAsync(userIds, tenantId, ct);
    }

    /// <summary>The real repository, counting the per-file attempt reads.</summary>
    private sealed class CountingRepository(IAnalysisRunListRepository inner) : IAnalysisRunListRepository
    {
        public int SumCalls { get; private set; }

        public Task<IReadOnlyList<AnalysisRunDocumentProjection>> GetRunningAsync(string tenantId, int take, CancellationToken ct)
            => inner.GetRunningAsync(tenantId, take, ct);

        public Task<IReadOnlyList<AnalysisRunDocumentProjection>> GetFailedAsync(string tenantId, DateTime sinceUtc, int take, CancellationToken ct)
            => inner.GetFailedAsync(tenantId, sinceUtc, take, ct);

        public Task<IReadOnlyList<AnalysisRunDocumentProjection>> GetCompletedAsync(string tenantId, DateTime sinceUtc, int take, CancellationToken ct)
            => inner.GetCompletedAsync(tenantId, sinceUtc, take, ct);

        public Task<IReadOnlyDictionary<string, int>> SumExtractionAttemptsByHashesAsync(
            IReadOnlyCollection<string> fileHashes, string tenantId, CancellationToken ct)
        {
            SumCalls++;
            return inner.SumExtractionAttemptsByHashesAsync(fileHashes, tenantId, ct);
        }

        public Task<IReadOnlyList<AnalysisRunFailureAuditProjection>> GetFailureAuditsAsync(
            IReadOnlyCollection<Guid> documentIds, string tenantId, DateTime sinceUtc, CancellationToken ct)
            => inner.GetFailureAuditsAsync(documentIds, tenantId, sinceUtc, ct);

        public Task<IReadOnlyDictionary<Guid, string>> GetUserDisplayNamesAsync(
            IReadOnlyCollection<Guid> userIds, string tenantId, CancellationToken ct)
            => inner.GetUserDisplayNamesAsync(userIds, tenantId, ct);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    // ── Automatic retry ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ARunningItem_ReportsItsScheduledThenRunningRetry_FromTheRegistry()
    {
        var entity   = _world.AddEntity(TenantA);
        var waiting  = _world.AddDocument(TenantA, entity, "Processing");
        var plain    = _world.AddDocument(TenantA, entity, "Processing");
        var due      = Now.AddSeconds(60);
        Assert.Equal(AnalysisAdmission.Admitted, _registry.TryAdmit(TenantA, waiting.DocumentId));
        _registry.MarkAutoRetryScheduled(waiting.DocumentId, TenantA, due);

        var response = await BuildSut().GetRunsAsync(TenantA, 7, true, Now, CancellationToken.None);

        var item = Assert.Single(response.Running, r => r.DocumentId == waiting.DocumentId);
        Assert.Equal(new AnalysisRunAutoRetry { State = "scheduled", ScheduledAt = due }, item.AutoRetry);
        Assert.Null(Assert.Single(response.Running, r => r.DocumentId == plain.DocumentId).AutoRetry);

        _registry.MarkAutoRetryRunning(waiting.DocumentId, TenantA);
        response = await BuildSut().GetRunsAsync(TenantA, 7, true, Now, CancellationToken.None);

        item = Assert.Single(response.Running, r => r.DocumentId == waiting.DocumentId);
        Assert.Equal(new AnalysisRunAutoRetry { State = "running", ScheduledAt = due }, item.AutoRetry);

        // Another tenant asking never sees it.
        var theirs = await BuildSut().GetRunsAsync(TenantB, 7, true, Now, CancellationToken.None);
        Assert.Empty(theirs.Running);
    }

    [Fact]
    public async Task AFailedRetry_IsExhausted_AndEveryFailureFieldIsReportedAsWithoutIt()
    {
        var entity    = _world.AddEntity(TenantA);
        var failedAt  = Now.AddHours(-1);
        var retried   = _world.AddDocument(TenantA, entity, "Failed", processedAt: failedAt, fileHash: "hash-retried",
            lastError: "claude_server_error", lastFailedAt: failedAt, fiscalYear: 2024);
        var plain     = _world.AddDocument(TenantA, entity, "Failed", processedAt: failedAt, fileHash: "hash-plain",
            lastError: "claude_server_error", lastFailedAt: failedAt, fiscalYear: 2024);
        var requester = _world.AddUser(TenantA, "Ana Analista");
        foreach (var doc in new[] { retried, plain })
            _world.AddFailureAudit(TenantA, doc.DocumentId, failedAt, requester.UserId, null);
        _world.AddFailureAudit(TenantA, retried.DocumentId, failedAt.AddMilliseconds(40), requester.UserId,
            """{"outcome":"failed","reason":"claude_server_error"}""", action: "Analysis.AutoRetryFinished");

        var response = await BuildSut().GetRunsAsync(TenantA, 7, true, Now, CancellationToken.None);

        var exhausted = Assert.Single(response.Failed, f => f.DocumentId == retried.DocumentId);
        var without   = Assert.Single(response.Failed, f => f.DocumentId == plain.DocumentId);
        Assert.Equal(new AnalysisRunAutoRetry { State = "exhausted", ScheduledAt = null }, exhausted.AutoRetry);
        Assert.Null(without.AutoRetry);

        // The retry changes nothing else: same code, reason, cause, retry flag, budget, year and requester.
        Assert.Equal(without with { DocumentId = exhausted.DocumentId, AutoRetry = exhausted.AutoRetry }, exhausted);
        Assert.Equal("model_error", exhausted.FailureCode);
        Assert.Equal("service_unavailable", exhausted.FailureReason);
        Assert.Equal("Ana Analista", exhausted.RequestedBy);
        Assert.True(exhausted.CanRetry);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("abandoned")]
    public async Task ARetryThatDidNotFail_IsNotExhausted(string outcome)
    {
        var entity   = _world.AddEntity(TenantA);
        var failedAt = Now.AddHours(-1);
        var doc      = _world.AddDocument(TenantA, entity, "Failed", processedAt: failedAt);
        _world.AddFailureAudit(TenantA, doc.DocumentId, failedAt, null, """{"reason":"consent_required"}""");
        _world.AddFailureAudit(TenantA, doc.DocumentId, failedAt.AddMilliseconds(10), null,
            $$"""{"outcome":"{{outcome}}","reason":null}""", action: "Analysis.AutoRetryFinished");

        var response = await BuildSut().GetRunsAsync(TenantA, 7, true, Now, CancellationToken.None);

        var item = Assert.Single(response.Failed);
        Assert.Null(item.AutoRetry);
        Assert.Equal("consent_required", item.FailureCode);
    }

    [Fact]
    public async Task AnOlderExhaustedRetry_DoesNotMarkALaterFailure()
    {
        // The retry failed yesterday; the analyst retried by hand today and that run failed on its own.
        var entity   = _world.AddEntity(TenantA);
        var failedAt = Now.AddHours(-1);
        var doc      = _world.AddDocument(TenantA, entity, "Failed", processedAt: failedAt,
            lastError: "claude_timeout", lastFailedAt: failedAt);
        _world.AddFailureAudit(TenantA, doc.DocumentId, Now.AddDays(-1), null,
            """{"outcome":"failed","reason":"claude_server_error"}""", action: "Analysis.AutoRetryFinished");
        _world.AddFailureAudit(TenantA, doc.DocumentId, failedAt, null, null);

        var response = await BuildSut().GetRunsAsync(TenantA, 7, true, Now, CancellationToken.None);

        var item = Assert.Single(response.Failed);
        Assert.Null(item.AutoRetry);
        Assert.Equal("timeout", item.FailureReason);
    }

    [Fact]
    public void TheRetryOutcomeRow_NeverSuppliesTheFailureReasonOrTheRequester()
    {
        var failedAt = Now.AddHours(-1);
        var document = new AnalysisRunDocumentProjection
        {
            DocumentId = Guid.NewGuid(), EntityId = Guid.NewGuid(), FileName = "b.pdf", ProcessedAt = failedAt,
        };
        var requester = Guid.NewGuid();
        var audits = new List<AnalysisRunFailureAuditProjection>
        {
            new() { DocumentId = document.DocumentId, Action = "Analysis.AutoRetryFinished", Timestamp = failedAt.AddSeconds(1),
                    UserId = Guid.NewGuid(), NewValueJson = """{"outcome":"failed","reason":"stale_processing"}""" },
            new() { DocumentId = document.DocumentId, Action = "Analysis.Failed", Timestamp = failedAt,
                    UserId = requester, NewValueJson = """{"reason":"consent_required"}""" },
        };

        var (code, requestedBy) = AnalysisRunListService.ResolveFailure(document, audits);

        Assert.Equal("consent_required", code);
        Assert.Equal(requester, requestedBy);
    }
}
