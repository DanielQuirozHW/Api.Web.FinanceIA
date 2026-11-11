// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-30

using System.Reflection;
using System.Text.Json;
using Api.Web.FinanceIA.Clases.Filters;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.AnalysisRuns;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;
using Api.Web.FinanceIA.Tests.AnalysisRuns;
using Api.Web.FinanceIA.Tests.Legajo;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// "Reanalizar" on a persona humana analysis: POST /api/analyses/{id}/rescore re-evaluates it IN PLACE. Each
/// test creates the analysis through the real POST /api/person-analyses, changes the world (a corrected
/// document, a pf_ parameter, a fiador's legajo), and runs the real AnalysisRescoreService — eligibility,
/// cooldown, the persona branch and the real repository write — over the same InMemory store, one fresh
/// context per request.
/// </summary>
public sealed partial class PersonAnalysesControllerTests
{
    private static readonly DateTimeOffset ReevaluatedAt = DateTimeOffset.UtcNow.AddMinutes(5);

    // ── The point of the feature ────────────────────────────────────────────────────

    [Fact]
    public async Task Reevaluate_ACorrectedDocument_ChangesTheIndicatorInPlace_AndTheHistoryKeepsTheOldOne()
    {
        var h = new Harness();
        var (entity, legajo) = h.PersonWithReadyLegajo();
        // A usable BCRA snapshot: cuota_ingreso counts the current instalments from it.
        legajo.BcraDataJson      = JsonSerializer.Serialize(BcraSnapshot(
            BcraDeudaConsultaEstado.NoDebtReported, BcraChequesConsultaEstado.NoChequesReported, DateTime.UtcNow.AddDays(-1)));
        legajo.BcraDataUpdatedAt = DateTime.UtcNow.AddDays(-1);
        var created = AssertCreated(await h.Sut.CreateAsync(Request(entity.EntityId, installment: 150_000m), CancellationToken.None));
        var before  = await ReadRowsAsync(h);

        // The analyst corrects the Ganancias extraction: the reconciled income drops to a quarter.
        var run = h.Legajos.Runs.Single(r => r.DocumentType == DocumentTypes.Ganancias);
        var correction = new LegajoCorrection
        {
            CorrectionId = Guid.NewGuid(), TenantId = Tenant, ExtractionId = run.ExtractionId,
            FieldPath = "/totals/renta", Reason = "monto mal leído", CorrectedAt = DateTime.UtcNow
        };
        h.Legajos.Corrections.Add(correction);
        h.Reconciliation.Incomes.Single().Amount = 3_000_000m;

        var outcome = await h.Rescore().RescoreAsync(created.ResultId, Tenant, h.Users.Analyst.UserId, "10.0.0.1", "trace", CancellationToken.None);

        Assert.Equal(200, outcome.StatusCode);
        var after = await ReadRowsAsync(h);

        // In place: the same rows, the same ids, the same terms; only the evaluation columns moved.
        var oldRow = Assert.Single(before.Participants);
        var newRow = Assert.Single(after.Participants);
        Assert.Equal(oldRow.ParticipantId, newRow.ParticipantId);
        Assert.Equal(oldRow.PersonLegajoId, newRow.PersonLegajoId);
        Assert.Equal(oldRow.AmountAtCharge, newRow.AmountAtCharge);
        Assert.Equal(oldRow.InstallmentAmount, newRow.InstallmentAmount);
        Assert.NotEqual(oldRow.IndicatorsJson, newRow.IndicatorsJson);
        Assert.Equal(ReevaluatedAt.UtcDateTime, newRow.CreatedAt);

        var oldCuota = Indicator(oldRow, "cuota_ingreso");
        var newCuota = Indicator(newRow, "cuota_ingreso");
        Assert.NotNull(oldCuota.Value);
        Assert.NotNull(newCuota.Value);
        Assert.True(newCuota.Value > oldCuota.Value);

        // The new evaluation pins the correction it read.
        var sources = PersonEvaluationJson.TryDeserialize<PersonEvaluationSources>(newRow.DocumentVersionsJson)!;
        Assert.Contains(correction.CorrectionId, sources.Documents.Single(d => d.ExtractionId == run.ExtractionId).CorrectionIds);

        // One Analyses row still, one result re-scored once, nothing balance-shaped written.
        Assert.Single(after.Analyses);
        var result = Assert.Single(after.Results);
        Assert.Equal(1, result.RescoreCount);
        Assert.Equal(ReevaluatedAt.UtcDateTime, result.RescoredAt);
        Assert.Equal(h.Users.Analyst.UserId, result.RescoredBy);
        Assert.Equal(before.Results.Single().GeneratedAt, result.GeneratedAt);
        Assert.Null(result.BalanceJson);
        Assert.Null(result.AiSuggestedScore);
        Assert.NotEqual(before.Results.Single().AiOutputHash, result.AiOutputHash);

        // The history holds every overwritten column exactly as stored.
        var history = Assert.Single(after.Rescores);
        Assert.Null(history.PreviousScore);
        Assert.Null(history.NewScore);
        Assert.Null(history.PreviousLevel);
        Assert.Null(history.NewLevel);
        Assert.Equal(before.Results.Single().EnrichmentStatus, history.PreviousEnrichmentStatus);

        var previous = JsonSerializer.Deserialize<PersonRescorePreviousValues>(history.PreviousValuesJson, PersonEvaluationJson.Options)!;
        Assert.Equal(AnalysisKinds.PersonaHumana, previous.Kind);
        var kept = Assert.Single(previous.Participants);
        Assert.Equal(oldRow.ParticipantId, kept.ParticipantId);
        Assert.Equal(oldRow.DocumentVersionsJson, kept.DocumentVersionsJson);
        Assert.Equal(oldRow.FiltersJson, kept.FiltersJson);
        Assert.Equal(oldRow.IndicatorsJson, kept.IndicatorsJson);
        Assert.Equal(oldRow.CoverageJson, kept.CoverageJson);
        Assert.Equal(oldRow.EvaluationVersion, kept.EvaluationVersion);
        Assert.Equal(oldRow.CreatedAt, kept.CreatedAt);
        Assert.Equal(before.Results.Single().AiInputHash, previous.Result.AiInputHash);
        Assert.Equal(before.Results.Single().AiOutputHash, previous.Result.AiOutputHash);
        Assert.Equal(before.Results.Single().BcraDataJson, previous.Result.BcraDataJson);
        Assert.Equal(before.Results.Single().EnrichmentStatus, previous.Result.EnrichmentStatus);
        Assert.Null(previous.Result.RescoredAt);

        // …so the previous evaluation can be rebuilt by the very reader the detail uses.
        var rebuilt = PersonEvaluationJson.ToResponse(new AnalysisParticipant
        {
            ParticipantId = kept.ParticipantId, PersonLegajoId = kept.PersonLegajoId, Role = kept.Role,
            AmountAtCharge = oldRow.AmountAtCharge, Currency = oldRow.Currency, InstallmentAmount = oldRow.InstallmentAmount,
            DocumentVersionsJson = kept.DocumentVersionsJson, FiltersJson = kept.FiltersJson,
            IndicatorsJson = kept.IndicatorsJson, CoverageJson = kept.CoverageJson,
            EvaluationVersion = kept.EvaluationVersion, CreatedAt = kept.CreatedAt
        });
        Assert.Equal(oldCuota.Value, rebuilt.Indicators.Single(i => i.Id == "cuota_ingreso").Value);

        // The rescore block.
        var summary = outcome.Summary!;
        Assert.Null(summary.PreviousScore);
        Assert.Null(summary.NewScore);
        Assert.Equal(created.PersonEvaluation!.Suggestion, summary.PreviousSuggestion);
        Assert.NotNull(summary.NewSuggestion);
        Assert.Equal(PersonEvaluationService.Version, summary.PreviousEvaluationVersion);
        Assert.Equal(PersonEvaluationService.Version, summary.NewEvaluationVersion);
        Assert.Equal(0, summary.FiadoresCount);
        Assert.Null(summary.CounterGuaranteeCoverage);
        Assert.Equal(["cobertura_servicio_deuda", "cuota_ingreso", "solvencia_pn"], summary.Indicators!.Select(i => i.Id));
        var cuota = summary.Indicators!.Single(i => i.Id == "cuota_ingreso");
        Assert.Equal(oldCuota.Value, cuota.PreviousValue);
        Assert.Equal(newCuota.Value, cuota.NewValue);
        Assert.Equal(newCuota.Band, cuota.NewBand);
        Assert.Equal(PersonFilterCounts.Of(PersonEvaluationJson.FiltersOf(oldRow.FiltersJson)), summary.FilterCounts!.Previous);
        Assert.Equal([RescoreSource.Bcra, RescoreSource.Arca, RescoreSource.Lufe, RescoreSource.Legajo],
            summary.Sources.Select(s => s.Source));

        // Audited once.
        Assert.Single(h.Audit.Entries, e => e.Action == "Analysis.PersonReevaluated");
    }

    [Fact]
    public async Task Reevaluate_FreezesTheTenantsCurrentPfParametersAnew()
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();
        var created = AssertCreated(await h.Sut.CreateAsync(Request(entity.EntityId), CancellationToken.None));
        var oldParameters = Sources(Assert.Single((await ReadRowsAsync(h)).Participants)).Parameters;

        h.Db.ScoringConfigs.Add(new ScoringConfig
        {
            ConfigId = Guid.NewGuid(), TenantId = Tenant, ConfigKey = "pf_antiguedad_min_meses",
            ConfigValue = oldParameters.AntiguedadMinMeses + 12, UpdatedAt = DateTime.UtcNow
        });
        await h.Db.SaveChangesAsync();

        Assert.Equal(200, (await ReevaluateAsync(h, created)).StatusCode);

        var after = await ReadRowsAsync(h);
        Assert.Equal(oldParameters.AntiguedadMinMeses + 12, Sources(Assert.Single(after.Participants)).Parameters.AntiguedadMinMeses);

        var previous = Previous(Assert.Single(after.Rescores));
        Assert.Equal(oldParameters.AntiguedadMinMeses,
            PersonEvaluationJson.TryDeserialize<PersonEvaluationSources>(previous.Participants.Single().DocumentVersionsJson)!
                .Parameters.AntiguedadMinMeses);
    }

    [Fact]
    public async Task Reevaluate_WithFiadores_KeepsEveryIdentityAndInput_AndReevaluatesEachOnHisCurrentLegajo()
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();
        var first = Inmueble(1_000_000m);
        var (fiador1, _) = AddFiador(h, entity, items: first);
        var (fiador2, _) = AddFiador(h, entity, cuit: FiadorCuit2, items: Inmueble(200_000m));
        var request = WithFiadores(entity.EntityId,
            FiadorOf(fiador1.FiadorId) with { GuaranteesGivenAmount = 10_000m },
            FiadorOf(fiador2.FiadorId, GuaranteeTypes.Conyuge) with
            {
                AmountAtCharge = 1_500_000m, SpouseConsent = true, HousingInopposable = true
            });
        var created = AssertCreated(await h.Sut.CreateAsync(request, CancellationToken.None));
        var before  = await ReadRowsAsync(h);
        Assert.False(created.PersonEvaluation!.CounterGuaranteeCoverage!.MeetsMin);

        // The first fiador's appraisal is updated on his legajo.
        first.MarketValue = 5_000_000m;
        h.Refresh.UnusableCalls.Clear();

        var outcome = await ReevaluateAsync(h, created);

        Assert.Equal(200, outcome.StatusCode);
        var after = await ReadRowsAsync(h);
        Assert.Equal(3, after.Participants.Count);

        foreach (var old in before.Participants)
        {
            var now = Assert.Single(after.Participants, p => p.ParticipantId == old.ParticipantId);
            Assert.Equal(old.FiadorId, now.FiadorId);
            Assert.Equal(old.PersonLegajoId, now.PersonLegajoId);
            Assert.Equal(old.Role, now.Role);
            Assert.Equal(old.AmountAtCharge, now.AmountAtCharge);
            Assert.Equal(old.Currency, now.Currency);
            Assert.Equal(old.InstallmentAmount, now.InstallmentAmount);
            Assert.Equal(old.GuaranteeType, now.GuaranteeType);
            Assert.Equal(old.SpouseConsent, now.SpouseConsent);
            Assert.Equal(old.HousingInopposable, now.HousingInopposable);
            Assert.Equal(old.GuaranteesGivenAmount, now.GuaranteesGivenAmount);
        }

        // The canonical fiador order is unchanged.
        Assert.Equal(PersonEvaluationJson.FiadorRowsOf(before.Participants).Select(p => p.ParticipantId),
                     PersonEvaluationJson.FiadorRowsOf(after.Participants).Select(p => p.ParticipantId));

        var firstRow = after.Participants.Single(p => p.FiadorId == fiador1.FiadorId);
        Assert.NotEqual(before.Participants.Single(p => p.FiadorId == fiador1.FiadorId).CoverageJson, firstRow.CoverageJson);

        // Each fiador got his one BCRA retry on his own legajo, as on the POST (in the rows' canonical order).
        Assert.Equal(new[] { fiador1.PersonLegajoId!.Value, fiador2.PersonLegajoId!.Value }.Order(), h.Refresh.UnusableCalls.Order());

        // The decision now reads the NEW evaluation: the coverage meets the minimum.
        var (rules, _, fiadores) = await PersonEvaluationJson.ResolveSolicitanteAsync(
            new PersonAnalysisRepository(h.NewContext(), NullLogger<PersonAnalysisRepository>.Instance),
            created.AnalysisId, Tenant, CancellationToken.None);
        Assert.True(rules.CounterGuaranteeCoverage!.MeetsMin);
        Assert.False(rules.CounterGuaranteeBelowMin);
        Assert.Equal(2, fiadores.Count);

        var coverage = outcome.Summary!.CounterGuaranteeCoverage!;
        Assert.False(coverage.PreviousMeetsMin);
        Assert.True(coverage.NewMeetsMin);
        Assert.Equal(created.PersonEvaluation.CounterGuaranteeCoverage.Ratio, coverage.PreviousRatio);
        Assert.Equal(rules.CounterGuaranteeCoverage.Ratio, coverage.NewRatio);
        Assert.Equal(2, outcome.Summary.FiadoresCount);

        var previous = Previous(Assert.Single(after.Rescores));
        Assert.Equal(before.Participants.Select(p => p.ParticipantId).OrderBy(id => id),
                     previous.Participants.Select(p => p.ParticipantId).OrderBy(id => id));
    }

    [Fact]
    public async Task Reevaluate_AnUnusableBcraSnapshot_IsReQueriedOnce_AndAStillFailingBcraReevaluatesAsDegraded()
    {
        var h = new Harness();
        var (entity, legajo) = h.PersonWithReadyLegajo();
        var created = AssertCreated(await h.Sut.CreateAsync(Request(entity.EntityId), CancellationToken.None));
        Assert.Single(h.Bcra.ForAnalysisCalls);

        // Still down: one more try, then SinDatos as at creation — never a refusal.
        var down = await ReevaluateAsync(h, created);
        Assert.Equal(200, down.StatusCode);
        Assert.Equal(2, h.Bcra.ForAnalysisCalls.Count);
        Assert.Equal(nameof(BcraEnrichmentOutcome.Degraded), down.Summary!.NewEnrichmentStatus);
        Assert.False(down.Summary.Sources.Single(s => s.Source == RescoreSource.Bcra).Live);

        // Back: the answer replaces the legajo's snapshot and the evaluation reads it.
        h.Bcra.ForAnalysis = BcraSnapshot(BcraDeudaConsultaEstado.NoDebtReported, BcraChequesConsultaEstado.NoChequesReported, DateTime.UtcNow);
        var back = await h.Rescore(ReevaluatedAt.AddMinutes(10)).RescoreAsync(
            created.ResultId, Tenant, h.Users.Analyst.UserId, null, null, CancellationToken.None);

        Assert.Equal(200, back.StatusCode);
        Assert.Equal(3, h.Bcra.ForAnalysisCalls.Count);
        Assert.Equal(nameof(BcraEnrichmentOutcome.Degraded), back.Summary!.PreviousEnrichmentStatus);
        Assert.Equal(nameof(BcraEnrichmentOutcome.Complete), back.Summary.NewEnrichmentStatus);
        Assert.True(back.Summary.Sources.Single(s => s.Source == RescoreSource.Bcra).Live);
        Assert.NotNull(legajo.BcraDataJson);
        Assert.Equal(legajo.BcraDataJson, Assert.Single((await ReadRowsAsync(h)).Results).BcraDataJson);
        Assert.Equal(2, (await ReadRowsAsync(h)).Rescores.Count);
    }

    // ── Eligibility: the balance rules, through the same service ─────────────────

    [Theory]
    [InlineData(AnalysisStatuses.Approved, false, RescoreErrorCodes.NotPending)]
    [InlineData(AnalysisStatuses.Rejected, false, RescoreErrorCodes.NotPending)]
    [InlineData(AnalysisStatuses.PreApproved, true, RescoreErrorCodes.NotPending)]
    [InlineData(AnalysisStatuses.PendingReview, true, RescoreErrorCodes.HasVotes)]
    public async Task Reevaluate_ADecidedOrSignedAnalysis_IsRefused_AndNothingIsWritten(string status, bool preApproved, string errorCode)
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();
        var created = AssertCreated(await h.Sut.CreateAsync(Request(entity.EntityId), CancellationToken.None));

        var analysis = h.Db.Analyses.Single();
        analysis.Status = status;
        if (preApproved)
        {
            analysis.PreApprovedBy = Guid.NewGuid();
            analysis.PreApprovedAt = DateTime.UtcNow;
        }
        h.Db.ChangeTracker.DetectChanges();   // AutoDetectChanges is disabled on the context
        await h.Db.SaveChangesAsync();

        await AssertReevaluationRefusedAsync(h, created, 409, errorCode);
    }

    [Fact]
    public async Task Reevaluate_WithAVoteOnRecord_IsRefused()
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();
        var created = AssertCreated(await h.Sut.CreateAsync(Request(entity.EntityId), CancellationToken.None));
        h.Db.AnalysisApprovalVotes.Add(new AnalysisApprovalVote
        {
            VoteId = Guid.NewGuid(), AnalysisId = created.AnalysisId, UserId = Guid.NewGuid(), TenantId = Tenant,
            Vote = AnalysisVoteValues.Rejected, VotedAt = DateTime.UtcNow
        });
        await h.Db.SaveChangesAsync();

        await AssertReevaluationRefusedAsync(h, created, 409, RescoreErrorCodes.HasVotes);
    }

    [Fact]
    public async Task Reevaluate_TwiceWithinTheCooldown_Is429_AndOnlyTheFirstLands()
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();
        var created = AssertCreated(await h.Sut.CreateAsync(Request(entity.EntityId), CancellationToken.None));

        Assert.Equal(200, (await ReevaluateAsync(h, created)).StatusCode);
        var second = await h.Rescore(ReevaluatedAt.AddSeconds(5)).RescoreAsync(
            created.ResultId, Tenant, h.Users.Analyst.UserId, null, null, CancellationToken.None);

        Assert.Equal(429, second.StatusCode);
        Assert.Equal(RescoreErrorCodes.TooManyRequests, second.ErrorCode);
        Assert.NotNull(second.RetryAfterSeconds);
        Assert.Single((await ReadRowsAsync(h)).Rescores);
    }

    [Fact]
    public async Task Reevaluate_ALostRace_WritesNothing()
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();
        var created = AssertCreated(await h.Sut.CreateAsync(Request(entity.EntityId), CancellationToken.None));
        var before  = Fingerprint(await ReadRowsAsync(h));
        var row     = (await ReadRowsAsync(h)).Participants.Single();

        await using var db = h.NewContext();
        var applied = await new AnalysisRescoreRepository(db, NullLogger<AnalysisRescoreRepository>.Instance)
            .TryApplyAsync(new PersonAnalysisRescoreWrite
            {
                ResultId = created.ResultId, AnalysisId = created.AnalysisId, TenantId = Tenant,
                ExpectedRescoreCount = 7,   // another re-evaluation landed first
                RescoredAt = DateTime.UtcNow, RescoredBy = Guid.NewGuid(),
                AiInputHash = "x", AiOutputHash = "y", EnrichmentStatus = nameof(BcraEnrichmentOutcome.Complete),
                Participants = [new PersonParticipantReevaluation
                {
                    ParticipantId = row.ParticipantId, DocumentVersionsJson = "{}", FiltersJson = "[]",
                    IndicatorsJson = "[]", CoverageJson = "{}", EvaluationVersion = "x", CreatedAt = DateTime.UtcNow
                }],
                History = new AnalysisRescore
                {
                    RescoreId = Guid.NewGuid(), TenantId = Tenant, AnalysisId = created.AnalysisId, ResultId = created.ResultId,
                    RescoredAt = DateTime.UtcNow, PreviousEnrichmentStatus = "x", NewEnrichmentStatus = "y",
                    PreviousValuesJson = "{}", SourcesJson = "[]"
                }
            }, CancellationToken.None);

        Assert.Equal(AnalysisRescoreWriteOutcome.LostRace, applied);
        Assert.Equal(before, Fingerprint(await ReadRowsAsync(h)));
    }

    // ── Readiness refusals: the POST's own, before anything is written ────────────

    [Fact]
    public async Task Reevaluate_WithTheConsentRevoked_Is409ConsentRequired()
    {
        var h = new Harness();
        var (entity, legajo) = h.PersonWithReadyLegajo();
        var created = AssertCreated(await h.Sut.CreateAsync(Request(entity.EntityId), CancellationToken.None));
        legajo.ConsentRecordedAt = null;

        var outcome = await AssertReevaluationRefusedAsync(h, created, 409, PersonEvaluationErrorCodes.ConsentRequired);
        Assert.Contains("autorización de tratamiento de datos", outcome.Detail);
    }

    [Fact]
    public async Task Reevaluate_WithNoReadyDocumentLeft_Is409LegajoNotReady()
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();
        var created = AssertCreated(await h.Sut.CreateAsync(Request(entity.EntityId), CancellationToken.None));
        h.Legajos.Documents.Single(d => d.DocumentType == DocumentTypes.Ganancias).IsDeleted = true;

        await AssertReevaluationRefusedAsync(h, created, 409, PersonEvaluationErrorCodes.LegajoNotReady);
    }

    public static TheoryData<string> FiadorNoLongerReady =>
    [
        PersonEvaluationErrorCodes.FiadorReasons.NotActive,
        PersonEvaluationErrorCodes.FiadorReasons.ConsentRequired,
        PersonEvaluationErrorCodes.FiadorReasons.NoReadyDocument,
        PersonEvaluationErrorCodes.FiadorReasons.LegajoChanged
    ];

    [Theory]
    [MemberData(nameof(FiadorNoLongerReady))]
    public async Task Reevaluate_AFiadorNoLongerReady_Is409WithHisIdAndTheReason_AndNothingIsWritten(string reason)
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();
        var (fiador, fiadorLegajo) = AddFiador(h, entity, items: Inmueble(1_000_000m));
        var created = AssertCreated(await h.Sut.CreateAsync(WithFiadores(entity.EntityId, FiadorOf(fiador.FiadorId)), CancellationToken.None));

        switch (reason)
        {
            case PersonEvaluationErrorCodes.FiadorReasons.NotActive:
                fiador.Status = "Inactive";
                break;
            case PersonEvaluationErrorCodes.FiadorReasons.ConsentRequired:
                fiadorLegajo!.ConsentRecordedAt = null;
                break;
            case PersonEvaluationErrorCodes.FiadorReasons.NoReadyDocument:
                h.Legajos.Documents.Single(d => d.PersonLegajoId == fiadorLegajo!.PersonLegajoId
                                             && d.DocumentType == DocumentTypes.ManifestacionBienes).LegajoStatus = LegajoStatuses.Extracting;
                break;
            case PersonEvaluationErrorCodes.FiadorReasons.LegajoChanged:
                var relinked = h.Legajos.AddLegajo(Tenant, consent: true);
                h.Legajos.AddDocument(relinked, DocumentTypes.ManifestacionBienes, LegajoStatuses.Ready, hash: "mb-new");
                fiador.PersonLegajoId = relinked.PersonLegajoId;
                break;
        }

        var outcome = await AssertReevaluationRefusedAsync(h, created, 409, PersonEvaluationErrorCodes.FiadorLegajoNotReady);
        Assert.Equal(fiador.FiadorId, outcome.Extensions!["fiadorId"]);
        Assert.Equal(reason, outcome.Extensions["reason"]);
        Assert.Empty(h.Refresh.UnusableCalls.Skip(1));   // only the POST's own retry
    }

    [Fact]
    public async Task Reevaluate_AFiadorWhoIsNowTheSolicitante_Is409FiadorIsSolicitante()
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();
        var (fiador, _) = AddFiador(h, entity, items: Inmueble(1_000_000m));
        var created = AssertCreated(await h.Sut.CreateAsync(WithFiadores(entity.EntityId, FiadorOf(fiador.FiadorId)), CancellationToken.None));
        fiador.CUIT = entity.CUIT;

        var outcome = await AssertReevaluationRefusedAsync(h, created, 409, PersonEvaluationErrorCodes.FiadorIsSolicitante);
        Assert.Equal(fiador.FiadorId, outcome.Extensions!["fiadorId"]);
    }

    // ── Quota, history, privacy ──────────────────────────────────────────────────

    [Fact]
    public async Task Reevaluate_ConsumesNoAnalysisUnit()
    {
        // The endpoint carries no quota gate, unlike the POST that created the analysis …
        var action = typeof(AnalysisController).GetMethod(nameof(AnalysisController.RescoreAsync))!;
        Assert.Null(action.GetCustomAttribute<BillingQuotaAttribute>());
        Assert.NotNull(typeof(PersonAnalysesController).GetMethod(nameof(PersonAnalysesController.CreateAsync))!
            .GetCustomAttribute<BillingQuotaAttribute>());

        // … and writes no Analyses row, which is what the monthly usage counts.
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();
        var created = AssertCreated(await h.Sut.CreateAsync(Request(entity.EntityId), CancellationToken.None));

        Assert.Equal(200, (await ReevaluateAsync(h, created)).StatusCode);
        Assert.Equal(200, (await h.Rescore(ReevaluatedAt.AddHours(1)).RescoreAsync(
            created.ResultId, Tenant, h.Users.Analyst.UserId, null, null, CancellationToken.None)).StatusCode);

        Assert.Single((await ReadRowsAsync(h)).Analyses);
    }

    [Fact]
    public async Task Reevaluate_History_CarriesTheSuggestionsBeforeAndAfter()
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();
        var created = AssertCreated(await h.Sut.CreateAsync(Request(entity.EntityId), CancellationToken.None));

        var outcome = await ReevaluateAsync(h, created);
        var history = await h.Rescore().GetHistoryAsync(created.ResultId, Tenant, CancellationToken.None);

        var item = Assert.Single(history!);
        Assert.Null(item.PreviousScore);
        Assert.Null(item.NewScore);
        Assert.Equal(outcome.Summary!.PreviousSuggestion, item.PreviousSuggestion);
        Assert.Equal(outcome.Summary.NewSuggestion, item.NewSuggestion);
        Assert.NotNull(item.PreviousSuggestion);
    }

    [Fact]
    public async Task Reevaluate_LogsAndAuditCarryIdsOnly_NeverANameOrCuit()
    {
        var logger = new RecordingRescoreLogger();
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();
        var (fiador, _) = AddFiador(h, entity, items: Inmueble(1_000_000m));
        var created = AssertCreated(await h.Sut.CreateAsync(WithFiadores(entity.EntityId, FiadorOf(fiador.FiadorId)), CancellationToken.None));

        Assert.Equal(200, (await h.Rescore(logger: logger).RescoreAsync(
            created.ResultId, Tenant, h.Users.Analyst.UserId, null, null, CancellationToken.None)).StatusCode);

        Assert.Contains(logger.Messages, m => m.Contains(created.AnalysisId.ToString()));
        Assert.Contains(logger.Messages, m => m.Contains(fiador.FiadorId.ToString()));

        var audit = Assert.Single(h.Audit.Entries, e => e.Action == "Analysis.PersonReevaluated");
        Assert.Equal(created.AnalysisId.ToString(), audit.EntityId);
        Assert.Contains(fiador.FiadorId.ToString(), audit.NewJson);

        string[] forbidden =
        [
            LegajoTestData.LegajoFullName, LegajoTestData.LegajoCuit, LegajoTestData.LegajoCuit.Replace("-", string.Empty),
            FiadorName, FiadorCuit, FiadorCuit.Replace("-", string.Empty)
        ];
        foreach (var text in logger.Messages.Concat([audit.OldJson ?? string.Empty, audit.NewJson ?? string.Empty]))
            Assert.DoesNotContain(forbidden, f => text.Contains(f, StringComparison.Ordinal));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static Task<AnalysisRescoreOutcome> ReevaluateAsync(Harness h, AnalysisResultResponse created)
        => h.Rescore().RescoreAsync(created.ResultId, Tenant, h.Users.Analyst.UserId, null, null, CancellationToken.None);

    /// <summary>The refusal, after proving it wrote nothing, asked the BCRA nothing and audited nothing.</summary>
    private static async Task<AnalysisRescoreOutcome> AssertReevaluationRefusedAsync(
        Harness h, AnalysisResultResponse created, int status, string errorCode)
    {
        var before     = Fingerprint(await ReadRowsAsync(h));
        var bcraCalls  = h.Bcra.ForAnalysisCalls.Count;
        var refreshes  = h.Refresh.UnusableCalls.Count;
        var audits     = h.Audit.Entries.Count;

        var outcome = await ReevaluateAsync(h, created);

        Assert.Equal(status, outcome.StatusCode);
        Assert.Equal(errorCode, outcome.ErrorCode);
        Assert.Null(outcome.Summary);
        Assert.Equal(before, Fingerprint(await ReadRowsAsync(h)));
        Assert.Equal(bcraCalls, h.Bcra.ForAnalysisCalls.Count);
        Assert.Equal(refreshes, h.Refresh.UnusableCalls.Count);
        Assert.Equal(audits, h.Audit.Entries.Count);
        return outcome;
    }

    private sealed record StoredRows(
        IReadOnlyList<Analysis> Analyses,
        IReadOnlyList<AnalysisResult> Results,
        IReadOnlyList<AnalysisParticipant> Participants,
        IReadOnlyList<AnalysisRescore> Rescores);

    private static async Task<StoredRows> ReadRowsAsync(Harness h)
    {
        await using var db = h.NewContext();
        return new StoredRows(
            await db.Analyses.AsNoTracking().ToListAsync(),
            await db.AnalysisResults.AsNoTracking().ToListAsync(),
            await db.AnalysisParticipants.AsNoTracking().OrderBy(p => p.ParticipantId).ToListAsync(),
            await db.AnalysisRescores.AsNoTracking().ToListAsync());
    }

    /// <summary>Every stored column of every row the re-evaluation could touch, as one string.</summary>
    private static string Fingerprint(StoredRows rows) => JsonSerializer.Serialize(rows);

    private static PersonIndicatorResult Indicator(AnalysisParticipant row, string id)
        => PersonEvaluationJson.TryDeserialize<List<PersonIndicatorResult>>(row.IndicatorsJson)!.Single(i => i.Id == id);

    private static PersonEvaluationSources Sources(AnalysisParticipant row)
        => PersonEvaluationJson.TryDeserialize<PersonEvaluationSources>(row.DocumentVersionsJson)!;

    private static PersonRescorePreviousValues Previous(AnalysisRescore history)
        => JsonSerializer.Deserialize<PersonRescorePreviousValues>(history.PreviousValuesJson, PersonEvaluationJson.Options)!;

    private sealed class RecordingRescoreLogger : ILogger<PersonAnalysisRescoreService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
