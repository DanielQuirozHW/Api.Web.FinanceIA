// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-06

using System.Text.Json;
using Api.Web.FinanceIA.Controllers;
using Api.Web.FinanceIA.Models.Financia;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Pins the chat's "CIFRAS DEL BALANCE" block (<see cref="AnalysisController.BuildChatBalanceFiguresBlock"/>):
/// the extracted figures the model needs for inventory, receivables financing and financial-debt questions,
/// rendered from the persisted BalanceJson (flat, in pesos), with the fixed rules after them. Without the
/// block the model estimated inventory from two ratios and called total liabilities "deuda". Every figure
/// and label here is SYNTHETIC.
/// </summary>
public sealed class AnalysisControllerChatBalanceFiguresTests
{
    private const string Header = "CIFRAS DEL BALANCE (leídas del documento):";

    // ── Fixtures ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A persisted-shape BalanceJson. <c>prior_year</c> and the rubros are serialised from the real models,
    /// so a renamed <c>[JsonPropertyName]</c> turns these tests red instead of silently emptying the block.
    /// </summary>
    private static string Blob(
        string? amountsUnit = "miles",
        double? amountsScale = 1000d,
        string? documentKind = null,
        IReadOnlyList<BalanceLineItem>? currentAssetsItems = null,
        BalanceAccounts? priorYear = null,
        bool withScalars = true)
    {
        var root = new Dictionary<string, object?>();

        if (withScalars)
        {
            root["assets"]                  = 1_500_000d;
            root["current_assets"]          = 900_000d;
            root["non_current_assets"]      = 600_000d;
            root["liabilities"]             = 1_000_000d;
            root["current_liabilities"]     = 700_000d;
            root["non_current_liabilities"] = 300_000d;
            root["equity"]                  = 500_000d;
            root["net_revenue"]             = 2_400_000d;
            root["net_profit"]              = 85_000.5d;
            root["inventory"]               = 350_000d;
            root["prepaid_expenses"]        = null;   // skipped
            root["operating_result"]        = 210_000d;
            root["financial_expenses"]      = -60_000d;
            root["result_before_tax"]       = 130_000d;
            root["income_tax"]              = -45_000d;
        }

        root["balanced"]       = true;
        root["difference_pct"] = 0d;
        root["cash_flow"]      = new CashFlowData { Present = false };
        root["amounts_unit"]   = amountsUnit;
        if (amountsScale is not null)
            root["amounts_scale"] = amountsScale;

        root["current_assets_items"] = currentAssetsItems ?? new List<BalanceLineItem>
        {
            new() { Label = "Caja y bancos",          Amount = 120_000d },
            new() { Label = "Créditos por ventas",    Amount = 430_000d },
            new() { Label = "Bienes de cambio",       Amount = 350_000d },
            new() { Label = "Sin importe",            Amount = null },  // skipped
        };
        root["current_liabilities_items"] = new List<BalanceLineItem>
        {
            new() { Label = "Deudas comerciales",     Amount = 420_000d },
            new() { Label = "Préstamos bancarios",    Amount = 180_000d },
            new() { Label = "Cargas fiscales",        Amount = 100_000d },
        };
        root["non_current_liabilities_items"] = null;
        root["prior_year"] = priorYear ?? new BalanceAccounts
        {
            TotalAssets  = 1_300_000d,
            CurrentAssets = 800_000d,
            Equity       = 450_000d,
            NetRevenue   = 2_000_000d,
            NetProfit    = 60_000d,
        };

        if (documentKind is not null)
            root["document_kind"] = documentKind;

        return JsonSerializer.Serialize(root);
    }

    private static string Body(string block)
    {
        Assert.StartsWith(Header + "\n", block, StringComparison.Ordinal);
        Assert.EndsWith("\n" + AnalysisController.ChatBalanceFiguresRules, block, StringComparison.Ordinal);
        return block[(Header.Length + 1)..^(AnalysisController.ChatBalanceFiguresRules.Length + 1)];
    }

    // ── The figures ───────────────────────────────────────────────────────────

    [Fact]
    public void Renders_unit_scalars_rubros_and_prior_year_in_order_skipping_nulls()
    {
        var block = AnalysisController.BuildChatBalanceFiguresBlock(Blob());

        Assert.Equal(
            "Importes en pesos: el documento los expresa en miles de pesos y acá ya están multiplicados por 1000 " +
            "(el resumen ejecutivo puede citarlos en miles de pesos, como están impresos).\n" +
            "Ejercicio cerrado:\n" +
            "- Total del Activo: 1500000\n" +
            "- Total Activo Corriente: 900000\n" +
            "- Total Activo No Corriente: 600000\n" +
            "- Total del Pasivo: 1000000\n" +
            "- Total Pasivo Corriente: 700000\n" +
            "- Total Pasivo No Corriente: 300000\n" +
            "- Total del Patrimonio Neto: 500000\n" +
            "- Ventas netas (Ingresos por ventas): 2400000\n" +
            "- Bienes de cambio: 350000\n" +
            "- Resultado operativo: 210000\n" +
            "- Gastos financieros (Resultados financieros y por tenencia): -60000\n" +
            "- Resultado antes del impuesto a las ganancias: 130000\n" +
            "- Impuesto a las ganancias: -45000\n" +
            "- Resultado del ejercicio / Ganancia (Pérdida) del ejercicio: 85000.5\n" +
            "Rubros del Activo Corriente:\n" +
            "- Caja y bancos: 120000\n" +
            "- Créditos por ventas: 430000\n" +
            "- Bienes de cambio: 350000\n" +
            "Rubros del Pasivo Corriente:\n" +
            "- Deudas comerciales: 420000\n" +
            "- Préstamos bancarios: 180000\n" +
            "- Cargas fiscales: 100000\n" +
            "Rubros del Pasivo No Corriente:\n" +
            "No disponible\n" +
            "Ejercicio anterior:\n" +
            "- Total del Activo: 1300000\n" +
            "- Total Activo Corriente: 800000\n" +
            "- Total del Patrimonio Neto: 450000\n" +
            "- Ventas netas (Ingresos por ventas): 2000000\n" +
            "- Resultado del ejercicio / Ganancia (Pérdida) del ejercicio: 60000",
            Body(block));

        Assert.DoesNotContain("Gastos pagados por adelantado", block, StringComparison.Ordinal);
        Assert.DoesNotContain("Sin importe", block, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("millones", 1_000_000d, "Importes en pesos: el documento los expresa en millones de pesos y acá ya están multiplicados por 1000000")]
    [InlineData("pesos",    1d,         "Importes en pesos, como los expresa el documento.")]
    [InlineData(null,       1d,         "Importes en pesos (el documento no indicó la unidad; se tomaron como pesos).")]
    [InlineData(null,       null,       "Unidad de los importes: no registrada para este análisis; las cifras están tal como se leyeron del documento.")]
    public void States_the_unit_the_figures_are_in(string? unit, double? scale, string expectedLine)
    {
        var block = AnalysisController.BuildChatBalanceFiguresBlock(Blob(amountsUnit: unit, amountsScale: scale));

        Assert.StartsWith(Header + "\n" + expectedLine, block, StringComparison.Ordinal);
    }

    [Fact]
    public void A_trial_balance_says_its_figures_are_normalised()
    {
        var trial = AnalysisController.BuildChatBalanceFiguresBlock(Blob(documentKind: "trial_balance"));
        var plain = AnalysisController.BuildChatBalanceFiguresBlock(Blob());

        const string Line = "Documento: balance de sumas y saldos; las cifras ya están normalizadas";
        Assert.Contains("\n" + Line, trial, StringComparison.Ordinal);
        Assert.DoesNotContain(Line, plain, StringComparison.Ordinal);
        Assert.True(trial.IndexOf(Line, StringComparison.Ordinal) < trial.IndexOf("Ejercicio cerrado:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_missing_prior_year_prints_no_disponible_under_its_heading()
    {
        var json  = Blob().Replace("\"prior_year\":{", "\"prior_year_x\":{", StringComparison.Ordinal);
        var block = AnalysisController.BuildChatBalanceFiguresBlock(json);

        Assert.Contains("\nEjercicio anterior:\nNo disponible\n", block, StringComparison.Ordinal);
    }

    // ── No disponible ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"texto\"")]
    [InlineData("[1,2]")]
    [InlineData("{}")]
    [InlineData("{\"cash_flow\":{\"present\":true,\"operating\":100},\"amounts_unit\":\"miles\",\"amounts_scale\":1000}")]
    [InlineData("{\"assets\":\"1500000\",\"current_assets_items\":[{\"label\":\"Caja\",\"amount\":null}]}")]
    public void Without_figures_the_header_and_the_rules_survive_over_no_disponible(string? balanceJson)
    {
        var block = AnalysisController.BuildChatBalanceFiguresBlock(balanceJson);

        Assert.Equal(Header + "\nNo disponible\n" + AnalysisController.ChatBalanceFiguresRules, block);
    }

    [Fact]
    public void Rubros_alone_are_enough_to_render_the_block()
    {
        var block = AnalysisController.BuildChatBalanceFiguresBlock(Blob(withScalars: false));

        Assert.Contains("Ejercicio cerrado:\nNo disponible\nRubros del Activo Corriente:\n- Caja y bancos: 120000", block, StringComparison.Ordinal);
    }

    // ── Shape ─────────────────────────────────────────────────────────────────

    [Fact]
    public void No_blank_line_inside_the_block_even_for_hostile_labels()
    {
        var block = AnalysisController.BuildChatBalanceFiguresBlock(Blob(currentAssetsItems:
        [
            new() { Label = "Caja\r\n\r\ny bancos",  Amount = 1d },
            new() { Label = "Otros\tcréditos\n",     Amount = 2d },
            new() { Label = "   ",                   Amount = 3d },
            new() { Label = null,                    Amount = 4d },
        ]));

        Assert.DoesNotContain("\n\n", block, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", block, StringComparison.Ordinal);
        Assert.DoesNotContain("\t", block, StringComparison.Ordinal);
        Assert.Contains(
            "Rubros del Activo Corriente:\n- Caja y bancos: 1\n- Otros créditos: 2\n- (sin nombre): 3\n- (sin nombre): 4\n",
            block, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_label_is_clamped_at_a_word_boundary()
    {
        var label = string.Join(" ", Enumerable.Repeat("Préstamo", 22));   // 22×8 + 21 = 197 chars
        var block = AnalysisController.BuildChatBalanceFiguresBlock(Blob(currentAssetsItems:
            [new() { Label = label, Amount = 10d }]));

        var line = block.Split('\n').Single(l => l.EndsWith(": 10", StringComparison.Ordinal));
        var shown = line[2..^": 10".Length];

        Assert.EndsWith("…", shown, StringComparison.Ordinal);
        Assert.True(shown.Length - 1 <= AnalysisController.MaxRubroLabelChars);
        Assert.EndsWith("Préstamo…", shown, StringComparison.Ordinal);   // whole words only
        Assert.Equal(string.Join(" ", Enumerable.Repeat("Préstamo", 13)) + "…", shown);
    }

    [Fact]
    public void A_single_unbroken_token_is_cut_at_the_cap()
    {
        var clamped = AnalysisController.ClampRubroLabel(new string('x', 200));

        Assert.Equal(new string('x', AnalysisController.MaxRubroLabelChars) + "…", clamped);
    }

    [Fact]
    public void A_long_list_is_capped_and_says_how_many_were_left_out()
    {
        var items = Enumerable.Range(1, 45)
            .Select(i => new BalanceLineItem { Label = $"Rubro {i}", Amount = i })
            .ToList();

        var block = AnalysisController.BuildChatBalanceFiguresBlock(Blob(currentAssetsItems: items));
        var lines = block.Split('\n');
        var start = Array.IndexOf(lines, "Rubros del Activo Corriente:");

        Assert.Equal("- Rubro 1: 1",   lines[start + 1]);
        Assert.Equal("- Rubro 40: 40", lines[start + AnalysisController.MaxRubrosPerList]);
        Assert.Equal("- (y 5 rubros más no listados; se muestran los primeros 40)", lines[start + 41]);
        Assert.Equal("Rubros del Pasivo Corriente:", lines[start + 42]);
        Assert.DoesNotContain("Rubro 41", block, StringComparison.Ordinal);
    }

    // ── The rules ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_rules_paragraph_follows_the_figures_in_both_branches(bool withFigures)
    {
        var block = AnalysisController.BuildChatBalanceFiguresBlock(withFigures ? Blob() : null);

        Assert.EndsWith("\n" + AnalysisController.ChatBalanceFiguresRules, block, StringComparison.Ordinal);
        Assert.Contains(
            "NO están disponibles el costo de ventas, las compras, las amortizaciones y depreciaciones del ejercicio " +
            "ni los rubros detallados del ejercicio anterior (créditos por ventas, deudas comerciales, deudas bancarias y demás)",
            block, StringComparison.Ordinal);
        Assert.Contains("indicá exactamente qué cifra falta y pedile al analista que la aporte; nunca la aproximes", block, StringComparison.Ordinal);

        // Rule 2: prior-year totals are present, so an average over both years is allowed.
        Assert.Contains(
            "Una cifra que figura en los dos ejercicios, bajo \"Ejercicio cerrado:\" y bajo \"Ejercicio anterior:\", se puede promediar.",
            block, StringComparison.Ordinal);

        // Rule 3: arithmetic on figures in the prompt is allowed and declared; inferring from ratios is not.
        Assert.Contains(
            "Está permitido calcular con cifras que estén en los bloques de este prompt (las cifras del balance, " +
            "los ratios, el resumen ejecutivo), siempre que digas que es un cálculo y qué cifras usaste.",
            block, StringComparison.Ordinal);
        Assert.Contains("Nunca infieras un rubro a partir de ratios", block, StringComparison.Ordinal);
        Assert.Contains("ni completes con una estimación o un valor verosímil una cifra que no está en ningún bloque", block, StringComparison.Ordinal);
        Assert.DoesNotContain("Nunca obtengas un rubro por diferencia", block, StringComparison.Ordinal);
        Assert.Contains("nunca el pasivo total", block, StringComparison.Ordinal);
        Assert.Contains("controles de consistencia", block, StringComparison.Ordinal);
    }

    [Fact]
    public void The_rules_paragraph_is_pinned_verbatim()
    {
        // A literal, not the constant: a wording change to the model's rules must be a deliberate test edit.
        Assert.Equal(
            "(Estas cifras son las que la lectura automática tomó del documento, con las correcciones manuales " +
            "que haya registrado un analista. Si CALIDAD DE LOS DATOS indica que la lectura del balance no superó " +
            "los controles de consistencia, decilo cada vez que las uses. En este análisis NO están disponibles el " +
            "costo de ventas, las compras, las amortizaciones y depreciaciones del ejercicio ni los rubros " +
            "detallados del ejercicio anterior (créditos por ventas, deudas comerciales, deudas bancarias y demás): " +
            "si una pregunta los necesita —días de inventario, días de pago a proveedores, EBITDA— indicá " +
            "exactamente qué cifra falta y pedile al analista que la aporte; nunca la aproximes. Una cifra que " +
            "figura en los dos ejercicios, bajo \"Ejercicio cerrado:\" y bajo \"Ejercicio anterior:\", se puede " +
            "promediar. Está permitido calcular con cifras que estén en los bloques de este prompt (las cifras del " +
            "balance, los ratios, el resumen ejecutivo), siempre que digas que es un cálculo y qué cifras usaste. " +
            "Nunca infieras un rubro a partir de ratios (por ejemplo, los bienes de cambio como diferencia entre la " +
            "liquidez corriente y la prueba ácida) ni completes con una estimación o un valor verosímil una cifra " +
            "que no está en ningún bloque. La deuda financiera es la deuda bancaria, financiera y concursal que " +
            "figura en los rubros, nunca el pasivo total; si la naturaleza de un rubro no surge clara de su " +
            "nombre, decilo.)",
            AnalysisController.ChatBalanceFiguresRules);
        Assert.DoesNotContain("\n", AnalysisController.ChatBalanceFiguresRules, StringComparison.Ordinal);
    }

    // ── In the system prompt ──────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_block_sits_between_the_ratios_and_the_cash_flow_as_its_own_block(bool withFigures)
    {
        var result = new AnalysisResult { BalanceJson = withFigures ? Blob() : null, RatiosJson = "{\"current_ratio\":1.29}" };

        var prompt = AnalysisController.BuildChatSystemPrompt(
            result, [], hasAttachment: false, reliability: null, fiscalYear: null);

        var block = AnalysisController.BuildChatBalanceFiguresBlock(result.BalanceJson);
        Assert.Contains("RATIOS FINANCIEROS:\n{\"current_ratio\":1.29}\n\n" + block + "\n\nFLUJO DE EFECTIVO:", prompt, StringComparison.Ordinal);
    }
}
