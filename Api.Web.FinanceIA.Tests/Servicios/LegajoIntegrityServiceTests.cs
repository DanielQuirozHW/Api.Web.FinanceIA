// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-28

using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Tests.Legajo;
using Xunit;
using S = Api.Web.FinanceIA.Servicios.LegajoIntegrityService;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// <see cref="LegajoIntegrityService"/>: every check passing on the clean synthetic documents, failing
/// when one figure is off, skipped when an operand is missing, the tolerance edges (relative and the
/// rounding allowance), the unit scaling, and the message conventions. It never edits its input.
/// </summary>
public sealed class LegajoIntegrityServiceTests
{
    private static readonly S Sut = new();
    private static readonly DateOnly PeriodEnd2025 = new(2025, 12, 31);

    private static JsonObject Bp(Action<JsonObject>? mutate = null)
    {
        var input = LegajoExtractionFixtures.BienesPersonales();
        mutate?.Invoke(input);
        return LegajoExtractionFixtures.Normalize(LegajoExtractionPrompts.BienesPersonales, input);
    }

    private static JsonObject Mb(Action<JsonObject>? mutate = null)
    {
        var input = LegajoExtractionFixtures.ManifestacionBienes();
        mutate?.Invoke(input);
        return LegajoExtractionFixtures.Normalize(LegajoExtractionPrompts.ManifestacionBienes, input);
    }

    private static LegajoIntegrityReport EvaluateBp(JsonObject result, DateOnly? periodEnd = null)
        => Sut.Evaluate(DocumentTypes.BienesPersonales, result, periodEnd ?? PeriodEnd2025);

    private static LegajoIntegrityReport EvaluateMb(JsonObject result)
        => Sut.Evaluate(DocumentTypes.ManifestacionBienes, result, PeriodEnd2025);

    private static void Set(JsonObject input, string pointer, JsonNode? value) => Assert.True(LegajoJsonPointer.Set(input, pointer, value));

    // ── Clean documents ─────────────────────────────────────────────────────────

    [Fact]
    public void Bp_Clean_EveryCheckRunsAndPasses()
    {
        var report = EvaluateBp(Bp());

        Assert.True(report.Passed);
        Assert.Equal("Pesos", report.AmountsUnit);
        Assert.False(report.AmountsUnitAssumed);
        Assert.Equal(
            [S.BpRubroItemsCheck, S.BpRubroItemsCheck, S.BpRubroItemsCheck, S.BpRubroItemsCheck, S.BpRubroTotalsPaisCheck,
             S.BpRubroTotalsExteriorCheck, S.BpTotalBienesCheck, S.BpOwnershipPctCheck, S.BpNonNegativeCheck, S.BpFiscalYearPeriodCheck],
            report.Checks.Select(c => c.Name));
        Assert.Empty(report.Skipped);
        Assert.Contains(report.Checks, c => c.Subject == "Depositos/Exterior");
    }

    [Fact]
    public void Mb_Clean_EveryCheckRunsAndPasses_InMiles()
    {
        var report = EvaluateMb(Mb());

        Assert.True(report.Passed, string.Join("; ", report.Failed.Select(c => c.Message)));
        Assert.Equal("Miles", report.AmountsUnit);
        Assert.Empty(report.Skipped);
        Assert.Equal(4, report.Checks.Count(c => c.Name == S.MbSectionLinesCheck));
        Assert.Equal(3, report.Checks.Count(c => c.Name == S.MbAnnexItemsCheck));

        // Amounts are scaled to pesos.
        var total = report.Checks.Single(c => c.Name == S.MbTotalActivoCheck);
        Assert.Equal(12_000_000m, total.Actual);
    }

    [Fact]
    public void Evaluate_NeverModifiesItsInput()
    {
        var result = Bp(i => Set(i, "/totals/totalBienes/value", 1m));
        var before = result.ToJsonString();

        EvaluateBp(result);

        Assert.Equal(before, result.ToJsonString());
    }

    // ── Bienes Personales: failing ──────────────────────────────────────────────

    [Fact]
    public void Bp_ItemsDoNotAddUpToTheRubroTotal_Fails_WithTheFieldPaths()
    {
        var report = EvaluateBp(Bp(i => Set(i, "/items/1/fiscalValue", 260_000m)));

        var check = Assert.Single(report.Failed);
        Assert.Equal(S.BpRubroItemsCheck, check.Name);
        Assert.Equal("Rodados/Pais", check.Subject);
        Assert.Equal(260_000m, check.Expected);
        Assert.Equal(250_000m, check.Actual);
        Assert.Equal(10_000m, check.Diff);
        Assert.Equal(["/rubroTotals/1/total", "/items/1/fiscalValue"], check.FieldPaths);
        Assert.Equal(["/rubroTotals/1/total"], check.FlaggedPaths);
        Assert.Equal("La suma de los bienes del rubro rodados en el país ($260.000) no coincide con el total informado " +
                     "para ese rubro ($250.000).", check.Message);
    }

    // ── Flag the printed total, not every operand ────────────────────────────────

    [Fact]
    public void TotalVsPartsChecks_FlagOnlyThePrintedTotal_KeepEveryOperandInFieldPaths()
    {
        // Each total-vs-parts check, on a clean document: flaggedPaths is the printed total (or result).
        var bp = EvaluateBp(Bp());
        Assert.Equal(["/rubroTotals/1/total"], bp.Checks.Single(c => c.Subject == "Rodados/Pais").FlaggedPaths);
        Assert.Equal(["/totals/bienesPais"], bp.Checks.Single(c => c.Name == S.BpRubroTotalsPaisCheck).FlaggedPaths);
        Assert.Equal(["/totals/bienesExterior"], bp.Checks.Single(c => c.Name == S.BpRubroTotalsExteriorCheck).FlaggedPaths);
        Assert.Equal(["/totals/totalBienes"], bp.Checks.Single(c => c.Name == S.BpTotalBienesCheck).FlaggedPaths);

        var mb = EvaluateMb(Mb());
        Assert.Equal(["/totals/activoCorriente"], mb.Checks.Single(c => c.Subject == "ActivoCorriente").FlaggedPaths);
        Assert.Equal(["/totals/pasivoNoCorriente"], mb.Checks.Single(c => c.Subject == "PasivoNoCorriente").FlaggedPaths);
        Assert.Equal(["/totals/totalActivo"], mb.Checks.Single(c => c.Name == S.MbTotalActivoCheck).FlaggedPaths);
        Assert.Equal(["/totals/totalPasivo"], mb.Checks.Single(c => c.Name == S.MbTotalPasivoCheck).FlaggedPaths);
        Assert.Equal(["/totals/patrimonioNeto"], mb.Checks.Single(c => c.Name == S.MbPatrimonioNetoCheck).FlaggedPaths);
        Assert.Equal(["/incomeStatement/resultado"], mb.Checks.Single(c => c.Name == S.MbIncomeResultCheck).FlaggedPaths);
        // Annex A details the two lines that reference it: those statement lines are the printed total.
        Assert.Equal(["/lines/2/amount", "/lines/3/amount"], mb.Checks.Single(c => c.Subject == "A").FlaggedPaths);

        // Every flagged path is one of the check's field paths.
        foreach (var check in bp.Checks.Concat(mb.Checks).Where(c => c.FlaggedPaths is not null))
            Assert.All(check.FlaggedPaths!, path => Assert.Contains(path, check.FieldPaths));
    }

    [Fact]
    public void RangeSignAndSingleFieldChecks_HaveNoFlaggedPaths_SoEveryPathIsFlagged()
    {
        var bp = EvaluateBp(Bp());
        var mb = EvaluateMb(Mb());

        foreach (var name in new[] { S.BpOwnershipPctCheck, S.BpNonNegativeCheck, S.BpFiscalYearPeriodCheck })
            Assert.Null(bp.Checks.Single(c => c.Name == name).FlaggedPaths);
        Assert.Null(mb.Checks.Single(c => c.Name == S.MbOwnershipPctCheck).FlaggedPaths);

        var failed = new LegajoIntegrityCheck { Name = "x", Passed = false, FieldPaths = ["/a", "/b"] };
        Assert.Equal(["/a", "/b"], failed.PathsToFlag);
        Assert.Equal(["/a"], (failed with { FlaggedPaths = ["/a"] }).PathsToFlag);
    }

    [Fact]
    public void FlaggedPaths_IsAdditive_OmittedFromTheJsonWhenNull()
    {
        var report = EvaluateBp(Bp());

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(report));
        var checks = json.RootElement.GetProperty("checks").EnumerateArray().ToList();
        var total = checks.Single(c => c.GetProperty("name").GetString() == S.BpTotalBienesCheck);
        var range = checks.Single(c => c.GetProperty("name").GetString() == S.BpOwnershipPctCheck);

        Assert.Equal(["/totals/totalBienes"], total.GetProperty("flaggedPaths").EnumerateArray().Select(p => p.GetString()!));
        Assert.False(range.TryGetProperty("flaggedPaths", out _));
        Assert.False(range.TryGetProperty("pathsToFlag", out _));

        // A run stored before the property existed reads back with every field path to flag.
        const string storedJson =
            "{\"name\":\"bp_total_bienes\",\"passed\":false,\"fieldPaths\":[\"/totals/totalBienes\",\"/totals/bienesPais\"]}";
        var stored = JsonSerializer.Deserialize<LegajoIntegrityCheck>(storedJson)!;
        Assert.Null(stored.FlaggedPaths);
        Assert.Equal(["/totals/totalBienes", "/totals/bienesPais"], stored.PathsToFlag);
    }

    [Fact]
    public void Bp_TotalBienes_Off_Fails()
    {
        var report = EvaluateBp(Bp(i => Set(i, "/totals/totalBienes/value", 1_800_000m)));

        var check = Assert.Single(report.Failed);
        Assert.Equal(S.BpTotalBienesCheck, check.Name);
        Assert.Contains("/totals/totalBienes", check.FieldPaths);
    }

    [Fact]
    public void Bp_RubroTotalsPais_Off_Fails()
    {
        var report = EvaluateBp(Bp(i => Set(i, "/totals/bienesPais/value", 1_300_000.50m)));

        Assert.Contains(report.Failed, c => c.Name == S.BpRubroTotalsPaisCheck);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(120)]
    [InlineData(-5)]
    public void Bp_OwnershipOutsideTheRange_Fails(int pct)
    {
        var report = EvaluateBp(Bp(i => Set(i, "/items/0/ownershipPct", pct)));

        var check = Assert.Single(report.Failed);
        Assert.Equal(S.BpOwnershipPctCheck, check.Name);
        Assert.Equal(["/items/0/ownershipPct"], check.FieldPaths);
        Assert.StartsWith("1 de los bienes informa un porcentaje de titularidad fuera de rango", check.Message);
    }

    [Fact]
    public void Bp_NegativeValue_Fails()
    {
        var report = EvaluateBp(Bp(i =>
        {
            Set(i, "/totals/bienesExentos/value", -1m);
        }));

        var check = Assert.Single(report.Failed);
        Assert.Equal(S.BpNonNegativeCheck, check.Name);
        Assert.Equal(["/totals/bienesExentos"], check.FieldPaths);
    }

    [Fact]
    public void Bp_FiscalYearOtherThanThePeriodEnd_Fails()
    {
        var report = EvaluateBp(Bp(), periodEnd: new DateOnly(2024, 12, 31));

        var check = Assert.Single(report.Failed);
        Assert.Equal(S.BpFiscalYearPeriodCheck, check.Name);
        Assert.Equal("El período fiscal informado (2025) no coincide con el año de cierre del documento (2024).", check.Message);
    }

    // ── Bienes Personales: skipped ──────────────────────────────────────────────

    [Fact]
    public void Bp_MissingOperands_SkipInsteadOfFailing()
    {
        var report = Sut.Evaluate(DocumentTypes.BienesPersonales, Bp(i =>
        {
            Set(i, "/totals/bienesExterior/value", null);
            Set(i, "/items/0/fiscalValue", null);
            Set(i, "/header/fiscalYear/value", null);
        }), periodEnd: null);

        Assert.True(report.Passed);
        Assert.Contains(new LegajoIntegritySkip(S.BpTotalBienesCheck), report.Skipped);
        Assert.Contains(new LegajoIntegritySkip(S.BpRubroTotalsExteriorCheck), report.Skipped);
        Assert.Contains(new LegajoIntegritySkip(S.BpRubroItemsCheck, "Inmuebles/Pais"), report.Skipped);
        Assert.Contains(new LegajoIntegritySkip(S.BpFiscalYearPeriodCheck), report.Skipped);
    }

    [Fact]
    public void Bp_RubroTotalWithoutItemizedAssets_IsSkipped()
    {
        var report = EvaluateBp(Bp(i => i["items"]!.AsArray().RemoveAt(1)));   // no Rodados item left

        Assert.Contains(new LegajoIntegritySkip(S.BpRubroItemsCheck, "Rodados/Pais"), report.Skipped);
        Assert.True(report.Passed);
    }

    // ── Tolerance ───────────────────────────────────────────────────────────────

    [Fact]
    public void Tolerance_RelativeHalfPercentPasses_JustAboveFails()
    {
        // totalBienes 1.700.000,50; 0.5 in a hundred of ~1.708.500 is ~8.542.
        Assert.True(EvaluateBp(Bp(i => Set(i, "/totals/totalBienes/value", 1_708_500m))).Passed);
        Assert.Contains(EvaluateBp(Bp(i => Set(i, "/totals/totalBienes/value", 1_708_600m))).Failed,
            c => c.Name == S.BpTotalBienesCheck);
    }

    [Fact]
    public void Tolerance_RoundingAllowanceScalesWithTheUnitAndTheOperands()
    {
        // En miles, one printed unit is $1.000: two summed operands may drift by one unit.
        var withinRounding = EvaluateMb(Mb(i => Set(i, "/totals/totalActivo/value", 12_001m)));
        Assert.DoesNotContain(withinRounding.Failed, c => c.Name == S.MbTotalActivoCheck);

        // Small figures, where the relative tolerance is tiny: the allowance is what passes them.
        var small = Mb(i =>
        {
            Set(i, "/header/amountsUnit/value", "Pesos");
            Set(i, "/totals/activoCorriente/value", 2_001m);   // lines add up to 2.000: one peso off, two lines
        });
        Assert.DoesNotContain(EvaluateMb(small).Failed, c => c is { Name: S.MbSectionLinesCheck, Subject: "ActivoCorriente" });

        var beyond = Mb(i =>
        {
            Set(i, "/header/amountsUnit/value", "Pesos");
            Set(i, "/totals/activoCorriente/value", 2_020m);
        });
        Assert.Contains(EvaluateMb(beyond).Failed, c => c is { Name: S.MbSectionLinesCheck, Subject: "ActivoCorriente" });
    }

    // ── Manifestación de bienes: failing and skipped ────────────────────────────

    [Fact]
    public void Mb_PatrimonioNeto_Off_Fails_QuotedInMiles()
    {
        var report = EvaluateMb(Mb(i => Set(i, "/totals/patrimonioNeto/value", 9_300m)));

        var check = Assert.Single(report.Failed);
        Assert.Equal(S.MbPatrimonioNetoCheck, check.Name);
        Assert.Equal("El patrimonio neto informado ($9.300, en miles) no coincide con el activo total menos el pasivo " +
                     "total ($8.300, en miles).", check.Message);
    }

    [Fact]
    public void Mb_AnnexItemsDoNotAddUpToTheReferencingLines_Fails()
    {
        var report = EvaluateMb(Mb(i => Set(i, "/annexItems/1/marketValue", 2_500m)));

        var check = Assert.Single(report.Failed);
        Assert.Equal(S.MbAnnexItemsCheck, check.Name);
        Assert.Equal("A", check.Subject);
        Assert.Contains("/annexItems/1/marketValue", check.FieldPaths);
        Assert.Contains("/lines/3/amount", check.FieldPaths);
    }

    [Fact]
    public void Mb_AnnexRefSpelledDifferently_StillMatches()
    {
        var report = EvaluateMb(Mb(i => Set(i, "/annexItems/2/annexRef", "Anexo b")));

        Assert.True(report.Passed);
        Assert.Contains(report.Checks, c => c is { Name: S.MbAnnexItemsCheck, Subject: "Anexo b", Passed: true });
    }

    [Fact]
    public void Mb_IncomeStatement_Off_Fails_Absent_Skips()
    {
        Assert.Contains(EvaluateMb(Mb(i => Set(i, "/incomeStatement/resultado/value", 1_000m))).Failed,
            c => c.Name == S.MbIncomeResultCheck);

        var without = EvaluateMb(Mb(i => i["incomeStatement"] = null));
        Assert.Contains(new LegajoIntegritySkip(S.MbIncomeResultCheck), without.Skipped);
    }

    [Fact]
    public void Mb_SectionWithoutLines_IsSkipped()
    {
        var report = EvaluateMb(Mb(i => i["lines"]!.AsArray().RemoveAt(4)));   // the only PasivoCorriente line

        Assert.Contains(new LegajoIntegritySkip(S.MbSectionLinesCheck, "PasivoCorriente"), report.Skipped);
    }

    [Fact]
    public void UnreadUnit_IsAssumedPesos_AndSaidSo()
    {
        var report = EvaluateMb(Mb(i => Set(i, "/header/amountsUnit/value", null)));

        Assert.True(report.AmountsUnitAssumed);
        Assert.Equal("Pesos", report.AmountsUnit);
    }

    [Fact]
    public void Messages_NeverContainAPercentSign_AndChecksJsonHasTheDocumentedShape()
    {
        var bp = EvaluateBp(Bp(i =>
        {
            Set(i, "/items/0/ownershipPct", 150);
            Set(i, "/totals/totalBienes/value", 1m);
            Set(i, "/items/1/fiscalValue", -1m);
        }), periodEnd: new DateOnly(2020, 1, 1));
        var mb = EvaluateMb(Mb(i => Set(i, "/totals/patrimonioNeto/value", 1m)));

        foreach (var check in bp.Failed.Concat(mb.Failed))
            Assert.DoesNotContain("%", check.Message);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(bp));
        var root = json.RootElement;
        Assert.False(root.GetProperty("passed").GetBoolean());
        var first = root.GetProperty("checks")[0];
        foreach (var key in new[] { "name", "passed", "expected", "actual", "diff", "fieldPaths" })
            Assert.True(first.TryGetProperty(key, out _), key);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("skipped").ValueKind);
    }

    [Fact]
    public void Overflow_IsAFailedArithmeticCheck_NeverAnException()
    {
        // Bypasses the reader's bound on purpose: a hand-edited row, or a figure scaled by millions.
        var tree = Bp();
        LegajoJsonPointer.Set(tree, "/header/amountsUnit/value", "Millones");
        LegajoJsonPointer.Set(tree, "/totals/totalBienes/value", 1e26m);

        var report = EvaluateBp(tree);

        Assert.False(report.Passed);
        var check = Assert.Single(report.Checks, c => c.Name == S.ArithmeticErrorCheck);
        Assert.False(check.Passed);
        Assert.DoesNotContain("%", check.Message);
    }

    [Fact]
    public void Overflow_InTheAssessor_GoesToNeedsReview()
    {
        var tree = Bp();
        LegajoJsonPointer.Set(tree, "/header/amountsUnit/value", "Millones");
        LegajoJsonPointer.Set(tree, "/totals/totalBienes/value", 1e26m);

        var assessment = LegajoExtractionAssessor.Assess(LegajoExtractionPrompts.BienesPersonales, tree, Sut,
            PeriodEnd2025, LegajoTestData.LegajoCuit, 0.7m);

        Assert.Equal(LegajoStatuses.NeedsReview, assessment.Status);
        Assert.True(assessment.FailedChecks > 0);
    }

    [Fact]
    public void UnsupportedType_YieldsAnEmptyPassedReport()
    {
        var report = Sut.Evaluate(DocumentTypes.Reibp, new JsonObject(), null);

        Assert.True(report.Passed);
        Assert.Empty(report.Checks);
    }
}
