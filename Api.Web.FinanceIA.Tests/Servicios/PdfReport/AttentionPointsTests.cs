// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-08

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.Pdf;
using Api.Web.FinanceIA.Servicios.ScoreReliability;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios.PdfReport;

/// <summary>"Puntos de atención": which facts become bullets, in what order, and the cap.</summary>
public sealed class AttentionPointsTests
{
    private static AttentionPoints.Facts Rich() => new()
    {
        ReliabilityLevel   = ScoreReliabilityLevels.Provisional,
        ReliabilityReasons =
        [
            new(ScoreReliabilitySeverities.Caveat, "El sector no tiene perfil propio."),
            new(ScoreReliabilitySeverities.Provisional, "El balance no supera los controles de integridad."),
        ],
        FloorAppliedTo      = 350m,
        CriticalRatioLabels = ["Endeudamiento"],
        Adjustments         =
        [
            (ScoreAnatomy.AdjustmentKey.Ventas, "Ventas declaradas (LUFE)", -100m),
            (ScoreAnatomy.AdjustmentKey.Casfog, "Garantías SGR en mora (CASFOG)", 20m),
        ],
        BcraSituacion       = 3,
        BcraTrendDelta      = 1,
        ChequesRechazados   = 3,
        ChequesImpagos      = 1,
        Alerts              =
        [
            new("low", "CAPEX_SOSTENIDO", "Inversión sostenida"),
            new("high", "DEUDA_CORTO_PLAZO", "Concentración de deuda en el corto plazo"),
            new("critical", "VENTAS_EN_CAIDA", null),
            new("medium", "INFLATION_ADJUSTMENT_RT6", null),
        ],
    };

    [Fact]
    public void MostSevereFirst_CappedAtFive()
    {
        IReadOnlyList<string> points = AttentionPoints.Select(Rich());

        Assert.Equal(
        [
            "Ventas declaradas (LUFE): −100 pts sobre el score.",                 // the critical VENTAS_EN_CAIDA alert, said with figures
            "El balance no supera los controles de integridad.",                  // provisional reason
            "Piso por ratio crítico: el score se llevó a 350 (Endeudamiento).",   // floor applied
            "Situación BCRA 3 — Con problemas, empeorando 1 nivel.",              // situación ≥ 3, trend merged in
            "Concentración de deuda en el corto plazo",                           // high alert, the model's title
        ], points);
    }

    [Fact]
    public void TheCapCanTrimToThree()
    {
        Assert.Equal(3, AttentionPoints.Select(Rich(), cap: 3).Count);
    }

    [Fact]
    public void BelowTheCap_TheRestFollowInOrder_AndOnlyPenaltiesCount()
    {
        IReadOnlyList<string> points = AttentionPoints.Select(Rich(), cap: 20);

        Assert.Single(points, p => p.StartsWith("Ventas", StringComparison.Ordinal));
        Assert.DoesNotContain(points, p => p.Contains("CASFOG", StringComparison.Ordinal));   // +20 took nothing
        Assert.Single(points, p => p.StartsWith("Situación BCRA", StringComparison.Ordinal));
        Assert.Contains("3 cheques rechazados (1 impago).", points);
        Assert.Contains("El sector no tiene perfil propio.", points);
        Assert.DoesNotContain(points, p => p.Contains("Inversión sostenida", StringComparison.Ordinal));  // low alert
        Assert.DoesNotContain(points, p => p.Contains("RT 6", StringComparison.Ordinal));                 // medium alert
        Assert.Equal("3 cheques rechazados (1 impago).", points[^2]);
        Assert.Equal("El sector no tiene perfil propio.", points[^1]);   // a caveat is the least severe
    }

    [Fact]
    public void ACompleteVerdict_AddsNoReliabilityBullet()
    {
        var facts = new AttentionPoints.Facts
        {
            ReliabilityLevel   = ScoreReliabilityLevels.Complete,
            ReliabilityReasons = [new(ScoreReliabilitySeverities.Caveat, "Texto que no debe aparecer.")],
        };

        Assert.Empty(AttentionPoints.Select(facts));
    }

    [Fact]
    public void NothingToSay_IsEmpty_AndThePageSaysSo()
    {
        Assert.Empty(AttentionPoints.Select(new AttentionPoints.Facts { BcraSituacion = 1, ChequesRechazados = 0 }));
        Assert.Equal("Sin puntos de atención.", AttentionPoints.None);
    }

    [Fact]
    public void RegularizedCheques_AreStillMentioned_BelowUnpaidOnes()
    {
        IReadOnlyList<string> points = AttentionPoints.Select(new AttentionPoints.Facts { ChequesRechazados = 1, ChequesImpagos = 0 });

        Assert.Equal(["1 cheque rechazado (todos regularizados)."], points);
    }

    // ── One bullet per fact (AttentionOverlaps) ─────────────────────────────────

    private static AttentionPoints.Facts WithFact(AttentionFact fact, params AttentionPoints.Alert[] alerts) => fact switch
    {
        AttentionFact.Ventas  => new() { Adjustments = [(ScoreAnatomy.AdjustmentKey.Ventas, "Ventas declaradas (LUFE)", -40m)], Alerts = alerts },
        AttentionFact.Bcra    => new() { BcraSituacion = 2, Alerts = alerts },
        AttentionFact.Cheques => new() { ChequesRechazados = 2, ChequesImpagos = 1, Alerts = alerts },
        AttentionFact.InterestCoverage => new()
        {
            ReliabilityLevel   = ScoreReliabilityLevels.WithCaveats,
            ReliabilityReasons = [CoverageReason(ScoreReliabilityCodes.RatiosNotMeaningful)],
            Alerts             = alerts,
        },
        AttentionFact.CriticalFloor => new()
        {
            FloorAppliedTo      = 350m,
            CriticalRatioLabels = ["Endeudamiento"],
            CriticalRatioKeys   = ["endeudamiento"],
            Alerts              = alerts,
        },
        _                     => throw new ArgumentOutOfRangeException(nameof(fact)),
    };

    private const string CoverageMessage = "No se pudo calcular Cobertura de intereses de forma significativa (por ejemplo, por un denominador en cero) y se excluyó del score.";
    private const string FloorText       = "Piso por ratio crítico: el score se llevó a 350 (Endeudamiento).";

    private static AttentionPoints.ReliabilityReason CoverageReason(string code, params string[] extraKeys) =>
        new(ScoreReliabilitySeverities.Caveat, CoverageMessage, code, ["cobertura_intereses", .. extraKeys]);

    private static string FactText(AttentionFact fact) => fact switch
    {
        AttentionFact.Ventas  => "Ventas declaradas (LUFE): −40 pts sobre el score.",
        AttentionFact.Bcra    => "Situación BCRA 2 — Con seguimiento especial.",
        AttentionFact.Cheques => "2 cheques rechazados (1 impago).",
        AttentionFact.InterestCoverage => CoverageMessage,
        AttentionFact.CriticalFloor    => FloorText,
        _                     => throw new ArgumentOutOfRangeException(nameof(fact)),
    };

    public static TheoryData<string, string> OverlapPairs()
    {
        var data = new TheoryData<string, string>();   // the fact by name: AttentionFact is internal
        foreach ((AttentionFact fact, IReadOnlyList<string> codes) in AttentionOverlaps.Codes)
        {
            foreach (string code in codes)
                data.Add(fact.ToString(), code);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(OverlapPairs))]
    public void AnOverlappingCriticalAlert_GivesWayToItsFact_AtTheAlertsRank(string factName, string code)
    {
        AttentionFact fact = Enum.Parse<AttentionFact>(factName);

        // A reliability reason at 95 sits between a critical alert (100) and every fact's own rank (≤ 85).
        AttentionPoints.Facts withFact = WithFact(fact, new AttentionPoints.Alert("critical", code.ToLowerInvariant(), "Título del modelo"));
        AttentionPoints.Facts facts = withFact with
        {
            ReliabilityLevel   = ScoreReliabilityLevels.Provisional,
            ReliabilityReasons = [.. withFact.ReliabilityReasons, new(ScoreReliabilitySeverities.Provisional, "Motivo provisorio.")],
        };

        IReadOnlyList<string> points = AttentionPoints.Select(facts, cap: 20);

        Assert.Equal([FactText(fact), "Motivo provisorio."], points);
    }

    [Theory]
    [MemberData(nameof(OverlapPairs))]
    public void AnOverlappingAlert_WhoseFactIsAbsent_IsPrinted(string factName, string code)
    {
        Assert.True(Enum.IsDefined(Enum.Parse<AttentionFact>(factName)));
        IReadOnlyList<string> points = AttentionPoints.Select(
            new AttentionPoints.Facts { BcraSituacion = 1, Alerts = [new("high", code, "Título del modelo")] });

        // A catalogue code the model happens to use reads with the catalogue's curated name (AlertTitles).
        Assert.Equal([AlertTitles.Resolve(code, "Título del modelo")!], points);
    }

    [Fact]
    public void Codes_AreListedOnce_AcrossEveryFact()
    {
        string[] all = AttentionOverlaps.Codes.Values.SelectMany(codes => codes).ToArray();

        Assert.Equal(all.Length, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Null(AttentionOverlaps.FactOf("DEUDA_CORTO_PLAZO"));
        Assert.Null(AttentionOverlaps.FactOf(null));
        Assert.Equal(AttentionFact.Ventas, AttentionOverlaps.FactOf(" ventas_en_caida "));
    }

    [Fact]
    public void Pair_VentasAlert_VsLufeFact_PrintsTheFigures()
    {
        IReadOnlyList<string> points = AttentionPoints.Select(WithFact(AttentionFact.Ventas, new AttentionPoints.Alert("high", "VENTAS_EN_CAIDA", null)));

        Assert.Equal(["Ventas declaradas (LUFE): −40 pts sobre el score."], points);
    }

    [Fact]
    public void Pair_VentasAlert_WithoutAPenalty_KeepsTheAlert()
    {
        var facts = new AttentionPoints.Facts
        {
            Adjustments = [(ScoreAnatomy.AdjustmentKey.Ventas, "Ventas declaradas (LUFE)", 0m)],
            Alerts      = [new("high", "VENTAS_EN_CAIDA", null)],
        };

        Assert.Equal(["Ventas en caída"], AttentionPoints.Select(facts));
    }

    [Fact]
    public void Pair_BcraAlert_VsSituacionFact_PrintsTheSituacion()
    {
        var facts = new AttentionPoints.Facts { BcraSituacion = 4, Alerts = [new("high", "BCRA_SITUACION", "Deterioro en BCRA")] };

        Assert.Equal(["Situación BCRA 4 — Con alto riesgo de insolvencia."], AttentionPoints.Select(facts));
    }

    [Fact]
    public void Pair_Situacion2_AndTrend_MergeIntoOneLine()
    {
        IReadOnlyList<string> points = AttentionPoints.Select(new AttentionPoints.Facts { BcraSituacion = 2, BcraTrendDelta = 1 });

        Assert.Equal(["Situación BCRA 2 — Con seguimiento especial, empeorando 1 nivel."], points);
    }

    [Fact]
    public void Pair_Situacion3_AndTrend_MergeIntoOneLine_Plural()
    {
        IReadOnlyList<string> points = AttentionPoints.Select(new AttentionPoints.Facts { BcraSituacion = 3, BcraTrendDelta = 2 });

        Assert.Equal(["Situación BCRA 3 — Con problemas, empeorando 2 niveles."], points);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public void TrendAlone_WhenTheSituacionIsNormalOrUnknown(int? situacion)
    {
        IReadOnlyList<string> points = AttentionPoints.Select(new AttentionPoints.Facts { BcraSituacion = situacion, BcraTrendDelta = 2 });

        Assert.Equal(["Situación BCRA empeorando (2 niveles)."], points);
    }

    [Fact]
    public void AnImprovingTrend_AddsNothing()
    {
        Assert.Equal(["Situación BCRA 2 — Con seguimiento especial."],
            AttentionPoints.Select(new AttentionPoints.Facts { BcraSituacion = 2, BcraTrendDelta = -1 }));
    }

    [Fact]
    public void Pair_ChequesAlert_VsChequesFact_PrintsTheCounts()
    {
        IReadOnlyList<string> points = AttentionPoints.Select(WithFact(AttentionFact.Cheques, new AttentionPoints.Alert("high", "CHEQUES_RECHAZADOS", "Cheques rechazados")));

        Assert.Equal(["2 cheques rechazados (1 impago)."], points);
    }

    [Fact]
    public void ANonOverlappingAlert_IsKeptBesideTheFact()
    {
        IReadOnlyList<string> points = AttentionPoints.Select(
            WithFact(AttentionFact.Ventas, new AttentionPoints.Alert("high", "DEUDA_CORTO_PLAZO", "Concentración de deuda")));

        Assert.Equal(["Concentración de deuda", "Ventas declaradas (LUFE): −40 pts sobre el score."], points);
    }

    [Fact]
    public void AlertTitles_UseTheTenantsCatalogueName()
    {
        var facts = new AttentionPoints.Facts
        {
            Alerts            = [new("critical", "ENDEUDAMIENTO_ELEVADO", "Título del modelo")],
            AlertDisplayNames = new Dictionary<string, string> { ["ENDEUDAMIENTO_ELEVADO"] = "Endeudamiento por encima del umbral" },
        };

        Assert.Equal(["Endeudamiento por encima del umbral"], AttentionPoints.Select(facts));
    }

    // ── Both channels (catalogue first) ─────────────────────────────────────────

    private static AttentionPoints.Alert Cat(string level, string code, string? title) => new(level, code, title);

    [Fact]
    public void ACatalogueAlert_ComesBeforeAModelAlert_OfTheSameSeverity()
    {
        var facts = new AttentionPoints.Facts
        {
            Alerts          = [new("high", "DEUDA_CORTO_PLAZO", "Concentración de deuda")],
            CatalogueAlerts = [Cat("high", "PASIVO_100_CORRIENTE", "Pasivo íntegramente corriente")],
        };

        Assert.Equal(["Pasivo íntegramente corriente", "Concentración de deuda"], AttentionPoints.Select(facts));
    }

    [Fact]
    public void ACriticalModelAlert_StillOutranksAHighCatalogueAlert()
    {
        var facts = new AttentionPoints.Facts
        {
            Alerts          = [new("critical", "DEUDA_CORTO_PLAZO", "Concentración de deuda")],
            CatalogueAlerts = [Cat("high", "PASIVO_100_CORRIENTE", "Pasivo íntegramente corriente")],
        };

        Assert.Equal(["Concentración de deuda", "Pasivo íntegramente corriente"], AttentionPoints.Select(facts));
    }

    [Fact]
    public void CatalogueAlerts_RankLikeModelAlerts_MediumAndLowAreNotCandidates()
    {
        var facts = new AttentionPoints.Facts
        {
            ReliabilityLevel   = ScoreReliabilityLevels.Provisional,
            ReliabilityReasons = [new(ScoreReliabilitySeverities.Provisional, "Motivo provisorio.")],
            CatalogueAlerts    =
            [
                Cat("medium", "INVENTARIO_ELEVADO", "Participación elevada de bienes de cambio"),
                Cat("high", "FLUJO_NO_CONCILIA", "El estado de flujo de efectivo no concilia"),
                Cat("low", "FLUJO_FINANCIACION_CERO", "Actividades de financiación en cero"),
                Cat("critical", "PASIVO_100_CORRIENTE", "Pasivo íntegramente corriente"),
            ],
        };

        Assert.Equal(
            ["Pasivo íntegramente corriente", "Motivo provisorio.", "El estado de flujo de efectivo no concilia"],
            AttentionPoints.Select(facts));
    }

    [Fact]
    public void ACatalogueTitle_IsPrintedAsStored_NotTheCuratedOrTenantName()
    {
        var facts = new AttentionPoints.Facts
        {
            CatalogueAlerts   = [Cat("high", "ENDEUDAMIENTO_ELEVADO", "Endeudamiento sobre el umbral de la SGR")],
            AlertDisplayNames = new Dictionary<string, string> { ["ENDEUDAMIENTO_ELEVADO"] = "Otro nombre" },
        };

        Assert.Equal(["Endeudamiento sobre el umbral de la SGR"], AttentionPoints.Select(facts));
    }

    [Fact]
    public void ACatalogueAlert_WithABlankTitle_FallsBackToItsCode()
    {
        var facts = new AttentionPoints.Facts { CatalogueAlerts = [Cat("high", "FLUJO_NO_CONCILIA", "  ")] };

        Assert.Equal(["Flujo no concilia"], AttentionPoints.Select(facts));
    }

    // ── Catalogue vs reliability reason: COBERTURA_INTERESES_INDETERMINABLE ─────

    [Theory]
    [InlineData("ratios_not_meaningful")]
    [InlineData("ratios_low_coverage")]
    [InlineData("ratios_not_measured")]
    public void Pair_CoberturaCatalogueAlert_VsAReasonNamingInterestCoverage_KeepsTheReason_AtTheAlertsRank(string reasonCode)
    {
        var facts = new AttentionPoints.Facts
        {
            ReliabilityLevel   = ScoreReliabilityLevels.WithCaveats,
            ReliabilityReasons = [CoverageReason(reasonCode, "roe")],
            Alerts             = [new("high", "DEUDA_CORTO_PLAZO", "Concentración de deuda")],
            CatalogueAlerts    = [Cat("high", "COBERTURA_INTERESES_INDETERMINABLE", "Cobertura de intereses indeterminable")],
        };

        // The reason (a caveat, rank 30) takes the alert's 80 and comes before the model alert of the same rank.
        Assert.Equal([CoverageMessage, "Concentración de deuda"], AttentionPoints.Select(facts));
    }

    [Fact]
    public void Pair_CoberturaCatalogueAlert_VsAReasonOnOtherRatios_KeepsBoth()
    {
        var facts = new AttentionPoints.Facts
        {
            ReliabilityLevel   = ScoreReliabilityLevels.WithCaveats,
            ReliabilityReasons = [new(ScoreReliabilitySeverities.Caveat, "No se pudo calcular ROE.", ScoreReliabilityCodes.RatiosNotMeaningful, ["roe"])],
            CatalogueAlerts    = [Cat("high", "COBERTURA_INTERESES_INDETERMINABLE", "Cobertura de intereses indeterminable")],
        };

        Assert.Equal(["Cobertura de intereses indeterminable", "No se pudo calcular ROE."], AttentionPoints.Select(facts));
    }

    [Fact]
    public void Pair_CoberturaCatalogueAlert_WithACompleteVerdict_IsPrinted()
    {
        var facts = new AttentionPoints.Facts
        {
            ReliabilityLevel   = ScoreReliabilityLevels.Complete,
            ReliabilityReasons = [CoverageReason(ScoreReliabilityCodes.RatiosNotMeaningful)],
            CatalogueAlerts    = [Cat("high", "COBERTURA_INTERESES_INDETERMINABLE", "Cobertura de intereses indeterminable")],
        };

        Assert.Equal(["Cobertura de intereses indeterminable"], AttentionPoints.Select(facts));
    }

    [Fact]
    public void Pair_CoberturaCatalogueAlert_VsAnUnrelatedReasonCode_KeepsBoth()
    {
        // integrity_failed carries no ratio keys about interest coverage: not the same finding.
        var facts = new AttentionPoints.Facts
        {
            ReliabilityLevel   = ScoreReliabilityLevels.Provisional,
            ReliabilityReasons = [new(ScoreReliabilitySeverities.Provisional, "La extracción no superó 1 control.", ScoreReliabilityCodes.IntegrityFailed, ["cobertura_intereses"])],
            CatalogueAlerts    = [Cat("high", "COBERTURA_INTERESES_INDETERMINABLE", "Cobertura de intereses indeterminable")],
        };

        Assert.Equal(["La extracción no superó 1 control.", "Cobertura de intereses indeterminable"], AttentionPoints.Select(facts));
    }

    // ── Catalogue vs the critical floor: ENDEUDAMIENTO_ELEVADO ────────────────────

    [Fact]
    public void Pair_EndeudamientoCatalogueAlert_VsAFloorOnEndeudamiento_KeepsTheFloorLine()
    {
        AttentionPoints.Facts facts = WithFact(AttentionFact.CriticalFloor) with
        {
            CatalogueAlerts = [Cat("critical", "ENDEUDAMIENTO_ELEVADO", "Endeudamiento elevado")],
            ReliabilityLevel   = ScoreReliabilityLevels.Provisional,
            ReliabilityReasons = [new(ScoreReliabilitySeverities.Provisional, "Motivo provisorio.")],
        };

        // The floor (90) takes the critical alert's 100, above the provisional reason.
        Assert.Equal([FloorText, "Motivo provisorio."], AttentionPoints.Select(facts));
    }

    [Fact]
    public void Pair_EndeudamientoCatalogueAlert_VsAFloorOnOtherRatios_KeepsBoth()
    {
        var facts = new AttentionPoints.Facts
        {
            FloorAppliedTo      = 350m,
            CriticalRatioLabels = ["Liquidez corriente"],
            CriticalRatioKeys   = ["liquidez_corriente"],
            CatalogueAlerts     = [Cat("high", "ENDEUDAMIENTO_ELEVADO", "Endeudamiento elevado")],
        };

        Assert.Equal(["Piso por ratio crítico: el score se llevó a 350 (Liquidez corriente).", "Endeudamiento elevado"], AttentionPoints.Select(facts));
    }

    [Fact]
    public void Pair_EndeudamientoCatalogueAlert_VsAnUnverifiedFloorReason_KeepsTheReason()
    {
        const string Unverified = "El score está fijado por el piso crítico de Endeudamiento, calculado con cifras del balance que no cierran.";
        var facts = new AttentionPoints.Facts
        {
            ReliabilityLevel   = ScoreReliabilityLevels.Provisional,
            ReliabilityReasons = [new(ScoreReliabilitySeverities.Provisional, Unverified, ScoreReliabilityCodes.CriticalFloorUnverified, ["endeudamiento"])],
            CatalogueAlerts    = [Cat("high", "ENDEUDAMIENTO_ELEVADO", "Endeudamiento elevado")],
        };

        Assert.Equal([Unverified], AttentionPoints.Select(facts));
    }

    [Fact]
    public void Pair_EndeudamientoCatalogueAlert_WithBothFloorAndUnverifiedReason_TheFloorLineAbsorbsIt()
    {
        const string Unverified = "El score está fijado por el piso crítico de Endeudamiento.";
        AttentionPoints.Facts facts = WithFact(AttentionFact.CriticalFloor) with
        {
            ReliabilityLevel   = ScoreReliabilityLevels.Provisional,
            ReliabilityReasons = [new(ScoreReliabilitySeverities.Provisional, Unverified, ScoreReliabilityCodes.CriticalFloorUnverified, ["endeudamiento"])],
            CatalogueAlerts    = [Cat("critical", "ENDEUDAMIENTO_ELEVADO", "Endeudamiento elevado")],
        };

        Assert.Equal([FloorText, Unverified], AttentionPoints.Select(facts));
    }

    // ── Catalogue vs model: the same finding keeps the catalogue alert ────────────

    public static TheoryData<string, string> EquivalentPairs()
    {
        var data = new TheoryData<string, string>();
        foreach ((string catalogueCode, IReadOnlyList<string> modelCodes) in AttentionOverlaps.ModelEquivalents)
        {
            foreach (string modelCode in modelCodes)
                data.Add(catalogueCode, modelCode);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(EquivalentPairs))]
    public void AModelAlert_StatingACatalogueFinding_GivesWayToIt_AtTheHigherRank(string catalogueCode, string modelCode)
    {
        var facts = new AttentionPoints.Facts
        {
            ReliabilityLevel   = ScoreReliabilityLevels.Provisional,
            ReliabilityReasons = [new(ScoreReliabilitySeverities.Provisional, "Motivo provisorio.")],
            Alerts             = [new("critical", modelCode.ToLowerInvariant(), "Título del modelo")],
            CatalogueAlerts    = [Cat("medium", catalogueCode, "Título del catálogo")],
        };

        // The catalogue alert (medium: no candidate on its own) prints at the model alert's critical rank.
        Assert.Equal(["Título del catálogo", "Motivo provisorio."], AttentionPoints.Select(facts));
    }

    [Fact]
    public void AModelAlert_StatingACatalogueFinding_IsKept_WhenTheCatalogueAlertIsAbsent()
    {
        var facts = new AttentionPoints.Facts { Alerts = [new("high", "INVENTARIO_ALTO", "Inventario alto")] };

        Assert.Equal(["Inventario alto"], AttentionPoints.Select(facts));
    }

    [Fact]
    public void AModelAlert_ForADifferentCatalogueFinding_IsKept()
    {
        var facts = new AttentionPoints.Facts
        {
            Alerts          = [new("high", "INVENTARIO_ALTO", "Inventario alto")],
            CatalogueAlerts = [Cat("high", "PASIVO_100_CORRIENTE", "Pasivo íntegramente corriente")],
        };

        Assert.Equal(["Pasivo íntegramente corriente", "Inventario alto"], AttentionPoints.Select(facts));
    }

    [Fact]
    public void ModelAndCatalogue_SameFinding_ThatAlsoRepeatsAFact_LeaveOnlyTheFact_AtTheHighestRank()
    {
        var facts = new AttentionPoints.Facts
        {
            ReliabilityLevel   = ScoreReliabilityLevels.WithCaveats,
            ReliabilityReasons = [CoverageReason(ScoreReliabilityCodes.RatiosNotMeaningful)],
            Alerts             = [new("critical", "COBERTURA_INTERESES_NO_DETERMINABLE", "Cobertura no determinable")],
            CatalogueAlerts    = [Cat("medium", "COBERTURA_INTERESES_INDETERMINABLE", "Cobertura de intereses indeterminable")],
        };

        Assert.Equal([CoverageMessage], AttentionPoints.Select(facts));
    }

    [Fact]
    public void AModelSpelling_OfACatalogueFinding_RepeatsTheSameFact_EvenWithoutTheCatalogueAlert()
    {
        AttentionPoints.Facts facts = WithFact(AttentionFact.CriticalFloor, new AttentionPoints.Alert("high", "ALTO_ENDEUDAMIENTO", "Alto endeudamiento"));

        Assert.Equal([FloorText], AttentionPoints.Select(facts));
    }

    [Fact]
    public void ModelEquivalents_CoverEveryCatalogueCode_AndListEachModelCodeOnce()
    {
        Assert.Equal(
            AlertRuleEngine.ImplementedCodes.OrderBy(c => c, StringComparer.Ordinal),
            AttentionOverlaps.ModelEquivalents.Keys.OrderBy(c => c, StringComparer.Ordinal));

        string[] all = AttentionOverlaps.ModelEquivalents.Values.SelectMany(codes => codes).ToArray();
        Assert.Equal(all.Length, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // A fact code is never also a mere model spelling of another catalogue finding.
        foreach ((string catalogueCode, IReadOnlyList<string> modelCodes) in AttentionOverlaps.ModelEquivalents)
        {
            foreach (string modelCode in modelCodes)
            {
                if (AttentionOverlaps.FactOf(modelCode) is { } fact)
                    Assert.Equal(AttentionOverlaps.FactOf(catalogueCode), fact);
            }
        }
    }

    [Theory]
    [InlineData("PASIVO_100_CORRIENTE")]
    [InlineData("INVENTARIO_ELEVADO")]
    [InlineData("FLUJO_NO_CONCILIA")]
    [InlineData("FLUJO_FINANCIACION_CERO")]
    public void TheOtherCatalogueCodes_RepeatNoFact(string code)
    {
        Assert.Null(AttentionOverlaps.FactOf(code));
    }

    // ── QA Q47: the debt finding printed twice on page 1, and catalogue figures ─────

    [Fact]
    public void TheDebtFinding_UnderTheModelsLongSpelling_PrintsOnce_AsTheCatalogueAlert()
    {
        var facts = new AttentionPoints.Facts
        {
            Alerts          = [new("high", "ENDEUDAMIENTO_ELEVADO_SOBRE_ACTIVO_TOTAL", "Endeudamiento elevado sobre activo total")],
            CatalogueAlerts = [new("high", "ENDEUDAMIENTO_ELEVADO", "Endeudamiento elevado", "84,4 % (umbral 50,0 %)")],
        };

        Assert.Equal(["Endeudamiento elevado — 84,4 % (umbral 50,0 %)"], AttentionPoints.Select(facts));
    }

    // ── Q47 again: the model codes a 90-day production sweep of RiskAlerts found ─────

    [Fact]
    public void TheDebtFinding_AsProductionWritesIt_PrintsOnce_AsTheCatalogueAlertWithItsFigure()
    {
        // The real page 1: the catalogue alert (proportion 0,844 vs 0,50) beside the model's DEBT_RATIO_HIGH.
        var catalogueItem = new CatalogueAlertChannel.Item
        {
            Level          = "high",
            Code           = "ENDEUDAMIENTO_ELEVADO",
            Title          = "Endeudamiento elevado",
            Description    = "El pasivo supera el umbral sobre el activo total.",
            MetricKey      = "debt_ratio",
            MetricValue    = 0.844,
            ThresholdValue = 0.50,
            MetricUnit     = "proportion",
        };

        var facts = new AttentionPoints.Facts
        {
            Alerts          = [new("high", "DEBT_RATIO_HIGH", "Endeudamiento elevado sobre activo total")],
            CatalogueAlerts =
            [
                new(catalogueItem.Level, catalogueItem.Code, catalogueItem.Title, CatalogueMetricFormat.Inline(catalogueItem)),
            ],
        };

        AttentionPoints.Selection selection = AttentionPoints.Evaluate(facts);

        Assert.Equal(["Endeudamiento elevado — 84,4 % (umbral 50,0 %)"], selection.Bullets);
        Assert.Equal([AttentionPoints.BulletSource.Catalogue], selection.Items.Select(item => item.Source));
        Assert.Null(AttentionPoints.UnlistedOverlap(selection, printed: 5));
    }

    [Theory]
    [InlineData("ENDEUDAMIENTO_ELEVADO", "DEBT_RATIO_HIGH")]
    [InlineData("ENDEUDAMIENTO_ELEVADO", "HIGH_DEBT_RATIO")]
    [InlineData("COBERTURA_INTERESES_INDETERMINABLE", "COBERTURA_INTERESES_NO_DETERMINABLE")]
    [InlineData("COBERTURA_INTERESES_INDETERMINABLE", "COBERTURA_INTERESES_ND")]
    [InlineData("COBERTURA_INTERESES_INDETERMINABLE", "ALT_COBERTURA_INTERESES_INDETERMINABLE")]
    [InlineData("COBERTURA_INTERESES_INDETERMINABLE", "INTEREST_COVERAGE_UNDETERMINABLE")]
    [InlineData("COBERTURA_INTERESES_INDETERMINABLE", "INTEREST_COVERAGE_INDETERMINABLE")]
    [InlineData("COBERTURA_INTERESES_INDETERMINABLE", "INTEREST_COVERAGE_UNDETERMINED")]
    [InlineData("COBERTURA_INTERESES_INDETERMINABLE", "INTEREST_COVERAGE_ND")]
    [InlineData("COBERTURA_INTERESES_INDETERMINABLE", "GASTOS_FINANCIEROS_NO_DETERMINABLES")]
    [InlineData("COBERTURA_INTERESES_INDETERMINABLE", "FINANCIAL_EXPENSES_NOT_DETERMINABLE")]
    [InlineData("COBERTURA_INTERESES_INDETERMINABLE", "FINANCIAL_EXPENSES_UNDETERMINED")]
    [InlineData("COBERTURA_INTERESES_INDETERMINABLE", "INTERESES_NO_AISLABLES")]
    [InlineData("INVENTARIO_ELEVADO", "INVENTARIO_ELEVADO")]
    [InlineData("INVENTARIO_ELEVADO", "BIENES_CAMBIO_ELEVADOS")]
    [InlineData("INVENTARIO_ELEVADO", "HIGH_INVENTORY")]
    [InlineData("INVENTARIO_ELEVADO", "STOCK_ELEVADO")]
    [InlineData("INVENTARIO_ELEVADO", "INVENTORY_CONCENTRATION")]
    [InlineData("INVENTARIO_ELEVADO", "INV_CONCENTRATION")]
    [InlineData("INVENTARIO_ELEVADO", "BIENES_CAMBIO_CONCENTRACION")]
    [InlineData("INVENTARIO_ELEVADO", "CONCENTRACION_BIENES_CAMBIO")]
    [InlineData("INVENTARIO_ELEVADO", "BIENES_CAMBIO_CONCENTRADOS")]
    [InlineData("INVENTARIO_ELEVADO", "INVENTARIO_CONCENTRADO")]
    [InlineData("INVENTARIO_ELEVADO", "INVENTARIO_DOMINANTE")]
    [InlineData("INVENTARIO_ELEVADO", "BIENES_CAMBIO_DOMINANTES")]
    [InlineData("INVENTARIO_ELEVADO", "BIENES_CAMBIO_DOMINAN_AC")]
    [InlineData("INVENTARIO_ELEVADO", "INVENTORY_DOMINATES_CURRENT")]
    [InlineData("INVENTARIO_ELEVADO", "INVENTORY_DOMINATES_CURRENT_ASSETS")]
    [InlineData("INVENTARIO_ELEVADO", "INVENTORY_EXCEEDS_CURRENT_ASSETS")]
    [InlineData("PASIVO_100_CORRIENTE", "SIN_PASIVO_NO_CORRIENTE")]
    [InlineData("PASIVO_100_CORRIENTE", "PASIVO_NO_CORRIENTE_NULO")]
    [InlineData("PASIVO_100_CORRIENTE", "NO_NCLIAB")]
    [InlineData("PASIVO_100_CORRIENTE", "PASIVO_CORRIENTE_CONCENTRADO")]
    [InlineData("PASIVO_100_CORRIENTE", "PASIVO_CONCENTRADO_CORRIENTE")]
    [InlineData("PASIVO_100_CORRIENTE", "CONCENTRACION_PASIVO_CORRIENTE")]
    [InlineData("PASIVO_100_CORRIENTE", "DEBT_CONCENTRATION_SHORT_TERM")]
    public void ProductionSweepCodes_AreEquivalentToTheirCatalogueCode(string catalogueCode, string modelCode)
    {
        Assert.True(AttentionOverlaps.SameFinding(catalogueCode, modelCode), $"{modelCode} should state {catalogueCode}.");
        Assert.True(AttentionOverlaps.SameFinding(catalogueCode, $"  {modelCode.ToLowerInvariant()} "));
    }

    [Theory]
    [InlineData("LEVERAGE_HIGH")]
    [InlineData("HIGH_LEVERAGE")]
    [InlineData("APALANCAMIENTO_ELEVADO")]
    [InlineData("CAIDA_INGRESOS")]
    [InlineData("REVENUE_DROP")]
    public void LeverageAndBalanceRevenueDrops_AreDeliberatelyNotListed(string modelCode)
    {
        Assert.False(AttentionOverlaps.IsListed(modelCode));
        Assert.Null(AttentionOverlaps.FactOf(modelCode));
    }

    [Fact]
    public void ACatalogueBullet_WithoutAFigure_IsItsTitleAlone()
    {
        var facts = new AttentionPoints.Facts { CatalogueAlerts = [Cat("high", "INVENTARIO_ELEVADO", "Inventario elevado")] };

        Assert.Equal(["Inventario elevado"], AttentionPoints.Select(facts));
    }

    [Fact]
    public void AModelBullet_NeverCarriesAFigure()
    {
        var facts = new AttentionPoints.Facts { Alerts = [new("high", "DEUDA_CORTO_PLAZO", "Deuda de corto plazo", "99 %")] };

        Assert.Equal(["Deuda de corto plazo"], AttentionPoints.Select(facts));
    }

    [Fact]
    public void Evaluate_RecordsEachBulletsSource()
    {
        var facts = new AttentionPoints.Facts
        {
            BcraSituacion   = 3,
            Alerts          = [new("high", "DEUDA_CORTO_PLAZO", "Deuda de corto plazo")],
            CatalogueAlerts = [Cat("high", "PASIVO_100_CORRIENTE", "Pasivo íntegramente corriente")],
        };

        AttentionPoints.Selection selection = AttentionPoints.Evaluate(facts);

        Assert.Equal(
            [
                (AttentionPoints.BulletSource.Fact, (string?)null),
                (AttentionPoints.BulletSource.Catalogue, "PASIVO_100_CORRIENTE"),
                (AttentionPoints.BulletSource.Model, "DEUDA_CORTO_PLAZO"),
            ],
            selection.Items.Select(item => (item.Source, item.Code)));
        Assert.Equal(AttentionPoints.Select(facts), selection.Bullets);
    }

    [Fact]
    public void UnlistedOverlap_NamesTheCodes_WhenACatalogueAlertSitsBesideAnUnlistedModelAlert()
    {
        var facts = new AttentionPoints.Facts
        {
            Alerts          = [new("high", "DEUDA_CORTO_PLAZO", "Deuda de corto plazo"), new("high", "INVENTARIO_ALTO", "Inventario alto")],
            CatalogueAlerts = [Cat("high", "PASIVO_100_CORRIENTE", "Pasivo íntegramente corriente")],
        };

        AttentionPoints.UnlistedOverlapCodes? codes = AttentionPoints.UnlistedOverlap(AttentionPoints.Evaluate(facts), printed: 5);

        Assert.NotNull(codes);
        Assert.Equal(["PASIVO_100_CORRIENTE"], codes.CatalogueCodes);
        Assert.Equal(["DEUDA_CORTO_PLAZO"], codes.ModelCodes);   // INVENTARIO_ALTO is a listed spelling
    }

    [Fact]
    public void UnlistedOverlap_IsNull_WithoutACatalogueAlert_OrWithOnlyListedModelCodes_OrBeyondThePrintedBullets()
    {
        var modelOnly = new AttentionPoints.Facts { Alerts = [new("high", "DEUDA_CORTO_PLAZO", "Deuda de corto plazo")] };
        Assert.Null(AttentionPoints.UnlistedOverlap(AttentionPoints.Evaluate(modelOnly), printed: 5));

        var listed = new AttentionPoints.Facts
        {
            Alerts          = [new("high", "INVENTARIO_ALTO", "Inventario alto")],
            CatalogueAlerts = [Cat("high", "PASIVO_100_CORRIENTE", "Pasivo íntegramente corriente")],
        };
        Assert.Null(AttentionPoints.UnlistedOverlap(AttentionPoints.Evaluate(listed), printed: 5));

        var trimmed = new AttentionPoints.Facts
        {
            Alerts          = [new("high", "DEUDA_CORTO_PLAZO", "Deuda de corto plazo")],
            CatalogueAlerts = [Cat("high", "PASIVO_100_CORRIENTE", "Pasivo íntegramente corriente")],
        };
        Assert.Null(AttentionPoints.UnlistedOverlap(AttentionPoints.Evaluate(trimmed), printed: 1));
    }

    [Fact]
    public void UnlistedOverlap_NeverLogsAModelCodeThatIsNotCodeShaped()
    {
        var facts = new AttentionPoints.Facts
        {
            Alerts          = [new("high", "Deuda de la empresa con su socio", "Deuda con el socio")],
            CatalogueAlerts = [Cat("high", "PASIVO_100_CORRIENTE", "Pasivo íntegramente corriente")],
        };

        AttentionPoints.UnlistedOverlapCodes? codes = AttentionPoints.UnlistedOverlap(AttentionPoints.Evaluate(facts), printed: 5);

        Assert.NotNull(codes);
        Assert.Equal([AttentionPoints.NonCode], codes.ModelCodes);
    }

    [Theory]
    [InlineData("ENDEUDAMIENTO_ELEVADO_SOBRE_ACTIVO_TOTAL", true)]
    [InlineData("endeudamiento_elevado", true)]
    [InlineData("VENTAS_EN_CAIDA", true)]
    [InlineData("DEUDA_CORTO_PLAZO", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsListed_KnowsEveryList(string? code, bool expected)
    {
        Assert.Equal(expected, AttentionOverlaps.IsListed(code));
    }
}
