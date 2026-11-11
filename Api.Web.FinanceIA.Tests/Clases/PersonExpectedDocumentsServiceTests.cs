// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-31

using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Api.Web.FinanceIA.Tests.PersonAnalysis.ExpectedDocumentsTestKit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// <see cref="PersonExpectedDocumentsService"/> over the REAL TenantSettings and pf_ repositories on an in-memory
/// database: the per-regime fallback, the never-throwing reads (a bad row or a failed read is the code defaults
/// plus a Warning that never carries the value), the update and reset round trips, their audit trail and the
/// effective recency cut the GET shows.
/// </summary>
public sealed class PersonExpectedDocumentsServiceTests
{
    private const string Tenant = DashboardFixture.Tenant;

    private static readonly CancellationToken Ct     = CancellationToken.None;
    private static readonly Guid              UserId = Guid.NewGuid();

    private static PersonExpectedDocumentsService Service(
        DashboardFixture f, IAuditService? audit = null, ILogger<PersonExpectedDocumentsService>? logger = null)
        => RealService(f.Context, audit, logger);

    private static Dictionary<string, List<PersonExpectedDocumentLineDto?>?> MonoOnly()
        => new() { [ExpectedDocumentRegimes.Monotributo] = CustomMonotributo() };

    // ── Resolver ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoRow_ReadsTheDefaults_ForEveryRegime()
    {
        await using var f = DashboardFixture.Create(nameof(NoRow_ReadsTheDefaults_ForEveryRegime));

        var resolved = await Service(f).GetResolvedAsync(Tenant, Ct);

        Assert.Equal(ExpectedDocumentRegimes.All, resolved.Regimes.Select(r => r.Regime));
        Assert.All(resolved.Regimes, r =>
        {
            Assert.Equal(ExpectedDocumentsSources.Default, r.Source);
            Assert.Same(LegajoExpectedDocuments.Defaults[r.Regime], r.Lines);
            Assert.Null(r.UpdatedAt);
        });
        Assert.Null(resolved.UpdatedAt);
    }

    [Fact]
    public async Task APartialRow_FallsBackPerRegime()
    {
        await using var f = DashboardFixture.Create(nameof(APartialRow_FallsBackPerRegime));
        var savedAt = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
        Seed(f.Context, Tenant, CustomMonotributoJson(), savedAt);

        var service = Service(f);
        var mono    = await service.ForAsync(Tenant, FiscalRegimes.Monotributo, Ct);
        var ri      = await service.ForAsync(Tenant, FiscalRegimes.ResponsableInscripto, Ct);
        var exento  = await service.ForAsync(Tenant, FiscalRegimes.Exento, Ct);

        Assert.Equal(ExpectedDocumentsSources.Tenant, mono.Source);
        Assert.Equal(savedAt, mono.UpdatedAt);
        Assert.DoesNotContain(mono.Lines, l => l.DocumentType == DocumentTypes.DdjjIibb);
        Assert.Equal(3, mono.Lines.Single(l => l.DocumentType == DocumentTypes.CertificacionIngresos).MaxAgeMonths);

        Assert.Equal(ExpectedDocumentsSources.Default, ri.Source);
        Assert.Null(ri.UpdatedAt);
        Assert.Same(LegajoExpectedDocuments.ResponsableInscripto, ri.Lines);
        Assert.Equal(ExpectedDocumentRegimes.ExentoOrDesconocido, exento.Regime);
        Assert.Same(LegajoExpectedDocuments.ExentoOrDesconocido, exento.Lines);
    }

    [Fact]
    public async Task TheRow_IsTenantScoped()
    {
        await using var f = DashboardFixture.Create(nameof(TheRow_IsTenantScoped));
        Seed(f.Context, DashboardFixture.Other, CustomMonotributoJson());

        var mono = await Service(f).ForAsync(Tenant, FiscalRegimes.Monotributo, Ct);

        Assert.Equal(ExpectedDocumentsSources.Default, mono.Source);
    }

    [Theory]
    [InlineData("{ not json", PersonExpectedDocumentsMatrix.ErrorUnparseable)]
    [InlineData("""{"regimes":{"Monotributo":[{"documentType":"Monotributo","requirement":"required"}]}}""", PersonExpectedDocumentsMatrix.ErrorInvalid)]
    public async Task AnUnusableRow_ReadsTheDefaults_WithAWarningThatNeverCarriesTheValue(string json, string kind)
    {
        await using var f = DashboardFixture.Create(nameof(AnUnusableRow_ReadsTheDefaults_WithAWarningThatNeverCarriesTheValue) + kind);
        Seed(f.Context, Tenant, json);
        var logger = new CapturingLogger<PersonExpectedDocumentsService>();

        var resolved = await Service(f, logger: logger).GetResolvedAsync(Tenant, Ct);

        Assert.Same(PersonExpectedDocumentsResolved.Defaults, resolved);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(Tenant, warning.Message);
        Assert.Contains(kind, warning.Message);
        Assert.Contains(TenantSettingKeys.PersonExpectedDocuments, warning.Message);
        Assert.DoesNotContain("documentType", warning.Message);
        Assert.DoesNotContain("not json", warning.Message);
    }

    [Fact]
    public async Task AFailedRead_ReadsTheDefaults_AndNeverThrows()
    {
        var logger  = new CapturingLogger<PersonExpectedDocumentsService>();
        var service = new PersonExpectedDocumentsService(
            new ThrowingSettingsRepository(), null!, new RecordingAuditService(), logger);

        var mono = await service.ForAsync(Tenant, FiscalRegimes.Monotributo, Ct);

        Assert.Equal(ExpectedDocumentsSources.Default, mono.Source);
        Assert.Same(LegajoExpectedDocuments.Monotributo, mono.Lines);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(PersonExpectedDocumentsService.ErrorReadFailed, warning.Message);
    }

    [Fact]
    public async Task ACancelledRead_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var service = new PersonExpectedDocumentsService(
            new ThrowingSettingsRepository(cancel: true), null!, new RecordingAuditService(), NullLogger<PersonExpectedDocumentsService>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetResolvedAsync(Tenant, cts.Token));
    }

    // ── Update / reset ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_StoresCanonicalJson_ReturnsTheView_AndAuditsBeforeAndAfter()
    {
        await using var f = DashboardFixture.Create(nameof(Update_StoresCanonicalJson_ReturnsTheView_AndAuditsBeforeAndAfter));
        var audit = new RecordingAuditService();

        var view = await Service(f, audit).UpdateAsync(Tenant, MonoOnly(), UserId, "1.2.3.4", "req", Ct);

        var mono = view.Regimes.Single(r => r.Regime == ExpectedDocumentRegimes.Monotributo);
        Assert.Equal(ExpectedDocumentsSources.Tenant, mono.Source);
        Assert.Equal(ExpectedDocumentsSources.Default, view.Regimes.Single(r => r.Regime == ExpectedDocumentRegimes.ResponsableInscripto).Source);
        Assert.NotNull(view.UpdatedAt);

        f.Context.ChangeTracker.Clear();
        var stored = await f.Context.TenantSettings.AsNoTracking()
            .SingleAsync(s => s.TenantId == Tenant && s.SettingKey == TenantSettingKeys.PersonExpectedDocuments);
        Assert.Equal(CustomMonotributoJson(), stored.SettingValue);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(PersonExpectedDocumentsService.AuditAction, entry.Action);
        Assert.Equal("TenantSettings", entry.EntityType);
        Assert.Equal(TenantSettingKeys.PersonExpectedDocuments, entry.EntityId);
        Assert.Equal(UserId, entry.UserId);

        var before = JsonNode.Parse(entry.OldValueJson!)!;
        var after  = JsonNode.Parse(entry.NewValueJson!)!;
        Assert.All(before["regimes"]!.AsArray(), r => Assert.Equal("default", (string?)r!["source"]));
        Assert.Equal("tenant", (string?)after["regimes"]![0]!["source"]);
        Assert.Equal(6, after["regimes"]![0]!["lines"]!.AsArray().Count);
        Assert.Equal(3, (int?)after["regimes"]![0]!["lines"]![2]!["maxAgeMonths"]);
        Assert.Null(after["reset"]);
    }

    [Fact]
    public async Task Update_Invalid_Throws_AndWritesNothing()
    {
        await using var f = DashboardFixture.Create(nameof(Update_Invalid_Throws_AndWritesNothing));
        var audit = new RecordingAuditService();
        var bad   = new Dictionary<string, List<PersonExpectedDocumentLineDto?>?>
        {
            [ExpectedDocumentRegimes.Monotributo] = [Line(DocumentTypes.Ganancias)]
        };

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => Service(f, audit).UpdateAsync(Tenant, bad, UserId, null, null, Ct));

        Assert.Contains(ex.Errors, e => e.Field == "regimes.Monotributo");
        Assert.Empty(audit.Entries);
        Assert.False(await f.Context.TenantSettings.AnyAsync());
    }

    [Fact]
    public async Task Reset_DeletesTheRow_AndAuditsIt()
    {
        await using var f = DashboardFixture.Create(nameof(Reset_DeletesTheRow_AndAuditsIt));
        Seed(f.Context, Tenant, CustomMonotributoJson());
        var audit = new RecordingAuditService();

        var view = await Service(f, audit).ResetAsync(Tenant, UserId, null, null, Ct);

        Assert.All(view.Regimes, r => Assert.Equal(ExpectedDocumentsSources.Default, r.Source));
        Assert.Null(view.UpdatedAt);
        f.Context.ChangeTracker.Clear();
        Assert.False(await f.Context.TenantSettings.AnyAsync(s => s.TenantId == Tenant));

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(PersonExpectedDocumentsService.AuditAction, entry.Action);
        Assert.Equal("tenant", (string?)JsonNode.Parse(entry.OldValueJson!)!["regimes"]![0]!["source"]);
        Assert.True((bool?)JsonNode.Parse(entry.NewValueJson!)!["reset"]);

        // A second reset is a no-op on the data, still audited.
        await Service(f, audit).ResetAsync(Tenant, UserId, null, null, Ct);
        Assert.Equal(2, audit.Entries.Count);
    }

    // ── View ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task View_EffectiveMaxAge_InheritsTheTenantsClassKeys()
    {
        await using var f = DashboardFixture.Create(nameof(View_EffectiveMaxAge_InheritsTheTenantsClassKeys));
        f.Context.ScoringConfigs.Add(new ScoringConfig
        {
            ConfigId = Guid.NewGuid(), TenantId = Tenant, ConfigKey = "pf_doc_antiguedad_max_meses", ConfigValue = 9m,
            UpdatedAt = DashboardFixture.Now
        });
        await f.Context.SaveChangesAsync();
        Seed(f.Context, Tenant, CustomMonotributoJson());

        var view = await Service(f).GetViewAsync(Tenant, Ct);

        var mono = view.Regimes.Single(r => r.Regime == ExpectedDocumentRegimes.Monotributo).Lines;
        var line = (string type) => mono.Single(l => l.DocumentType == type);

        Assert.Equal(("timeless", (int?)null, (int?)null),
            (line(DocumentTypes.AutorizacionDatos).AgeClass, line(DocumentTypes.AutorizacionDatos).MaxAgeMonths, line(DocumentTypes.AutorizacionDatos).EffectiveMaxAgeMonths));
        Assert.Equal(("pointInTime", (int?)null, (int?)9),            // inherits the tenant's pf_doc_antiguedad_max_meses
            (line(DocumentTypes.Monotributo).AgeClass, line(DocumentTypes.Monotributo).MaxAgeMonths, line(DocumentTypes.Monotributo).EffectiveMaxAgeMonths));
        Assert.Equal(("pointInTime", (int?)3, (int?)3),               // its own cut wins
            (line(DocumentTypes.CertificacionIngresos).AgeClass, line(DocumentTypes.CertificacionIngresos).MaxAgeMonths, line(DocumentTypes.CertificacionIngresos).EffectiveMaxAgeMonths));
        Assert.Equal(("annual", (int?)null, (int?)18),                // the default pf_ddjj_anual_antiguedad_max_meses
            (line(DocumentTypes.BienesPersonales).AgeClass, line(DocumentTypes.BienesPersonales).MaxAgeMonths, line(DocumentTypes.BienesPersonales).EffectiveMaxAgeMonths));
        Assert.Equal("optional", line(DocumentTypes.CertificacionIngresos).Requirement);
        Assert.Equal([DocumentTypes.Ganancias], line(DocumentTypes.BienesPersonales).Alternatives);

        Assert.All(view.Defaults, r => Assert.Equal(ExpectedDocumentsSources.Default, r.Source));
        Assert.Equal(7, view.Defaults.Single(r => r.Regime == ExpectedDocumentRegimes.Monotributo).Lines.Count);
        Assert.Equal(LegajoExpectedDocuments.ConfigurableTypes, view.DocumentTypes);
        Assert.DoesNotContain(DocumentTypes.Otro, view.DocumentTypes);
    }

    // ── Doubles ────────────────────────────────────────────────────────────────────

    private sealed class ThrowingSettingsRepository(bool cancel = false) : ITenantSettingsRepository
    {
        public Task<string?> GetValueAsync(string tenantId, string settingKey, CancellationToken ct) => throw new NotSupportedException();

        public Task<TenantSettingEntry?> GetEntryAsync(string tenantId, string settingKey, CancellationToken ct)
            => cancel ? throw new OperationCanceledException(ct) : throw new InvalidOperationException("db down");

        public Task<IReadOnlyDictionary<string, string>> GetValuesAsync(
            string tenantId, IReadOnlyCollection<string> settingKeys, CancellationToken ct) => throw new NotSupportedException();

        public Task UpsertAsync(string tenantId, string settingKey, string settingValue, CancellationToken ct) => throw new NotSupportedException();

        public Task SetValuesAsync(string tenantId, IReadOnlyDictionary<string, string?> values, CancellationToken ct) => throw new NotSupportedException();
    }

    internal sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
