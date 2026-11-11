// Project : Api.Web.FinanceIA
// Created : 2026-10-20

using System.Globalization;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Defaults of the persona humana evaluation thresholds and decision rules (M10 Phase 4a) — the
/// <c>pf_</c> keys of [dbo].[ScoringConfig]. The same pattern as <see cref="ScoringDefaults"/>: DB-driven
/// with a full code fallback, so a tenant with no <c>pf_</c> row evaluates on these values. Read
/// <see cref="Values"/> for the current key set.
/// </summary>
/// <remarks>
/// EVERY VALUE IS PROVISIONAL. No cut has a source for a persona humana PyME yet; they await the
/// calibration of Phase 6. Only the 50 % cuota/ingreso cut has a direct source (the BCRA's ceiling for
/// personal scoring). The keys are accepted and validated by <c>SettingsService</c> and surfaced by GET
/// scoring-config as its own <c>personaHumana</c> array, laid out by <see cref="Layout"/>, never mixed into
/// the balance groups. Every value must fit [dbo].[ScoringConfig].[ConfigValue], DECIMAL(10,4): peso
/// amounts are therefore in THOUSANDS (<c>_miles</c>).
/// <para>
/// Engine 0.6 retired <c>pf_plazo_estimado_deuda_meses</c> (the existing installments are now a share of the
/// Central de Deudores balance, <c>pf_cuota_estimada_pct_saldo</c>). A stored row of it is inert: every reader
/// filters the tenant rows by <see cref="IsKey"/>, and a PUT of it is refused as an unknown key.
/// </para>
/// <para>
/// Engine 0.9 split the investments by class: pf_haircut_titulo_publico, pf_haircut_accion_on (acciones and ON con
/// cotización) and pf_haircut_fci. pf_haircut_inversion keeps its key and default — tenant overrides survive — as the
/// haircut of an investment that is not classified (Otra, or no class).
/// </para>
/// </remarks>
public static class PersonEvaluationDefaults
{
    /// <summary>The prefix every key of this class carries.</summary>
    public const string KeyPrefix = "pf_";

    private static readonly Dictionary<string, (decimal Value, string Description)> Entries = new()
    {
        // Pre-filters
        ["pf_requiere_certificado_mipyme"]      = ( 1.0000m, "PROVISIONAL. Certificado MiPyME faltante o vencido a la fecha de evaluación: 1 = rechazo sugerido, 0 = alerta"),
        ["pf_certificado_alerta_desde_mes"]     = ( 5.0000m, "PROVISIONAL. Mes (1-12) desde el cual un certificado vencido el 30/04 de ese año se marca como alerta de vigencia (renovación pendiente) en lugar de vencido"),
        ["pf_certificado_alerta_hasta_mes"]     = ( 8.0000m, "PROVISIONAL. Último mes (1-12, no menor que pf_certificado_alerta_desde_mes) en que un certificado vencido el 30/04 de ese año sigue siendo alerta de vigencia; después cuenta como vencido"),
        ["pf_bcra_sit_rechazo"]                 = ( 3.0000m, "PROVISIONAL. Situación BCRA (1-5) desde la cual el filtro de la Central de Deudores sugiere rechazo, vigente o dentro de la ventana"),
        ["pf_bcra_ventana_meses"]               = (12.0000m, "PROVISIONAL. Meses hacia atrás que mira el filtro de situación (el BCRA informa 12)"),
        ["pf_bcra_eventos_ventana_meses"]       = (24.0000m, "PROVISIONAL. Meses hacia atrás para situación 5, situación jurídica y refinanciaciones (alerta fuerte)"),
        ["pf_cheques_pagados_alerta_meses"]     = (12.0000m, "PROVISIONAL. Meses hacia atrás en los que un cheque rechazado sin fondos, ya pagado, es alerta"),
        ["pf_antiguedad_rechazo_meses"]         = (12.0000m, "PROVISIONAL. Antigüedad de la actividad en meses por debajo de la cual se sugiere rechazo, solo con una fecha de inicio real; la aproximación por el período de alta más antiguo en ARCA llega como máximo a alerta fuerte"),
        ["pf_antiguedad_min_meses"]             = (24.0000m, "PROVISIONAL. Antigüedad mínima de la actividad en meses; por debajo, alerta"),
        ["pf_ventas_acreedor_fisco_tolerancia"] = ( 0.1000m, "PROVISIONAL. Diferencia relativa tolerada entre las ventas declaradas al acreedor (certificación, manifestación) y al fisco; por encima, alerta fuerte"),

        // Indicators
        ["pf_consumo_mensual_minimo_miles"]     = (2450.0000m, "PROVISIONAL. Piso de consumo personal mensual en MILES de pesos del mes base pf_consumo_mes_base (canasta de referencia; 2450 = $ 2.450.000); se reexpresa por IPC al mes de referencia de la evaluación. 0 = sin definir"),
        ["pf_consumo_mes_base"]                 = (202609.0000m, "PROVISIONAL. Mes (AAAAMM) en cuyos pesos está expresado el piso de consumo; la evaluación lo indexa por IPC hasta su mes de referencia y, sin IPC para alguno de los dos meses, usa el piso nominal"),
        ["pf_cuota_ingreso_bajo"]               = ( 0.2500m, "PROVISIONAL. Cuota/ingreso hasta este corte: banda verde"),
        ["pf_cuota_ingreso_medio"]              = ( 0.3500m, "PROVISIONAL. Cuota/ingreso hasta este corte: banda amarilla"),
        ["pf_cuota_ingreso_alto"]               = ( 0.5000m, "PROVISIONAL. Cuota/ingreso hasta este corte: banda naranja; por encima, roja (50 % = tope del BCRA para scoring de personas)"),
        ["pf_cuota_estimada_pct_saldo"]         = ( 0.0500m, "PROVISIONAL. Fracción del saldo total de la Central de Deudores que se estima como cuotas vigentes mensuales (0,05 = 5 % del saldo por mes)"),
        ["pf_dscr_rojo"]                        = ( 1.0000m, "PROVISIONAL. Cobertura del servicio de deuda por debajo de este valor: banda roja"),
        ["pf_dscr_verde"]                       = ( 1.2500m, "PROVISIONAL. Cobertura del servicio de deuda desde este valor: banda verde; entre el corte rojo y este, amarilla"),
        ["pf_agro_modo_anual"]                  = ( 1.0000m, "PROVISIONAL. Evaluación anual de la persona humana cuya actividad principal en ARCA es agropecuaria (CLAE 01, 02 o 03): ventana de ventas alineada a la campaña, cuota según su periodicidad y sin alertas por estacionalidad (1 = sí, 0 = no). El analista puede forzarla en cada análisis"),
        ["pf_agro_cuotas_por_anio"]             = ( 2.0000m, "PROVISIONAL. Cuotas por año que se asumen para una operación agropecuaria cuando el análisis no informa la periodicidad (1 = anual o al vencimiento, 2 = semestral, 4 = trimestral, 12 = mensual)"),
        ["pf_agro_mes_inicio_campania"]         = ( 7.0000m, "PROVISIONAL. Mes (1-12) en que empieza la campaña agropecuaria (7 = campaña julio-junio); la ventana de ventas de 12 meses se alinea a la última campaña completa"),
        ["pf_patrimonio_operativo_alerta_pct"]  = ( 0.7000m, "PROVISIONAL. Peso de los bienes de la actividad sobre el patrimonio neto desde el cual se marca alerta"),

        // Coverage
        ["pf_doc_antiguedad_max_meses"]         = ( 6.0000m, "PROVISIONAL. Antigüedad máxima en meses de la manifestación de bienes, la certificación de ingresos y las DDJJ mensuales"),
        ["pf_ddjj_anual_antiguedad_max_meses"]  = (18.0000m, "PROVISIONAL. Antigüedad máxima en meses de las DDJJ anuales (Ganancias, Bienes Personales, REIBP)"),
        ["pf_informe_max_dias"]                 = (90.0000m, "PROVISIONAL. Antigüedad máxima en días de los informes externos (BCRA, ARCA, LUFE) del legajo"),

        // Decision rules
        ["pf_aprobacion_requiere_motivo"]       = ( 1.0000m, "PROVISIONAL. Aprobar un análisis de persona humana exige un motivo (1 = sí, 0 = no)"),
        ["pf_rechazo_sugerido_requiere_preaprobacion"] = ( 1.0000m, "PROVISIONAL. Aprobar un análisis con rechazo sugerido pasa siempre por pre-aprobación y quórum, sin importar el monto (1 = sí, 0 = no)"),
        ["pf_contragarantia_cobertura_min_pct"] = (100.0000m, "PROVISIONAL. Cobertura mínima de la contragarantía sobre el aval, en % (0-100). Mayor a 0: al aprobar un análisis de persona humana ninguna operación puede ser a sola firma"),

        // Guarantees (fiadores, M10 Phase 5a-2)
        ["pf_cobertura_rojo"]                   = ( 1.0000m, "PROVISIONAL. Cobertura patrimonial ajustada del fiador (patrimonio ejecutable / monto a su cargo) por debajo de este valor: banda roja"),
        ["pf_cobertura_verde"]                  = ( 2.0000m, "PROVISIONAL. Cobertura patrimonial ajustada del fiador desde este valor: banda verde; entre el corte rojo y este, amarilla"),
        ["pf_cuota_ingreso_estres_max"]         = ( 0.5000m, "PROVISIONAL. Cuota/ingreso de estrés del fiador (cuotas vigentes estimadas más la cuota COMPLETA de la operación sobre su ingreso mensual) por encima de este corte: alerta"),
        ["pf_haircut_inmueble"]                 = ( 0.5000m, "PROVISIONAL. Descuento sobre el valor de un inmueble que no es vivienda para llegar a su valor ejecutable"),
        ["pf_haircut_inmueble_vivienda"]        = ( 0.2500m, "PROVISIONAL. Descuento sobre el valor de la vivienda, que solo se computa con asentimiento conyugal o si es oponible (inoponibilidad); sin eso no es ejecutable"),
        ["pf_haircut_rodado"]                   = ( 0.4000m, "PROVISIONAL. Descuento sobre el valor de un rodado"),
        ["pf_haircut_participacion"]            = ( 0.6000m, "PROVISIONAL. Descuento sobre el valor de una participación societaria"),
        ["pf_haircut_deposito"]                 = ( 0.0000m, "PROVISIONAL. Descuento sobre el valor de un depósito (cuenta, plazo fijo); el descuento por descalce de moneda se suma si corresponde"),
        ["pf_haircut_inversion"]                = ( 0.3000m, "PROVISIONAL. Descuento sobre inversiones sin clasificar (cuando el documento no permite saber si son títulos públicos, acciones, obligaciones negociables o FCI)"),
        ["pf_haircut_titulo_publico"]           = ( 0.1500m, "PROVISIONAL. Descuento sobre el valor de títulos públicos (bonos, letras del Tesoro, títulos provinciales o municipales); el descuento por descalce de moneda se suma si corresponde"),
        ["pf_haircut_accion_on"]                = ( 0.3000m, "PROVISIONAL. Descuento sobre el valor de acciones con cotización y obligaciones negociables con cotización; el descuento por descalce de moneda se suma si corresponde"),
        ["pf_haircut_fci"]                      = ( 0.3000m, "PROVISIONAL. Descuento sobre el valor de cuotapartes de fondos comunes de inversión; el descuento por descalce de moneda se suma si corresponde"),
        ["pf_haircut_credito"]                  = ( 1.0000m, "PROVISIONAL. Descuento sobre el valor de un crédito a favor de la persona; 1 = no se computa (no es ejecutable sin documentación)"),
        ["pf_haircut_otro"]                     = ( 1.0000m, "PROVISIONAL. Descuento sobre el valor de otros bienes; 1 = no se computan (bien no admitido)"),
        ["pf_haircut_descalce_moneda"]          = ( 0.0800m, "PROVISIONAL. Descuento adicional cuando la moneda del bien difiere de la moneda de la operación; se suma al descuento del bien"),
        ["pf_ratio_mercado_fiscal_alerta"]      = (30.0000m, "PROVISIONAL. Veces que el valor de la manifestación puede superar al valor fiscal (Bienes Personales) de un bien del fiador antes de marcar alerta"),
    };

    /// <summary>Key → default numeric value.</summary>
    public static readonly IReadOnlyDictionary<string, decimal> Values =
        Entries.ToDictionary(e => e.Key, e => e.Value.Value);

    /// <summary>Key → human-readable description.</summary>
    public static readonly IReadOnlyDictionary<string, string> Descriptions =
        Entries.ToDictionary(e => e.Key, e => e.Value.Description);

    /// <summary>Returns the default value for <paramref name="key"/>.</summary>
    public static decimal Get(string key) => Values[key];

    /// <summary>True when <paramref name="key"/> is one of this class's keys.</summary>
    public static bool IsKey(string? key) => key is not null && Values.ContainsKey(key);

    // ── Key groups (used by SettingsService for validation) ──────────────────────

    /// <summary>Keys that must be whole numbers, with their inclusive bounds.</summary>
    public static readonly IReadOnlyDictionary<string, (int Min, int Max)> IntegerKeys = new Dictionary<string, (int Min, int Max)>
    {
        ["pf_certificado_alerta_desde_mes"]    = (1, 12),
        ["pf_certificado_alerta_hasta_mes"]    = (1, 12),
        ["pf_bcra_sit_rechazo"]                = (1, 5),
        ["pf_bcra_ventana_meses"]              = (1, 12),
        ["pf_bcra_eventos_ventana_meses"]      = (1, 24),
        ["pf_cheques_pagados_alerta_meses"]    = (0, 60),
        ["pf_antiguedad_rechazo_meses"]        = (0, 240),
        ["pf_antiguedad_min_meses"]            = (0, 240),
        ["pf_consumo_mensual_minimo_miles"]    = (0, 999_999),
        ["pf_doc_antiguedad_max_meses"]        = (1, 60),
        ["pf_ddjj_anual_antiguedad_max_meses"] = (1, 60),
        ["pf_informe_max_dias"]                = (1, 365),
        ["pf_contragarantia_cobertura_min_pct"] = (0, 100),
        ["pf_ratio_mercado_fiscal_alerta"]     = (1, 1000),
        ["pf_agro_cuotas_por_anio"]            = (1, 12),
        ["pf_agro_mes_inicio_campania"]        = (1, 12),
    };

    /// <summary>
    /// The cuotas a year a persona humana operation can have: annual or single payment at maturity, semiannual,
    /// quarterly or monthly. Shared by pf_agro_cuotas_por_anio and the request's cuotasPorAnio.
    /// </summary>
    public static readonly IReadOnlyList<int> AllowedCuotasPorAnio = [1, 2, 4, 12];

    /// <summary>Integer keys that, inside their <see cref="IntegerKeys"/> bounds, admit only a closed set of values.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<int>> AllowedValueKeys =
        new Dictionary<string, IReadOnlyList<int>>
        {
            ["pf_agro_cuotas_por_anio"] = AllowedCuotasPorAnio,
        };

    /// <summary>"1, 2, 4 o 12": an allowed set as an es-AR list.</summary>
    public static string EsArList(IReadOnlyList<int> values)
        => values.Count == 1
            ? values[0].ToString(CultureInfo.InvariantCulture)
            : string.Join(", ", values.Take(values.Count - 1).Select(v => v.ToString(CultureInfo.InvariantCulture)))
              + " o " + values[^1].ToString(CultureInfo.InvariantCulture);

    /// <summary>Keys that are 0 (off) or 1 (on).</summary>
    public static readonly string[] FlagKeys =
    [
        "pf_requiere_certificado_mipyme", "pf_aprobacion_requiere_motivo", "pf_rechazo_sugerido_requiere_preaprobacion",
        "pf_agro_modo_anual"
    ];

    /// <summary>Keys that are fractions in [0, 1].</summary>
    public static readonly string[] FractionKeys =
    [
        "pf_cuota_ingreso_bajo", "pf_cuota_ingreso_medio", "pf_cuota_ingreso_alto",
        "pf_ventas_acreedor_fisco_tolerancia", "pf_patrimonio_operativo_alerta_pct",
        "pf_cuota_ingreso_estres_max",
        "pf_haircut_inmueble", "pf_haircut_inmueble_vivienda", "pf_haircut_rodado", "pf_haircut_participacion",
        "pf_haircut_deposito", "pf_haircut_inversion", "pf_haircut_titulo_publico", "pf_haircut_accion_on",
        "pf_haircut_fci", "pf_haircut_credito", "pf_haircut_otro",
        "pf_haircut_descalce_moneda"
    ];

    /// <summary>Fractions with bounds narrower than [0, 1], inclusive.</summary>
    public static readonly IReadOnlyDictionary<string, (decimal Min, decimal Max)> BoundedFractionKeys =
        new Dictionary<string, (decimal Min, decimal Max)>
        {
            ["pf_cuota_estimada_pct_saldo"] = (0.01m, 0.20m),
        };

    /// <summary>Keys that are a month as a whole number AAAAMM, from <see cref="YearMonthMin"/> to the current month.</summary>
    public static readonly string[] YearMonthKeys = ["pf_consumo_mes_base"];

    /// <summary>Earliest month any <see cref="YearMonthKeys"/> key accepts.</summary>
    public const int YearMonthMin = 202001;

    /// <summary><paramref name="date"/>'s month as a whole number AAAAMM.</summary>
    public static int ToYearMonth(DateOnly date) => date.Year * 100 + date.Month;

    /// <summary>The debt-service coverage bands, bounded to [<see cref="DscrMin"/>, <see cref="DscrMax"/>] with verde above rojo.</summary>
    public static readonly string[] DscrKeys = ["pf_dscr_rojo", "pf_dscr_verde"];

    /// <summary>Lower bound of every <see cref="DscrKeys"/> key.</summary>
    public const decimal DscrMin = 0m;

    /// <summary>Upper bound of every <see cref="DscrKeys"/> key.</summary>
    public const decimal DscrMax = 10m;

    /// <summary>
    /// The fiador's adjusted patrimonial coverage bands, bounded to [<see cref="CoberturaMin"/>,
    /// <see cref="CoberturaMax"/>] with verde above rojo.
    /// </summary>
    public static readonly string[] CoberturaKeys = ["pf_cobertura_rojo", "pf_cobertura_verde"];

    /// <summary>Lower bound of every <see cref="CoberturaKeys"/> key.</summary>
    public const decimal CoberturaMin = 0m;

    /// <summary>Upper bound of every <see cref="CoberturaKeys"/> key.</summary>
    public const decimal CoberturaMax = 10m;

    /// <summary>The three cuota/ingreso cuts, which must be strictly ascending.</summary>
    public static readonly string[] CuotaIngresoCutKeys =
    [
        "pf_cuota_ingreso_bajo", "pf_cuota_ingreso_medio", "pf_cuota_ingreso_alto"
    ];

    // ── Settings UI layout (GET scoring-config → personaHumana) ─────────────────

    /// <summary>
    /// Every key in the order of <see cref="Entries"/>, with the Settings UI section it belongs to
    /// (<c>elegibilidad</c>, <c>bcra</c>, <c>capacidad</c>, <c>solvencia</c>, <c>documentacion</c>,
    /// <c>decision</c>, <c>garantias</c>) and its unit (<c>meses</c>, <c>dias</c>, <c>miles_ars</c>, <c>fraccion</c>,
    /// <c>veces</c>, <c>yyyymm</c>, or null). An explicit list, so the order never depends on how a dictionary
    /// enumerates. The two certificate months are a month of the year, not a duration, and the
    /// counter-guarantee coverage is a whole percent: none of the units fits them, so they carry null. Neither
    /// does the agro flag, the cuotas a year (a count, from a closed set) nor the campaign's first month.
    /// </summary>
    public static readonly IReadOnlyList<(string Key, string Group, string? Unit)> Layout =
    [
        ("pf_requiere_certificado_mipyme",             "elegibilidad",  null),
        ("pf_certificado_alerta_desde_mes",            "elegibilidad",  null),
        ("pf_certificado_alerta_hasta_mes",            "elegibilidad",  null),
        ("pf_bcra_sit_rechazo",                        "bcra",          null),
        ("pf_bcra_ventana_meses",                      "bcra",          "meses"),
        ("pf_bcra_eventos_ventana_meses",              "bcra",          "meses"),
        ("pf_cheques_pagados_alerta_meses",            "bcra",          "meses"),
        ("pf_antiguedad_rechazo_meses",                "elegibilidad",  "meses"),
        ("pf_antiguedad_min_meses",                    "elegibilidad",  "meses"),
        ("pf_ventas_acreedor_fisco_tolerancia",        "elegibilidad",  "fraccion"),
        ("pf_consumo_mensual_minimo_miles",            "capacidad",     "miles_ars"),
        ("pf_consumo_mes_base",                        "capacidad",     "yyyymm"),
        ("pf_cuota_ingreso_bajo",                      "capacidad",     "fraccion"),
        ("pf_cuota_ingreso_medio",                     "capacidad",     "fraccion"),
        ("pf_cuota_ingreso_alto",                      "capacidad",     "fraccion"),
        ("pf_cuota_estimada_pct_saldo",                "capacidad",     "fraccion"),
        ("pf_dscr_rojo",                               "capacidad",     "veces"),
        ("pf_dscr_verde",                              "capacidad",     "veces"),
        ("pf_agro_modo_anual",                         "capacidad",     null),
        ("pf_agro_cuotas_por_anio",                    "capacidad",     null),
        ("pf_agro_mes_inicio_campania",                "capacidad",     null),
        ("pf_patrimonio_operativo_alerta_pct",         "solvencia",     "fraccion"),
        ("pf_doc_antiguedad_max_meses",                "documentacion", "meses"),
        ("pf_ddjj_anual_antiguedad_max_meses",         "documentacion", "meses"),
        ("pf_informe_max_dias",                        "documentacion", "dias"),
        ("pf_aprobacion_requiere_motivo",              "decision",      null),
        ("pf_rechazo_sugerido_requiere_preaprobacion", "decision",      null),
        ("pf_contragarantia_cobertura_min_pct",        "decision",      null),
        ("pf_cobertura_rojo",                          "garantias",     "veces"),
        ("pf_cobertura_verde",                         "garantias",     "veces"),
        ("pf_cuota_ingreso_estres_max",                "garantias",     "fraccion"),
        ("pf_haircut_inmueble",                        "garantias",     "fraccion"),
        ("pf_haircut_inmueble_vivienda",               "garantias",     "fraccion"),
        ("pf_haircut_rodado",                          "garantias",     "fraccion"),
        ("pf_haircut_participacion",                   "garantias",     "fraccion"),
        ("pf_haircut_deposito",                        "garantias",     "fraccion"),
        ("pf_haircut_inversion",                       "garantias",     "fraccion"),
        ("pf_haircut_titulo_publico",                  "garantias",     "fraccion"),
        ("pf_haircut_accion_on",                       "garantias",     "fraccion"),
        ("pf_haircut_fci",                             "garantias",     "fraccion"),
        ("pf_haircut_credito",                         "garantias",     "fraccion"),
        ("pf_haircut_otro",                            "garantias",     "fraccion"),
        ("pf_haircut_descalce_moneda",                 "garantias",     "fraccion"),
        ("pf_ratio_mercado_fiscal_alerta",             "garantias",     "veces"),
    ];

    /// <summary>
    /// The kind of <paramref name="key"/> (<c>integer</c>, <c>flag</c>, <c>fraction</c> or <c>ratio</c>)
    /// and its inclusive bounds, read from the same key groups the validation walks. A month key (AAAAMM) is an
    /// <c>integer</c> whose upper bound is the month of <paramref name="today"/>.
    /// </summary>
    public static (string Kind, decimal Min, decimal Max) BoundsOf(string key, DateOnly today)
    {
        if (IntegerKeys.TryGetValue(key, out var range))
            return ("integer", range.Min, range.Max);

        if (YearMonthKeys.Contains(key))
            return ("integer", YearMonthMin, ToYearMonth(today));

        if (BoundedFractionKeys.TryGetValue(key, out var fraction))
            return ("fraction", fraction.Min, fraction.Max);

        if (FlagKeys.Contains(key))
            return ("flag", 0m, 1m);

        if (FractionKeys.Contains(key))
            return ("fraction", 0m, 1m);

        if (DscrKeys.Contains(key))
            return ("ratio", DscrMin, DscrMax);

        if (CoberturaKeys.Contains(key))
            return ("ratio", CoberturaMin, CoberturaMax);

        throw new ArgumentException($"'{key}' is not a classified persona humana key.", nameof(key));
    }
}
