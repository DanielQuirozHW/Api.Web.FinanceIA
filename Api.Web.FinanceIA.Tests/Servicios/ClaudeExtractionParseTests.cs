// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-24

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Api.Web.FinanceIA.Models;
using Microsoft.Extensions.Options;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Drives <see cref="ClaudeService.AnalyzeDocumentAsync"/> end to end over a canned Anthropic
/// envelope — the only way to reach the private response parsing, which is where M11 lives: the
/// lenient amount reading, the scale to pesos, the integrity checks, the confidence cap, the
/// appended anomalies and the persisted BalanceJson shape. No live API call is made.
/// </summary>
public sealed class ClaudeExtractionParseTests
{
    private static readonly byte[] FakePdf = "%PDF-1.4 fixture"u8.ToArray();

    // ── Harness ───────────────────────────────────────────────────────────────

    private static ClaudeService BuildSut(string modelJson, IBalanceIntegrityService? integrity = null) =>
        new(new StubHttpClientFactory(new CannedHandler(Envelope(modelJson))),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Anthropic:ApiKey"] = "test-key",
                    // No image fallback: the fixture PDF cannot be rendered, and the pass under test
                    // is the first one.
                    ["Anthropic:ExtractionConfidenceFallbackThreshold"] = "0"
                })
                .Build(),
            NullLogger<ClaudeService>.Instance,
            integrity ?? new BalanceIntegrityService(),
            Options.Create(new ExtractionModelOptions()),
            NullScanOrientationDetector.Instance);

    private static string Envelope(string modelJson) =>
        JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = modelJson } },
            usage   = new { input_tokens = 10, output_tokens = 20 }
        });

    private static Task<ClaudeAnalysisResult> AnalyzeAsync(string modelJson, IBalanceIntegrityService? integrity = null) =>
        BuildSut(modelJson, integrity).AnalyzeDocumentAsync(FakePdf, "balance", 2025, CancellationToken.None);

    private static JsonObject Balance(ClaudeAnalysisResult result) => JsonNode.Parse(result.BalanceJson)!.AsObject();

    private static List<string> Anomalies(ClaudeAnalysisResult result) =>
        JsonSerializer.Deserialize<List<string>>(result.AnomaliesJson)!;

    // A minimal model answer; `balanceCheck` and `extra` are spliced in as raw JSON members.
    internal static string ModelJson(string balanceCheck, string extra = "", string confidence = "0.9") =>
        "{" +
        "\"ratios\": {}, \"alerts\": [], \"anomalies\": [\"Observación del modelo.\"], " +
        "\"executive_summary\": \"Resumen.\", " +
        $"\"confidence_score\": {confidence}, \"fiscal_year_detected\": 2025, " +
        $"\"balance_check\": {{ {balanceCheck} }}" +
        (extra.Length > 0 ? ", " + extra : string.Empty) +
        "}";

    // ── Pinned column-swap case ───────────────────────────────────────────────

    private const string ColumnSwapBalanceCheck =
        "\"assets\": 87100000000, \"current_assets\": 72100000000, \"non_current_assets\": 15000000000, " +
        "\"liabilities\": 55573115556, \"current_liabilities\": 47637949041, \"non_current_liabilities\": 7935166515, " +
        "\"equity\": 31526884444, \"inventory\": 27200000000, \"balanced\": true, \"difference_pct\": 0, " +
        "\"current_liabilities_items\": [" +
        "{\"label\": \"Deudas comerciales\", \"amount\": 33190632263}, " +
        "{\"label\": \"Préstamos\", \"amount\": 4997185813}, " +
        "{\"label\": \"Remuneraciones y cargas sociales\", \"amount\": 526531729}, " +
        "{\"label\": \"Cargas fiscales\", \"amount\": 4532840481}, " +
        "{\"label\": \"Otros pasivos\", \"amount\": 0}]";

    private const string ColumnSwapPriorYear =
        "\"amounts_unit\": \"pesos\", \"prior_year\": { \"assets\": 47637949041, \"current_assets\": 35000000000, " +
        "\"non_current_assets\": 12637949041, \"liabilities\": 30000000000, \"current_liabilities\": 25000000000, " +
        "\"non_current_liabilities\": 5000000000, \"equity\": 17637949041 }";

    [Fact]
    public async Task ColumnSwapCase_CapsConfidenceAndPersistsTheIntegrityReport()
    {
        var result = await AnalyzeAsync(ModelJson(ColumnSwapBalanceCheck, ColumnSwapPriorYear));

        Assert.True(result.ConfidenceScore <= 0.69m);
        Assert.Equal(0.69m, result.ConfidenceScore);
        Assert.False(result.BalanceIntegrityPassed);

        var integrity = Balance(result)["integrity"]!.AsObject();
        Assert.False(integrity["passed"]!.GetValue<bool>());
        Assert.Equal(
            ["liquidez_corriente", "prueba_acida"],
            integrity["affectedRatios"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.True(integrity["confidenceCapped"]!.GetValue<bool>());
        Assert.Equal(0.9, integrity["reportedConfidence"]!.GetValue<double>());
        Assert.Equal(0.69, integrity["effectiveConfidence"]!.GetValue<double>());
        Assert.Equal("pesos", integrity["amountsUnit"]!.GetValue<string>());
        Assert.False(integrity["amountsUnitAssumed"]!.GetValue<bool>());

        // The model's own balance_check said balanced=true; it is persisted as reported.
        Assert.True(Balance(result)["balanced"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ColumnSwapCase_AppendsTheIntegrityAnomaliesAfterTheModelsOwn()
    {
        var result = await AnalyzeAsync(ModelJson(ColumnSwapBalanceCheck, ColumnSwapPriorYear));

        var anomalies = Anomalies(result);
        Assert.Equal("Observación del modelo.", anomalies[0]);
        Assert.Contains(
            "La suma de los rubros del pasivo corriente ($43.247.190.286) no coincide con el pasivo " +
            "corriente informado ($47.637.949.041).",
            anomalies);
        Assert.Contains(anomalies, a => a.Contains("columna comparativa", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ColumnSwapCase_PersistsRubrosAndPriorYear()
    {
        var balance = Balance(await AnalyzeAsync(ModelJson(ColumnSwapBalanceCheck, ColumnSwapPriorYear)));

        var items = balance["current_liabilities_items"]!.AsArray();
        Assert.Equal(5, items.Count);
        Assert.Equal("Deudas comerciales", items[0]!["label"]!.GetValue<string>());
        Assert.Equal(33190632263d, items[0]!["amount"]!.GetValue<double>());
        Assert.Equal(47637949041d, balance["prior_year"]!["assets"]!.GetValue<double>());
        Assert.Null(balance["current_assets_items"]);
    }

    // ── Scale ─────────────────────────────────────────────────────────────────

    private const string ThousandsBalanceCheck =
        "\"assets\": 1000, \"current_assets\": 600, \"non_current_assets\": 400, " +
        "\"liabilities\": 700, \"current_liabilities\": 500, \"non_current_liabilities\": 200, " +
        "\"equity\": 300, \"net_revenue\": 2000, \"net_profit\": 100, \"inventory\": 150, " +
        "\"prepaid_expenses\": 10, \"operating_result\": 180, \"financial_expenses\": -40, " +
        "\"result_before_tax\": 140, \"income_tax\": -40, \"balanced\": true, \"difference_pct\": 0, " +
        "\"current_assets_items\": [{\"label\": \"Caja y bancos\", \"amount\": 250}, {\"label\": \"Bienes de cambio\", \"amount\": 350}]";

    private const string ThousandsExtra =
        "\"amounts_unit\": \"miles\", \"prior_year\": { \"assets\": 800, \"liabilities\": 550, \"equity\": 250 }, " +
        "\"cash_flow\": { \"present\": true, \"method\": \"indirect\", \"cash_beginning\": 40, \"cash_end\": 55, " +
        "\"net_change\": 15, \"operating\": 30, \"investing\": -10, \"financing\": -5, " +
        "\"components\": { \"capex\": -10, \"dividends_paid\": -5 } }";

    [Fact]
    public async Task Thousands_PersistsEveryAmountInPesos()
    {
        var result  = await AnalyzeAsync(ModelJson(ThousandsBalanceCheck, ThousandsExtra));
        var balance = Balance(result);

        Assert.Equal("miles", balance["amounts_unit"]!.GetValue<string>());
        Assert.Equal(1000d, balance["amounts_scale"]!.GetValue<double>());
        Assert.Equal(1_000_000d, balance["assets"]!.GetValue<double>());
        Assert.Equal(300_000d, balance["equity"]!.GetValue<double>());
        Assert.Equal(-40_000d, balance["income_tax"]!.GetValue<double>());
        Assert.Equal(250_000d, balance["current_assets_items"]![0]!["amount"]!.GetValue<double>());
        Assert.Equal(800_000d, balance["prior_year"]!["assets"]!.GetValue<double>());
        Assert.Equal(55_000d, balance["cash_flow"]!["cash_end"]!.GetValue<double>());
        Assert.Equal(-10_000d, balance["cash_flow"]!["components"]!["capex"]!.GetValue<double>());
        Assert.True(balance["integrity"]!["passed"]!.GetValue<bool>());
        Assert.Equal(0.9m, result.ConfidenceScore);
    }

    [Fact]
    public async Task Thousands_RatiosAreUnchangedByTheScale()
    {
        var result = await AnalyzeAsync(ModelJson(ThousandsBalanceCheck, ThousandsExtra));
        var ratios = new RatioCalculationService();

        var persisted = JsonSerializer.Deserialize<BalanceAccounts>(result.BalanceJson)!;
        var printed   = persisted.InPrintedUnits();

        Assert.Equal(1000d, printed.TotalAssets!.Value, 6);
        Assert.Null(printed.AmountsScale);
        // The two raw operands RatiosData carries for the scoring engine (algorithm 10) are FIGURES, not
        // ratios, so they follow the scale by design; every ratio and every exclusion reason must not.
        var fromPrinted   = ratios.Calculate(printed);
        var fromPersisted = ratios.Calculate(persisted);
        Assert.Equal(fromPersisted.OperatingResult, fromPrinted.OperatingResult * 1000d);
        fromPrinted.OperatingResult   = fromPersisted.OperatingResult   = null;
        fromPrinted.FinancialExpenses = fromPersisted.FinancialExpenses = null;
        Assert.Equivalent(fromPrinted, fromPersisted);
        Assert.Equal(1.2, ratios.Calculate(persisted).CurrentRatio);
    }

    [Fact]
    public async Task Thousands_IntegrityAnomalyQuotesPrintedUnitsLikeTheModelDoes()
    {
        var result = await AnalyzeAsync(ModelJson(
            "\"current_liabilities\": 47637949, \"balanced\": true, \"difference_pct\": 0, " +
            "\"current_liabilities_items\": [{\"label\": \"Deudas comerciales\", \"amount\": 43247190}]",
            "\"amounts_unit\": \"miles\""));

        Assert.Equal(47_637_949_000d, Balance(result)["current_liabilities"]!.GetValue<double>());
        Assert.Contains(
            "La suma de los rubros del pasivo corriente ($43.247.190, en miles) no coincide con el pasivo " +
            "corriente informado ($47.637.949, en miles).",
            Anomalies(result));
    }

    // ── Deserialization ───────────────────────────────────────────────────────

    [Fact]
    public async Task LossQuotedAsAString_IsParsedAsNegative()
    {
        var result = await AnalyzeAsync(ModelJson(
            "\"assets\": 1000, \"liabilities\": 700, \"equity\": 300, \"net_profit\": \"(1.234.567)\", " +
            "\"balanced\": true, \"difference_pct\": 0"));

        Assert.Equal(-1_234_567d, Balance(result)["net_profit"]!.GetValue<double>());
        Assert.DoesNotContain(Anomalies(result), a => a.Contains("no pudo interpretarse", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnreadableFigure_BecomesNullPlusAnAnomaly()
    {
        var result = await AnalyzeAsync(ModelJson(
            "\"assets\": 1000, \"liabilities\": 700, \"equity\": 300, \"net_revenue\": \"ilegible\", " +
            "\"balanced\": true, \"difference_pct\": 0"));

        Assert.Null(Balance(result)["net_revenue"]);
        Assert.Contains(
            "Un importe extraído del documento («ilegible») no pudo interpretarse como número y se dejó sin valor.",
            Anomalies(result));
    }

    [Fact]
    public async Task QuotedConfidenceNullBalancedAndQuotedCashFlowFigure_DoNotFailTheAnalysis()
    {
        var result = await AnalyzeAsync(ModelJson(
            "\"assets\": 1000, \"liabilities\": 700, \"equity\": 300, \"balanced\": null, \"difference_pct\": 0",
            "\"cash_flow\": { \"present\": true, \"operating\": \"(1.500)\", \"net_change\": 15 }",
            confidence: "\"0.85\""));

        Assert.Equal(0.85m, result.ConfidenceScore);
        Assert.False(result.BalanceCheckBalanced);

        var cashFlow = Balance(result)["cash_flow"]!;
        Assert.True(cashFlow["present"]!.GetValue<bool>());
        Assert.Equal(-1_500d, cashFlow["operating"]!.GetValue<double>());
    }

    // ── Real nulls ────────────────────────────────────────────────────────────

    [Fact]
    public async Task TotalsNotExtracted_PersistAsNullNotZero()
    {
        var result = await AnalyzeAsync(ModelJson(
            "\"current_assets\": 600, \"current_liabilities\": 500, \"balanced\": false, \"difference_pct\": 0"));

        var balance = Balance(result);
        Assert.True(balance.ContainsKey("assets"));
        Assert.Null(balance["assets"]);
        Assert.Null(balance["liabilities"]);
        Assert.Null(balance["equity"]);

        var accounts = JsonSerializer.Deserialize<BalanceAccounts>(result.BalanceJson)!;
        var ratios   = new RatioCalculationService().Calculate(accounts);
        Assert.Null(ratios.DebtRatio);
        Assert.Equal(1.2, ratios.CurrentRatio);
    }

    // ── Trial balance (prompt 2.19) ───────────────────────────────────────────
    // SYNTHETIC "Balance de Presentación": Activo 1.000.000 = Pasivo 400.000 + PN impreso 450.000 + resultado
    // 150.000; the ledger prints credits negative and the model echoed the rubros' credit sign.

    internal const string TrialBalanceCheck =
        "\"assets\": 1000000, \"current_assets\": 700000, \"non_current_assets\": 300000, " +
        "\"liabilities\": 400000, \"current_liabilities\": 300000, \"non_current_liabilities\": 100000, " +
        "\"equity\": 450000, \"net_revenue\": 850000, \"net_profit\": 95000, \"balanced\": false, \"difference_pct\": 15, " +
        "\"current_liabilities_items\": [" +
        "{\"label\": \"Deudas comerciales\", \"amount\": -180000}, {\"label\": \"Cargas fiscales\", \"amount\": -120000}]";

    internal const string TrialBalanceBlock =
        "\"amounts_unit\": \"pesos\", \"document_kind\": \"trial_balance\", \"trial_balance\": { " +
        "\"credit_balances_negative\": true, \"equity_printed\": -450000, \"results_accounts\": [" +
        "{\"code\": \"4.1.01\", \"label\": \"Ventas\", \"amount\": \"-850.000,00\", \"class\": \"revenue\"}, " +
        "{\"code\": \"5.1.01\", \"label\": \"Costo de ventas\", \"amount\": 500000, \"class\": \"cost_of_sales\"}, " +
        "{\"code\": \"5.2.01\", \"label\": \"Gastos de administración\", \"amount\": 120000, \"class\": \"operating_expense\"}, " +
        "{\"code\": \"5.3.01\", \"label\": \"Intereses\", \"amount\": 40000, \"class\": \"interest\"}, " +
        "{\"code\": \"5.4.01\", \"label\": \"Impuesto a las ganancias\", \"amount\": 60000, \"class\": \"income_tax\"}, " +
        "{\"code\": \"6.1.01\", \"label\": \"RECPAM\", \"amount\": -20000, \"class\": \"recpam\"}] }";

    [Fact]
    public async Task TrialBalance_IsDetected_SignedPositive_AndItsEquityIncludesTheYearResult()
    {
        var result  = await AnalyzeAsync(ModelJson(TrialBalanceCheck, TrialBalanceBlock));
        var balance = Balance(result);

        Assert.Equal("trial_balance", balance["document_kind"]!.GetValue<string>());
        Assert.Equal(600000d, balance["equity"]!.GetValue<double>());
        Assert.Equal(150000d, balance["net_profit"]!.GetValue<double>());
        Assert.Equal(210000d, balance["result_before_tax"]!.GetValue<double>());
        Assert.Equal(-60000d, balance["income_tax"]!.GetValue<double>());
        Assert.Equal(-40000d, balance["financial_expenses"]!.GetValue<double>());
        Assert.Equal(230000d, balance["operating_result"]!.GetValue<double>());
        Assert.Equal([180000d, 120000d],
            balance["current_liabilities_items"]!.AsArray().Select(i => i!["amount"]!.GetValue<double>()));

        // The audit keeps the printed equity; no account label is persisted.
        var audit = balance["trial_balance"]!.AsObject();
        Assert.Equal("result_added", audit["adjustment"]!.GetValue<string>());
        Assert.Equal(450000d, audit["equity_printed"]!.GetValue<double>());
        Assert.Equal(150000d, audit["year_result"]!.GetValue<double>());
        Assert.DoesNotContain("Ventas", result.BalanceJson, StringComparison.Ordinal);

        // Both checks the reading failed now pass, so the first pass is kept with its confidence.
        Assert.True(result.BalanceIntegrityPassed);
        Assert.Equal(0.9m, result.ConfidenceScore);
        Assert.Equal(
            [
                "Observación del modelo.",
                "El documento es un balance de sumas y saldos previo al cierre: el patrimonio neto impreso ($450.000) no " +
                "incluye el resultado del ejercicio. Se le sumó el resultado del ejercicio calculado a partir de las " +
                "cuentas de resultado ($150.000), con lo que el patrimonio neto asciende a $600.000.",
            ],
            Anomalies(result));

        var dto = ExtractionIntegrityReader.Read(result.BalanceJson, 2025)!;
        Assert.Equal("trial_balance", dto.DocumentKind);
        Assert.True(dto.Verified);
    }

    [Fact]
    public async Task FinancialStatements_PersistExactlyWhatTheyDidBefore()
    {
        const string check =
            "\"assets\": 1000, \"current_assets\": 600, \"non_current_assets\": 400, \"liabilities\": 700, " +
            "\"current_liabilities\": 500, \"non_current_liabilities\": 200, \"equity\": 300, \"net_profit\": -10, " +
            "\"balanced\": true, \"difference_pct\": 0";

        var without = await AnalyzeAsync(ModelJson(check, "\"amounts_unit\": \"pesos\""));
        var with    = await AnalyzeAsync(ModelJson(check,
            "\"amounts_unit\": \"pesos\", \"document_kind\": \"financial_statements\", \"trial_balance\": null"));

        Assert.Equal(without.BalanceJson, with.BalanceJson);
        Assert.Equal(without.AnomaliesJson, with.AnomaliesJson);
        Assert.Equal(without.ConfidenceScore, with.ConfidenceScore);
        Assert.False(Balance(with).ContainsKey("document_kind"));
        Assert.False(Balance(with).ContainsKey("trial_balance"));
        Assert.Null(ExtractionIntegrityReader.Read(with.BalanceJson)!.DocumentKind);
    }

    [Fact]
    public async Task TrialBalance_WithAMalformedBlock_StillParses_AndLeavesEquityAndResultAsRead()
    {
        var result = await AnalyzeAsync(ModelJson(TrialBalanceCheck,
            "\"amounts_unit\": \"pesos\", \"document_kind\": \"trial_balance\", \"trial_balance\": \"n/a\""));
        var balance = Balance(result);

        Assert.Equal("trial_balance", balance["document_kind"]!.GetValue<string>());
        Assert.Equal("not_computable", balance["trial_balance"]!["adjustment"]!.GetValue<string>());
        Assert.Equal(450000d, balance["equity"]!.GetValue<double>());
        Assert.Equal(95000d, balance["net_profit"]!.GetValue<double>());
        Assert.False(result.BalanceIntegrityPassed);
    }

    // ── Older output and fault isolation ──────────────────────────────────────

    [Fact]
    public async Task OlderOutput_WithoutTheNewFields_StillParsesAndAssumesPesos()
    {
        var result = await AnalyzeAsync(ModelJson(
            "\"assets\": 1000, \"current_assets\": 600, \"non_current_assets\": 400, \"liabilities\": 700, " +
            "\"equity\": 300, \"balanced\": true, \"difference_pct\": 0"));

        var balance = Balance(result);
        Assert.Null(balance["amounts_unit"]);
        Assert.Equal(1d, balance["amounts_scale"]!.GetValue<double>());
        Assert.Null(balance["prior_year"]);
        Assert.Equal(1000d, balance["assets"]!.GetValue<double>());
        Assert.True(balance["integrity"]!["amountsUnitAssumed"]!.GetValue<bool>());
        Assert.True(balance["integrity"]!["passed"]!.GetValue<bool>());
        Assert.True(result.BalanceIntegrityPassed);
        Assert.Equal(["Observación del modelo."], Anomalies(result));
    }

    [Fact]
    public async Task IntegrityServiceThrows_AnalysisContinuesWithIntegrityNull()
    {
        var result = await AnalyzeAsync(ModelJson(ColumnSwapBalanceCheck, ColumnSwapPriorYear), new ThrowingIntegrity());

        var balance = Balance(result);
        Assert.True(balance.ContainsKey("integrity"));
        Assert.Null(balance["integrity"]);
        Assert.Null(result.BalanceIntegrityPassed);
        Assert.Equal(0.9m, result.ConfidenceScore);
        Assert.Equal(["Observación del modelo."], Anomalies(result));
        Assert.Equal(47637949041d, balance["current_liabilities"]!.GetValue<double>());
    }

    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed class ThrowingIntegrity : IBalanceIntegrityService
    {
        public BalanceIntegrityOutcome Evaluate(ExtractedBalance balance) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class CannedHandler(string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }
}
