// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-16

using System.Security.Claims;
using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Covers <see cref="AlertRuleCatalogueController"/>: the listing (disabled rules must be visible —
/// enabling them is the screen's whole purpose), and the PATCH guards.
///
/// The guards exist because each failure is SILENT in production rather than loud: a ratio sent as
/// 55 instead of 0,55 makes a rule unfireable, enabling an unimplemented code produces a rule that
/// reads as active and never fires, and a bad severity would surface as a raw SQL check-constraint
/// violation. Every rejection therefore also asserts that UpdateAsync was never called.
///
/// Hand-rolled private sealed stubs (no mock library), matching the CreditLines controller tests.
/// </summary>
public sealed class AlertRuleCatalogueControllerTests
{
    private const string Tenant      = "tenant-1";
    private const string FirebaseUid = "uid-1";

    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // ── Stubs ─────────────────────────────────────────────────────────────────

    private sealed class StubTenantContextAccessor : ITenantContextAccessor
    {
        public TenantContext? TenantContext { get; set; }
    }

    private sealed class StubCatalogueRepository : IAlertRuleCatalogueRepository
    {
        public List<AlertRuleDefinition> AllToReturn { get; set; } = new();
        public AlertRuleDefinition?      RuleToReturn { get; set; }

        public bool     UpdateCalled         { get; private set; }
        public bool?    CapturedEnabled      { get; private set; }
        public string?  CapturedSeverity     { get; private set; }
        public decimal? CapturedThreshold    { get; private set; }
        public Guid?    CapturedUpdatedBy    { get; private set; }

        public Task<IReadOnlyList<AlertRuleDefinition>> GetAllAsync(string tenantId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AlertRuleDefinition>>(AllToReturn);

        public Task<AlertRuleDefinition?> GetByIdAsync(Guid ruleId, string tenantId, CancellationToken ct)
            => Task.FromResult(RuleToReturn);

        public Task<AlertRuleDefinition?> UpdateAsync(
            Guid ruleId, string tenantId, bool enabled, string severity, decimal? thresholdValue,
            Guid updatedBy, CancellationToken ct)
        {
            UpdateCalled      = true;
            CapturedEnabled   = enabled;
            CapturedSeverity  = severity;
            CapturedThreshold = thresholdValue;
            CapturedUpdatedBy = updatedBy;

            // Mirrors the real repository: the persisted snapshot reflects the written columns and
            // leaves every other column as it was.
            var persisted = Clone(RuleToReturn!);
            persisted.Enabled        = enabled;
            persisted.Severity       = severity;
            persisted.ThresholdValue = thresholdValue;
            persisted.UpdatedBy      = updatedBy;
            return Task.FromResult<AlertRuleDefinition?>(persisted);
        }

        public Task<IReadOnlyList<AlertRuleDefinition>> GetEnabledAsync(string tenantId, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubUserRepository : IUserRepository
    {
        public User? UserToReturn { get; set; }

        /// <summary>Users the batch lookup can resolve. Ids absent from here come back unresolved.</summary>
        public List<User> KnownUsers { get; set; } = new();

        /// <summary>How many times the batch lookup ran — one per request, never one per row.</summary>
        public int GetByIdsCallCount { get; private set; }

        /// <summary>Ids the batch lookup was asked for, across every call.</summary>
        public List<Guid> CapturedIds { get; } = new();

        public Task<User?> GetByFirebaseUidAsync(string firebaseUid, CancellationToken ct)
            => Task.FromResult(UserToReturn);

        public Task<IReadOnlyList<User>> GetByIdsAsync(IEnumerable<Guid> userIds, CancellationToken ct)
        {
            var ids = userIds.ToList();
            GetByIdsCallCount++;
            CapturedIds.AddRange(ids);

            return Task.FromResult<IReadOnlyList<User>>(
                KnownUsers.Where(u => ids.Contains(u.UserId)).ToList());
        }

        // Deliberately still throwing: resolving names row by row through the single-id lookup is
        // exactly the mistake this stub is here to catch. A listing must batch.
        public Task<User?> GetByUserIdAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<User?> GetByEmailAsync(string email, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> ExistsByFirebaseUidAsync(string firebaseUid, CancellationToken ct) => throw new NotImplementedException();
        public Task<User> CreateAsync(User user, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateLastLoginAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateOnboardingFlagsAsync(Guid userId, string? flagsJson, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<User>> GetActiveByTenantAsync(string tenantId, CancellationToken ct) => throw new NotImplementedException();
        // Team assignment (DDL 063) — unused by this suite.
        public Task UpdateTeamAsync(Guid userId, Guid? teamId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> ClearTeamAsync(Guid teamId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class StubAuditService : IAuditService
    {
        public bool    LogCalled    { get; private set; }
        public string? Action       { get; private set; }
        public string? EntityType   { get; private set; }
        public string? EntityId     { get; private set; }
        public Guid?   UserId       { get; private set; }
        public string? OldValueJson { get; private set; }
        public string? NewValueJson { get; private set; }

        public Task LogAsync(
            string tenantId, Guid? userId, string action, string severity = "Info",
            string? entityType = null, string? entityId = null, string? ipAddress = null,
            string? requestId = null, string? oldValueJson = null, string? newValueJson = null,
            CancellationToken ct = default)
        {
            LogCalled    = true;
            Action       = action;
            EntityType   = entityType;
            EntityId     = entityId;
            UserId       = userId;
            OldValueJson = oldValueJson;
            NewValueJson = newValueJson;
            return Task.CompletedTask;
        }

        public Task LogSessionAsync(
            Guid tenantId, Guid? userId, string email, string eventType,
            string? ipAddress, string? userAgent, string? failureReason = null,
            CancellationToken ct = default)
            => Task.CompletedTask;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static TenantContext BuildTenant() => new()
    {
        TenantId               = Tenant,
        FirebaseProjectId      = "proj",
        FirebaseCredentialJson = "{}",
        RecaptchaSecretKey     = "secret"
    };

    private static AlertRuleDefinition BuildRule(
        string   code           = AlertRuleEngine.InventarioElevado,
        bool     enabled        = false,
        string   severity       = "medium",
        decimal? thresholdValue = 0.5000m,
        Guid?    updatedBy      = null) => new()
    {
        RuleId                = Guid.NewGuid(),
        TenantId              = Tenant,
        Code                  = code,
        Enabled               = enabled,
        Severity              = severity,
        ThresholdValue        = thresholdValue,
        // Null by default: that is a seeded row, never touched through the API.
        UpdatedBy             = updatedBy,
        DisplayNameEs         = "Participación elevada de bienes de cambio",
        DescriptionTemplateEs = "Los bienes de cambio representan el {valor} % ...",
        CreatedAt             = new DateTime(2026, 8, 16, 0, 0, 0, DateTimeKind.Utc),
        UpdatedAt             = new DateTime(2026, 8, 16, 0, 0, 0, DateTimeKind.Utc),
    };

    private static AlertRuleDefinition Clone(AlertRuleDefinition r) => new()
    {
        RuleId                = r.RuleId,
        TenantId              = r.TenantId,
        Code                  = r.Code,
        Enabled               = r.Enabled,
        Severity              = r.Severity,
        ThresholdValue        = r.ThresholdValue,
        DisplayNameEs         = r.DisplayNameEs,
        DescriptionTemplateEs = r.DescriptionTemplateEs,
        CreatedAt             = r.CreatedAt,
        UpdatedAt             = r.UpdatedAt,
        UpdatedBy             = r.UpdatedBy,
    };

    /// <summary>Builds a request by deserializing real JSON, so Patch semantics are exercised end to end.</summary>
    private static UpdateAlertRuleRequest Request(string json) =>
        JsonSerializer.Deserialize<UpdateAlertRuleRequest>(
            json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static AlertRuleCatalogueController BuildSut(
        StubCatalogueRepository catalogue,
        StubAuditService        audit,
        StubUserRepository?     users = null)
    {
        var sut = new AlertRuleCatalogueController(
            new StubTenantContextAccessor { TenantContext = BuildTenant() },
            catalogue,
            users ?? new StubUserRepository
            {
                UserToReturn = new User { UserId = UserId, FirebaseUid = FirebaseUid }
            },
            audit,
            NullLogger<AlertRuleCatalogueController>.Instance);

        sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim("uid", FirebaseUid) }, "Test"))
            }
        };
        return sut;
    }

    private static ProblemDetails ProblemOf(IActionResult result)
    {
        var objectResult = Assert.IsType<ObjectResult>(result);
        return Assert.IsType<ProblemDetails>(objectResult.Value);
    }

    // ── GET ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE POINT OF THE LISTING: disabled rules must come back. Every rule ships disabled, so a
    /// read that filtered them out would return an empty screen on a fresh tenant.
    /// </summary>
    [Fact]
    public async Task GetRules_ReturnsDisabledRulesToo()
    {
        var catalogue = new StubCatalogueRepository
        {
            AllToReturn =
            [
                BuildRule(AlertRuleEngine.PasivoCienCorriente, enabled: true,  thresholdValue: null),
                BuildRule(AlertRuleEngine.InventarioElevado,   enabled: false, thresholdValue: 0.5m),
            ]
        };
        var sut = BuildSut(catalogue, new StubAuditService());

        var result = await sut.GetRulesAsync(CancellationToken.None);

        var ok       = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<AlertRulesResponseDto>(ok.Value);
        Assert.Equal(2, response.TotalCount);
        Assert.Equal(1, response.EnabledCount);
        Assert.Contains(response.Rules, r => !r.Enabled);
    }

    /// <summary>
    /// A rule the engine does not implement is flagged, so the screen can show it as inert instead
    /// of offering a toggle that would be rejected.
    /// </summary>
    [Fact]
    public async Task GetRules_FlagsUnimplementedCodes()
    {
        var catalogue = new StubCatalogueRepository
        {
            AllToReturn =
            [
                BuildRule(AlertRuleEngine.InventarioElevado),
                BuildRule("REGLA_FANTASMA", thresholdValue: null),
            ]
        };
        var sut = BuildSut(catalogue, new StubAuditService());

        var result = await sut.GetRulesAsync(CancellationToken.None);

        var ok       = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<AlertRulesResponseDto>(ok.Value);
        Assert.True(response.Rules.Single(r => r.Code == AlertRuleEngine.InventarioElevado).Implemented);
        Assert.False(response.Rules.Single(r => r.Code == "REGLA_FANTASMA").Implemented);
    }

    /// <summary>
    /// The threshold unit reaches the screen, so it can label the input and warn that 55 rather
    /// than 0,55 makes the rule unfireable — instead of rediscovering that by trial and error
    /// against a 400. Null for a rule that takes no threshold is the same signal the PATCH guard
    /// uses, so the screen can disable the input rather than offer one the API will reject.
    /// </summary>
    [Fact]
    public async Task GetRules_CarriesThresholdUnit_NullForThresholdlessRules()
    {
        var catalogue = new StubCatalogueRepository
        {
            AllToReturn = AlertRuleEngine.ImplementedCodes
                .Select(code => BuildRule(code, thresholdValue: null))
                .ToList()
        };
        var sut = BuildSut(catalogue, new StubAuditService());

        var result = await sut.GetRulesAsync(CancellationToken.None);

        var ok       = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<AlertRulesResponseDto>(ok.Value);

        // Driven off ThresholdUnits rather than a named code, so the assertion stays honest as
        // rules gain or lose a threshold instead of needing an edit per rule.
        Assert.All(
            response.Rules.Where(r => AlertRuleEngine.ThresholdUnits.ContainsKey(r.Code)),
            r => Assert.Equal(RuleThresholdUnitWire.Proportion, r.ThresholdUnit));

        Assert.All(
            response.Rules.Where(r => !AlertRuleEngine.ThresholdUnits.ContainsKey(r.Code)),
            r => Assert.Null(r.ThresholdUnit));

        // Both halves must be non-empty, or the two Assert.All calls above pass vacuously.
        Assert.Contains(response.Rules, r => r.ThresholdUnit is not null);
        Assert.Contains(response.Rules, r => r.ThresholdUnit is null);
    }

    /// <summary>
    /// Pins the wire value itself, not just its presence: the screen keys behaviour off this
    /// string, so a rename of the enum member must not silently change it.
    /// </summary>
    [Fact]
    public async Task GetRules_ThresholdUnit_SerializesAsStableLowercaseString()
    {
        var catalogue = new StubCatalogueRepository
        {
            AllToReturn = [BuildRule(AlertRuleEngine.InventarioElevado)]
        };
        var sut = BuildSut(catalogue, new StubAuditService());

        var result   = await sut.GetRulesAsync(CancellationToken.None);
        var ok       = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<AlertRulesResponseDto>(ok.Value);

        var json = JsonSerializer.Serialize(
            response, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"thresholdUnit\":\"proportion\"", json);
    }

    // ── GET: UpdatedByName ────────────────────────────────────────────────────

    /// <summary>
    /// THE POINT OF THE FIELD: "última modificación: 3f2a…-9c1b" tells the analyst nothing. A
    /// threshold change alters which alerts fire and therefore what the SGR decides on, so the
    /// editor has to be legible.
    /// </summary>
    [Fact]
    public async Task GetRules_ResolvesUpdatedByName_WhenUpdatedByResolves()
    {
        var editorId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var catalogue = new StubCatalogueRepository
        {
            AllToReturn = [BuildRule(updatedBy: editorId)]
        };
        var users = new StubUserRepository
        {
            UserToReturn = new User { UserId = UserId, FirebaseUid = FirebaseUid },
            KnownUsers   = [new User { UserId = editorId, DisplayName = "Ana Gómez" }]
        };
        var sut = BuildSut(catalogue, new StubAuditService(), users);

        var result   = await sut.GetRulesAsync(CancellationToken.None);
        var ok       = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<AlertRulesResponseDto>(ok.Value);

        var rule = Assert.Single(response.Rules);
        Assert.Equal(editorId, rule.UpdatedBy);
        Assert.Equal("Ana Gómez", rule.UpdatedByName);
    }

    /// <summary>
    /// A seeded row has never been touched through the API. Null, not a placeholder: inventing a
    /// name for a change that nobody made would be worse than showing nothing.
    /// </summary>
    [Fact]
    public async Task GetRules_UpdatedByName_IsNull_ForSeededRow()
    {
        var catalogue = new StubCatalogueRepository { AllToReturn = [BuildRule()] };
        var users     = new StubUserRepository
        {
            UserToReturn = new User { UserId = UserId, FirebaseUid = FirebaseUid }
        };
        var sut = BuildSut(catalogue, new StubAuditService(), users);

        var result   = await sut.GetRulesAsync(CancellationToken.None);
        var ok       = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<AlertRulesResponseDto>(ok.Value);

        var rule = Assert.Single(response.Rules);
        Assert.Null(rule.UpdatedBy);
        Assert.Null(rule.UpdatedByName);
    }

    /// <summary>
    /// The editor may have left the organisation since the change. The id stays on the wire —
    /// the audit trail is intact — but the name resolves to null rather than failing the read.
    /// </summary>
    [Fact]
    public async Task GetRules_UpdatedByName_IsNull_WhenUserDoesNotResolve()
    {
        var vanishedId = Guid.Parse("33333333-3333-3333-3333-333333333333");

        var catalogue = new StubCatalogueRepository
        {
            AllToReturn = [BuildRule(updatedBy: vanishedId)]
        };
        var users = new StubUserRepository
        {
            UserToReturn = new User { UserId = UserId, FirebaseUid = FirebaseUid }
            // KnownUsers deliberately empty: the id no longer resolves to anyone.
        };
        var sut = BuildSut(catalogue, new StubAuditService(), users);

        var result   = await sut.GetRulesAsync(CancellationToken.None);
        var ok       = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<AlertRulesResponseDto>(ok.Value);

        var rule = Assert.Single(response.Rules);
        Assert.Equal(vanishedId, rule.UpdatedBy);
        Assert.Null(rule.UpdatedByName);
    }

    /// <summary>
    /// One lookup for the whole catalogue, not one per row. The distinct ids are asked for once —
    /// repeated editors collapse and the null of a seeded row is never asked about at all.
    ///
    /// The stub's GetByUserIdAsync throws, so a rewrite that resolved names row by row through the
    /// single-id lookup would fail here rather than quietly issuing N queries per request.
    /// </summary>
    [Fact]
    public async Task GetRules_ResolvesEditors_InOneCall_NotPerRow()
    {
        var anaId   = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var brunoId = Guid.Parse("44444444-4444-4444-4444-444444444444");

        var catalogue = new StubCatalogueRepository
        {
            AllToReturn =
            [
                BuildRule(AlertRuleEngine.InventarioElevado,   updatedBy: anaId),
                BuildRule(AlertRuleEngine.FlujoNoConcilia,     updatedBy: brunoId),
                // Same editor as the first row: must not be asked for twice.
                BuildRule(AlertRuleEngine.PasivoCienCorriente, updatedBy: anaId),
                // Seeded: contributes no id at all.
                BuildRule(AlertRuleEngine.FlujoFinanciacionCero),
            ]
        };
        var users = new StubUserRepository
        {
            UserToReturn = new User { UserId = UserId, FirebaseUid = FirebaseUid },
            KnownUsers =
            [
                new User { UserId = anaId,   DisplayName = "Ana Gómez" },
                new User { UserId = brunoId, DisplayName = "Bruno Paz" },
            ]
        };
        var sut = BuildSut(catalogue, new StubAuditService(), users);

        var result   = await sut.GetRulesAsync(CancellationToken.None);
        var ok       = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<AlertRulesResponseDto>(ok.Value);

        Assert.Equal(1, users.GetByIdsCallCount);
        Assert.Equal(new[] { anaId, brunoId }, users.CapturedIds);

        Assert.Equal(4, response.Rules.Count);
        Assert.Equal(
            "Ana Gómez",
            response.Rules.Single(r => r.Code == AlertRuleEngine.InventarioElevado).UpdatedByName);
        Assert.Equal(
            "Bruno Paz",
            response.Rules.Single(r => r.Code == AlertRuleEngine.FlujoNoConcilia).UpdatedByName);
        Assert.Equal(
            "Ana Gómez",
            response.Rules.Single(r => r.Code == AlertRuleEngine.PasivoCienCorriente).UpdatedByName);
        Assert.Null(
            response.Rules.Single(r => r.Code == AlertRuleEngine.FlujoFinanciacionCero).UpdatedByName);
    }

    /// <summary>
    /// Pins the camelCase wire name, mirroring the thresholdUnit test: the configuration screen
    /// binds to this key.
    /// </summary>
    [Fact]
    public async Task GetRules_UpdatedByName_SerializesAsCamelCase()
    {
        var editorId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var catalogue = new StubCatalogueRepository
        {
            AllToReturn = [BuildRule(updatedBy: editorId)]
        };
        var users = new StubUserRepository
        {
            UserToReturn = new User { UserId = UserId, FirebaseUid = FirebaseUid },
            KnownUsers   = [new User { UserId = editorId, DisplayName = "Ana Gómez" }]
        };
        var sut = BuildSut(catalogue, new StubAuditService(), users);

        var result   = await sut.GetRulesAsync(CancellationToken.None);
        var ok       = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<AlertRulesResponseDto>(ok.Value);

        var json = JsonSerializer.Serialize(
            response, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"updatedByName\":\"Ana G\\u00F3mez\"", json);
    }

    // ── PATCH: tenant isolation ───────────────────────────────────────────────

    /// <summary>
    /// A rule id belonging to another tenant is a 404 and must never reach the write. The stub
    /// returns null from GetByIdAsync exactly as the real repository does when its tenant
    /// predicate does not match.
    /// </summary>
    [Fact]
    public async Task Patch_WhenRuleBelongsToAnotherTenant_Returns404AndNeverWrites()
    {
        var catalogue = new StubCatalogueRepository { RuleToReturn = null };
        var audit     = new StubAuditService();
        var sut       = BuildSut(catalogue, audit);

        var result = await sut.UpdateRuleAsync(
            Guid.NewGuid(), Request("""{"enabled": true}"""), CancellationToken.None);

        Assert.Equal(404, ProblemOf(result).Status);
        Assert.False(catalogue.UpdateCalled);
        Assert.False(audit.LogCalled);
    }

    // ── PATCH: partial-update semantics ───────────────────────────────────────

    /// <summary>An omitted field is left at its stored value, not defaulted.</summary>
    [Fact]
    public async Task Patch_OmittedFields_AreLeftUnchanged()
    {
        var rule      = BuildRule(enabled: true, severity: "medium", thresholdValue: 0.50m);
        var catalogue = new StubCatalogueRepository { RuleToReturn = rule };
        var sut       = BuildSut(catalogue, new StubAuditService());

        var result = await sut.UpdateRuleAsync(
            rule.RuleId, Request("""{"thresholdValue": 0.55}"""), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(0.55m,    catalogue.CapturedThreshold);
        Assert.True(catalogue.CapturedEnabled);              // unchanged
        Assert.Equal("medium", catalogue.CapturedSeverity);  // unchanged
    }

    /// <summary>
    /// An explicit null clears the threshold — the case that motivated Patch&lt;T&gt;. It must not
    /// be confused with the omitted case asserted above.
    /// </summary>
    [Fact]
    public async Task Patch_ExplicitNullThreshold_ClearsIt()
    {
        var rule      = BuildRule(thresholdValue: 0.50m);
        var catalogue = new StubCatalogueRepository { RuleToReturn = rule };
        var sut       = BuildSut(catalogue, new StubAuditService());

        var result = await sut.UpdateRuleAsync(
            rule.RuleId, Request("""{"thresholdValue": null}"""), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.True(catalogue.UpdateCalled);
        Assert.Null(catalogue.CapturedThreshold);
    }

    [Fact]
    public async Task Patch_WithNoFieldsAtAll_Returns400()
    {
        var catalogue = new StubCatalogueRepository { RuleToReturn = BuildRule() };
        var sut       = BuildSut(catalogue, new StubAuditService());

        var result = await sut.UpdateRuleAsync(Guid.NewGuid(), Request("{}"), CancellationToken.None);

        Assert.Equal(400, ProblemOf(result).Status);
        Assert.False(catalogue.UpdateCalled);
    }

    // ── PATCH: severity validation ────────────────────────────────────────────

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("critical")]
    public async Task Patch_AcceptsEveryConstraintSeverity(string severity)
    {
        var rule      = BuildRule();
        var catalogue = new StubCatalogueRepository { RuleToReturn = rule };
        var sut       = BuildSut(catalogue, new StubAuditService());

        var result = await sut.UpdateRuleAsync(
            rule.RuleId, Request($$"""{"severity": "{{severity}}"}"""), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(severity, catalogue.CapturedSeverity);
    }

    /// <summary>
    /// Uppercase is rejected: CK_AlertRuleCatalogue_Severity pins the lowercase vocabulary, so
    /// "HIGH" would otherwise surface as a raw SQL constraint violation.
    /// </summary>
    [Theory]
    [InlineData("HIGH")]
    [InlineData("High")]
    [InlineData("urgent")]
    [InlineData("")]
    public async Task Patch_RejectsInvalidSeverity(string severity)
    {
        var rule      = BuildRule();
        var catalogue = new StubCatalogueRepository { RuleToReturn = rule };
        var audit     = new StubAuditService();
        var sut       = BuildSut(catalogue, audit);

        var result = await sut.UpdateRuleAsync(
            rule.RuleId, Request($$"""{"severity": "{{severity}}"}"""), CancellationToken.None);

        Assert.Equal(400, ProblemOf(result).Status);
        Assert.False(catalogue.UpdateCalled);
        Assert.False(audit.LogCalled);
    }

    [Fact]
    public async Task Patch_RejectsExplicitNullSeverity()
    {
        var rule      = BuildRule();
        var catalogue = new StubCatalogueRepository { RuleToReturn = rule };
        var sut       = BuildSut(catalogue, new StubAuditService());

        var result = await sut.UpdateRuleAsync(
            rule.RuleId, Request("""{"severity": null}"""), CancellationToken.None);

        Assert.Equal(400, ProblemOf(result).Status);
        Assert.False(catalogue.UpdateCalled);
    }

    // ── PATCH: threshold validation ───────────────────────────────────────────

    /// <summary>
    /// THE FACTOR-OF-100 TRAP: 55 rather than 0,55 for a ratio rule. Inventory over current assets
    /// can never exceed 55, so the rule would simply stop firing — silently. Rejected.
    /// </summary>
    [Theory]
    [InlineData(55)]
    [InlineData(0)]
    [InlineData(-0.5)]
    [InlineData(1.5)]
    public async Task Patch_RejectsRatioThresholdOutsideZeroToOne(double threshold)
    {
        var rule      = BuildRule(AlertRuleEngine.InventarioElevado);
        var catalogue = new StubCatalogueRepository { RuleToReturn = rule };
        var audit     = new StubAuditService();
        var sut       = BuildSut(catalogue, audit);

        var result = await sut.UpdateRuleAsync(
            rule.RuleId,
            Request($$"""{"thresholdValue": {{threshold.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}"""),
            CancellationToken.None);

        Assert.Equal(400, ProblemOf(result).Status);
        Assert.False(catalogue.UpdateCalled);
        Assert.False(audit.LogCalled);
    }

    /// <summary>1 is a valid proportion (100 %); the interval is (0, 1].</summary>
    [Fact]
    public async Task Patch_AcceptsRatioThresholdOfExactlyOne()
    {
        var rule      = BuildRule(AlertRuleEngine.InventarioElevado);
        var catalogue = new StubCatalogueRepository { RuleToReturn = rule };
        var sut       = BuildSut(catalogue, new StubAuditService());

        var result = await sut.UpdateRuleAsync(
            rule.RuleId, Request("""{"thresholdValue": 1}"""), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1m, catalogue.CapturedThreshold);
    }

    /// <summary>More than 4 decimals does not fit DECIMAL(10,4) and would be silently rounded by SQL Server.</summary>
    [Fact]
    public async Task Patch_RejectsThresholdBeyondFourDecimals()
    {
        var rule      = BuildRule(AlertRuleEngine.InventarioElevado);
        var catalogue = new StubCatalogueRepository { RuleToReturn = rule };
        var sut       = BuildSut(catalogue, new StubAuditService());

        var result = await sut.UpdateRuleAsync(
            rule.RuleId, Request("""{"thresholdValue": 0.55555}"""), CancellationToken.None);

        Assert.Equal(400, ProblemOf(result).Status);
        Assert.False(catalogue.UpdateCalled);
    }

    [Fact]
    public async Task Patch_RejectsThresholdExceedingColumnPrecision()
    {
        var rule      = BuildRule(AlertRuleEngine.InventarioElevado);
        var catalogue = new StubCatalogueRepository { RuleToReturn = rule };
        var sut       = BuildSut(catalogue, new StubAuditService());

        var result = await sut.UpdateRuleAsync(
            rule.RuleId, Request("""{"thresholdValue": 1000000}"""), CancellationToken.None);

        var problem = ProblemOf(result);
        Assert.Equal(400, problem.Status);
        Assert.Contains("dígitos", problem.Detail);
        Assert.False(catalogue.UpdateCalled);
    }

    // ── PATCH: thresholdless-rule guard ───────────────────────────────────────

    /// <summary>
    /// THE SILENT-DECORATION TRAP: the engine never reads a threshold for these rules, so a value
    /// stored against one would be accepted, rendered on the configuration screen, and ignored on
    /// every analysis. The analyst would believe she had changed something. Refused instead.
    /// </summary>
    [Theory]
    [InlineData(AlertRuleEngine.FlujoNoConcilia)]
    [InlineData(AlertRuleEngine.PasivoCienCorriente)]
    [InlineData(AlertRuleEngine.CoberturaInteresesIndeterminable)]
    [InlineData(AlertRuleEngine.FlujoFinanciacionCero)]
    public async Task Patch_SettingThresholdOnThresholdlessRule_Returns400AndNeverWrites(string code)
    {
        var rule      = BuildRule(code, thresholdValue: null);
        var catalogue = new StubCatalogueRepository { RuleToReturn = rule };
        var audit     = new StubAuditService();
        var sut       = BuildSut(catalogue, audit);

        var result = await sut.UpdateRuleAsync(
            rule.RuleId, Request("""{"thresholdValue": 12.5}"""), CancellationToken.None);

        var problem = ProblemOf(result);
        Assert.Equal(400, problem.Status);
        Assert.Contains(code, problem.Detail);
        Assert.Contains("no utiliza umbral", problem.Detail);
        Assert.False(catalogue.UpdateCalled);
        Assert.False(audit.LogCalled);
    }

    /// <summary>
    /// The counterpart: a rule that DOES take a threshold accepts one. Locks the guard to the codes
    /// the engine actually reads a threshold for, rather than blocking thresholds outright.
    /// </summary>
    [Fact]
    public async Task Patch_SettingThresholdOnThresholdedRule_IsAccepted()
    {
        var rule      = BuildRule(AlertRuleEngine.InventarioElevado, thresholdValue: 0.50m);
        var catalogue = new StubCatalogueRepository { RuleToReturn = rule };
        var sut       = BuildSut(catalogue, new StubAuditService());

        var result = await sut.UpdateRuleAsync(
            rule.RuleId, Request("""{"thresholdValue": 0.55}"""), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(0.55m, catalogue.CapturedThreshold);
    }

    // ── Threshold guards judge the SUBMITTED value, not the stored one ────────
    // A rule whose STORED threshold is already invalid is reachable: until this endpoint existed
    // the catalogue was edited by hand in SQL. Judging the resolved value would reject every patch
    // to such a rule — including one that never mentions the threshold — leaving it uneditable and
    // unenableable behind an error about a field the caller did not touch.

    /// <summary>
    /// THE REGRESSION: a thresholdless rule carrying a stray stored threshold must still be
    /// enableable. {"enabled": true} says nothing about the threshold and must not be judged by it.
    /// </summary>
    [Fact]
    public async Task Patch_EnablingThresholdlessRuleWithStoredThreshold_IsAccepted()
    {
        var rule      = BuildRule(AlertRuleEngine.FlujoNoConcilia, enabled: false, thresholdValue: 12.5m);
        var catalogue = new StubCatalogueRepository { RuleToReturn = rule };
        var sut       = BuildSut(catalogue, new StubAuditService());

        var result = await sut.UpdateRuleAsync(
            rule.RuleId, Request("""{"enabled": true}"""), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.True(catalogue.CapturedEnabled);
        Assert.Equal(12.5m, catalogue.CapturedThreshold);   // carried through untouched
    }

    /// <summary>
    /// Same defect one branch over: a proportion rule whose stored threshold is out of range (55
    /// rather than 0,55, as a hand-written UPDATE could leave it) must still accept a patch that
    /// only changes its severity.
    /// </summary>
    [Fact]
    public async Task Patch_EditingProportionRuleWithStoredOutOfRangeThreshold_IsAccepted()
    {
        var rule      = BuildRule(AlertRuleEngine.InventarioElevado, thresholdValue: 55m);
        var catalogue = new StubCatalogueRepository { RuleToReturn = rule };
        var sut       = BuildSut(catalogue, new StubAuditService());

        var result = await sut.UpdateRuleAsync(
            rule.RuleId, Request("""{"severity": "high"}"""), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("high", catalogue.CapturedSeverity);
    }

    /// <summary>
    /// The complement, so the fix does not become a hole: submitting the out-of-range value is
    /// still rejected, even on a rule that already stores one.
    /// </summary>
    [Fact]
    public async Task Patch_SubmittingOutOfRangeThreshold_IsStillRejected_EvenWhenStoredValueIsAlsoInvalid()
    {
        var rule      = BuildRule(AlertRuleEngine.InventarioElevado, thresholdValue: 55m);
        var catalogue = new StubCatalogueRepository { RuleToReturn = rule };
        var sut       = BuildSut(catalogue, new StubAuditService());

        var result = await sut.UpdateRuleAsync(
            rule.RuleId, Request("""{"thresholdValue": 60}"""), CancellationToken.None);

        Assert.Equal(400, ProblemOf(result).Status);
        Assert.False(catalogue.UpdateCalled);
    }

    /// <summary>
    /// The guard fires on a VALUE, not on the key: clearing to null must stay legal for a
    /// thresholdless rule, otherwise a payload that changes nothing about the threshold would be
    /// rejected.
    /// </summary>
    [Fact]
    public async Task Patch_ClearingThresholdOnThresholdlessRule_IsAccepted()
    {
        var rule      = BuildRule(AlertRuleEngine.FlujoNoConcilia, thresholdValue: null);
        var catalogue = new StubCatalogueRepository { RuleToReturn = rule };
        var sut       = BuildSut(catalogue, new StubAuditService());

        var result = await sut.UpdateRuleAsync(
            rule.RuleId, Request("""{"thresholdValue": null}"""), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.True(catalogue.UpdateCalled);
        Assert.Null(catalogue.CapturedThreshold);
    }

    // ── PATCH: unimplemented-code guard ───────────────────────────────────────

    /// <summary>
    /// Enabling a code no engine rule implements is REJECTED, not accepted-and-ignored: the engine
    /// would log-and-skip it on every analysis, so the row would read as active and never fire.
    /// </summary>
    [Fact]
    public async Task Patch_EnablingUnimplementedCode_Returns400AndNeverWrites()
    {
        var rule      = BuildRule("REGLA_FANTASMA", enabled: false, thresholdValue: null);
        var catalogue = new StubCatalogueRepository { RuleToReturn = rule };
        var audit     = new StubAuditService();
        var sut       = BuildSut(catalogue, audit);

        var result = await sut.UpdateRuleAsync(
            rule.RuleId, Request("""{"enabled": true}"""), CancellationToken.None);

        var problem = ProblemOf(result);
        Assert.Equal(400, problem.Status);
        Assert.Contains("REGLA_FANTASMA", problem.Detail);
        Assert.False(catalogue.UpdateCalled);
        Assert.False(audit.LogCalled);
    }

    /// <summary>
    /// The guard is about ENABLING, not about editing: an unimplemented rule that stays disabled
    /// can still have its severity corrected.
    /// </summary>
    [Fact]
    public async Task Patch_EditingDisabledUnimplementedCode_IsAllowed()
    {
        var rule      = BuildRule("REGLA_FANTASMA", enabled: false, thresholdValue: null);
        var catalogue = new StubCatalogueRepository { RuleToReturn = rule };
        var sut       = BuildSut(catalogue, new StubAuditService());

        var result = await sut.UpdateRuleAsync(
            rule.RuleId, Request("""{"severity": "low"}"""), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("low", catalogue.CapturedSeverity);
    }

    /// <summary>Every implemented code can be enabled — the guard must not over-reject.</summary>
    [Theory]
    [InlineData(AlertRuleEngine.PasivoCienCorriente)]
    [InlineData(AlertRuleEngine.InventarioElevado)]
    [InlineData(AlertRuleEngine.EndeudamientoElevado)]
    [InlineData(AlertRuleEngine.CoberturaInteresesIndeterminable)]
    [InlineData(AlertRuleEngine.FlujoNoConcilia)]
    [InlineData(AlertRuleEngine.FlujoFinanciacionCero)]
    public async Task Patch_EnablingImplementedCode_IsAllowed(string code)
    {
        var rule      = BuildRule(code, enabled: false, thresholdValue: null);
        var catalogue = new StubCatalogueRepository { RuleToReturn = rule };
        var sut       = BuildSut(catalogue, new StubAuditService());

        var result = await sut.UpdateRuleAsync(
            rule.RuleId, Request("""{"enabled": true}"""), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.True(catalogue.CapturedEnabled);
    }

    // ── PATCH: audit ──────────────────────────────────────────────────────────

    /// <summary>
    /// A threshold change alters which alerts fire and therefore what the SGR decides on, so the
    /// entry must carry who changed which rule and both the before and after values.
    /// </summary>
    [Fact]
    public async Task Patch_WritesAuditEntryWithBeforeAndAfter()
    {
        var rule      = BuildRule(AlertRuleEngine.InventarioElevado, enabled: false, severity: "medium", thresholdValue: 0.50m);
        var catalogue = new StubCatalogueRepository { RuleToReturn = rule };
        var audit     = new StubAuditService();
        var sut       = BuildSut(catalogue, audit);

        await sut.UpdateRuleAsync(
            rule.RuleId,
            Request("""{"enabled": true, "severity": "high", "thresholdValue": 0.65}"""),
            CancellationToken.None);

        Assert.True(audit.LogCalled);
        Assert.Equal("AlertRule.Updated",   audit.Action);
        Assert.Equal("AlertRuleCatalogue",  audit.EntityType);
        Assert.Equal(rule.RuleId.ToString(), audit.EntityId);
        Assert.Equal(UserId, audit.UserId);

        var before = JsonDocument.Parse(audit.OldValueJson!).RootElement;
        var after  = JsonDocument.Parse(audit.NewValueJson!).RootElement;

        Assert.False(before.GetProperty("Enabled").GetBoolean());
        Assert.Equal("medium", before.GetProperty("Severity").GetString());
        Assert.Equal(0.50m, before.GetProperty("ThresholdValue").GetDecimal());

        Assert.True(after.GetProperty("Enabled").GetBoolean());
        Assert.Equal("high", after.GetProperty("Severity").GetString());
        Assert.Equal(0.65m, after.GetProperty("ThresholdValue").GetDecimal());

        // Code on both sides so the entry is readable without joining back to the catalogue.
        Assert.Equal(AlertRuleEngine.InventarioElevado, before.GetProperty("Code").GetString());
        Assert.Equal(AlertRuleEngine.InventarioElevado, after.GetProperty("Code").GetString());
    }

    /// <summary>The UpdatedBy column exists to record who moved a number — it must be the caller.</summary>
    [Fact]
    public async Task Patch_RecordsCallerAsUpdatedBy()
    {
        var rule      = BuildRule();
        var catalogue = new StubCatalogueRepository { RuleToReturn = rule };
        var sut       = BuildSut(catalogue, new StubAuditService());

        await sut.UpdateRuleAsync(
            rule.RuleId, Request("""{"severity": "high"}"""), CancellationToken.None);

        Assert.Equal(UserId, catalogue.CapturedUpdatedBy);
    }

    /// <summary>
    /// The PATCH response names the editor too, so the screen can render the row it just saved
    /// without re-fetching the list.
    ///
    /// It costs no query: the editor is the caller by construction, and the caller's row was
    /// already read to authorise the write. GetByIdsCallCount == 0 is what pins that — a second
    /// lookup here would be a wasted round trip on every save.
    /// </summary>
    [Fact]
    public async Task Patch_ReturnsUpdatedByName_WithoutASecondUserLookup()
    {
        var rule      = BuildRule();
        var catalogue = new StubCatalogueRepository { RuleToReturn = rule };
        var users     = new StubUserRepository
        {
            UserToReturn = new User
            {
                UserId      = UserId,
                FirebaseUid = FirebaseUid,
                DisplayName = "Ana Gómez"
            }
        };
        var sut = BuildSut(catalogue, new StubAuditService(), users);

        var result = await sut.UpdateRuleAsync(
            rule.RuleId, Request("""{"severity": "high"}"""), CancellationToken.None);

        var ok  = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<AlertRuleDto>(ok.Value);

        Assert.Equal(UserId, dto.UpdatedBy);
        Assert.Equal("Ana Gómez", dto.UpdatedByName);
        Assert.Equal(0, users.GetByIdsCallCount);
    }
}
