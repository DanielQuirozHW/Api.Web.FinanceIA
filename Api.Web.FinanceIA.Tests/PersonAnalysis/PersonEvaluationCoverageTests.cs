// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-20

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;
using Xunit;
using static Api.Web.FinanceIA.Tests.PersonAnalysis.PersonEvaluationFixtures;

namespace Api.Web.FinanceIA.Tests.PersonAnalysis;

/// <summary>
/// The documentary coverage index per regime, its recency rules and its "cannot conclude" statements; the
/// frozen DocumentVersionsJson; the evaluable-document guard; the parameter resolution.
/// </summary>
public sealed class PersonEvaluationCoverageTests
{
    private static PersonCoverageResult CoverageOf(PersonEvaluationInput input)
        => PersonEvaluationService.Evaluate(input).Coverage;

    // ── Per regime ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(FiscalRegimes.Monotributo, 6)]
    [InlineData(FiscalRegimes.ResponsableInscripto, 7)]
    [InlineData(FiscalRegimes.Exento, 4)]
    [InlineData(FiscalRegimes.Desconocido, 4)]
    public void RequiredTotal_FollowsTheRegimeMatrix(string regime, int requiredTotal)
    {
        var coverage = CoverageOf(Empty(regime));

        Assert.Equal(requiredTotal, coverage.RequiredTotal);
        Assert.Equal(0, coverage.RequiredPresent);
        Assert.Equal(0m, coverage.Index);
        Assert.Equal(LegajoExpectedDocuments.For(regime).Count, coverage.Items.Count);
    }

    [Fact]
    public void Monotributo_CountsOnlyReadyDocuments_AndListsPendingOnes()
    {
        var pending = Guid.NewGuid();
        var input = Empty(FiscalRegimes.Monotributo) with
        {
            Documents =
            [
                Doc(DocumentTypes.AutorizacionDatos),
                Doc(DocumentTypes.Monotributo, periodEnd: new DateOnly(2026, 9, 30)),
                Doc(DocumentTypes.CertificacionIngresos, periodEnd: new DateOnly(2026, 8, 31)),
                Doc(DocumentTypes.ManifestacionBienes, LegajoStatuses.NeedsReview, id: pending),
                Doc(DocumentTypes.DdjjIibb, LegajoStatuses.Failed)
            ]
        };

        var coverage = CoverageOf(input);

        Assert.Equal(3, coverage.RequiredPresent);
        Assert.Equal(0.5m, coverage.Index);

        var manifestacion = Assert.Single(coverage.Items, i => i.DocumentType == DocumentTypes.ManifestacionBienes);
        Assert.False(manifestacion.Present);
        Assert.Equal([pending], manifestacion.PendingDocumentIds);

        var iibb = Assert.Single(coverage.Items, i => i.DocumentType == DocumentTypes.DdjjIibb);
        Assert.Empty(iibb.PendingDocumentIds);   // a Failed document is not "on its way"
    }

    [Fact]
    public void Reibp_SatisfiesBienesPersonales()
    {
        var input = Empty() with { Documents = [Doc(DocumentTypes.Reibp, periodEnd: new DateOnly(2025, 12, 31))] };

        var item = Assert.Single(CoverageOf(input).Items, i => i.DocumentType == DocumentTypes.BienesPersonales);

        Assert.True(item.Present);
    }

    // ── Recency ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Recency_PointInTimeDocument_IsStaleAfterSixMonths()
    {
        var input = Empty() with { Documents = [Doc(DocumentTypes.ManifestacionBienes, periodEnd: new DateOnly(2026, 2, 28))] };

        var item = Assert.Single(CoverageOf(input).Items, i => i.DocumentType == DocumentTypes.ManifestacionBienes);

        Assert.Equal(7, item.AgeMonths);
        Assert.Equal(6, item.MaxAgeMonths);
        Assert.True(item.IsStale);
    }

    [Theory]
    [InlineData("2026-03-31", "2026-09-30", 6)]   // month-end into a shorter month: complete
    [InlineData("2026-03-31", "2026-09-29", 5)]
    [InlineData("2026-02-15", "2026-10-20", 8)]
    [InlineData("2026-02-28", "2026-10-20", 7)]
    public void MonthsBetween_CountsCompletedMonths(string from, string to, int expected)
    {
        Assert.Equal(expected, PersonEvaluationService.MonthsBetween(DateOnly.Parse(from), DateOnly.Parse(to)));
    }

    [Fact]
    public void Recency_AnnualReturn_UsesItsOwnCut()
    {
        var input = Empty() with { Documents = [Doc(DocumentTypes.Ganancias, periodEnd: new DateOnly(2025, 12, 31))] };

        var item = Assert.Single(CoverageOf(input).Items, i => i.DocumentType == DocumentTypes.Ganancias);

        Assert.Equal(18, item.MaxAgeMonths);
        Assert.False(item.IsStale);
    }

    [Fact]
    public void Recency_ConsentHasNoRule()
    {
        var input = Empty() with { Documents = [Doc(DocumentTypes.AutorizacionDatos)] };

        var item = Assert.Single(CoverageOf(input).Items, i => i.DocumentType == DocumentTypes.AutorizacionDatos);

        Assert.Null(item.MaxAgeMonths);
        Assert.False(item.IsStale);
    }

    [Fact]
    public void Snapshots_OlderThanTheReportLimitOrMissing_AreStale()
    {
        var input = Empty() with { BcraUpdatedAt = AsOfUtc.AddDays(-100), ArcaUpdatedAt = AsOfUtc.AddDays(-10) };

        var coverage = CoverageOf(input);

        Assert.True(Assert.Single(coverage.Snapshots, s => s.Source == "BCRA").IsStale);
        Assert.False(Assert.Single(coverage.Snapshots, s => s.Source == "ARCA").IsStale);
        Assert.True(CoverageOf(Empty()).Snapshots.All(s => s.IsStale));
    }

    // ── Cannot conclude ────────────────────────────────────────────────────────────

    [Fact]
    public void CannotConclude_MonotributistaWithoutInvoicing_IsFlujoSoloDeclarado()
    {
        var codes = CoverageOf(Empty(FiscalRegimes.Monotributo)).CannotConclude.Select(c => c.Code).ToList();

        Assert.Contains("flujo_solo_declarado", codes);
        Assert.Contains("sin_cuadro_resultados", codes);
        Assert.Contains("gastos_personales_no_disponibles", codes);
        Assert.DoesNotContain("consumo_sin_definir", codes);   // the default floor is defined
        Assert.Contains("cuit_estado_no_disponible", codes);
        Assert.Contains("bcra_no_disponible", codes);
    }

    [Fact]
    public void CannotConclude_CuitStatus_OnlyWhenTheCuitFilterHasNoData()
    {
        var withKey    = Empty() with { Arca = ArcaWithKey("ACTIVO") };
        var inactive   = Empty() with { Arca = ArcaWithKey("INACTIVO") };
        var withoutKey = Empty() with { Arca = ArcaWithKey(null) };

        Assert.DoesNotContain(CoverageOf(withKey).CannotConclude, c => c.Code == "cuit_estado_no_disponible");
        Assert.DoesNotContain(CoverageOf(inactive).CannotConclude, c => c.Code == "cuit_estado_no_disponible");
        Assert.Contains(CoverageOf(withoutKey).CannotConclude, c => c.Code == "cuit_estado_no_disponible");
        Assert.Contains(CoverageOf(Empty()).CannotConclude, c => c.Code == "cuit_estado_no_disponible");
    }

    [Fact]
    public void EngineVersion_Is010()
    {
        // 0.10: the coverage measures against the tenant's expected-documents matrix (per regime, per-line
        // recency cuts) and freezes it in the sources. 0.9: the fiador's investments are discounted by class (títulos públicos, acciones/ON con cotización, FCI;
        // the unclassified ones keep pf_haircut_inversion). 0.8: the cuotas vigentes declared by the analyst replace the estimate, and cash at hand is its own
        // kind, never counted. 0.7 brought the agro annual mode (campaign window, seasonal DDJJ recency) and the
        // cuota read by its periodicity; 0.6 the sourced defaults, the cuotas vigentes as a share of the balance,
        // the CPI-indexed consumo floor and the asset haircuts. Stored rows keep their own version.
        Assert.Equal("0.10", PersonEvaluationService.Version);
        Assert.Equal("0.10", PersonEvaluationService.Evaluate(Empty()).Version);
    }

    [Fact]
    public void CannotConclude_MonotributistaWithInvoicing_DropsTheStatement()
    {
        var input = Empty(FiscalRegimes.Monotributo) with { Incomes = Incomes(MonthlyIva(new DateOnly(2025, 10, 1), 12, _ => 1m)) };

        Assert.DoesNotContain(CoverageOf(input).CannotConclude, c => c.Code == "flujo_solo_declarado");
    }

    [Fact]
    public void CannotConclude_ResponsableInscripto_NamesTheMissingResultOrTheMissingAmortizaciones()
    {
        Assert.Contains(CoverageOf(Empty()).CannotConclude, c => c.Code == "sin_resultado_actividad");

        var withGanancias = Empty() with
        {
            Incomes = Incomes(Year(LegajoIncomeSources.Ganancias, LegajoIncomeConcepts.Renta3ra, 2025, 1m))
        };
        var codes = CoverageOf(withGanancias).CannotConclude.Select(c => c.Code).ToList();

        Assert.DoesNotContain("sin_resultado_actividad", codes);
        Assert.Contains("amortizaciones_intereses_no_disponibles", codes);
    }

    [Fact]
    public void CannotConclude_DesconocidoRegime_IsStated()
    {
        Assert.Contains(CoverageOf(Empty(FiscalRegimes.Desconocido)).CannotConclude, c => c.Code == "regimen_desconocido");
    }

    [Fact]
    public void CannotConclude_SalesWithoutCpi_AreNominalOnly()
    {
        var input = Empty() with { Incomes = Incomes(MonthlyIva(new DateOnly(2025, 10, 1), 12, _ => 1m)) };

        Assert.Contains(CoverageOf(input).CannotConclude, c => c.Code == "ventas_solo_nominales");
        Assert.DoesNotContain(CoverageOf(input with { InflationSeries = Cpi() }).CannotConclude, c => c.Code == "ventas_solo_nominales");
    }

    [Fact]
    public void Coverage_BlocksNothing_TheFlagOnlyFollowsTheFilters()
    {
        // Nothing at all in the legajo: coverage 0, yet no rejection is suggested on that ground.
        Assert.Equal(PersonSuggestions.ParaDecisionDelAnalista, PersonEvaluationService.Evaluate(Empty()).Suggestion);
    }

    // ── Frozen document versions ───────────────────────────────────────────────────

    [Fact]
    public void Sources_PinEachDocumentToItsRunAndOrderedCorrections()
    {
        var ganancias = new Document { DocumentId = Guid.NewGuid(), DocumentType = DocumentTypes.Ganancias, LegajoStatus = LegajoStatuses.Ready, UploadedAt = AsOfUtc.AddDays(-5), PeriodEnd = new DateTime(2025, 12, 31) };
        var consent   = new Document { DocumentId = Guid.NewGuid(), DocumentType = DocumentTypes.AutorizacionDatos, LegajoStatus = LegajoStatuses.Ready, UploadedAt = AsOfUtc.AddDays(-9) };
        var deleted   = new Document { DocumentId = Guid.NewGuid(), DocumentType = DocumentTypes.DdjjIva, LegajoStatus = LegajoStatuses.Ready, UploadedAt = AsOfUtc.AddDays(-7), IsDeleted = true };

        var run = new LegajoExtraction
        {
            ExtractionId = Guid.NewGuid(), DocumentId = ganancias.DocumentId, Kind = LegajoExtractionKinds.Extraction,
            SchemaVersion = "1.0", PromptVersion = "gan-1.0", Model = "claude-sonnet-4-6", OutputHash = "abc"
        };
        var classification = new LegajoExtraction
        {
            ExtractionId = Guid.NewGuid(), DocumentId = consent.DocumentId, Kind = LegajoExtractionKinds.Classification
        };
        var later   = new LegajoCorrection { CorrectionId = Guid.NewGuid(), ExtractionId = run.ExtractionId, CorrectedAt = AsOfUtc.AddDays(-1) };
        var earlier = new LegajoCorrection { CorrectionId = Guid.NewGuid(), ExtractionId = run.ExtractionId, CorrectedAt = AsOfUtc.AddDays(-2) };
        var legajo  = new PersonLegajo { PersonLegajoId = LegajoId, TenantId = Tenant, BcraDataUpdatedAt = AsOfUtc.AddDays(-4) };

        var sources = PersonEvaluationService.Sources(
            [ganancias, consent, deleted],
            new Dictionary<Guid, LegajoExtraction> { [ganancias.DocumentId] = run, [consent.DocumentId] = classification },
            [later, earlier],
            legajo, AsOfUtc.AddDays(-6), Inventory(), incomeRows: 3, macroFetchedAt: null, macroSource: null,
            PersonEvaluationParameters.Defaults);

        Assert.Equal([consent.DocumentId, ganancias.DocumentId], sources.Documents.Select(d => d.DocumentId));

        var pinned = Assert.Single(sources.Documents, d => d.DocumentId == ganancias.DocumentId);
        Assert.Equal(run.ExtractionId, pinned.ExtractionId);
        Assert.Equal("gan-1.0", pinned.PromptVersion);
        Assert.Equal([earlier.CorrectionId, later.CorrectionId], pinned.CorrectionIds);

        Assert.Null(Assert.Single(sources.Documents, d => d.DocumentId == consent.DocumentId).ExtractionId);
        Assert.Equal(legajo.BcraDataUpdatedAt, sources.BcraDataUpdatedAt);
        Assert.Equal(AsOfUtc.AddDays(-6), sources.LufeDataUpdatedAt);
        Assert.Equal(3, sources.IncomeRows);
        Assert.Equal(3, sources.Parameters.BcraSituacionRechazo);
    }

    [Fact]
    public void HasEvaluableDocument_TheConsentAloneDoesNotCount()
    {
        Document Make(string type, string status, bool deleted = false)
            => new() { DocumentId = Guid.NewGuid(), DocumentType = type, LegajoStatus = status, IsDeleted = deleted };

        Assert.False(PersonEvaluationService.HasEvaluableDocument([Make(DocumentTypes.AutorizacionDatos, LegajoStatuses.Ready)]));
        Assert.False(PersonEvaluationService.HasEvaluableDocument([Make(DocumentTypes.Ganancias, LegajoStatuses.NeedsReview)]));
        Assert.False(PersonEvaluationService.HasEvaluableDocument([Make(DocumentTypes.Ganancias, LegajoStatuses.Ready, deleted: true)]));
        Assert.True(PersonEvaluationService.HasEvaluableDocument([Make(DocumentTypes.Ganancias, LegajoStatuses.Ready)]));
    }

    // ── Parameters ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Parameters_TenantRowsOverrideTheDefaults_AndAreListed()
    {
        var parameters = PersonEvaluationParameters.From(new Dictionary<string, decimal>
        {
            ["pf_bcra_ventana_meses"] = 6m,
            ["weight_roe"]            = 0.5m   // not a pf_ key: ignored
        });

        Assert.Equal(6, parameters.BcraVentanaMeses);
        Assert.Equal(3, parameters.BcraSituacionRechazo);
        Assert.Equal(["pf_bcra_ventana_meses"], parameters.TenantOverrides);
    }

    [Fact]
    public void Defaults_AreAllProvisional_AndPrefixed()
    {
        Assert.All(PersonEvaluationDefaults.Values.Keys, k => Assert.StartsWith(PersonEvaluationDefaults.KeyPrefix, k));
        Assert.All(PersonEvaluationDefaults.Descriptions.Values, d => Assert.StartsWith("PROVISIONAL", d));
        Assert.Empty(PersonEvaluationDefaults.Values.Keys.Intersect(ScoringDefaults.Values.Keys));
    }
}
