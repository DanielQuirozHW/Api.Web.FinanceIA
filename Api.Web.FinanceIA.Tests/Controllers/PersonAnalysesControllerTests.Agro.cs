// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-30

using System.Text.Json;
using Api.Web.FinanceIA.Models.Arca;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Tests.PersonAnalysis;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Engine 0.7 through POST /api/person-analyses and "Reanalizar": the optional cuotasPorAnio and agroMode, their
/// validation, the solicitante's row storing the RESOLVED periodicity and the override as stated (DDL 078), the
/// detail exposing both, a re-evaluation reusing them, a pre-078 row re-read as monthly, and fiador rows untouched.
/// </summary>
public sealed partial class PersonAnalysesControllerTests
{
    private static void WithArcaActivity(PersonLegajo legajo, string clae)
    {
        legajo.ArcaSnapshotJson = JsonSerializer.Serialize(PersonEvaluationFixtures.Arca() with
        {
            Actividades = [new ArcaActividad(clae, "actividad", "883", 1, "201901")]
        });
        legajo.ArcaDataUpdatedAt = DateTime.UtcNow.AddDays(-1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(24)]
    public async Task Create_CuotasPorAnioOutsideTheSet_Is400ValidationError(int cuotasPorAnio)
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();

        var result = await h.Sut.CreateAsync(
            Request(entity.EntityId, installment: 100_000m) with { CuotasPorAnio = cuotasPorAnio }, CancellationToken.None);

        var problem = AssertCoded(result, 400, "validation_error");
        var errors = JsonSerializer.Serialize(problem.Extensions["errors"],
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Assert.Contains("cuotasPorAnio", errors);
        Assert.Contains("La periodicidad de la cuota debe ser 1, 2, 4 o 12 cuotas por año.", errors);
        Assert.Empty(h.Db.Analyses.AsNoTracking());
    }

    [Fact]
    public async Task Create_AnAgroActivity_StoresTheTenantsPeriodicity_AndExposesTheMode()
    {
        var h = new Harness();
        var (entity, legajo) = h.PersonWithReadyLegajo();
        WithArcaActivity(legajo, "11111");

        var created = AssertCreated(await h.Sut.CreateAsync(Request(entity.EntityId, installment: 600_000m), CancellationToken.None));

        var row = Assert.Single(h.Db.AnalysisParticipants.AsNoTracking());
        Assert.Equal((byte)2, row.CuotasPorAnio);
        Assert.Null(row.AgroModeOverride);

        var evaluation = created.PersonEvaluation!;
        Assert.Equal(2, evaluation.CuotasPorAnio);
        Assert.NotNull(evaluation.AgroMode);
        Assert.True(evaluation.AgroMode.Active);
        Assert.Equal(PersonAgroModeOrigins.Actividad, evaluation.AgroMode.Origin);
        Assert.Equal("011111", evaluation.AgroMode.Clae);
        Assert.Equal("0.10", evaluation.EvaluationVersion);

        var cuota = Assert.Single(evaluation.Indicators, i => i.Id == "cuota_ingreso");
        Assert.Equal("true", cuota.Inputs["modoAgro"]);
        Assert.Equal("2", cuota.Inputs["cuotasPorAnio"]);
    }

    [Fact]
    public async Task Create_TheAnalystsTerms_AreStoredAsStated()
    {
        var h = new Harness();
        var (entity, legajo) = h.PersonWithReadyLegajo();
        WithArcaActivity(legajo, "011111");

        var created = AssertCreated(await h.Sut.CreateAsync(
            Request(entity.EntityId, installment: 600_000m) with { CuotasPorAnio = 4, AgroMode = false }, CancellationToken.None));

        var row = Assert.Single(h.Db.AnalysisParticipants.AsNoTracking());
        Assert.Equal((byte)4, row.CuotasPorAnio);
        Assert.False(row.AgroModeOverride);
        Assert.False(created.PersonEvaluation!.AgroMode!.Active);
        Assert.Equal(PersonAgroModeOrigins.Analista, created.PersonEvaluation.AgroMode.Origin);
        Assert.False(created.PersonEvaluation.AgroMode.Override);
        Assert.Equal(4, created.PersonEvaluation.CuotasPorAnio);
    }

    [Fact]
    public async Task Create_WithoutAnyAgroTerm_StoresAMonthlyCuota_AndDetects()
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();

        var created = AssertCreated(await h.Sut.CreateAsync(Request(entity.EntityId, installment: 150_000m), CancellationToken.None));

        var row = Assert.Single(h.Db.AnalysisParticipants.AsNoTracking());
        Assert.Equal((byte)12, row.CuotasPorAnio);
        Assert.Null(row.AgroModeOverride);
        Assert.Equal(12, created.PersonEvaluation!.CuotasPorAnio);
        Assert.False(created.PersonEvaluation.AgroMode!.Active);
        Assert.DoesNotContain(created.PersonEvaluation.Indicators, i => i.Inputs.ContainsKey("modoAgro"));
    }

    [Fact]
    public async Task Reevaluate_ReusesTheStoredPeriodicity_EvenWhenTheTenantDefaultChanges()
    {
        var h = new Harness();
        var (entity, legajo) = h.PersonWithReadyLegajo();
        WithArcaActivity(legajo, "011111");
        var created = AssertCreated(await h.Sut.CreateAsync(Request(entity.EntityId, installment: 600_000m), CancellationToken.None));

        h.Db.ScoringConfigs.Add(new ScoringConfig
        {
            ConfigId = Guid.NewGuid(), TenantId = Tenant, ConfigKey = "pf_agro_cuotas_por_anio", ConfigValue = 4m,
            UpdatedAt = DateTime.UtcNow
        });
        await h.Db.SaveChangesAsync();

        Assert.Equal(200, (await ReevaluateAsync(h, created)).StatusCode);

        var row = Assert.Single((await ReadRowsAsync(h)).Participants);
        Assert.Equal((byte)2, row.CuotasPorAnio);
        Assert.Equal(2, Sources(row).Agro!.CuotasPorAnio);
        Assert.Equal(4, Sources(row).Parameters.AgroCuotasPorAnio);
    }

    [Fact]
    public async Task Reevaluate_ReusesTheStoredOverride_WhateverTheActivityBecomes()
    {
        var h = new Harness();
        var (entity, legajo) = h.PersonWithReadyLegajo();
        WithArcaActivity(legajo, "620100");
        var created = AssertCreated(await h.Sut.CreateAsync(
            Request(entity.EntityId, installment: 600_000m) with { AgroMode = true, CuotasPorAnio = 1 }, CancellationToken.None));

        WithArcaActivity(legajo, "011111");
        Assert.Equal(200, (await ReevaluateAsync(h, created)).StatusCode);

        var row = Assert.Single((await ReadRowsAsync(h)).Participants);
        Assert.True(row.AgroModeOverride);
        var agro = Sources(row).Agro!;
        Assert.True(agro.Active);
        Assert.Equal(PersonAgroModeOrigins.Analista, agro.Origin);
        Assert.Equal(1, agro.CuotasPorAnio);
        Assert.Equal("011111", agro.Clae);
    }

    [Fact]
    public async Task Reevaluate_APre078Row_ReadsItsCuotaAsMonthly_AndDetectsTheMode()
    {
        var h = new Harness();
        var (entity, legajo) = h.PersonWithReadyLegajo();
        var created = AssertCreated(await h.Sut.CreateAsync(Request(entity.EntityId, installment: 150_000m), CancellationToken.None));

        // As written before DDL 078: no periodicity, no override.
        var stored = h.Db.AnalysisParticipants.Single();
        stored.CuotasPorAnio = null;
        h.Db.ChangeTracker.DetectChanges();
        await h.Db.SaveChangesAsync();
        WithArcaActivity(legajo, "011111");

        Assert.Equal(200, (await ReevaluateAsync(h, created)).StatusCode);

        var row = Assert.Single((await ReadRowsAsync(h)).Participants);
        Assert.Null(row.CuotasPorAnio);   // the terms are never overwritten
        var agro = Sources(row).Agro!;
        Assert.True(agro.Active);
        Assert.Equal(12, agro.CuotasPorAnio);   // NOT the tenant's agro default of 2
    }

    [Fact]
    public async Task Create_WithAFiador_LeavesTheFiadorRowWithoutAgroTerms()
    {
        var h = new Harness();
        var (entity, legajo) = h.PersonWithReadyLegajo();
        WithArcaActivity(legajo, "011111");
        var (fiador, fiadorLegajo) = AddFiador(h, entity, items: Inmueble(1_000_000m));
        WithArcaActivity(fiadorLegajo!, "011111");

        var request = WithFiadores(entity.EntityId, FiadorOf(fiador.FiadorId)) with { InstallmentAmount = 600_000m, AgroMode = true };
        var created = AssertCreated(await h.Sut.CreateAsync(request, CancellationToken.None));

        var fiadorRow = Assert.Single(h.Db.AnalysisParticipants.AsNoTracking(), p => p.Role == ParticipantRoles.Fiador);
        Assert.Null(fiadorRow.CuotasPorAnio);
        Assert.Null(fiadorRow.AgroModeOverride);
        Assert.Null(Sources(fiadorRow).Agro);
        Assert.DoesNotContain(created.PersonEvaluation!.Fiadores!.Single().Indicators, i => i.Inputs.ContainsKey("modoAgro"));
    }
}
