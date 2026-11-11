// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-29

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;
using Xunit;
using static Api.Web.FinanceIA.Tests.PersonAnalysis.PersonEvaluationFixtures;

namespace Api.Web.FinanceIA.Tests.PersonAnalysis;

/// <summary>
/// The pure fiador evaluation (M10 Phase 5a-2): haircuts by kind, the housing rule, correlated collateral, the
/// currency mismatch, liabilities and guarantees given, the coverage bands at their edges, the stress
/// cuota/ingreso, the asset alerts and the spouse rules. Every figure is synthetic; the amount at charge is
/// 1.000.000 (<see cref="PersonEvaluationFixtures.Empty"/>).
/// </summary>
public sealed class PersonEvaluationFiadorTests
{
    private const string SolicitanteCuit = "20-12345678-6";

    internal static LegajoItem Asset(
        string kind, decimal? market, decimal? fiscal = null, bool housing = false, string? currency = null,
        string? linkedCuit = null, string keyKind = LegajoMatchKeyKinds.Ninguno, string? key = null, decimal? pct = null,
        string status = LegajoReconciliationStatuses.Matched, string? conflicts = null, string side = LegajoItemSides.Activo)
    {
        var item = Item(side, kind, market, documentId: market is null ? null : Guid.NewGuid());
        item.FiscalValue           = fiscal;
        item.FiscalValueDocumentId = fiscal is null ? null : Guid.NewGuid();
        item.IsHousing             = housing;
        item.Currency              = currency;
        item.LinkedCuit            = linkedCuit;
        item.MatchKeyKind          = keyKind;
        item.MatchKey              = key;
        item.OwnershipPct          = pct;
        item.ReconciliationStatus  = status;
        item.ConflictReasons       = conflicts;
        return item;
    }

    internal static FiadorEvaluationInput Fiador(
        LegajoItem[] items, string guaranteeType = GuaranteeTypes.Fianza, bool spouseConsent = false,
        bool housingInopposable = false, decimal guaranteesGiven = 0m, LegajoItem[]? solicitante = null,
        PersonEvaluationInput? person = null)
        => new()
        {
            Person                = (person ?? Empty()) with { Inventory = Inventory(items) },
            SolicitanteCuit       = SolicitanteCuit,
            SolicitanteInventory  = Inventory(solicitante ?? []),
            GuaranteeType         = guaranteeType,
            SpouseConsent         = spouseConsent,
            HousingInopposable    = housingInopposable,
            GuaranteesGivenAmount = guaranteesGiven
        };

    private static FiadorAssetResult Single(FiadorEvaluationResult result)
        => Assert.Single(result.Coverage.Assets);

    private static PersonIndicatorResult IndicatorOf(FiadorEvaluationResult result, string id)
        => Assert.Single(result.Indicators, i => i.Id == id);

    // ── Filters ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Filters_AreTheSolicitantesCuitChecks_WithoutTheCertificateNorTheActivity()
    {
        var result = PersonEvaluationService.FiadorEvaluation(Fiador([]));

        Assert.Equal(
            ["bcra_situacion", "bcra_eventos_graves", "bcra_cheques_impagos", "arca_sucesion", "arca_cuit_activo"],
            result.Filters.Select(f => f.Id));
        Assert.Equal(PersonEvaluationService.Version, result.Version);
    }

    [Fact]
    public void Filters_SinDatos_PointToTheFiadorsLegajo()
    {
        var person = Empty() with { BcraRefreshTarget = "en el legajo del fiador" };
        var result = PersonEvaluationService.FiadorEvaluation(Fiador([], person: person));

        var filter = Assert.Single(result.Filters, f => f.Id == "bcra_situacion");
        Assert.Equal(PersonFilterOutcomes.SinDatos, filter.Outcome);
        Assert.Contains("Actualizá BCRA en el legajo del fiador.", filter.Evidence);
    }

    [Fact]
    public void Filters_SituacionAtTheRejectionCut_SuggestsARejection()
    {
        var person = Empty() with { Bcra = Bcra(Deuda(situacion: 5), Cheques(BcraChequesConsultaEstado.NoChequesReported)) };

        Assert.Equal(PersonSuggestions.RechazoSugerido,
            PersonEvaluationService.FiadorEvaluation(Fiador([], person: person)).Suggestion);
    }

    // ── Haircuts ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(LegajoItemKinds.Inmueble,      0.50, 500_000)]
    [InlineData(LegajoItemKinds.Rodado,        0.40, 600_000)]
    [InlineData(LegajoItemKinds.Participacion, 0.60, 400_000)]
    public void EachKind_TakesItsHaircut(string kind, double haircut, double executable)
    {
        var asset = Single(PersonEvaluationService.FiadorEvaluation(Fiador([Asset(kind, 1_000_000m)])));

        Assert.Equal(FiadorAssetTreatments.Computado, asset.Treatment);
        Assert.Equal(FiadorAssetReasons.ValorManifestacion, asset.Reason);
        Assert.Equal((decimal)haircut, asset.Haircut);
        Assert.Null(asset.CurrencyHaircut);
        Assert.Equal((decimal)executable, asset.ExecutableValue);
        Assert.Equal(PersonEvidenceKinds.Declarado, asset.EvidenceKind);
    }

    [Fact]
    public void Haircuts_AreTenantParameters()
    {
        var person = Empty() with { Parameters = With(r => r["pf_haircut_rodado"] = 0.10m) };

        var asset = Single(PersonEvaluationService.FiadorEvaluation(
            Fiador([Asset(LegajoItemKinds.Rodado, 1_000_000m)], person: person)));

        Assert.Equal(900_000m, asset.ExecutableValue);
    }

    // ── Deposits, investments, receivables, other (engine 0.6) ────────────────────

    [Theory]
    [InlineData(LegajoItemKinds.Deposito,  0.0, 1_000_000)]   // pf_haircut_deposito
    [InlineData(LegajoItemKinds.Inversion, 0.3,   700_000)]   // pf_haircut_inversion
    public void DepositsAndInvestments_AreCounted_AfterTheirHaircut(string kind, double haircut, double executable)
    {
        var result = PersonEvaluationService.FiadorEvaluation(Fiador([Asset(kind, 1_000_000m)]));
        var asset  = Single(result);

        Assert.Equal(FiadorAssetTreatments.Computado, asset.Treatment);
        Assert.Equal(FiadorAssetReasons.ValorManifestacion, asset.Reason);
        Assert.Equal((decimal)haircut, asset.Haircut);
        Assert.Null(asset.CurrencyHaircut);
        Assert.Equal((decimal)executable, asset.ExecutableValue);
        Assert.Equal((decimal)executable, result.Coverage.GrossExecutable);
    }

    [Fact]
    public void Deposit_InAnotherCurrency_AddsTheDescalceHaircut()
    {
        var asset = Single(PersonEvaluationService.FiadorEvaluation(
            Fiador([Asset(LegajoItemKinds.Deposito, 1_000_000m, currency: "USD")])));

        Assert.Equal(FiadorAssetTreatments.Computado, asset.Treatment);
        Assert.Equal(0m, asset.Haircut);
        Assert.Equal(0.08m, asset.CurrencyHaircut);
        Assert.Equal(920_000m, asset.ExecutableValue);
        Assert.Contains("descalce de moneda (USD frente a ARS)", asset.ReasonText);
    }

    [Theory]
    [InlineData(LegajoItemKinds.Credito, FiadorAssetReasons.NoEjecutableSinDocumentacion, "no es ejecutable sin documentación")]
    [InlineData(LegajoItemKinds.Otro,    FiadorAssetReasons.BienNoAdmitido,               "no se admite")]
    public void ReceivablesAndOther_AtTheirDefaultFullHaircut_AreExcluded_WithTheirReason(string kind, string reason, string text)
    {
        var result = PersonEvaluationService.FiadorEvaluation(Fiador([Asset(kind, 1_000_000m)]));
        var asset  = Single(result);

        Assert.Equal(FiadorAssetTreatments.Excluido, asset.Treatment);
        Assert.Equal(reason, asset.Reason);
        Assert.Contains(text, asset.ReasonText);
        Assert.Equal(0m, asset.ExecutableValue);
        Assert.Equal(0m, result.Coverage.GrossExecutable);
    }

    [Fact]
    public void Receivable_WithATenantHaircutBelowOne_IsCounted()
    {
        var person = Empty() with { Parameters = With(r => r["pf_haircut_credito"] = 0.5m) };

        var asset = Single(PersonEvaluationService.FiadorEvaluation(
            Fiador([Asset(LegajoItemKinds.Credito, 1_000_000m)], person: person)));

        Assert.Equal(FiadorAssetTreatments.Computado, asset.Treatment);
        Assert.Equal(0.5m, asset.Haircut);
        Assert.Equal(500_000m, asset.ExecutableValue);
    }

    [Theory]
    [InlineData(LegajoItemKinds.Deposito,  "pf_haircut_deposito")]
    [InlineData(LegajoItemKinds.Inversion, "pf_haircut_inversion")]
    public void DepositOrInvestment_AtATenantFullHaircut_IsExcluded_AsDescuentoTotal(string kind, string key)
    {
        var person = Empty() with { Parameters = With(r => r[key] = 1m) };

        var asset = Single(PersonEvaluationService.FiadorEvaluation(Fiador([Asset(kind, 1_000_000m)], person: person)));

        Assert.Equal(FiadorAssetTreatments.Excluido, asset.Treatment);
        Assert.Equal(FiadorAssetReasons.DescuentoTotal, asset.Reason);
        Assert.Contains("100 %", asset.ReasonText);
    }

    [Fact]
    public void AnUnknownKind_IsListed_NotCounted()
    {
        var item = Asset(LegajoItemKinds.Otro, 1_000_000m);
        item.Kind = "Semoviente";

        var result = PersonEvaluationService.FiadorEvaluation(Fiador([item]));
        var asset  = Single(result);

        Assert.Equal(FiadorAssetTreatments.NoComputado, asset.Treatment);
        Assert.Equal(FiadorAssetReasons.TipoNoReconocido, asset.Reason);
        Assert.Contains("no reconocido", asset.ReasonText);
        Assert.Equal(0m, result.Coverage.GrossExecutable);
    }

    [Fact]
    public void AnAssetFrozenBefore06_AsPendienteDeDefinicion_StillReads()
    {
        const string json = """
            { "itemId": "8f1c2a8e-3c9b-4d6a-9a51-2b0f7f5a1e10", "origin": "propio", "kind": "Deposito", "matchKeyKind": "Ninguno",
              "valueUsed": 1000000, "isHousing": false, "soloValorFiscal": false, "executableValue": 0,
              "treatment": "NoComputado", "reason": "pendiente_de_definicion",
              "reasonText": "Depósito: no computado en esta versión (descuento pendiente de definición).",
              "evidenceKind": "Declarado", "alerts": [] }
            """;

        var asset = PersonEvaluationJson.TryDeserialize<FiadorAssetResult>(json);

        Assert.NotNull(asset);
        Assert.Equal(FiadorAssetTreatments.NoComputado, asset.Treatment);
        Assert.Equal(FiadorAssetReasons.PendienteDeDefinicion, asset.Reason);
    }

    [Fact]
    public void OwnershipPct_IsNotAppliedAgain_TheValueAlreadyIsTheShare()
    {
        var asset = Single(PersonEvaluationService.FiadorEvaluation(
            Fiador([Asset(LegajoItemKinds.Inmueble, 1_000_000m, pct: 50m)])));

        Assert.Equal(500_000m, asset.ExecutableValue);
        Assert.Equal(50m, asset.OwnershipPct);
    }

    // ── Housing ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Housing_WithoutConsentNorInopponibility_IsExcluded()
    {
        var asset = Single(PersonEvaluationService.FiadorEvaluation(
            Fiador([Asset(LegajoItemKinds.Inmueble, 1_000_000m, housing: true)])));

        Assert.Equal(FiadorAssetTreatments.Excluido, asset.Treatment);
        Assert.Equal(FiadorAssetReasons.ViviendaSinAsentimiento, asset.Reason);
        Assert.Equal(0m, asset.ExecutableValue);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Housing_WithConsentOrInopponibility_CountsAt25Pct(bool consent, bool inopposable)
    {
        var asset = Single(PersonEvaluationService.FiadorEvaluation(Fiador(
            [Asset(LegajoItemKinds.Inmueble, 1_000_000m, housing: true)],
            spouseConsent: consent, housingInopposable: inopposable)));

        Assert.Equal(FiadorAssetTreatments.Computado, asset.Treatment);
        Assert.Equal(0.25m, asset.Haircut);
        Assert.Equal(750_000m, asset.ExecutableValue);
    }

    // ── Correlated collateral and currency ────────────────────────────────────────

    [Theory]
    [InlineData(LegajoItemKinds.Participacion)]
    [InlineData(LegajoItemKinds.Credito)]
    public void LinkedToTheSolicitantesCuit_IsExcluded(string kind)
    {
        var asset = Single(PersonEvaluationService.FiadorEvaluation(Fiador(
            [Asset(kind, 1_000_000m, linkedCuit: "20123456786", keyKind: LegajoMatchKeyKinds.Cuit, key: "20123456786")])));

        Assert.Equal(FiadorAssetTreatments.Excluido, asset.Treatment);
        Assert.Equal(FiadorAssetReasons.GarantiaCorrelacionada, asset.Reason);
    }

    [Fact]
    public void LinkedToAnotherCuit_Counts()
    {
        var asset = Single(PersonEvaluationService.FiadorEvaluation(Fiador(
            [Asset(LegajoItemKinds.Participacion, 1_000_000m, linkedCuit: "30-55555555-1")])));

        Assert.Equal(FiadorAssetTreatments.Computado, asset.Treatment);
    }

    [Fact]
    public void CurrencyMismatch_AddsTheDescalceHaircut()
    {
        var asset = Single(PersonEvaluationService.FiadorEvaluation(
            Fiador([Asset(LegajoItemKinds.Inmueble, 1_000_000m, currency: "USD")])));

        Assert.Equal(0.50m, asset.Haircut);
        Assert.Equal(0.08m, asset.CurrencyHaircut);
        Assert.Equal(420_000m, asset.ExecutableValue);
        Assert.Contains("descalce de moneda (USD frente a ARS)", asset.ReasonText);
    }

    [Fact]
    public void SameCurrency_HasNoDescalce()
    {
        var asset = Single(PersonEvaluationService.FiadorEvaluation(
            Fiador([Asset(LegajoItemKinds.Inmueble, 1_000_000m, currency: "ARS")])));

        Assert.Null(asset.CurrencyHaircut);
        Assert.Equal(500_000m, asset.ExecutableValue);
    }

    // ── Value source and alerts ───────────────────────────────────────────────────

    [Fact]
    public void OnlyAFiscalValue_IsUsed_AndMarkedSoloValorFiscal()
    {
        var asset = Single(PersonEvaluationService.FiadorEvaluation(Fiador(
            [Asset(LegajoItemKinds.Inmueble, null, fiscal: 800_000m, status: LegajoReconciliationStatuses.OnlyInBienesPersonales)])));

        Assert.True(asset.SoloValorFiscal);
        Assert.Equal("bienes_personales", asset.ValueSource);
        Assert.Equal(FiadorAssetReasons.SoloValorFiscal, asset.Reason);
        Assert.Equal(PersonEvidenceKinds.Verificado, asset.EvidenceKind);
        Assert.Equal(400_000m, asset.ExecutableValue);
        Assert.Contains(FiadorAssetAlerts.OnlyInBienesPersonales, asset.Alerts);
    }

    [Fact]
    public void TheFiscalValue_NeverReplacesTheManifestacion_BelowFloorStaysAnAlert()
    {
        var asset = Single(PersonEvaluationService.FiadorEvaluation(Fiador(
            [Asset(LegajoItemKinds.Inmueble, 900_000m, fiscal: 1_000_000m,
                status: LegajoReconciliationStatuses.Conflict, conflicts: LegajoItemConflictReasons.BelowFiscalFloor)])));

        Assert.Equal(900_000m, asset.ValueUsed);
        Assert.Equal("manifestacion", asset.ValueSource);
        Assert.Equal(450_000m, asset.ExecutableValue);
        Assert.Equal([FiadorAssetAlerts.BelowFiscalFloor], asset.Alerts);
    }

    [Fact]
    public void OnlyInTheManifestacion_IsAnAlert()
    {
        var asset = Single(PersonEvaluationService.FiadorEvaluation(Fiador(
            [Asset(LegajoItemKinds.Rodado, 100_000m, status: LegajoReconciliationStatuses.OnlyInManifestacion)])));

        Assert.Equal([FiadorAssetAlerts.OnlyInManifestacion], asset.Alerts);
    }

    [Theory]
    [InlineData(3_000_000, false)]   // exactly 30 times: not above
    [InlineData(3_000_001, true)]
    public void ManifestacionFarAboveTheFiscalValue_IsAnAlert(double market, bool alert)
    {
        var asset = Single(PersonEvaluationService.FiadorEvaluation(Fiador(
            [Asset(LegajoItemKinds.Inmueble, (decimal)market, fiscal: 100_000m)])));

        Assert.Equal(alert, asset.Alerts.Contains(FiadorAssetAlerts.MercadoMuySuperiorAlFiscal));
    }

    [Fact]
    public void NoValueAtAll_IsExcluded()
    {
        var asset = Single(PersonEvaluationService.FiadorEvaluation(Fiador([Asset(LegajoItemKinds.Rodado, null)])));

        Assert.Equal(FiadorAssetTreatments.Excluido, asset.Treatment);
        Assert.Equal(FiadorAssetReasons.SinValor, asset.Reason);
    }

    // ── Liabilities, guarantees given, bands ──────────────────────────────────────

    [Fact]
    public void LiabilitiesAndGuaranteesGiven_AreSubtracted()
    {
        var result = PersonEvaluationService.FiadorEvaluation(Fiador(
            [Asset(LegajoItemKinds.Inmueble, 2_000_000m), Asset(LegajoItemKinds.Deuda, 300_000m, side: LegajoItemSides.Pasivo)],
            guaranteesGiven: 200_000m));

        Assert.Equal(1_000_000m, result.Coverage.GrossExecutable);
        Assert.Equal(300_000m, result.Coverage.Liabilities);
        Assert.Equal("manifestacion", result.Coverage.LiabilitiesSource);
        Assert.Equal(200_000m, result.Coverage.GuaranteesGivenAmount);
        Assert.Equal(500_000m, result.Coverage.ExecutablePatrimony);
        Assert.Single(result.Coverage.LiabilityItemIds);
        Assert.Equal(0.5m, IndicatorOf(result, "cobertura_patrimonial_fiador").Value);
    }

    [Fact]
    public void NoDeclaredDebt_ButTheCentralDeDeudoresHasSome_UsesTheBcraTotal()
    {
        var person = Empty() with { Bcra = Bcra(Deuda(montoTotal: 100m)) };   // thousands of pesos
        var result = PersonEvaluationService.FiadorEvaluation(Fiador([Asset(LegajoItemKinds.Inmueble, 2_000_000m)], person: person));

        Assert.Equal(100_000m, result.Coverage.Liabilities);
        Assert.Equal("bcra", result.Coverage.LiabilitiesSource);
        Assert.Equal(900_000m, result.Coverage.ExecutablePatrimony);
    }

    [Theory]
    [InlineData(1_999_800, 0.9999, PersonIndicatorBands.Rojo)]
    [InlineData(2_000_000, 1.0, PersonIndicatorBands.Amarillo)]   // = rojo: not below it
    [InlineData(3_999_800, 1.9999, PersonIndicatorBands.Amarillo)]
    [InlineData(4_000_000, 2.0, PersonIndicatorBands.Verde)]      // = verde: from it
    public void Coverage_BandsAtTheEdges(double inmueble, double ratio, string band)
    {
        // The ratio is rounded to 4 places before it is banded, as every persona ratio is.
        var indicator = IndicatorOf(
            PersonEvaluationService.FiadorEvaluation(Fiador([Asset(LegajoItemKinds.Inmueble, (decimal)inmueble)])),
            "cobertura_patrimonial_fiador");

        Assert.Equal(PersonIndicatorStatuses.Ok, indicator.Status);
        Assert.Equal((decimal)ratio, indicator.Value);
        Assert.Equal(band, indicator.Band);
        Assert.Equal(PersonEvidenceKinds.Declarado, indicator.EvidenceKind);
    }

    [Fact]
    public void Coverage_OfAnOperationNotInPesos_IsSinDatos()
    {
        var person = Empty() with { Currency = "USD" };
        var result = PersonEvaluationService.FiadorEvaluation(Fiador([Asset(LegajoItemKinds.Inmueble, 2_000_000m)], person: person));

        Assert.False(result.Coverage.Comparable);
        var indicator = IndicatorOf(result, "cobertura_patrimonial_fiador");
        Assert.Equal(PersonIndicatorStatuses.SinDatos, indicator.Status);
        Assert.Contains("no se convierte moneda", indicator.Evidence);
    }

    [Fact]
    public void Coverage_OfAnEmptyInventory_IsSinDatos()
    {
        Assert.Equal(PersonIndicatorStatuses.SinDatos,
            IndicatorOf(PersonEvaluationService.FiadorEvaluation(Fiador([])), "cobertura_patrimonial_fiador").Status);
    }

    [Fact]
    public void Coverage_ReadOnlyFromTheFiscalValue_IsVerificado()
    {
        var indicator = IndicatorOf(PersonEvaluationService.FiadorEvaluation(Fiador(
            [Asset(LegajoItemKinds.Inmueble, null, fiscal: 4_000_000m)])), "cobertura_patrimonial_fiador");

        Assert.Equal(PersonEvidenceKinds.Verificado, indicator.EvidenceKind);
    }

    // ── Stress cuota/ingreso ──────────────────────────────────────────────────────

    private static PersonEvaluationInput Earner(decimal cuota, decimal? bcraMiles = null)
        => Empty() with
        {
            Incomes           = Incomes(Year(LegajoIncomeSources.Ganancias, LegajoIncomeConcepts.Renta3ra, 2025, 12_000_000m)),
            Bcra              = bcraMiles is { } m ? Bcra(Deuda(montoTotal: m)) : Bcra(Deuda(BcraDeudaConsultaEstado.NoDebtReported, null, montoTotal: null)),
            InstallmentAmount = cuota
        };

    [Theory]
    [InlineData(499_900, PersonIndicatorBands.Verde)]
    [InlineData(500_000, PersonIndicatorBands.Verde)]   // = the maximum: not above it
    [InlineData(500_100, PersonIndicatorBands.Rojo)]   // 0,5001: ratios are rounded to 4 places
    public void Stress_FullCuotaOverMonthlyIncome_AgainstTheMaximum(double cuota, string band)
    {
        var indicator = IndicatorOf(
            PersonEvaluationService.FiadorEvaluation(Fiador([], person: Earner((decimal)cuota))), "cuota_ingreso_estres");

        Assert.Equal(PersonIndicatorStatuses.Ok, indicator.Status);
        Assert.Equal(band, indicator.Band);
        Assert.Equal(band == PersonIndicatorBands.Rojo ? PersonSeverities.Normal : null, indicator.Severity);
        Assert.Equal(PersonEvidenceKinds.Verificado, indicator.EvidenceKind);
    }

    [Fact]
    public void Stress_AddsTheEstimatedCurrentCuotas()
    {
        // 2.400 miles = $ 2.400.000; 5 % = 120.000 a month; (400.000 + 120.000) / 1.000.000 = 0,52.
        var indicator = IndicatorOf(
            PersonEvaluationService.FiadorEvaluation(Fiador([], person: Earner(400_000m, bcraMiles: 2_400m))), "cuota_ingreso_estres");

        Assert.Equal(0.52m, indicator.Value);
        Assert.Equal("120000", indicator.Inputs["cuotasVigentesEstimadas"]);
        Assert.Equal("0.05", indicator.Inputs["cuotaEstimadaPctSaldo"]);
        Assert.False(indicator.Inputs.ContainsKey("plazoEstimadoDeudaMeses"));
        Assert.Contains("(5 % del saldo de la Central de Deudores)", indicator.Evidence);
    }

    [Fact]
    public void Stress_WithoutACuota_IsSinDatos()
    {
        var person = Earner(1m) with { InstallmentAmount = null };

        Assert.Equal(PersonIndicatorStatuses.SinDatos,
            IndicatorOf(PersonEvaluationService.FiadorEvaluation(Fiador([], person: person)), "cuota_ingreso_estres").Status);
    }

    // ── Spouse ────────────────────────────────────────────────────────────────────

    private static readonly LegajoItem[] SolicitanteHome =
    [
        Asset(LegajoItemKinds.Inmueble, 2_000_000m, housing: true, keyKind: LegajoMatchKeyKinds.Partida, key: "P1")
    ];

    [Fact]
    public void Spouse_WithConsent_MakesTheSolicitantesHousingExecutable()
    {
        var result = PersonEvaluationService.FiadorEvaluation(Fiador(
            [Asset(LegajoItemKinds.Rodado, 1_000_000m, keyKind: LegajoMatchKeyKinds.Dominio, key: "AB123CD")],
            GuaranteeTypes.Conyuge, spouseConsent: true, solicitante: SolicitanteHome));

        var home = Assert.Single(result.Coverage.Assets, a => a.Origin == FiadorAssetOrigins.Solicitante);
        Assert.Equal(FiadorAssetTreatments.Computado, home.Treatment);
        Assert.Equal(1_500_000m, home.ExecutableValue);
        Assert.Equal(2_100_000m, result.Coverage.GrossExecutable);
    }

    [Fact]
    public void Spouse_WithoutConsent_DoesNotReachTheSolicitantesHousing()
    {
        var result = PersonEvaluationService.FiadorEvaluation(Fiador([], GuaranteeTypes.Conyuge, solicitante: SolicitanteHome));

        Assert.DoesNotContain(result.Coverage.Assets, a => a.Origin == FiadorAssetOrigins.Solicitante);
    }

    [Fact]
    public void Spouse_AnAssetAlsoInTheSolicitantesInventory_NeverCountsOnItsOwn_EvenIfTheSharesFit()
    {
        var result = PersonEvaluationService.FiadorEvaluation(Fiador(
            [Asset(LegajoItemKinds.Inmueble, 1_000_000m, keyKind: LegajoMatchKeyKinds.Partida, key: "P1", pct: 50m)],
            GuaranteeTypes.Conyuge, solicitante:
            [Asset(LegajoItemKinds.Inmueble, 1_000_000m, keyKind: LegajoMatchKeyKinds.Partida, key: "P1", pct: 50m)]));

        var own = Single(result);
        Assert.Equal(FiadorAssetReasons.EnInventarioDelSolicitante, own.Reason);
        Assert.Equal(0m, result.Coverage.GrossExecutable);
    }

    [Fact]
    public void Fianza_AnAssetAlsoInTheSolicitantesInventory_StillCounts()
    {
        var asset = Single(PersonEvaluationService.FiadorEvaluation(Fiador(
            [Asset(LegajoItemKinds.Inmueble, 1_000_000m, keyKind: LegajoMatchKeyKinds.Partida, key: "P1")],
            solicitante: SolicitanteHome)));

        Assert.Equal(FiadorAssetTreatments.Computado, asset.Treatment);
    }

    [Fact]
    public void GuaranteeTypes_ArePinned()
    {
        Assert.Equal(["Fianza", "Conyuge"], GuaranteeTypes.Known);
        Assert.True(GuaranteeTypes.IsKnown("Conyuge"));
        Assert.False(GuaranteeTypes.IsKnown("conyuge"));
        Assert.False(GuaranteeTypes.IsKnown(null));
    }
}
