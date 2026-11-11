// Project : Api.Web.FinanceIA
// Created : 2026-05-08
// Modified: 2026-05-08

using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Models.Financia;

// ── Enums ─────────────────────────────────────────────────────────────────────

/// <summary>Discriminates between a successful debt lookup, a CUIT with no reported debt, and a transient API failure.</summary>
public enum BcraDeudaConsultaEstado
{
    DebtFound,
    NoDebtReported,
    LookupFailed
}

/// <summary>Discriminates between rejected cheques found, a CUIT with none reported, and a transient API failure.</summary>
public enum BcraChequesConsultaEstado
{
    ChequesFound,
    NoChequesReported,
    LookupFailed
}

/// <summary>
/// Whether an analysis's BCRA enrichment actually happened, and if not, why. Persisted per analysis
/// in <c>AnalysisResults.EnrichmentStatus</c> so a transient BCRA outage — which silently costs the
/// analysis the whole 0.35 BCRA weight — is distinguishable from an entity that legitimately has no
/// CUIT to look up. Before this existed, both produced the same row.
/// <para>
/// The distinction between the two non-Complete values is PERMANENCE, not magnitude. Both are scored
/// without the BCRA component and both are numerically penalised by the same renormalisation. But a
/// <see cref="NotApplicable"/> analysis can never score differently, because there is no CUIT and so
/// no BCRA data can ever exist for it; a <see cref="Degraded"/> one would score differently on a
/// retry that succeeded. Only the second is a data-availability artefact.
/// </para>
/// </summary>
public enum BcraEnrichmentOutcome
{
    /// <summary>BCRA answered; <c>Deuda.Estado</c> is <c>DebtFound</c> or <c>NoDebtReported</c>.</summary>
    Complete,

    /// <summary>
    /// The entity resolved and has no CUIT on file, so there was never anything to query. The
    /// exclusion is permanent and correct — not a failure.
    /// </summary>
    NotApplicable,

    /// <summary>
    /// We tried and failed, and a retry could succeed: the entity could not be resolved, or the BCRA
    /// call threw, returned nothing, or returned <c>Deuda.Estado == LookupFailed</c>.
    /// </summary>
    Degraded
}

// ── Stored / response models ──────────────────────────────────────────────────

/// <summary>
/// Aggregated BCRA data persisted as JSON in <c>AnalysisResults.BcraDataJson</c>
/// and surfaced in the analysis response as the <c>bcraData</c> field.
/// </summary>
public sealed class BcraData
{
    [JsonPropertyName("deuda")]
    public BcraDeudaData? Deuda { get; set; }

    [JsonPropertyName("macro")]
    public BcraMacroData? Macro { get; set; }

    [JsonPropertyName("cambiario")]
    public BcraCambiarioData? Cambiario { get; set; }

    [JsonPropertyName("cheques")]
    public BcraChequesData? Cheques { get; set; }
}

/// <summary>Per-period situación snapshot used for BCRA trend calculation.</summary>
public sealed class BcraSituacionPeriodo
{
    [JsonPropertyName("periodo")]        public string Periodo { get; set; } = string.Empty;
    [JsonPropertyName("situacionMaxima")] public int SituacionMaxima { get; set; }
}

/// <summary>Per-period total debt snapshot used for the debt-evolution chart.</summary>
public sealed class BcraMontoPeriodo
{
    [JsonPropertyName("periodo")]    public string  Periodo    { get; set; } = string.Empty;
    [JsonPropertyName("montoTotal")] public decimal MontoTotal { get; set; }
}

/// <summary>
/// Per-period debt split across three situación bands, for the portal's stacked
/// debt-evolution chart. Same unit as <see cref="BcraDeudaData.MontoTotal"/> (thousands of
/// pesos) — NOT the pesos unit the cheques block uses.
/// </summary>
public sealed class BcraMontoSituacionPeriodo
{
    [JsonPropertyName("periodo")]          public string  Periodo          { get; set; } = string.Empty;

    /// <summary>Debt reported under situación &lt;= 1 — "normal".</summary>
    [JsonPropertyName("montoSituacion1")]  public decimal MontoSituacion1  { get; set; }

    /// <summary>Debt reported under situación 2 or 3 — "con problemas".</summary>
    [JsonPropertyName("montoSituacion23")] public decimal MontoSituacion23 { get; set; }

    /// <summary>Debt reported under situación &gt;= 4 — irrecuperable / judicial.</summary>
    [JsonPropertyName("montoSituacion46")] public decimal MontoSituacion46 { get; set; }
}

/// <summary>Per-entity debt detail from the most recent BCRA reporting period.</summary>
public sealed class BcraEntidadDeudaDetalle
{
    [JsonPropertyName("entidad")]           public string? Entidad        { get; set; }
    [JsonPropertyName("situacion")]         public int     Situacion      { get; set; }
    [JsonPropertyName("monto")]             public decimal Monto          { get; set; }
    [JsonPropertyName("diasAtrasoPago")]    public int     DiasAtrasoPago { get; set; }
    [JsonPropertyName("refinanciaciones")]  public bool    Refinanciaciones  { get; set; }
    [JsonPropertyName("situacionJuridica")] public bool    SituacionJuridica { get; set; }
    [JsonPropertyName("enRevision")]        public bool    EnRevision        { get; set; }
    [JsonPropertyName("procesoJud")]        public bool    ProcesoJud        { get; set; }
}

/// <summary>Normalised summary of the Central de Deudores result for a CUIT.</summary>
public sealed class BcraDeudaData
{
    [JsonPropertyName("identificacion")]
    public long? Identificacion { get; set; }

    [JsonPropertyName("denominacion")]
    public string? Denominacion { get; set; }

    /// <summary>Highest risk classification found across all reporting entities (1 = normal, 6 = irrecoverable).</summary>
    [JsonPropertyName("situacionMaxima")]
    public int? SituacionMaxima { get; set; }

    /// <summary>Sum of all reported debt amounts (in thousands of pesos).</summary>
    [JsonPropertyName("montoTotal")]
    public decimal? MontoTotal { get; set; }

    /// <summary>Names of financial institutions that reported debt for this CUIT.</summary>
    [JsonPropertyName("entidadesInformantes")]
    public List<string>? EntidadesInformantes { get; set; } = new();

    /// <summary>Most recent reporting period in YYYYMM format.</summary>
    [JsonPropertyName("periodoConsultado")]
    public string? PeriodoConsultado { get; set; }

    /// <summary>
    /// Per-period situación history (most recent first, at most 12), used for BCRA trend calculation.
    /// Populated on <c>DebtFound</c> and — from Deudas/Historicas — on <c>NoDebtReported</c> too, when
    /// the CUIT had debt in past months but none in the latest período (the scoring engine reads it on
    /// DebtFound only). Empty when there is no such history or the history leg failed.
    /// Null for legacy stored data predating feature 5c.
    /// </summary>
    [JsonPropertyName("historialSituaciones")]
    public List<BcraSituacionPeriodo>? HistorialSituaciones { get; set; } = new();

    /// <summary>
    /// Per-period total debt (sum of all reporting entities' monto), most recent first, capped at
    /// 24 periods for the debt-evolution chart. Same unit as <see cref="MontoTotal"/> (thousands of
    /// pesos) — NOT the pesos unit the cheques block uses. Like <see cref="HistorialSituaciones"/> it
    /// can be populated on <c>NoDebtReported</c> (past months only; <see cref="MontoTotal"/> is then
    /// null). Null for legacy stored data predating this feature.
    /// </summary>
    [JsonPropertyName("historialMontos")]
    public List<BcraMontoPeriodo>? HistorialMontos { get; set; } = new();

    /// <summary>
    /// The same períodos as <see cref="HistorialMontos"/> — same filter, same newest-first order,
    /// same 24-period cap — with each período's amount split across three situación bands, for the
    /// portal's STACKED debt-evolution chart. Same unit as <see cref="MontoTotal"/> (thousands of
    /// pesos) — NOT the pesos unit the cheques block uses.
    /// <para>
    /// The bands (<c>&lt;= 1</c>, <c>2</c> or <c>3</c>, <c>&gt;= 4</c>) partition every integer
    /// situación, not just BCRA's documented 1-6, so the three always sum EXACTLY to the matching
    /// <see cref="BcraMontoPeriodo.MontoTotal"/>: a stray situación cannot fall between two bands
    /// and quietly shrink the stack below the total the other series reports for that período.
    /// </para>
    /// Can be populated on <c>NoDebtReported</c>, exactly when <see cref="HistorialMontos"/> is.
    /// Null for legacy stored data predating this feature.
    /// </summary>
    [JsonPropertyName("historialMontosPorSituacion")]
    public List<BcraMontoSituacionPeriodo>? HistorialMontosPorSituacion { get; set; } = new();

    /// <summary>
    /// Per-entity debt detail for the most recent period, ordered by Monto descending.
    /// Empty list for NoDebtReported / LookupFailed; null for legacy stored data.
    /// </summary>
    [JsonPropertyName("entidadesDetalle")]
    public List<BcraEntidadDeudaDetalle>? EntidadesDetalle { get; set; } = new();

    /// <summary>True if at least one reporting entity flagged a judicial process.</summary>
    [JsonPropertyName("tieneProcesoJudicial")]
    public bool TieneProcesoJudicial { get; set; }

    /// <summary>True if at least one reporting entity flagged a legal situation.</summary>
    [JsonPropertyName("tieneSituacionJuridica")]
    public bool TieneSituacionJuridica { get; set; }

    /// <summary>True if at least one reporting entity has the debt under revision.</summary>
    [JsonPropertyName("enRevision")]
    public bool EnRevision { get; set; }

    /// <summary>Maximum payment arrears in days across all reporting entities.</summary>
    [JsonPropertyName("maxDiasAtraso")]
    public int MaxDiasAtraso { get; set; }

    /// <summary>Outcome of the Central de Deudores query for this CUIT.</summary>
    [JsonPropertyName("estado")]
    public BcraDeudaConsultaEstado Estado { get; set; }

    [JsonPropertyName("fetchedAt")]
    public DateTime FetchedAt { get; set; }
}

/// <summary>Per-causal aggregate of rejected cheques, for the distribution chart.</summary>
public sealed class BcraChequeCausalResumen
{
    /// <summary>Rejection cause description as reported by BCRA (e.g. "SIN FONDOS SUFICIENTES").</summary>
    [JsonPropertyName("causal")]          public string? Causal          { get; set; }

    /// <summary>Count of rejected cheques under this causal.</summary>
    [JsonPropertyName("cantidad")]        public int     Cantidad        { get; set; }

    /// <summary>Sum of rejected-cheque amounts under this causal, in pesos (not thousands).</summary>
    [JsonPropertyName("monto")]           public decimal Monto           { get; set; }

    /// <summary>Count of still-unpaid (impago) cheques under this causal.</summary>
    [JsonPropertyName("cantidadImpagos")] public int     CantidadImpagos { get; set; }
}

/// <summary>
/// One rejected cheque, flattened out of the causal/entidad nesting with its
/// <c>causal</c> and <c>entidad</c> carried down so the portal can filter without re-walking.
/// </summary>
public sealed class BcraChequeDetalle
{
    /// <summary>Rejection cause this cheque was reported under.</summary>
    [JsonPropertyName("causal")]         public string?   Causal         { get; set; }

    /// <summary>Reporting entity code (a number in this endpoint, unlike the deuda block).</summary>
    [JsonPropertyName("entidad")]        public int       Entidad        { get; set; }

    [JsonPropertyName("nroCheque")]      public long      NroCheque      { get; set; }

    [JsonPropertyName("fechaRechazo")]   public DateTime? FechaRechazo   { get; set; }

    /// <summary>Cheque face amount, in pesos (not thousands, unlike the deuda block).</summary>
    [JsonPropertyName("monto")]          public decimal   Monto          { get; set; }

    /// <summary>Date the cheque was paid; <c>null</c> is meaningful — it means the cheque is still impago.</summary>
    [JsonPropertyName("fechaPago")]      public DateTime? FechaPago      { get; set; }

    [JsonPropertyName("fechaPagoMulta")] public DateTime? FechaPagoMulta { get; set; }
    [JsonPropertyName("estadoMulta")]    public string?   EstadoMulta    { get; set; }
    [JsonPropertyName("ctaPersonal")]    public bool      CtaPersonal    { get; set; }
    [JsonPropertyName("denomJuridica")]  public string?   DenomJuridica  { get; set; }
    [JsonPropertyName("enRevision")]     public bool      EnRevision     { get; set; }
    [JsonPropertyName("procesoJud")]     public bool      ProcesoJud     { get; set; }

    /// <summary>Derived: <c>true</c> when the cheque has been paid (<c>FechaPago != null</c>).</summary>
    [JsonPropertyName("pagado")]         public bool      Pagado         { get; set; }
}

/// <summary>Normalised summary of the Central de Deudores "cheques rechazados" result for a CUIT.</summary>
public sealed class BcraChequesData
{
    [JsonPropertyName("identificacion")]
    public long? Identificacion { get; set; }

    [JsonPropertyName("denominacion")]
    public string? Denominacion { get; set; }

    /// <summary>Total count of rejected cheques returned by BCRA (roughly a 24-month window).</summary>
    [JsonPropertyName("totalRechazados")]
    public int TotalRechazados { get; set; }

    /// <summary>Count of rejected cheques still unpaid (<c>FechaPago == null</c>).</summary>
    [JsonPropertyName("totalImpagos")]
    public int TotalImpagos { get; set; }

    /// <summary>Sum of all rejected-cheque amounts, in pesos (not thousands, unlike the deuda block).</summary>
    [JsonPropertyName("montoTotalRechazado")]
    public decimal MontoTotalRechazado { get; set; }

    /// <summary>Sum of unpaid (impago) rejected-cheque amounts, in pesos (not thousands).</summary>
    [JsonPropertyName("montoTotalImpago")]
    public decimal MontoTotalImpago { get; set; }

    /// <summary>Earliest rejection date across all returned cheques.</summary>
    [JsonPropertyName("fechaPrimerRechazo")]
    public DateTime? FechaPrimerRechazo { get; set; }

    /// <summary>Latest rejection date across all returned cheques.</summary>
    [JsonPropertyName("fechaUltimoRechazo")]
    public DateTime? FechaUltimoRechazo { get; set; }

    /// <summary>True if at least one rejected cheque is flagged with a judicial process.</summary>
    [JsonPropertyName("tieneProcesoJudicial")]
    public bool TieneProcesoJudicial { get; set; }

    /// <summary>True if at least one rejected cheque is flagged under revision.</summary>
    [JsonPropertyName("enRevision")]
    public bool EnRevision { get; set; }

    /// <summary>
    /// Per-causal aggregate for the distribution chart.
    /// Empty list for NoChequesReported / LookupFailed; null for legacy stored data.
    /// </summary>
    [JsonPropertyName("causales")]
    public List<BcraChequeCausalResumen>? Causales { get; set; } = new();

    /// <summary>
    /// Flat per-cheque list, ordered by fechaRechazo descending (most recent first).
    /// Empty list for NoChequesReported / LookupFailed; null for legacy stored data.
    /// </summary>
    [JsonPropertyName("detalle")]
    public List<BcraChequeDetalle>? Detalle { get; set; } = new();

    /// <summary>Outcome of the cheques-rechazados query for this CUIT.</summary>
    [JsonPropertyName("estado")]
    public BcraChequesConsultaEstado Estado { get; set; }

    [JsonPropertyName("fetchedAt")]
    public DateTime FetchedAt { get; set; }
}

/// <summary>Selected macroeconomic variables from the BCRA statistics API (variables 27 and 135).</summary>
public sealed class BcraMacroData
{
    /// <summary>Monthly CPI inflation % — BCRA variable 27 ("Inflación mensual").</summary>
    [JsonPropertyName("inflacionMensual")]
    public double? InflacionMensual { get; set; }

    /// <summary>
    /// The same variable 27 as a monthly SERIES, keyed "YYYYMM" → that month's inflation in the same
    /// unit as <see cref="InflacionMensual"/> (a percent: 2.5 means 2.5 %). Added for §7.3b, which
    /// deflates the LUFE sales figures by CPI before comparing them, and read only by
    /// <c>ScoringService</c>.
    /// <para>
    /// It rides inside the existing <c>BcraDataJson</c> / <c>MacroDataJson</c> blobs — no column and
    /// no DDL — which is also what makes it DETERMINISTIC: the series a score used is frozen into
    /// the analysis's own snapshot, so a later macro refresh cannot move a past score.
    /// </para>
    /// <para>
    /// <c>null</c> on every payload written before §7.3b, and whenever the wide request behind it
    /// failed. Absence is not an error: the sales-drop penalty falls back to measuring the drop
    /// nominally and REPORTS that it did. It is deliberately a separate member rather than a
    /// replacement for the scalar, which several other call sites read and which is sourced from its
    /// own, narrower request.
    /// </para>
    /// </summary>
    [JsonPropertyName("inflacionMensualSerie")]
    public Dictionary<string, double>? InflacionMensualSerie { get; set; }

    /// <summary>
    /// Wholesale reference rate (TAMAR, nominal annual) % — BCRA variable 135
    /// ("Tasa de interés TAMAR de bancos públicos y privados"). The former policy-rate series
    /// ("Tasa de Política Monetaria", id 160) was discontinued 2025-07-10. The wire name is kept
    /// as <c>tasaReferencia</c> for Portal contract compatibility.
    /// </summary>
    [JsonPropertyName("tasaReferencia")]
    public double? TasaReferencia { get; set; }

    [JsonPropertyName("fetchedAt")]
    public DateTime FetchedAt { get; set; }
}

/// <summary>Exchange-rate data from the BCRA statistics API (variable 4).</summary>
public sealed class BcraCambiarioData
{
    /// <summary>Official USD/ARS exchange rate (BNA) — BCRA variable 4.</summary>
    [JsonPropertyName("tipoCambioOficial")]
    public double? TipoCambioOficial { get; set; }

    [JsonPropertyName("fetchedAt")]
    public DateTime FetchedAt { get; set; }
}

/// <summary>A single time-series variable returned by the BCRA statistics API.</summary>
public sealed class BcraVariable
{
    [JsonPropertyName("idVariable")]
    public int IdVariable { get; set; }

    [JsonPropertyName("descripcion")]
    public string Descripcion { get; set; } = string.Empty;

    [JsonPropertyName("valor")]
    public double Valor { get; set; }

    [JsonPropertyName("fecha")]
    public string Fecha { get; set; } = string.Empty;
}

// ── Raw API response shapes (deserialization only) ────────────────────────────

/// <summary>Envelope returned by <c>GET /centraldedeudores/v1.0/Deudas/{cuit}</c>.</summary>
internal sealed class BcraDeudaApiResponse
{
    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("results")]
    public BcraDeudaResultsNode? Results { get; set; }
}

internal sealed class BcraDeudaResultsNode
{
    [JsonPropertyName("identificacion")]
    public long Identificacion { get; set; }

    [JsonPropertyName("denominacion")]
    public string? Denominacion { get; set; }

    [JsonPropertyName("periodos")]
    public List<BcraDeudaPeriodo>? Periodos { get; set; }
}

internal sealed class BcraDeudaPeriodo
{
    [JsonPropertyName("periodo")]
    public string Periodo { get; set; } = string.Empty;

    [JsonPropertyName("entidades")]
    public List<BcraDeudaEntidad>? Entidades { get; set; }
}

internal sealed class BcraDeudaEntidad
{
    [JsonPropertyName("entidad")]
    public string? NombreEntidad { get; set; }

    [JsonPropertyName("situacion")]
    public int Situacion { get; set; }

    [JsonPropertyName("monto")]
    public decimal Monto { get; set; }

    [JsonPropertyName("diasAtrasoPago")]    public int  DiasAtrasoPago    { get; set; }
    [JsonPropertyName("situacionJuridica")] public bool SituacionJuridica { get; set; }
    [JsonPropertyName("enRevision")]        public bool EnRevision        { get; set; }
    [JsonPropertyName("procesoJud")]        public bool ProcesoJud        { get; set; }
    [JsonPropertyName("refinanciaciones")]  public bool Refinanciaciones  { get; set; }
}

/// <summary>Envelope returned by <c>GET /centraldedeudores/v1.0/Deudas/ChequesRechazados/{cuit}</c>.</summary>
internal sealed class BcraChequesApiResponse
{
    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("results")]
    public BcraChequesResultsNode? Results { get; set; }
}

internal sealed class BcraChequesResultsNode
{
    [JsonPropertyName("identificacion")]
    public long Identificacion { get; set; }

    [JsonPropertyName("denominacion")]
    public string? Denominacion { get; set; }

    [JsonPropertyName("causales")]
    public List<BcraChequesCausalNode>? Causales { get; set; }
}

internal sealed class BcraChequesCausalNode
{
    [JsonPropertyName("causal")]
    public string? Causal { get; set; }

    [JsonPropertyName("entidades")]
    public List<BcraChequesEntidadNode>? Entidades { get; set; }
}

internal sealed class BcraChequesEntidadNode
{
    /// <summary>Reporting entity code — a number in this endpoint (the deuda endpoint uses a string).</summary>
    [JsonPropertyName("entidad")]
    public int Entidad { get; set; }

    [JsonPropertyName("detalle")]
    public List<BcraChequeDetalleNode>? Detalle { get; set; }
}

/// <summary>One rejected cheque as returned on the wire. Dates arrive as <c>YYYY-MM-DD</c> strings and are nullable.</summary>
internal sealed class BcraChequeDetalleNode
{
    [JsonPropertyName("nroCheque")]      public long    NroCheque      { get; set; }
    [JsonPropertyName("fechaRechazo")]   public string? FechaRechazo   { get; set; }
    [JsonPropertyName("monto")]          public decimal Monto          { get; set; }
    [JsonPropertyName("fechaPago")]      public string? FechaPago      { get; set; }
    [JsonPropertyName("fechaPagoMulta")] public string? FechaPagoMulta { get; set; }
    [JsonPropertyName("estadoMulta")]    public string? EstadoMulta    { get; set; }
    [JsonPropertyName("ctaPersonal")]    public bool    CtaPersonal    { get; set; }
    [JsonPropertyName("denomJuridica")]  public string? DenomJuridica  { get; set; }
    [JsonPropertyName("enRevision")]     public bool    EnRevision     { get; set; }
    [JsonPropertyName("procesoJud")]     public bool    ProcesoJud     { get; set; }
}

/// <summary>
/// Envelope returned by <c>GET /estadisticas/v4.0/Monetarias/{idVariable}?Desde=...&amp;Hasta=...</c>.
/// Each result node contains a <c>detalle</c> time-series array sorted ascending by date.
/// </summary>
internal sealed class BcraMonetariasApiResponse
{
    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("results")]
    public List<BcraMonetariasResultNode>? Results { get; set; }
}

internal sealed class BcraMonetariasResultNode
{
    [JsonPropertyName("idVariable")]
    public int IdVariable { get; set; }

    [JsonPropertyName("detalle")]
    public List<BcraMonetariasDetalleNode>? Detalle { get; set; }
}

internal sealed class BcraMonetariasDetalleNode
{
    [JsonPropertyName("fecha")]
    public string? Fecha { get; set; }

    [JsonPropertyName("valor")]
    public double Valor { get; set; }
}

/// <summary>
/// Envelope returned by
/// <c>GET /estadisticascambiarias/v1.0/Cotizaciones/{codMoneda}?fechaDesde=...&amp;fechaHasta=...</c>.
/// <c>results</c> is an array of date nodes sorted descending; each node has a <c>detalle</c>
/// array of currency entries for that date.
/// </summary>
internal sealed class BcraCotizacionesApiResponse
{
    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("results")]
    public List<BcraCotizacionResultNode>? Results { get; set; }
}

/// <summary>One date entry in the cotizaciones results array.</summary>
internal sealed class BcraCotizacionResultNode
{
    [JsonPropertyName("fecha")]
    public string? Fecha { get; set; }

    [JsonPropertyName("detalle")]
    public List<BcraCotizacionDetalleNode>? Detalle { get; set; }
}

/// <summary>One currency entry within a date node's detalle array.</summary>
internal sealed class BcraCotizacionDetalleNode
{
    [JsonPropertyName("codigoMoneda")]
    public string? CodigoMoneda { get; set; }

    /// <summary>Official exchange rate (USD/ARS BNA sell price).</summary>
    [JsonPropertyName("tipoCotizacion")]
    public double TipoCotizacion { get; set; }
}
