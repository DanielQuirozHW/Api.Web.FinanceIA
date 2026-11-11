// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-19

using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Tests.Legajo;
using Xunit;
using static Api.Web.FinanceIA.Tests.Legajo.LegajoExtractionFixtures;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// <see cref="LegajoReconciliation"/>: the precedence rules, the exact-key matching per key kind, the
/// statuses, the snapshot and rectificativa selection, the incomes per source, the read-time coherence,
/// and the merge that keeps the analyst's edits. Pure; synthetic documents built from the clean fixtures.
/// </summary>
public sealed class LegajoReconciliationTests
{
    private static readonly Guid LegajoId = Guid.NewGuid();
    private static readonly DateTime Now  = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>An approved document of <paramref name="type"/> read on <paramref name="toolInput"/>.</summary>
    private static LegajoReconciliationInput Doc(
        string type, JsonObject? toolInput = null, DateTime? uploadedAt = null, Guid? supersedes = null, Guid? id = null)
    {
        var schema     = LegajoExtractionPrompts.For(type)!;
        var documentId = id ?? Guid.NewGuid();
        var run = new LegajoExtraction
        {
            ExtractionId   = Guid.NewGuid(),
            TenantId       = LegajoTestData.Tenant,
            PersonLegajoId = LegajoId,
            DocumentId     = documentId,
            Kind           = LegajoExtractionKinds.Extraction,
            DocumentType   = type,
            SchemaVersion  = schema.SchemaVersion,
            PromptVersion  = schema.PromptVersion,
            Model          = "claude-sonnet-4-6",
            ReadMode       = LegajoReadModes.Document,
            Status         = LegajoExtractionStatuses.Succeeded,
            ResultJson     = Normalize(schema, toolInput ?? Clean(type)).ToJsonString(),
            IsCurrent      = true
        };

        var effective = LegajoExtractionReview.Evaluate(
            run, [], new LegajoIntegrityService(), null, LegajoTestData.LegajoCuit, 0.7m)!;
        return new LegajoReconciliationInput(documentId, uploadedAt ?? Now.AddDays(-1), supersedes, effective);
    }

    private static LegajoReconciliationBuild Build(params LegajoReconciliationInput[] inputs)
        => LegajoReconciliation.Build(LegajoTestData.Tenant, LegajoId, inputs);

    private static JsonObject With(JsonObject input, Action<JsonObject> mutate)
    {
        mutate(input);
        return input;
    }

    private static JsonObject Key(string kind, string? value) => new() { ["kind"] = kind, ["value"] = value };

    private static JsonObject BpItems(JsonObject bp) => (JsonObject)bp["items"]!.AsArray()[0]!;

    private static JsonObject Annex(JsonObject mb, int i) => (JsonObject)mb["annexItems"]!.AsArray()[i]!;

    /// <summary>A BP with the Inmueble (partida PART-0001, 50 %) and an MB whose annex Inmueble carries <paramref name="mbKey"/>.</summary>
    private static (LegajoReconciliationInput Bp, LegajoReconciliationInput Mb) PairOnInmueble(
        JsonObject mbKey, decimal mbOwnership = 50m, Action<JsonObject>? mutateMbItem = null)
    {
        var mb = With(ManifestacionBienes(), m =>
        {
            Annex(m, 0)["matchKey"]     = mbKey;
            Annex(m, 0)["ownershipPct"] = mbOwnership;
            mutateMbItem?.Invoke(Annex(m, 0));
        });
        return (Doc(DocumentTypes.BienesPersonales), Doc(DocumentTypes.ManifestacionBienes, mb));
    }

    // ── Inventory: precedence ───────────────────────────────────────────────────

    [Fact]
    public void Matched_KeepsBothValues_ManifestacionValue_BienesPersonalesOwnershipAndExistence()
    {
        var (bp, mb) = PairOnInmueble(Key("Partida", "PART-0001"));

        var item = Assert.Single(Build(bp, mb).Items, i => i.ItemKey == "Partida:PART0001");

        Assert.Equal(LegajoReconciliationStatuses.Matched, item.ReconciliationStatus);
        Assert.Equal(LegajoItemSides.Activo, item.Side);
        Assert.Equal(LegajoItemKinds.Inmueble, item.Kind);
        Assert.Equal(8_000_000m, item.MarketValue);           // MB "en miles" → pesos
        Assert.Equal(mb.DocumentId, item.MarketValueDocumentId);
        Assert.Equal(1_000_000m, item.FiscalValue);           // BP, the floor, kept beside it
        Assert.Equal(bp.DocumentId, item.FiscalValueDocumentId);
        Assert.Equal(50m, item.OwnershipPct);                 // existence and ownership: BP
        Assert.Equal("Bien sintetico Inmuebles", item.Description);
        Assert.True(item.IsHousing);
        Assert.Null(item.ConflictReasons);
        Assert.Equal(LegajoTestData.Tenant, item.TenantId);
        Assert.Equal(LegajoId, item.PersonLegajoId);
    }

    [Fact]
    public void Matched_OwnershipFromManifestacion_WhenBienesPersonalesPrintsNone()
    {
        var bp = With(BienesPersonales(), b => BpItems(b)["ownershipPct"] = null);
        var mb = With(ManifestacionBienes(), m =>
        {
            Annex(m, 0)["matchKey"]     = Key("Partida", "PART-0001");
            Annex(m, 0)["ownershipPct"] = 25m;
        });

        var item = Assert.Single(Build(Doc(DocumentTypes.BienesPersonales, bp), Doc(DocumentTypes.ManifestacionBienes, mb)).Items,
                                 i => i.ItemKey == "Partida:PART0001");

        Assert.Equal(25m, item.OwnershipPct);
        Assert.Equal(LegajoReconciliationStatuses.Matched, item.ReconciliationStatus);
    }

    [Fact]
    public void Conflict_WhenMarketValueIsBelowTheFiscalFloor()
    {
        var (bp, mb) = PairOnInmueble(Key("Partida", "PART-0001"), mutateMbItem: i => i["marketValue"] = 900m); // 900.000 < 1.000.000

        var item = Assert.Single(Build(bp, mb).Items, i => i.ItemKey == "Partida:PART0001");

        Assert.Equal(LegajoReconciliationStatuses.Conflict, item.ReconciliationStatus);
        Assert.Equal(LegajoItemConflictReasons.BelowFiscalFloor, item.ConflictReasons);
        Assert.Equal(900_000m, item.MarketValue);
        Assert.Equal(1_000_000m, item.FiscalValue);
    }

    [Fact]
    public void Conflict_WhenOwnershipDiffers()
    {
        var (bp, mb) = PairOnInmueble(Key("Partida", "PART-0001"), mbOwnership: 100m);

        var item = Assert.Single(Build(bp, mb).Items, i => i.ItemKey == "Partida:PART0001");

        Assert.Equal(LegajoReconciliationStatuses.Conflict, item.ReconciliationStatus);
        Assert.Equal(LegajoItemConflictReasons.OwnershipDiffers, item.ConflictReasons);
        Assert.Equal(50m, item.OwnershipPct);   // BP prevails even in conflict
    }

    [Fact]
    public void Conflict_WhenCurrencyDiffers_ListsEveryReason()
    {
        var (bp, mb) = PairOnInmueble(Key("Partida", "PART-0001"), mbOwnership: 100m, mutateMbItem: i =>
        {
            i["currency"]    = "USD";
            i["marketValue"] = 10m;
        });

        var item = Assert.Single(Build(bp, mb).Items, i => i.ItemKey == "Partida:PART0001");

        Assert.Equal("OwnershipDiffers,BelowFiscalFloor,CurrencyDiffers", item.ConflictReasons);
        Assert.Equal("USD", item.Currency);   // currency: the manifestación first
    }

    [Fact]
    public void Debts_ComeFromTheManifestacion_IncludingLinesWithoutAnExtractedAnnex()
    {
        var items = Build(Doc(DocumentTypes.ManifestacionBienes)).Items;

        var debts = items.Where(i => i.Side == LegajoItemSides.Pasivo).OrderBy(i => i.MarketValue).ToList();
        Assert.Equal(2, debts.Count);
        Assert.All(debts, d => Assert.Equal(LegajoItemKinds.Deuda, d.Kind));
        Assert.All(debts, d => Assert.Equal(LegajoReconciliationStatuses.OnlyInManifestacion, d.ReconciliationStatus));
        Assert.Equal([700_000m, 3_000_000m], debts.Select(d => d.MarketValue!.Value));   // line "Deudas comerciales" + annex C

        // "Caja y bancos" has no annex: an asset of its own. Lines pointing at annexes A and B are not counted twice.
        Assert.Single(items, i => i.Description == "Caja y bancos" && i.Side == LegajoItemSides.Activo && i.MarketValue == 500_000m);
        Assert.DoesNotContain(items, i => i.Description is "Inmuebles" or "Rodados" or "Inversiones" or "Prestamo hipotecario");
        Assert.Equal(12_000_000m, items.Where(i => i.Side == LegajoItemSides.Activo).Sum(i => i.MarketValue!.Value)); // = totalActivo
    }

    [Fact]
    public void BienesPersonalesRubros_MapOntoTheInventoryKinds()
    {
        var bp = With(BienesPersonales(), b =>
        {
            var items = b["items"]!.AsArray();
            items.Add(new JsonObject
            {
                ["rubro"] = "TitulosYAcciones", ["location"] = "Pais", ["description"] = "Acciones SA sintetica",
                ["matchKey"] = Key("Cuit", "30-71234567-9"), ["ownershipPct"] = 10m, ["acquisitionDate"] = null,
                ["fiscalValue"] = 0m, ["currency"] = "ARS", ["isExempt"] = false, ["isHousing"] = false,
                ["page"] = 1, ["quote"] = "Acciones", ["confidence"] = 0.95m
            });
            items.Add(new JsonObject
            {
                ["rubro"] = "TitulosYAcciones", ["location"] = "Pais", ["description"] = "FCI sintetico",
                ["matchKey"] = Key("Ninguno", null), ["ownershipPct"] = 100m, ["acquisitionDate"] = null,
                ["fiscalValue"] = 0m, ["currency"] = "ARS", ["isExempt"] = false, ["isHousing"] = false,
                ["page"] = 1, ["quote"] = "FCI", ["confidence"] = 0.95m
            });
        });

        var items = Build(Doc(DocumentTypes.BienesPersonales, bp)).Items;

        Assert.Single(items, i => i.Kind == LegajoItemKinds.Inmueble);
        Assert.Single(items, i => i.Kind == LegajoItemKinds.Rodado);
        Assert.Equal(2, items.Count(i => i.Kind == LegajoItemKinds.Deposito));
        var shares = Assert.Single(items, i => i.Kind == LegajoItemKinds.Participacion);
        Assert.Equal("30-71234567-9", shares.LinkedCuit);
        Assert.Single(items, i => i.Kind == LegajoItemKinds.Inversion && i.Description == "FCI sintetico");
        Assert.All(items, i => Assert.Equal(LegajoReconciliationStatuses.OnlyInBienesPersonales, i.ReconciliationStatus));
        Assert.All(items, i => Assert.Null(i.MarketValue));
    }

    [Fact]
    public void BienesPersonalesCash_IsItsOwnKind_DepositsStayDeposits_OtherGoodsStayOtro()
    {
        // Engine 0.8: DineroEfectivo used to fall into Otro with the household goods; the fiador coverage
        // excludes it by kind (efectivo_sin_respaldo), so it must reconcile apart. Bank deposits are unchanged.
        var bp = With(BienesPersonales(), b =>
        {
            var items = b["items"]!.AsArray();
            items.Add(new JsonObject
            {
                ["rubro"] = "DineroEfectivo", ["location"] = "Pais", ["description"] = "Efectivo sintetico",
                ["matchKey"] = Key("Ninguno", null), ["ownershipPct"] = 100m, ["acquisitionDate"] = null,
                ["fiscalValue"] = 80_000m, ["currency"] = "ARS", ["isExempt"] = false, ["isHousing"] = false,
                ["page"] = 1, ["quote"] = "Efectivo", ["confidence"] = 0.95m
            });
            items.Add(new JsonObject
            {
                ["rubro"] = "OtrosBienes", ["location"] = "Pais", ["description"] = "Bienes del hogar sinteticos",
                ["matchKey"] = Key("Ninguno", null), ["ownershipPct"] = 100m, ["acquisitionDate"] = null,
                ["fiscalValue"] = 40_000m, ["currency"] = "ARS", ["isExempt"] = false, ["isHousing"] = false,
                ["page"] = 1, ["quote"] = "Hogar", ["confidence"] = 0.95m
            });
        });

        var items = Build(Doc(DocumentTypes.BienesPersonales, bp)).Items;

        var cash = Assert.Single(items, i => i.Kind == LegajoItemKinds.Efectivo);
        Assert.Equal(80_000m, cash.FiscalValue);
        Assert.Equal(LegajoItemSides.Activo, cash.Side);
        Assert.Equal(2, items.Count(i => i.Kind == LegajoItemKinds.Deposito));
        Assert.Equal("Bienes del hogar sinteticos", Assert.Single(items, i => i.Kind == LegajoItemKinds.Otro).Description);

        // The inventory read groups it after the deposits, in LegajoItemKinds order.
        var groups = LegajoReconciliationReadModel.BuildInventory(LegajoId, items, new Dictionary<Guid, string>()).Groups
            .Select(g => g.Kind).ToList();
        Assert.Equal(groups.IndexOf(LegajoItemKinds.Deposito) + 1, groups.IndexOf(LegajoItemKinds.Efectivo));
    }

    // ── Inventory: matching by each key kind ────────────────────────────────────

    [Theory]
    [InlineData("Partida", "PART-0001", "Partida", "part 0001", "Partida:PART0001")]
    [InlineData("Partida", "12.345/6", "Partida", "12345 6", "Partida:123456")]
    [InlineData("Dominio", "AB123CD", "Dominio", "ab 123-cd", "Dominio:AB123CD")]
    [InlineData("Cuit", "30-71234567-9", "Cuit", "30712345679", "Cuit:30712345679")]
    [InlineData("Cbu", "0110599520000001234567", "Cbu", "0110599 5-2000 0001234567", "Cbu:0110599520000001234567")]
    public void Match_ByEachKeyKind_OnTheNormalizedValue(
        string bpKind, string bpValue, string mbKind, string mbValue, string expectedKey)
    {
        var bp = With(BienesPersonales(), b => BpItems(b)["matchKey"] = Key(bpKind, bpValue));
        var mb = With(ManifestacionBienes(), m =>
        {
            Annex(m, 0)["matchKey"]     = Key(mbKind, mbValue);
            Annex(m, 0)["ownershipPct"] = 50m;
        });

        var build = Build(Doc(DocumentTypes.BienesPersonales, bp), Doc(DocumentTypes.ManifestacionBienes, mb));

        var item = Assert.Single(build.Items, i => i.ItemKey == expectedKey);
        Assert.Equal(LegajoReconciliationStatuses.Matched, item.ReconciliationStatus);
        Assert.Equal(bpKind, item.MatchKeyKind);
        Assert.Equal(expectedKey[(bpKind.Length + 1)..], item.MatchKey);
        Assert.NotNull(item.MarketValue);
        Assert.NotNull(item.FiscalValue);
    }

    [Fact]
    public void NoMatch_AcrossDifferentKeyKinds_OrValues()
    {
        var (bp, mb) = PairOnInmueble(Key("Dominio", "PART-0001"));

        var items = Build(bp, mb).Items;

        Assert.Single(items, i => i.ItemKey == "Partida:PART0001" && i.ReconciliationStatus == LegajoReconciliationStatuses.OnlyInBienesPersonales);
        Assert.Single(items, i => i.ItemKey == "Dominio:PART0001" && i.ReconciliationStatus == LegajoReconciliationStatuses.OnlyInManifestacion);
    }

    [Theory]
    [InlineData("Otro", "PART-0001")]
    [InlineData("Ninguno", null)]
    public void Otro_AndNinguno_NeverMatch(string kind, string? value)
    {
        var bp = With(BienesPersonales(), b => BpItems(b)["matchKey"] = Key(kind, value));
        var mb = With(ManifestacionBienes(), m => Annex(m, 0)["matchKey"] = Key(kind, value));

        var items = Build(Doc(DocumentTypes.BienesPersonales, bp), Doc(DocumentTypes.ManifestacionBienes, mb)).Items;

        Assert.DoesNotContain(items, i => i.ReconciliationStatus == LegajoReconciliationStatuses.Matched);
        Assert.Single(items, i => i.Kind == LegajoItemKinds.Inmueble && i.FiscalValue is not null && i.MarketValue is null);
        Assert.Single(items, i => i.Kind == LegajoItemKinds.Inmueble && i.MarketValue is not null && i.FiscalValue is null);
    }

    [Fact]
    public void DuplicateKey_InOneDocument_IsNeverPaired()
    {
        var mb = With(ManifestacionBienes(), m =>
        {
            Annex(m, 0)["matchKey"] = Key("Partida", "PART-0001");
            Annex(m, 1)["matchKey"] = Key("Partida", "PART 0001");
        });

        var items = Build(Doc(DocumentTypes.BienesPersonales), Doc(DocumentTypes.ManifestacionBienes, mb)).Items;

        Assert.DoesNotContain(items, i => i.ReconciliationStatus == LegajoReconciliationStatuses.Matched);
        Assert.Equal(["Partida:PART0001#BP1", "Partida:PART0001#MB1", "Partida:PART0001#MB2"],
                     items.Where(i => i.MatchKey == "PART0001").Select(i => i.ItemKey).Order(StringComparer.Ordinal));
        Assert.Equal(items.Count, items.Select(i => i.ItemKey).Distinct().Count());
    }

    [Fact]
    public void UnkeyedRows_GetUniqueStableKeys()
    {
        var items = Build(Doc(DocumentTypes.BienesPersonales)).Items;

        // The two "Bien sintetico Depositos" rows (país and exterior).
        Assert.Contains(items, i => i.ItemKey == "BP:Deposito:BIEN SINTETICO DEPOSITOS");
        Assert.Contains(items, i => i.ItemKey == "BP:Deposito:BIEN SINTETICO DEPOSITOS#2");
        Assert.Equal(items.Count, items.Select(i => i.ItemKey).Distinct().Count());
    }

    // ── Selection: snapshot, unit, supersession ─────────────────────────────────

    [Fact]
    public void Inventory_TakesTheLatestBienesPersonales_ByFiscalYearThenRectificativa()
    {
        var older = Doc(DocumentTypes.BienesPersonales, With(BienesPersonales(), b => b["header"]!["fiscalYear"] = F(2024)));
        var original = Doc(DocumentTypes.BienesPersonales, With(BienesPersonales(), b => BpItems(b)["fiscalValue"] = 1_000_000m));
        var rectificativa = Doc(DocumentTypes.BienesPersonales, With(BienesPersonales(), b =>
        {
            b["header"]!["isRectificativa"]     = F(true);
            b["header"]!["rectificativaNumber"] = F(1);
            BpItems(b)["fiscalValue"] = 1_100_000m;
            ((JsonObject)b["rubroTotals"]!.AsArray()[0]!)["total"] = 1_100_000m;
        }), uploadedAt: Now.AddDays(-30));   // uploaded earlier: the rectificativa still wins

        var build = Build(older, original, rectificativa);

        Assert.Equal(1_100_000m, Assert.Single(build.Items, i => i.ItemKey == "Partida:PART0001").FiscalValue);
        Assert.Contains(build.Skipped, s => s.DocumentId == older.DocumentId && s.Reason == LegajoReconciliationSkipReasons.OlderSnapshot);
        Assert.Contains(build.Skipped, s => s.DocumentId == original.DocumentId && s.Reason == LegajoReconciliationSkipReasons.OlderSnapshot);
        Assert.Equal([rectificativa.DocumentId], build.DocumentsUsed);
    }

    [Fact]
    public void Inventory_TakesTheLatestManifestacion_ByMeasurementDate()
    {
        var older = Doc(DocumentTypes.ManifestacionBienes, With(ManifestacionBienes(), m =>
        {
            m["header"]!["measurementDate"] = F("2024-12-31");
            Annex(m, 3)["marketValue"] = 1m;
        }), uploadedAt: Now);
        var latest = Doc(DocumentTypes.ManifestacionBienes, uploadedAt: Now.AddDays(-10));

        var build = Build(older, latest);

        Assert.All(build.Items, i => Assert.Equal(latest.DocumentId, i.MarketValueDocumentId));

        // The older one still covers the year with its estado de resultados (the newest upload of that range),
        // so it is used, not skipped.
        Assert.All(build.Incomes, i => Assert.Equal(older.DocumentId, i.DocumentId));
        Assert.Empty(build.Skipped);
        Assert.Equal(2, build.DocumentsUsed.Count);
    }

    [Fact]
    public void UnknownUnit_SkipsTheDocument()
    {
        var mb = Doc(DocumentTypes.ManifestacionBienes, With(ManifestacionBienes(), m => m["header"]!["amountsUnit"] = F(null)));

        var build = Build(mb);

        Assert.Empty(build.Items);
        Assert.Empty(build.Incomes);
        Assert.Equal(LegajoReconciliationSkipReasons.AmountsUnitMissing, Assert.Single(build.Skipped).Reason);
    }

    [Fact]
    public void SupersedesDocumentId_DropsTheSupersededDocument()
    {
        var old = Doc(DocumentTypes.BienesPersonales, With(BienesPersonales(), b => b["header"]!["fiscalYear"] = F(2026)));
        var replacement = Doc(DocumentTypes.BienesPersonales, supersedes: old.DocumentId);

        var build = Build(old, replacement);

        Assert.All(build.Items, i => Assert.Equal(replacement.DocumentId, i.FiscalValueDocumentId));
        Assert.Contains(build.Skipped, s => s.DocumentId == old.DocumentId && s.Reason == LegajoReconciliationSkipReasons.Superseded);
    }

    // ── Incomes ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Iva_SalesPerMonth_AreNetoGravadoPlusExentas()
    {
        var iva = Doc(DocumentTypes.DdjjIva);

        var incomes = Build(iva).Incomes;

        Assert.Equal(2, incomes.Count);
        var november = Assert.Single(incomes, i => i.Period == "2025-11");
        Assert.Equal(LegajoIncomeSources.Iva, november.Source);
        Assert.Equal(LegajoIncomeConcepts.Ventas, november.Concept);
        Assert.Equal(1_250_000m, november.Amount);
        Assert.Equal(new DateOnly(2025, 11, 1), november.PeriodFrom);
        Assert.Equal(new DateOnly(2025, 11, 30), november.PeriodTo);
        Assert.Equal(iva.DocumentId, november.DocumentId);
        Assert.Equal(iva.Extraction.Run.ExtractionId, november.ExtractionId);
        Assert.Equal("ARS", november.Currency);
        Assert.Equal(800_000m, Assert.Single(incomes, i => i.Period == "2025-12").Amount);
    }

    [Fact]
    public void Iva_RectificativaInsideTheDocument_ReplacesTheOriginal()
    {
        var iva = Doc(DocumentTypes.DdjjIva, With(DdjjIva(), d =>
        {
            var rect = (JsonObject)d["periods"]!.AsArray()[0]!.DeepClone();
            rect["rectificativaNumber"] = 1;
            rect["ventasNetoGravado"]   = 1_300_000m;
            d["periods"]!.AsArray().Add(rect);
        }));

        var november = Assert.Single(Build(iva).Incomes, i => i.Period == "2025-11");

        Assert.Equal(1_350_000m, november.Amount);
    }

    [Fact]
    public void Iva_RectificativaInAnotherDocument_ReplacesTheOlderDocumentsRows()
    {
        var original = Doc(DocumentTypes.DdjjIva, uploadedAt: Now.AddDays(-5));
        var rectificativa = Doc(DocumentTypes.DdjjIva, With(DdjjIva(), d =>
        {
            var periods = d["periods"]!.AsArray();
            periods.RemoveAt(1);
            periods[0]!["rectificativaNumber"] = 2;
            periods[0]!["ventasNetoGravado"]   = 2_000_000m;
            d["alicuotaLines"] = new JsonArray();
        }), uploadedAt: Now.AddDays(-20));

        var build = Build(original, rectificativa);

        var november = Assert.Single(build.Incomes, i => i.Period == "2025-11");
        Assert.Equal(2_050_000m, november.Amount);
        Assert.Equal(rectificativa.DocumentId, november.DocumentId);
        Assert.Equal(original.DocumentId, Assert.Single(build.Incomes, i => i.Period == "2025-12").DocumentId);
    }

    [Fact]
    public void SamePeriodAndRank_TheNewestDocumentWins_AndTheOtherIsReplaced()
    {
        var older = Doc(DocumentTypes.Monotributo, uploadedAt: Now.AddDays(-9));
        var newer = Doc(DocumentTypes.Monotributo, uploadedAt: Now.AddDays(-1));

        var build = Build(older, newer);

        Assert.All(build.Incomes, i => Assert.Equal(newer.DocumentId, i.DocumentId));
        Assert.Contains(build.Skipped, s => s.DocumentId == older.DocumentId && s.Reason == LegajoReconciliationSkipReasons.Replaced);
    }

    [Fact]
    public void Iibb_TotalIngresos_ElseTheSumOfTheBases_AndAnAnnualDdjjIsAYear()
    {
        var monthly = Doc(DocumentTypes.DdjjIibb, With(DdjjIibb(), d => d["periods"]!.AsArray().RemoveAt(1)));
        var annual = Doc(DocumentTypes.DdjjIibb, With(DdjjIibb(), d =>
        {
            d["header"]!["fiscalYear"] = F(2024);
            d["periods"] = new JsonArray();
            foreach (var row in d["rows"]!.AsArray())
                row!["period"] = null;
        }));

        var incomes = Build(monthly, annual).Incomes;

        Assert.Equal(1_500_000m, Assert.Single(incomes, i => i.Period == "2025-11").Amount);   // printed total
        Assert.Equal(1_800_000m, Assert.Single(incomes, i => i.Period == "2025-12").Amount);   // Σ base imponible
        var year = Assert.Single(incomes, i => i.Period == "2024");
        Assert.Equal(3_300_000m, year.Amount);
        Assert.Equal(new DateOnly(2024, 1, 1), year.PeriodFrom);
        Assert.Equal(new DateOnly(2024, 12, 31), year.PeriodTo);
        Assert.All(incomes, i => Assert.Equal(LegajoIncomeSources.Iibb, i.Source));
    }

    [Fact]
    public void Certificacion_Monthly_InPesos()
    {
        var incomes = Build(Doc(DocumentTypes.CertificacionIngresos)).Incomes;

        Assert.Equal(["2025-10", "2025-11", "2025-12"], incomes.Select(i => i.Period!).Order());
        Assert.Equal(1_300_500m, Assert.Single(incomes, i => i.Period == "2025-12").Amount);   // "en miles"
        Assert.All(incomes, i => Assert.Equal(LegajoIncomeConcepts.IngresoCertificado, i.Concept));
    }

    [Theory]
    [InlineData("2025-01-01", "2025-12-31", "2025")]
    [InlineData("2024-07-01", "2025-06-30", null)]
    public void Certificacion_WithoutMonths_IsItsRange(string from, string to, string? period)
    {
        var ci = Doc(DocumentTypes.CertificacionIngresos, With(CertificacionIngresos(), c =>
        {
            c["header"]!["periodFrom"] = F(from);
            c["header"]!["periodTo"]   = F(to);
            foreach (var row in c["rows"]!.AsArray())
                row!["period"] = null;
        }));

        var income = Assert.Single(Build(ci).Incomes);

        Assert.Equal(period, income.Period);
        Assert.Equal(DateOnly.Parse(from), income.PeriodFrom);
        Assert.Equal(DateOnly.Parse(to), income.PeriodTo);
        Assert.Equal(3_500_500m, income.Amount);
    }

    [Fact]
    public void Ganancias_ConceptsPerFiscalYear()
    {
        var incomes = Build(Doc(DocumentTypes.Ganancias)).Incomes;

        Assert.All(incomes, i => Assert.Equal("2025", i.Period));
        Assert.Equal(8_500_000.50m, Assert.Single(incomes, i => i.Concept == LegajoIncomeConcepts.Renta3ra).Amount);
        Assert.Equal(2_300_000m, Assert.Single(incomes, i => i.Concept == LegajoIncomeConcepts.Renta4ta).Amount);
        Assert.Equal(1_300_000m, Assert.Single(incomes, i => i.Concept == LegajoIncomeConcepts.Otro).Amount);   // 1.200.000 + 150.000 − 50.000
        Assert.Equal(4_800_000.50m, Assert.Single(incomes, i => i.Concept == LegajoIncomeConcepts.MontoConsumido).Amount);
    }

    [Fact]
    public void Ganancias_TheRectificativaWins()
    {
        var original = Doc(DocumentTypes.Ganancias, uploadedAt: Now);
        var rectificativa = Doc(DocumentTypes.Ganancias, With(Ganancias(), g =>
        {
            g["header"]!["isRectificativa"] = F(true);
            g["justification"]!["montoConsumido"] = F(5_000_000m);
        }), uploadedAt: Now.AddDays(-3));

        var build = Build(original, rectificativa);

        Assert.All(build.Incomes, i => Assert.Equal(rectificativa.DocumentId, i.DocumentId));
        Assert.Equal(5_000_000m, Assert.Single(build.Incomes, i => i.Concept == LegajoIncomeConcepts.MontoConsumido).Amount);
        Assert.Contains(build.Skipped, s => s.DocumentId == original.DocumentId && s.Reason == LegajoReconciliationSkipReasons.Replaced);
    }

    [Fact]
    public void Manifestacion_IncomeStatement_IsVentasAndResult()
    {
        var incomes = Build(Doc(DocumentTypes.ManifestacionBienes)).Incomes;

        Assert.Equal(6_000_000m, Assert.Single(incomes, i => i.Concept == LegajoIncomeConcepts.Ventas).Amount);
        Assert.Equal(1_500_000m, Assert.Single(incomes, i => i.Concept == LegajoIncomeConcepts.Otro).Amount);
        Assert.All(incomes, i => Assert.Equal(LegajoIncomeSources.Manifestacion, i.Source));
        Assert.All(incomes, i => Assert.Equal("2025", i.Period));
    }

    [Fact]
    public void Monotributo_PaymentsPerMonth()
    {
        var incomes = Build(Doc(DocumentTypes.Monotributo)).Incomes;

        Assert.Equal(3, incomes.Count);
        Assert.All(incomes, i => Assert.Equal((LegajoIncomeSources.Monotributo, LegajoIncomeConcepts.Otro, 42_000.50m),
                                              (i.Source, i.Concept, i.Amount)));
    }

    [Fact]
    public void Incomes_AreUniquePerSourceConceptAndRange()
    {
        var build = Build(Doc(DocumentTypes.DdjjIva), Doc(DocumentTypes.DdjjIibb), Doc(DocumentTypes.CertificacionIngresos),
                          Doc(DocumentTypes.Ganancias), Doc(DocumentTypes.ManifestacionBienes), Doc(DocumentTypes.Monotributo),
                          Doc(DocumentTypes.BienesPersonales));

        Assert.Equal(build.Incomes.Count,
                     build.Incomes.Select(i => (i.Source, i.Concept, i.PeriodFrom, i.PeriodTo)).Distinct().Count());
        Assert.Equal(7, build.DocumentsUsed.Count);
        Assert.Empty(build.Skipped);
    }

    [Fact]
    public void PeriodLabel_IsAMonthAYearOrNothing()
    {
        Assert.Equal("2025-02", LegajoReconciliation.PeriodLabel(new DateOnly(2025, 2, 1), new DateOnly(2025, 2, 28)));
        Assert.Equal("2025", LegajoReconciliation.PeriodLabel(new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31)));
        Assert.Null(LegajoReconciliation.PeriodLabel(new DateOnly(2025, 2, 2), new DateOnly(2025, 2, 28)));
        Assert.Null(LegajoReconciliation.PeriodLabel(new DateOnly(2024, 7, 1), new DateOnly(2025, 6, 30)));
    }

    // ── Read model: coherence and precedence picks ──────────────────────────────

    [Fact]
    public void Coherence_IsTheMaxRelativeDifferenceBetweenTheSalesSources()
    {
        var build = Build(Doc(DocumentTypes.DdjjIva), Doc(DocumentTypes.DdjjIibb), Doc(DocumentTypes.CertificacionIngresos),
                          Doc(DocumentTypes.Monotributo));

        var view = LegajoReconciliationReadModel.BuildIncomes(LegajoId, build.Incomes);

        Assert.Equal(["2025-12", "2025-11", "2025-10"], view.Periods.Select(p => p.Period!));
        var november = view.Periods.Single(p => p.Period == "2025-11");
        Assert.Equal("month", november.Granularity);
        // IVA 1.250.000, IIBB 1.500.000, certificación 1.200.000; the monotributo payment is not a sale.
        Assert.Equal(3, november.Coherence.Sources);
        Assert.Equal(0.2m, november.Coherence.MaxRelativeDiff);
        Assert.Equal(LegajoIncomeSources.Iibb, november.Coherence.HighSource);
        Assert.Equal(LegajoIncomeSources.Certificacion, november.Coherence.LowSource);
        Assert.Equal(LegajoIncomeSources.Iva, november.PrimarySalesSource);
        Assert.Null(november.PrimaryResultSource);

        var october = view.Periods.Single(p => p.Period == "2025-10");
        Assert.Equal(1, october.Coherence.Sources);    // only the certificación
        Assert.Null(october.Coherence.MaxRelativeDiff);
        Assert.Equal(LegajoIncomeSources.Certificacion, october.PrimarySalesSource);
    }

    [Fact]
    public void ResultPrecedence_GananciasOverTheManifestacion()
    {
        var build = Build(Doc(DocumentTypes.Ganancias), Doc(DocumentTypes.ManifestacionBienes));

        var year = Assert.Single(LegajoReconciliationReadModel.BuildIncomes(LegajoId, build.Incomes).Periods);

        Assert.Equal("year", year.Granularity);
        Assert.Equal(LegajoIncomeSources.Ganancias, year.PrimaryResultSource);
        Assert.Equal(LegajoIncomeSources.Manifestacion, year.PrimarySalesSource);
        Assert.Equal(1, year.Coherence.Sources);
    }

    [Fact]
    public void Inventory_GroupsAndTotals_AreComputedAtReadTime()
    {
        var (bp, mb) = PairOnInmueble(Key("Partida", "PART-0001"));
        var items = Build(bp, mb).Items;
        foreach (var item in items)
            item.ItemId = Guid.NewGuid();

        var view = LegajoReconciliationReadModel.BuildInventory(LegajoId, items, new Dictionary<Guid, string>());

        Assert.Equal(LegajoItemSides.Activo, view.Groups[0].Side);
        Assert.Equal(LegajoItemSides.Pasivo, view.Groups[^1].Side);
        Assert.Equal(12_000_000m, view.Totals.ActivoMarketValue);
        Assert.Equal(3_700_000m, view.Totals.PasivoMarketValue);
        Assert.Equal(8_300_000m, view.Totals.PatrimonioNetoMarketValue);                 // = the manifestación's PN
        Assert.Equal(1_700_000.50m, view.Totals.ActivoFiscalValue);                      // = BP totalBienes
        var inmuebles = view.Groups.Single(g => g.Kind == LegajoItemKinds.Inmueble);
        Assert.Equal(8_000_000m, inmuebles.MarketValueTotal);
        Assert.Equal(1_000_000m, inmuebles.FiscalValueTotal);
        Assert.Equal(1, view.StatusCounts[LegajoReconciliationStatuses.Matched]);
        Assert.Equal(LegajoReconciliationStatuses.Known.Count, view.StatusCounts.Count);
        Assert.Equal([bp.DocumentId], view.BienesPersonalesDocumentIds);
        Assert.Equal([mb.DocumentId], view.ManifestacionDocumentIds);
        Assert.Equal(3, view.Totals.ActivoWithoutMarketValue);   // rodado and the two depósitos of BP
    }

    // ── Merge: analyst edits survive a rebuild ──────────────────────────────────

    private static List<LegajoItem> Stored(LegajoReconciliationBuild build)
        => LegajoReconciliation.Merge([], [], build, Now.AddDays(-1)).ItemsAdded.ToList();

    [Fact]
    public void Merge_KeepsTheItemId_TheEditedFields_AndTheNote()
    {
        var (bp, mb) = PairOnInmueble(Key("Partida", "PART-0001"));
        var stored = Stored(Build(bp, mb));
        var inmueble = stored.Single(i => i.ItemKey == "Partida:PART0001");
        var id = inmueble.ItemId;
        inmueble.IsHousing    = false;
        inmueble.OwnershipPct = 75m;
        inmueble.EditedFields = "IsHousing,OwnershipPct";
        inmueble.AnalystNote  = "Nota del analista";

        // The documents change the value; the analyst's fields stand.
        var (bp2, mb2) = PairOnInmueble(Key("Partida", "PART-0001"), mutateMbItem: i => i["marketValue"] = 9_000m);
        var result = LegajoReconciliation.Merge(stored, [], Build(bp2, mb2), Now);

        Assert.Equal(id, inmueble.ItemId);
        Assert.False(inmueble.IsHousing);
        Assert.Equal(75m, inmueble.OwnershipPct);
        Assert.Equal("Nota del analista", inmueble.AnalystNote);
        Assert.Equal(9_000_000m, inmueble.MarketValue);
        Assert.Equal(mb2.DocumentId, inmueble.MarketValueDocumentId);
        Assert.Equal(Now, inmueble.UpdatedAt);
        Assert.Null(inmueble.UpdatedBy);
        Assert.Empty(result.ItemsAdded);
        Assert.Empty(result.ItemsRemoved);
    }

    [Fact]
    public void Merge_UnchangedRows_AreNotTouched()
    {
        var (bp, mb) = PairOnInmueble(Key("Partida", "PART-0001"));
        var stored = Stored(Build(bp, mb));
        var before = stored.ToDictionary(i => i.ItemId, i => i.UpdatedAt);

        var result = LegajoReconciliation.Merge(stored, [], Build(bp, mb), Now);

        Assert.Equal(0, result.ItemsUpdated);
        Assert.All(stored, i => Assert.Equal(before[i.ItemId], i.UpdatedAt));
    }

    [Fact]
    public void Merge_AConfirmedConflict_SurvivesWhileTheSameConflictStands()
    {
        var (bp, mb) = PairOnInmueble(Key("Partida", "PART-0001"), mutateMbItem: i => i["marketValue"] = 900m);
        var stored = Stored(Build(bp, mb));
        var item = stored.Single(i => i.ItemKey == "Partida:PART0001");
        item.ReconciliationStatus = LegajoReconciliationStatuses.AnalystConfirmed;
        item.ConfirmedFingerprint = LegajoReconciliation.Fingerprint(item);

        var result = LegajoReconciliation.Merge(stored, [], Build(bp, mb), Now);

        Assert.Equal(LegajoReconciliationStatuses.AnalystConfirmed, item.ReconciliationStatus);
        Assert.Equal(0, result.ConfirmationsLapsed);
    }

    [Fact]
    public void Merge_AConfirmedConflict_LapsesWhenTheFiguresChange()
    {
        var (bp, mb) = PairOnInmueble(Key("Partida", "PART-0001"), mutateMbItem: i => i["marketValue"] = 900m);
        var stored = Stored(Build(bp, mb));
        var item = stored.Single(i => i.ItemKey == "Partida:PART0001");
        item.ReconciliationStatus = LegajoReconciliationStatuses.AnalystConfirmed;
        item.ConfirmedFingerprint = LegajoReconciliation.Fingerprint(item);

        var (bp2, mb2) = PairOnInmueble(Key("Partida", "PART-0001"), mutateMbItem: i => i["marketValue"] = 800m);
        var result = LegajoReconciliation.Merge(stored, [], Build(bp2, mb2), Now);

        Assert.Equal(LegajoReconciliationStatuses.Conflict, item.ReconciliationStatus);
        Assert.Null(item.ConfirmedFingerprint);
        Assert.Equal(1, result.ConfirmationsLapsed);
    }

    [Fact]
    public void Merge_AConfirmedConflict_LapsesWhenTheConflictIsResolved()
    {
        var (bp, mb) = PairOnInmueble(Key("Partida", "PART-0001"), mutateMbItem: i => i["marketValue"] = 900m);
        var stored = Stored(Build(bp, mb));
        var item = stored.Single(i => i.ItemKey == "Partida:PART0001");
        item.ReconciliationStatus = LegajoReconciliationStatuses.AnalystConfirmed;
        item.ConfirmedFingerprint = LegajoReconciliation.Fingerprint(item);

        var (bp2, mb2) = PairOnInmueble(Key("Partida", "PART-0001"));
        LegajoReconciliation.Merge(stored, [], Build(bp2, mb2), Now);

        Assert.Equal(LegajoReconciliationStatuses.Matched, item.ReconciliationStatus);
    }

    [Fact]
    public void Merge_RemovesItemsNoDocumentStatesAnyMore_AndAddsNewOnes()
    {
        var stored = Stored(Build(Doc(DocumentTypes.BienesPersonales)));

        var result = LegajoReconciliation.Merge(stored, [], Build(Doc(DocumentTypes.ManifestacionBienes)), Now);

        Assert.Equal(stored.Count, result.ItemsRemoved.Count);
        Assert.NotEmpty(result.ItemsAdded);
        Assert.All(result.ItemsAdded, i => Assert.NotEqual(Guid.Empty, i.ItemId));
        Assert.All(result.ItemsAdded, i => Assert.Equal(Now, i.CreatedAt));
    }

    [Fact]
    public void Merge_Incomes_UpdateInPlaceByKey_AddAndRemove()
    {
        var first = LegajoReconciliation.Merge([], [], Build(Doc(DocumentTypes.DdjjIva)), Now.AddDays(-1)).IncomesAdded.ToList();
        var november = first.Single(i => i.Period == "2025-11");
        var id = november.IncomeId;

        var rect = Doc(DocumentTypes.DdjjIva, With(DdjjIva(), d =>
        {
            d["periods"]!.AsArray().RemoveAt(1);
            d["periods"]!.AsArray()[0]!["ventasNetoGravado"] = 2_000_000m;
            d["alicuotaLines"] = new JsonArray();
        }));
        var result = LegajoReconciliation.Merge([], first, Build(rect), Now);

        Assert.Equal(id, november.IncomeId);
        Assert.Equal(2_050_000m, november.Amount);
        Assert.Equal(rect.DocumentId, november.DocumentId);
        Assert.Equal(1, result.IncomesUpdated);
        Assert.Equal("2025-12", Assert.Single(result.IncomesRemoved).Period);
        Assert.Empty(result.IncomesAdded);
    }
}
