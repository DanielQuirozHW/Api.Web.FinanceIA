// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-02

using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.BalanceCorrections;

namespace Api.Web.FinanceIA.Tests.BalanceCorrections;

/// <summary>
/// A SYNTHETIC balance in the shape of the FINEX case — never a real client file. The document prints
/// "Créditos por ventas" as "–", but the reading put the bank-debt figure (119.378.233,22) there too, so
/// the current-asset rubros add up to more than the activo corriente and <c>current_assets_items</c> fails.
/// Every other check passes. The figures are in pesos, from a balance "expresado en miles"
/// (amounts_scale 1000), exactly as ClaudeService persists them; the integrity node is the one the REAL
/// <see cref="BalanceIntegrityService"/> writes, with a second-pass provenance.
/// </summary>
internal static class BalanceCorrectionFixtures
{
    /// <summary>The misread "Créditos por ventas" amount — the bank debt, read into the wrong rubro.</summary>
    public const double MisreadAmount = 119_378_233.22;

    /// <summary>The path of the misread line.</summary>
    public const string MisreadPath = "current_assets_items[1].amount";

    /// <summary>The analyst's reason: free text that no prompt or log may ever carry.</summary>
    public const string Reason = "Créditos por ventas figura con guion en el balance; la cifra es la deuda bancaria.";

    /// <summary>The result before income tax the document prints.</summary>
    public const double ResultBeforeTax = 90_000_000d;

    /// <summary>
    /// A misread result before tax: 9.000.000 against a net profit of 60.000.000 and a tax of -30.000.000,
    /// so neither the signed nor the magnitude reading of the tax reconciles.
    /// </summary>
    public const double MisreadResultBeforeTax = 9_000_000d;

    /// <summary>The FINEX-shaped balance with its integrity node, as persisted.</summary>
    public static string FinexBalanceJson() => Build(MisreadAmount, ResultBeforeTax);

    /// <summary>The same balance with the misread line at 0 and NO trail or correction marker.</summary>
    public static string CorrectedFigures() => Build(0d, ResultBeforeTax);

    /// <summary>
    /// The same balance with every rubro right but <c>result_before_tax</c> misread
    /// (<see cref="MisreadResultBeforeTax"/>): net_profit_reconciliation is the ONE failed check.
    /// </summary>
    public static string MisreadResultBeforeTaxJson() => Build(0d, MisreadResultBeforeTax);

    /// <summary>
    /// Policy 6 — the same balance with result before tax and income tax read from the COMPARATIVE column:
    /// 70.000.000 − 25.000.000 = the prior year's net profit of 45.000.000, beside the current 60.000.000.
    /// With <paramref name="recordVerdict"/> false the check's <c>columnMix</c> is stripped, as a blob written before
    /// policy 6 would carry it (its prior_year would still reveal the mix).
    /// </summary>
    public static string MixedColumnsJson(bool recordVerdict)
    {
        var json = Build(0d, 70_000_000d, incomeTax: -25_000_000d);
        if (recordVerdict)
            return json;

        var root = JsonNode.Parse(json)!.AsObject();
        foreach (var check in root["integrity"]!["checks"]!.AsArray())
            check!.AsObject().Remove("columnMix");
        return root.ToJsonString();
    }

    /// <summary>A correction command for the FINEX case.</summary>
    public static BalanceCorrectionCommand FinexCommand(double value = 0d)
        => new(Reason, [new BalanceCorrectionEntry(BalanceCorrectionPath.TryParse(MisreadPath)!, value)]);

    /// <summary>A command over one path, with the standard reason.</summary>
    public static BalanceCorrectionCommand Command(string path, double? value)
        => new(Reason, [new BalanceCorrectionEntry(BalanceCorrectionPath.TryParse(path)!, value)]);

    private static string Build(double creditosPorVentas, double resultBeforeTax, double incomeTax = -30_000_000d)
    {
        var root = new JsonObject
        {
            ["assets"]                  = 500_000_000d,
            ["current_assets"]          = 200_000_000d,
            ["non_current_assets"]      = 300_000_000d,
            ["liabilities"]             = 200_000_000d,
            ["current_liabilities"]     = 150_000_000d,
            ["non_current_liabilities"] = 50_000_000d,
            ["equity"]                  = 300_000_000d,
            ["net_revenue"]             = 900_000_000d,
            ["net_profit"]              = 60_000_000d,
            ["inventory"]               = 30_000_000d,
            ["prepaid_expenses"]        = null,
            ["operating_result"]        = 100_000_000d,
            ["financial_expenses"]      = 20_000_000d,
            ["balanced"]                = true,
            ["difference_pct"]          = 0d,
            ["fiscal_year_detected"]    = 2024,
            ["cash_flow"]               = new JsonObject { ["present"] = false },
            ["amounts_unit"]            = "miles",
            ["amounts_scale"]           = 1000d,
            ["result_before_tax"]       = resultBeforeTax,
            ["income_tax"]              = incomeTax,
            ["current_assets_items"]    = new JsonArray
            {
                Item("Caja y bancos", 50_000_000d),
                Item("Créditos por ventas", creditosPorVentas),
                Item("Otros créditos", 120_000_000d),
                Item("Bienes de cambio", 30_000_000d),
            },
            ["current_liabilities_items"] = new JsonArray
            {
                Item("Deudas comerciales", 30_621_766.78),
                Item("Deudas bancarias", 119_378_233.22),
            },
            ["non_current_liabilities_items"] = new JsonArray
            {
                Item("Deudas bancarias", 50_000_000d),
            },
            ["prior_year"] = new JsonObject
            {
                ["current_assets"]          = 160_000_000d,
                ["non_current_assets"]      = 240_000_000d,
                ["assets"]                  = 400_000_000d,
                ["current_liabilities"]     = 115_000_000d,
                ["non_current_liabilities"] = 40_000_000d,
                ["liabilities"]             = 155_000_000d,
                ["equity"]                  = 245_000_000d,
                ["net_revenue"]             = 700_000_000d,
                ["net_profit"]              = 45_000_000d,
            },
        };

        var balance = new ExtractedBalance
        {
            Current                    = root.Deserialize<BalanceAccounts>()! with { AmountsScale = null },
            PriorYear                  = root["prior_year"]!.Deserialize<BalanceAccounts>(),
            CurrentAssetsItems         = root["current_assets_items"]!.Deserialize<List<BalanceLineItem>>(),
            CurrentLiabilitiesItems    = root["current_liabilities_items"]!.Deserialize<List<BalanceLineItem>>(),
            NonCurrentLiabilitiesItems = root["non_current_liabilities_items"]!.Deserialize<List<BalanceLineItem>>(),
            ResultBeforeTax            = resultBeforeTax,
            IncomeTax                  = incomeTax,
            AppliedScale               = 1000d,
        };

        var report  = new BalanceIntegrityService().Evaluate(balance).Report;
        var capped  = report.Passed ? 0.92 : (double)BalanceIntegrityService.ConfidenceCap;
        var written = report with
        {
            AmountsUnit         = BalanceAmountsUnit.Miles,
            AmountsUnitAssumed  = false,
            ConfidenceCapped    = !report.Passed,
            ReportedConfidence  = 0.92,
            EffectiveConfidence = capped,
            Extraction          = new ExtractionPassInfo
            {
                Method             = "pdf",
                SecondPassRan      = true,
                SecondPassTriggers = [ExtractionPassInfo.IntegrityTrigger],
                KeptPass           = ExtractionPassInfo.FirstPass,
                KeptReason         = "fewer_failed_checks",
            },
        };

        root["integrity"] = JsonSerializer.SerializeToNode(written);
        return root.ToJsonString();
    }

    private static JsonObject Item(string label, double amount) => new() { ["label"] = label, ["amount"] = amount };
}
