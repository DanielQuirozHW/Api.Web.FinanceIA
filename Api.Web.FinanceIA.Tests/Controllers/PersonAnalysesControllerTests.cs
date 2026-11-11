// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-20

using System.Reflection;
using System.Security.Claims;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Clases.Filters;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;
using Api.Web.FinanceIA.Tests.AnalysisRuns;
using Api.Web.FinanceIA.Tests.Legajo;
using Api.Web.FinanceIA.Tests.PersonAnalysis;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// POST /api/person-analyses (M10 Phase 4a): the guards (consent, readiness, entity kind, an open
/// analysis, validation), the three rows written as one unit through the real repository on EF InMemory,
/// the frozen DocumentVersionsJson, the quota gate, and tenant isolation.
/// </summary>
public sealed partial class PersonAnalysesControllerTests
{
    private const string Tenant = LegajoTestData.Tenant;

    // ── Happy path ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_WritesTheAnalysisResultAndParticipant_WithNoDocumentAndNoScore()
    {
        var h = new Harness();
        var (entity, legajo) = h.PersonWithReadyLegajo();

        var result = await h.Sut.CreateAsync(Request(entity.EntityId, installment: 150_000m), CancellationToken.None);

        var created  = Assert.IsType<CreatedResult>(result);
        var response = Assert.IsType<AnalysisResultResponse>(created.Value);
        Assert.Equal(AnalysisKinds.PersonaHumana, response.AnalysisKind);
        Assert.Equal(AnalysisStatuses.PendingReview, response.Status);
        Assert.Equal(Guid.Empty, response.DocumentId);
        Assert.Null(response.AiSuggestedScore);
        Assert.Null(response.AiSuggestedLevel);
        Assert.NotNull(response.PersonEvaluation);
        Assert.Equal($"/api/analyses/{response.ResultId}", created.Location);

        var analysis = Assert.Single(h.Db.Analyses.AsNoTracking());
        Assert.Null(analysis.DocumentId);
        Assert.Equal(AnalysisKinds.PersonaHumana, analysis.AnalysisKind);
        Assert.Equal(entity.EntityId, analysis.EntityId);
        Assert.Equal(h.Users.Analyst.UserId, analysis.RequestedBy);
        Assert.Equal(PersonAnalysesController.NoModel, analysis.AiModel);
        Assert.Equal(0.00m, analysis.AiTemperature);
        Assert.Equal("pf-0.10", analysis.PromptVersion);

        var stored = Assert.Single(h.Db.AnalysisResults.AsNoTracking());
        Assert.Equal(analysis.AnalysisId, stored.AnalysisId);
        Assert.Null(stored.BalanceJson);
        Assert.Null(stored.RatiosJson);
        Assert.Null(stored.AiSuggestedScore);
        Assert.Null(stored.ExecutiveSummary);
        Assert.Null(stored.ScoreBreakdownJson);
        Assert.Equal(64, stored.AiInputHash.Length);
        Assert.Equal(64, stored.AiOutputHash.Length);

        var participant = Assert.Single(h.Db.AnalysisParticipants.AsNoTracking());
        Assert.Equal(legajo.PersonLegajoId, participant.PersonLegajoId);
        Assert.Equal(ParticipantRoles.Solicitante, participant.Role);
        Assert.Equal(2_000_000m, participant.AmountAtCharge);
        Assert.Equal(150_000m, participant.InstallmentAmount);
        Assert.Null(participant.ScoreJson);
        Assert.Equal(PersonEvaluationService.Version, participant.EvaluationVersion);
    }

    [Fact]
    public async Task Create_FreezesLufe_ReadsTheCertificate_AndExposesTheDecisionRules()
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();
        entity.LufeDataJson      = """{"Entidad":{"certificado_pyme":{"categoria":"Micro","hasta":"2099-04-30"}}}""";
        entity.LufeDataUpdatedAt = DateTime.UtcNow.AddDays(-3);

        var result = await h.Sut.CreateAsync(Request(entity.EntityId), CancellationToken.None);

        var response   = Assert.IsType<AnalysisResultResponse>(Assert.IsType<CreatedResult>(result).Value);
        var evaluation = response.PersonEvaluation!;
        Assert.Equal("certificado_mipyme", evaluation.Filters[0].Id);
        Assert.Equal(PersonFilterOutcomes.Ok, evaluation.Filters[0].Outcome);
        Assert.Equal(PersonEvidenceKinds.Verificado, evaluation.Filters[0].EvidenceKind);

        var rules = evaluation.DecisionRules!;
        Assert.True(rules.RequiresReason);
        Assert.Equal(PersonDecisionRules.Confirmations, rules.RequiredConfirmations);

        var stored = Assert.Single(h.Db.AnalysisResults.AsNoTracking());
        Assert.Equal(entity.LufeDataJson, stored.LufeDataJson);

        var participant = Assert.Single(h.Db.AnalysisParticipants.AsNoTracking());
        var sources = PersonEvaluationJson.TryDeserialize<PersonEvaluationSources>(participant.DocumentVersionsJson)!;
        Assert.Equal(entity.LufeDataUpdatedAt, sources.LufeDataUpdatedAt);
    }

    [Fact]
    public async Task Create_FreezesTheExactExtractionRunsAndCorrections()
    {
        var h = new Harness();
        var (entity, legajo) = h.PersonWithReadyLegajo();
        var ganancias = h.Legajos.Documents.Single(d => d.DocumentType == DocumentTypes.Ganancias);
        var run = h.Legajos.Runs.Single(r => r.DocumentId == ganancias.DocumentId);
        var correction = new LegajoCorrection
        {
            CorrectionId = Guid.NewGuid(), TenantId = Tenant, ExtractionId = run.ExtractionId,
            FieldPath = "/totals/x", Reason = "ajuste", CorrectedAt = DateTime.UtcNow.AddHours(-1)
        };
        h.Legajos.Corrections.Add(correction);

        await h.Sut.CreateAsync(Request(entity.EntityId), CancellationToken.None);

        var participant = Assert.Single(h.Db.AnalysisParticipants.AsNoTracking());
        var sources = PersonEvaluationJson.TryDeserialize<PersonEvaluationSources>(participant.DocumentVersionsJson)!;
        var pinned = Assert.Single(sources.Documents, d => d.DocumentId == ganancias.DocumentId);
        Assert.Equal(run.ExtractionId, pinned.ExtractionId);
        Assert.Equal([correction.CorrectionId], pinned.CorrectionIds);

        // A later correction of the legajo does not touch the stored analysis.
        var frozen = participant.DocumentVersionsJson;
        h.Legajos.Corrections.Add(new LegajoCorrection
        {
            CorrectionId = Guid.NewGuid(), TenantId = Tenant, ExtractionId = run.ExtractionId,
            FieldPath = "/totals/y", Reason = "otro ajuste", CorrectedAt = DateTime.UtcNow
        });
        Assert.Equal(frozen, Assert.Single(h.Db.AnalysisParticipants.AsNoTracking()).DocumentVersionsJson);
    }

    [Fact]
    public async Task Create_ReadsTheTenantPfParameters()
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();
        h.Db.ScoringConfigs.Add(new ScoringConfig
        {
            ConfigId = Guid.NewGuid(), TenantId = Tenant, ConfigKey = "pf_antiguedad_min_meses",
            ConfigValue = 6m, UpdatedAt = DateTime.UtcNow
        });
        h.Db.ChangeTracker.DetectChanges();
        h.Db.SaveChanges();

        await h.Sut.CreateAsync(Request(entity.EntityId), CancellationToken.None);

        var sources = PersonEvaluationJson.TryDeserialize<PersonEvaluationSources>(
            Assert.Single(h.Db.AnalysisParticipants.AsNoTracking()).DocumentVersionsJson)!;
        Assert.Equal(6, sources.Parameters.AntiguedadMinMeses);
        Assert.Equal(["pf_antiguedad_min_meses"], sources.Parameters.TenantOverrides);
    }

    [Fact]
    public async Task Create_SendsNothingToAModel_AndAuditsWithoutIdentity()
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();

        await h.Sut.CreateAsync(Request(entity.EntityId), CancellationToken.None);

        var audit = Assert.Single(h.Audit.Entries, e => e.Action == "Analysis.PersonEvaluated");
        Assert.DoesNotContain(LegajoTestData.LegajoCuit, audit.NewJson);
        Assert.DoesNotContain(LegajoTestData.LegajoFullName, audit.NewJson);

        var participant = Assert.Single(h.Db.AnalysisParticipants.AsNoTracking());
        foreach (var json in new[] { participant.FiltersJson, participant.IndicatorsJson, participant.CoverageJson, participant.DocumentVersionsJson })
        {
            Assert.DoesNotContain(LegajoTestData.LegajoCuit, json);
            Assert.DoesNotContain(LegajoTestData.LegajoFullName, json);
        }
    }

    [Fact]
    public async Task Create_IgnoresReconciledRowsOfADeletedDocument()
    {
        var h = new Harness();
        var (entity, legajo) = h.PersonWithReadyLegajo();
        var deleted = h.Legajos.AddDocument(legajo, DocumentTypes.DdjjIva, LegajoStatuses.Ready, hash: "iva");
        deleted.IsDeleted = true;
        h.Reconciliation.Incomes.Add(new LegajoIncome
        {
            IncomeId = Guid.NewGuid(), TenantId = Tenant, PersonLegajoId = legajo.PersonLegajoId,
            Period = "2025", PeriodFrom = new DateOnly(2025, 1, 1), PeriodTo = new DateOnly(2025, 12, 31),
            Source = LegajoIncomeSources.Iva, Concept = LegajoIncomeConcepts.Ventas, Amount = 999_000_000m,
            DocumentId = deleted.DocumentId, ExtractionId = Guid.NewGuid()
        });

        await h.Sut.CreateAsync(Request(entity.EntityId), CancellationToken.None);

        var participant = Assert.Single(h.Db.AnalysisParticipants.AsNoTracking());
        Assert.DoesNotContain(deleted.DocumentId.ToString(), participant.IndicatorsJson);
        Assert.DoesNotContain("999000000", participant.IndicatorsJson);
    }

    // ── Guards ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_WithoutConsent_Is409ConsentRequired_AndWritesNothing()
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo(consent: false);

        var result = await h.Sut.CreateAsync(Request(entity.EntityId), CancellationToken.None);

        AssertCoded(result, 409, "consent_required");
        Assert.Empty(h.Db.Analyses.AsNoTracking());
    }

    [Fact]
    public async Task Create_WithOnlyTheConsentReady_Is409LegajoNotReady()
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo(withGanancias: false);

        var result = await h.Sut.CreateAsync(Request(entity.EntityId), CancellationToken.None);

        AssertCoded(result, 409, "legajo_not_ready");
        Assert.Empty(h.Db.Analyses.AsNoTracking());
    }

    [Fact]
    public async Task Create_WhenTheOnlyDocumentIsInReview_Is409LegajoNotReady()
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();
        h.Legajos.Documents.Single(d => d.DocumentType == DocumentTypes.Ganancias).LegajoStatus = LegajoStatuses.NeedsReview;

        AssertCoded(await h.Sut.CreateAsync(Request(entity.EntityId), CancellationToken.None), 409, "legajo_not_ready");
    }

    [Fact]
    public async Task Create_OnACompany_Is400EntityNotPerson()
    {
        var h = new Harness();
        var company = h.AddEntity(PersonTypes.Juridica, legajoId: null);

        AssertCoded(await h.Sut.CreateAsync(Request(company.EntityId), CancellationToken.None), 400, "entity_not_person");
    }

    [Fact]
    public async Task Create_UnknownEntity_Is404()
    {
        var h = new Harness();
        AssertCoded(await h.Sut.CreateAsync(Request(Guid.NewGuid()), CancellationToken.None), 404, "entity_not_found");
    }

    [Fact]
    public async Task Create_AnotherTenantsEntity_Is404_TenantIsolation()
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();
        entity.TenantId = "other-tenant";

        AssertCoded(await h.Sut.CreateAsync(Request(entity.EntityId), CancellationToken.None), 404, "entity_not_found");
        Assert.Empty(h.Db.Analyses.AsNoTracking());
    }

    [Fact]
    public async Task Create_EntityPointingAtAnotherTenantsLegajo_IsRefused()
    {
        var h = new Harness();
        var (entity, legajo) = h.PersonWithReadyLegajo();
        legajo.TenantId = "other-tenant";

        AssertCoded(await h.Sut.CreateAsync(Request(entity.EntityId), CancellationToken.None), 400, "entity_not_person");
        Assert.Empty(h.Db.Analyses.AsNoTracking());
    }

    [Fact]
    public async Task Create_WhileAPersonAnalysisAwaitsItsDecision_Is409()
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();
        Assert.IsType<CreatedResult>(await h.Sut.CreateAsync(Request(entity.EntityId), CancellationToken.None));

        AssertCoded(await h.Sut.CreateAsync(Request(entity.EntityId), CancellationToken.None), 409, "person_analysis_open");
        Assert.Single(h.Db.Analyses.AsNoTracking());
    }

    [Fact]
    public async Task Create_AfterTheEarlierOneWasDecided_IsAllowed()
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();
        await h.Sut.CreateAsync(Request(entity.EntityId), CancellationToken.None);

        var first = h.Db.Analyses.Single();
        first.Status = AnalysisStatuses.Rejected;
        h.Db.ChangeTracker.DetectChanges();
        h.Db.SaveChanges();

        Assert.IsType<CreatedResult>(await h.Sut.CreateAsync(Request(entity.EntityId), CancellationToken.None));
        Assert.Equal(2, h.Db.Analyses.AsNoTracking().Count());
    }

    [Theory]
    [InlineData(0, "ARS", null, "requestedAmount")]
    [InlineData(1000, "XYZ", null, "currency")]
    [InlineData(1000, "ARS", -5, "installmentAmount")]
    public async Task Create_InvalidRequest_Is400ValidationError(int amount, string currency, int? installment, string field)
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();

        var result = await h.Sut.CreateAsync(new CreatePersonAnalysisRequest
        {
            EntityId = entity.EntityId, RequestedAmount = amount, Currency = currency, InstallmentAmount = installment
        }, CancellationToken.None);

        var problem = AssertCoded(result, 400, "validation_error");
        Assert.Contains(field, System.Text.Json.JsonSerializer.Serialize(problem.Extensions["errors"]));
    }

    // ── Quota and roles ────────────────────────────────────────────────────────────

    [Fact]
    public void Create_IsGatedByTheAnalysisQuota_AndRestrictedToAnalysisCreators()
    {
        var method = typeof(PersonAnalysesController).GetMethod(nameof(PersonAnalysesController.CreateAsync))!;

        var quota = Assert.Single(method.GetCustomAttributes<BillingQuotaAttribute>());
        Assert.Equal(BillingQuotaKind.Analysis, Assert.Single(quota.Arguments!));
        Assert.Equal(Roles.AnalysisCreators, Assert.Single(method.GetCustomAttributes<AuthorizeAttribute>()).Roles);
    }

    [Fact]
    public async Task Create_ThePersonaRow_CountsAsOneAnalysisUnit()
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();
        await h.Sut.CreateAsync(Request(entity.EntityId), CancellationToken.None);

        var repo = new AnalysisRepository(h.Db, NullLogger<AnalysisRepository>.Instance);
        var count = await repo.CountByTenantInPeriodAsync(
            Tenant, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1), CancellationToken.None);

        Assert.Equal(1, count);
    }

    // ── Go-live: validation status and the BCRA snapshot ──────────────────────────

    [Fact]
    public async Task Create_ResponseCarriesNoValidationStatus_WhileTheRowStoresTheForcedPassed()
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();

        var result = await h.Sut.CreateAsync(Request(entity.EntityId), CancellationToken.None);

        var response = Assert.IsType<AnalysisResultResponse>(Assert.IsType<CreatedResult>(result).Value);
        Assert.Equal(string.Empty, response.ValidationStatus);
        Assert.Equal(AnalysisStatuses.PendingReview, response.Status);   // nothing is approved
        Assert.Equal(PersonAnalysesController.StoredValidationStatus, Assert.Single(h.Db.AnalysisResults.AsNoTracking()).ValidationStatus);
        Assert.Equal("Analysis.PersonEvaluated", Assert.Single(h.Audit.Entries).Action);
    }

    [Fact]
    public async Task Create_CleanPerson_StoredAsEntitiesControllerStoresIt_ReadsOk_AndBcraIsNotAskedAgain()
    {
        var h = new Harness();
        var (entity, legajo) = h.PersonWithReadyLegajo();
        var fetchedAt = new DateTime(2026, 10, 18, 15, 0, 0, DateTimeKind.Utc);
        // Exactly what EntitiesController writes: the default serializer, enums as numbers.
        legajo.BcraDataJson = System.Text.Json.JsonSerializer.Serialize(
            BcraSnapshot(BcraDeudaConsultaEstado.NoDebtReported, BcraChequesConsultaEstado.NoChequesReported, fetchedAt));
        legajo.BcraDataUpdatedAt = fetchedAt;

        var result = await h.Sut.CreateAsync(Request(entity.EntityId, installment: 100_000m), CancellationToken.None);

        var response = Assert.IsType<AnalysisResultResponse>(Assert.IsType<CreatedResult>(result).Value);
        var situacion = FilterOf(response, "bcra_situacion");
        Assert.Equal(PersonFilterOutcomes.Ok, situacion.Outcome);
        Assert.Equal(PersonEvidenceKinds.Verificado, situacion.EvidenceKind);
        Assert.StartsWith("No registra deudas en el sistema financiero (Central de Deudores, consulta del 18/10/2026).", situacion.Evidence);
        Assert.Equal(PersonFilterOutcomes.Ok, FilterOf(response, "bcra_eventos_graves").Outcome);
        Assert.Equal(PersonFilterOutcomes.Ok, FilterOf(response, "bcra_cheques_impagos").Outcome);

        var cuota = Assert.Single(response.PersonEvaluation!.Indicators, i => i.Id == "cuota_ingreso");
        Assert.Equal(PersonIndicatorStatuses.Ok, cuota.Status);
        Assert.Equal("0", cuota.Inputs["cuotasVigentesEstimadas"]);

        Assert.Equal(nameof(BcraEnrichmentOutcome.Complete), response.EnrichmentStatus);
        Assert.Empty(h.Bcra.ForAnalysisCalls);
        Assert.Empty(h.LegajoRepository.Saves);
    }

    [Fact]
    public async Task Create_FailedSnapshot_IsReQueriedOnce_AndARealAnswerReplacesIt()
    {
        var h = new Harness();
        var (entity, legajo) = h.PersonWithReadyLegajo();
        var failedAt = new DateTime(2026, 10, 19, 14, 0, 0, DateTimeKind.Utc);
        legajo.BcraDataJson = System.Text.Json.JsonSerializer.Serialize(
            BcraSnapshot(BcraDeudaConsultaEstado.LookupFailed, BcraChequesConsultaEstado.LookupFailed, failedAt));
        legajo.BcraDataUpdatedAt = failedAt;
        h.Bcra.ForAnalysis = BcraSnapshot(
            BcraDeudaConsultaEstado.NoDebtReported, BcraChequesConsultaEstado.NoChequesReported, DateTime.UtcNow);

        var result = await h.Sut.CreateAsync(Request(entity.EntityId), CancellationToken.None);

        var response = Assert.IsType<AnalysisResultResponse>(Assert.IsType<CreatedResult>(result).Value);
        Assert.Equal([entity.CUIT], h.Bcra.ForAnalysisCalls);

        var (savedEntity, savedLegajo) = Assert.Single(h.LegajoRepository.Saves);
        Assert.Same(legajo, savedLegajo);
        Assert.Equal(savedEntity.BcraDataJson, savedLegajo.BcraDataJson);
        Assert.Equal(savedEntity.BcraDataUpdatedAt, savedLegajo.BcraDataUpdatedAt);
        Assert.True(savedLegajo.BcraDataUpdatedAt > failedAt);

        Assert.Equal(PersonFilterOutcomes.Ok, FilterOf(response, "bcra_situacion").Outcome);
        Assert.Equal(PersonFilterOutcomes.Ok, FilterOf(response, "bcra_cheques_impagos").Outcome);
        Assert.Equal(nameof(BcraEnrichmentOutcome.Complete), response.EnrichmentStatus);
        // The analysis freezes the refreshed snapshot, not the failed one.
        Assert.Equal(savedLegajo.BcraDataJson, Assert.Single(h.Db.AnalysisResults.AsNoTracking()).BcraDataJson);
    }

    [Fact]
    public async Task Create_FailedSnapshot_StillFailingOnTheReQuery_KeepsIt_AndSaysTheQueryFailed()
    {
        var h = new Harness();
        var (entity, legajo) = h.PersonWithReadyLegajo();
        var failedAt = new DateTime(2026, 10, 19, 14, 0, 0, DateTimeKind.Utc);
        var stored = System.Text.Json.JsonSerializer.Serialize(
            BcraSnapshot(BcraDeudaConsultaEstado.LookupFailed, BcraChequesConsultaEstado.LookupFailed, failedAt));
        legajo.BcraDataJson = stored;
        h.Bcra.ForAnalysis = BcraSnapshot(
            BcraDeudaConsultaEstado.LookupFailed, BcraChequesConsultaEstado.NoChequesReported, DateTime.UtcNow);

        var result = await h.Sut.CreateAsync(Request(entity.EntityId), CancellationToken.None);

        var response = Assert.IsType<AnalysisResultResponse>(Assert.IsType<CreatedResult>(result).Value);
        Assert.Single(h.Bcra.ForAnalysisCalls);
        Assert.Empty(h.LegajoRepository.Saves);
        Assert.Equal(stored, legajo.BcraDataJson);

        var situacion = FilterOf(response, "bcra_situacion");
        Assert.Equal(PersonFilterOutcomes.SinDatos, situacion.Outcome);
        Assert.StartsWith("La consulta a la Central de Deudores del BCRA del 19/10/2026 falló.", situacion.Evidence);
        Assert.Equal(nameof(BcraEnrichmentOutcome.Degraded), response.EnrichmentStatus);
    }

    [Fact]
    public async Task Create_NoSnapshot_AndTheReQueryThrows_StillEvaluates_WithSinDatos()
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();
        h.Bcra.Throws = true;

        var result = await h.Sut.CreateAsync(Request(entity.EntityId), CancellationToken.None);

        var response = Assert.IsType<AnalysisResultResponse>(Assert.IsType<CreatedResult>(result).Value);
        Assert.Single(h.Bcra.ForAnalysisCalls);
        Assert.Empty(h.LegajoRepository.Saves);
        var situacion = FilterOf(response, "bcra_situacion");
        Assert.Equal(PersonFilterOutcomes.SinDatos, situacion.Outcome);
        Assert.StartsWith("El legajo no tiene consulta a la Central de Deudores del BCRA.", situacion.Evidence);
        Assert.Single(h.Db.Analyses.AsNoTracking());
    }

    // ── Repository ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Repository_ReadsAreTenantScoped()
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();
        await h.Sut.CreateAsync(Request(entity.EntityId), CancellationToken.None);
        var analysisId = h.Db.Analyses.Single().AnalysisId;

        var repo = new PersonAnalysisRepository(h.Db, NullLogger<PersonAnalysisRepository>.Instance);

        Assert.Single(await repo.GetParticipantsAsync(analysisId, Tenant, CancellationToken.None));
        Assert.Empty(await repo.GetParticipantsAsync(analysisId, "other-tenant", CancellationToken.None));
        var kindAndYear = await repo.GetKindAndFiscalYearAsync(analysisId, Tenant, CancellationToken.None);
        Assert.Equal(AnalysisKinds.PersonaHumana, kindAndYear?.Kind);
        Assert.Equal(h.Db.Analyses.Single().FiscalYear, kindAndYear?.FiscalYear);
        Assert.Equal(entity.EntityId, kindAndYear?.EntityId);
        Assert.Null(await repo.GetKindAndFiscalYearAsync(analysisId, "other-tenant", CancellationToken.None));
        Assert.False(await repo.HasOpenPersonAnalysisAsync(entity.EntityId, "other-tenant", CancellationToken.None));
    }

    [Fact]
    public async Task Repository_RefusesABalanceAnalysis()
    {
        var h = new Harness();
        var repo = new PersonAnalysisRepository(h.Db, NullLogger<PersonAnalysisRepository>.Instance);
        var analysis = new Analysis { AnalysisId = Guid.NewGuid(), TenantId = Tenant };

        await Assert.ThrowsAsync<ArgumentException>(() => repo.CreateAsync(
            analysis,
            new AnalysisResult { AnalysisId = analysis.AnalysisId, TenantId = Tenant },
            new AnalysisParticipant { AnalysisId = analysis.AnalysisId, TenantId = Tenant },
            [],
            CancellationToken.None));
    }

    // ── Harness ────────────────────────────────────────────────────────────────────

    private static CreatePersonAnalysisRequest Request(Guid entityId, decimal? installment = null) => new()
    {
        EntityId          = entityId,
        RequestedAmount   = 2_000_000m,
        Currency          = "ARS",
        InstallmentAmount = installment
    };

    private static ProblemDetails AssertCoded(IActionResult result, int status, string errorCode)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(status, objectResult.StatusCode ?? problem.Status);
        Assert.Equal(errorCode, problem.Extensions["errorCode"]);
        return problem;
    }

    private sealed class Harness
    {
        private readonly string _databaseName = $"person-analyses-{Guid.NewGuid()}";

        public FinanciaDbContext Db { get; }

        /// <summary>A fresh context over the same store, as a second request would get.</summary>
        public FinanciaDbContext NewContext()
            => new(new DbContextOptionsBuilder<FinanciaDbContext>().UseInMemoryDatabase(_databaseName).Options);

        public FakeLegajoDocumentRepository Legajos { get; } = new();
        public StubEntityRepository Entities { get; } = new();
        public StubUserRepository Users { get; } = new();
        public RecordingAuditService Audit { get; } = new();
        public ListReconciliationRepository Reconciliation { get; } = new();
        public StubBcraService Bcra { get; } = new();
        public FakePersonLegajoRepository LegajoRepository { get; }
        public FakeFiadorRepository Fiadores { get; } = new();
        public RecordingSnapshotRefreshService Refresh { get; }

        public PersonAnalysesController Sut { get; }

        public Harness(Microsoft.Extensions.Logging.ILogger<PersonAnalysesController>? logger = null)
        {
            Db = NewContext();
            LegajoRepository = new FakePersonLegajoRepository(Legajos);
            Refresh = new RecordingSnapshotRefreshService(Legajos);
            Sut = new PersonAnalysesController(
                new StubTenantContextAccessor(),
                Entities,
                LegajoRepository,
                Legajos,
                Reconciliation,
                new PersonAnalysisRepository(Db, NullLogger<PersonAnalysisRepository>.Instance),
                Users,
                Bcra,
                Fiadores,
                Refresh,
                Audit,
                ExpectedDocumentsTestKit.RealService(Db),
                logger ?? NullLogger<PersonAnalysesController>.Instance)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(
                            [new Claim("uid", LegajoTestData.FirebaseUid)], "Test"))
                    }
                }
            };
        }

        /// <summary>
        /// POST /api/analyses/{id}/rescore as DI would build it for one request: the real re-score service and
        /// persona branch over a fresh context, on these fakes, at <paramref name="at"/> (default: a few
        /// minutes from now, past the creation).
        /// </summary>
        public AnalysisRescoreService Rescore(
            DateTimeOffset? at = null, Microsoft.Extensions.Logging.ILogger<PersonAnalysisRescoreService>? logger = null)
        {
            var db = NewContext();
            var rescoreRepository = new AnalysisRescoreRepository(db, NullLogger<AnalysisRescoreRepository>.Instance);
            var person = new PersonAnalysisRescoreService(
                Entities,
                new PersonAnalysisRepository(db, NullLogger<PersonAnalysisRepository>.Instance),
                LegajoRepository,
                Legajos,
                Reconciliation,
                Bcra,
                Fiadores,
                Refresh,
                rescoreRepository,
                Audit,
                ExpectedDocumentsTestKit.RealService(db),
                logger ?? NullLogger<PersonAnalysisRescoreService>.Instance);

            return new AnalysisRescoreService(
                new AnalysisResultRepository(db, NullLogger<AnalysisResultRepository>.Instance),
                new AnalysisRepository(db, NullLogger<AnalysisRepository>.Instance),
                new StoreDocumentRepository(new InMemoryDocumentStore()),   // a persona analysis has no document
                rescoreRepository,
                Entities,
                Bcra,
                new ScoringService(db, NullLogger<ScoringService>.Instance),
                new RatioCalculationService(),
                new StubRescoreClaude(),                                    // never called: no narrative
                new RecordingApiCallLog(),
                Audit,
                person,
                Options.Create(new Api.Web.FinanceIA.Models.RescoreOptions()),
                NullLogger<AnalysisRescoreService>.Instance,
                new MutableClock(at ?? ReevaluatedAt));
        }

        public Entity AddEntity(string personType, Guid? legajoId)
        {
            var entity = new Entity
            {
                EntityId       = Guid.NewGuid(),
                TenantId       = Tenant,
                CUIT           = LegajoTestData.LegajoCuit,
                BusinessName   = LegajoTestData.LegajoFullName,
                PersonType     = personType,
                PersonLegajoId = legajoId
            };
            Entities.Entities.Add(entity);
            return entity;
        }

        /// <summary>A persona humana entity with a legajo: consent and (by default) a Ready Ganancias with a run.</summary>
        public (Entity Entity, PersonLegajo Legajo) PersonWithReadyLegajo(bool consent = true, bool withGanancias = true)
        {
            var legajo = Legajos.AddLegajo(Tenant, consent);
            legajo.FiscalRegime = FiscalRegimes.ResponsableInscripto;
            var entity = AddEntity(PersonTypes.Humana, legajo.PersonLegajoId);
            Legajos.EntityByLegajo[legajo.PersonLegajoId] = entity.EntityId;

            if (withGanancias)
            {
                var ganancias = Legajos.AddDocument(legajo, DocumentTypes.Ganancias, LegajoStatuses.Ready, hash: "gan");
                ganancias.PeriodEnd = new DateTime(2025, 12, 31);
                Legajos.Runs.Add(new LegajoExtraction
                {
                    ExtractionId   = Guid.NewGuid(),
                    TenantId       = Tenant,
                    PersonLegajoId = legajo.PersonLegajoId,
                    DocumentId     = ganancias.DocumentId,
                    Kind           = LegajoExtractionKinds.Extraction,
                    DocumentType   = DocumentTypes.Ganancias,
                    SchemaVersion  = "1.0",
                    PromptVersion  = "gan-1.0",
                    Model          = "claude-sonnet-4-6",
                    Status         = LegajoExtractionStatuses.Succeeded,
                    ResultJson     = "{}",
                    OutputHash     = "out",
                    InputHash      = "in",
                    IsCurrent      = true,
                    CreatedAt      = DateTime.UtcNow.AddDays(-2)
                });
                Reconciliation.Incomes.Add(new LegajoIncome
                {
                    IncomeId = Guid.NewGuid(), TenantId = Tenant, PersonLegajoId = legajo.PersonLegajoId,
                    Period = "2025", PeriodFrom = new DateOnly(2025, 1, 1), PeriodTo = new DateOnly(2025, 12, 31),
                    Source = LegajoIncomeSources.Ganancias, Concept = LegajoIncomeConcepts.Renta3ra, Amount = 12_000_000m,
                    DocumentId = ganancias.DocumentId, ExtractionId = Legajos.Runs[^1].ExtractionId
                });
            }

            return (entity, legajo);
        }
    }

    private sealed class FakePersonLegajoRepository(FakeLegajoDocumentRepository legajos) : IPersonLegajoRepository
    {
        public Task<PersonLegajo?> GetByIdAsync(Guid personLegajoId, string tenantId, CancellationToken ct)
            => Task.FromResult(legajos.Legajos.SingleOrDefault(l => l.PersonLegajoId == personLegajoId && l.TenantId == tenantId));

        public Task<PersonLegajo?> GetByCuitAsync(string cuit, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<string?> GetMonotributoCategoryAsync(Guid personLegajoId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public List<(Entity Entity, PersonLegajo Legajo)> Saves { get; } = [];

        public Task SaveEntityWithLegajoAsync(Entity entity, bool entityIsNew, PersonLegajo legajo, bool legajoIsNew, CancellationToken ct)
        {
            Assert.False(entityIsNew);
            Assert.False(legajoIsNew);
            Saves.Add((entity, legajo));
            return Task.CompletedTask;
        }

        public Task SaveFiadorLegajoLinkAsync(Fiador fiador, PersonLegajo legajo, bool legajoIsNew, CancellationToken ct) => throw new NotImplementedException();
        public Task SaveLegajoSnapshotsAsync(PersonLegajo legajo, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class ListReconciliationRepository : ILegajoReconciliationRepository
    {
        public List<LegajoItem> Items { get; } = [];
        public List<LegajoIncome> Incomes { get; } = [];

        public Task<IReadOnlyList<LegajoItem>> GetItemsAsync(Guid personLegajoId, string tenantId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<LegajoItem>>(Items.Where(i => i.PersonLegajoId == personLegajoId && i.TenantId == tenantId).ToList());

        public Task<IReadOnlyList<LegajoIncome>> GetIncomesAsync(Guid personLegajoId, string tenantId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<LegajoIncome>>(Incomes.Where(i => i.PersonLegajoId == personLegajoId && i.TenantId == tenantId).ToList());

        public Task<LegajoItem?> GetItemAsync(Guid itemId, Guid personLegajoId, string tenantId, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> TryUpdateItemAsync(LegajoItem item, DateTime expectedUpdatedAt, CancellationToken ct) => throw new NotImplementedException();
        public Task<Api.Web.FinanceIA.Servicios.Legajo.LegajoReconciliationMergeResult> ApplyRebuildAsync(
            Guid personLegajoId, string tenantId, Api.Web.FinanceIA.Servicios.Legajo.LegajoReconciliationBuild build,
            DateTime now, CancellationToken ct) => throw new NotImplementedException();
    }

    /// <summary>
    /// No CPI series. The per-CUIT read is the one re-query of an unusable legajo snapshot: by default every
    /// BCRA call failed (null), or <see cref="ForAnalysis"/> / <see cref="Throws"/> say otherwise.
    /// </summary>
    private sealed class StubBcraService : IBcraService
    {
        public BcraData? ForAnalysis { get; set; }
        public bool Throws { get; set; }
        public List<string> ForAnalysisCalls { get; } = [];

        public Task<BcraMacroData?> GetMacroAsync(CancellationToken ct) => Task.FromResult<BcraMacroData?>(null);

        public Task<BcraData?> GetBcraDataForAnalysisAsync(string cuit, CancellationToken ct)
        {
            ForAnalysisCalls.Add(cuit);
            return Throws
                ? throw new HttpRequestException("BCRA down")
                : Task.FromResult(ForAnalysis);
        }

        public Task<BcraDeudaData> GetDeudaAsync(string cuit, CancellationToken ct) => throw new NotSupportedException("no per-CUIT call");
        public Task<BcraChequesData> GetChequesRechazadosAsync(string cuit, CancellationToken ct) => throw new NotSupportedException("no per-CUIT call");
        public Task<BcraCambiarioData?> GetCambiarioAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>A BCRA payload as BcraService builds it, with both query outcomes set and dated.</summary>
    private static BcraData BcraSnapshot(BcraDeudaConsultaEstado deuda, BcraChequesConsultaEstado cheques, DateTime fetchedAt) => new()
    {
        Deuda   = new BcraDeudaData { Estado = deuda, FetchedAt = fetchedAt, EntidadesDetalle = [], HistorialSituaciones = [] },
        Cheques = new BcraChequesData { Estado = cheques, FetchedAt = fetchedAt }
    };

    private static PersonFilterResult FilterOf(AnalysisResultResponse response, string id)
        => Assert.Single(response.PersonEvaluation!.Filters, f => f.Id == id);
}
