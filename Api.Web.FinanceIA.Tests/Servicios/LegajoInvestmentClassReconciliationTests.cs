// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-31

using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Tests.Legajo;
using Xunit;
using static Api.Web.FinanceIA.Tests.Legajo.LegajoExtractionFixtures;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// The investment class through the reconciliation (DDL 080, mb-1.3 / bp-1.1): read from each document, the
/// manifestación winning, only on an Inversion item; the analyst's class kept across rebuilds; a keyed listed share
/// that moves from Participacion to Inversion keeping its row, note, edits and confirmation; the class in the
/// snapshot but not in the conflict fingerprint; and the read model treating an unknown stored value as unclassified.
/// </summary>
public sealed class LegajoInvestmentClassReconciliationTests
{
    private static readonly Guid LegajoId = Guid.NewGuid();
    private static readonly DateTime Now  = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);

    private static readonly string ListedKey   = "Cuit:" + LegajoCuit.Digits(ListedIssuerCuit);
    private static readonly string UnlistedKey = "Cuit:" + LegajoCuit.Digits(UnlistedIssuerCuit);

    // ── Helpers (as in LegajoReconciliationTests) ───────────────────────────────

    private static LegajoReconciliationInput Doc(string type, JsonObject toolInput)
    {
        var schema     = LegajoExtractionPrompts.For(type)!;
        var documentId = Guid.NewGuid();
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
            ResultJson     = Normalize(schema, toolInput).ToJsonString(),
            IsCurrent      = true
        };

        var effective = LegajoExtractionReview.Evaluate(
            run, [], new LegajoIntegrityService(), null, LegajoTestData.LegajoCuit, 0.7m)!;
        return new LegajoReconciliationInput(documentId, Now.AddDays(-1), null, effective);
    }

    private static LegajoReconciliationInput Mb(Action<JsonObject>? mutate = null)
    {
        var mb = ManifestacionBienesWithInvestments();
        mutate?.Invoke(mb);
        return Doc(DocumentTypes.ManifestacionBienes, mb);
    }

    private static LegajoReconciliationInput Bp(Action<JsonObject>? mutate = null)
    {
        var bp = BienesPersonalesWithInvestments();
        mutate?.Invoke(bp);
        return Doc(DocumentTypes.BienesPersonales, bp);
    }

    /// <summary>The annex row of the manifestación (or item of Bienes Personales) described as <paramref name="description"/>.</summary>
    private static JsonObject Row(JsonObject document, string array, string description)
        => document[array]!.AsArray().OfType<JsonObject>().Single(r => (string?)r["description"] == description);

    private static IReadOnlyList<LegajoItem> Build(params LegajoReconciliationInput[] inputs)
        => LegajoReconciliation.Build(LegajoTestData.Tenant, LegajoId, inputs).Items;

    private static LegajoReconciliationBuild BuildFull(params LegajoReconciliationInput[] inputs)
        => LegajoReconciliation.Build(LegajoTestData.Tenant, LegajoId, inputs);

    private static List<LegajoItem> Stored(params LegajoReconciliationInput[] inputs)
        => LegajoReconciliation.Merge([], [], BuildFull(inputs), Now.AddDays(-1)).ItemsAdded.ToList();

    private static LegajoItem ByDescription(IEnumerable<LegajoItem> items, string description)
        => items.Single(i => i.Description == description);

    // ── From the documents ──────────────────────────────────────────────────────

    [Fact]
    public void Manifestacion_ClassifiesEachInvestment_AndAListedShareWithACuit_IsAnInversion()
    {
        var items = Build(Mb());

        var share = items.Single(i => i.ItemKey == ListedKey);
        Assert.Equal(LegajoItemKinds.Inversion, share.Kind);
        Assert.Equal(LegajoInvestmentClasses.AccionCotizante, share.InvestmentClass);
        Assert.Equal(ListedIssuerCuit, share.LinkedCuit);

        Assert.Equal(LegajoInvestmentClasses.TituloPublico, ByDescription(items, "Bono sintetico 2030").InvestmentClass);
        Assert.Equal(LegajoInvestmentClasses.CuotaparteFci, ByDescription(items, "FCI sintetico renta fija").InvestmentClass);

        // Every other kind stays unclassified.
        Assert.All(items.Where(i => i.Kind != LegajoItemKinds.Inversion), i => Assert.Null(i.InvestmentClass));
    }

    [Fact]
    public void BienesPersonales_AClassifiedShareWithACuit_IsAnInversion_AnUnclassifiedOneStaysAParticipation()
    {
        var items = Build(Bp());

        var listed = items.Single(i => i.ItemKey == ListedKey);
        Assert.Equal(LegajoItemKinds.Inversion, listed.Kind);
        Assert.Equal(LegajoInvestmentClasses.AccionCotizante, listed.InvestmentClass);

        var unlisted = items.Single(i => i.ItemKey == UnlistedKey);
        Assert.Equal(LegajoItemKinds.Participacion, unlisted.Kind);
        Assert.Null(unlisted.InvestmentClass);
        Assert.Equal(UnlistedIssuerCuit, unlisted.LinkedCuit);

        Assert.Equal(LegajoInvestmentClasses.TituloPublico, ByDescription(items, "Bono sintetico 2030").InvestmentClass);
        Assert.Equal(LegajoInvestmentClasses.CuotaparteFci, ByDescription(items, "FCI sintetico renta fija").InvestmentClass);
    }

    // ── Bienes Personales: a doubt never promotes a CUIT row out of Participacion ─

    private const string ListedShare = "Acciones Sintetica SA (BYMA: SINT)";

    [Theory]
    [InlineData(LegajoInvestmentClasses.Otra)]
    [InlineData(null)]
    public void BienesPersonales_ACuitRow_WithOtraOrNoClass_StaysAParticipation_WithNoClass(string? investmentClass)
    {
        var items = Build(Bp(b => Row(b, "items", ListedShare)["investmentClass"] = investmentClass));

        var share = items.Single(i => i.ItemKey == ListedKey);
        Assert.Equal(LegajoItemKinds.Participacion, share.Kind);
        Assert.Null(share.InvestmentClass);
        Assert.Equal(ListedIssuerCuit, share.LinkedCuit);
    }

    [Theory]
    [InlineData(LegajoInvestmentClasses.TituloPublico)]
    [InlineData(LegajoInvestmentClasses.AccionCotizante)]
    [InlineData(LegajoInvestmentClasses.ObligacionNegociable)]
    [InlineData(LegajoInvestmentClasses.CuotaparteFci)]
    public void BienesPersonales_ACuitRow_WithAConcreteClass_IsAnInversion_WithThatClass(string investmentClass)
    {
        var items = Build(Bp(b => Row(b, "items", ListedShare)["investmentClass"] = investmentClass));

        var share = items.Single(i => i.ItemKey == ListedKey);
        Assert.Equal(LegajoItemKinds.Inversion, share.Kind);
        Assert.Equal(investmentClass, share.InvestmentClass);
    }

    [Fact]
    public void BienesPersonales_ARowWithoutACuit_WithOtra_IsAnInversion_OfClassOtra()
    {
        var items = Build(Bp(b => Row(b, "items", "Bono sintetico 2030")["investmentClass"] = LegajoInvestmentClasses.Otra));

        var bono = ByDescription(items, "Bono sintetico 2030");
        Assert.Equal(LegajoItemKinds.Inversion, bono.Kind);
        Assert.Equal(LegajoInvestmentClasses.Otra, bono.InvestmentClass);
    }

    [Fact]
    public void Pair_ABienesPersonalesOtra_ThatStayedAParticipation_IsNotInherited()
    {
        // The manifestación calls the CUIT row an Inversion without a class; Bienes Personales said Otra (and so kept it
        // a Participacion). The kind is the manifestación's; the doubt is not carried across as a class.
        var bp = Bp(b => Row(b, "items", ListedShare)["investmentClass"] = LegajoInvestmentClasses.Otra);
        var mb = Mb(m => Row(m, "annexItems", ListedShare)["investmentClass"] = null);

        var share = Build(bp, mb).Single(i => i.ItemKey == ListedKey);

        Assert.Equal(LegajoItemKinds.Inversion, share.Kind);
        Assert.Null(share.InvestmentClass);
    }

    [Theory]
    [InlineData(LegajoInvestmentClasses.TituloPublico, true)]
    [InlineData(LegajoInvestmentClasses.AccionCotizante, true)]
    [InlineData(LegajoInvestmentClasses.ObligacionNegociable, true)]
    [InlineData(LegajoInvestmentClasses.CuotaparteFci, true)]
    [InlineData(LegajoInvestmentClasses.Otra, false)]
    [InlineData(null, false)]
    [InlineData("Bono", false)]
    [InlineData("tituloPublico", false)]
    public void IsConcrete_IsEveryKnownClassButOtra(string? value, bool expected)
    {
        Assert.Equal(expected, LegajoInvestmentClasses.IsConcrete(value));
        Assert.Equal(LegajoInvestmentClasses.Known.Where(c => c != LegajoInvestmentClasses.Otra), LegajoInvestmentClasses.Concrete);
    }

    [Fact]
    public void BienesPersonales_AClassOnAnotherRubro_IsIgnored()
    {
        var items = Build(Bp(b => ((JsonObject)b["items"]!.AsArray()[0]!)["investmentClass"] = LegajoInvestmentClasses.TituloPublico));

        Assert.Null(items.Single(i => i.Kind == LegajoItemKinds.Inmueble).InvestmentClass);
    }

    [Fact]
    public void Pair_TheManifestacionsClassWins()
    {
        var bp = Bp(b => Row(b, "items", "Acciones Sintetica SA (BYMA: SINT)")["investmentClass"] = LegajoInvestmentClasses.ObligacionNegociable);

        var share = Build(bp, Mb()).Single(i => i.ItemKey == ListedKey);

        Assert.Equal(LegajoReconciliationStatuses.Matched, share.ReconciliationStatus);
        Assert.Equal(LegajoInvestmentClasses.AccionCotizante, share.InvestmentClass);
    }

    [Fact]
    public void Pair_BienesPersonalesClassifies_WhenTheManifestacionDoesNot()
    {
        var mb = Mb(m => Row(m, "annexItems", "Acciones Sintetica SA (BYMA: SINT)")["investmentClass"] = null);

        var share = Build(Bp(), mb).Single(i => i.ItemKey == ListedKey);

        Assert.Equal(LegajoInvestmentClasses.AccionCotizante, share.InvestmentClass);
    }

    [Fact]
    public void Pair_TheManifestacionsKindWins_AndANonInversionGetsNoClass()
    {
        // The manifestación files the share as a participation; Bienes Personales classified it. The kind is the
        // manifestación's, and only an Inversion carries a class (CK_LegajoItems_InvestmentClass).
        var mb = Mb(m =>
        {
            var row = Row(m, "annexItems", "Acciones Sintetica SA (BYMA: SINT)");
            row["kind"]            = "Participacion";
            row["investmentClass"] = LegajoInvestmentClasses.AccionCotizante;
        });

        var share = Build(Bp(), mb).Single(i => i.ItemKey == ListedKey);

        Assert.Equal(LegajoItemKinds.Participacion, share.Kind);
        Assert.Null(share.InvestmentClass);
    }

    [Fact]
    public void AClassPrintedOnANonInversionAnnexItem_IsDropped()
    {
        var mb = Mb(m => ((JsonObject)m["annexItems"]!.AsArray()[0]!)["investmentClass"] = LegajoInvestmentClasses.Otra);

        Assert.Null(Build(mb).Single(i => i.Kind == LegajoItemKinds.Inmueble).InvestmentClass);
    }

    [Fact]
    public void AnOlderRun_WithoutTheProperty_ReconcilesUnclassified()
    {
        // The clean fixtures carry no investmentClass at all: the shape of every mb-1.2 / bp-1.0 run.
        var items = Build(Doc(DocumentTypes.ManifestacionBienes, ManifestacionBienes()),
                          Doc(DocumentTypes.BienesPersonales, BienesPersonales()));

        var inversion = Assert.Single(items, i => i.Kind == LegajoItemKinds.Inversion);
        Assert.Null(inversion.InvestmentClass);
        Assert.All(items, i => Assert.Null(i.InvestmentClass));
    }

    // ── Merge ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Merge_AnEditedClass_SurvivesARebuild()
    {
        var mb = Mb();
        var stored = Stored(mb);
        var bono = ByDescription(stored, "Bono sintetico 2030");
        bono.InvestmentClass = LegajoInvestmentClasses.Otra;
        bono.EditedFields    = LegajoReconciliation.EditedFieldsCsv([LegajoItemEditableFields.InvestmentClass]);

        var result = LegajoReconciliation.Merge(stored, [], BuildFull(mb), Now);

        Assert.Equal(LegajoInvestmentClasses.Otra, bono.InvestmentClass);
        Assert.Equal("InvestmentClass", bono.EditedFields);
        Assert.Equal(0, result.ItemsUpdated);
    }

    [Fact]
    public void Merge_AnEditedNullClass_IsNotRefilled()
    {
        var stored = Stored(Mb());
        var fci = ByDescription(stored, "FCI sintetico renta fija");
        fci.InvestmentClass = null;
        fci.EditedFields    = "InvestmentClass";

        LegajoReconciliation.Merge(stored, [], BuildFull(Mb()), Now);

        Assert.Null(fci.InvestmentClass);
    }

    [Fact]
    public void Merge_AnUneditedClass_TakesTheDocuments()
    {
        var mb = Mb();
        var stored = Stored(mb);
        var bono = ByDescription(stored, "Bono sintetico 2030");
        bono.InvestmentClass = LegajoInvestmentClasses.Otra;   // as if an older document said so

        var result = LegajoReconciliation.Merge(stored, [], BuildFull(mb), Now);

        Assert.Equal(LegajoInvestmentClasses.TituloPublico, bono.InvestmentClass);
        Assert.Equal(1, result.ItemsUpdated);
    }

    [Fact]
    public void Merge_AnItemThatStopsBeingAnInversion_LosesItsClass_AndTheEdit()
    {
        var stored = Stored(Mb());
        var share = stored.Single(i => i.ItemKey == ListedKey);
        share.InvestmentClass = LegajoInvestmentClasses.ObligacionNegociable;
        share.OwnershipPct    = 15m;
        share.EditedFields    = "OwnershipPct,InvestmentClass";

        var asParticipation = Mb(m => Row(m, "annexItems", "Acciones Sintetica SA (BYMA: SINT)")["kind"] = "Participacion");
        var result = LegajoReconciliation.Merge(stored, [], BuildFull(asParticipation), Now);

        Assert.Equal(LegajoItemKinds.Participacion, share.Kind);
        Assert.Null(share.InvestmentClass);
        Assert.Equal("OwnershipPct", share.EditedFields);
        Assert.Equal(15m, share.OwnershipPct);
        Assert.Equal(Now, share.UpdatedAt);
        Assert.Empty(result.ItemsRemoved);
    }

    [Fact]
    public void Merge_AKeyedListedShare_MovesFromParticipacionToInversion_KeepingItsRowNoteEditsAndConfirmation()
    {
        // Before mb-1.3 the manifestación filed the listed share as a participation, with no class, and its
        // currency (USD) differed from Bienes Personales' (ARS): a Conflict the analyst confirmed.
        static void AsUsd(JsonObject m) => Row(m, "annexItems", "Acciones Sintetica SA (BYMA: SINT)")["currency"] = "USD";
        var before = Mb(m =>
        {
            AsUsd(m);
            var row = Row(m, "annexItems", "Acciones Sintetica SA (BYMA: SINT)");
            row["kind"]            = "Participacion";
            row["investmentClass"] = null;
        });
        var bp = Bp();
        var stored = Stored(bp, before);

        var share = stored.Single(i => i.ItemKey == ListedKey);
        Assert.Equal(LegajoItemKinds.Participacion, share.Kind);
        Assert.Equal(LegajoReconciliationStatuses.Conflict, share.ReconciliationStatus);
        var id = share.ItemId;
        share.AnalystNote          = "Cotiza en BYMA";
        share.IsActivityAsset      = true;
        share.EditedFields         = "IsActivityAsset";
        share.ReconciliationStatus = LegajoReconciliationStatuses.AnalystConfirmed;
        share.ConfirmedFingerprint = LegajoReconciliation.Fingerprint(share);

        // mb-1.3 reads it as an Inversion / AccionCotizante. Its key (the CUIT) does not contain the kind.
        var result = LegajoReconciliation.Merge(stored, [], BuildFull(bp, Mb(AsUsd)), Now);

        Assert.Equal(id, share.ItemId);
        Assert.Equal(LegajoItemKinds.Inversion, share.Kind);
        Assert.Equal(LegajoInvestmentClasses.AccionCotizante, share.InvestmentClass);
        Assert.Equal("Cotiza en BYMA", share.AnalystNote);
        Assert.True(share.IsActivityAsset);
        Assert.Equal("IsActivityAsset", share.EditedFields);
        Assert.Equal(LegajoReconciliationStatuses.AnalystConfirmed, share.ReconciliationStatus);
        Assert.Equal(0, result.ConfirmationsLapsed);
        Assert.DoesNotContain(result.ItemsRemoved, i => i.ItemId == id);
        Assert.DoesNotContain(result.ItemsAdded, i => i.ItemKey == ListedKey);
    }

    [Fact]
    public void Merge_AClassChange_MarksTheRowUpdated_ButAConfirmationStands()
    {
        // The same documents on both sides, so the class is the only thing that differs: the row as stored before 080.
        var bp = Bp();
        var mb = Mb(m => Row(m, "annexItems", "Acciones Sintetica SA (BYMA: SINT)")["currency"] = "USD");
        var stored = Stored(bp, mb);

        var share = stored.Single(i => i.ItemKey == ListedKey);
        Assert.Equal(LegajoReconciliationStatuses.Conflict, share.ReconciliationStatus);
        share.InvestmentClass      = null;
        share.ReconciliationStatus = LegajoReconciliationStatuses.AnalystConfirmed;
        share.ConfirmedFingerprint = LegajoReconciliation.Fingerprint(share);
        var before = share.UpdatedAt;

        var result = LegajoReconciliation.Merge(stored, [], BuildFull(bp, mb), Now);

        Assert.Equal(LegajoInvestmentClasses.AccionCotizante, share.InvestmentClass);
        Assert.NotEqual(before, share.UpdatedAt);
        Assert.Equal(Now, share.UpdatedAt);
        Assert.Null(share.UpdatedBy);
        Assert.Equal(1, result.ItemsUpdated);
        Assert.Equal(LegajoReconciliationStatuses.AnalystConfirmed, share.ReconciliationStatus);
        Assert.Equal(0, result.ConfirmationsLapsed);
    }

    [Fact]
    public void Fingerprint_IgnoresTheClass()
    {
        var item = new LegajoItem { Kind = LegajoItemKinds.Inversion, MarketValue = 1m, FiscalValue = 2m, ConflictReasons = "BelowFiscalFloor" };
        var unclassified = LegajoReconciliation.Fingerprint(item);

        item.InvestmentClass = LegajoInvestmentClasses.CuotaparteFci;

        Assert.Equal(unclassified, LegajoReconciliation.Fingerprint(item));
    }

    // ── Read model ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(LegajoItemKinds.Inversion, LegajoInvestmentClasses.TituloPublico, LegajoInvestmentClasses.TituloPublico)]
    [InlineData(LegajoItemKinds.Inversion, LegajoInvestmentClasses.Otra, LegajoInvestmentClasses.Otra)]
    [InlineData(LegajoItemKinds.Inversion, null, null)]
    [InlineData(LegajoItemKinds.Inversion, "Bono", null)]               // a value this code does not know
    [InlineData(LegajoItemKinds.Inversion, "tituloPublico", null)]      // the CHECK is exact; so is the read
    [InlineData(LegajoItemKinds.Deposito, LegajoInvestmentClasses.CuotaparteFci, null)]
    public void ReadModel_ExposesTheClass_AndTreatsAnUnknownOrMisplacedOneAsUnclassified(string kind, string? stored, string? expected)
    {
        var item = new LegajoItem
        {
            ItemId = Guid.NewGuid(), TenantId = LegajoTestData.Tenant, PersonLegajoId = LegajoId, ItemKey = "k",
            Kind = kind, InvestmentClass = stored, UpdatedAt = Now
        };

        Assert.Equal(expected, LegajoReconciliationReadModel.Item(item, new Dictionary<Guid, string>()).InvestmentClass);
    }

    [Fact]
    public void Inventory_ExposesTheClassOnEachItem()
    {
        var stored = Stored(Bp(), Mb());

        var inventory = LegajoReconciliationReadModel.BuildInventory(LegajoId, stored, new Dictionary<Guid, string>());
        var inversiones = inventory.Groups.Single(g => g.Kind == LegajoItemKinds.Inversion).Items;

        // The listed share pairs on its CUIT; the bono and the FCI carry no key, so each document lists its own row.
        Assert.Equal(
            [LegajoInvestmentClasses.AccionCotizante,
             LegajoInvestmentClasses.CuotaparteFci, LegajoInvestmentClasses.CuotaparteFci,
             LegajoInvestmentClasses.TituloPublico, LegajoInvestmentClasses.TituloPublico],
            inversiones.Select(i => i.InvestmentClass!).Order(StringComparer.Ordinal));
    }
}
