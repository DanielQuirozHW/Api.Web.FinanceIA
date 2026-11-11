// Project : Api.Web.FinanceIA
// Created : 2026-10-20

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The resolved <c>pf_</c> thresholds one persona humana evaluation runs on: the tenant's
/// [dbo].[ScoringConfig] rows over <see cref="PersonEvaluationDefaults"/>. Frozen with the evaluation, so
/// a past analysis records the exact cuts it used.
/// </summary>
public sealed record PersonEvaluationParameters
{
    /// <summary>pf_requiere_certificado_mipyme: a missing or expired certificate is a Rechazo (else an Alerta).</summary>
    public required bool RequiereCertificadoMipyme { get; init; }

    /// <summary>pf_certificado_alerta_desde_mes.</summary>
    public required int CertificadoAlertaDesdeMes { get; init; }

    /// <summary>
    /// pf_certificado_alerta_hasta_mes. Deliberately NOT <c>required</c>: the parameters are frozen inside
    /// AnalysisParticipants.DocumentVersionsJson, and a required member missing from an older blob would make
    /// that whole blob unreadable. Null therefore means "the evaluation predates the key" — no upper bound,
    /// the rule of that time. <see cref="From"/> always sets it.
    /// </summary>
    public int? CertificadoAlertaHastaMes { get; init; }

    /// <summary>pf_bcra_sit_rechazo.</summary>
    public required int BcraSituacionRechazo { get; init; }

    /// <summary>pf_bcra_ventana_meses.</summary>
    public required int BcraVentanaMeses { get; init; }

    /// <summary>pf_bcra_eventos_ventana_meses.</summary>
    public required int BcraEventosVentanaMeses { get; init; }

    /// <summary>pf_cheques_pagados_alerta_meses.</summary>
    public required int ChequesPagadosAlertaMeses { get; init; }

    /// <summary>pf_antiguedad_rechazo_meses.</summary>
    public required int AntiguedadRechazoMeses { get; init; }

    /// <summary>pf_antiguedad_min_meses.</summary>
    public required int AntiguedadMinMeses { get; init; }

    /// <summary>pf_ventas_acreedor_fisco_tolerancia.</summary>
    public required decimal VentasAcreedorFiscoTolerancia { get; init; }

    /// <summary>pf_consumo_mensual_minimo_miles, converted to PESOS (0 = not defined).</summary>
    public required decimal ConsumoMensualMinimo { get; init; }

    /// <summary>pf_cuota_ingreso_bajo.</summary>
    public required decimal CuotaIngresoBajo { get; init; }

    /// <summary>pf_cuota_ingreso_medio.</summary>
    public required decimal CuotaIngresoMedio { get; init; }

    /// <summary>pf_cuota_ingreso_alto.</summary>
    public required decimal CuotaIngresoAlto { get; init; }

    /// <summary>
    /// pf_plazo_estimado_deuda_meses, RETIRED in engine 0.6 (the cuotas vigentes are now
    /// <see cref="CuotaEstimadaPctSaldo"/> of the balance). Non-null only on an evaluation frozen before 0.6, so it
    /// still shows the term it used; <see cref="From"/> no longer sets it.
    /// </summary>
    public int? PlazoEstimadoDeudaMeses { get; init; }

    /// <summary>pf_dscr_rojo.</summary>
    public required decimal DscrRojo { get; init; }

    /// <summary>pf_dscr_verde.</summary>
    public required decimal DscrVerde { get; init; }

    /// <summary>pf_patrimonio_operativo_alerta_pct.</summary>
    public required decimal PatrimonioOperativoAlertaPct { get; init; }

    /// <summary>pf_doc_antiguedad_max_meses.</summary>
    public required int DocAntiguedadMaxMeses { get; init; }

    /// <summary>pf_ddjj_anual_antiguedad_max_meses.</summary>
    public required int DdjjAnualAntiguedadMaxMeses { get; init; }

    /// <summary>pf_informe_max_dias.</summary>
    public required int InformeMaxDias { get; init; }

    /// <summary>pf_aprobacion_requiere_motivo.</summary>
    public required bool AprobacionRequiereMotivo { get; init; }

    /// <summary>pf_rechazo_sugerido_requiere_preaprobacion.</summary>
    public required bool RechazoSugeridoRequierePreaprobacion { get; init; }

    /// <summary>
    /// pf_contragarantia_cobertura_min_pct: the minimum counter-guarantee coverage of the aval, as a whole
    /// percent (0-100). Not <c>required</c> for the same reason as <see cref="CertificadoAlertaHastaMes"/>:
    /// null means the frozen evaluation predates the key. <see cref="From"/> always sets it.
    /// </summary>
    public int? ContragarantiaCoberturaMinPct { get; init; }

    // ── Fiadores (M10 Phase 5a-2) ────────────────────────────────────────────────
    // None of these is required, for the reason given on CertificadoAlertaHastaMes: null means the frozen
    // evaluation predates engine 0.5. From always sets them; the fiador rules read them through the
    // Resolved* helpers below, which fall back to the default.

    /// <summary>pf_cobertura_rojo.</summary>
    public decimal? CoberturaRojo { get; init; }

    /// <summary>pf_cobertura_verde.</summary>
    public decimal? CoberturaVerde { get; init; }

    /// <summary>pf_cuota_ingreso_estres_max.</summary>
    public decimal? CuotaIngresoEstresMax { get; init; }

    /// <summary>pf_haircut_inmueble.</summary>
    public decimal? HaircutInmueble { get; init; }

    /// <summary>pf_haircut_inmueble_vivienda.</summary>
    public decimal? HaircutInmuebleVivienda { get; init; }

    /// <summary>pf_haircut_rodado.</summary>
    public decimal? HaircutRodado { get; init; }

    /// <summary>pf_haircut_participacion.</summary>
    public decimal? HaircutParticipacion { get; init; }

    /// <summary>pf_haircut_descalce_moneda.</summary>
    public decimal? HaircutDescalceMoneda { get; init; }

    /// <summary>pf_ratio_mercado_fiscal_alerta.</summary>
    public int? RatioMercadoFiscalAlerta { get; init; }

    // Engine 0.6. Not required either: null means the frozen evaluation predates 0.6. From always sets them;
    // the engine reads them through the Resolved* helpers below.

    /// <summary>pf_cuota_estimada_pct_saldo: the monthly cuotas vigentes as a fraction of the BCRA balance.</summary>
    public decimal? CuotaEstimadaPctSaldo { get; init; }

    /// <summary>pf_consumo_mes_base: the month (AAAAMM) in whose pesos <see cref="ConsumoMensualMinimo"/> is expressed.</summary>
    public int? ConsumoMesBase { get; init; }

    /// <summary>pf_haircut_deposito.</summary>
    public decimal? HaircutDeposito { get; init; }

    /// <summary>pf_haircut_inversion.</summary>
    public decimal? HaircutInversion { get; init; }

    /// <summary>pf_haircut_credito.</summary>
    public decimal? HaircutCredito { get; init; }

    // Engine 0.9 (investment classes). Not required: null means the frozen evaluation predates 0.9. From always sets
    // them; the engine reads them through the Resolved* helpers below.

    /// <summary>pf_haircut_titulo_publico: an Inversion of class TituloPublico.</summary>
    public decimal? HaircutTituloPublico { get; init; }

    /// <summary>pf_haircut_accion_on: an Inversion of class AccionCotizante or ObligacionNegociable.</summary>
    public decimal? HaircutAccionOn { get; init; }

    /// <summary>pf_haircut_fci: an Inversion of class CuotaparteFci.</summary>
    public decimal? HaircutFci { get; init; }

    /// <summary>pf_haircut_otro.</summary>
    public decimal? HaircutOtro { get; init; }

    // Engine 0.7 (agro annual mode). Not required: null means the frozen evaluation predates 0.7. From always
    // sets them; the engine reads them through the Resolved* helpers below.

    /// <summary>pf_agro_modo_anual: a solicitante whose main ARCA activity is agro is evaluated in annual mode.</summary>
    public bool? AgroModoAnual { get; init; }

    /// <summary>pf_agro_cuotas_por_anio: the cuotas a year of an agro operation that does not state them.</summary>
    public int? AgroCuotasPorAnio { get; init; }

    /// <summary>pf_agro_mes_inicio_campania: the campaign's first month (1-12).</summary>
    public int? AgroMesInicioCampania { get; init; }

    /// <summary>The keys that came from the tenant's rows rather than the code defaults.</summary>
    public IReadOnlyList<string> TenantOverrides { get; init; } = [];

    /// <summary>
    /// Resolves every key: the tenant's value when <paramref name="tenantRows"/> has it, the default
    /// otherwise. Keys outside <see cref="PersonEvaluationDefaults"/> are ignored.
    /// </summary>
    public static PersonEvaluationParameters From(IReadOnlyDictionary<string, decimal> tenantRows)
    {
        ArgumentNullException.ThrowIfNull(tenantRows);

        decimal Get(string key) => tenantRows.TryGetValue(key, out var value) ? value : PersonEvaluationDefaults.Get(key);
        int GetInt(string key) => (int)Math.Round(Get(key), MidpointRounding.AwayFromZero);
        bool GetFlag(string key) => Get(key) >= 0.5m;

        return new PersonEvaluationParameters
        {
            RequiereCertificadoMipyme            = GetFlag("pf_requiere_certificado_mipyme"),
            CertificadoAlertaDesdeMes            = GetInt("pf_certificado_alerta_desde_mes"),
            CertificadoAlertaHastaMes            = GetInt("pf_certificado_alerta_hasta_mes"),
            BcraSituacionRechazo                 = GetInt("pf_bcra_sit_rechazo"),
            BcraVentanaMeses                     = GetInt("pf_bcra_ventana_meses"),
            BcraEventosVentanaMeses              = GetInt("pf_bcra_eventos_ventana_meses"),
            ChequesPagadosAlertaMeses            = GetInt("pf_cheques_pagados_alerta_meses"),
            AntiguedadRechazoMeses               = GetInt("pf_antiguedad_rechazo_meses"),
            AntiguedadMinMeses                   = GetInt("pf_antiguedad_min_meses"),
            VentasAcreedorFiscoTolerancia        = Get("pf_ventas_acreedor_fisco_tolerancia"),
            ConsumoMensualMinimo                 = GetInt("pf_consumo_mensual_minimo_miles") * 1000m,
            CuotaIngresoBajo                     = Get("pf_cuota_ingreso_bajo"),
            CuotaIngresoMedio                    = Get("pf_cuota_ingreso_medio"),
            CuotaIngresoAlto                     = Get("pf_cuota_ingreso_alto"),
            DscrRojo                             = Get("pf_dscr_rojo"),
            DscrVerde                            = Get("pf_dscr_verde"),
            PatrimonioOperativoAlertaPct         = Get("pf_patrimonio_operativo_alerta_pct"),
            DocAntiguedadMaxMeses                = GetInt("pf_doc_antiguedad_max_meses"),
            DdjjAnualAntiguedadMaxMeses          = GetInt("pf_ddjj_anual_antiguedad_max_meses"),
            InformeMaxDias                       = GetInt("pf_informe_max_dias"),
            AprobacionRequiereMotivo             = GetFlag("pf_aprobacion_requiere_motivo"),
            RechazoSugeridoRequierePreaprobacion = GetFlag("pf_rechazo_sugerido_requiere_preaprobacion"),
            ContragarantiaCoberturaMinPct        = GetInt("pf_contragarantia_cobertura_min_pct"),
            CoberturaRojo                        = Get("pf_cobertura_rojo"),
            CoberturaVerde                       = Get("pf_cobertura_verde"),
            CuotaIngresoEstresMax                = Get("pf_cuota_ingreso_estres_max"),
            HaircutInmueble                      = Get("pf_haircut_inmueble"),
            HaircutInmuebleVivienda              = Get("pf_haircut_inmueble_vivienda"),
            HaircutRodado                        = Get("pf_haircut_rodado"),
            HaircutParticipacion                 = Get("pf_haircut_participacion"),
            HaircutDescalceMoneda                = Get("pf_haircut_descalce_moneda"),
            RatioMercadoFiscalAlerta             = GetInt("pf_ratio_mercado_fiscal_alerta"),
            CuotaEstimadaPctSaldo                = Get("pf_cuota_estimada_pct_saldo"),
            ConsumoMesBase                       = GetInt("pf_consumo_mes_base"),
            HaircutDeposito                      = Get("pf_haircut_deposito"),
            HaircutInversion                     = Get("pf_haircut_inversion"),
            HaircutCredito                       = Get("pf_haircut_credito"),
            HaircutTituloPublico                 = Get("pf_haircut_titulo_publico"),
            HaircutAccionOn                      = Get("pf_haircut_accion_on"),
            HaircutFci                           = Get("pf_haircut_fci"),
            HaircutOtro                          = Get("pf_haircut_otro"),
            AgroModoAnual                        = GetFlag("pf_agro_modo_anual"),
            AgroCuotasPorAnio                    = GetInt("pf_agro_cuotas_por_anio"),
            AgroMesInicioCampania                = GetInt("pf_agro_mes_inicio_campania"),
            TenantOverrides                      = PersonEvaluationDefaults.Values.Keys
                                                       .Where(tenantRows.ContainsKey)
                                                       .Order(StringComparer.Ordinal)
                                                       .ToList()
        };
    }

    /// <summary><see cref="CoberturaRojo"/>, or the default when the blob predates it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public decimal ResolvedCoberturaRojo => CoberturaRojo ?? PersonEvaluationDefaults.Get("pf_cobertura_rojo");

    /// <summary><see cref="CoberturaVerde"/>, or the default when the blob predates it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public decimal ResolvedCoberturaVerde => CoberturaVerde ?? PersonEvaluationDefaults.Get("pf_cobertura_verde");

    /// <summary><see cref="CuotaIngresoEstresMax"/>, or the default when the blob predates it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public decimal ResolvedCuotaIngresoEstresMax => CuotaIngresoEstresMax ?? PersonEvaluationDefaults.Get("pf_cuota_ingreso_estres_max");

    /// <summary><see cref="HaircutInmueble"/>, or the default when the blob predates it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public decimal ResolvedHaircutInmueble => HaircutInmueble ?? PersonEvaluationDefaults.Get("pf_haircut_inmueble");

    /// <summary><see cref="HaircutInmuebleVivienda"/>, or the default when the blob predates it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public decimal ResolvedHaircutInmuebleVivienda => HaircutInmuebleVivienda ?? PersonEvaluationDefaults.Get("pf_haircut_inmueble_vivienda");

    /// <summary><see cref="HaircutRodado"/>, or the default when the blob predates it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public decimal ResolvedHaircutRodado => HaircutRodado ?? PersonEvaluationDefaults.Get("pf_haircut_rodado");

    /// <summary><see cref="HaircutParticipacion"/>, or the default when the blob predates it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public decimal ResolvedHaircutParticipacion => HaircutParticipacion ?? PersonEvaluationDefaults.Get("pf_haircut_participacion");

    /// <summary><see cref="HaircutDescalceMoneda"/>, or the default when the blob predates it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public decimal ResolvedHaircutDescalceMoneda => HaircutDescalceMoneda ?? PersonEvaluationDefaults.Get("pf_haircut_descalce_moneda");

    /// <summary><see cref="RatioMercadoFiscalAlerta"/>, or the default when the blob predates it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int ResolvedRatioMercadoFiscalAlerta => RatioMercadoFiscalAlerta ?? (int)PersonEvaluationDefaults.Get("pf_ratio_mercado_fiscal_alerta");

    /// <summary><see cref="CuotaEstimadaPctSaldo"/>, or the default when the blob predates it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public decimal ResolvedCuotaEstimadaPctSaldo => CuotaEstimadaPctSaldo ?? PersonEvaluationDefaults.Get("pf_cuota_estimada_pct_saldo");

    /// <summary><see cref="ConsumoMesBase"/>, or the default when the blob predates it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int ResolvedConsumoMesBase => ConsumoMesBase ?? (int)PersonEvaluationDefaults.Get("pf_consumo_mes_base");

    /// <summary><see cref="HaircutDeposito"/>, or the default when the blob predates it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public decimal ResolvedHaircutDeposito => HaircutDeposito ?? PersonEvaluationDefaults.Get("pf_haircut_deposito");

    /// <summary><see cref="HaircutInversion"/>, or the default when the blob predates it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public decimal ResolvedHaircutInversion => HaircutInversion ?? PersonEvaluationDefaults.Get("pf_haircut_inversion");

    /// <summary><see cref="HaircutCredito"/>, or the default when the blob predates it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public decimal ResolvedHaircutCredito => HaircutCredito ?? PersonEvaluationDefaults.Get("pf_haircut_credito");

    /// <summary><see cref="HaircutTituloPublico"/>, or the default when the blob predates it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public decimal ResolvedHaircutTituloPublico => HaircutTituloPublico ?? PersonEvaluationDefaults.Get("pf_haircut_titulo_publico");

    /// <summary><see cref="HaircutAccionOn"/>, or the default when the blob predates it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public decimal ResolvedHaircutAccionOn => HaircutAccionOn ?? PersonEvaluationDefaults.Get("pf_haircut_accion_on");

    /// <summary><see cref="HaircutFci"/>, or the default when the blob predates it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public decimal ResolvedHaircutFci => HaircutFci ?? PersonEvaluationDefaults.Get("pf_haircut_fci");

    /// <summary><see cref="HaircutOtro"/>, or the default when the blob predates it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public decimal ResolvedHaircutOtro => HaircutOtro ?? PersonEvaluationDefaults.Get("pf_haircut_otro");

    /// <summary><see cref="AgroModoAnual"/>, or the default when the blob predates it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ResolvedAgroModoAnual => AgroModoAnual ?? PersonEvaluationDefaults.Get("pf_agro_modo_anual") >= 0.5m;

    /// <summary><see cref="AgroCuotasPorAnio"/>, or the default when the blob predates it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int ResolvedAgroCuotasPorAnio => AgroCuotasPorAnio ?? (int)PersonEvaluationDefaults.Get("pf_agro_cuotas_por_anio");

    /// <summary><see cref="AgroMesInicioCampania"/>, or the default when the blob predates it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int ResolvedAgroMesInicioCampania => AgroMesInicioCampania ?? (int)PersonEvaluationDefaults.Get("pf_agro_mes_inicio_campania");

    /// <summary>The defaults alone — a tenant with no <c>pf_</c> row.</summary>
    public static PersonEvaluationParameters Defaults { get; } = From(new Dictionary<string, decimal>());
}
