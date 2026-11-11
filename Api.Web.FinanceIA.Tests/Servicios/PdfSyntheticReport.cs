// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-08

using System.Globalization;
using System.Text.Json;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Lufe;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// A complete, entirely SYNTHETIC company analysis — no real names, CUITs or figures — rich enough to exercise
/// every section of the company PDF: a LUFE penalty whose rounding must not ride on it, rejected cheques, a
/// worsening BCRA situation, a CASFOG snapshot, a non-measurable interest coverage, titled risk alerts, catalogue
/// alerts (a proportion, an absolute figure and one without a metric) and a narrative that opens with a Markdown heading. Used by the golden test and by the render-to-disk helper
/// that produces the PR screenshots.
/// </summary>
internal static class PdfSyntheticReport
{
    internal const string BusinessName = "Metalúrgica Ejemplo S.A.";
    internal const string Cuit         = "30000000000";   // the placeholder CUIT: never a real client's

    internal static readonly Guid ResultId   = new("5c0c72eb-1111-4222-8333-444455556666");
    internal static readonly Guid AnalysisId = new("6d1d83fc-2222-4333-8444-555566667777");

    private static IEnumerable<string> Months(int year, int month, int count)
    {
        for (int i = 0; i < count; i++)
        {
            int idx = (year * 12) + (month - 1) + i;
            yield return string.Create(CultureInfo.InvariantCulture, $"{idx / 12:D4}{(idx % 12) + 1:D2}");
        }
    }

    // The LUFE series and CPI the synthetic score read; the variation is the engine's own figure for them.
    private static List<LufeVentaCompraMes> Serie() =>
        Months(2025, 1, 21)
            .Select((p, i) => new LufeVentaCompraMes
            {
                Periodo = p,
                Ventas  = i < 12 ? 90_000_000m + (i * 1_000_000m) : 82_000_000m + (i * 500_000m),
                Compras = 51_000_000m,
            })
            .ToList();

    private static Dictionary<string, double> Ipc() => Months(2024, 1, 34).ToDictionary(p => p, _ => 2.0d);

    private static decimal VentasVariacion() =>
        Math.Round(Api.Web.FinanceIA.Servicios.ScoringService.ComputeVentasVariacion(Serie(), Ipc())!.Value.Valor, 4);

    // The case a real export got wrong: components 950 / 900 / 900 → 475 + 315 + 135 = 925, the engine's
    // composite 7,5 → 920, LUFE 10 internal → −100, final 18 → 820. The old report printed LUFE −105.
    private static string Breakdown() => BreakdownTemplate.Replace(
        "__VENTAS_VARIACION__", VentasVariacion().ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private const string BreakdownTemplate =
        """
        {
          "breakdown": {
            "liquidez_corriente": {"score": 10, "status": "Saludable"},
            "prueba_acida":       {"score": 30, "status": "Saludable"},
            "endeudamiento":      {"score": 45, "status": "Atencion"},
            "leverage":           {"score": 20, "status": "Saludable"},
            "cobertura_intereses":{"score": 50, "status": "Atencion", "excluded": true, "exclusionReason": "not_meaningful"},
            "roe":                {"score": 5,  "status": "Saludable"},
            "roa":                {"score": 10, "status": "Saludable"},
            "margen_neto":        {"score": 12, "status": "Saludable"}
          },
          "scoreRatios": 5.0, "scoreBcra": 10.0, "scoreMacro": 10.0,
          "scoreBeforeFloor": 7.5,
          "appliedFloor": 0, "criticalRatios": [],
          "bcraExcluded": false,
          "effectiveWeightRatios": 0.50, "effectiveWeightBcra": 0.35, "effectiveWeightMacro": 0.15,
          "sectorProfile": "Construccion",
          "bcraTrendDelta": 1, "bcraTrendAdj": 3,
          "chequesAdj": 1.5, "chequesProporcionRecuperada": 0.72,
          "ventasAdj": 10, "ventasVariacion": __VENTAS_VARIACION__, "ventasBase": "interanual_3m", "ventasVariacionEsReal": true,
          "levelCutoffs": {"low": 35, "medium": 60, "high": 80}
        }
        """;

    private const string Ratios =
        """{"current_ratio":1.52,"acid_test":0.97,"debt_ratio":0.42,"leverage":0.72,"roe":12.4,"roa":7.2,"net_margin":6.9,"interest_coverage":null}""";

    private const string Balance =
        """
        {
          "assets": 980000000, "liabilities": 410000000, "equity": 570000000,
          "current_assets": 520000000, "non_current_assets": 460000000,
          "current_liabilities": 342000000, "non_current_liabilities": 68000000,
          "net_revenue": 1020000000, "net_profit": 70700000,
          "fiscal_year_detected": 2025,
          "cash_flow": {
            "present": true, "method": "indirect",
            "cash_beginning": 61000000, "cash_end": 88000000, "net_change": 27000000,
            "operating": 112000000, "investing": -58000000, "financing": -27000000,
            "components": { "capex": -61000000, "asset_disposals": 3000000, "dividends_paid": -15000000, "short_term_debt_repaid": -12000000 }
          }
        }
        """;

    private const string RiskAlerts =
        """
        [
          {"level":"high","code":"ENDEUDAMIENTO_CORTO_PLAZO","title":"Concentración de deuda en el corto plazo","description":"El 83,4 % del pasivo vence dentro de los doce meses."},
          {"level":"critical","code":"VENTAS_EN_CAIDA","description":"Las ventas declaradas cayeron en términos reales frente al año anterior."},
          {"level":"medium","code":"INFLATION_ADJUSTMENT_RT6","description":"El balance aplica el ajuste por inflación (RT 6)."},
          {"level":"low","code":"CAPEX_SOSTENIDO","title":"Inversión sostenida en bienes de uso","description":"La inversión en bienes de uso supera las amortizaciones del ejercicio."}
        ]
        """;

    // The deterministic channel. ENDEUDAMIENTO_ELEVADO measures the synthetic balance itself (410 / 980 = 0,4184);
    // COBERTURA_INTERESES_INDETERMINABLE matches its missing financial expenses. No implemented rule reports an
    // ABSOLUTE figure yet, so SALDO_FISCAL_VENCIDO is FICTIONAL — a stand-in for a future rule, there only so the
    // report shows how a plain figure prints ("1.500.000").
    private const string CatalogueAlerts =
        """
        [
          {"level":"medium","code":"COBERTURA_INTERESES_INDETERMINABLE","title":"Cobertura de intereses indeterminable","description":"El estado de resultados no expone los gastos financieros por separado, por lo que la cobertura de intereses no puede calcularse.","metric_key":null,"metric_value":null,"threshold_value":null,"metric_unit":null},
          {"level":"high","code":"ENDEUDAMIENTO_ELEVADO","title":"Endeudamiento elevado","description":"El pasivo representa el 41,8 % del activo, por encima del umbral configurado de 40,0 %.","metric_key":"liabilities_over_assets","metric_value":0.4184,"threshold_value":0.4000,"metric_unit":"proportion"},
          {"level":"medium","code":"SALDO_FISCAL_VENCIDO","title":"Saldo fiscal vencido por encima del umbral","description":"El saldo fiscal vencido informado supera el umbral configurado.","metric_key":"saldo_fiscal_vencido","metric_value":1500000,"threshold_value":1000000,"metric_unit":"absolute"}
        ]
        """;

    private const string Anomalies =
        """["El rubro Otros créditos creció 64 % respecto del ejercicio anterior sin nota explicativa."]""";

    private const string Narrative =
        "## Justificación del score\n\n" +
        "El score de **820** refleja ratios financieros sólidos —liquidez corriente de 1,52x y endeudamiento de 0,42x— " +
        "y un contexto BCRA favorable, moderados por la caída real de las ventas declaradas en LUFE, que restó 100 puntos.";

    private const string Summary =
        "# Resumen ejecutivo\n\n" +
        "La empresa presenta una estructura patrimonial equilibrada, con un patrimonio neto que financia el 58 % del activo. " +
        "La rentabilidad es positiva (ROE 12,4 %) y la liquidez corriente holgada. Las ventas declaradas muestran una caída real " +
        "del 25,6 % interanual en los últimos tres meses, que el score penaliza. La situación en la Central de Deudores pasó de 1 a 2 " +
        "en el último trimestre y se registran cheques rechazados, en su mayoría regularizados.";

    private static string Bcra() =>
        JsonSerializer.Serialize(new BcraData
        {
            Deuda = new BcraDeudaData
            {
                SituacionMaxima      = 2,
                MontoTotal           = 185_400m,
                EntidadesInformantes = ["Banco Uno", "Banco Dos", "Banco Tres"],
                PeriodoConsultado    = "202609",
                HistorialSituaciones =
                [
                    new BcraSituacionPeriodo { Periodo = "202609", SituacionMaxima = 2 },
                    new BcraSituacionPeriodo { Periodo = "202608", SituacionMaxima = 1 },
                    new BcraSituacionPeriodo { Periodo = "202607", SituacionMaxima = 1 },
                ],
                Estado = BcraDeudaConsultaEstado.DebtFound,
            },
            Macro = new BcraMacroData
            {
                InflacionMensual      = 2.1,
                TasaReferencia        = 38.5,
                InflacionMensualSerie = Ipc(),
            },
            Cambiario = new BcraCambiarioData { TipoCambioOficial = 1185.5 },
            Cheques = new BcraChequesData
            {
                TotalRechazados     = 3,
                TotalImpagos        = 1,
                MontoTotalRechazado = 4_350_000m,
                MontoTotalImpago    = 1_218_000m,
                Estado              = BcraChequesConsultaEstado.ChequesFound,
            },
        });

    private static string Lufe() =>
        JsonSerializer.Serialize(new LufeData
        {
            Entidad = new LufeEntidad { MesCierre = 6 },
            VentasComprasMensual = Serie(),
            IndicadoresHistoricos =
            [
                new LufeIndicador { Periodo = 202406m, VentasPb = 870_000_000m, ComprasPb = 0m, VtasMensualesProm = 72_500_000m },
                new LufeIndicador { Periodo = 202506m, VentasPb = 1_020_000_000m, ComprasPb = 0m, VtasMensualesProm = 85_000_000m },
            ],
        });

    private static string Casfog() =>
        JsonSerializer.Serialize(new CasfogData
        {
            Estado = CasfogConsultaEstado.DataFound,
            Garantias = new CasfogGarantias
            {
                SaldoBrutoGarantiasVigentes = 42_000_000m,
                CantidadGarantiasOtorgadas  = 7,
                MontoGarantiasOtorgadas     = 96_000_000m,
                CantidadGarantiasVigentes   = 3,
                PorSgr =
                [
                    new CasfogGarantiaSgr { Sgr = "SGR Ejemplo Norte", SaldoBrutoVigente = 30_000_000m, SituacionDeuda = 0, DiasAtraso = 0, CantidadEnMora = 0 },
                    new CasfogGarantiaSgr { Sgr = "SGR Ejemplo Sur",   SaldoBrutoVigente = 12_000_000m, SituacionDeuda = 1, DiasAtraso = 0, CantidadEnMora = 0 },
                ],
            },
        });

    internal static AnalysisResult Result() => new()
    {
        ResultId           = ResultId,
        AnalysisId         = AnalysisId,
        TenantId           = "tenant-synthetic",
        BalanceJson        = Balance,
        RatiosJson         = Ratios,
        ScoreBreakdownJson = Breakdown(),
        BcraDataJson       = Bcra(),
        LufeDataJson       = Lufe(),
        CasfogDataJson     = Casfog(),
        AiSuggestedScore   = 18m,
        AiSuggestedLevel   = "Low",
        EnrichmentStatus   = "Complete",
        ExecutiveSummary   = Summary,
        ScoringNarrative   = Narrative,
        RiskAlerts         = RiskAlerts,
        CatalogueAlerts    = CatalogueAlerts,
        Anomalies          = Anomalies,
        GeneratedAt        = new DateTime(2026, 10, 20, 15, 0, 0, DateTimeKind.Utc),
    };

    /// <summary>What the controller gathers for the first page — synthetic people, line and previous analysis.</summary>
    internal static CompanyPdfReport Report() => new()
    {
        EntityName  = BusinessName,
        EntityCuit  = Cuit,
        UserNames   = new Dictionary<Guid, string>
        {
            [new Guid("8f3fa5be-4444-4555-8666-777788889999")] = "Analista Uno",
            [new Guid("9a4ab6cf-5555-4666-8777-88889999aaaa")] = "Revisora Dos",
        },
        CreditLines =
        [
            new CreditLine { Amount = 40_000_000m, Currency = "ARS", Status = "Active" },
        ],
        Previous = new AnalysisComparisonSide { AnalysisId = Guid.Empty, ResultId = Guid.Empty, FiscalYear = 2024, Status = "Approved", PromptVersion = "2.20", Score = 760 },
        Current  = new AnalysisComparisonSide { AnalysisId = AnalysisId, ResultId = ResultId, FiscalYear = 2025, Status = "Approved", PromptVersion = "2.20", Score = 820 },
        TenantDisplayName = "SGR Sintética",
    };

    internal static Analysis Analysis() => new()
    {
        AnalysisId      = AnalysisId,
        TenantId        = "tenant-synthetic",
        EntityId        = new Guid("7e2e94ad-3333-4444-8555-666677778888"),
        AnalysisKind    = AnalysisKinds.Balance,
        RequestedBy     = new Guid("8f3fa5be-4444-4555-8666-777788889999"),
        RequestedAmount = 50_000_000m,
        Status          = "Approved",
        FiscalYear      = 2025,
        ReviewedBy      = new Guid("9a4ab6cf-5555-4666-8777-88889999aaaa"),
        ReviewedAt      = new DateTime(2026, 10, 28, 18, 30, 0, DateTimeKind.Utc),
        DecisionAmount  = 40_000_000m,
        DecisionCurrency = "ARS",
        ConfirmedOperationsJson =
            """[{"type":1,"amount":25000000,"currency":"ARS","validUntil":"2027-10-31"},{"type":3,"amount":15000000,"currency":"ARS","validUntil":"2027-04-30"}]""",
        CreatedAt       = new DateTime(2026, 10, 20, 14, 55, 0, DateTimeKind.Utc),
    };
}
