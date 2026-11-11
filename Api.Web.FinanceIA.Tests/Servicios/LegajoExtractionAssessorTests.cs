// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-28

using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Tests.Legajo;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// <see cref="LegajoExtractionAssessor"/> (the review queues, the holder-CUIT comparison, the status)
/// and <see cref="LegajoExtractionSelection"/> (which pass is kept). Pure functions over synthetic trees.
/// </summary>
public sealed class LegajoExtractionAssessorTests
{
    private static readonly LegajoIntegrityService Integrity = new();

    private static JsonObject Bp(Action<JsonObject>? mutate = null)
    {
        var input = LegajoExtractionFixtures.BienesPersonales();
        mutate?.Invoke(input);
        return LegajoExtractionFixtures.Normalize(LegajoExtractionPrompts.BienesPersonales, input);
    }

    private static LegajoExtractionAssessment Assess(JsonObject tree, IReadOnlyCollection<string>? corrected = null,
                                                     LegajoExtractionSchema? schema = null)
        => LegajoExtractionAssessor.Assess(schema ?? LegajoExtractionPrompts.BienesPersonales, tree, Integrity,
            new DateOnly(2025, 12, 31), LegajoTestData.LegajoCuit, 0.7m, corrected);

    [Fact]
    public void Clean_Ready_NoQueues_HolderMatches()
    {
        var assessment = Assess(Bp());

        Assert.Equal(LegajoStatuses.Ready, assessment.Status);
        Assert.Empty(assessment.Missing);
        Assert.Empty(assessment.Flagged);
        Assert.True(assessment.HolderCuitMatches);
        Assert.Equal(LegajoTestData.LegajoCuit, assessment.HolderCuit);
        Assert.Equal(0.95m, assessment.MeanConfidence);
    }

    [Fact]
    public void RequiredFields_AndTheItemsOrRubroTotalsRule_FillTheMissingQueue()
    {
        var assessment = Assess(Bp(i =>
        {
            // Since the totalBienes requirement, a total is missing only when neither it nor both parts are printed.
            LegajoJsonPointer.Set(i, "/totals/totalBienes/value", null);
            LegajoJsonPointer.Set(i, "/totals/bienesPais/value", null);
            i["items"] = new JsonArray();
            i["rubroTotals"] = new JsonArray();
        }));

        // Requirements are appended after the required fields and the items-or-rubroTotals rule.
        Assert.Equal(["/items", "/totals/totalBienes"], assessment.Missing);
        Assert.Equal(LegajoStatuses.NeedsReview, assessment.Status);
    }

    [Fact]
    public void NullUnit_IsMissing_NotAssumed()
    {
        var assessment = Assess(Bp(i => LegajoJsonPointer.Set(i, "/header/amountsUnit/value", null)));

        Assert.Equal([LegajoExtractionVocabulary.AmountsUnitPath], assessment.Missing);
        Assert.Equal(LegajoStatuses.NeedsReview, assessment.Status);
    }

    [Fact]
    public void Mb_RequiredFields()
    {
        var input = LegajoExtractionFixtures.ManifestacionBienes();
        LegajoJsonPointer.Set(input, "/header/measurementDate/value", null);
        var tree = LegajoExtractionFixtures.Normalize(LegajoExtractionPrompts.ManifestacionBienes, input);

        var assessment = Assess(tree, schema: LegajoExtractionPrompts.ManifestacionBienes);

        Assert.Equal(["/header/measurementDate"], assessment.Missing);
    }

    [Fact]
    public void LowConfidence_FlagsTheFieldOrTheRow_UntilCorrected()
    {
        var tree = Bp(i =>
        {
            LegajoJsonPointer.Set(i, "/totals/baseImponible/confidence", 0.5m);
            LegajoJsonPointer.Set(i, "/items/2/confidence", 0.3m);
            LegajoJsonPointer.Set(i, "/totals/minimoNoImponible/confidence", 0.1m);   // no value: nothing to review
        });

        var before = Assess(tree);
        Assert.Equal(["/items/2", "/totals/baseImponible"], before.Flagged.Select(f => f.FieldPath));
        Assert.All(before.Flagged, f => Assert.Equal([LegajoExtractionAssessment.ReasonLowConfidence], f.Reasons));

        var after = Assess(tree, corrected: ["/totals/baseImponible", "/items/2/fiscalValue"]);
        Assert.Empty(after.Flagged);
        Assert.Equal(LegajoStatuses.Ready, after.Status);
    }

    [Fact]
    public void FailedTotalCheck_FlagsOnlyThePrintedTotal_AndMergesReasons()
    {
        var assessment = Assess(Bp(i =>
        {
            LegajoJsonPointer.Set(i, "/totals/totalBienes/value", 1_900_000m);
            LegajoJsonPointer.Set(i, "/totals/totalBienes/confidence", 0.2m);
        }));

        var total = Assert.Single(assessment.Flagged);
        Assert.Equal("/totals/totalBienes", total.FieldPath);
        Assert.Equal([LegajoExtractionAssessment.ReasonLowConfidence, LegajoExtractionAssessment.ReasonCheckFailed], total.Reasons);
        Assert.Equal(1, assessment.FailedChecks);

        // The parts are not flagged, but the check still names them for the Portal to highlight.
        var check = Assert.Single(assessment.Checks.Failed);
        Assert.Equal(["/totals/totalBienes", "/totals/bienesPais", "/totals/bienesExterior"], check.FieldPaths);
        Assert.Equal(["/totals/totalBienes"], check.FlaggedPaths);
    }

    [Fact]
    public void FailedPeerOrRangeCheck_StillFlagsEveryFieldPath()
    {
        // A range rule has no printed total: every offending path is flagged (flaggedPaths stays null).
        var assessment = Assess(Bp(i => LegajoJsonPointer.Set(i, "/items/0/ownershipPct", 150)));

        var check = Assert.Single(assessment.Checks.Failed);
        Assert.Null(check.FlaggedPaths);
        Assert.Equal(["/items/0/ownershipPct"], assessment.Flagged.Select(f => f.FieldPath));
    }

    /// <summary>
    /// The 2026-10-01 live test: the analyst corrected a rubro subtotal the model misread; the país total then
    /// disagreed with the subtotals, and "Para revisar" listed the país total AND every rubro subtotal. Only
    /// the printed total must be flagged; the check keeps the subtotals in its fieldPaths.
    /// </summary>
    [Fact]
    public void Scenario20261001_CorrectedRubroSubtotal_FlagsExactlyThePaisTotal()
    {
        // The Rodados item reads 260.000 and its subtotal was misread as 205.000; the printed país total
        // (1.400.000,50) does not add up to the subtotals as corrected.
        var schema = LegajoExtractionPrompts.BienesPersonales;
        var input  = LegajoExtractionFixtures.BienesPersonales();
        LegajoJsonPointer.Set(input, "/items/1/fiscalValue", 260_000m);
        LegajoJsonPointer.Set(input, "/rubroTotals/1/total", 205_000m);

        var run = new LegajoExtraction
        {
            ExtractionId   = Guid.NewGuid(),
            TenantId       = LegajoTestData.Tenant,
            PersonLegajoId = Guid.NewGuid(),
            DocumentId     = Guid.NewGuid(),
            Kind           = LegajoExtractionKinds.Extraction,
            DocumentType   = schema.DocumentType,
            SchemaVersion  = schema.SchemaVersion,
            PromptVersion  = schema.PromptVersion,
            Model          = "claude-sonnet-4-6",
            ReadMode       = LegajoReadModes.Document,
            Status         = LegajoExtractionStatuses.Succeeded,
            ResultJson     = LegajoExtractionFixtures.Normalize(schema, input).ToJsonString(),
            IsCurrent      = true,
            CreatedAt      = DateTime.UtcNow.AddHours(-1)
        };

        LegajoEffectiveExtraction Evaluate(params LegajoCorrection[] corrections)
            => LegajoExtractionReview.Evaluate(run, corrections, Integrity, new DateOnly(2025, 12, 31),
                                               LegajoTestData.LegajoCuit, 0.7m)!;

        static IEnumerable<string> CheckFailed(LegajoEffectiveExtraction e)
            => e.Assessment.Flagged
                .Where(f => f.Reasons.Contains(LegajoExtractionAssessment.ReasonCheckFailed))
                .Select(f => f.FieldPath);

        // Before: the misread subtotal (bp_rubro_items) and the país total (bp_rubro_totals_pais), nothing else.
        var before = Evaluate();
        Assert.Equal(["/rubroTotals/1/total", "/totals/bienesPais"], CheckFailed(before));

        // After the analyst corrects the subtotal to 260.000: only the país total.
        var after = Evaluate(new LegajoCorrection
        {
            CorrectionId       = Guid.NewGuid(),
            TenantId           = LegajoTestData.Tenant,
            ExtractionId       = run.ExtractionId,
            FieldPath          = "/rubroTotals/1/total",
            CorrectedValueJson = "260000",
            Reason             = "Leido del original",
            CorrectedBy        = Guid.NewGuid(),
            CorrectedAt        = DateTime.UtcNow
        });

        Assert.Equal(["/totals/bienesPais"], CheckFailed(after));
        Assert.Equal(LegajoStatuses.NeedsReview, after.Assessment.Status);

        var pais = Assert.Single(after.Assessment.Checks.Failed);
        Assert.Equal(LegajoIntegrityService.BpRubroTotalsPaisCheck, pais.Name);
        Assert.Equal(["/totals/bienesPais"], pais.FlaggedPaths);
        Assert.Equal(["/totals/bienesPais", "/rubroTotals/0/total", "/rubroTotals/1/total", "/rubroTotals/2/total"],
                     pais.FieldPaths);
    }

    [Fact]
    public void HolderCuit_OfAnotherPerson_Blocks_EvenWithOtherQueues()
    {
        var input = LegajoExtractionFixtures.BienesPersonales(holderCuit: LegajoTestData.OtherCuitDigits);
        LegajoJsonPointer.Set(input, "/header/fiscalYear/value", null);

        var assessment = Assess(LegajoExtractionFixtures.Normalize(LegajoExtractionPrompts.BienesPersonales, input));

        Assert.False(assessment.HolderCuitMatches);
        Assert.Equal(LegajoStatuses.Blocked, assessment.Status);
    }

    [Theory]
    [InlineData(LegajoTestData.InvalidCuitDigits)]
    [InlineData(null)]
    public void HolderCuit_InvalidOrAbsent_NeitherMatchesNorBlocks(string? cuit)
    {
        var input = LegajoExtractionFixtures.BienesPersonales(holderCuit: cuit);

        var assessment = Assess(LegajoExtractionFixtures.Normalize(LegajoExtractionPrompts.BienesPersonales, input));

        Assert.Null(assessment.HolderCuitMatches);
        Assert.Null(assessment.HolderCuit);
        Assert.Equal(LegajoStatuses.Ready, assessment.Status);
    }

    [Fact]
    public void HolderCuit_LowConfidence_IsNotAReviewItem()
    {
        var assessment = Assess(Bp(i => LegajoJsonPointer.Set(i, "/header/holderCuit/confidence", 0.1m)));

        Assert.Empty(assessment.Flagged);
    }

    // ── Phase 3a types ──────────────────────────────────────────────────────────

    public static TheoryData<string> TaxTypes =>
    [
        DocumentTypes.Ganancias, DocumentTypes.DdjjIva, DocumentTypes.DdjjIibb, DocumentTypes.CertificacionIngresos,
        DocumentTypes.Monotributo
    ];

    private static LegajoExtractionAssessment AssessClean(string documentType, Action<JsonObject>? mutate = null)
        => Assess(LegajoExtractionFixtures.NormalizedClean(documentType, mutate),
                  schema: LegajoExtractionFixtures.Schemas[documentType]);

    [Theory]
    [MemberData(nameof(TaxTypes))]
    public void TaxTypes_Clean_Ready_NoQueues_HolderMatches(string documentType)
    {
        var assessment = AssessClean(documentType);

        Assert.Equal(LegajoStatuses.Ready, assessment.Status);
        Assert.Empty(assessment.Missing);
        Assert.Empty(assessment.Flagged);
        Assert.Equal(0, assessment.FailedChecks);
        Assert.True(assessment.HolderCuitMatches);
    }

    [Theory]
    [MemberData(nameof(TaxTypes))]
    public void TaxTypes_AnotherPersonsCuit_Blocks(string documentType)
    {
        var input = LegajoExtractionFixtures.Clean(documentType, holderCuit: LegajoTestData.OtherCuitDigits);
        var schema = LegajoExtractionFixtures.Schemas[documentType];

        var assessment = Assess(LegajoExtractionFixtures.Normalize(schema, input), schema: schema);

        Assert.Equal(LegajoStatuses.Blocked, assessment.Status);
    }

    [Theory]
    [InlineData(DocumentTypes.Ganancias, "/header/fiscalYear")]
    [InlineData(DocumentTypes.Ganancias, "/header/amountsUnit")]
    [InlineData(DocumentTypes.DdjjIva, "/header/amountsUnit")]
    [InlineData(DocumentTypes.DdjjIibb, "/header/amountsUnit")]
    [InlineData(DocumentTypes.CertificacionIngresos, "/header/periodFrom")]
    [InlineData(DocumentTypes.CertificacionIngresos, "/header/periodTo")]
    [InlineData(DocumentTypes.CertificacionIngresos, "/header/amountsUnit")]
    [InlineData(DocumentTypes.Monotributo, "/header/amountsUnit")]
    public void TaxTypes_RequiredField_Null_IsMissing(string documentType, string path)
    {
        var assessment = AssessClean(documentType, i => LegajoJsonPointer.Set(i, path + "/value", null));

        Assert.Contains(path, assessment.Missing);
        Assert.Equal(LegajoStatuses.NeedsReview, assessment.Status);
    }

    [Fact]
    public void Ganancias_Requirement_TheGananciaNetaOrACategoryResult()
    {
        Assert.Empty(AssessClean(DocumentTypes.Ganancias, i => LegajoJsonPointer.Set(i, "/totals/gananciaNeta/value", null)).Missing);
        Assert.Empty(AssessClean(DocumentTypes.Ganancias, i => i["categoryResults"] = new JsonArray()).Missing);

        var neither = AssessClean(DocumentTypes.Ganancias, i =>
        {
            LegajoJsonPointer.Set(i, "/totals/gananciaNeta/value", null);
            i["categoryResults"] = new JsonArray();
        });
        Assert.Equal(["/totals/gananciaNeta"], neither.Missing);
    }

    [Fact]
    public void Iva_Requirement_APeriodWithItsSalesOrItsDebito()
    {
        // Sales gone, débito kept: still met.
        Assert.Empty(AssessClean(DocumentTypes.DdjjIva, i =>
        {
            LegajoJsonPointer.Set(i, "/periods/0/ventasNetoGravado", null);
            LegajoJsonPointer.Set(i, "/periods/1/ventasNetoGravado", null);
        }).Missing);

        // Figures without a period, or periods without figures: not met.
        var noPeriod = AssessClean(DocumentTypes.DdjjIva, i =>
        {
            LegajoJsonPointer.Set(i, "/periods/0/period", null);
            LegajoJsonPointer.Set(i, "/periods/1/period", null);
        });
        Assert.Equal(["/periods"], noPeriod.Missing);

        var noFigures = AssessClean(DocumentTypes.DdjjIva, i =>
        {
            foreach (var index in new[] { 0, 1 })
            {
                LegajoJsonPointer.Set(i, $"/periods/{index}/ventasNetoGravado", null);
                LegajoJsonPointer.Set(i, $"/periods/{index}/debitoFiscal", null);
            }
        });
        Assert.Contains("/periods", noFigures.Missing);
        Assert.Equal(["/periods"], AssessClean(DocumentTypes.DdjjIva, i => i["periods"] = new JsonArray()).Missing);
    }

    [Fact]
    public void Iibb_Requirement_ABaseOrATaxOrTheIngresosTotales()
    {
        var neither = AssessClean(DocumentTypes.DdjjIibb, i =>
        {
            i["rows"] = new JsonArray();
            LegajoJsonPointer.Set(i, "/periods/0/ingresosTotales", null);
            LegajoJsonPointer.Set(i, "/periods/1/ingresosTotales", null);
        });
        Assert.Equal(["/rows"], neither.Missing);

        Assert.Empty(AssessClean(DocumentTypes.DdjjIibb, i => i["rows"] = new JsonArray()).Missing);
    }

    [Fact]
    public void Ci_Requirement_TheTotalOrACertifiedAmount_AndAnUnstatedUnitIsMissing()
    {
        Assert.Empty(AssessClean(DocumentTypes.CertificacionIngresos,
            i => LegajoJsonPointer.Set(i, "/totals/totalIngresos/value", null)).Missing);

        var neither = AssessClean(DocumentTypes.CertificacionIngresos, i =>
        {
            LegajoJsonPointer.Set(i, "/totals/totalIngresos/value", null);
            i["rows"] = new JsonArray();
        });
        Assert.Equal(["/totals/totalIngresos"], neither.Missing);
    }

    [Fact]
    public void Mono_Requirement_TheCategoriaOrAPayment()
    {
        Assert.Empty(AssessClean(DocumentTypes.Monotributo, i => LegajoJsonPointer.Set(i, "/header/categoria/value", null)).Missing);
        Assert.Empty(AssessClean(DocumentTypes.Monotributo, i => i["payments"] = new JsonArray()).Missing);

        var neither = AssessClean(DocumentTypes.Monotributo, i =>
        {
            LegajoJsonPointer.Set(i, "/header/categoria/value", null);
            i["payments"] = new JsonArray();
        });
        Assert.Equal(["/header/categoria"], neither.Missing);
    }

    [Fact]
    public void Iva_FailedTotal_FlagsTheDebitoAndTheSaldo_NeverTheLines()
    {
        var assessment = AssessClean(DocumentTypes.DdjjIva, i => LegajoJsonPointer.Set(i, "/periods/0/debitoFiscal", 240_000m));

        Assert.Equal(["/periods/0/debitoFiscal", "/periods/0/saldoTecnico/amount", "/periods/0/saldoTecnico/side"],
                     assessment.Flagged.Select(f => f.FieldPath));
        Assert.Equal(LegajoStatuses.NeedsReview, assessment.Status);
    }

    // ── Selection ───────────────────────────────────────────────────────────────

    private static LegajoExtractionAssessment Candidate(int failedChecks, int missing, decimal? confidence)
    {
        var checks = Enumerable.Range(0, failedChecks)
            .Select(i => new LegajoIntegrityCheck { Name = "c" + i, Passed = false })
            .ToList();
        var report = new LegajoIntegrityReport { Passed = failedChecks == 0, AmountsUnit = "Pesos", Checks = checks, Skipped = [] };
        return new LegajoExtractionAssessment(report, Enumerable.Repeat("/x", missing).ToList(), [], null, null, confidence);
    }

    [Fact]
    public void Selection_FewerFailedChecks_ThenFewerMissing_ThenHigherConfidence_TieKeepsTheFirst()
    {
        Assert.True(LegajoExtractionSelection.IsBetter(Candidate(2, 0, 0.9m), Candidate(1, 3, 0.1m)));
        Assert.False(LegajoExtractionSelection.IsBetter(Candidate(1, 3, 0.1m), Candidate(2, 0, 0.9m)));
        Assert.True(LegajoExtractionSelection.IsBetter(Candidate(1, 2, 0.9m), Candidate(1, 1, 0.5m)));
        Assert.True(LegajoExtractionSelection.IsBetter(Candidate(0, 0, 0.8m), Candidate(0, 0, 0.9m)));
        Assert.False(LegajoExtractionSelection.IsBetter(Candidate(0, 0, 0.9m), Candidate(0, 0, 0.9m)));
    }

    [Fact]
    public void Selection_AnUnusablePassAlwaysLoses()
    {
        Assert.True(LegajoExtractionSelection.IsBetter(null, Candidate(5, 5, 0.1m)));
        Assert.False(LegajoExtractionSelection.IsBetter(Candidate(5, 5, 0.1m), null));
        Assert.False(LegajoExtractionSelection.IsBetter(null, null));
    }
}
