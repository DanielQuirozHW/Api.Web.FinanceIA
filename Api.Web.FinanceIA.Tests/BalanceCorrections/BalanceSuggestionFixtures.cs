// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-03

using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Servicios.BalanceCorrections;
using SkiaSharp;

namespace Api.Web.FinanceIA.Tests.BalanceCorrections;

/// <summary>
/// SYNTHETIC balances and model answers for the correction suggestion (10-F4a) — never a real client file.
/// <para>
/// The sideways-scan shape: a landscape balance "expresado en miles" whose pasivo corriente total was read
/// from the ASSETS half (the activo corriente figure, 200.000) and whose pasivo corriente rubros carry one
/// line that is not printed there. The document prints Total Pasivo Corriente 150.000 with two rubros (30.000
/// and 120.000). Failed: liabilities_components and current_liabilities_items; everything else passes.
/// </para>
/// </summary>
internal static class BalanceSuggestionFixtures
{
    /// <summary>The misread pasivo corriente, in pesos (the activo corriente figure).</summary>
    public const double MisreadCurrentLiabilities = 200_000_000d;

    /// <summary>The printed pasivo corriente, in thousands as printed.</summary>
    public const double PrintedCurrentLiabilities = 150_000d;

    /// <summary>A label planted in the stored rubros: it must never reach a prompt or a log.</summary>
    public const string PlantedLabel = "Préstamo de Juan Pérez CUIT 20-12345678-9";

    /// <summary>The model id the scripted replies report.</summary>
    public const string Model = "claude-sonnet-4-6";

    /// <summary>The sideways-scan balance with the integrity node the REAL checks write.</summary>
    public static string SidewaysBalanceJson()
    {
        var root = new JsonObject
        {
            ["assets"]                  = 500_000_000d,
            ["current_assets"]          = 200_000_000d,
            ["non_current_assets"]      = 300_000_000d,
            ["liabilities"]             = 200_000_000d,
            ["current_liabilities"]     = MisreadCurrentLiabilities,
            ["non_current_liabilities"] = 50_000_000d,
            ["equity"]                  = 300_000_000d,
            ["net_revenue"]             = 900_000_000d,
            ["net_profit"]              = 60_000_000d,
            ["inventory"]               = 30_000_000d,
            ["operating_result"]        = 100_000_000d,
            ["financial_expenses"]      = 20_000_000d,
            ["balanced"]                = true,
            ["fiscal_year_detected"]    = 2024,
            ["amounts_unit"]            = "miles",
            ["amounts_scale"]           = 1000d,
            ["current_liabilities_items"] = new JsonArray
            {
                Item("Deudas comerciales", 30_000_000d),
                Item("Deudas bancarias", 120_000_000d),
                Item(PlantedLabel, 40_000_000d),
            },
        };

        return WithIntegrity(root);
    }

    /// <summary>Attaches the integrity node <see cref="BalanceCorrectionApplier.DryRun"/> computes over the figures.</summary>
    public static string WithIntegrity(JsonObject root)
    {
        var report = BalanceCorrectionApplier.DryRun(root.ToJsonString(), [], new BalanceIntegrityService()).Report!;
        root["integrity"] = JsonSerializer.SerializeToNode(report with
        {
            AmountsUnit         = BalanceAmountsUnit.Miles,
            ReportedConfidence  = 0.9,
            EffectiveConfidence = report.Passed ? 0.9 : (double)BalanceIntegrityService.ConfidenceCap,
        });
        return root.ToJsonString();
    }

    /// <summary>The model's answer, written as the JSON it sends.</summary>
    public static BalanceSuggestionAnswer Answer(string json)
        => JsonSerializer.Deserialize<BalanceSuggestionAnswer>(json)!;

    /// <summary>The model reads the sideways scan right: the total from the liabilities half, two rubros.</summary>
    public static BalanceSuggestionAnswer SidewaysFixAnswer() => Answer(
        """
        {"fields":[
           {"path":"current_liabilities","printed_value":150000,"matches_stored":false,"image":4,"half":"right","band":[0.40,0.44],"label":"Total Pasivo Corriente"},
           {"path":"liabilities","printed_value":200000,"matches_stored":true,"image":4,"half":"right","band":[0.70,0.74],"label":"Total del Pasivo"},
           {"path":"non_current_liabilities","printed_value":50000,"matches_stored":true,"image":4,"half":"right","band":[0.60,0.64],"label":"Total Pasivo No Corriente"}],
         "items":[{"array":"current_liabilities_items","lines":[
           {"stored_index":0,"printed_value":30000,"matches_stored":true,"image":4,"half":"right","band":[0.30,0.33],"label":"Deudas comerciales"},
           {"stored_index":1,"printed_value":120000,"matches_stored":true,"image":4,"half":"right","band":[0.34,0.37],"label":"Deudas bancarias"}]}]}
        """);

    /// <summary>The FINEX reading: the "–" of Créditos por ventas is zero.</summary>
    public static BalanceSuggestionAnswer FinexFixAnswer() => Answer(
        """
        {"fields":[{"path":"current_assets","printed_value":200000,"matches_stored":true,"image":1,"half":"full","band":[0.50,0.53],"label":"Total Activo Corriente"}],
         "items":[{"array":"current_assets_items","lines":[
           {"stored_index":0,"printed_value":50000,"matches_stored":true,"image":1,"half":"full","band":[0.20,0.23],"label":"Caja y bancos"},
           {"stored_index":1,"printed_value":0,"matches_stored":false,"image":1,"half":"full","band":[0.25,0.28],"label":"Créditos por ventas"},
           {"stored_index":2,"printed_value":120000,"matches_stored":true,"image":1,"half":"full","band":[0.30,0.33],"label":"Otros créditos"},
           {"stored_index":3,"printed_value":30000,"matches_stored":true,"image":1,"half":"full","band":[0.35,0.38],"label":"Bienes de cambio"}]}]}
        """);

    /// <summary>The FINEX figures confirmed exactly as stored — the document itself does not add up.</summary>
    public static BalanceSuggestionAnswer FinexConfirmedAnswer() => Answer(
        """
        {"fields":[{"path":"current_assets","printed_value":200000,"matches_stored":true,"image":1,"half":"full","band":[0.50,0.53],"label":"Total Activo Corriente"}],
         "items":[{"array":"current_assets_items","lines":[
           {"stored_index":0,"printed_value":50000,"matches_stored":true},
           {"stored_index":1,"printed_value":119378.23322,"matches_stored":true},
           {"stored_index":2,"printed_value":120000,"matches_stored":true},
           {"stored_index":3,"printed_value":30000,"matches_stored":true}]}]}
        """);

    // ── net_profit_reconciliation alone (the document's presentation, not the reading) ─────────────

    /// <summary>The printed net result, in pesos (amounts_scale 1).</summary>
    public const double NetProfit = 11_861_910.77;

    /// <summary>The printed income tax, a charge in parentheses.</summary>
    public const double IncomeTax = -2_330_167.58;

    /// <summary>The printed "resultado antes del impuesto" subtotal that leaves out the financial results.</summary>
    public const double PrintedPreTaxSubtotal = 12_500_000d;

    /// <summary>The result before tax that reconciles: net − tax.</summary>
    public const double DerivedResultBeforeTax = 14_192_078.35;

    /// <summary>
    /// A balance in pesos whose structure checks all pass (no rubros, no prior year) and whose income
    /// statement carries <paramref name="resultBeforeTax"/> next to the net result and the tax: with the
    /// printed subtotal, net_profit_reconciliation is the ONE failed check.
    /// </summary>
    public static string NetProfitBalanceJson(
        double? resultBeforeTax = PrintedPreTaxSubtotal,
        double incomeTax = IncomeTax,
        double netProfit = NetProfit,
        double netRevenue = 900_000_000d)
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
            ["net_revenue"]             = netRevenue,
            ["net_profit"]              = netProfit,
            ["inventory"]               = 30_000_000d,
            ["operating_result"]        = 20_000_000d,
            ["financial_expenses"]      = 5_000_000d,
            ["result_before_tax"]       = resultBeforeTax,
            ["income_tax"]              = incomeTax,
            ["balanced"]                = true,
            ["fiscal_year_detected"]    = 2024,
            ["amounts_unit"]            = "unidades",
            ["amounts_scale"]           = 1d,
        };

        return WithIntegrity(root);
    }

    /// <summary>
    /// A balance whose income statement reconciles but whose net sales are negative: sign_net_revenue — a
    /// soft check a suggestion does not target — is the ONE failed check.
    /// </summary>
    public static string SignOnlyBalanceJson() =>
        NetProfitBalanceJson(DerivedResultBeforeTax, netRevenue: -900_000_000d);

    /// <summary>
    /// The model's reading of the income statement: the net result on image 1 (page 1), the tax on image 2
    /// (page 2), and the pre-tax line where printed (null: no such line).
    /// </summary>
    public static BalanceSuggestionAnswer NetProfitAnswer(
        double? netProfit = NetProfit,
        bool netMatches = true,
        double? incomeTax = IncomeTax,
        double? resultBeforeTax = PrintedPreTaxSubtotal) => new()
    {
        Fields =
        [
            Field("net_profit", netProfit, netMatches, 1, [0.40, 0.44], "Ganancia del ejercicio"),
            Field("income_tax", incomeTax, true, 2, [0.30, 0.34], "Impuesto a las ganancias"),
            resultBeforeTax is null
                ? new BalanceSuggestionFieldAnswer { Path = "result_before_tax" }
                : Field("result_before_tax", resultBeforeTax, true, 1, [0.30, 0.34], "Resultado antes del impuesto a las ganancias"),
        ],
        Items = [],
    };

    /// <summary>The two portrait pages of the income statement.</summary>
    public static IReadOnlyList<RenderedImage> NetProfitImages() =>
    [
        Image(1, 1, RenderedImage.FullHalf),
        Image(2, 2, RenderedImage.FullHalf, barTop: 300, barBottom: 340),
    ];

    /// <summary>A successful reply carrying <paramref name="answer"/> and <paramref name="images"/>.</summary>
    public static BalanceSuggestionReply Reply(BalanceSuggestionAnswer answer, IReadOnlyList<RenderedImage>? images = null) => new()
    {
        Answer       = answer,
        Images       = images ?? [],
        Model        = Model,
        RequestSent  = true,
        HttpStatus   = 200,
        InputTokens  = 12_000,
        OutputTokens = 800,
        DurationMs   = 4_321,
    };

    /// <summary>
    /// A synthetic page image: white, with a black horizontal bar across rows [barTop, barBottom) — what
    /// a printed row looks like to the crop geometry.
    /// </summary>
    public static RenderedImage Image(
        int number, int page, string half, int width = 600, int height = 1000, int barTop = 400, int barBottom = 440)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Black };
            canvas.DrawRect(new SKRect(0, barTop, width, barBottom), paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data  = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return new RenderedImage(number, page, half, width, height, Convert.ToBase64String(data.ToArray()));
    }

    /// <summary>The three images of a split landscape page 2 at numbers 2..4 after a full page 1.</summary>
    public static IReadOnlyList<RenderedImage> SidewaysImages() =>
    [
        Image(1, 1, RenderedImage.FullHalf),
        Image(2, 2, RenderedImage.FullHalf, width: 1400, height: 1000),
        Image(3, 2, RenderedImage.LeftHalf, width: 784, height: 1000),
        Image(4, 2, RenderedImage.RightHalf, width: 784, height: 1000),
    ];

    // ── A recorded column mix (synthetic, TATAIKE-like) ────────────────────────────

    /// <summary>The current year's net result, read right.</summary>
    public const double MixNetProfit = 1_000_000d;

    /// <summary>The result before tax as stored: the PRIOR year's, read from the comparative column.</summary>
    public const double MixStoredPreTax = 1_500_000d;

    /// <summary>The income tax as stored: the PRIOR year's, a charge.</summary>
    public const double MixStoredTax = -400_000d;

    /// <summary>The prior year's net result: stored pre-tax + stored tax, which is what makes it a column mix.</summary>
    public const double MixPriorNetProfit = 1_100_000d;

    /// <summary>The current year's printed result before tax.</summary>
    public const double MixCurrentPreTax = 1_350_000d;

    /// <summary>The current year's printed tax: current pre-tax + this = the current net result.</summary>
    public const double MixCurrentTax = -350_000d;

    /// <summary>
    /// A balance whose structure passes and whose result before tax and tax were read from the comparative
    /// column: net_profit_reconciliation fails as a column mix. With <paramref name="recordVerdict"/> false the
    /// check's <c>columnMix</c> is stripped, as a blob written before it was recorded carries it.
    /// </summary>
    public static string ColumnMixBalanceJson(bool recordVerdict = true)
    {
        var root = JsonNode.Parse(NetProfitBalanceJson(MixStoredPreTax, MixStoredTax, MixNetProfit))!.AsObject();
        root.Remove("integrity");
        root["prior_year"] = new JsonObject { ["net_profit"] = MixPriorNetProfit };
        root = JsonNode.Parse(WithIntegrity(root))!.AsObject();

        if (!recordVerdict)
        {
            foreach (var check in root["integrity"]!["checks"]!.AsArray())
                check!.AsObject().Remove("columnMix");
        }

        return root.ToJsonString();
    }

    /// <summary>
    /// The model's reading under a column mix: the net result confirmed on image 1, and the pre-tax result and the
    /// tax as given — located (image + band) or, with <paramref name="located"/> false, with no location at all.
    /// </summary>
    public static BalanceSuggestionAnswer ColumnMixAnswer(double preTax, double tax, bool located = true)
    {
        BalanceSuggestionFieldAnswer Read(string path, double value, double stored, int image, List<double> band, string label) =>
            located
                ? Field(path, value, value == stored, image, band, label)
                : new BalanceSuggestionFieldAnswer { Path = path, PrintedValue = value, MatchesStored = value == stored };

        return new BalanceSuggestionAnswer
        {
            Fields =
            [
                Field("net_profit", MixNetProfit, true, 1, [0.40, 0.44], "Ganancia del ejercicio"),
                Read("income_tax", tax, MixStoredTax, 2, [0.30, 0.34], "Impuesto a las ganancias"),
                Read("result_before_tax", preTax, MixStoredPreTax, 1, [0.30, 0.34], "Resultado antes del impuesto a las ganancias"),
            ],
            Items = [],
        };
    }

    private static JsonObject Item(string label, double amount) => new() { ["label"] = label, ["amount"] = amount };

    private static BalanceSuggestionFieldAnswer Field(
        string path, double? printed, bool matches, int image, List<double> band, string label) => new()
    {
        Path          = path,
        PrintedValue  = printed,
        MatchesStored = matches,
        Image         = image,
        Half          = RenderedImage.FullHalf,
        Band          = band,
        Label         = label,
    };
}
