// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-29

using System.Text.Json;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;
using Api.Web.FinanceIA.Tests.Legajo;
using Api.Web.FinanceIA.Tests.PersonAnalysis;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// POST /api/person-analyses with fiadores (M10 Phase 5a-2): the 400s and 409s, all before anything is
/// written; one frozen Fiador row per fiador with the DDL 077 columns; the BCRA re-query through the
/// legajo-scoped refresh service; the response read through the same reader as GET; and the freeze — a later
/// correction of the fiador's legajo does not move a past analysis. Every CUIT and name is synthetic.
/// </summary>
public sealed partial class PersonAnalysesControllerTests
{
    private const string FiadorCuit  = "27-11223344-5";
    private const string FiadorCuit2 = "20-99887766-1";
    private const string FiadorName  = "FIADOR SINTETICO";

    // ── Fixtures ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// A fiador of <paramref name="entity"/>: by default active, with a legajo that has consent, a Ready
    /// manifestación and the given reconciled items (each counted on that manifestación).
    /// </summary>
    private static (Fiador Fiador, PersonLegajo? Legajo) AddFiador(
        Harness h, Entity entity, string cuit = FiadorCuit, string status = "Active", bool withLegajo = true,
        bool consent = true, bool ready = true, params LegajoItem[] items)
    {
        PersonLegajo? legajo = null;
        if (withLegajo)
        {
            legajo = h.Legajos.AddLegajo(Tenant, consent);
            legajo.CUIT = cuit;
            legajo.FiscalRegime = FiscalRegimes.ResponsableInscripto;

            var manifestacion = h.Legajos.AddDocument(
                legajo, DocumentTypes.ManifestacionBienes, ready ? LegajoStatuses.Ready : LegajoStatuses.Extracting,
                hash: "mb-" + legajo.PersonLegajoId);

            foreach (var item in items)
            {
                item.TenantId              = Tenant;
                item.PersonLegajoId        = legajo.PersonLegajoId;
                item.MarketValueDocumentId = item.MarketValue is null ? null : manifestacion.DocumentId;
                item.FiscalValueDocumentId = null;
                h.Reconciliation.Items.Add(item);
            }
        }

        var fiador = new Fiador
        {
            FiadorId        = Guid.NewGuid(),
            TenantId        = Tenant,
            EntityId        = entity.EntityId,
            CUIT            = cuit,
            Denominacion    = FiadorName,
            Origen          = "Manual",
            Status          = status,
            PersonLegajoId  = legajo?.PersonLegajoId,
            CreatedByUserId = Guid.NewGuid(),
            CreatedAt       = DateTime.UtcNow.AddDays(-5)
        };
        h.Fiadores.Fiadores.Add(fiador);
        return (fiador, legajo);
    }

    private static LegajoItem Inmueble(decimal market, string? partida = null, decimal? pct = null)
        => PersonEvaluationFiadorTests.Asset(LegajoItemKinds.Inmueble, market,
            keyKind: partida is null ? LegajoMatchKeyKinds.Ninguno : LegajoMatchKeyKinds.Partida, key: partida, pct: pct);

    private static CreatePersonAnalysisFiadorRequest FiadorOf(Guid fiadorId, string type = GuaranteeTypes.Fianza) => new()
    {
        FiadorId      = fiadorId,
        GuaranteeType = type
    };

    private static CreatePersonAnalysisRequest WithFiadores(Guid entityId, params CreatePersonAnalysisFiadorRequest[] fiadores)
        => Request(entityId, installment: 150_000m) with { Fiadores = fiadores };

    private static AnalysisResultResponse AssertCreated(IActionResult result)
        => Assert.IsType<AnalysisResultResponse>(Assert.IsType<CreatedResult>(result).Value);

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, PersonEvaluationJson.Options);

    private static void AssertNothingWritten(Harness h)
    {
        Assert.Empty(h.Db.Analyses.AsNoTracking());
        Assert.Empty(h.Db.AnalysisParticipants.AsNoTracking());
        Assert.Empty(h.Refresh.UnusableCalls);
        Assert.Empty(h.Bcra.ForAnalysisCalls);
    }

    // ── Without fiadores: as before ───────────────────────────────────────────────

    [Fact]
    public async Task Create_WithAnEmptyFiadoresList_IsTheSolicitantesAlone()
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();

        var response = AssertCreated(await h.Sut.CreateAsync(WithFiadores(entity.EntityId), CancellationToken.None));

        Assert.Single(h.Db.AnalysisParticipants.AsNoTracking());
        Assert.Null(response.PersonEvaluation!.Fiadores);
        Assert.Null(response.PersonEvaluation.CounterGuaranteeCoverage);
        Assert.Null(response.PersonEvaluation.DecisionRules!.CounterGuaranteeCoverage);
        Assert.False(response.PersonEvaluation.DecisionRules.CounterGuaranteeBelowMin);
        Assert.Empty(h.Refresh.UnusableCalls);
    }

    // ── 400 validation ────────────────────────────────────────────────────────────

    public static TheoryData<string, string> InvalidFiadores => new()
    {
        { "empty_id",       "fiadores[0].fiadorId" },
        { "duplicate",      "fiadores[1].fiadorId" },
        { "unknown_type",   "fiadores[0].guaranteeType" },
        { "missing_type",   "fiadores[0].guaranteeType" },
        { "zero_amount",    "fiadores[0].amountAtCharge" },
        { "above_request",  "fiadores[0].amountAtCharge" },
        { "negative_given", "fiadores[0].guaranteesGivenAmount" },
        { "negative_cuotas", "fiadores[0].cuotasVigentesDeclaradas" },
        { "two_spouses",    "fiadores" },
        { "too_many",       "fiadores" },
    };

    [Theory]
    [MemberData(nameof(InvalidFiadores))]
    public async Task Create_InvalidFiadores_Are400_BeforeAnyRead(string scenario, string field)
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();
        var id = Guid.NewGuid();

        CreatePersonAnalysisFiadorRequest[] fiadores = scenario switch
        {
            "empty_id"       => [FiadorOf(Guid.Empty)],
            "duplicate"      => [FiadorOf(id), FiadorOf(id)],
            "unknown_type"   => [FiadorOf(id, "Aval")],
            "missing_type"   => [FiadorOf(id) with { GuaranteeType = null }],
            "zero_amount"    => [FiadorOf(id) with { AmountAtCharge = 0m }],
            "above_request"  => [FiadorOf(id) with { AmountAtCharge = 2_000_001m }],
            "negative_given" => [FiadorOf(id) with { GuaranteesGivenAmount = -1m }],
            "negative_cuotas" => [FiadorOf(id) with { CuotasVigentesDeclaradas = -0.01m }],
            "two_spouses"    => [FiadorOf(id, GuaranteeTypes.Conyuge), FiadorOf(Guid.NewGuid(), GuaranteeTypes.Conyuge)],
            "too_many"       => Enumerable.Range(0, PersonAnalysesController.MaxFiadores + 1).Select(_ => FiadorOf(Guid.NewGuid())).ToArray(),
            _                => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        var problem = AssertCoded(await h.Sut.CreateAsync(WithFiadores(entity.EntityId, fiadores), CancellationToken.None),
            400, "validation_error");

        var errors = JsonSerializer.Serialize(problem.Extensions["errors"]);
        Assert.Contains($"\"field\":\"{field}\"", errors);
        AssertNothingWritten(h);
    }

    // ── 409 refusals ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_TheSolicitanteAsFiador_Is409FiadorIsSolicitante()
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();
        var (fiador, _) = AddFiador(h, entity, cuit: LegajoTestData.LegajoCuit, items: Inmueble(1_000_000m));

        var problem = AssertCoded(await h.Sut.CreateAsync(WithFiadores(entity.EntityId, FiadorOf(fiador.FiadorId)), CancellationToken.None),
            409, "fiador_is_solicitante");

        Assert.Equal(fiador.FiadorId, problem.Extensions["fiadorId"]);
        AssertNothingWritten(h);
    }

    [Fact]
    public async Task Create_AFiadorOnTheSolicitantesOwnLegajo_Is409FiadorIsSolicitante()
    {
        var h = new Harness();
        var (entity, legajo) = h.PersonWithReadyLegajo();
        var (fiador, _) = AddFiador(h, entity, withLegajo: false);
        fiador.PersonLegajoId = legajo.PersonLegajoId;

        AssertCoded(await h.Sut.CreateAsync(WithFiadores(entity.EntityId, FiadorOf(fiador.FiadorId)), CancellationToken.None),
            409, "fiador_is_solicitante");
        AssertNothingWritten(h);
    }

    [Theory]
    [InlineData("not_found")]
    [InlineData("other_entity")]
    [InlineData("other_tenant")]
    [InlineData("not_active")]
    [InlineData("no_legajo")]
    [InlineData("consent_required")]
    [InlineData("no_ready_document")]
    public async Task Create_AFiadorNotReady_Is409_NamingTheFiadorAndWhy(string scenario)
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();
        var other = h.AddEntity(PersonTypes.Juridica, null);

        var fiadorId = scenario switch
        {
            "not_found"         => Guid.NewGuid(),
            "other_entity"      => AddFiador(h, other, items: Inmueble(1_000_000m)).Fiador.FiadorId,
            "other_tenant"      => SetTenant(AddFiador(h, entity, items: Inmueble(1_000_000m)).Fiador, LegajoTestData.OtherTenant),
            "not_active"        => AddFiador(h, entity, status: "Removed", items: Inmueble(1_000_000m)).Fiador.FiadorId,
            "no_legajo"         => AddFiador(h, entity, withLegajo: false).Fiador.FiadorId,
            "consent_required"  => AddFiador(h, entity, consent: false, items: Inmueble(1_000_000m)).Fiador.FiadorId,
            "no_ready_document" => AddFiador(h, entity, ready: false, items: Inmueble(1_000_000m)).Fiador.FiadorId,
            _                   => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        var expectedReason = scenario is "other_entity" or "other_tenant" ? "not_found" : scenario;

        var problem = AssertCoded(await h.Sut.CreateAsync(WithFiadores(entity.EntityId, FiadorOf(fiadorId)), CancellationToken.None),
            409, "fiador_legajo_not_ready");

        Assert.Equal(fiadorId, problem.Extensions["fiadorId"]);
        Assert.Equal(expectedReason, problem.Extensions["reason"]);
        AssertNothingWritten(h);

        static Guid SetTenant(Fiador fiador, string tenant)
        {
            fiador.TenantId = tenant;
            return fiador.FiadorId;
        }
    }

    [Fact]
    public async Task Create_TheSecondFiadorNotReady_RefusesTheWholeRequest()
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();
        var (ready, _)  = AddFiador(h, entity, items: Inmueble(1_000_000m));
        var (notReady, _) = AddFiador(h, entity, cuit: FiadorCuit2, consent: false, items: Inmueble(1_000_000m));

        var problem = AssertCoded(await h.Sut.CreateAsync(
            WithFiadores(entity.EntityId, FiadorOf(ready.FiadorId), FiadorOf(notReady.FiadorId)), CancellationToken.None),
            409, "fiador_legajo_not_ready");

        Assert.Equal(notReady.FiadorId, problem.Extensions["fiadorId"]);
        AssertNothingWritten(h);
    }

    // ── Rows written and the response ─────────────────────────────────────────────

    [Fact]
    public async Task Create_WritesOneFrozenFiadorRow_WithThe077Columns()
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();
        var (fiador, fiadorLegajo) = AddFiador(h, entity, items: Inmueble(6_000_000m));

        var request = WithFiadores(entity.EntityId, FiadorOf(fiador.FiadorId) with
        {
            SpouseConsent         = true,
            HousingInopposable    = false,
            GuaranteesGivenAmount = 100_000m
        });
        var response = AssertCreated(await h.Sut.CreateAsync(request, CancellationToken.None));

        var rows = h.Db.AnalysisParticipants.AsNoTracking().ToList();
        Assert.Equal(2, rows.Count);
        var solicitante = Assert.Single(rows, r => r.Role == ParticipantRoles.Solicitante);
        Assert.Null(solicitante.FiadorId);
        Assert.Null(solicitante.GuaranteeType);

        var row = Assert.Single(rows, r => r.Role == ParticipantRoles.Fiador);
        Assert.Equal(solicitante.AnalysisId, row.AnalysisId);
        Assert.Equal(fiador.FiadorId, row.FiadorId);
        Assert.Equal(fiadorLegajo!.PersonLegajoId, row.PersonLegajoId);
        Assert.Equal(2_000_000m, row.AmountAtCharge);   // defaults to the requested amount
        Assert.Equal("ARS", row.Currency);
        Assert.Equal(150_000m, row.InstallmentAmount);
        Assert.Equal(GuaranteeTypes.Fianza, row.GuaranteeType);
        Assert.True(row.SpouseConsent);
        Assert.False(row.HousingInopposable);
        Assert.Equal(100_000m, row.GuaranteesGivenAmount);
        Assert.Equal(PersonEvaluationService.Version, row.EvaluationVersion);
        Assert.Null(row.ScoreJson);

        var coverage = PersonEvaluationJson.TryDeserialize<FiadorCoverageResult>(row.CoverageJson)!;
        Assert.Equal(3_000_000m, coverage.GrossExecutable);
        Assert.Equal(2_900_000m, coverage.ExecutablePatrimony);
        var sources = PersonEvaluationJson.TryDeserialize<PersonEvaluationSources>(row.DocumentVersionsJson)!;
        Assert.Null(sources.LufeDataUpdatedAt);
        Assert.Single(sources.Documents, d => d.DocumentType == DocumentTypes.ManifestacionBienes);

        var evaluation = response.PersonEvaluation!;
        var fiadorView = Assert.Single(evaluation.Fiadores!);
        Assert.Equal(fiador.FiadorId, fiadorView.FiadorId);
        Assert.Equal(FiadorName, fiadorView.Name);
        Assert.Equal(GuaranteeTypes.Fianza, fiadorView.GuaranteeType);
        Assert.Equal(PersonSuggestions.ParaDecisionDelAnalista, fiadorView.Suggestion);
        Assert.Equal(5, fiadorView.Filters.Count);
        Assert.Equal(["cobertura_patrimonial_fiador", "cuota_ingreso_estres"], fiadorView.Indicators.Select(i => i.Id));
        Assert.Single(fiadorView.Coverage!.Assets);

        var aggregate = evaluation.CounterGuaranteeCoverage!;
        Assert.Equal(1, aggregate.FiadoresCount);
        Assert.Equal(2_000_000m, aggregate.ExecutableTotal);   // capped at the amount at charge
        Assert.Equal(1m, aggregate.Ratio);
        Assert.True(aggregate.MeetsMin);
        Assert.Same(aggregate, evaluation.DecisionRules!.CounterGuaranteeCoverage);
        Assert.False(evaluation.DecisionRules.CounterGuaranteeBelowMin);
    }

    [Fact]
    public async Task Create_BelowTheMinimum_RequiresAReason_AndSaysSo()
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();
        var (fiador, _) = AddFiador(h, entity, items: Inmueble(1_000_000m));
        h.Db.ScoringConfigs.Add(new ScoringConfig
        {
            ConfigId = Guid.NewGuid(), TenantId = Tenant, ConfigKey = "pf_aprobacion_requiere_motivo", ConfigValue = 0m
        });
        h.Db.ChangeTracker.DetectChanges();
        await h.Db.SaveChangesAsync();

        var response = AssertCreated(await h.Sut.CreateAsync(WithFiadores(entity.EntityId, FiadorOf(fiador.FiadorId)), CancellationToken.None));

        var rules = response.PersonEvaluation!.DecisionRules!;
        Assert.Equal(0.25m, rules.CounterGuaranteeCoverage!.Ratio);
        Assert.False(rules.CounterGuaranteeCoverage.MeetsMin);
        Assert.True(rules.CounterGuaranteeBelowMin);
        Assert.True(rules.RequiresReason);   // although pf_aprobacion_requiere_motivo = 0
    }

    [Fact]
    public async Task Create_EachFiadorGetsTheUnusableBcraRequery_AndReadsItsAnswer()
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();
        var (fiador, fiadorLegajo) = AddFiador(h, entity, items: Inmueble(1_000_000m));
        h.Refresh.OnUnusable = legajo =>
        {
            legajo.BcraDataJson = JsonSerializer.Serialize(BcraSnapshot(
                BcraDeudaConsultaEstado.NoDebtReported, BcraChequesConsultaEstado.NoChequesReported, DateTime.UtcNow));
            return LegajoSnapshotRefreshStatus.Refreshed;
        };

        var response = AssertCreated(await h.Sut.CreateAsync(WithFiadores(entity.EntityId, FiadorOf(fiador.FiadorId)), CancellationToken.None));

        Assert.Equal([fiadorLegajo!.PersonLegajoId], h.Refresh.UnusableCalls);
        var filter = Assert.Single(response.PersonEvaluation!.Fiadores![0].Filters, f => f.Id == "bcra_situacion");
        Assert.Equal(PersonFilterOutcomes.Ok, filter.Outcome);
    }

    [Fact]
    public async Task Create_AFiadorWithoutBcra_ReadsSinDatos_PointingToHisLegajo()
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();
        var (fiador, _) = AddFiador(h, entity, items: Inmueble(1_000_000m));
        h.Refresh.OnUnusable = _ => LegajoSnapshotRefreshStatus.BcraUnavailable;

        var response = AssertCreated(await h.Sut.CreateAsync(WithFiadores(entity.EntityId, FiadorOf(fiador.FiadorId)), CancellationToken.None));

        var filter = Assert.Single(response.PersonEvaluation!.Fiadores![0].Filters, f => f.Id == "bcra_situacion");
        Assert.Equal(PersonFilterOutcomes.SinDatos, filter.Outcome);
        Assert.Contains("Actualizá BCRA en el legajo del fiador.", filter.Evidence);
    }

    [Fact]
    public async Task Create_TwoFiadoresSharingAProperty_CountItOnce()
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();
        var (a, _) = AddFiador(h, entity, items: Inmueble(2_000_000m, partida: "P-1"));
        var (b, _) = AddFiador(h, entity, cuit: FiadorCuit2, items: Inmueble(1_600_000m, partida: "P-1"));

        var response = AssertCreated(await h.Sut.CreateAsync(
            WithFiadores(entity.EntityId, FiadorOf(a.FiadorId), FiadorOf(b.FiadorId)), CancellationToken.None));

        var aggregate = response.PersonEvaluation!.CounterGuaranteeCoverage!;
        Assert.Equal(2, aggregate.FiadoresCount);
        Assert.Equal(1_000_000m, aggregate.ExecutableTotal);
        Assert.Equal(0.5m, aggregate.Ratio);
        Assert.Single(aggregate.Contributions, c => c.DuplicateItemIds.Count == 1);
    }

    // ── Frozen, and one reader ────────────────────────────────────────────────────

    [Fact]
    public async Task Create_IsFrozen_AndTheDetailReaderReturnsTheSameCoverage()
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();
        var (fiador, _) = AddFiador(h, entity, items: Inmueble(1_000_000m));
        h.Db.Fiadores.Add(fiador);
        h.Db.ChangeTracker.DetectChanges();
        await h.Db.SaveChangesAsync();

        var response = AssertCreated(await h.Sut.CreateAsync(WithFiadores(entity.EntityId, FiadorOf(fiador.FiadorId)), CancellationToken.None));

        // The analyst corrects the fiador's legajo afterwards: a new asset and a higher value.
        h.Reconciliation.Items.Single(i => i.PersonLegajoId == fiador.PersonLegajoId).MarketValue = 9_000_000m;
        h.Reconciliation.Items.Add(new LegajoItem
        {
            ItemId = Guid.NewGuid(), TenantId = Tenant, PersonLegajoId = fiador.PersonLegajoId!.Value,
            Kind = LegajoItemKinds.Rodado, MarketValue = 5_000_000m
        });

        var repository = new PersonAnalysisRepository(h.Db, NullLogger<PersonAnalysisRepository>.Instance);
        var (rules, solicitante, fiadores) = await PersonEvaluationJson.ResolveSolicitanteAsync(
            repository, response.AnalysisId, Tenant, CancellationToken.None);
        var detail = PersonEvaluationJson.ToDetailResponse(rules, solicitante, fiadores)!;

        Assert.Equal(Json(response.PersonEvaluation!.CounterGuaranteeCoverage), Json(detail.CounterGuaranteeCoverage));
        Assert.Equal(0.25m, detail.CounterGuaranteeCoverage!.Ratio);
        Assert.Equal(Json(response.PersonEvaluation.Fiadores![0].Coverage), Json(detail.Fiadores![0].Coverage));
        Assert.Equal(FiadorName, detail.Fiadores[0].Name);
    }

    // ── Repository ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Repository_RefusesAFiadorRowThatDoesNotNameItsFiador()
    {
        var h = new Harness();
        var repo = new PersonAnalysisRepository(h.Db, NullLogger<PersonAnalysisRepository>.Instance);
        var analysis = new Analysis { AnalysisId = Guid.NewGuid(), TenantId = Tenant, AnalysisKind = AnalysisKinds.PersonaHumana };

        AnalysisParticipant Participant(string role, Guid? fiadorId) => new()
        {
            ParticipantId = Guid.NewGuid(), AnalysisId = analysis.AnalysisId, TenantId = Tenant, Role = role, FiadorId = fiadorId
        };

        await Assert.ThrowsAsync<ArgumentException>(() => repo.CreateAsync(
            analysis, new AnalysisResult { AnalysisId = analysis.AnalysisId, TenantId = Tenant },
            Participant(ParticipantRoles.Solicitante, null), [Participant(ParticipantRoles.Fiador, null)], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => repo.CreateAsync(
            analysis, new AnalysisResult { AnalysisId = analysis.AnalysisId, TenantId = Tenant },
            Participant(ParticipantRoles.Solicitante, null), [Participant(ParticipantRoles.Solicitante, Guid.NewGuid())], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => repo.CreateAsync(
            analysis, new AnalysisResult { AnalysisId = analysis.AnalysisId, TenantId = Tenant },
            Participant(ParticipantRoles.Fiador, Guid.NewGuid()), [], CancellationToken.None));

        Assert.Empty(h.Db.Analyses.AsNoTracking());
    }

    [Fact]
    public async Task Repository_FiadorIdentities_AreTenantScoped()
    {
        var h = new Harness();
        var entity = h.AddEntity(PersonTypes.Juridica, null);
        var (mine, _)   = AddFiador(h, entity, withLegajo: false);
        var (theirs, _) = AddFiador(h, entity, cuit: FiadorCuit2, withLegajo: false);
        theirs.TenantId = LegajoTestData.OtherTenant;
        h.Db.Fiadores.AddRange(mine, theirs);
        h.Db.ChangeTracker.DetectChanges();
        await h.Db.SaveChangesAsync();

        var identities = await new PersonAnalysisRepository(h.Db, NullLogger<PersonAnalysisRepository>.Instance)
            .GetFiadorIdentitiesAsync([mine.FiadorId, theirs.FiadorId, Guid.NewGuid()], Tenant, CancellationToken.None);

        var identity = Assert.Single(identities, n => n.Key == mine.FiadorId).Value;
        Assert.Equal(FiadorName, identity.Name);
        Assert.Equal(mine.CUIT, identity.Cuit);
        Assert.Single(identities);
    }

    // ── Logs ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_LogsCarryIdsOnly_NeverTheFiadorsNameOrCuit()
    {
        var logger = new RecordingLogger();
        var h = new Harness(logger);
        var (entity, _) = h.PersonWithReadyLegajo();
        var (fiador, _) = AddFiador(h, entity, items: Inmueble(1_000_000m));

        AssertCreated(await h.Sut.CreateAsync(WithFiadores(entity.EntityId, FiadorOf(fiador.FiadorId)), CancellationToken.None));

        Assert.Contains(logger.Messages, m => m.Contains(fiador.FiadorId.ToString()));
        Assert.DoesNotContain(logger.Messages, m => m.Contains(FiadorName) || m.Contains(FiadorCuit)
                                                 || m.Contains(FiadorCuit.Replace("-", string.Empty)));
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The legajo-scoped refresh, recorded: <see cref="RefreshUnusableBcraAsync"/> returns the legajo as stored,
    /// with the status <see cref="OnUnusable"/> picks (it may also change the legajo, as a real refresh would);
    /// NotNeeded by default. The real service's semantics are PersonLegajoUnusableBcraRefreshTests'.
    /// </summary>
    private sealed class RecordingSnapshotRefreshService(FakeLegajoDocumentRepository legajos) : IPersonLegajoSnapshotRefreshService
    {
        public List<Guid> UnusableCalls { get; } = [];
        public Func<PersonLegajo, LegajoSnapshotRefreshStatus>? OnUnusable { get; set; }

        public Task<LegajoSnapshotRefreshOutcome> RefreshUnusableBcraAsync(Guid personLegajoId, string tenantId, CancellationToken ct)
        {
            UnusableCalls.Add(personLegajoId);
            var legajo = legajos.Legajos.Single(l => l.PersonLegajoId == personLegajoId && l.TenantId == tenantId);
            var status = OnUnusable?.Invoke(legajo) ?? LegajoSnapshotRefreshStatus.NotNeeded;
            return Task.FromResult(new LegajoSnapshotRefreshOutcome(status, legajo));
        }

        public Task<LegajoSnapshotRefreshOutcome> RefreshBcraAsync(Guid personLegajoId, string tenantId, CancellationToken ct)
            => throw new NotSupportedException("the evaluation never forces a refresh");

        public Task<LegajoSnapshotRefreshOutcome> RefreshArcaAsync(Guid personLegajoId, string tenantId, string? cuitRepresentada, CancellationToken ct)
            => throw new NotSupportedException("the evaluation never refreshes ARCA");
    }

    private sealed class RecordingLogger : ILogger<PersonAnalysesController>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
