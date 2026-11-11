// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-06

using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Tests.Legajo;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// <see cref="LegajoBienesPersonalesSummary"/> and its place on the extraction view. The clean BP fixture
/// (synthetic figures) holds: Inmuebles Pais 1.000.000 (/items/0), Rodados Pais 250.000 (/items/1),
/// Depositos Pais 150.000,50 (/items/2), Depositos Exterior 300.000 (/items/3), one rubro total per pair
/// in the same order, país 1.400.000,50, exterior 300.000, total 1.700.000,50.
/// </summary>
public sealed class LegajoBienesPersonalesSummaryTests
{
    private static JsonObject Bp(Action<JsonObject>? mutate = null)
        => LegajoExtractionFixtures.NormalizedClean(DocumentTypes.BienesPersonales, mutate);

    private static LegajoBienesPersonalesSummaryResponse Summary(JsonObject tree, string unit = LegajoExtractionVocabulary.Pesos)
        => LegajoBienesPersonalesSummary.Build(tree, unit);

    // An extra item: a copy of /items/0 with another rubro, location and value.
    private static void AddItem(JsonObject input, string rubro, string location, decimal? fiscalValue)
    {
        var item = input["items"]![0]!.DeepClone().AsObject();
        item["rubro"]       = rubro;
        item["location"]    = location;
        item["description"] = "Bien sintetico " + rubro;
        item["matchKey"]    = new JsonObject { ["kind"] = "Ninguno", ["value"] = null };
        item["isHousing"]   = false;
        item["fiscalValue"] = fiscalValue;
        input["items"]!.AsArray().Add(item);
    }

    // ── Groups ─────────────────────────────────────────────────────────────────

    [Fact]
    public void RubroDisplayOrder_IsAPermutationOfTheVocabulary()
    {
        Assert.Equal(LegajoExtractionVocabulary.Rubros.Order(), LegajoBienesPersonalesSummary.RubroDisplayOrder.Order());
        Assert.Equal(
            ["Inmuebles", "Rodados", "Depositos", "DineroEfectivo", "TitulosYAcciones", "Creditos",
             "BienesMueblesRegistrables", "OtrosBienes", "Otro"],
            LegajoBienesPersonalesSummary.RubroDisplayOrder);
    }

    [Fact]
    public void Groups_FollowTheDisplayOrder_NotTheDocumentOrder_AndOnlyRubrosWithItems()
    {
        // Added out of order, at the end of the document: Otro, then DineroEfectivo, then Creditos.
        var tree = Bp(i =>
        {
            AddItem(i, "Otro", "Pais", 10m);
            AddItem(i, "DineroEfectivo", "Pais", 20m);
            AddItem(i, "Creditos", "Exterior", 30m);
        });

        var summary = Summary(tree);

        Assert.Equal(["Inmuebles", "Rodados", "Depositos", "DineroEfectivo", "Creditos", "Otro"],
                     summary.Groups.Select(g => g.Rubro));
        Assert.Equal(["/items/5"], summary.Groups.Single(g => g.Rubro == "DineroEfectivo").ItemPaths);
        Assert.Equal(["/items/4"], summary.Groups.Single(g => g.Rubro == "Otro").ItemPaths);
    }

    [Fact]
    public void Group_SumsByLocation_AndMatchesThePrintedSubtotalsByRubroAndLocation()
    {
        var summary = Summary(Bp());

        var depositos = Assert.Single(summary.Groups, g => g.Rubro == "Depositos");
        Assert.Equal(["/items/2", "/items/3"], depositos.ItemPaths);
        Assert.Equal(2, depositos.ItemCount);
        Assert.Equal(450_000.50m, depositos.FiscalValueSum);
        Assert.Equal(150_000.50m, depositos.FiscalValueSumPais);
        Assert.Equal(300_000m, depositos.FiscalValueSumExterior);
        Assert.Equal(0, depositos.ItemsWithoutFiscalValue);
        Assert.Equal(150_000.50m, depositos.PrintedSubtotalPais);
        Assert.Equal("/rubroTotals/2/total", depositos.PrintedSubtotalPaisPath);
        Assert.Equal(300_000m, depositos.PrintedSubtotalExterior);
        Assert.Equal("/rubroTotals/3/total", depositos.PrintedSubtotalExteriorPath);

        // A rubro with no exterior row and no exterior subtotal: both null, not zero.
        var inmuebles = Assert.Single(summary.Groups, g => g.Rubro == "Inmuebles");
        Assert.Equal(1_000_000m, inmuebles.FiscalValueSumPais);
        Assert.Null(inmuebles.FiscalValueSumExterior);
        Assert.Equal("/rubroTotals/0/total", inmuebles.PrintedSubtotalPaisPath);
        Assert.Null(inmuebles.PrintedSubtotalExterior);
        Assert.Null(inmuebles.PrintedSubtotalExteriorPath);
    }

    [Fact]
    public void NullFiscalValue_IsCounted_AndLeftOutOfTheSums()
    {
        var tree = Bp(i => AddItem(i, "Depositos", "Pais", 5m));
        Assert.True(LegajoJsonPointer.Set(tree, "/items/4/fiscalValue", null));

        var depositos = Assert.Single(Summary(tree).Groups, g => g.Rubro == "Depositos");

        Assert.Equal(["/items/2", "/items/3", "/items/4"], depositos.ItemPaths);
        Assert.Equal(3, depositos.ItemCount);
        Assert.Equal(1, depositos.ItemsWithoutFiscalValue);
        Assert.Equal(450_000.50m, depositos.FiscalValueSum);
        Assert.Equal(150_000.50m, depositos.FiscalValueSumPais);
    }

    [Fact]
    public void Group_WhoseRowsAllLackAFiscalValue_HasNullSums()
    {
        var tree = Bp();
        Assert.True(LegajoJsonPointer.Set(tree, "/items/1/fiscalValue", null));

        var rodados = Assert.Single(Summary(tree).Groups, g => g.Rubro == "Rodados");

        Assert.Null(rodados.FiscalValueSum);
        Assert.Null(rodados.FiscalValueSumPais);
        Assert.Equal(1, rodados.ItemsWithoutFiscalValue);
        Assert.Equal(250_000m, rodados.PrintedSubtotalPais);   // the printed subtotal is still shown
    }

    [Fact]
    public void Amounts_StayInThePrintedUnit()
    {
        var tree = Bp(i => LegajoJsonPointer.Set(i, "/header/amountsUnit/value", LegajoExtractionVocabulary.Miles));

        var summary = Summary(tree, LegajoExtractionVocabulary.Miles);

        Assert.Equal(LegajoExtractionVocabulary.Miles, summary.AmountsUnit);
        Assert.Equal(1_000_000m, summary.Groups[0].FiscalValueSum);
        Assert.Equal(1_700_000.50m, summary.Totals.TotalBienes.Printed);
    }

    // ── Totals ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Totals_AllPrinted_SourceIsPrinted_AndComputedIsAlongside()
    {
        var totals = Summary(Bp()).Totals;

        Assert.Equal("/totals/bienesPais", totals.BienesPais.FieldPath);
        Assert.Equal(1_400_000.50m, totals.BienesPais.Printed);
        Assert.Equal(1_400_000.50m, totals.BienesPais.Computed);
        Assert.Equal(LegajoBienesPersonalesSummary.SourcePrinted, totals.BienesPais.Source);

        Assert.Equal(300_000m, totals.BienesExterior.Computed);
        Assert.Equal(LegajoBienesPersonalesSummary.SourcePrinted, totals.BienesExterior.Source);

        Assert.Equal(1_700_000.50m, totals.TotalBienes.Printed);
        Assert.Equal(1_700_000.50m, totals.TotalBienes.Computed);
        Assert.Equal(LegajoBienesPersonalesSummary.SourcePrinted, totals.TotalBienes.Source);

        // Printed only.
        Assert.Equal(0m, totals.BienesExentos);
        Assert.Null(totals.MinimoNoImponible);
        Assert.Equal(1_700_000.50m, totals.BaseImponible);
        Assert.Equal(8_500m, totals.ImpuestoDeterminado);
    }

    [Fact]
    public void TotalBienes_NotPrinted_IsComputedFromThePrintedParts()
    {
        // The printed parts win over the items: país printed off by 1.000 from its items.
        var tree = Bp(i =>
        {
            LegajoJsonPointer.Set(i, "/totals/totalBienes/value", null);
            LegajoJsonPointer.Set(i, "/totals/bienesPais/value", 1_401_000.50m);
        });

        var total = Summary(tree).Totals.TotalBienes;

        Assert.Null(total.Printed);
        Assert.Equal(1_701_000.50m, total.Computed);
        Assert.Equal(LegajoBienesPersonalesSummary.SourceComputed, total.Source);
    }

    [Fact]
    public void NoTotalsPrinted_AllAreComputedFromTheItems()
    {
        var tree = Bp(i =>
        {
            foreach (var name in new[] { "bienesPais", "bienesExterior", "totalBienes" })
                LegajoJsonPointer.Set(i, "/totals/" + name + "/value", null);
        });

        var totals = Summary(tree).Totals;

        Assert.Equal((null, 1_400_000.50m, LegajoBienesPersonalesSummary.SourceComputed),
                     (totals.BienesPais.Printed, totals.BienesPais.Computed, totals.BienesPais.Source));
        Assert.Equal((null, 300_000m, LegajoBienesPersonalesSummary.SourceComputed),
                     (totals.BienesExterior.Printed, totals.BienesExterior.Computed, totals.BienesExterior.Source));
        Assert.Equal((null, 1_700_000.50m, LegajoBienesPersonalesSummary.SourceComputed),
                     (totals.TotalBienes.Printed, totals.TotalBienes.Computed, totals.TotalBienes.Source));
    }

    [Fact]
    public void OnlyPaisPrinted_TotalIsTheSumOfEveryItem()
    {
        var tree = Bp(i =>
        {
            LegajoJsonPointer.Set(i, "/totals/totalBienes/value", null);
            LegajoJsonPointer.Set(i, "/totals/bienesExterior/value", null);
            LegajoJsonPointer.Set(i, "/totals/bienesPais/value", 9m);   // not used: exterior is missing
        });

        Assert.Equal(1_700_000.50m, Summary(tree).Totals.TotalBienes.Computed);
    }

    [Fact]
    public void NothingPrintedAndNoItemValue_SourceIsNone()
    {
        var tree = Bp(i =>
        {
            foreach (var name in new[] { "bienesPais", "bienesExterior", "totalBienes" })
                LegajoJsonPointer.Set(i, "/totals/" + name + "/value", null);
            i["items"] = new JsonArray();
        });

        var summary = Summary(tree);

        Assert.Empty(summary.Groups);
        Assert.All(new[] { summary.Totals.BienesPais, summary.Totals.BienesExterior, summary.Totals.TotalBienes }, t =>
        {
            Assert.Null(t.Printed);
            Assert.Null(t.Computed);
            Assert.Equal(LegajoBienesPersonalesSummary.SourceNone, t.Source);
        });
    }

    [Fact]
    public void NoExteriorItems_ComputedExteriorIsNull_NotZero()
    {
        var tree = Bp(i =>
        {
            i["items"]!.AsArray().RemoveAt(3);
            LegajoJsonPointer.Set(i, "/totals/bienesExterior/value", null);
        });

        var exterior = Summary(tree).Totals.BienesExterior;

        Assert.Null(exterior.Computed);
        Assert.Equal(LegajoBienesPersonalesSummary.SourceNone, exterior.Source);
    }

    // ── Overflow ───────────────────────────────────────────────────────────────

    [Fact]
    public void Overflow_YieldsNullSums_NeverThrows()
    {
        // Hand-built tree (past the reader's bound): two items at decimal.MaxValue in the same rubro.
        var tree = Bp(i =>
        {
            foreach (var name in new[] { "bienesPais", "bienesExterior", "totalBienes" })
                LegajoJsonPointer.Set(i, "/totals/" + name + "/value", null);
        });
        LegajoJsonPointer.Set(tree, "/items/0/fiscalValue", decimal.MaxValue);
        LegajoJsonPointer.Set(tree, "/items/1/rubro", "Inmuebles");
        LegajoJsonPointer.Set(tree, "/items/1/fiscalValue", decimal.MaxValue);

        var summary = Summary(tree);

        var inmuebles = Assert.Single(summary.Groups, g => g.Rubro == "Inmuebles");
        Assert.Null(inmuebles.FiscalValueSum);
        Assert.Null(inmuebles.FiscalValueSumPais);

        // The overflowed group makes the computed totals unknown, never a silently partial sum.
        Assert.Null(summary.Totals.BienesPais.Computed);
        Assert.Null(summary.Totals.TotalBienes.Computed);
        Assert.Equal(LegajoBienesPersonalesSummary.SourceNone, summary.Totals.TotalBienes.Source);

        // Exterior did not overflow and is still computed; the other groups keep their sums.
        Assert.Equal(300_000m, summary.Totals.BienesExterior.Computed);
        Assert.Equal(450_000.50m, Assert.Single(summary.Groups, g => g.Rubro == "Depositos").FiscalValueSum);
    }

    [Fact]
    public void Overflow_OfThePrintedParts_YieldsANullComputedTotal()
    {
        var tree = Bp(i => LegajoJsonPointer.Set(i, "/totals/totalBienes/value", null));
        LegajoJsonPointer.Set(tree, "/totals/bienesPais/value", decimal.MaxValue);
        LegajoJsonPointer.Set(tree, "/totals/bienesExterior/value", decimal.MaxValue);

        var total = Summary(tree).Totals.TotalBienes;

        Assert.Null(total.Computed);
        Assert.Equal(LegajoBienesPersonalesSummary.SourceNone, total.Source);
    }

    // ── On the view ────────────────────────────────────────────────────────────

    [Fact]
    public void View_CarriesTheSummary_ForBienesPersonales()
    {
        var view = View(LegajoExtractionPrompts.BienesPersonales, LegajoExtractionFixtures.BienesPersonales());

        Assert.NotNull(view.BienesPersonalesSummary);
        Assert.Equal(view.AmountsUnit, view.BienesPersonalesSummary!.AmountsUnit);
        Assert.Equal(["Inmuebles", "Rodados", "Depositos"], view.BienesPersonalesSummary.Groups.Select(g => g.Rubro));
    }

    [Fact]
    public void View_ReflectsACorrectionToAnItemsFiscalValue()
    {
        var correction = new LegajoCorrection
        {
            CorrectionId       = Guid.NewGuid(),
            TenantId           = LegajoTestData.Tenant,
            FieldPath          = "/items/2/fiscalValue",
            CorrectedValueJson = "160000.50",
            Reason             = "Leido del original",
            CorrectedBy        = Guid.NewGuid(),
            CorrectedAt        = DateTime.UtcNow
        };

        var view = View(LegajoExtractionPrompts.BienesPersonales, LegajoExtractionFixtures.BienesPersonales(), correction);

        var summary = view.BienesPersonalesSummary!;
        var depositos = Assert.Single(summary.Groups, g => g.Rubro == "Depositos");
        Assert.Equal(160_000.50m, depositos.FiscalValueSumPais);
        Assert.Equal(460_000.50m, depositos.FiscalValueSum);
        Assert.Equal(1_410_000.50m, summary.Totals.BienesPais.Computed);
        Assert.Equal(1_400_000.50m, summary.Totals.BienesPais.Printed);   // printed is unchanged
    }

    [Fact]
    public void View_HandEditedOverflow_StillServesTheView()
    {
        // ResultJson as stored, edited by hand past the reader's bound.
        var tree = LegajoExtractionFixtures.Normalize(LegajoExtractionPrompts.BienesPersonales, LegajoExtractionFixtures.BienesPersonales());
        LegajoJsonPointer.Set(tree, "/items/0/fiscalValue", decimal.MaxValue);
        LegajoJsonPointer.Set(tree, "/items/1/rubro", "Inmuebles");
        LegajoJsonPointer.Set(tree, "/items/1/fiscalValue", decimal.MaxValue);

        var view = ViewOf(DocumentTypes.BienesPersonales, tree.ToJsonString());

        Assert.NotNull(view.BienesPersonalesSummary);
        Assert.Null(Assert.Single(view.BienesPersonalesSummary!.Groups, g => g.Rubro == "Inmuebles").FiscalValueSum);
    }

    [Fact]
    public void View_HasNoSummary_ForManifestacionBienes_OrGanancias()
    {
        Assert.Null(View(LegajoExtractionPrompts.ManifestacionBienes, LegajoExtractionFixtures.ManifestacionBienes())
                        .BienesPersonalesSummary);
        Assert.Null(View(LegajoExtractionPrompts.Ganancias, LegajoExtractionFixtures.Ganancias()).BienesPersonalesSummary);
    }

    [Fact]
    public void View_SerializesTheSummary_CamelCase_AndNullForOtherTypes()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var bp = JsonNode.Parse(JsonSerializer.Serialize(
            View(LegajoExtractionPrompts.BienesPersonales, LegajoExtractionFixtures.BienesPersonales()), options))!;
        var summary = bp["bienesPersonalesSummary"]!;
        Assert.Equal("Pesos", summary["amountsUnit"]!.GetValue<string>());
        Assert.Equal("/rubroTotals/0/total", summary["groups"]![0]!["printedSubtotalPaisPath"]!.GetValue<string>());
        Assert.Equal("printed", summary["totals"]!["totalBienes"]!["source"]!.GetValue<string>());

        var mb = JsonNode.Parse(JsonSerializer.Serialize(
            View(LegajoExtractionPrompts.ManifestacionBienes, LegajoExtractionFixtures.ManifestacionBienes()), options))!.AsObject();
        Assert.True(mb.ContainsKey("bienesPersonalesSummary"));
        Assert.Null(mb["bienesPersonalesSummary"]);
    }

    private static LegajoExtractionViewResponse View(
        LegajoExtractionSchema schema, JsonObject input, params LegajoCorrection[] corrections)
        => ViewOf(schema.DocumentType, LegajoExtractionFixtures.Normalize(schema, input).ToJsonString(), corrections);

    private static LegajoExtractionViewResponse ViewOf(string documentType, string resultJson, params LegajoCorrection[] corrections)
    {
        var schema = LegajoExtractionPrompts.For(documentType)!;
        var run = new LegajoExtraction
        {
            ExtractionId  = Guid.NewGuid(),
            TenantId      = LegajoTestData.Tenant,
            DocumentId    = Guid.NewGuid(),
            Kind          = LegajoExtractionKinds.Extraction,
            DocumentType  = documentType,
            SchemaVersion = schema.SchemaVersion,
            PromptVersion = schema.PromptVersion,
            Model         = "claude-sonnet-4-6",
            ReadMode      = LegajoReadModes.Document,
            Status        = LegajoExtractionStatuses.Succeeded,
            ResultJson    = resultJson,
            IsCurrent     = true,
            CreatedAt     = DateTime.UtcNow.AddHours(-1)
        };

        foreach (var correction in corrections)
            correction.ExtractionId = run.ExtractionId;

        var extraction = LegajoExtractionReview.Evaluate(
            run, corrections, new LegajoIntegrityService(), new DateOnly(2025, 12, 31), LegajoTestData.LegajoCuit, 0.7m);
        Assert.NotNull(extraction);

        var document = new Document { DocumentId = run.DocumentId, TenantId = run.TenantId, LegajoStatus = LegajoStatuses.NeedsReview };
        return LegajoExtractionReview.BuildView(extraction, document, new Dictionary<Guid, string>());
    }
}
