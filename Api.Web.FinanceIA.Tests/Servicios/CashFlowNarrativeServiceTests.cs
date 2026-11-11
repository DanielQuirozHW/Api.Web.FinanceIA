// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-10

using System.Linq;
using System.Text.RegularExpressions;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

// ── Test class ────────────────────────────────────────────────────────────────

/// <summary>
/// Covers the deterministic es-AR cash-flow reading ("LECTURA DEL FLUJO"). The reconciliation state
/// is produced by the REAL <see cref="CashFlowReconciliationService"/> (as the controller does at read
/// time), so the R0 gate is exercised through the same arithmetic that ships. The figures are the real
/// production analyses that motivated the feature — they are the specification.
/// </summary>
public sealed class CashFlowNarrativeServiceTests
{
    private readonly CashFlowNarrativeService      _sut        = new();
    private readonly CashFlowReconciliationService _reconciler = new();

    // Reads exactly as GetById does: recompute reconciliation, then narrate.
    private IReadOnlyList<string> Read(CashFlowData cf) => _sut.Read(cf, _reconciler.Reconcile(cf));

    // The narrative may NEVER contain a causal connector — it states WHAT happened, never WHY.
    private static readonly Regex CausalConnector =
        new(@"\b(porque|para|debido\s+a|gracias\s+a)\b", RegexOptions.IgnoreCase);

    private static void AssertNoCausalConnectors(IEnumerable<string> lines)
    {
        foreach (var line in lines)
            Assert.False(CausalConnector.IsMatch(line), $"Causal connector found in: \"{line}\"");
    }

    // ── R6 — the structural pairing (MAYTOM): proportion, not restatement ────────

    [Fact]
    public void Maytom_R6_StatesTheProportion_WithoutRepeatingR1R2OrCausalWording()
    {
        // operating −971.123.240 · investing +655.017.771 (entirely asset disposals) · financing absent.
        var cf = new CashFlowData
        {
            Present    = true,
            Operating  = -971_123_240,
            Investing  = 655_017_771,
            Components  = new CashFlowComponents { AssetDisposals = 655_017_771 },
        };

        var result = Read(cf);

        // R6 adds the ONE fact R1/R2 don't carry: the proportion (655.017.771 / 971.123.240 ≈ 67%).
        Assert.Contains(result, s =>
            s.Contains("equivale al 67 %") && s.Contains("inversión") && s.Contains("consumió la operación"));

        // R1 states the operating direction; investing is fully explained by asset disposals.
        Assert.Contains(result, s => s.Contains("consumió") && s.Contains("$ 971.123.240"));
        Assert.Contains(result, s => s.Contains("íntegramente") && s.Contains("ventas de bienes de uso"));

        // R6 must NOT restate R1/R2 — each magnitude appears exactly once across the whole reading.
        Assert.Equal(1, result.Count(s => s.Contains("$ 971.123.240")));
        Assert.Equal(1, result.Count(s => s.Contains("$ 655.017.771")));

        AssertNoCausalConnectors(result);
    }

    // ── R3/R4 — RAYEN: the investing residual must NOT be characterised by term ───

    [Fact]
    public void Rayen_InvestingResidual_IsNotCharacterisedByTerm()
    {
        // investing −332.379.214 = capex −51.295.121 + a −281.084.094 residual (which was an FCI, a
        // CURRENT asset — so calling it long-term / non-current would be a false claim).
        var cf = new CashFlowData
        {
            Present    = true,
            Operating  = 664_079_969,
            Investing  = -332_379_214,
            Components  = new CashFlowComponents { Capex = -51_295_121 },
        };

        var result = Read(cf);

        // Capex is named (bienes de uso).
        Assert.Contains(result, s => s.Contains("$ 51.295.121") && s.Contains("inversión en bienes de uso"));

        // The residual is reported, but NOT itemised and NOT termed. (−332.379.214 − (−51.295.121) =
        // −281.084.093 from whole-peso inputs; the real analysis's −281.084.094 differs by the cents
        // that rounded — immaterial here, since the point is that the residual is not characterised.)
        var residualLine = Assert.Single(result, s => s.Contains("corresponde a conceptos que esta lectura no identifica por separado"));
        Assert.Contains("$ 281.084.09", residualLine);
        Assert.DoesNotContain("corriente", residualLine);   // neither "corriente" nor "no corriente"
        Assert.DoesNotContain("plazo", residualLine);

        AssertNoCausalConnectors(result);
    }

    // ── R2 — a single component equal to the subtotal → "íntegramente" ───────────

    [Fact]
    public void Investing_SingleComponentEqualsSubtotal_NamesItIntegramente()
    {
        // A single capex line that equals the subtotal within a peso.
        var cf = new CashFlowData
        {
            Present    = true,
            Operating  = 558_367_419,
            Investing  = -756_406_949,
            Components  = new CashFlowComponents { Capex = -756_406_949 },
        };

        var result = Read(cf);

        Assert.Contains(result, s =>
            s.Contains("íntegramente") &&
            s.Contains("inversión en bienes de uso") &&
            s.Contains("$ 756.406.949"));

        // No unexplained remainder when the component equals the subtotal.
        Assert.DoesNotContain(result, s => s.Contains("corresponde a conceptos que esta lectura no identifica por separado"));

        AssertNoCausalConnectors(result);
    }

    // ── SOUTHERN — all three present, reconciles, no components → direction only ──

    [Fact]
    public void Southern_AllSectionsPresentNoComponents_StatesDirectionOnly()
    {
        var cf = new CashFlowData
        {
            Present       = true,
            Operating     = 100_000_000,
            Investing     = -40_000_000,
            Financing     = -10_000_000,
            NetChange     = 50_000_000,   // 100 − 40 − 10 = 50 ✓
            CashBeginning = 200_000_000,
            CashEnd       = 250_000_000,  // 200 + 50 = 250 ✓
        };

        var result = Read(cf);

        // Reconciles → no caution sentence; exactly one sentence per section.
        Assert.Equal(3, result.Count);
        Assert.Contains(result, s => s.Contains("generó") && s.Contains("$ 100.000.000"));
        Assert.Contains(result, s => s.Contains("inversión") && s.Contains("$ 40.000.000"));
        Assert.Contains(result, s => s.Contains("financiación") && s.Contains("$ 10.000.000"));

        // Direction only — nothing invented about components or term.
        Assert.DoesNotContain(result, s => s.Contains("íntegramente"));
        Assert.DoesNotContain(result, s => s.Contains("bienes de uso"));
        Assert.DoesNotContain(result, s => s.Contains("deuda"));
        Assert.DoesNotContain(result, s => s.Contains("dividendos"));
        Assert.DoesNotContain(result, s => s.Contains("aportes"));

        AssertNoCausalConnectors(result);
    }

    // ── R0 — EL NOGAL: a failed check returns the caution sentence ALONE ──────────

    [Fact]
    public void ElNogal_SectionsVsNetChangeFailed_ReturnsCautionSentenceAlone()
    {
        var cf = new CashFlowData
        {
            Present   = true,
            Operating = 100_000_000, Investing = 50_000_000, Financing = 20_000_000,  // sum = 170M
            NetChange = 1_000_000_000,                                                // reported 1.000M → fails
        };

        // Precondition: the real reconciler must actually flag this check.
        Assert.Equal(ReconciliationState.Failed, _reconciler.Reconcile(cf).SectionsVsNetChange.State);

        var result = Read(cf);

        var only = Assert.Single(result);
        Assert.Contains("no reconcilia", only);
        // Nothing else is narrated — no figures leak through.
        Assert.DoesNotContain("$ 100.000.000", only);
        Assert.DoesNotContain("$ 1.000.000.000", only);
    }

    // ── R5 — financing components carry their term; nothing is inferred ───────────

    [Fact]
    public void Financing_DebtAndDividends_AreNamedWithTerm()
    {
        var cf = new CashFlowData
        {
            Present   = true,
            Financing = 150_000_000,
            Components = new CashFlowComponents
            {
                LongTermDebtRaised = 200_000_000,
                DividendsPaid      = -50_000_000,   // 200 − 50 = 150 ✓ no residual
            },
        };

        var result = Read(cf);

        Assert.Contains(result, s =>
            s.Contains("$ 200.000.000") && s.Contains("toma de deuda de largo plazo"));
        Assert.Contains(result, s =>
            s.Contains("$ 50.000.000") && s.Contains("pago de dividendos"));
        Assert.DoesNotContain(result, s => s.Contains("corresponde a conceptos que esta lectura no identifica por separado"));

        AssertNoCausalConnectors(result);
    }

    // ── R5 — a financing subtotal with NO component is stated, never inferred ─────

    [Fact]
    public void Financing_SubtotalWithoutComponents_StatesItAndSaysNotBrokenDown()
    {
        // MILLAGRO's financing section was 100% RECPAM (an inflation adjustment, not borrowing): a
        // subtotal with no debt/dividend/capital component. We must never call it debt.
        var cf = new CashFlowData
        {
            Present   = true,
            Financing = 300_000_000,
        };

        var result = Read(cf);

        var line = Assert.Single(result, s => s.Contains("financiación"));
        Assert.Contains("esta lectura no identifica ninguno de sus conceptos", line);
        Assert.DoesNotContain("deuda", line);
        Assert.DoesNotContain("aportes", line);

        AssertNoCausalConnectors(result);
    }

    // ── R7 — inflation: net_change = 0 with a non-null monetary result (no div/0) ─

    [Fact]
    public void Inflation_NetChangeZeroWithMonetaryResult_DoesNotDivideByZero()
    {
        var cf = new CashFlowData
        {
            Present              = true,
            NetChange            = 0,
            MonetaryResultOnCash = -50_000_000,
        };

        var result = Read(cf);  // must not throw

        // Real movement excluding inflation = 0 − (−50M) = +50M.
        Assert.Contains(result, s => s.Contains("Excluyendo") && s.Contains("$ 50.000.000"));
        // The "≥ 50% of net_change" dominance sentence is guarded off when net_change is zero.
        Assert.DoesNotContain(result, s => s.Contains("mayor parte de la variación"));

        AssertNoCausalConnectors(result);
    }

    // ── R7 — inflation dominates the variation ───────────────────────────────────

    [Fact]
    public void Inflation_MonetaryResultIsMostOfTheVariation_FlagsIt()
    {
        var cf = new CashFlowData
        {
            Present              = true,
            NetChange            = 100_000_000,
            MonetaryResultOnCash = 80_000_000,   // 80% of the variation
        };

        var result = Read(cf);

        // Movement excluding inflation = 100M − 80M = 20M.
        Assert.Contains(result, s => s.Contains("Excluyendo") && s.Contains("$ 20.000.000"));
        Assert.Contains(result, s => s.Contains("mayor parte de la variación"));

        AssertNoCausalConnectors(result);
    }

    // ── Gates — not present / all null → empty ───────────────────────────────────

    [Fact]
    public void NotPresent_ReturnsEmpty()
    {
        var cf = new CashFlowData { Present = false, Operating = -100_000_000 };
        Assert.Empty(Read(cf));
    }

    [Fact]
    public void PresentButEveryFigureNull_ReturnsEmpty()
    {
        var cf = new CashFlowData { Present = true };
        Assert.Empty(Read(cf));
    }

    // ── Fix (1) — a component with NO reported subtotal is still narrated ─────────

    [Fact]
    public void Agroquimicos_FinancingSubtotalNullWithDividend_NarratesDividendNoSubtotal()
    {
        // AGROQUIMICOS DEL NORTE: financing subtotal absent, but dividends_paid −155.337.000 — the
        // largest outflow after operating. It must be narrated; no subtotal may be stated or implied.
        var cf = new CashFlowData
        {
            Present    = true,
            Components  = new CashFlowComponents { DividendsPaid = -155_337_000 },
        };

        var result = Read(cf);

        var line = Assert.Single(result);
        Assert.Contains("pago de dividendos", line);
        Assert.Contains("-$ 155.337.000", line);          // signed outflow
        Assert.Contains("no informa el subtotal", line);   // subtotal explicitly not reported
        // No subtotal invented, inferred or summed.
        Assert.DoesNotContain("resultado neto", line);
        Assert.DoesNotContain("aportó", line);
        Assert.DoesNotContain("se aplicaron", line);

        AssertNoCausalConnectors(result);
    }

    // ── Fix (2) — itemisation carries the sign (opposite directions look different) ─

    [Fact]
    public void Ripeho_ItemisesCapexNegativeAndDisposalsPositive_Signed()
    {
        // RIPEHO: investing −756.406.949 = capex −758.810.665 + asset disposals +2.403.716.
        var cf = new CashFlowData
        {
            Present    = true,
            Operating  = 558_367_419,
            Investing  = -756_406_949,
            Components  = new CashFlowComponents { Capex = -758_810_665, AssetDisposals = 2_403_716 },
        };

        var result = Read(cf);

        var detail = Assert.Single(result, s => s.Contains("El detalle informado incluye"));
        Assert.Contains("-$ 758.810.665 en concepto de inversión en bienes de uso", detail);  // outflow, negative
        Assert.Contains("$ 2.403.716 en concepto de ventas de bienes de uso", detail);         // inflow, positive
        Assert.DoesNotContain("-$ 2.403.716", detail);   // the inflow must NOT read as negative

        AssertNoCausalConnectors(result);
    }

    // ── Fix (4) — a fully-zero section emits nothing ─────────────────────────────

    [Fact]
    public void Tekun_FinancingZeroWithZeroDividends_EmitsNoFinancingSentence()
    {
        // TEKUN: financing = 0 and dividends_paid = 0 — a zero cannot be "íntegramente" anything.
        var cf = new CashFlowData
        {
            Present    = true,
            Financing  = 0,
            Components  = new CashFlowComponents { DividendsPaid = 0 },
        };

        var result = Read(cf);

        Assert.Empty(result);
        Assert.DoesNotContain(result, s => s.Contains("financiación"));
    }

    // ── Fix (4) — a zero subtotal with non-zero opposing components is real info ──

    [Fact]
    public void Section_NetsToZeroFromOpposingComponents_IsStated()
    {
        // financing = 0, but 100M raised and 100M repaid underneath — offsetting movements.
        var cf = new CashFlowData
        {
            Present    = true,
            Financing  = 0,
            Components  = new CashFlowComponents
            {
                LongTermDebtRaised = 100_000_000,
                LongTermDebtRepaid = -100_000_000,
            },
        };

        var result = Read(cf);

        var line = Assert.Single(result);
        Assert.Contains("nulo", line);   // the section nets to zero
        Assert.Contains("informa $ 100.000.000 en concepto de toma de deuda de largo plazo", line);
        Assert.Contains("-$ 100.000.000 en concepto de cancelación de deuda de largo plazo", line);

        AssertNoCausalConnectors(result);
    }

    // ── Financial placements — CETA CEREALES: capex + colocaciones sum to the subtotal ──

    [Fact]
    public void Ceta_InvestingCapexPlusFinancialPlacements_ItemisedNoResidual()
    {
        // CETA CEREALES: investing −1.120.956.597 = capex −468.216.265 + a financial-placement line
        // ("Inversión en colocaciones financieras") −652.740.332. Both itemised; residual rounds to 0.
        var cf = new CashFlowData
        {
            Present    = true,
            Investing  = -1_120_956_597,
            Components  = new CashFlowComponents { Capex = -468_216_265, FinancialPlacements = -652_740_332 },
        };

        var result = Read(cf);

        var detail = Assert.Single(result, s => s.Contains("El detalle informado incluye"));
        Assert.Contains("-$ 468.216.265 en concepto de inversión en bienes de uso", detail);
        Assert.Contains("-$ 652.740.332 en concepto de colocaciones financieras", detail);
        // Sum equals the subtotal within a peso → no residual sentence.
        Assert.DoesNotContain(result, s => s.Contains("corresponde a conceptos que esta lectura no identifica por separado"));

        AssertNoCausalConnectors(result);
    }

    // ── Financial placements are TERM-NEUTRAL: never any corto/largo plazo wording ──

    [Fact]
    public void Investing_FinancialPlacementAlone_CarriesNoTermWording()
    {
        var cf = new CashFlowData
        {
            Present    = true,
            Investing  = -652_740_332,
            Components  = new CashFlowComponents { FinancialPlacements = -652_740_332 },
        };

        var result = Read(cf);

        Assert.Contains(result, s => s.Contains("colocaciones financieras"));
        // A placement is not corriente / no corriente by virtue of its investing-section placement.
        Assert.DoesNotContain(result, s => s.Contains("corto plazo"));
        Assert.DoesNotContain(result, s => s.Contains("largo plazo"));

        AssertNoCausalConnectors(result);
    }

    // ── Director fees are a distinct concept, a sibling of dividends — never merged ──

    [Fact]
    public void Financing_DirectorFeesAndDividends_AreItemisedAsDistinctConcepts()
    {
        var cf = new CashFlowData
        {
            Present    = true,
            Financing  = -125_000_000,
            Components  = new CashFlowComponents { DividendsPaid = -50_000_000, DirectorFeesPaid = -75_000_000 },
        };

        var result = Read(cf);

        var detail = Assert.Single(result, s => s.Contains("El detalle informado incluye"));
        Assert.Contains("-$ 50.000.000 en concepto de pago de dividendos", detail);
        Assert.Contains("-$ 75.000.000 en concepto de honorarios al directorio", detail);
        Assert.DoesNotContain(result, s => s.Contains("corresponde a conceptos que esta lectura no identifica por separado"));

        AssertNoCausalConnectors(result);
    }

    // ── CETA CEREALES financing: dividends=0 dropped, director fees stated, no residual ──

    [Fact]
    public void Ceta_FinancingZeroDividendDropped_DirectorFeesStated_NoResidual()
    {
        // financing −105.150.755 = long-term repaid −26.733.333 + short-term repaid −3.342.422 +
        // honorarios al directorio −75.075.000; dividends_paid reported 0 → dropped, not narrated.
        var cf = new CashFlowData
        {
            Present    = true,
            Financing  = -105_150_755,
            Components  = new CashFlowComponents
            {
                LongTermDebtRepaid  = -26_733_333,
                ShortTermDebtRepaid = -3_342_422,
                DividendsPaid       = 0,
                DirectorFeesPaid    = -75_075_000,
            },
        };

        var result = Read(cf);

        var detail = Assert.Single(result, s => s.Contains("El detalle informado incluye"));
        Assert.Contains("-$ 75.075.000 en concepto de honorarios al directorio", detail);
        Assert.Contains("-$ 26.733.333 en concepto de cancelación de deuda de largo plazo", detail);
        Assert.Contains("-$ 3.342.422 en concepto de cancelación de deuda de corto plazo", detail);
        // The zero dividend carries no information → dropped, never stated.
        Assert.DoesNotContain(result, s => s.Contains("pago de dividendos"));
        // Sum equals the subtotal within a peso → no residual sentence.
        Assert.DoesNotContain(result, s => s.Contains("corresponde a conceptos que esta lectura no identifica por separado"));

        AssertNoCausalConnectors(result);
    }

    // ── Regression: with all three new keys null, the reading is pinned going forward ──

    [Fact]
    public void Ripeho_AllNewKeysNull_ProducesUnchangedReading()
    {
        // The RIPEHO fixture (capex + asset disposals) with the three new keys left null pins the
        // reading going forward — new nullable components must never perturb an existing scenario.
        // (This asserts the reading, not a comparison against any pre-change baseline.)
        var cf = new CashFlowData
        {
            Present    = true,
            Operating  = 558_367_419,
            Investing  = -756_406_949,
            Components  = new CashFlowComponents { Capex = -758_810_665, AssetDisposals = 2_403_716 },
        };

        var result = Read(cf);

        var expected = new[]
        {
            "El flujo operativo fue positivo: la operación generó $ 558.367.419.",
            "En actividades de inversión se aplicaron $ 756.406.949.",
            "El detalle informado incluye -$ 758.810.665 en concepto de inversión en bienes de uso y " +
            "$ 2.403.716 en concepto de ventas de bienes de uso.",
        };
        Assert.Equal(expected, result);
    }

    // ── No-subtotal path: an identified placement is stated without inventing a subtotal ──

    [Fact]
    public void Investing_SubtotalNullWithFinancialPlacement_NarratesComponentNoSubtotal()
    {
        // A financial-placement line identified in a section whose subtotal the statement omits: the
        // component is stated; no subtotal is invented, inferred or summed. (This is the deterministic
        // side of the newly-triggered "movements without a reported subtotal" behaviour.)
        var cf = new CashFlowData
        {
            Present    = true,
            Components  = new CashFlowComponents { FinancialPlacements = -652_740_332 },
        };

        var result = Read(cf);

        var line = Assert.Single(result);
        Assert.Contains("colocaciones financieras", line);
        Assert.Contains("-$ 652.740.332", line);
        Assert.Contains("no informa el subtotal", line);
        // No subtotal invented, inferred or summed.
        Assert.DoesNotContain("resultado neto", line);
        Assert.DoesNotContain("aportó", line);
        Assert.DoesNotContain("se aplicaron", line);

        AssertNoCausalConnectors(result);
    }

    // ── Capex + placement + redemption with a surviving residual: all three itemised, in order ──

    [Fact]
    public void Investing_CapexPlacementAndRedemption_ItemisedInOrderWithResidual()
    {
        // capex −100M + colocaciones −200M + rescate +50M = −250M against a −280M subtotal → a −30M
        // residual survives rounding and is reported.
        var cf = new CashFlowData
        {
            Present    = true,
            Investing  = -280_000_000,
            Components  = new CashFlowComponents
            {
                Capex                       = -100_000_000,
                FinancialPlacements         = -200_000_000,
                FinancialPlacementsRedeemed = 50_000_000,
            },
        };

        var result = Read(cf);

        var detail = Assert.Single(result, s => s.Contains("El detalle informado incluye"));
        Assert.Contains(
            "-$ 100.000.000 en concepto de inversión en bienes de uso, " +
            "-$ 200.000.000 en concepto de colocaciones financieras y " +
            "$ 50.000.000 en concepto de rescate de colocaciones financieras",
            detail);

        var residual = Assert.Single(result, s => s.Contains("corresponde a conceptos que esta lectura no identifica por separado"));
        Assert.Contains("El resto, -$ 30.000.000", residual);

        AssertNoCausalConnectors(result);
    }

    // ── LANGELLOTTI — an itemised line with no catalogue key must NOT read as the statement's fault ──

    [Fact]
    public void Langellotti_UnmappedInterestLine_ResidualDoesNotBlameTheStatement()
    {
        // LANGELLOTTI S.R.L. (ejercicio 31/08/2025), indirect-method EFE that reconciles exactly. The
        // statement itemises financing in named lines: "Pago de préstamos" (636.181.932) and "Pago de
        // intereses" (73.775.968). "Pago de intereses" has no key in OUR component catalogue, so it
        // falls into the residual. -$ 73.775.968 IS the statement's own "Pago de intereses" line — the
        // concept is missing from our mapping, not from the document — so the residual sentence must
        // never assert the statement failed to detail it. (That false claim is the whole point here.)
        var cf = new CashFlowData
        {
            Present       = true,
            Operating     = 674_264_002,
            Investing     = -33_744_213,
            Financing     = -709_957_900,
            NetChange     = -69_438_111,   // 674.264.002 − 33.744.213 − 709.957.900 = −69.438.111 ✓
            CashBeginning = 153_073_829,
            CashEnd       = 83_635_718,    // 153.073.829 − 69.438.111 = 83.635.718 ✓
            Components     = new CashFlowComponents
            {
                Capex               = -113_667_450,
                AssetDisposals      = 79_923_237,
                ShortTermDebtRepaid = -636_181_932,   // "Pago de préstamos"; "Pago de intereses" has no key
            },
        };

        var result = Read(cf);

        // Reconciles → no R0 caution sentence.
        Assert.DoesNotContain(result, s => s.Contains("no reconcilia"));

        // The residual carries the unmapped -$ 73.775.968 with document-neutral attribution.
        var residualLine = Assert.Single(
            result, s => s.Contains("corresponde a conceptos que esta lectura no identifica por separado"));
        Assert.Contains("El resto, -$ 73.775.968", residualLine);

        // It must NOT claim the statement failed to detail it — the old blaming phrases are gone entirely.
        Assert.DoesNotContain(result, s => s.Contains("no se detalla por concepto"));
        Assert.DoesNotContain(result, s => s.Contains("no desagrega por concepto"));

        // No term wording on the residual sentence. (The financing DETAIL line legitimately reads
        // "cancelación de deuda de corto plazo"; the residual line must carry no term.)
        Assert.DoesNotContain("plazo", residualLine);
        Assert.DoesNotContain("corriente", residualLine);

        AssertNoCausalConnectors(result);
    }

    // ── PII: the reading now ships to the Claude API ──────────────────────────

    [Fact]
    public void No_input_derived_free_text_ever_reaches_the_reading()
    {
        // Method and MonetaryResultOnCashSection are the ONLY string-typed members on the whole
        // CashFlowData/CashFlowComponents graph, and neither is narratable: every label in the
        // output is a compile-time literal and every input-derived value goes through
        // Magnitude/Signed, which format a double.
        //
        // That was cosmetic while the reading only reached the Portal and the PDF. It is now
        // load-bearing: the CHAT system prompt ships this text to the Claude API, so a leak here
        // would violate the hard rule "never send PII (entity name, CUIT, or any identifying data)
        // inside a prompt to the Claude API".
        var cf = new CashFlowData
        {
            Present                     = true,
            Operating                   =  558_367_419,
            Investing                   = -120_000_000,
            Method                      = "ACME SERVICIOS SRL — CUIT 30123456789",
            MonetaryResultOnCashSection = "ACME SERVICIOS SRL",
            Components                  = new CashFlowComponents { Capex = -50_000_000 },
        };

        var result = Read(cf);
        Assert.NotEmpty(result); // the fixture must actually produce a reading, or this proves nothing

        foreach (var line in result)
        {
            Assert.DoesNotContain("ACME",        line, StringComparison.Ordinal);
            Assert.DoesNotContain("SERVICIOS",   line, StringComparison.Ordinal);
            Assert.DoesNotContain("30123456789", line, StringComparison.Ordinal);
            Assert.DoesNotContain("CUIT",        line, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── QA Q48: placements the statement printed under Financing (shared case 1) ─────

    [Fact]
    public void PlacementsPrintedInFinancing_AreNarratedUnderFinancing()
    {
        var cashFlow = new CashFlowData
        {
            Present    = true,
            Operating  = 1300,
            Investing  = -1000,
            Financing  = -300,
            Components = new CashFlowComponents { Capex = -1000, ShortTermDebtRaised = 200, FinancialPlacements = -500 },
        };

        IReadOnlyList<string> lines = new CashFlowNarrativeService().Read(cashFlow, new CashFlowReconciliationService().Reconcile(cashFlow));

        Assert.Contains("En actividades de inversión se aplicaron $ 1.000, íntegramente en concepto de inversión en bienes de uso.", lines);
        Assert.Contains("El detalle informado incluye $ 200 en concepto de toma de deuda de corto plazo y -$ 500 en concepto de colocaciones financieras.", lines);
        Assert.DoesNotContain(lines, line => line.StartsWith("El resto", StringComparison.Ordinal));
    }
}
