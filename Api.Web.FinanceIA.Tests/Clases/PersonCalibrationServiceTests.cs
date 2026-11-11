// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-30

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Data.Repositories;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;
using static Api.Web.FinanceIA.Tests.Clases.PersonCalibrationFixture;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// GET /api/dashboard/person-calibration at the service: the population, each section, the derived flags and
/// their isolation — over real repositories on EF InMemory.
/// </summary>
public sealed class PersonCalibrationServiceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static Task<PersonCalibrationResponse> BoardAsync(PersonCalibrationFixture f, Guid? teamId = null, IPersonCalibrationRepository? repository = null)
        => f.Service(repository).GetBoardAsync(DashboardFixture.Tenant, October, teamId, DashboardFixture.Now, Ct);

    // ── population ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Board_IgnoresBalanceAnalyses_OtherTenants_AndAnalysesOutsideThePeriod()
    {
        await using var f = PersonCalibrationFixture.Create(nameof(Board_IgnoresBalanceAnalyses_OtherTenants_AndAnalysesOutsideThePeriod));
        var analyst = f.Dashboard.AddUser(Roles.Analyst);

        var (inRange, _) = f.AddPersonAnalysis(analyst.UserId);
        f.AddSolicitante(inRange.AnalysisId);

        // A balance analysis in range, with a stray solicitante-looking row: still ignored everywhere.
        var (balance, _) = f.Dashboard.AddAnalysis(analyst.UserId, analysis: a => a.CreatedAt = new DateTime(2026, 10, 10, 15, 0, 0, DateTimeKind.Utc));
        f.AddSolicitante(balance.AnalysisId, [Filter("bcra_situacion", PersonFilterOutcomes.Rechazo)]);

        var (before, _) = f.AddPersonAnalysis(analyst.UserId, createdAtUtc: new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc));
        f.AddSolicitante(before.AnalysisId);

        var other = f.Dashboard.AddUser(Roles.Analyst, tenant: DashboardFixture.Other);
        var (foreign, _) = f.AddPersonAnalysis(other.UserId, tenant: DashboardFixture.Other);
        f.AddSolicitante(foreign.AnalysisId, tenant: DashboardFixture.Other);
        await f.SaveAsync();

        var board = await BoardAsync(f);

        Assert.Empty(board.DegradedSections);
        Assert.Equal(1, board.Summary!.Total);
        Assert.Equal(1, board.Filters!.Items.Single().Total);
        Assert.Equal(0, board.UnreadableRows!.Count);
        // The pilot milestone is tenant-wide: the September analysis counts, the balance and foreign ones do not.
        Assert.Equal(2, board.Summary.Pilot.TotalToDate);
        Assert.Equal(new DateTime(2026, 9, 20, 12, 0, 0), board.Summary.Pilot.FirstAnalysisAt);
        Assert.True(board.Summary.Pilot.Reached30Days);
        Assert.False(board.Summary.Pilot.Reached20);
        Assert.Equal(PersonCalibrationTeamAttributions.CurrentMembership, board.TeamAttribution);
    }

    [Fact]
    public async Task Board_PeriodBoundaries_AreArgentineDays()
    {
        await using var f = PersonCalibrationFixture.Create(nameof(Board_PeriodBoundaries_AreArgentineDays));
        var analyst = f.Dashboard.AddUser(Roles.Analyst);

        // 23:30 ART on the 23rd (= 02:30 UTC on the 24th): in. 00:10 ART on the 24th (= 03:10 UTC): out.
        var (lastMinute, _) = f.AddPersonAnalysis(analyst.UserId, createdAtUtc: new DateTime(2026, 10, 24, 2, 30, 0, DateTimeKind.Utc));
        f.AddSolicitante(lastMinute.AnalysisId);
        var (nextDay, _) = f.AddPersonAnalysis(analyst.UserId, createdAtUtc: new DateTime(2026, 10, 24, 3, 10, 0, DateTimeKind.Utc));
        f.AddSolicitante(nextDay.AnalysisId);
        // 00:00 ART on the 1st (= 03:00 UTC): in. 23:59 ART on Sep 30 (= 02:59 UTC on Oct 1): out.
        var (firstMinute, _) = f.AddPersonAnalysis(analyst.UserId, createdAtUtc: new DateTime(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc));
        f.AddSolicitante(firstMinute.AnalysisId);
        var (dayBefore, _) = f.AddPersonAnalysis(analyst.UserId, createdAtUtc: new DateTime(2026, 10, 1, 2, 59, 0, DateTimeKind.Utc));
        f.AddSolicitante(dayBefore.AnalysisId);
        await f.SaveAsync();

        var file = await f.Service().ExportAsync(DashboardFixture.Tenant, October, null, Ct);

        Assert.Equal(new[] { firstMinute.AnalysisId, lastMinute.AnalysisId }.Order(), file.AnalysisIds.Order());
    }

    [Fact]
    public async Task Board_TeamFilter_UsesTheRequestersCurrentMembership()
    {
        await using var f = PersonCalibrationFixture.Create(nameof(Board_TeamFilter_UsesTheRequestersCurrentMembership));
        var team    = f.Dashboard.AddTeam(1, []);
        var member  = f.Dashboard.AddUser(Roles.Analyst, teamId: team.TeamId);
        var outsider = f.Dashboard.AddUser(Roles.Analyst);

        var (mine, _) = f.AddPersonAnalysis(member.UserId);
        f.AddSolicitante(mine.AnalysisId);
        var (theirs, _) = f.AddPersonAnalysis(outsider.UserId);
        f.AddSolicitante(theirs.AnalysisId);
        await f.SaveAsync();

        var board = await BoardAsync(f, team.TeamId);
        Assert.Equal(1, board.Summary!.Total);
        Assert.Equal(team.TeamId, board.TeamId);

        // The analyst moves out of the team: their history leaves with them — "current_membership".
        var row = await f.Dashboard.Context.Users.FindAsync(member.UserId);
        row!.TeamId = null;
        await f.SaveAsync();

        Assert.Equal(0, (await BoardAsync(f, team.TeamId)).Summary!.Total);
        Assert.Equal(2, (await BoardAsync(f)).Summary!.Total);
    }

    // ── summary and decisions ─────────────────────────────────────────────────

    [Fact]
    public async Task Summary_CountsByStatus_BySuggestion_AndDecided()
    {
        await using var f = PersonCalibrationFixture.Create(nameof(Summary_CountsByStatus_BySuggestion_AndDecided));
        var analyst = f.Dashboard.AddUser(Roles.Analyst);

        foreach (var (status, outcome) in new[]
        {
            (AnalysisStatuses.Approved, PersonFilterOutcomes.Ok),
            (AnalysisStatuses.Rejected, PersonFilterOutcomes.Rechazo),
            (AnalysisStatuses.PreApproved, PersonFilterOutcomes.Rechazo),
            (AnalysisStatuses.PendingReview, PersonFilterOutcomes.Alerta)
        })
        {
            var (a, _) = f.AddPersonAnalysis(analyst.UserId, status);
            f.AddSolicitante(a.AnalysisId, [Filter("f1", outcome)]);
        }
        await f.SaveAsync();

        var summary = (await BoardAsync(f)).Summary!;

        Assert.Equal(4, summary.Total);
        Assert.Equal(2, summary.Decided);
        Assert.Equal(2, summary.Pending);
        Assert.All(summary.ByStatus, s => Assert.Equal(1, s.Count));
        Assert.Equal(2, summary.BySuggestion.Single(s => s.Code == PersonSuggestions.RechazoSugerido).Count);
        Assert.Equal(2, summary.BySuggestion.Single(s => s.Code == PersonSuggestions.ParaDecisionDelAnalista).Count);
    }

    [Fact]
    public async Task Decisions_DeriveTheSuggestion_TheMatrix_AndEachFlag()
    {
        await using var f = PersonCalibrationFixture.Create(nameof(Decisions_DeriveTheSuggestion_TheMatrix_AndEachFlag));
        var analyst  = f.Dashboard.AddUser(Roles.Analyst, name: "Ana");
        var manager  = f.Dashboard.AddUser(Roles.RiskManager, name: "Rita");
        var entity   = f.Dashboard.AddEntity(name: "PyME Uno");

        // Approved against a Rechazo sugerido, with the reason on the confirmation checks.
        var (againstRechazo, _) = f.AddPersonAnalysis(analyst.UserId, AnalysisStatuses.Approved, entityId: entity.EntityId, analysis: a =>
        {
            a.ReviewedBy             = manager.UserId;
            a.ReviewedAt             = DashboardFixture.Now.AddDays(-1);
            a.ReviewNotes            = "nota";
            a.ConfirmationChecksJson = $$"""{"counterGuaranteesConfirmed":true,"fdrLimitConfirmed":true,"reason":"conozco al cliente","by":"{{manager.UserId}}","at":"2026-10-22T12:00:00Z"}""";
        });
        f.AddSolicitante(againstRechazo.AnalysisId, [Filter("bcra_situacion", PersonFilterOutcomes.Rechazo), Filter("antiguedad", PersonFilterOutcomes.Ok)]);

        // Pre-approved with a red band: the analyst's approving act is flagged before confirmation.
        var (redBand, _) = f.AddPersonAnalysis(analyst.UserId, AnalysisStatuses.PreApproved, analysis: a =>
        {
            a.PreApprovedBy = analyst.UserId;
            a.PreApprovedAt = DashboardFixture.Now.AddDays(-2);
            a.PreApprovalChecksJson = $$"""{"counterGuaranteesConfirmed":true,"fdrLimitConfirmed":true,"reason":"garantía sólida","by":"{{analyst.UserId}}","at":"2026-10-21T12:00:00Z"}""";
        });
        f.AddSolicitante(redBand.AnalysisId, indicators: [Indicator("dscr", PersonIndicatorBands.Rojo), Indicator("cuota_ingreso", PersonIndicatorBands.Verde)]);

        // Rejected in green: the reason is the review notes (a rejection writes no checks).
        var (inGreen, _) = f.AddPersonAnalysis(analyst.UserId, AnalysisStatuses.Rejected, analysis: a =>
        {
            a.ReviewedBy  = analyst.UserId;
            a.ReviewedAt  = DashboardFixture.Now.AddDays(-1);
            a.ReviewNotes = "no me convence";
        });
        f.AddSolicitante(inGreen.AnalysisId, indicators: [Indicator("dscr", PersonIndicatorBands.Verde)]);

        // Approved with no reason to flag; rejected with a red band (not "in green").
        var (clean, _) = f.AddPersonAnalysis(analyst.UserId, AnalysisStatuses.Approved);
        f.AddSolicitante(clean.AnalysisId, indicators: [Indicator("dscr", PersonIndicatorBands.Verde)]);
        var (rejectedRed, _) = f.AddPersonAnalysis(analyst.UserId, AnalysisStatuses.Rejected);
        f.AddSolicitante(rejectedRed.AnalysisId, indicators: [Indicator("dscr", PersonIndicatorBands.Rojo)]);
        await f.SaveAsync();

        var decisions = (await BoardAsync(f)).Decisions!;

        var rechazoRow = decisions.Matrix.Single(m => m.Suggestion == PersonSuggestions.RechazoSugerido);
        Assert.Equal(1, rechazoRow.Approved);
        Assert.Equal(0, rechazoRow.Rejected);
        var paraRow = decisions.Matrix.Single(m => m.Suggestion == PersonSuggestions.ParaDecisionDelAnalista);
        Assert.Equal(1, paraRow.Approved);
        Assert.Equal(2, paraRow.Rejected);
        Assert.Equal(1, paraRow.PreApproved);

        Assert.Equal(3, decisions.FlaggedCases.TotalCount);
        var cases = decisions.FlaggedCases.Items.ToDictionary(c => c.AnalysisId);

        var first = cases[againstRechazo.AnalysisId];
        Assert.True(first.Flags.ApprovedWithRechazoSugerido);
        Assert.False(first.Flags.ApprovedWithRedBand);
        Assert.Equal(PersonSuggestions.RechazoSugerido, first.Suggestion);
        Assert.Equal(["bcra_situacion"], first.RechazoFilterIds);
        Assert.Equal("conozco al cliente", first.Reason);
        Assert.Equal(manager.UserId, first.DecidedBy);
        Assert.Equal("Rita", first.DecidedByName);
        Assert.Equal("Ana", first.RequestedByName);
        Assert.Equal("PyME Uno", first.EntityName);
        Assert.Null(first.Flags.CounterGuaranteeBelowMin);

        var second = cases[redBand.AnalysisId];
        Assert.True(second.Flags.ApprovedWithRedBand);
        Assert.False(second.Flags.ApprovedWithRechazoSugerido);
        Assert.Equal(["dscr"], second.RojoIndicatorIds);
        Assert.Equal(PersonCalibrationOutcomes.PreApproved, second.Outcome);
        Assert.Equal(analyst.UserId, second.DecidedBy);
        Assert.Equal("garantía sólida", second.Reason);

        var third = cases[inGreen.AnalysisId];
        Assert.True(third.Flags.RejectedInGreen);
        Assert.Equal("no me convence", third.Reason);

        Assert.DoesNotContain(clean.AnalysisId, cases.Keys);
        Assert.DoesNotContain(rejectedRed.AnalysisId, cases.Keys);
    }

    [Fact]
    public async Task CounterGuaranteeBelowMin_UsesTheFrozenMinimum_NotTheTenantsCurrentOne()
    {
        await using var f = PersonCalibrationFixture.Create(nameof(CounterGuaranteeBelowMin_UsesTheFrozenMinimum_NotTheTenantsCurrentOne));
        var analyst = f.Dashboard.AddUser(Roles.Analyst);

        // Both cover 60 % of the aval (600k of 1M). One froze a 50 % minimum, the other an 80 % one.
        var (frozenLow, _) = f.AddPersonAnalysis(analyst.UserId, AnalysisStatuses.Approved);
        f.AddSolicitante(frozenLow.AnalysisId, parameters: Parameters(minPct: 50));
        f.AddFiador(frozenLow.AnalysisId, executable: 600_000m, amountAtCharge: 1_000_000m);

        var (frozenHigh, _) = f.AddPersonAnalysis(analyst.UserId, AnalysisStatuses.Approved);
        f.AddSolicitante(frozenHigh.AnalysisId, parameters: Parameters(minPct: 80));
        f.AddFiador(frozenHigh.AnalysisId, executable: 600_000m, amountAtCharge: 1_000_000m);

        // The tenant has since moved its minimum to 90 %: neither verdict may change.
        f.Dashboard.AddConfig("pf_contragarantia_cobertura_min_pct", 90m);

        // No fiadores → null; a USD operation → no ratio → null; frozen parameters predating the key → null.
        var (noFiadores, _) = f.AddPersonAnalysis(analyst.UserId, AnalysisStatuses.Approved);
        f.AddSolicitante(noFiadores.AnalysisId);
        var (usd, _) = f.AddPersonAnalysis(analyst.UserId, AnalysisStatuses.Approved);
        f.AddSolicitante(usd.AnalysisId, currency: "USD");
        f.AddFiador(usd.AnalysisId, executable: 1m, amountAtCharge: 1_000_000m, currency: "USD");
        var (preKey, _) = f.AddPersonAnalysis(analyst.UserId, AnalysisStatuses.Approved);
        f.AddSolicitante(preKey.AnalysisId, parameters: Parameters(minPct: null));
        f.AddFiador(preKey.AnalysisId, executable: 1m, amountAtCharge: 1_000_000m);
        await f.SaveAsync();

        var file  = await f.Service().ExportAsync(DashboardFixture.Tenant, October, null, Ct);
        var lines = Csv(file);
        string Flag(Guid id) => lines.Single(l => l[0] == id.ToString())[^1];

        Assert.Equal("No", Flag(frozenLow.AnalysisId));
        Assert.Equal("Sí", Flag(frozenHigh.AnalysisId));
        Assert.Equal("", Flag(noFiadores.AnalysisId));
        Assert.Equal("", Flag(usd.AnalysisId));
        Assert.Equal("", Flag(preKey.AnalysisId));

        // Only the approval below its frozen minimum is a flagged case, with its ratio and that minimum.
        var flagged = Assert.Single((await BoardAsync(f)).Decisions!.FlaggedCases.Items);
        Assert.Equal(frozenHigh.AnalysisId, flagged.AnalysisId);
        Assert.True(flagged.Flags.CounterGuaranteeBelowMin);
        Assert.Equal(0.6m, flagged.CounterGuaranteeRatio);
        Assert.Equal(80, flagged.FrozenMinCounterGuaranteePct);
    }

    // ── filters, indicators, documents ────────────────────────────────────────

    [Fact]
    public async Task Filters_RankBySinDatos_AndCountCannotConcludeCodes()
    {
        await using var f = PersonCalibrationFixture.Create(nameof(Filters_RankBySinDatos_AndCountCannotConcludeCodes));
        var analyst = f.Dashboard.AddUser(Roles.Analyst);

        var cannot = Coverage(cannotConclude: [new PersonCannotConclude("sin_bcra", "t")]);
        var (a1, _) = f.AddPersonAnalysis(analyst.UserId);
        f.AddSolicitante(a1.AnalysisId, [Filter("lufe", PersonFilterOutcomes.SinDatos), Filter("bcra", PersonFilterOutcomes.SinDatos)], coverage: cannot);
        var (a2, _) = f.AddPersonAnalysis(analyst.UserId);
        f.AddSolicitante(a2.AnalysisId, [Filter("lufe", PersonFilterOutcomes.SinDatos), Filter("bcra", PersonFilterOutcomes.Ok)], coverage: cannot);
        var (a3, _) = f.AddPersonAnalysis(analyst.UserId);
        f.AddSolicitante(a3.AnalysisId, [Filter("lufe", PersonFilterOutcomes.Alerta), Filter("bcra", PersonFilterOutcomes.Rechazo), Filter("arca", PersonFilterOutcomes.Ok)]);
        await f.SaveAsync();

        var filters = (await BoardAsync(f)).Filters!;

        Assert.Equal(["lufe", "bcra", "arca"], filters.Items.Select(i => i.FilterId));
        var lufe = filters.Items[0];
        Assert.Equal((2, 1, 0, 0, 3), (lufe.SinDatos, lufe.Alerta, lufe.Ok, lufe.Rechazo, lufe.Total));
        Assert.Equal(0.6667m, lufe.SinDatosRate);
        var bcra = filters.Items[1];
        Assert.Equal((1, 1, 1), (bcra.SinDatos, bcra.Ok, bcra.Rechazo));
        Assert.Equal(2, Assert.Single(filters.CannotConclude).Count);
    }

    [Fact]
    public async Task Indicators_CountBands_SinDatos_AndTheDecisionsInEach()
    {
        await using var f = PersonCalibrationFixture.Create(nameof(Indicators_CountBands_SinDatos_AndTheDecisionsInEach));
        var analyst = f.Dashboard.AddUser(Roles.Analyst);

        var (a1, _) = f.AddPersonAnalysis(analyst.UserId, AnalysisStatuses.Approved);
        f.AddSolicitante(a1.AnalysisId, indicators: [Indicator("dscr", PersonIndicatorBands.Rojo)]);
        var (a2, _) = f.AddPersonAnalysis(analyst.UserId, AnalysisStatuses.Rejected);
        f.AddSolicitante(a2.AnalysisId, indicators: [Indicator("dscr", PersonIndicatorBands.Rojo)]);
        var (a3, _) = f.AddPersonAnalysis(analyst.UserId, AnalysisStatuses.Approved);
        f.AddSolicitante(a3.AnalysisId, indicators: [Indicator("dscr", null, PersonIndicatorStatuses.SinDatos)]);
        await f.SaveAsync();

        var dscr = Assert.Single((await BoardAsync(f)).Indicators!);
        Assert.Equal(["Rojo", "SinDatos"], dscr.Bands.Select(b => b.Band));
        var rojo = dscr.Bands[0];
        Assert.Equal((2, 1, 1), (rojo.Count, rojo.Approved, rojo.Rejected));
        Assert.Equal(1, dscr.Bands[1].Approved);
    }

    [Fact]
    public async Task Documents_CountMissing_Stale_SeasonalGap_AndStaleSnapshots()
    {
        await using var f = PersonCalibrationFixture.Create(nameof(Documents_CountMissing_Stale_SeasonalGap_AndStaleSnapshots));
        var analyst = f.Dashboard.AddUser(Roles.Analyst);

        var (a1, _) = f.AddPersonAnalysis(analyst.UserId);
        f.AddSolicitante(a1.AnalysisId, coverage: Coverage(FiscalRegimes.Monotributo,
            [Item(DocumentTypes.Monotributo, present: false), Item(DocumentTypes.DdjjIibb, isStale: true), Item(DocumentTypes.FlujoProyectado, required: false, present: false)],
            [Snapshot("BCRA", isStale: true), Snapshot("ARCA", isStale: false)]));
        var (a2, _) = f.AddPersonAnalysis(analyst.UserId);
        f.AddSolicitante(a2.AnalysisId, coverage: Coverage(FiscalRegimes.Monotributo,
            [Item(DocumentTypes.Monotributo), Item(DocumentTypes.DdjjIibb, seasonalGap: true)],
            [Snapshot("BCRA", isStale: true)]));
        var (a3, _) = f.AddPersonAnalysis(analyst.UserId);
        f.AddSolicitante(a3.AnalysisId, coverage: Coverage(FiscalRegimes.ResponsableInscripto, [Item(DocumentTypes.Monotributo, present: false)]));
        await f.SaveAsync();

        var documents = (await BoardAsync(f)).Documents!;
        PersonCalibrationDocument Doc(string regime, string type) => documents.Items.Single(d => d.FiscalRegime == regime && d.DocumentType == type);

        var monotributo = Doc(FiscalRegimes.Monotributo, DocumentTypes.Monotributo);
        Assert.Equal((2, 2, 1, 0), (monotributo.Analyses, monotributo.Required, monotributo.Missing, monotributo.Stale));
        var iibb = Doc(FiscalRegimes.Monotributo, DocumentTypes.DdjjIibb);
        Assert.Equal((0, 1, 1), (iibb.Missing, iibb.Stale, iibb.SeasonalGap));
        var flujo = Doc(FiscalRegimes.Monotributo, DocumentTypes.FlujoProyectado);
        Assert.Equal((0, 0), (flujo.Required, flujo.Missing));
        Assert.Equal(1, Doc(FiscalRegimes.ResponsableInscripto, DocumentTypes.Monotributo).Missing);

        var bcra = documents.Snapshots.Single(s => s.Source == "BCRA");
        Assert.Equal((2, 2), (bcra.Total, bcra.Stale));
        Assert.Equal(0, documents.Snapshots.Single(s => s.Source == "ARCA").Stale);
    }

    // ── reevaluations and parameters ──────────────────────────────────────────

    [Fact]
    public async Task Reevaluations_CountAnalyses_Rows_AndSuggestionFlips()
    {
        await using var f = PersonCalibrationFixture.Create(nameof(Reevaluations_CountAnalyses_Rows_AndSuggestionFlips));
        var analyst = f.Dashboard.AddUser(Roles.Analyst, name: "Ana");

        var (a1, r1) = f.AddPersonAnalysis(analyst.UserId);
        f.AddSolicitante(a1.AnalysisId);
        f.AddRescore(r1, analyst.UserId, DashboardFixture.Now.AddDays(-3), PersonSuggestions.RechazoSugerido, PersonSuggestions.ParaDecisionDelAnalista);
        f.AddRescore(r1, analyst.UserId, DashboardFixture.Now.AddDays(-1), PersonSuggestions.ParaDecisionDelAnalista, PersonSuggestions.ParaDecisionDelAnalista);
        var (a2, r2) = f.AddPersonAnalysis(analyst.UserId);
        f.AddSolicitante(a2.AnalysisId);
        f.AddRescore(r2, analyst.UserId, DashboardFixture.Now.AddDays(-2), PersonSuggestions.RechazoSugerido, PersonSuggestions.ParaDecisionDelAnalista);
        f.AddPersonAnalysis(analyst.UserId); // never re-evaluated, and unreadable: still part of the population
        await f.SaveAsync();

        var reevaluations = (await BoardAsync(f)).Reevaluations!;

        Assert.Equal(2, reevaluations.AnalysesReevaluated);
        Assert.Equal(3, reevaluations.TotalReevaluations);
        Assert.Equal(2, reevaluations.SuggestionChanges);
        var top = reevaluations.Flips[0];
        Assert.Equal((PersonSuggestions.RechazoSugerido, PersonSuggestions.ParaDecisionDelAnalista, 2), (top.Previous, top.New, top.Count));
        Assert.Equal(a1.AnalysisId, reevaluations.Recent[0].AnalysisId);
        Assert.Equal("Ana", reevaluations.Recent[0].RescoredByName);
    }

    [Fact]
    public async Task Parameters_EngineVersionsTied_SortAsDottedNumbers_And010SourcesAddNoFrozenDifference()
    {
        await using var f = PersonCalibrationFixture.Create(nameof(Parameters_EngineVersionsTied_SortAsDottedNumbers_And010SourcesAddNoFrozenDifference));
        var analyst = f.Dashboard.AddUser(Roles.Analyst);

        var (a1, _) = f.AddPersonAnalysis(analyst.UserId);
        f.AddSolicitante(a1.AnalysisId, version: "0.10");
        var (a2, _) = f.AddPersonAnalysis(analyst.UserId);
        f.AddSolicitante(a2.AnalysisId, version: "0.9");
        var (a3, _) = f.AddPersonAnalysis(analyst.UserId);
        var withMatrix = f.AddSolicitante(a3.AnalysisId, version: "0.10");
        var (a4, _) = f.AddPersonAnalysis(analyst.UserId);
        f.AddSolicitante(a4.AnalysisId, version: "0.9");

        // A 0.10 blob carries the frozen expected-documents matrix, outside the parameters.
        var sources = PersonEvaluationJson.TryDeserialize<PersonEvaluationSources>(withMatrix.DocumentVersionsJson)! with
        {
            ExpectedDocuments = new PersonExpectedDocumentsForRegime
            {
                Regime = ExpectedDocumentRegimes.Monotributo,
                Source = ExpectedDocumentsSources.Tenant,
                Lines  = [new(DocumentTypes.AutorizacionDatos, true, []), new(DocumentTypes.Monotributo, true, [], MaxAgeMonths: 3)]
            }
        };
        withMatrix.DocumentVersionsJson = PersonEvaluationJson.Serialize(sources);
        await f.SaveAsync();

        var parameters = (await BoardAsync(f)).Parameters!;

        // Text order would put "0.10" first; on a tie the earlier engine comes first.
        Assert.Equal([("0.9", 2), ("0.10", 2)], parameters.EvaluationVersions.Select(v => (v.Code, v.Count)));
        Assert.Empty(parameters.FrozenDifferences);
    }

    [Fact]
    public async Task Parameters_ShowCurrentVsDefault_Overrides_Versions_AndFrozenDifferences()
    {
        await using var f = PersonCalibrationFixture.Create(nameof(Parameters_ShowCurrentVsDefault_Overrides_Versions_AndFrozenDifferences));
        var analyst = f.Dashboard.AddUser(Roles.Analyst);
        f.Dashboard.AddConfig("pf_contragarantia_cobertura_min_pct", 75m);
        f.Dashboard.AddConfig("pf_contragarantia_cobertura_min_pct", 10m, tenant: DashboardFixture.Other);

        var (a1, _) = f.AddPersonAnalysis(analyst.UserId);
        f.AddSolicitante(a1.AnalysisId, parameters: Parameters(minPct: 60), version: "0.6");
        var (a2, _) = f.AddPersonAnalysis(analyst.UserId);
        f.AddSolicitante(a2.AnalysisId, parameters: Parameters(minPct: 75), version: "0.7");
        var (a3, _) = f.AddPersonAnalysis(analyst.UserId);
        f.AddSolicitante(a3.AnalysisId, parameters: Parameters(minPct: 75), version: "0.7");
        await f.SaveAsync();

        var parameters = (await BoardAsync(f)).Parameters!;

        Assert.Equal(PersonEvaluationDefaults.Layout.Count, parameters.Items.Count);
        var min = parameters.Items.Single(p => p.Key == "pf_contragarantia_cobertura_min_pct");
        Assert.True(min.Overridden);
        Assert.Equal(75m, min.CurrentValue);
        Assert.Equal(PersonEvaluationDefaults.Get("pf_contragarantia_cobertura_min_pct"), min.DefaultValue);
        Assert.Equal(DashboardFixture.Now, min.UpdatedAt);

        var untouched = parameters.Items.Single(p => p.Key == "pf_dscr_rojo");
        Assert.False(untouched.Overridden);
        Assert.Equal(untouched.DefaultValue, untouched.CurrentValue);
        Assert.Null(untouched.UpdatedAt);

        Assert.Equal([("0.7", 2), ("0.6", 1)], parameters.EvaluationVersions.Select(v => (v.Code, v.Count)));

        // Only the parameter that changed mid-pilot is listed.
        var difference = Assert.Single(parameters.FrozenDifferences);
        Assert.Equal("contragarantiaCoberturaMinPct", difference.Parameter);
        Assert.Equal([("75", 2), ("60", 1)], difference.Values.Select(v => (v.Value, v.Count)));
    }

    // ── robustness ────────────────────────────────────────────────────────────

    [Fact]
    public async Task UnreadableRows_AreSkippedAndCounted_AndTheRestOfTheBoardRenders()
    {
        await using var f = PersonCalibrationFixture.Create(nameof(UnreadableRows_AreSkippedAndCounted_AndTheRestOfTheBoardRenders));
        var analyst = f.Dashboard.AddUser(Roles.Analyst);

        var (good, _) = f.AddPersonAnalysis(analyst.UserId, AnalysisStatuses.Approved);
        f.AddSolicitante(good.AnalysisId, [Filter("bcra", PersonFilterOutcomes.Rechazo)]);

        var (garbled, _) = f.AddPersonAnalysis(analyst.UserId, AnalysisStatuses.Approved);
        f.AddSolicitante(garbled.AnalysisId).FiltersJson = "[{not json";

        var (noSolicitante, _) = f.AddPersonAnalysis(analyst.UserId);
        await f.SaveAsync();

        var board = await BoardAsync(f);

        Assert.Empty(board.DegradedSections);
        Assert.Equal(2, board.UnreadableRows!.Count);
        Assert.Equal(new[] { garbled.AnalysisId, noSolicitante.AnalysisId }.Order(), board.UnreadableRows.AnalysisIds.Order());
        Assert.Equal(3, board.Summary!.Total);
        Assert.Equal(1, board.Summary.BySuggestion.Sum(s => s.Count));
        Assert.Equal(1, Assert.Single(board.Filters!.Items).Rechazo);
        Assert.Equal(good.AnalysisId, Assert.Single(board.Decisions!.FlaggedCases.Items).AnalysisId);
    }

    [Fact]
    public async Task UnknownJsonMembers_FromANewerEngine_AreIgnored()
    {
        await using var f = PersonCalibrationFixture.Create(nameof(UnknownJsonMembers_FromANewerEngine_AreIgnored));
        var analyst = f.Dashboard.AddUser(Roles.Analyst);

        var (a, _) = f.AddPersonAnalysis(analyst.UserId);
        var solicitante = f.AddSolicitante(a.AnalysisId);
        solicitante.FiltersJson = """[{"id":"bcra","outcome":"SinDatos","evidence":"e","source":"s","existingInstallmentsAmount":123}]""";
        await f.SaveAsync();

        var board = await BoardAsync(f);

        Assert.Equal(0, board.UnreadableRows!.Count);
        Assert.Equal(1, Assert.Single(board.Filters!.Items).SinDatos);
    }

    [Fact]
    public async Task AFailingSection_LandsInDegradedSections_AndTheOthersRender()
    {
        await using var f = PersonCalibrationFixture.Create(nameof(AFailingSection_LandsInDegradedSections_AndTheOthersRender));
        var analyst = f.Dashboard.AddUser(Roles.Analyst);
        var (a, _) = f.AddPersonAnalysis(analyst.UserId);
        f.AddSolicitante(a.AnalysisId);
        await f.SaveAsync();

        var failing = new FailingPersonCalibrationRepository(f.Repository(), nameof(IPersonCalibrationRepository.GetRescoresAsync));
        var board   = await BoardAsync(f, repository: failing);

        Assert.Equal([PersonCalibrationSections.Reevaluations], board.DegradedSections);
        Assert.Null(board.Reevaluations);
        Assert.NotNull(board.Summary);
        Assert.NotNull(board.Parameters);
    }

    [Fact]
    public async Task APopulationThatCannotLoad_DegradesEverySectionThatNeedsIt_StillA200()
    {
        await using var f = PersonCalibrationFixture.Create(nameof(APopulationThatCannotLoad_DegradesEverySectionThatNeedsIt_StillA200));

        var failing = new FailingPersonCalibrationRepository(f.Repository(), nameof(IPersonCalibrationRepository.GetAnalysesAsync));
        var board   = await BoardAsync(f, repository: failing);

        Assert.Equal(
            [PersonCalibrationSections.Summary, PersonCalibrationSections.Decisions, PersonCalibrationSections.Filters,
             PersonCalibrationSections.Indicators, PersonCalibrationSections.Documents, PersonCalibrationSections.Reevaluations,
             PersonCalibrationSections.Parameters],
            board.DegradedSections);
        Assert.Null(board.UnreadableRows);
    }

    [Fact]
    public async Task Cancellation_Propagates_AndIsNotASectionFailure()
    {
        await using var f = PersonCalibrationFixture.Create(nameof(Cancellation_Propagates_AndIsNotASectionFailure));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            f.Service().GetBoardAsync(DashboardFixture.Tenant, October, null, DashboardFixture.Now, cts.Token));
    }

    // ── CSV rows ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Export_WritesOneRowPerAnalysis_WithEsArLabels_AndTheUnreadableOnesMarked()
    {
        await using var f = PersonCalibrationFixture.Create(nameof(Export_WritesOneRowPerAnalysis_WithEsArLabels_AndTheUnreadableOnesMarked));
        var team    = f.Dashboard.AddTeam(1, []);
        var analyst = f.Dashboard.AddUser(Roles.Analyst, teamId: team.TeamId, name: "Ana");
        var entity  = f.Dashboard.AddEntity(name: "=HYPERLINK(\"x\")");

        var (a1, r1) = f.AddPersonAnalysis(analyst.UserId, AnalysisStatuses.Approved, entityId: entity.EntityId, analysis: a =>
        {
            a.ReviewedBy  = analyst.UserId;
            a.ReviewedAt  = new DateTime(2026, 10, 12, 2, 0, 0, DateTimeKind.Utc); // 11/10 23:00 ART
            a.ReviewNotes = "motivo; con separador";
        });
        f.AddSolicitante(a1.AnalysisId, [Filter("bcra_situacion", PersonFilterOutcomes.Rechazo)],
            coverage: Coverage(items: [Item(DocumentTypes.Monotributo, present: false)]));
        f.AddFiador(a1.AnalysisId, executable: 255_000m, amountAtCharge: 1_000_000m);
        f.AddRescore(r1, analyst.UserId, DashboardFixture.Now, PersonSuggestions.RechazoSugerido, PersonSuggestions.RechazoSugerido);

        var (a2, _) = f.AddPersonAnalysis(analyst.UserId);
        await f.SaveAsync();

        var file = await f.Service().ExportAsync(DashboardFixture.Tenant, October, null, Ct);
        Assert.Equal("calibracion_persona_2026-10-01_2026-10-23.csv", file.FileName);

        var lines = Csv(file);
        Assert.Equal(3, lines.Count);
        Assert.Equal(PersonCalibrationService.CsvHeaders, lines[0]);

        var row = lines.Single(l => l[0] == a1.AnalysisId.ToString());
        string Col(string header) => row[PersonCalibrationService.CsvHeaders.ToList().IndexOf(header)];
        Assert.Equal("10/10/2026", Col("Fecha de alta"));
        Assert.Equal("Ana", Col("Analista"));
        Assert.Equal("Equipo", Col("Equipo"));
        Assert.Equal("'=HYPERLINK(\"x\")", Col("Entidad"));
        Assert.Equal("Aprobado", Col("Estado"));
        Assert.Equal("Rechazo sugerido", Col("Sugerencia"));
        Assert.Equal("11/10/2026", Col("Fecha de decisión"));
        Assert.Equal("motivo; con separador", Col("Motivo"));
        Assert.Equal("25,5", Col("Cobertura de contragarantía (%)"));
        Assert.Equal("60", Col("Mínimo congelado (%)"));
        Assert.Equal("1", Col("Cantidad de reanálisis"));
        Assert.Equal("0.7", Col("Versión del motor"));
        Assert.Equal("Sí", Col("Aprobado con rechazo sugerido"));
        Assert.Equal("Sí", Col("Contragarantía bajo el mínimo"));
        Assert.NotEqual("", Col("Filtros en Rechazo"));
        Assert.NotEqual("", Col("Documentos faltantes"));

        var unreadable = lines.Single(l => l[0] == a2.AnalysisId.ToString());
        Assert.Equal(PersonCalibrationService.UnreadableSuggestionLabel, unreadable[PersonCalibrationService.CsvHeaders.ToList().IndexOf("Sugerencia")]);
    }

    /// <summary>Parses the export: strips the BOM, splits rows on CRLF and cells on ';', honouring RFC 4180 quotes.</summary>
    internal static List<string[]> Csv(PersonCalibrationExportFile file)
    {
        var text  = System.Text.Encoding.UTF8.GetString(file.Content).TrimStart('﻿');
        var rows  = new List<string[]>();
        var cells = new List<string>();
        var cell  = new System.Text.StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else cell.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ';') { cells.Add(cell.ToString()); cell.Clear(); }
            else if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                cells.Add(cell.ToString()); cell.Clear();
                rows.Add([.. cells]); cells.Clear();
                i++;
            }
            else cell.Append(ch);
        }

        return rows;
    }
}
