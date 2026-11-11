// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-20

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// The persona humana pf_ keys (M10 Phase 4a) through PUT /api/settings/scoring: accepted and stored,
/// bounded (whole numbers, fractions, ascending cuts), refused on the sector PUT, and kept out of the
/// balance groups of the Settings DTO. Their own personaHumana array, the reset and the audit values are
/// in the .Contract partial.
/// </summary>
public sealed partial class SettingsServicePersonEvaluationTests
{
    private const string Tenant = "tenant-pf";

    [Fact]
    public async Task PfKey_IsAcceptedAndStored_WithItsDescription()
    {
        var (sut, db) = Build();

        var dto = await Update(sut, ("pf_bcra_sit_rechazo", 4m));

        Assert.NotNull(dto);
        var row = Assert.Single(db.ScoringConfigs.AsNoTracking().Where(r => r.TenantId == Tenant));
        Assert.Equal("pf_bcra_sit_rechazo", row.ConfigKey);
        Assert.Equal(4m, row.ConfigValue);
        Assert.StartsWith("PROVISIONAL", row.Description);
    }

    [Fact]
    public async Task PfKeys_AreNotSurfacedInTheSettingsDto()
    {
        var (sut, _) = Build();

        var dto = await Update(sut, ("pf_antiguedad_min_meses", 12m));

        var keys = dto!.RatioWeights.Concat(dto.RatioThresholds).Concat(dto.ComponentWeights)
            .Concat(dto.ScoreThresholds).Concat(dto.Macro).Select(i => i.Key);
        Assert.DoesNotContain(keys, k => k.StartsWith(PersonEvaluationDefaults.KeyPrefix, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("pf_bcra_sit_rechazo", "6")]        // above 5
    [InlineData("pf_bcra_sit_rechazo", "2.5")]      // not whole
    [InlineData("pf_bcra_ventana_meses", "13")]     // the BCRA informs 12
    [InlineData("pf_informe_max_dias", "0")]
    [InlineData("pf_cuota_ingreso_alto", "1.5")]    // a fraction
    [InlineData("pf_ventas_acreedor_fisco_tolerancia", "1.01")]
    [InlineData("pf_consumo_mensual_minimo_miles", "-1")]
    [InlineData("pf_consumo_mensual_minimo_miles", "1000000")]   // above 999999 thousands
    [InlineData("pf_consumo_mensual_minimo_miles", "2450.5")]    // thousands, whole
    [InlineData("pf_requiere_certificado_mipyme", "2")]          // a flag
    [InlineData("pf_aprobacion_requiere_motivo", "0.5")]
    [InlineData("pf_rechazo_sugerido_requiere_preaprobacion", "-1")]
    [InlineData("pf_certificado_alerta_desde_mes", "13")]
    [InlineData("pf_cheques_pagados_alerta_meses", "61")]
    [InlineData("pf_cuota_estimada_pct_saldo", "0.005")]        // 0,01-0,20
    [InlineData("pf_cuota_estimada_pct_saldo", "0.25")]
    [InlineData("pf_consumo_mes_base", "201912")]                // before 202001
    [InlineData("pf_consumo_mes_base", "202613")]                // not a month
    [InlineData("pf_consumo_mes_base", "202600")]
    [InlineData("pf_consumo_mes_base", "202609.5")]              // whole
    [InlineData("pf_consumo_mes_base", "999912")]                // after the current month
    [InlineData("pf_haircut_deposito", "-0.1")]
    [InlineData("pf_haircut_inversion", "1.3")]
    [InlineData("pf_haircut_credito", "1.01")]
    [InlineData("pf_haircut_otro", "-1")]
    [InlineData("pf_dscr_rojo", "11")]
    [InlineData("pf_patrimonio_operativo_alerta_pct", "1.2")]
    [InlineData("pf_contragarantia_cobertura_min_pct", "101")]   // a percent of the aval, 0-100
    [InlineData("pf_contragarantia_cobertura_min_pct", "-1")]
    [InlineData("pf_contragarantia_cobertura_min_pct", "12.5")]  // whole percent
    [InlineData("pf_cobertura_rojo", "-0.1")]
    [InlineData("pf_cobertura_verde", "11")]
    [InlineData("pf_cuota_ingreso_estres_max", "1.01")]
    [InlineData("pf_haircut_inmueble", "1.5")]
    [InlineData("pf_haircut_inmueble_vivienda", "-0.25")]
    [InlineData("pf_haircut_participacion", "2")]
    [InlineData("pf_haircut_descalce_moneda", "1.08")]
    [InlineData("pf_ratio_mercado_fiscal_alerta", "1001")]
    public async Task PfKey_OutOfBounds_IsRejected(string key, string value)
    {
        var (sut, db) = Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Update(sut, (key, decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture))));

        Assert.Empty(db.ScoringConfigs.AsNoTracking());
    }

    [Theory]
    [InlineData(0)]     // sola firma allowed
    [InlineData(100)]   // the default
    public async Task CounterGuaranteeCoverage_InsideItsBounds_IsAccepted(int value)
    {
        var (sut, db) = Build();

        await Update(sut, ("pf_contragarantia_cobertura_min_pct", value));

        var row = Assert.Single(db.ScoringConfigs.AsNoTracking().Where(r => r.TenantId == Tenant));
        Assert.Equal("pf_contragarantia_cobertura_min_pct", row.ConfigKey);
        Assert.Equal(value, row.ConfigValue);
    }

    [Fact]
    public async Task CuotaIngresoCuts_MustAscend()
    {
        var (sut, _) = Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Update(sut, ("pf_cuota_ingreso_medio", 0.20m)));   // below the 0.25 default of _bajo

        Assert.Contains("pf_cuota_ingreso_medio", ex.Message);
    }

    [Fact]
    public async Task CuotaIngresoCuts_MovedTogether_AreAccepted()
    {
        var (sut, _) = Build();

        var dto = await Update(sut,
            ("pf_cuota_ingreso_bajo", 0.20m), ("pf_cuota_ingreso_medio", 0.30m), ("pf_cuota_ingreso_alto", 0.40m));

        Assert.NotNull(dto);
    }

    [Fact]
    public async Task FiadorKeys_InsideTheirBounds_AreAccepted()
    {
        var (sut, db) = Build();

        await Update(sut,
            ("pf_cobertura_rojo", 0.5m), ("pf_cobertura_verde", 10m), ("pf_haircut_inmueble", 0m),
            ("pf_haircut_descalce_moneda", 1m), ("pf_ratio_mercado_fiscal_alerta", 1000m));

        Assert.Equal(5, db.ScoringConfigs.AsNoTracking().Count(r => r.TenantId == Tenant));
    }

    [Fact]
    public async Task DscrBands_VerdeMustExceedRojo()
    {
        var (sut, _) = Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Update(sut, ("pf_dscr_verde", 0.9m)));

        Assert.Contains("pf_dscr_verde", ex.Message);
    }

    [Fact]
    public async Task RejectionAge_MustNotExceedTheAlertAge()
    {
        var (sut, _) = Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Update(sut, ("pf_antiguedad_rechazo_meses", 30m)));

        Assert.Contains("pf_antiguedad_rechazo_meses", ex.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("13")]
    [InlineData("7.5")]
    public async Task CertificateUpperMonth_OutOfBounds_IsRejected(string value)
    {
        var (sut, db) = Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Update(sut, ("pf_certificado_alerta_hasta_mes", decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture))));

        Assert.Contains("pf_certificado_alerta_hasta_mes", ex.Message);
        Assert.Empty(db.ScoringConfigs.AsNoTracking());
    }

    [Fact]
    public async Task CertificateUpperMonth_BelowTheLowerMonth_IsRejected()
    {
        var (sut, _) = Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Update(sut, ("pf_certificado_alerta_hasta_mes", 4m)));   // below the default desde_mes 5

        Assert.Contains("pf_certificado_alerta_hasta_mes", ex.Message);
        Assert.Contains("pf_certificado_alerta_desde_mes", ex.Message);
    }

    [Fact]
    public async Task CertificateLowerMonth_RaisedAboveTheDefaultUpperMonth_Alone_IsRejected()
    {
        var (sut, _) = Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Update(sut, ("pf_certificado_alerta_desde_mes", 9m)));   // the default hasta_mes is 8

        Assert.Contains("pf_certificado_alerta_hasta_mes", ex.Message);
    }

    [Theory]
    [InlineData(9, 10)]
    [InlineData(6, 6)]     // a one-month window
    [InlineData(1, 12)]
    public async Task CertificateWindow_MovedTogether_IsAccepted(int desde, int hasta)
    {
        var (sut, db) = Build();

        var dto = await Update(sut,
            ("pf_certificado_alerta_desde_mes", desde), ("pf_certificado_alerta_hasta_mes", hasta));

        Assert.NotNull(dto);
        var row = Assert.Single(db.ScoringConfigs.AsNoTracking(), r => r.ConfigKey == "pf_certificado_alerta_hasta_mes");
        Assert.Equal(hasta, row.ConfigValue);
        Assert.StartsWith("PROVISIONAL", row.Description);
    }

    [Fact]
    public async Task TheOldMonotributoConsumoKey_IsNoLongerAccepted()
    {
        var (sut, _) = Build();
        Assert.Null(await Update(sut, ("pf_consumo_mensual_monotributo", 100m)));
    }

    [Fact]
    public async Task UnknownPfKey_IsRefusedLikeAnyUnknownKey()
    {
        var (sut, _) = Build();
        Assert.Null(await Update(sut, ("pf_does_not_exist", 1m)));
    }

    [Fact]
    public async Task SectorPut_RefusesPfKeys()
    {
        var (sut, _) = Build();

        var result = await sut.UpdateSectorProfileAsync(
            Guid.NewGuid(), Tenant, "Comercio",
            new UpdateSectorProfileRequest
            {
                Thresholds = [new ScoringConfigItemDto { Key = "pf_bcra_sit_rechazo", Value = 4m }]
            },
            ipAddress: null, requestId: null, ct: CancellationToken.None);

        Assert.Null(result);
    }

    // ── Harness (as in SettingsServiceScoringValidationTests) ───────────────────

    private static (SettingsService Sut, FinanciaDbContext Db) Build(params ScoringConfig[] seed)
    {
        var (sut, db, _) = BuildAudited(seed);
        return (sut, db);
    }

    private static (SettingsService Sut, FinanciaDbContext Db, RecordingAuditService Audit) BuildAudited(
        params ScoringConfig[] seed)
    {
        var db = new FinanciaDbContext(new DbContextOptionsBuilder<FinanciaDbContext>()
            .UseInMemoryDatabase($"settings-pf-{Guid.NewGuid()}")
            .Options);

        if (seed.Length > 0)
        {
            db.ScoringConfigs.AddRange(seed);
            db.ChangeTracker.DetectChanges();   // AutoDetectChanges is disabled on the context
            db.SaveChanges();
        }

        var audit = new RecordingAuditService();
        return (new SettingsService(db, audit, new ThrowingTenantProfileService(),
            NullLogger<SettingsService>.Instance), db, audit);
    }

    private static Task<ScoringConfigDto?> Update(SettingsService sut, params (string Key, decimal Value)[] items)
        => Send(sut, items.Select(i => new ScoringConfigUpdateItem { Key = i.Key, Value = i.Value }).ToArray());

    private static Task<ScoringConfigDto?> Send(SettingsService sut, params ScoringConfigUpdateItem[] items)
        => sut.UpdateScoringConfigAsync(
            Guid.NewGuid(), Tenant,
            new UpdateScoringConfigRequest { Items = items },
            ipAddress: null, requestId: null, ct: CancellationToken.None);

    private static ScoringConfigUpdateItem Reset(string key) => new() { Key = key, Reset = true };

    private static ScoringConfig SeedRow(string key, decimal value) => new()
    {
        ConfigId    = Guid.NewGuid(),
        TenantId    = Tenant,
        ConfigKey   = key,
        ConfigValue = value,
        Description = "seeded by SettingsServicePersonEvaluationTests",
        UpdatedAt   = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private sealed class ThrowingTenantProfileService : ITenantProfileService
    {
        public Task<TenantProfileResolution> GetEffectiveAsync(string tenantId, CancellationToken ct)
            => throw new NotSupportedException("Scoring config must not read the tenant profile.");

        public Task<TenantProfileResolution> SetEntityTypeAsync(string tenantId, string entityType, CancellationToken ct)
            => throw new NotSupportedException("Scoring config must not write the tenant profile.");
    }
}
