// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-08

using System.Security.Claims;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// PUT /api/bff/settings/scoring-config on the REAL <see cref="SettingsService"/> over an in-memory database:
/// pins the 400 a refused score threshold produces. The shape is the one every scoring-config validation
/// failure has always had — a ProblemDetails with title "Bad Request" and the validator's sentence as
/// <c>detail</c>, and NO <c>errorCode</c> or field list — so the Settings UI needs no change to show it.
/// </summary>
public sealed class SettingsControllerScoringConfigTests
{
    private const string Tenant = "tenant-settings-controller";

    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public async Task Put_WhenTheHighScoreThresholdExceeds100_Returns400ProblemDetailsNamingTheKey()
    {
        var result = await Controller().UpdateScoringConfig(
            Request(("score_threshold_high", 100.01m)), CancellationToken.None);

        var problem = BadRequestProblem(result);
        Assert.Equal(
            "Configuration value for 'score_threshold_high' must be between 0 and 100 (current: 100.01).",
            problem.Detail);
    }

    [Fact]
    public async Task Put_WhenScoreThresholdsAreOutOfOrder_KeepsTheSame400Shape()
    {
        var result = await Controller().UpdateScoringConfig(
            Request(("score_threshold_medium", 30.00m)), CancellationToken.None);

        var problem = BadRequestProblem(result);
        Assert.Equal(
            "Score thresholds must be ordered: low (35) < medium (30.00) < high (80).",
            problem.Detail);
    }

    [Fact]
    public async Task Put_WhenTheHighScoreThresholdIs100_IsAccepted()
    {
        var result = await Controller().UpdateScoringConfig(
            Request(("score_threshold_high", 100.00m)), CancellationToken.None);

        var dto = Assert.IsType<ScoringConfigDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(100.00m, dto.ScoreThresholds.Single(i => i.Key == "score_threshold_high").Value);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static ProblemDetails BadRequestProblem(IActionResult result)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);

        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(400, problem.Status);
        Assert.Equal("Bad Request", problem.Title);
        Assert.False(problem.Extensions.ContainsKey("errorCode"));
        Assert.False(problem.Extensions.ContainsKey("errors"));
        return problem;
    }

    // UpdateScoringConfig reaches only the settings service, the tenant accessor (via ExtractTenantId) and
    // the logger. The other five dependencies are never touched by this action, so they are left null:
    // an accidental call would fail loudly rather than pass quietly.
    private static SettingsController Controller()
    {
        var options = new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"settings-controller-{Guid.NewGuid()}")
            .Options;

        var service = new SettingsService(
            new FinanciaDbContext(options),
            new StubAuditService(),
            new ThrowingTenantProfileService(),
            NullLogger<SettingsService>.Instance);

        return new SettingsController(
            service,
            invitationService: null!,
            new TenantContextAccessor(),
            userRepository: null!,
            approvalLimitRepository: null!,
            teamRepository: null!,
            sgrSettingsService: null!,
            NullLogger<SettingsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim("userId", UserId.ToString()), new Claim("tenantId", Tenant)], "Internal"))
                }
            }
        };
    }

    private static UpdateScoringConfigRequest Request(params (string Key, decimal Value)[] items)
        => new()
        {
            Items = items
                .Select(i => new ScoringConfigUpdateItem { Key = i.Key, Value = i.Value })
                .ToList()
        };

    private sealed class ThrowingTenantProfileService : ITenantProfileService
    {
        public Task<TenantProfileResolution> GetEffectiveAsync(string tenantId, CancellationToken ct)
            => throw new NotSupportedException("Scoring config must not read the tenant profile.");

        public Task<TenantProfileResolution> SetEntityTypeAsync(string tenantId, string entityType, CancellationToken ct)
            => throw new NotSupportedException("Scoring config must not write the tenant profile.");
    }

    private sealed class StubAuditService : IAuditService
    {
        public Task LogAsync(
            string tenantId, Guid? userId, string action, string severity = "Info",
            string? entityType = null, string? entityId = null, string? ipAddress = null,
            string? requestId = null, string? oldValueJson = null, string? newValueJson = null,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task LogSessionAsync(
            Guid tenantId, Guid? userId, string email, string eventType,
            string? ipAddress, string? userAgent, string? failureReason = null,
            CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
