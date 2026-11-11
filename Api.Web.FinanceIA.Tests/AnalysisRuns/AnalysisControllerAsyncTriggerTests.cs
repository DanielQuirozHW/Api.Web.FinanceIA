// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-19

using System.Net;
using System.Security.Claims;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using static Api.Web.FinanceIA.Tests.AnalysisRuns.AnalysisRunTestData;

namespace Api.Web.FinanceIA.Tests.AnalysisRuns;

/// <summary>
/// Covers the <c>Prefer: respond-async</c> branch of <see cref="AnalysisController.TriggerAsync"/>:
/// 202 + Location + a queued run, admission refusals before any write, the compare-and-set claim,
/// and that a request cancelled after its 202 does not cancel the run. The request WITHOUT the header
/// is covered, unchanged, by the existing TriggerAsync suites; one test here pins that it still runs
/// inline.
/// </summary>
public sealed class AnalysisControllerAsyncTriggerTests
{
    private const string FirebaseUid = "uid-async";

    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private sealed class Rig
    {
        public required AnalysisController Sut { get; init; }
        public required PipelineHarness Harness { get; init; }
        public required AnalysisJobQueue Queue { get; init; }
        public required AnalysisRunRegistry Registry { get; init; }
        public required User User { get; init; }
        public required IOptions<AnalysisOptions> Options { get; init; }

        public HttpResponse Response => Sut.HttpContext.Response;

        public async Task<AnalysisRun?> TryDequeueAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            try
            {
                await foreach (var run in Queue.ReadAllAsync(cts.Token))
                    return run;
            }
            catch (OperationCanceledException)
            {
            }
            return null;
        }
    }

    private static Rig BuildRig(string? prefer = "respond-async", int capacity = 100, int perTenant = 5)
    {
        var harness = new PipelineHarness();
        var user    = new User { UserId = Guid.NewGuid(), DisplayName = "Analista" };
        var options = Options.Create(new AnalysisOptions { QueueCapacity = capacity, MaxQueuedPerTenant = perTenant });
        var queue    = new AnalysisJobQueue(options);
        var registry = new AnalysisRunRegistry(options);

        var sut = new AnalysisController(
            new StubTenantContextAccessor
            {
                TenantContext = new TenantContext
                {
                    TenantId               = TenantA,
                    FirebaseProjectId      = "proj",
                    FirebaseCredentialJson = "{}",
                    RecaptchaSecretKey     = "secret"
                }
            },                                          // tenantContextAccessor
            harness.Documents,                          // documentRepository
            harness.Results,                            // analysisResultRepository
            harness.Analyses,                           // analysisRepository
            harness.Claude,                             // claudeService
            null!,                                      // cashFlowReconciliationService
            null!,                                      // cashFlowNarrativeService
            null!,                                      // alertRatioReconciliationService
            null!,                                      // entityRepository
            new FixedUserRepository(user),              // userRepository
            null!,                                      // chatRepository
            null!,                                      // creditLineRepository
            null!,                                      // operationRepository
            harness.Audit,                              // auditService
            new NoopApiCallLog(),                       // apiCallLogService
            null!,                                      // excelCashFlowParser
            Options.Create(new BillingOptions()),        // billingOptions
            null!,                                      // tenantBillingConfigService
            null!,                                      // userApprovalLimitRepository
            null!,                                      // decisionNotifier
            null!,                                      // approvalQuorumService
            harness.Legajos,                            // legajoDocumentRepository
            harness.Build(),                            // analysisPipeline — real, the same one the worker runs
            queue,                                      // analysisJobQueue
            registry,                                   // analysisRunRegistry
            harness.RunState,                           // analysisRunStateRepository
            new Api.Web.FinanceIA.Tests.PersonAnalysis.StubPersonAnalysisRepository(),
            NullLogger<AnalysisController>.Instance);

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("uid", FirebaseUid)], "Test")),
            TraceIdentifier = "trace-202"
        };
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse("10.1.2.3");
        if (prefer is not null)
            httpContext.Request.Headers["Prefer"] = prefer;
        sut.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return new Rig { Sut = sut, Harness = harness, Queue = queue, Registry = registry, User = user, Options = options };
    }

    private static Document AddPending(Rig rig)
    {
        var doc = Document(TenantA, status: "Pending");
        rig.Harness.Store.Add(doc);
        return doc;
    }

    private static TriggerAnalysisRequest Request(Document doc) => new()
    {
        DocumentId      = doc.DocumentId,
        EntityId        = doc.EntityId,
        FiscalYear      = 2024,
        RequestedAmount = 1_000_000m
    };

    private static int? StatusCodeOf(IActionResult result) => result switch
    {
        ObjectResult o     => o.StatusCode,
        StatusCodeResult s => s.StatusCode,
        _                  => null
    };

    [Fact]
    public async Task WithRespondAsync_Returns202_WithLocation_AndQueuesTheRun()
    {
        var rig = BuildRig();
        var doc = AddPending(rig);

        var result = await rig.Sut.TriggerAsync(Request(doc), CancellationToken.None);

        var accepted = Assert.IsType<AcceptedAtActionResult>(result);
        Assert.Equal(202, accepted.StatusCode);
        Assert.Equal(nameof(DocumentsController.GetById), accepted.ActionName);
        Assert.Equal("Documents", accepted.ControllerName);
        Assert.Equal(doc.DocumentId, accepted.RouteValues!["id"]);
        var body = Assert.IsType<AnalysisAcceptedResponse>(accepted.Value);
        Assert.Equal((doc.DocumentId, doc.EntityId, "Processing"), (body.DocumentId, body.EntityId, body.Status));
        Assert.Equal("respond-async", rig.Response.Headers["Preference-Applied"].ToString());

        // Claimed, queued, admitted — and nothing spent yet.
        Assert.Equal("Processing", rig.Harness.Store.StatusOf(doc.DocumentId));
        Assert.Equal(0, rig.Harness.Claude.AnalyzeCalls);
        Assert.Empty(rig.Harness.Storage.Reads);
        Assert.Equal(1, rig.Registry.AdmittedCount);
        Assert.True(rig.Registry.IsTracked(doc.DocumentId));

        var run = Assert.IsType<AnalysisRun>(await rig.TryDequeueAsync());
        Assert.True(run.IsBackground);
        Assert.Equal(TenantA, run.TenantId);
        Assert.Equal(rig.User.UserId, run.RequestedBy);
        Assert.Equal((doc.DocumentId, doc.EntityId, 2024, 1_000_000m), (run.DocumentId, run.EntityId, run.FiscalYear, run.RequestedAmount!.Value));
        Assert.Equal(("10.1.2.3", "trace-202"), (run.IpAddress, run.RequestId));
        Assert.Equal(doc.StoragePath, run.StoragePath);
    }

    [Fact]
    public async Task TheAcceptedRun_RecordsItsRequesterAndClaimTime_ForTheRunsList()
    {
        var rig    = BuildRig();
        var doc    = AddPending(rig);
        var before = DateTime.UtcNow;

        await rig.Sut.TriggerAsync(Request(doc), CancellationToken.None);

        var info = rig.Registry.GetRunInfo(doc.DocumentId, TenantA);
        Assert.NotNull(info);
        Assert.Equal(rig.User.UserId, info.RequestedBy);
        Assert.InRange(info.StartedAtUtc, before, DateTime.UtcNow);
        Assert.Null(rig.Registry.GetRunInfo(doc.DocumentId, TenantB));
    }

    [Fact]
    public async Task TheClaim_ComparesTheStatusAndTimestampTheChecksValidated()
    {
        var rig = BuildRig();
        var doc = Document(TenantA, status: "Failed");
        doc.ProcessedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        rig.Harness.Store.Add(doc);

        var result = await rig.Sut.TriggerAsync(Request(doc), CancellationToken.None);

        Assert.IsType<AcceptedAtActionResult>(result);
        var claim = Assert.Single(rig.Harness.Store.Calls, c => c.Op == "TryBegin");
        Assert.Equal(TenantA, claim.TenantId);
        Assert.Null(rig.Harness.Store.Rows[doc.DocumentId].ProcessedAt);
    }

    [Fact]
    public async Task LosingTheClaim_Returns409_AndQueuesNothing()
    {
        var rig = BuildRig();
        var doc = AddPending(rig);
        // A concurrent trigger moves the row between the checks and the claim.
        rig.Harness.RunState.BeforeConditionalWrite = d => d.ProcessingStatus = "Processing";

        var result = await rig.Sut.TriggerAsync(Request(doc), CancellationToken.None);

        Assert.Equal(409, StatusCodeOf(result));
        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(result).Value);
        Assert.Equal("El documento ya se encuentra en el estado 'Processing'.", problem.Detail);
        Assert.Null(await rig.TryDequeueAsync());
        Assert.Equal(0, rig.Registry.AdmittedCount);
        Assert.False(rig.Registry.IsTracked(doc.DocumentId));
        Assert.False(rig.Response.Headers.ContainsKey("Preference-Applied"));
    }

    [Fact]
    public async Task QueueFull_Returns503WithRetryAfter_BeforeAnyWrite()
    {
        var rig = BuildRig(capacity: 1);
        Assert.Equal(AnalysisAdmission.Admitted, rig.Registry.TryAdmit(TenantB, Guid.NewGuid()));
        var doc = AddPending(rig);

        var result = await rig.Sut.TriggerAsync(Request(doc), CancellationToken.None);

        Assert.Equal(503, StatusCodeOf(result));
        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(result).Value);
        Assert.Equal("analysis_queue_full", problem.Extensions["errorCode"]);
        Assert.Equal("30", rig.Response.Headers.RetryAfter.ToString());
        Assert.Equal("Pending", rig.Harness.Store.StatusOf(doc.DocumentId));
        Assert.DoesNotContain(rig.Harness.Store.Calls, c => c.Op == "TryBegin");
    }

    [Fact]
    public async Task TenantCap_Returns429WithRetryAfter_BeforeAnyWrite()
    {
        var rig = BuildRig(perTenant: 1);
        Assert.Equal(AnalysisAdmission.Admitted, rig.Registry.TryAdmit(TenantA, Guid.NewGuid()));
        var doc = AddPending(rig);

        var result = await rig.Sut.TriggerAsync(Request(doc), CancellationToken.None);

        Assert.Equal(429, StatusCodeOf(result));
        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(result).Value);
        Assert.Equal("analysis_queue_tenant_limit", problem.Extensions["errorCode"]);
        Assert.Equal("30", rig.Response.Headers.RetryAfter.ToString());
        Assert.Equal("Pending", rig.Harness.Store.StatusOf(doc.DocumentId));
    }

    [Fact]
    public async Task AFastPathRefusal_IsUnchangedByTheHeader_AndAdmitsNothing()
    {
        var rig = BuildRig();
        var doc = Document(TenantA, status: "Processing");
        rig.Harness.Store.Add(doc);

        var result = await rig.Sut.TriggerAsync(Request(doc), CancellationToken.None);

        Assert.Equal(409, StatusCodeOf(result));
        Assert.Equal(0, rig.Registry.AdmittedCount);
        Assert.DoesNotContain(rig.Harness.Store.Calls, c => c.Op == "TryBegin");
    }

    [Fact]
    public async Task AMissingDocument_Is404_WithTheHeaderToo()
    {
        var rig = BuildRig();
        var doc = Document(TenantB, status: "Pending");   // another tenant's: invisible here
        rig.Harness.Store.Add(doc);

        var result = await rig.Sut.TriggerAsync(Request(doc), CancellationToken.None);

        Assert.Equal(404, StatusCodeOf(result));
        Assert.Equal(0, rig.Registry.AdmittedCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("return=minimal")]
    public async Task WithoutRespondAsync_TheAnalysisStillRunsInline_And201s(string? prefer)
    {
        var rig = BuildRig(prefer);
        var doc = AddPending(rig);

        var result = await rig.Sut.TriggerAsync(Request(doc), CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(result);
        Assert.Equal("Completed", rig.Harness.Store.StatusOf(doc.DocumentId));
        Assert.Equal(1, rig.Harness.Claude.AnalyzeCalls);
        Assert.Null(await rig.TryDequeueAsync());
        Assert.False(rig.Response.Headers.ContainsKey("Preference-Applied"));
        Assert.False(rig.Registry.IsTracked(doc.DocumentId));   // released once the inline run ended
    }

    [Fact]
    public async Task CancellingTheRequestAfterThe202_DoesNotCancelTheRun()
    {
        var rig = BuildRig();
        var doc = AddPending(rig);
        using var requestAborted = new CancellationTokenSource();

        var result = await rig.Sut.TriggerAsync(Request(doc), requestAborted.Token);
        Assert.IsType<AcceptedAtActionResult>(result);
        requestAborted.Cancel();   // the client goes away

        var services = new ServiceCollection();
        services.AddScoped<IAnalysisPipeline>(_ => rig.Harness.Build());
        var worker = new AnalysisJobWorker(
            rig.Queue, rig.Registry,
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            rig.Options, NullLogger<AnalysisJobWorker>.Instance);

        var run = Assert.IsType<AnalysisRun>(await rig.TryDequeueAsync());
        await worker.ProcessOneAsync(run, CancellationToken.None);

        Assert.Equal("Completed", rig.Harness.Store.StatusOf(doc.DocumentId));
        var completed = Assert.Single(rig.Harness.Notifier.Completed);
        Assert.Equal(rig.User.UserId, completed.UserId);
        Assert.Single(rig.Harness.Analyses.Created);
        Assert.Equal(0, rig.Registry.AdmittedCount);
        Assert.False(rig.Registry.IsTracked(doc.DocumentId));
    }
}
