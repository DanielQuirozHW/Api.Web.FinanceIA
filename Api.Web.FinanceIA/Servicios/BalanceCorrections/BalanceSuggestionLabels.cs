// Project : Api.Web.FinanceIA
// Created : 2026-11-03

namespace Api.Web.FinanceIA.Servicios.BalanceCorrections;

/// <summary>
/// Fixed Spanish texts of the correction suggestion: the label each correctable figure is usually printed
/// under on an Argentine balance (a HINT for the model, never read from the document), the name of each
/// rubro list, and the es-AR phrase that names a failed structure check without quoting a figure.
/// </summary>
public static class BalanceSuggestionLabels
{
    /// <summary>The printed-label hint of every top-level correctable figure (BalanceCorrectionPath.TopLevelKeys).</summary>
    public static readonly IReadOnlyDictionary<string, string> FieldLabels =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["current_assets"]          = "Total Activo Corriente",
            ["non_current_assets"]      = "Total Activo No Corriente",
            ["assets"]                  = "Total del Activo",
            ["current_liabilities"]     = "Total Pasivo Corriente",
            ["non_current_liabilities"] = "Total Pasivo No Corriente",
            ["liabilities"]             = "Total del Pasivo",
            ["equity"]                  = "Total del Patrimonio Neto",
            ["net_revenue"]             = "Ventas netas (Ingresos por ventas)",
            ["net_profit"]              = "Resultado del ejercicio / Ganancia (Pérdida) del ejercicio",
            ["operating_result"]        = "Resultado operativo",
            ["financial_expenses"]      = "Gastos financieros (Resultados financieros y por tenencia)",
            ["inventory"]               = "Bienes de cambio",
            ["prepaid_expenses"]        = "Gastos pagados por adelantado",
            ["result_before_tax"]       = "Resultado antes del impuesto a las ganancias",
            ["income_tax"]              = "Impuesto a las ganancias",
        };

    /// <summary>The Spanish name of each rubro list (BalanceCorrectionPath.ItemArrays).</summary>
    public static readonly IReadOnlyDictionary<string, string> ItemGroupLabels =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["current_assets_items"]          = "rubros del Activo Corriente",
            ["current_liabilities_items"]     = "rubros del Pasivo Corriente",
            ["non_current_liabilities_items"] = "rubros del Pasivo No Corriente",
        };

    /// <summary>
    /// The es-AR phrase of each check a suggestion targets (the structure checks and
    /// net_profit_reconciliation), lower-case, no figure, for "El balance presentado no cierra: …".
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> CheckPhrases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BalanceIntegrityService.AssetsComponentsCheck]           = "el activo total no coincide con la suma del activo corriente y el activo no corriente",
            [BalanceIntegrityService.LiabilitiesComponentsCheck]      = "el pasivo total no coincide con la suma del pasivo corriente y el pasivo no corriente",
            [BalanceIntegrityService.AccountingEquationCheck]         = "el activo total no coincide con la suma del pasivo total y el patrimonio neto",
            [BalanceIntegrityService.CurrentAssetsItemsCheck]         = "la suma de los rubros del activo corriente no coincide con el activo corriente",
            [BalanceIntegrityService.CurrentLiabilitiesItemsCheck]    = "la suma de los rubros del pasivo corriente no coincide con el pasivo corriente",
            [BalanceIntegrityService.NonCurrentLiabilitiesItemsCheck] = "la suma de los rubros del pasivo no corriente no coincide con el pasivo no corriente",
            [BalanceIntegrityService.ColumnSwapCheck]                 = "una cifra del ejercicio coincide con otra del ejercicio anterior",
            [BalanceIntegrityService.NetProfitReconciliationCheck]    = "el resultado del ejercicio no coincide con el resultado antes del impuesto a las ganancias neto de ese impuesto",
        };
}
