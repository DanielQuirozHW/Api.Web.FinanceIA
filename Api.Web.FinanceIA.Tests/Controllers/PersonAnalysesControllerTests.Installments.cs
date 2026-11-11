// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-30

using System.Text.Json;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Engine 0.8 through POST /api/person-analyses and "Reanalizar": the optional cuotasVigentesDeclaradas of the
/// solicitante and of each fiador, its validation, the row storing it as stated (DDL 079, NULL when absent, zero
/// kept), the detail echoing it, a re-evaluation reusing it, and a pre-079 row (NULL) estimating as before.
/// </summary>
public sealed partial class PersonAnalysesControllerTests
{
    /// <summary>A usable BCRA snapshot reporting 2.400 thousand of debt: the estimate is 5 % of it, 120.000 a month.</summary>
    private static void WithDebt(PersonLegajo legajo)
    {
        var fetchedAt = DateTime.UtcNow.AddDays(-1);
        legajo.BcraDataJson = JsonSerializer.Serialize(new BcraData
        {
            Deuda = new BcraDeudaData
            {
                Estado = BcraDeudaConsultaEstado.DebtFound, SituacionMaxima = 1, MontoTotal = 2_400m, PeriodoConsultado = "202609",
                FetchedAt = fetchedAt, EntidadesDetalle = [], HistorialSituaciones = []
            },
            Cheques = new BcraChequesData { Estado = BcraChequesConsultaEstado.NoChequesReported, FetchedAt = fetchedAt }
        });
        legajo.BcraDataUpdatedAt = fetchedAt;
    }

    private static PersonIndicatorResult CuotaIngresoOf(AnalysisResultResponse response)
        => Assert.Single(response.PersonEvaluation!.Indicators, i => i.Id == "cuota_ingreso");

    [Theory]
    [InlineData(-1)]
    [InlineData(-0.01)]
    public async Task Create_NegativeDeclaredInstallments_Is400ValidationError(double cuotas)
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();

        var result = await h.Sut.CreateAsync(
            Request(entity.EntityId, installment: 400_000m) with { CuotasVigentesDeclaradas = (decimal)cuotas }, CancellationToken.None);

        var problem = AssertCoded(result, 400, "validation_error");
        var errors = JsonSerializer.Serialize(problem.Extensions["errors"],
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Assert.Contains("\"field\":\"cuotasVigentesDeclaradas\"", errors);
        Assert.Contains("Las cuotas vigentes declaradas no pueden ser negativas.", errors);
        Assert.Empty(h.Db.Analyses.AsNoTracking());
    }

    [Fact]
    public async Task Create_DeclaredInstallments_AreStored_EchoedAndReplaceTheEstimate()
    {
        var h = new Harness();
        var (entity, legajo) = h.PersonWithReadyLegajo();
        WithDebt(legajo);

        var created = AssertCreated(await h.Sut.CreateAsync(
            Request(entity.EntityId, installment: 400_000m) with { CuotasVigentesDeclaradas = 50_000m }, CancellationToken.None));

        var row = Assert.Single(h.Db.AnalysisParticipants.AsNoTracking());
        Assert.Equal(50_000m, row.ExistingInstallmentsAmount);
        Assert.Equal(50_000m, created.PersonEvaluation!.CuotasVigentesDeclaradas);

        // (400.000 + 50.000) / (12M / 12) = 0,45; the estimate 120.000 is kept beside it.
        var cuota = CuotaIngresoOf(created);
        Assert.Equal(0.45m, cuota.Value);
        Assert.Equal(CuotasVigentesOrigenes.Declarada, cuota.Inputs["cuotasVigentesOrigen"]);
        Assert.Equal("120000", cuota.Inputs["cuotasVigentesEstimadas"]);
    }

    [Fact]
    public async Task Create_WithoutADeclaration_StoresNull_AndEstimatesAsBefore()
    {
        var h = new Harness();
        var (entity, legajo) = h.PersonWithReadyLegajo();
        WithDebt(legajo);

        var created = AssertCreated(await h.Sut.CreateAsync(Request(entity.EntityId, installment: 400_000m), CancellationToken.None));

        Assert.Null(Assert.Single(h.Db.AnalysisParticipants.AsNoTracking()).ExistingInstallmentsAmount);
        Assert.Null(created.PersonEvaluation!.CuotasVigentesDeclaradas);
        var cuota = CuotaIngresoOf(created);
        Assert.Equal(0.52m, cuota.Value);
        Assert.Equal(CuotasVigentesOrigenes.Estimada, cuota.Inputs["cuotasVigentesOrigen"]);
    }

    [Fact]
    public async Task Create_ZeroDeclared_IsStoredAsZero_NotAsAbsent()
    {
        var h = new Harness();
        var (entity, legajo) = h.PersonWithReadyLegajo();
        WithDebt(legajo);

        var created = AssertCreated(await h.Sut.CreateAsync(
            Request(entity.EntityId, installment: 400_000m) with { CuotasVigentesDeclaradas = 0m }, CancellationToken.None));

        Assert.Equal(0m, Assert.Single(h.Db.AnalysisParticipants.AsNoTracking()).ExistingInstallmentsAmount);
        Assert.Equal(0.40m, CuotaIngresoOf(created).Value);
        Assert.Equal(CuotasVigentesOrigenes.Declarada, CuotaIngresoOf(created).Inputs["cuotasVigentesOrigen"]);
    }

    [Fact]
    public async Task Create_PerFiador_StoresHisOwnDeclaration_AndAReevaluationReusesBoth()
    {
        var h = new Harness();
        var (entity, legajo) = h.PersonWithReadyLegajo();
        WithDebt(legajo);
        var (fiador, fiadorLegajo) = AddFiador(h, entity, items: Inmueble(6_000_000m));
        WithDebt(fiadorLegajo!);
        h.Reconciliation.Incomes.Add(new LegajoIncome
        {
            IncomeId = Guid.NewGuid(), TenantId = Tenant, PersonLegajoId = fiadorLegajo!.PersonLegajoId,
            Period = "2025", PeriodFrom = new DateOnly(2025, 1, 1), PeriodTo = new DateOnly(2025, 12, 31),
            Source = LegajoIncomeSources.Ganancias, Concept = LegajoIncomeConcepts.Renta3ra, Amount = 12_000_000m,
            // On his live manifestación: the evaluation reads only what a live document supports.
            DocumentId = h.Legajos.Documents.Single(d => d.PersonLegajoId == fiadorLegajo.PersonLegajoId
                                                          && d.DocumentType == DocumentTypes.ManifestacionBienes).DocumentId,
            ExtractionId = Guid.NewGuid()
        });

        var request = WithFiadores(entity.EntityId, FiadorOf(fiador.FiadorId) with { CuotasVigentesDeclaradas = 30_000m });
        var created = AssertCreated(await h.Sut.CreateAsync(request, CancellationToken.None));

        var rows = h.Db.AnalysisParticipants.AsNoTracking().ToList();
        Assert.Null(Assert.Single(rows, r => r.Role == ParticipantRoles.Solicitante).ExistingInstallmentsAmount);
        Assert.Equal(30_000m, Assert.Single(rows, r => r.Role == ParticipantRoles.Fiador).ExistingInstallmentsAmount);

        // (150.000 + 30.000) / 1.000.000 = 0,18 for the fiador; the solicitante still estimates.
        var fiadorView = Assert.Single(created.PersonEvaluation!.Fiadores!);
        Assert.Equal(30_000m, fiadorView.CuotasVigentesDeclaradas);
        var stress = Assert.Single(fiadorView.Indicators, i => i.Id == "cuota_ingreso_estres");
        Assert.Equal(0.18m, stress.Value);
        Assert.Equal(CuotasVigentesOrigenes.Declarada, stress.Inputs["cuotasVigentesOrigen"]);
        Assert.Equal(CuotasVigentesOrigenes.Estimada, CuotaIngresoOf(created).Inputs["cuotasVigentesOrigen"]);

        Assert.Equal(200, (await ReevaluateAsync(h, created)).StatusCode);

        var after = (await ReadRowsAsync(h)).Participants;
        var fiadorRow = Assert.Single(after, r => r.Role == ParticipantRoles.Fiador);
        Assert.Equal(30_000m, fiadorRow.ExistingInstallmentsAmount);
        var reevaluated = Indicator(fiadorRow, "cuota_ingreso_estres");
        Assert.Equal(0.18m, reevaluated.Value);
        Assert.Equal(CuotasVigentesOrigenes.Declarada, reevaluated.Inputs["cuotasVigentesOrigen"]);
    }

    [Fact]
    public async Task Reevaluate_ReusesTheStoredDeclaration()
    {
        var h = new Harness();
        var (entity, legajo) = h.PersonWithReadyLegajo();
        WithDebt(legajo);
        var created = AssertCreated(await h.Sut.CreateAsync(
            Request(entity.EntityId, installment: 400_000m) with { CuotasVigentesDeclaradas = 50_000m }, CancellationToken.None));

        Assert.Equal(200, (await ReevaluateAsync(h, created)).StatusCode);

        var row = Assert.Single((await ReadRowsAsync(h)).Participants);
        Assert.Equal(50_000m, row.ExistingInstallmentsAmount);   // the terms are never overwritten
        var cuota = Indicator(row, "cuota_ingreso");
        Assert.Equal(0.45m, cuota.Value);
        Assert.Equal(CuotasVigentesOrigenes.Declarada, cuota.Inputs["cuotasVigentesOrigen"]);
    }

    [Fact]
    public async Task Reevaluate_APre079Row_EstimatesAsBefore()
    {
        var h = new Harness();
        var (entity, legajo) = h.PersonWithReadyLegajo();
        WithDebt(legajo);
        var created = AssertCreated(await h.Sut.CreateAsync(Request(entity.EntityId, installment: 400_000m), CancellationToken.None));

        // As written before DDL 079: no declaration.
        Assert.Null(h.Db.AnalysisParticipants.Single().ExistingInstallmentsAmount);

        Assert.Equal(200, (await ReevaluateAsync(h, created)).StatusCode);

        var row = Assert.Single((await ReadRowsAsync(h)).Participants);
        Assert.Null(row.ExistingInstallmentsAmount);
        var cuota = Indicator(row, "cuota_ingreso");
        Assert.Equal(0.52m, cuota.Value);
        Assert.Equal(CuotasVigentesOrigenes.Estimada, cuota.Inputs["cuotasVigentesOrigen"]);
        Assert.False(cuota.Inputs.ContainsKey("cuotasVigentesDeclaradas"));
    }
}
