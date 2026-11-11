// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-31

using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;
using Xunit;
using static Api.Web.FinanceIA.Tests.PersonAnalysis.PersonEvaluationFixtures;

namespace Api.Web.FinanceIA.Tests.PersonAnalysis;

/// <summary>
/// Engine 0.9 — the fiador's investments by class: títulos públicos (pf_haircut_titulo_publico), acciones and ON con
/// cotización (pf_haircut_accion_on), FCI (pf_haircut_fci); Otra, no class and an unknown class fall back to
/// pf_haircut_inversion. The currency mismatch still adds on top, a 100 % class haircut keeps descuento_total with
/// the class named, the class is frozen on the asset, and a blob frozen before 0.9 reads — and labels — unchanged.
/// </summary>
public sealed class PersonEvaluationV09Tests
{
    private static LegajoItem Investment(string? investmentClass, decimal market = 1_000_000m, string? currency = null)
    {
        var item = PersonEvaluationFiadorTests.Asset(LegajoItemKinds.Inversion, market, currency: currency);
        item.InvestmentClass = investmentClass;
        return item;
    }

    private static FiadorAssetResult Evaluate(LegajoItem item, PersonEvaluationInput? person = null)
        => Assert.Single(PersonEvaluationService.FiadorEvaluation(PersonEvaluationFiadorTests.Fiador([item], person: person)).Coverage.Assets);

    // ── Version ─────────────────────────────────────────────────────────────────

    [Fact]
    public void EngineVersion_IsAtLeast09()
    {
        // The 0.9 rules below still hold from 0.9 on; the exact version is pinned in PersonEvaluationCoverageTests.
        Assert.True(EngineVersion.CompareVersions(PersonEvaluationService.Version, "0.9") >= 0);
        Assert.Equal(PersonEvaluationService.Version, PersonEvaluationService.Evaluate(Empty()).Version);
    }

    // ── The haircut follows the class ───────────────────────────────────────────

    [Theory]
    [InlineData(LegajoInvestmentClasses.TituloPublico,        0.15, 850_000)]   // pf_haircut_titulo_publico
    [InlineData(LegajoInvestmentClasses.AccionCotizante,      0.30, 700_000)]   // pf_haircut_accion_on
    [InlineData(LegajoInvestmentClasses.ObligacionNegociable, 0.30, 700_000)]   // pf_haircut_accion_on
    [InlineData(LegajoInvestmentClasses.CuotaparteFci,        0.30, 700_000)]   // pf_haircut_fci
    [InlineData(LegajoInvestmentClasses.Otra,                 0.30, 700_000)]   // pf_haircut_inversion
    [InlineData(null,                                         0.30, 700_000)]   // pf_haircut_inversion
    public void EachClass_TakesItsDefaultHaircut(string? investmentClass, double haircut, double executable)
    {
        var asset = Evaluate(Investment(investmentClass));

        Assert.Equal(FiadorAssetTreatments.Computado, asset.Treatment);
        Assert.Equal(FiadorAssetReasons.ValorManifestacion, asset.Reason);
        Assert.Equal((decimal)haircut, asset.Haircut);
        Assert.Null(asset.CurrencyHaircut);
        Assert.Equal((decimal)executable, asset.ExecutableValue);
        Assert.Equal(investmentClass, asset.InvestmentClass);
    }

    [Theory]
    [InlineData(LegajoInvestmentClasses.TituloPublico,        "pf_haircut_titulo_publico", 0.05, 950_000)]
    [InlineData(LegajoInvestmentClasses.AccionCotizante,      "pf_haircut_accion_on",      0.40, 600_000)]
    [InlineData(LegajoInvestmentClasses.ObligacionNegociable, "pf_haircut_accion_on",      0.40, 600_000)]
    [InlineData(LegajoInvestmentClasses.CuotaparteFci,        "pf_haircut_fci",            0.20, 800_000)]
    [InlineData(LegajoInvestmentClasses.Otra,                 "pf_haircut_inversion",      0.50, 500_000)]
    [InlineData(null,                                         "pf_haircut_inversion",      0.50, 500_000)]
    public void EachClass_ReadsItsOwnTenantKey(string? investmentClass, string key, double value, double executable)
    {
        var person = Empty() with { Parameters = With(r => r[key] = (decimal)value) };

        var asset = Evaluate(Investment(investmentClass), person);

        Assert.Equal((decimal)value, asset.Haircut);
        Assert.Equal((decimal)executable, asset.ExecutableValue);
    }

    [Fact]
    public void TheClassKeys_DoNotMoveTheUnclassifiedHaircut()
    {
        var person = Empty() with
        {
            Parameters = With(r =>
            {
                r["pf_haircut_titulo_publico"] = 0.9m;
                r["pf_haircut_accion_on"]      = 0.9m;
                r["pf_haircut_fci"]            = 0.9m;
            })
        };

        Assert.Equal(0.3m, Evaluate(Investment(null), person).Haircut);
        Assert.Equal(0.3m, Evaluate(Investment(LegajoInvestmentClasses.Otra), person).Haircut);
    }

    [Theory]
    [InlineData("Bono")]
    [InlineData("tituloPublico")]
    public void AnUnknownStoredClass_IsUnclassified_NeverAnError(string stored)
    {
        var asset = Evaluate(Investment(stored));

        Assert.Equal(FiadorAssetTreatments.Computado, asset.Treatment);
        Assert.Equal(0.3m, asset.Haircut);
        Assert.Null(asset.InvestmentClass);
    }

    [Fact]
    public void AClassOnAnotherKind_IsIgnored()
    {
        var deposit = PersonEvaluationFiadorTests.Asset(LegajoItemKinds.Deposito, 1_000_000m);
        deposit.InvestmentClass = LegajoInvestmentClasses.TituloPublico;

        var asset = Evaluate(deposit);

        Assert.Equal(0m, asset.Haircut);
        Assert.Null(asset.InvestmentClass);
    }

    // ── Currency mismatch on top ────────────────────────────────────────────────

    [Fact]
    public void ADollarBond_AddsTheDescalceHaircut_OnTopOfItsClass()
    {
        var asset = Evaluate(Investment(LegajoInvestmentClasses.TituloPublico, currency: "USD"));

        Assert.Equal(FiadorAssetTreatments.Computado, asset.Treatment);
        Assert.Equal(0.15m, asset.Haircut);
        Assert.Equal(0.08m, asset.CurrencyHaircut);
        Assert.Equal(770_000m, asset.ExecutableValue);   // 1.000.000 × (1 − 0,15 − 0,08)
        Assert.Contains("con un descuento de 15 % más 8 % por descalce de moneda (USD frente a ARS)", asset.ReasonText);
    }

    // ── A 100 % class haircut ───────────────────────────────────────────────────

    [Theory]
    [InlineData(LegajoInvestmentClasses.TituloPublico,        "pf_haircut_titulo_publico", "Inversión · Título público")]
    [InlineData(LegajoInvestmentClasses.AccionCotizante,      "pf_haircut_accion_on",      "Inversión · Acción con cotización")]
    [InlineData(LegajoInvestmentClasses.ObligacionNegociable, "pf_haircut_accion_on",      "Inversión · Obligación negociable")]
    [InlineData(LegajoInvestmentClasses.CuotaparteFci,        "pf_haircut_fci",            "Inversión · Cuotaparte de FCI")]
    [InlineData(LegajoInvestmentClasses.Otra,                 "pf_haircut_inversion",      "Inversión sin clasificar")]
    public void AFullClassHaircut_IsDescuentoTotal_NamingTheClass(string investmentClass, string key, string label)
    {
        var person = Empty() with { Parameters = With(r => r[key] = 1m) };

        var asset = Evaluate(Investment(investmentClass), person);

        Assert.Equal(FiadorAssetTreatments.Excluido, asset.Treatment);
        Assert.Equal(FiadorAssetReasons.DescuentoTotal, asset.Reason);
        Assert.Equal($"{label}: el descuento configurado es del 100 %, no suma al patrimonio ejecutable.", asset.ReasonText);
        Assert.Equal(0m, asset.ExecutableValue);
    }

    [Fact]
    public void AFullHaircut_OnAnUnclassifiedInvestment_KeepsThe08Text()
    {
        var person = Empty() with { Parameters = With(r => r["pf_haircut_inversion"] = 1m) };

        var asset = Evaluate(Investment(null), person);

        Assert.Equal(FiadorAssetReasons.DescuentoTotal, asset.Reason);
        Assert.Equal("Inversión: el descuento configurado es del 100 %, no suma al patrimonio ejecutable.", asset.ReasonText);
    }

    // ── Parameters: frozen before 0.9 ───────────────────────────────────────────

    [Fact]
    public void Parameters_From_SetsTheThreeClassHaircuts()
    {
        var parameters = PersonEvaluationParameters.Defaults;

        Assert.Equal(0.15m, parameters.HaircutTituloPublico);
        Assert.Equal(0.30m, parameters.HaircutAccionOn);
        Assert.Equal(0.30m, parameters.HaircutFci);
        Assert.Equal(0.30m, parameters.HaircutInversion);
    }

    [Fact]
    public void A08ParametersBlob_StillDeserializes_AndResolvesTheNewHaircutsToTheirDefaults()
    {
        var json = JsonNode.Parse(PersonEvaluationJson.Serialize(PersonEvaluationParameters.Defaults with { HaircutInversion = 0.2m }))!.AsObject();
        foreach (var key in new[] { "haircutTituloPublico", "haircutAccionOn", "haircutFci" })
            Assert.True(json.Remove(key), key);

        var parameters = PersonEvaluationJson.TryDeserialize<PersonEvaluationParameters>(json.ToJsonString());

        Assert.NotNull(parameters);
        Assert.Null(parameters.HaircutTituloPublico);
        Assert.Null(parameters.HaircutAccionOn);
        Assert.Null(parameters.HaircutFci);
        Assert.Equal(0.15m, parameters.ResolvedHaircutTituloPublico);
        Assert.Equal(0.30m, parameters.ResolvedHaircutAccionOn);
        Assert.Equal(0.30m, parameters.ResolvedHaircutFci);
        Assert.Equal(0.2m, parameters.ResolvedHaircutInversion);   // the tenant override it froze
    }

    [Fact]
    public void TheNewMembers_AreSerialisedInCamelCase_ForTheCalibrationBoard()
    {
        var json = JsonNode.Parse(PersonEvaluationJson.Serialize(PersonEvaluationParameters.Defaults))!.AsObject();

        Assert.Equal(0.15m, json["haircutTituloPublico"]!.GetValue<decimal>());
        Assert.Equal(0.30m, json["haircutAccionOn"]!.GetValue<decimal>());
        Assert.Equal(0.30m, json["haircutFci"]!.GetValue<decimal>());
        Assert.False(json.ContainsKey("resolvedHaircutTituloPublico"));
    }

    // ── Assets frozen before 0.9 ────────────────────────────────────────────────

    [Fact]
    public void A08Asset_WithoutTheClass_ReadsUnchanged_AndLabelsAsBefore()
    {
        var coverage = PersonEvaluationService.FiadorEvaluation(PersonEvaluationFiadorTests.Fiador([Investment(null)])).Coverage;
        var json = JsonNode.Parse(PersonEvaluationJson.Serialize(coverage))!.AsObject();
        var asset = json["assets"]!.AsArray()[0]!.AsObject();
        Assert.True(asset.Remove("investmentClass") || !asset.ContainsKey("investmentClass"));
        const string frozenText = "Computado al valor de la manifestación de bienes con un descuento de 30 %: $ 700.000.";
        asset["reasonText"] = frozenText;

        var read = PersonEvaluationJson.TryDeserialize<FiadorCoverageResult>(json.ToJsonString());

        var frozen = Assert.Single(read!.Assets);
        Assert.Null(frozen.InvestmentClass);
        Assert.Equal(0.3m, frozen.Haircut);
        Assert.Equal(700_000m, frozen.ExecutableValue);
        Assert.Equal(frozenText, frozen.ReasonText);
        Assert.Equal("Inversión", PersonLabels.AssetKind(frozen.Kind, frozen.InvestmentClass));
    }

    // ── Labels ──────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryClass_HasItsLabel()
    {
        Assert.Equal(LegajoInvestmentClasses.Known.Order(StringComparer.Ordinal), PersonLabels.InvestmentClassLabels.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("Título público",           PersonLabels.InvestmentClass(LegajoInvestmentClasses.TituloPublico));
        Assert.Equal("Acción con cotización",    PersonLabels.InvestmentClass(LegajoInvestmentClasses.AccionCotizante));
        Assert.Equal("Obligación negociable",    PersonLabels.InvestmentClass(LegajoInvestmentClasses.ObligacionNegociable));
        Assert.Equal("Cuotaparte de FCI",        PersonLabels.InvestmentClass(LegajoInvestmentClasses.CuotaparteFci));
        Assert.Equal("Inversión sin clasificar", PersonLabels.InvestmentClass(LegajoInvestmentClasses.Otra));
    }

    [Theory]
    [InlineData(LegajoItemKinds.Inversion, LegajoInvestmentClasses.TituloPublico,        "Inversión · Título público")]
    [InlineData(LegajoItemKinds.Inversion, LegajoInvestmentClasses.AccionCotizante,      "Inversión · Acción con cotización")]
    [InlineData(LegajoItemKinds.Inversion, LegajoInvestmentClasses.ObligacionNegociable, "Inversión · Obligación negociable")]
    [InlineData(LegajoItemKinds.Inversion, LegajoInvestmentClasses.CuotaparteFci,        "Inversión · Cuotaparte de FCI")]
    [InlineData(LegajoItemKinds.Inversion, LegajoInvestmentClasses.Otra,                 "Inversión sin clasificar")]
    [InlineData(LegajoItemKinds.Inversion, null,                                         "Inversión")]
    [InlineData(LegajoItemKinds.Inversion, "Bono",                                       "Inversión")]
    [InlineData(LegajoItemKinds.Deposito,  LegajoInvestmentClasses.TituloPublico,        "Depósito")]
    public void TheAssetLabel_NamesTheClass(string kind, string? investmentClass, string expected)
    {
        Assert.Equal(expected, PersonLabels.AssetKind(kind, investmentClass));
    }
}
