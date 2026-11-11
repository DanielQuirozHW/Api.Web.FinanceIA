// Project : Api.Web.FinanceIA
// Created : 2026-11-08

namespace Api.Web.FinanceIA.Servicios.Pdf;

/// <summary>
/// The readable title of a risk alert — never its raw CODE_IN_CAPS. In order:
/// <list type="number">
///   <item>the tenant's own <c>AlertRuleCatalogue.DisplayNameEs</c> for that code, when the tenant has a catalogue
///   row for it (read once per export by ExportController; the deterministic engine writes the same name into the
///   alerts it raises, so the PDF and the alerts screen agree);</item>
///   <item>a small curated map, a port of the Portal's <c>lib/alert-codes.ts</c> — it also carries the DDL 038
///   catalogue names, so a tenant with no catalogue rows still reads them;</item>
///   <item>the title the model wrote (prompt 2.7+);</item>
///   <item>the code humanised in sentence case, with its Spanish accents restored.</item>
/// </list>
/// </summary>
internal static class AlertTitles
{
    private static readonly IReadOnlyDictionary<string, string> Curated =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // lib/alert-codes.ts:28-34
            ["HIGH_BANK_DEBT_NONCURRENT"]     = "Deuda bancaria no corriente elevada",
            ["LARGE_CONSIGNMENT_OFF_BALANCE"] = "Consignaciones significativas fuera de balance",
            ["INFLATION_ADJUSTMENT_RT6"]      = "Ajuste por inflación (RT 6)",
            ["AFIP_PAYMENT_PLAN"]             = "Plan de pagos con AFIP",
            ["CASH_FLOW_HEADER_MISMATCH"]     = "Inconsistencia en el encabezado del flujo de efectivo",

            // AlertRuleEngine catalogue codes — DisplayNameEs of docs/database/038_alert_rule_catalogue.sql
            ["PASIVO_100_CORRIENTE"]               = "Pasivo íntegramente corriente",
            ["INVENTARIO_ELEVADO"]                 = "Participación elevada de bienes de cambio",
            ["ENDEUDAMIENTO_ELEVADO"]              = "Endeudamiento elevado",
            ["COBERTURA_INTERESES_INDETERMINABLE"] = "Cobertura de intereses indeterminable",
            ["FLUJO_NO_CONCILIA"]                  = "El estado de flujo de efectivo no concilia",
            ["FLUJO_FINANCIACION_CERO"]            = "Actividades de financiación en cero",
        };

    // Unaccented words an UPPER_SNAKE code spells that Spanish writes with an accent. Words ending in -cion /
    // -sion are handled by rule (every one of them takes the accent), so they are not listed here.
    private static readonly IReadOnlyDictionary<string, string> Accented =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["caida"]      = "caída",
            ["caidas"]     = "caídas",
            ["credito"]    = "crédito",
            ["creditos"]   = "créditos",
            ["deficit"]    = "déficit",
            ["perdida"]    = "pérdida",
            ["perdidas"]   = "pérdidas",
            ["economico"]  = "económico",
            ["economica"]  = "económica",
            ["critico"]    = "crítico",
            ["critica"]    = "crítica",
            ["criticos"]   = "críticos",
            ["minimo"]     = "mínimo",
            ["minima"]     = "mínima",
            ["maximo"]     = "máximo",
            ["maxima"]     = "máxima",
            ["analisis"]   = "análisis",
            ["ultimo"]     = "último",
            ["ultima"]     = "última",
            ["ultimos"]    = "últimos",
            ["mas"]        = "más",
            ["rapido"]     = "rápido",
            ["rapida"]     = "rápida",
            ["numero"]     = "número",
            ["liquido"]    = "líquido",
            ["liquida"]    = "líquida",
            ["deposito"]   = "depósito",
            ["depositos"]  = "depósitos",
            ["periodo"]    = "período",
            ["dias"]       = "días",
            ["indice"]     = "índice",
            ["indices"]    = "índices",
            ["tecnico"]    = "técnico",
            ["tecnica"]    = "técnica",
            ["publico"]    = "público",
            ["sintesis"]   = "síntesis",
            ["calculo"]    = "cálculo",
            ["capitulo"]   = "capítulo",
            ["deficits"]   = "déficits",
            ["tambien"]    = "también",
            ["unico"]      = "único",
            ["unica"]      = "única",
            ["practica"]   = "práctica",
            ["politica"]   = "política",
            ["prestamo"]   = "préstamo",
            ["prestamos"]  = "préstamos",
            ["dolar"]      = "dólar",
            ["dolares"]    = "dólares",
        };

    /// <summary>The title to print for an alert, or null when it has neither a usable title nor a code.</summary>
    /// <param name="code">The alert's code as stored.</param>
    /// <param name="modelTitle">The title the alert carries, if any.</param>
    /// <param name="catalogueNames">This tenant's catalogue display names by code; null or empty when unknown.</param>
    internal static string? Resolve(string? code, string? modelTitle, IReadOnlyDictionary<string, string>? catalogueNames = null)
    {
        string trimmedCode = code?.Trim() ?? string.Empty;

        if (trimmedCode.Length > 0
            && catalogueNames is not null
            && TryGetIgnoreCase(catalogueNames, trimmedCode, out string? displayName)
            && !string.IsNullOrWhiteSpace(displayName))
        {
            return displayName.Trim();
        }

        if (trimmedCode.Length > 0 && Curated.TryGetValue(trimmedCode, out string? curated))
            return curated;

        if (!string.IsNullOrWhiteSpace(modelTitle))
            return modelTitle.Trim();

        return trimmedCode.Length == 0 ? null : Humanize(trimmedCode);
    }

    /// <summary>"VENTAS_EN_CAIDA" → "Ventas en caída": underscores to spaces, sentence case, accents restored.</summary>
    internal static string Humanize(string code)
    {
        string[] words = code
            .Split(['_', '-', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => Accent(word.ToLowerInvariant()))
            .ToArray();

        string spaced = string.Join(' ', words);
        return spaced.Length == 0 ? spaced : char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }

    private static string Accent(string word)
    {
        if (Accented.TryGetValue(word, out string? accented))
            return accented;

        // situacion → situación, emision → emisión; the plural (-ciones) carries no accent.
        if (word.Length > 4 && (word.EndsWith("cion", StringComparison.Ordinal) || word.EndsWith("sion", StringComparison.Ordinal)))
            return word[..^2] + "ón";

        return word;
    }

    // The caller's map may have been built with any comparer; codes are matched ignoring case either way.
    private static bool TryGetIgnoreCase(IReadOnlyDictionary<string, string> map, string key, out string? value)
    {
        if (map.TryGetValue(key, out value))
            return true;

        foreach (KeyValuePair<string, string> pair in map)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = null;
        return false;
    }
}
