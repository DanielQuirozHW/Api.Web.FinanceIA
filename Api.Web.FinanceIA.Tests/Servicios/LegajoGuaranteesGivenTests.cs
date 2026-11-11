// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-30

using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Tests.Legajo;
using Xunit;
using static Api.Web.FinanceIA.Tests.Legajo.LegajoExtractionFixtures;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// mb-1.2: the avales y/o coobligaciones otorgados a manifestación declares — the schema field, the pure reader
/// (<see cref="LegajoReconciliation.GuaranteesGivenOf"/>) in pesos, null without the line or on a run extracted
/// before mb-1.2, and the read model that puts it beside the inventory on the effective values.
/// </summary>
public sealed class LegajoGuaranteesGivenTests
{
    private static JsonObject MbWith(JsonNode? guaranteesGiven)
    {
        var mb = ManifestacionBienes();
        mb["contingencies"] = new JsonObject
        {
            ["guaranteesGiven"] = guaranteesGiven is null ? F(null, page: null, quote: null) : F(guaranteesGiven)
        };
        return Normalize(LegajoExtractionPrompts.ManifestacionBienes, mb);
    }

    // ── Schema and prompt ─────────────────────────────────────────────────────────

    [Fact]
    public void TheManifestacionSchema_KeepsTheNullableContingenciesObject_SinceMb12()
    {
        var schema = LegajoExtractionPrompts.ManifestacionBienes;

        // mb-1.2 added it; mb-1.3 (investmentClass) keeps it unchanged.
        Assert.Equal("mb-1.3", schema.PromptVersion);
        var contingencies = Assert.IsType<LegajoSchemaObject>(LegajoSchemaResolver.Resolve(schema.Root, "/contingencies", out _));
        Assert.True(contingencies.Nullable);
        Assert.IsType<LegajoSchemaField>(LegajoSchemaResolver.Resolve(schema.Root, LegajoReconciliation.GuaranteesGivenPath, out _));
        Assert.DoesNotContain(LegajoReconciliation.GuaranteesGivenPath, schema.RequiredFieldPaths);
        Assert.Contains("avales y/o coobligaciones otorgados", schema.SystemPrompt);
        Assert.Contains("never list it in lines", schema.SystemPrompt);
    }

    [Fact]
    public void AReadingWithoutTheSection_IsStillValid_AndStoresItAsNull()
    {
        // The model may omit it, or answer null, when the document has no such line.
        var normalized = Normalize(LegajoExtractionPrompts.ManifestacionBienes, ManifestacionBienes());

        Assert.True(normalized.ContainsKey("contingencies"));
        Assert.Null(normalized["contingencies"]);
    }

    // ── The reader ────────────────────────────────────────────────────────────────

    [Fact]
    public void TheLine_IsReadInPesos_ByTheDocumentsUnit()
    {
        // The fixture states its amounts "en miles": 1.500 → $ 1.500.000.
        Assert.Equal(1_500_000m, LegajoReconciliation.GuaranteesGivenOf(MbWith(1_500m)));
    }

    [Fact]
    public void WithoutTheLine_ItIsNull()
    {
        Assert.Null(LegajoReconciliation.GuaranteesGivenOf(MbWith(null)));
        Assert.Null(LegajoReconciliation.GuaranteesGivenOf(Normalize(LegajoExtractionPrompts.ManifestacionBienes, ManifestacionBienes())));
    }

    [Fact]
    public void ARunExtractedBeforeMb12_HasNoNode_AndReadsNull()
    {
        var old = Normalize(LegajoExtractionPrompts.ManifestacionBienes, ManifestacionBienes());
        old.Remove("contingencies");

        Assert.Null(LegajoReconciliation.GuaranteesGivenOf(old));
    }

    [Fact]
    public void WithoutAnAmountsUnit_ItIsNull()
    {
        var mb = MbWith(1_500m);
        mb["header"]!["amountsUnit"]!["value"] = null;

        Assert.Null(LegajoReconciliation.GuaranteesGivenOf(mb));
    }

    // ── Beside the inventory ──────────────────────────────────────────────────────

    private static LegajoExtraction Run(Guid documentId, JsonObject result, string type = DocumentTypes.ManifestacionBienes) => new()
    {
        ExtractionId  = Guid.NewGuid(),
        TenantId      = LegajoTestData.Tenant,
        DocumentId    = documentId,
        Kind          = LegajoExtractionKinds.Extraction,
        DocumentType  = type,
        PromptVersion = LegajoExtractionPrompts.ManifestacionBienes.PromptVersion,
        Status        = LegajoExtractionStatuses.Succeeded,
        ResultJson    = result.ToJsonString(),
        IsCurrent     = true
    };

    private static LegajoInventoryResponse InventoryFrom(Guid manifestacionId)
    {
        var item = new LegajoItem
        {
            ItemId = Guid.NewGuid(), TenantId = LegajoTestData.Tenant, ItemKey = "MB:Inmueble:casa", Kind = LegajoItemKinds.Inmueble,
            MarketValue = 8_000_000m, MarketValueDocumentId = manifestacionId, ReconciliationStatus = LegajoReconciliationStatuses.OnlyInManifestacion
        };
        return LegajoReconciliationReadModel.BuildInventory(Guid.NewGuid(), [item], new Dictionary<Guid, string>());
    }

    [Fact]
    public void TheInventory_CarriesTheAmountAndItsDocument()
    {
        var documentId = Guid.NewGuid();
        var run = Run(documentId, MbWith(1_500m));

        var view = LegajoReconciliationReadModel.WithGuaranteesGiven(
            InventoryFrom(documentId), new Dictionary<Guid, LegajoExtraction> { [documentId] = run }, []);

        Assert.Equal(1_500_000m, view.GarantiasOtorgadasDeclaradas);
        Assert.Equal(documentId, view.GarantiasOtorgadasDocumentId);
    }

    [Fact]
    public void TheInventory_ReadsTheEffectiveValue_TheCorrectionApplied()
    {
        var documentId = Guid.NewGuid();
        var run = Run(documentId, MbWith(1_500m));
        var correction = new LegajoCorrection
        {
            CorrectionId = Guid.NewGuid(), TenantId = LegajoTestData.Tenant, ExtractionId = run.ExtractionId,
            FieldPath = LegajoReconciliation.GuaranteesGivenPath, CorrectedValueJson = "2000", Reason = "Leído mal",
            CorrectedBy = Guid.NewGuid(), CorrectedAt = DateTime.UtcNow
        };

        var view = LegajoReconciliationReadModel.WithGuaranteesGiven(
            InventoryFrom(documentId), new Dictionary<Guid, LegajoExtraction> { [documentId] = run }, [correction]);

        Assert.Equal(2_000_000m, view.GarantiasOtorgadasDeclaradas);
    }

    [Fact]
    public void WithoutTheLine_OrWithoutAManifestacion_BothFieldsStayNull()
    {
        var documentId = Guid.NewGuid();
        var noLine = LegajoReconciliationReadModel.WithGuaranteesGiven(
            InventoryFrom(documentId), new Dictionary<Guid, LegajoExtraction> { [documentId] = Run(documentId, MbWith(null)) }, []);
        Assert.Null(noLine.GarantiasOtorgadasDeclaradas);
        Assert.Null(noLine.GarantiasOtorgadasDocumentId);

        var empty = LegajoReconciliationReadModel.BuildInventory(Guid.NewGuid(), [], new Dictionary<Guid, string>());
        var none = LegajoReconciliationReadModel.WithGuaranteesGiven(empty, new Dictionary<Guid, LegajoExtraction>(), []);
        Assert.Null(none.GarantiasOtorgadasDeclaradas);
        Assert.Null(none.GarantiasOtorgadasDocumentId);
    }

    [Fact]
    public void AFailedRun_IsNotRead()
    {
        var documentId = Guid.NewGuid();
        var run = Run(documentId, MbWith(1_500m));
        run.Status = LegajoExtractionStatuses.Failed;

        var view = LegajoReconciliationReadModel.WithGuaranteesGiven(
            InventoryFrom(documentId), new Dictionary<Guid, LegajoExtraction> { [documentId] = run }, []);

        Assert.Null(view.GarantiasOtorgadasDeclaradas);
    }
}
