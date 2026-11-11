// Project : Api.Web.FinanceIA
// Created : 2026-08-24

using System.Globalization;
using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Models.Nosis;

// ── Enums ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Discriminates between a successful NOSIS lookup with content, a CUIT with
/// nothing on file (a valid "no data" answer), and a transient API failure.
/// Mirrors <c>CasfogConsultaEstado</c>; serialized numerically, like its sibling
/// (no <c>JsonStringEnumConverter</c> is registered in Program.cs).
/// </summary>
public enum NosisConsultaEstado
{
    DataFound,
    NoDataReported,
    LookupFailed
}

/// <summary>
/// Which NOSIS variable group (VR) a lookup should ask for. NOSIS ships one report group per
/// audience, and they answer different variable sets: the PRINCIPAL group covers the company
/// being analysed, the FIADOR group is centred on a guarantor's labour/company/tax profile and on
/// the COM_* compliance flags. Passing the wrong one silently returns the wrong variables rather
/// than failing, which is why this is an explicit required parameter on
/// <c>INosisService.GetVariablesAsync</c> and not an optional argument with a default.
///
/// <para>
/// The group is resolved to a configured VR number inside <c>NosisService</c>, never by the
/// caller: <see cref="Principal"/> reads <c>Nosis:VR</c> and <see cref="Fiador"/> reads
/// <c>Nosis:VrFiador</c>, falling back to <c>Nosis:VR</c> while the latter is unset.
/// </para>
/// </summary>
public enum NosisVariableGroup
{
    /// <summary>The company being analysed — the group <c>Nosis:VR</c> points at.</summary>
    Principal,

    /// <summary>A guarantor, queried by their OWN CUIT against <c>Nosis:VrFiador</c>.</summary>
    Fiador
}

// ── Stored / response models ──────────────────────────────────────────────────

/// <summary>
/// Normalised NOSIS (WS.01 <c>rest/variables</c>) snapshot for a CUIT, persisted as JSON in
/// <c>Entities.NosisDataJson</c> and copied write-once into <c>AnalysisResults.NosisDataJson</c>.
/// Projected from the vendor's FLAT <c>Variables[]</c> list by looking each variable up by its
/// <c>Nombre</c> — there is no nested object in the NOSIS response.
///
/// Every value field is nullable: a variable that is absent, empty or unparsable yields null for
/// that field alone and never brings the whole projection down. Monetary values arrive as es-AR
/// comma decimals (see <see cref="NosisVariableProjection"/>).
///
/// PII: EVERY field in FOUR of the regions below is identifying or specially protected data —
/// "Identidad (VI_*)", "Perfil laboral y fiscal (VI_*)", "Empleador (VI_Empleador_*)" and
/// "Cumplimiento normativo (COM_*)". Deliberately stated as regions rather than a list, because
/// the list went stale the first time a region grew. This is long past entity identifiers (CUIT,
/// razón social): it holds natural-person data — documento, fecha de nacimiento, sexo, edad,
/// domicilio, a deceased flag and a whole historia laboral — plus two things that are worse than
/// identity. The Empleador region names a THIRD PARTY (the subject's current employer, by name
/// and CUIT) who never consented to appear in this blob at all; and the Cumplimiento region
/// carries the UIF sujeto-obligado and PEP flags, which are regulated compliance signals.
/// Per the project rule it must NEVER be serialized wholesale into a Claude prompt — the model
/// analyses figures, not identities. No prompt builder reads <c>NosisDataJson</c> today; keep it
/// that way. With documento, fecha de nacimiento, a third party's CUIT and a PEP flag in the blob,
/// an accidental wholesale serialization is a reportable data incident, not a style slip.
/// </summary>
public sealed class NosisData
{
    /// <summary>Outcome of the NOSIS variables query for this CUIT.</summary>
    [JsonPropertyName("estado")]
    public NosisConsultaEstado Estado { get; set; }

    // ── Identidad (VI_*) ──────────────────────────────────────────────────────

    /// <summary>CUIT/CUIL as NOSIS reports it (<c>VI_Identificacion</c>).</summary>
    [JsonPropertyName("identificacion")]
    public string? Identificacion { get; set; }

    /// <summary>Razón social (<c>VI_RazonSocial</c>).</summary>
    [JsonPropertyName("razonSocial")]
    public string? RazonSocial { get; set; }

    /// <summary>
    /// Registra fallecimiento (<c>VI_Fallecido_Es</c>). Arrives as the text "Si"/"No", not a JSON
    /// boolean.
    /// </summary>
    [JsonPropertyName("esFallecido")]
    public bool? EsFallecido { get; set; }

    /// <summary>Documento de identidad (<c>VI_DNI</c>).</summary>
    [JsonPropertyName("dni")]
    public string? Dni { get; set; }

    /// <summary>
    /// Fecha de nacimiento (<c>VI_FecNacimiento</c>). Deliberately kept as a string and NEVER
    /// parsed as a date, for the same reason as <see cref="Situacion1Desde"/>: the vendor's date
    /// format is its own, and a DateTime round-trip would expose it to timezone shifting.
    /// </summary>
    [JsonPropertyName("fechaNacimiento")]
    public string? FechaNacimiento { get; set; }

    /// <summary>Sexo (<c>VI_Sexo</c>).</summary>
    [JsonPropertyName("sexo")]
    public string? Sexo { get; set; }

    /// <summary>Edad en años (<c>VI_Edad</c>).</summary>
    [JsonPropertyName("edad")]
    public int? Edad { get; set; }

    /// <summary>
    /// Localidad del domicilio (<c>VI_DomAF_Loc</c>). The vendor's expansion of the "AF" segment is
    /// not documented in the material we hold, so the property keeps the key's shape rather than
    /// asserting a meaning — in particular it is NOT asserted to be the ARCA-registered address.
    /// </summary>
    [JsonPropertyName("domAfLocalidad")]
    public string? DomAfLocalidad { get; set; }

    /// <summary>
    /// Provincia del domicilio (<c>VI_DomAF_Prov</c>). Same "AF" caveat as
    /// <see cref="DomAfLocalidad"/>.
    /// </summary>
    [JsonPropertyName("domAfProvincia")]
    public string? DomAfProvincia { get; set; }

    // ── Perfil laboral y fiscal (VI_*) ────────────────────────────────────────
    // The subject's own labour and AFIP-inscription record. Natural-person data — see the PII
    // note on the class.

    /// <summary>
    /// Antigüedad de la inscripción en AFIP (<c>VI_Inscrip_Afip_Antiguedad</c>). Typed
    /// <c>int?</c> by analogy with <see cref="Edad"/>, the file's other suffix-less VI count;
    /// the vendor does not declare the unit (years or months) in the material we hold, so the
    /// property keeps the key's shape rather than asserting one. A value the vendor sends with
    /// a unit attached (e.g. "10 años") is unparsable and nulls this field alone.
    /// </summary>
    [JsonPropertyName("inscripAfipAntiguedad")]
    public int? InscripAfipAntiguedad { get; set; }

    /// <summary>
    /// Categoría de monotributo (<c>VI_Inscrip_Monotributo</c>). Text, not a number: the value
    /// is a category letter (e.g. "C"), not a count.
    /// </summary>
    [JsonPropertyName("inscripMonotributo")]
    public string? InscripMonotributo { get; set; }

    /// <summary>
    /// <c>VI_Inscrip_IVA</c>. Kept as text, NOT <c>bool?</c>, on the same rule as
    /// <see cref="SrtFaltaPago"/>: it carries no <c>_Es</c>/<c>_Tiene</c> suffix, and we hold no
    /// raw sample proving it is a Si/No variable rather than a condición-IVA code. Passthrough is
    /// lossless either way — a "Si" survives as the string "Si" — so this can be retyped once a
    /// real response settles it.
    /// </summary>
    [JsonPropertyName("inscripIva")]
    public string? InscripIva { get; set; }

    /// <summary>Registra condición de jubilado (<c>VI_Jubilado_Es</c>). Text "Si"/"No" on the wire.</summary>
    [JsonPropertyName("esJubilado")]
    public bool? EsJubilado { get; set; }

    /// <summary>Registra relación de dependencia vigente (<c>VI_Empleado_Es</c>). Text "Si"/"No" on the wire.</summary>
    [JsonPropertyName("esEmpleado")]
    public bool? EsEmpleado { get; set; }

    /// <summary>
    /// Registra relación de dependencia en los últimos 12 meses (<c>VI_Empleado_12m_Es</c>).
    /// Text "Si"/"No" on the wire. Distinct from <see cref="EsEmpleado"/>, which is the vigente flag.
    /// </summary>
    [JsonPropertyName("esEmpleado12m")]
    public bool? EsEmpleado12m { get; set; }

    /// <summary>Integra alguna sociedad (<c>VI_IntegranteSociedad_Es</c>). Text "Si"/"No" on the wire.</summary>
    [JsonPropertyName("esIntegranteSociedad")]
    public bool? EsIntegranteSociedad { get; set; }

    /// <summary>Cantidad de empleados declarados por el propio sujeto (<c>VI_Empleados_Cant</c>).</summary>
    [JsonPropertyName("empleadosCant")]
    public int? EmpleadosCant { get; set; }

    /// <summary>
    /// Antigüedad laboral total (<c>VI_AntiguedadLaboral</c>). Same unit caveat and same typing
    /// rationale as <see cref="InscripAfipAntiguedad"/>.
    /// </summary>
    [JsonPropertyName("antiguedadLaboral")]
    public int? AntiguedadLaboral { get; set; }

    /// <summary>
    /// Antigüedad laboral con el empleador actual (<c>VI_AntiguedadLaboral_EmpleadorActual</c>).
    /// Same unit caveat as <see cref="InscripAfipAntiguedad"/>. Distinct from
    /// <see cref="AntiguedadLaboral"/>, which spans the whole record.
    /// </summary>
    [JsonPropertyName("antiguedadLaboralEmpleadorActual")]
    public int? AntiguedadLaboralEmpleadorActual { get; set; }

    // ── Empleador (VI_Empleador_*) ────────────────────────────────────────────
    // The subject's CURRENT EMPLOYER — a THIRD PARTY, identified by name and CUIT. See the PII
    // note on the class: this region identifies someone who is not even the subject of the query.

    /// <summary>
    /// CUIT del empleador actual (<c>VI_Empleador_CUIT</c>). A string, never a numeric type,
    /// consistent with <see cref="Identificacion"/> and with how every CUIT is stored in this
    /// application. The vendor may send it as a bare JSON number; <c>FlexibleStringConverter</c>
    /// on <c>NosisService.JsonOpts</c> is what makes reading it into a string safe.
    /// </summary>
    [JsonPropertyName("empleadorCuit")]
    public string? EmpleadorCuit { get; set; }

    /// <summary>
    /// Razón social del empleador actual (<c>VI_Empleador_RZ</c>). "RZ" is read as razón social
    /// by analogy with <see cref="RazonSocial"/> and because the key is paired with
    /// <c>VI_Empleador_CUIT</c>; the vendor does not expand it in the material we hold. The
    /// inference costs nothing — the field is a passthrough string either way. (Contrast
    /// <see cref="JuiciosRz60m"/>, where "RZ" sits in a counting key and is left as key shape.)
    /// </summary>
    [JsonPropertyName("empleadorRazonSocial")]
    public string? EmpleadorRazonSocial { get; set; }

    /// <summary>
    /// Cantidad de empleados del empleador actual (<c>VI_Empleador_Empleados_Cant</c>). This is
    /// the EMPLOYER's headcount, not the subject's — see <see cref="EmpleadosCant"/> for that.
    /// </summary>
    [JsonPropertyName("empleadorEmpleadosCant")]
    public int? EmpleadorEmpleadosCant { get; set; }

    // ── Perfil ────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>DF_Tiene</c> — arrives as the text "Si"/"No", not a JSON boolean. The vendor's
    /// expansion of the "DF" prefix is not documented in the material we hold, so the property
    /// keeps the key's shape rather than asserting a meaning.
    /// </summary>
    [JsonPropertyName("dfTiene")]
    public bool? DfTiene { get; set; }

    /// <summary>Nivel socioeconómico (<c>NSE</c>), e.g. "C1".</summary>
    [JsonPropertyName("nse")]
    public string? Nse { get; set; }

    /// <summary>Percentil dentro del nivel socioeconómico (<c>NSE_Percentil</c>).</summary>
    [JsonPropertyName("nsePercentil")]
    public int? NsePercentil { get; set; }

    /// <summary>Facturación estimada, expresada como bucket (<c>FE</c>).</summary>
    [JsonPropertyName("facturacionEstimada")]
    public int? FacturacionEstimada { get; set; }

    // ── Scores (SCO*) ─────────────────────────────────────────────────────────

    /// <summary>
    /// Score de riesgo vigente de NOSIS (<c>SCO_Vig</c>). This is the VENDOR's own score.
    /// It is NOT the internal 0–1000 presentation scale produced by
    /// <c>Api.Web.FinanceIA.Servicios.ScoreScale.ToNosis</c>, which is named after NOSIS but
    /// derived from this application's own motor score. Do not compare or substitute them.
    /// </summary>
    [JsonPropertyName("scoreVigente")]
    public int? ScoreVigente { get; set; }

    /// <summary>
    /// <c>SCOServ_Vig</c>. The vendor's expansion of the "Serv" segment is not documented in the
    /// material we hold, so the property keeps the key's shape rather than asserting a meaning.
    /// </summary>
    [JsonPropertyName("scoreServVigente")]
    public int? ScoreServVigente { get; set; }

    /// <summary>Score de riesgo vigente, variante fintech (<c>SCO_Vig_Fintech</c>).</summary>
    [JsonPropertyName("scoreVigenteFintech")]
    public int? ScoreVigenteFintech { get; set; }

    /// <summary>
    /// Score de riesgo a 24 meses atrás (<c>SCO_24m</c>). Like <see cref="ScoreVigente"/> this is
    /// the VENDOR's own score, not the internal 0–1000 presentation scale — do not compare or
    /// substitute them.
    /// </summary>
    [JsonPropertyName("score24m")]
    public int? Score24m { get; set; }

    // ── Central de deudores (CI_*) ────────────────────────────────────────────

    /// <summary>
    /// Peor situación BCRA vigente (<c>CI_Vig_PeorSit</c>). Kept as text even when it reads "1":
    /// the vendor types this variable as Texto.
    /// </summary>
    [JsonPropertyName("peorSituacionVigente")]
    public string? PeorSituacionVigente { get; set; }

    /// <summary>Cantidad de bancos informantes, total vigente (<c>CI_Vig_Total_CantBcos</c>).</summary>
    [JsonPropertyName("cantidadBancosVigente")]
    public int? CantidadBancosVigente { get; set; }

    /// <summary>Monto total vigente (<c>CI_Vig_Total_Monto</c>). es-AR comma decimal on the wire.</summary>
    [JsonPropertyName("montoTotalVigente")]
    public decimal? MontoTotalVigente { get; set; }

    /// <summary>Cantidad de bancos que informan situación 1 (<c>CI_Vig_Sit1_CantBcos</c>).</summary>
    [JsonPropertyName("cantidadBancosSituacion1Vigente")]
    public int? CantidadBancosSituacion1Vigente { get; set; }

    /// <summary>Compromiso mensual vigente (<c>CI_Vig_CompMensual</c>). es-AR comma decimal on the wire.</summary>
    [JsonPropertyName("compromisoMensualVigente")]
    public decimal? CompromisoMensualVigente { get; set; }

    /// <summary>
    /// Desde cuándo se mantiene en situación 1 (<c>CI_Sit1_Desde</c>), en formato <c>YYYYMM</c>
    /// (p. ej. "201601"). Deliberately kept as a string and NEVER parsed as a date: it has no day
    /// component, so any DateTime round-trip would invent one and expose it to timezone shifting.
    /// </summary>
    [JsonPropertyName("situacion1Desde")]
    public string? Situacion1Desde { get; set; }

    /// <summary>Peor situación en los últimos 6 meses (<c>CI_6m_PeorSit</c>).</summary>
    [JsonPropertyName("peorSituacion6m")]
    public string? PeorSituacion6m { get; set; }

    /// <summary>Peor situación en los últimos 12 meses (<c>CI_12m_PeorSit</c>).</summary>
    [JsonPropertyName("peorSituacion12m")]
    public string? PeorSituacion12m { get; set; }

    /// <summary>Cantidad de bancos informantes en los últimos 12 meses (<c>CI_12m_Total_CantBcos</c>).</summary>
    [JsonPropertyName("cantidadBancos12m")]
    public int? CantidadBancos12m { get; set; }

    /// <summary>
    /// Monto vigente de la línea "TC" (<c>CI_Vig_TC_Monto</c>). The vendor does not expand the
    /// segment in the material we hold, so the property keeps the key's shape rather than asserting
    /// a meaning. es-AR comma decimal on the wire.
    /// </summary>
    [JsonPropertyName("montoTcVigente")]
    public decimal? MontoTcVigente { get; set; }

    /// <summary>
    /// Monto vigente de la línea "Li_CC" (<c>CI_Vig_Li_CC_Monto</c>). Neither "Li" nor "CC" is
    /// expanded in the material we hold — "Li" is as likely Límite as línea — so the property keeps
    /// the key's shape rather than asserting a meaning. es-AR comma decimal on the wire.
    /// </summary>
    [JsonPropertyName("montoLiCcVigente")]
    public decimal? MontoLiCcVigente { get; set; }

    /// <summary>
    /// Monto vigente de la línea "Li_HI" (<c>CI_Vig_Li_HI_Monto</c>). Same "Li" caveat as
    /// <see cref="MontoLiCcVigente"/>. es-AR comma decimal on the wire.
    /// </summary>
    [JsonPropertyName("montoLiHiVigente")]
    public decimal? MontoLiHiVigente { get; set; }

    /// <summary>
    /// Monto vigente de la línea "Li_PR" (<c>CI_Vig_Li_PR_Monto</c>). Same "Li" caveat as
    /// <see cref="MontoLiCcVigente"/>. es-AR comma decimal on the wire.
    /// </summary>
    [JsonPropertyName("montoLiPrVigente")]
    public decimal? MontoLiPrVigente { get; set; }

    /// <summary>
    /// Monto vigente de la línea "Li_OPR" (<c>CI_Vig_Li_OPR_Monto</c>). Same "Li" caveat as
    /// <see cref="MontoLiCcVigente"/>. es-AR comma decimal on the wire.
    /// </summary>
    [JsonPropertyName("montoLiOprVigente")]
    public decimal? MontoLiOprVigente { get; set; }

    /// <summary>
    /// Deuda vigente a largo plazo (<c>CI_Vig_LargoPlazo</c>). Typed decimal by analogy with
    /// <see cref="CompromisoMensualVigente"/>, the file's other suffix-less CI amount; the vendor
    /// does not declare the type in the material we hold. es-AR comma decimal on the wire.
    /// </summary>
    [JsonPropertyName("largoPlazoVigente")]
    public decimal? LargoPlazoVigente { get; set; }

    /// <summary>
    /// Deuda vigente a corto plazo (<c>CI_Vig_CortoPlazo</c>). Same typing caveat as
    /// <see cref="LargoPlazoVigente"/>. es-AR comma decimal on the wire.
    /// </summary>
    [JsonPropertyName("cortoPlazoVigente")]
    public decimal? CortoPlazoVigente { get; set; }

    // ── Cumplimiento (VR_*) ───────────────────────────────────────────────────

    /// <summary>
    /// Cumplimiento en los últimos 12 meses (<c>VR_12m_Cumplimiento</c>). The vendor's expansion of
    /// the "VR" prefix is not documented in the material we hold, so the property keeps the key's
    /// shape rather than asserting a meaning.
    /// </summary>
    [JsonPropertyName("cumplimiento12m")]
    public string? Cumplimiento12m { get; set; }

    /// <summary>
    /// Atraso de cumplimiento en los últimos 60 meses (<c>VR_60m_Atraso_Cumplimiento_v2</c>). The
    /// vendor's <c>_v2</c> marker is kept in the name on purpose: if NOSIS ships a <c>_v3</c> there
    /// must be a name left to tell them apart. Same "VR" caveat as <see cref="Cumplimiento12m"/>.
    /// </summary>
    [JsonPropertyName("atrasoCumplimiento60mV2")]
    public string? AtrasoCumplimiento60mV2 { get; set; }

    // ── Incidencias ───────────────────────────────────────────────────────────

    /// <summary>Cheques sin fondos en los últimos 12 meses (<c>HC_12m_SF_Cant</c>).</summary>
    [JsonPropertyName("chequesSinFondos12m")]
    public int? ChequesSinFondos12m { get; set; }

    /// <summary>
    /// Registra pago de multa en los últimos 12 meses (<c>IN_12m_PagoMulta_Tiene</c>).
    /// Arrives as the text "Si"/"No", not a JSON boolean.
    /// </summary>
    [JsonPropertyName("tienePagoMulta12m")]
    public bool? TienePagoMulta12m { get; set; }

    /// <summary>Pedidos de quiebra en los últimos 12 meses (<c>PQ_12m_Cant</c>).</summary>
    [JsonPropertyName("pedidosQuiebra12m")]
    public int? PedidosQuiebra12m { get; set; }

    /// <summary>
    /// <c>RC_Vig_Cant</c>. The vendor's expansion of the "RC" prefix is not documented in the
    /// material we hold, so the property keeps the key's shape rather than asserting a meaning.
    /// </summary>
    [JsonPropertyName("rcVigenteCantidad")]
    public int? RcVigenteCantidad { get; set; }

    /// <summary>
    /// <c>RC_Vig_Fuente</c> — the source reported alongside <see cref="RcVigenteCantidad"/>. Same
    /// undocumented "RC" prefix, so the property keeps the key's shape.
    /// </summary>
    [JsonPropertyName("rcVigenteFuente")]
    public string? RcVigenteFuente { get; set; }

    /// <summary>
    /// Juicios en los últimos 60 meses, contados por "RZ" (<c>JU_60m_RZ_Cant</c>). Distinct from
    /// <see cref="PedidosQuiebra12m"/>, which counts a different variable over a different window.
    /// The "RZ" segment is not expanded in the material we hold, so it is kept as key shape.
    /// </summary>
    [JsonPropertyName("juiciosRz60m")]
    public int? JuiciosRz60m { get; set; }

    /// <summary>
    /// Concursos y quiebras en los últimos 60 meses, contados por "RZ" (<c>CQ_60m_RZ_Cant</c>).
    /// Sibling of <see cref="JuiciosRz60m"/>; same "RZ" caveat. Distinct from
    /// <see cref="PedidosQuiebra12m"/>.
    /// </summary>
    [JsonPropertyName("concursosQuiebrasRz60m")]
    public int? ConcursosQuiebrasRz60m { get; set; }

    /// <summary>
    /// <c>MN_24m_Cant</c>. The vendor's expansion of the "MN" prefix is not documented in the
    /// material we hold, so the property keeps the key's shape rather than asserting a meaning.
    /// </summary>
    [JsonPropertyName("mnCantidad24m")]
    public int? MnCantidad24m { get; set; }

    // ── Aportes (AP_*) ────────────────────────────────────────────────────────

    /// <summary>Código de estado como empleador (<c>AP_Vig_Empleador_Estado_Cod</c>).</summary>
    [JsonPropertyName("estadoEmpleadorCod")]
    public int? EstadoEmpleadorCod { get; set; }

    /// <summary>Código de estado como empleado (<c>AP_Vig_Empleado_Estado_Cod</c>).</summary>
    [JsonPropertyName("estadoEmpleadoCod")]
    public int? EstadoEmpleadoCod { get; set; }

    // The nine counters below are the CUIT's own record as an EMPLEADOR (not empleado): how many
    // monthly aportes patronales it paid, part-paid and left unpaid over 6, 12 and 24 months.

    /// <summary>Aportes patronales pagados en los últimos 6 meses (<c>AP_6m_Empleador_Pagos_Cant</c>).</summary>
    [JsonPropertyName("empleadorPagos6m")]
    public int? EmpleadorPagos6m { get; set; }

    /// <summary>Aportes patronales con pago parcial en los últimos 6 meses (<c>AP_6m_Empleador_PagoParcial_Cant</c>).</summary>
    [JsonPropertyName("empleadorPagosParciales6m")]
    public int? EmpleadorPagosParciales6m { get; set; }

    /// <summary>Aportes patronales impagos en los últimos 6 meses (<c>AP_6m_Empleador_Impagos_Cant</c>).</summary>
    [JsonPropertyName("empleadorImpagos6m")]
    public int? EmpleadorImpagos6m { get; set; }

    /// <summary>Aportes patronales pagados en los últimos 12 meses (<c>AP_12m_Empleador_Pagos_Cant</c>).</summary>
    [JsonPropertyName("empleadorPagos12m")]
    public int? EmpleadorPagos12m { get; set; }

    /// <summary>Aportes patronales con pago parcial en los últimos 12 meses (<c>AP_12m_Empleador_PagoParcial_Cant</c>).</summary>
    [JsonPropertyName("empleadorPagosParciales12m")]
    public int? EmpleadorPagosParciales12m { get; set; }

    /// <summary>Aportes patronales impagos en los últimos 12 meses (<c>AP_12m_Empleador_Impagos_Cant</c>).</summary>
    [JsonPropertyName("empleadorImpagos12m")]
    public int? EmpleadorImpagos12m { get; set; }

    /// <summary>Aportes patronales pagados en los últimos 24 meses (<c>AP_24m_Empleador_Pagos_Cant</c>).</summary>
    [JsonPropertyName("empleadorPagos24m")]
    public int? EmpleadorPagos24m { get; set; }

    /// <summary>Aportes patronales con pago parcial en los últimos 24 meses (<c>AP_24m_Empleador_PagoParcial_Cant</c>).</summary>
    [JsonPropertyName("empleadorPagosParciales24m")]
    public int? EmpleadorPagosParciales24m { get; set; }

    /// <summary>Aportes patronales impagos en los últimos 24 meses (<c>AP_24m_Empleador_Impagos_Cant</c>).</summary>
    [JsonPropertyName("empleadorImpagos24m")]
    public int? EmpleadorImpagos24m { get; set; }

    // ── ART (SRT_*) ───────────────────────────────────────────────────────────

    /// <summary>Número de contrato de ART (<c>SRT_ContratoNro</c>).</summary>
    [JsonPropertyName("srtContratoNro")]
    public string? SrtContratoNro { get; set; }

    /// <summary>Aseguradora de riesgos del trabajo (<c>SRT_Aseguradora</c>).</summary>
    [JsonPropertyName("srtAseguradora")]
    public string? SrtAseguradora { get; set; }

    /// <summary>Estado del contrato de ART (<c>SRT_Estado</c>).</summary>
    [JsonPropertyName("srtEstado")]
    public string? SrtEstado { get; set; }

    /// <summary>
    /// <c>SRT_FaltaPago</c>. Kept as text, NOT <c>bool?</c>: unlike every boolean in this type it
    /// carries no <c>_Es</c>/<c>_Tiene</c> suffix, and we hold no raw sample proving it is a Si/No
    /// variable. Passthrough is lossless either way — a "Si" survives as the string "Si" — so this
    /// can be retyped once a real response settles it.
    /// </summary>
    [JsonPropertyName("srtFaltaPago")]
    public string? SrtFaltaPago { get; set; }

    // ── Comercio exterior (CE_*) ──────────────────────────────────────────────

    /// <summary>Registra actividad como importador (<c>CE_Importador_Es</c>). Text "Si"/"No" on the wire.</summary>
    [JsonPropertyName("esImportador")]
    public bool? EsImportador { get; set; }

    /// <summary>Registra actividad como exportador (<c>CE_Exportador_Es</c>). Text "Si"/"No" on the wire.</summary>
    [JsonPropertyName("esExportador")]
    public bool? EsExportador { get; set; }

    /// <summary>Registra actividad histórica como importador (<c>CE_Importador_Hist_Es</c>). Text "Si"/"No" on the wire.</summary>
    [JsonPropertyName("esImportadorHistorico")]
    public bool? EsImportadorHistorico { get; set; }

    /// <summary>Registra actividad histórica como exportador (<c>CE_Exportador_Hist_Es</c>). Text "Si"/"No" on the wire.</summary>
    [JsonPropertyName("esExportadorHistorico")]
    public bool? EsExportadorHistorico { get; set; }

    // ── Cumplimiento normativo (COM_*) ────────────────────────────────────────
    // Regulated AML/compliance flags. Both are TEXT, deliberately — see each property.

    /// <summary>
    /// <c>COM_SO_Es</c>, read as "sujeto obligado" (UIF). Kept as TEXT, NOT <c>bool?</c>, even
    /// though the key carries the <c>_Es</c> suffix this file otherwise treats as a Si/No marker:
    /// the vendor dictionary sample for this variable is the free text "Posible SO", which
    /// <see cref="NosisVariableProjection"/>'s ParseBool would map to null. Nulling a compliance
    /// signal is a regulatory miss, not a formatting one, so the lossless passthrough wins — a
    /// literal "Si" still survives as the string "Si". Retype only against a real raw sample.
    /// </summary>
    [JsonPropertyName("sujetoObligado")]
    public string? SujetoObligado { get; set; }

    /// <summary>
    /// <c>COM_PEP_Es</c>, persona expuesta políticamente. Kept as TEXT for exactly the reason
    /// given on <see cref="SujetoObligado"/>: the vendor answers this with graded free text
    /// ("Posible", …) rather than Si/No, and a bool would silently discard it.
    /// </summary>
    [JsonPropertyName("pep")]
    public string? Pep { get; set; }

    // ── Sociedades (SC_*) ─────────────────────────────────────────────────────

    /// <summary>
    /// <c>SC_60m_Cant</c> — a count over the last 60 months. The vendor's expansion of the "SC"
    /// prefix is not documented in the material we hold, so the property keeps the key's shape
    /// rather than asserting a meaning; the analyst reads it as sociedades, which is plausible
    /// beside <see cref="EsIntegranteSociedad"/> but unconfirmed. Named after
    /// <see cref="MnCantidad24m"/>, the file's precedent for an undocumented prefix + <c>_Cant</c>.
    /// </summary>
    [JsonPropertyName("scCantidad60m")]
    public int? ScCantidad60m { get; set; }

    // ── Consultas (CO_*) ──────────────────────────────────────────────────────

    /// <summary>Consultas recibidas en los últimos 12 meses (<c>CO_12m_Cant</c>).</summary>
    [JsonPropertyName("consultas12m")]
    public int? Consultas12m { get; set; }

    /// <summary>Consultas recibidas en los últimos 6 meses (<c>CO_6m_Cant</c>).</summary>
    [JsonPropertyName("consultas6m")]
    public int? Consultas6m { get; set; }

    /// <summary>Consultas recibidas en los últimos 3 meses (<c>CO_3m_Cant</c>).</summary>
    [JsonPropertyName("consultas3m")]
    public int? Consultas3m { get; set; }

    /// <summary>Consultas recibidas en el último mes (<c>CO_1m_Cant</c>).</summary>
    [JsonPropertyName("consultas1m")]
    public int? Consultas1m { get; set; }

    /// <summary>UTC timestamp of the lookup that produced this snapshot.</summary>
    [JsonPropertyName("fetchedAt")]
    public DateTime FetchedAt { get; set; }
}

// ── Flat-list projection ──────────────────────────────────────────────────────

/// <summary>
/// Projects the NOSIS FLAT <c>Variables[]</c> list into a typed <see cref="NosisData"/> by looking
/// each variable up by its <c>Nombre</c>, and owns the defensive value parsing.
/// Kept out of <c>NosisService</c> so the 82-key mapping and the es-AR number rules can be
/// unit-tested without any HTTP plumbing (visible to the test project via InternalsVisibleTo).
///
/// Not every key is answered by the vendor today: the analyst's primary set is mapped ahead of
/// NOSIS widening the configured <c>Nosis:VR</c> report group. A key the vendor does not send is
/// simply a dictionary miss and projects to null, exactly like a key it sends empty.
/// </summary>
internal static class NosisVariableProjection
{
    // NOSIS returns es-AR comma decimals — confirmed against the official variable dictionary
    // (SCO_PD = "0,41", tasas = "133,78"). Do NOT switch to InvariantCulture without a raw sample
    // proving otherwise.
    //
    // Consequence worth knowing before touching this: under es-AR '.' is the GROUP separator, so
    // "1.234.567,89" reads as 1234567.89 and "1.234" as 1234 — correct for a comma-decimal feed —
    // while a dot-decimal "0.41" would read as 41. That asymmetry is exactly why the culture must
    // not be flipped on a hunch. CasfogService parses with InvariantCulture and marks the vendor
    // format UNCONFIRMED; that is a known gap there, not a pattern to copy here.
    private static readonly NumberFormatInfo EsArNumbers = new CultureInfo("es-AR").NumberFormat;

    // ── Variable keys (exactly as NOSIS names them) ───────────────────────────
    private const string KeyIdentificacion   = "VI_Identificacion";
    private const string KeyRazonSocial      = "VI_RazonSocial";
    private const string KeyFallecido        = "VI_Fallecido_Es";
    private const string KeyDni              = "VI_DNI";
    private const string KeyFecNacimiento    = "VI_FecNacimiento";
    private const string KeySexo             = "VI_Sexo";
    private const string KeyEdad             = "VI_Edad";
    private const string KeyDomAfLoc         = "VI_DomAF_Loc";
    private const string KeyDomAfProv        = "VI_DomAF_Prov";
    private const string KeyInscripAfipAntig = "VI_Inscrip_Afip_Antiguedad";
    private const string KeyInscripMonotrib  = "VI_Inscrip_Monotributo";
    private const string KeyInscripIva       = "VI_Inscrip_IVA";
    private const string KeyJubilado         = "VI_Jubilado_Es";
    private const string KeyEmpleado         = "VI_Empleado_Es";
    private const string KeyEmpleado12m      = "VI_Empleado_12m_Es";
    private const string KeyIntegranteSoc    = "VI_IntegranteSociedad_Es";
    private const string KeyEmpleadosCant    = "VI_Empleados_Cant";
    private const string KeyAntiguedadLab    = "VI_AntiguedadLaboral";
    private const string KeyAntigLabEmpActual= "VI_AntiguedadLaboral_EmpleadorActual";
    private const string KeyEmpleadorCuit    = "VI_Empleador_CUIT";
    private const string KeyEmpleadorRz      = "VI_Empleador_RZ";
    private const string KeyEmpleadorEmpCant = "VI_Empleador_Empleados_Cant";
    private const string KeyDfTiene          = "DF_Tiene";
    private const string KeyNse              = "NSE";
    private const string KeyNsePercentil     = "NSE_Percentil";
    private const string KeyFacturacionEst   = "FE";
    private const string KeyScoreVig         = "SCO_Vig";
    private const string KeyScoreServVig     = "SCOServ_Vig";
    private const string KeyScoreVigFintech  = "SCO_Vig_Fintech";
    private const string KeyScore24m         = "SCO_24m";
    private const string KeyPeorSitVig       = "CI_Vig_PeorSit";
    private const string KeyCantBcosVig      = "CI_Vig_Total_CantBcos";
    private const string KeyMontoTotalVig    = "CI_Vig_Total_Monto";
    private const string KeyCantBcosSit1Vig  = "CI_Vig_Sit1_CantBcos";
    private const string KeyCompMensualVig   = "CI_Vig_CompMensual";
    private const string KeySit1Desde        = "CI_Sit1_Desde";
    private const string KeyPeorSit6m        = "CI_6m_PeorSit";
    private const string KeyPeorSit12m       = "CI_12m_PeorSit";
    private const string KeyCantBcos12m      = "CI_12m_Total_CantBcos";
    private const string KeyMontoTcVig       = "CI_Vig_TC_Monto";
    private const string KeyMontoLiCcVig     = "CI_Vig_Li_CC_Monto";
    private const string KeyMontoLiHiVig     = "CI_Vig_Li_HI_Monto";
    private const string KeyMontoLiPrVig     = "CI_Vig_Li_PR_Monto";
    private const string KeyMontoLiOprVig    = "CI_Vig_Li_OPR_Monto";
    private const string KeyLargoPlazoVig    = "CI_Vig_LargoPlazo";
    private const string KeyCortoPlazoVig    = "CI_Vig_CortoPlazo";
    private const string KeyCumplimiento12m  = "VR_12m_Cumplimiento";
    private const string KeyAtrasoCumpl60mV2 = "VR_60m_Atraso_Cumplimiento_v2";
    private const string KeyChequesSinFondos = "HC_12m_SF_Cant";
    private const string KeyPagoMulta12m     = "IN_12m_PagoMulta_Tiene";
    private const string KeyPedidosQuiebra   = "PQ_12m_Cant";
    private const string KeyRcVigCant        = "RC_Vig_Cant";
    private const string KeyRcVigFuente      = "RC_Vig_Fuente";
    private const string KeyJuiciosRz60m     = "JU_60m_RZ_Cant";
    private const string KeyConcQuiebraRz60m = "CQ_60m_RZ_Cant";
    private const string KeyMnCant24m        = "MN_24m_Cant";
    private const string KeyEstadoEmpleador  = "AP_Vig_Empleador_Estado_Cod";
    private const string KeyEstadoEmpleado   = "AP_Vig_Empleado_Estado_Cod";
    private const string KeyEmpPagos6m       = "AP_6m_Empleador_Pagos_Cant";
    private const string KeyEmpParcial6m     = "AP_6m_Empleador_PagoParcial_Cant";
    private const string KeyEmpImpagos6m     = "AP_6m_Empleador_Impagos_Cant";
    private const string KeyEmpPagos12m      = "AP_12m_Empleador_Pagos_Cant";
    private const string KeyEmpParcial12m    = "AP_12m_Empleador_PagoParcial_Cant";
    private const string KeyEmpImpagos12m    = "AP_12m_Empleador_Impagos_Cant";
    private const string KeyEmpPagos24m      = "AP_24m_Empleador_Pagos_Cant";
    private const string KeyEmpParcial24m    = "AP_24m_Empleador_PagoParcial_Cant";
    private const string KeyEmpImpagos24m    = "AP_24m_Empleador_Impagos_Cant";
    private const string KeySrtContratoNro   = "SRT_ContratoNro";
    private const string KeySrtAseguradora   = "SRT_Aseguradora";
    private const string KeySrtEstado        = "SRT_Estado";
    private const string KeySrtFaltaPago     = "SRT_FaltaPago";
    private const string KeyImportador       = "CE_Importador_Es";
    private const string KeyExportador       = "CE_Exportador_Es";
    private const string KeyImportadorHist   = "CE_Importador_Hist_Es";
    private const string KeyExportadorHist   = "CE_Exportador_Hist_Es";
    private const string KeyComSo            = "COM_SO_Es";
    private const string KeyComPep           = "COM_PEP_Es";
    private const string KeyScCant60m        = "SC_60m_Cant";
    private const string KeyConsultas12m     = "CO_12m_Cant";
    private const string KeyConsultas6m      = "CO_6m_Cant";
    private const string KeyConsultas3m      = "CO_3m_Cant";
    private const string KeyConsultas1m      = "CO_1m_Cant";

    /// <summary>
    /// Builds a <see cref="NosisData"/> from the vendor's flat variable list. <paramref name="estado"/>
    /// is supplied by the caller, which alone knows the HTTP/Resultado outcome. Never throws: a
    /// missing, empty or unparsable variable leaves its own field null.
    /// </summary>
    internal static NosisData BuildFromVariables(
        IEnumerable<NosisVariableNode>? variables,
        NosisConsultaEstado estado,
        DateTime fetchedAt)
    {
        var byName = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in variables ?? Enumerable.Empty<NosisVariableNode>())
        {
            if (string.IsNullOrWhiteSpace(v?.Nombre))
                continue;
            // Indexer, not ToDictionary: a duplicated Nombre must take the last value, never throw.
            byName[v.Nombre.Trim()] = v.Valor;
        }

        string? Raw(string key) => byName.TryGetValue(key, out var value) ? value : null;

        return new NosisData
        {
            Estado                          = estado,
            Identificacion                  = ParseString (Raw(KeyIdentificacion)),
            RazonSocial                     = ParseString (Raw(KeyRazonSocial)),
            EsFallecido                     = ParseBool   (Raw(KeyFallecido)),
            Dni                             = ParseString (Raw(KeyDni)),
            FechaNacimiento                 = ParseString (Raw(KeyFecNacimiento)),
            Sexo                            = ParseString (Raw(KeySexo)),
            Edad                            = ParseInt    (Raw(KeyEdad)),
            DomAfLocalidad                  = ParseString (Raw(KeyDomAfLoc)),
            DomAfProvincia                  = ParseString (Raw(KeyDomAfProv)),
            InscripAfipAntiguedad           = ParseInt    (Raw(KeyInscripAfipAntig)),
            InscripMonotributo              = ParseString (Raw(KeyInscripMonotrib)),
            InscripIva                      = ParseString (Raw(KeyInscripIva)),
            EsJubilado                      = ParseBool   (Raw(KeyJubilado)),
            EsEmpleado                      = ParseBool   (Raw(KeyEmpleado)),
            EsEmpleado12m                   = ParseBool   (Raw(KeyEmpleado12m)),
            EsIntegranteSociedad            = ParseBool   (Raw(KeyIntegranteSoc)),
            EmpleadosCant                   = ParseInt    (Raw(KeyEmpleadosCant)),
            AntiguedadLaboral               = ParseInt    (Raw(KeyAntiguedadLab)),
            AntiguedadLaboralEmpleadorActual = ParseInt (Raw(KeyAntigLabEmpActual)),
            EmpleadorCuit                   = ParseString (Raw(KeyEmpleadorCuit)),
            EmpleadorRazonSocial            = ParseString (Raw(KeyEmpleadorRz)),
            EmpleadorEmpleadosCant          = ParseInt    (Raw(KeyEmpleadorEmpCant)),
            DfTiene                         = ParseBool   (Raw(KeyDfTiene)),
            Nse                             = ParseString (Raw(KeyNse)),
            NsePercentil                    = ParseInt    (Raw(KeyNsePercentil)),
            FacturacionEstimada             = ParseInt    (Raw(KeyFacturacionEst)),
            ScoreVigente                    = ParseInt    (Raw(KeyScoreVig)),
            ScoreServVigente                = ParseInt    (Raw(KeyScoreServVig)),
            ScoreVigenteFintech             = ParseInt    (Raw(KeyScoreVigFintech)),
            Score24m                        = ParseInt    (Raw(KeyScore24m)),
            PeorSituacionVigente            = ParseString (Raw(KeyPeorSitVig)),
            CantidadBancosVigente           = ParseInt    (Raw(KeyCantBcosVig)),
            MontoTotalVigente               = ParseDecimal(Raw(KeyMontoTotalVig)),
            CantidadBancosSituacion1Vigente = ParseInt    (Raw(KeyCantBcosSit1Vig)),
            CompromisoMensualVigente        = ParseDecimal(Raw(KeyCompMensualVig)),
            Situacion1Desde                 = ParseString (Raw(KeySit1Desde)),
            PeorSituacion6m                 = ParseString (Raw(KeyPeorSit6m)),
            PeorSituacion12m                = ParseString (Raw(KeyPeorSit12m)),
            CantidadBancos12m               = ParseInt    (Raw(KeyCantBcos12m)),
            MontoTcVigente                  = ParseDecimal(Raw(KeyMontoTcVig)),
            MontoLiCcVigente                = ParseDecimal(Raw(KeyMontoLiCcVig)),
            MontoLiHiVigente                = ParseDecimal(Raw(KeyMontoLiHiVig)),
            MontoLiPrVigente                = ParseDecimal(Raw(KeyMontoLiPrVig)),
            MontoLiOprVigente               = ParseDecimal(Raw(KeyMontoLiOprVig)),
            LargoPlazoVigente               = ParseDecimal(Raw(KeyLargoPlazoVig)),
            CortoPlazoVigente               = ParseDecimal(Raw(KeyCortoPlazoVig)),
            Cumplimiento12m                 = ParseString (Raw(KeyCumplimiento12m)),
            AtrasoCumplimiento60mV2         = ParseString (Raw(KeyAtrasoCumpl60mV2)),
            ChequesSinFondos12m             = ParseInt    (Raw(KeyChequesSinFondos)),
            TienePagoMulta12m               = ParseBool   (Raw(KeyPagoMulta12m)),
            PedidosQuiebra12m               = ParseInt    (Raw(KeyPedidosQuiebra)),
            RcVigenteCantidad               = ParseInt    (Raw(KeyRcVigCant)),
            RcVigenteFuente                 = ParseString (Raw(KeyRcVigFuente)),
            JuiciosRz60m                    = ParseInt    (Raw(KeyJuiciosRz60m)),
            ConcursosQuiebrasRz60m          = ParseInt    (Raw(KeyConcQuiebraRz60m)),
            MnCantidad24m                   = ParseInt    (Raw(KeyMnCant24m)),
            EstadoEmpleadorCod              = ParseInt    (Raw(KeyEstadoEmpleador)),
            EstadoEmpleadoCod               = ParseInt    (Raw(KeyEstadoEmpleado)),
            EmpleadorPagos6m                = ParseInt    (Raw(KeyEmpPagos6m)),
            EmpleadorPagosParciales6m       = ParseInt    (Raw(KeyEmpParcial6m)),
            EmpleadorImpagos6m              = ParseInt    (Raw(KeyEmpImpagos6m)),
            EmpleadorPagos12m               = ParseInt    (Raw(KeyEmpPagos12m)),
            EmpleadorPagosParciales12m      = ParseInt    (Raw(KeyEmpParcial12m)),
            EmpleadorImpagos12m             = ParseInt    (Raw(KeyEmpImpagos12m)),
            EmpleadorPagos24m               = ParseInt    (Raw(KeyEmpPagos24m)),
            EmpleadorPagosParciales24m      = ParseInt    (Raw(KeyEmpParcial24m)),
            EmpleadorImpagos24m             = ParseInt    (Raw(KeyEmpImpagos24m)),
            SrtContratoNro                  = ParseString (Raw(KeySrtContratoNro)),
            SrtAseguradora                  = ParseString (Raw(KeySrtAseguradora)),
            SrtEstado                       = ParseString (Raw(KeySrtEstado)),
            SrtFaltaPago                    = ParseString (Raw(KeySrtFaltaPago)),
            EsImportador                    = ParseBool   (Raw(KeyImportador)),
            EsExportador                    = ParseBool   (Raw(KeyExportador)),
            EsImportadorHistorico           = ParseBool   (Raw(KeyImportadorHist)),
            EsExportadorHistorico           = ParseBool   (Raw(KeyExportadorHist)),
            SujetoObligado                  = ParseString (Raw(KeyComSo)),
            Pep                             = ParseString (Raw(KeyComPep)),
            ScCantidad60m                   = ParseInt    (Raw(KeyScCant60m)),
            Consultas12m                    = ParseInt    (Raw(KeyConsultas12m)),
            Consultas6m                     = ParseInt    (Raw(KeyConsultas6m)),
            Consultas3m                     = ParseInt    (Raw(KeyConsultas3m)),
            Consultas1m                     = ParseInt    (Raw(KeyConsultas1m)),
            FetchedAt                       = fetchedAt
        };
    }

    /// <summary>
    /// True when at least one of the 82 projected fields carries a value. Kept as an exact 1:1
    /// mirror of the key list above — every mapped key gets a clause, no exceptions — because
    /// <c>NosisService</c> uses this as the sole gate that demotes <c>DataFound</c> to
    /// <c>NoDataReported</c>, and a field that is mapped but not listed here would let a snapshot
    /// carrying real data be persisted as "nothing on file".
    /// </summary>
    internal static bool HasContent(NosisData d)
        => d.Identificacion is not null
        || d.RazonSocial is not null
        || d.EsFallecido is not null
        || d.Dni is not null
        || d.FechaNacimiento is not null
        || d.Sexo is not null
        || d.Edad is not null
        || d.DomAfLocalidad is not null
        || d.DomAfProvincia is not null
        || d.InscripAfipAntiguedad is not null
        || d.InscripMonotributo is not null
        || d.InscripIva is not null
        || d.EsJubilado is not null
        || d.EsEmpleado is not null
        || d.EsEmpleado12m is not null
        || d.EsIntegranteSociedad is not null
        || d.EmpleadosCant is not null
        || d.AntiguedadLaboral is not null
        || d.AntiguedadLaboralEmpleadorActual is not null
        || d.EmpleadorCuit is not null
        || d.EmpleadorRazonSocial is not null
        || d.EmpleadorEmpleadosCant is not null
        || d.DfTiene is not null
        || d.Nse is not null
        || d.NsePercentil is not null
        || d.FacturacionEstimada is not null
        || d.ScoreVigente is not null
        || d.ScoreServVigente is not null
        || d.ScoreVigenteFintech is not null
        || d.Score24m is not null
        || d.PeorSituacionVigente is not null
        || d.CantidadBancosVigente is not null
        || d.MontoTotalVigente is not null
        || d.CantidadBancosSituacion1Vigente is not null
        || d.CompromisoMensualVigente is not null
        || d.Situacion1Desde is not null
        || d.PeorSituacion6m is not null
        || d.PeorSituacion12m is not null
        || d.CantidadBancos12m is not null
        || d.MontoTcVigente is not null
        || d.MontoLiCcVigente is not null
        || d.MontoLiHiVigente is not null
        || d.MontoLiPrVigente is not null
        || d.MontoLiOprVigente is not null
        || d.LargoPlazoVigente is not null
        || d.CortoPlazoVigente is not null
        || d.Cumplimiento12m is not null
        || d.AtrasoCumplimiento60mV2 is not null
        || d.ChequesSinFondos12m is not null
        || d.TienePagoMulta12m is not null
        || d.PedidosQuiebra12m is not null
        || d.RcVigenteCantidad is not null
        || d.RcVigenteFuente is not null
        || d.JuiciosRz60m is not null
        || d.ConcursosQuiebrasRz60m is not null
        || d.MnCantidad24m is not null
        || d.EstadoEmpleadorCod is not null
        || d.EstadoEmpleadoCod is not null
        || d.EmpleadorPagos6m is not null
        || d.EmpleadorPagosParciales6m is not null
        || d.EmpleadorImpagos6m is not null
        || d.EmpleadorPagos12m is not null
        || d.EmpleadorPagosParciales12m is not null
        || d.EmpleadorImpagos12m is not null
        || d.EmpleadorPagos24m is not null
        || d.EmpleadorPagosParciales24m is not null
        || d.EmpleadorImpagos24m is not null
        || d.SrtContratoNro is not null
        || d.SrtAseguradora is not null
        || d.SrtEstado is not null
        || d.SrtFaltaPago is not null
        || d.EsImportador is not null
        || d.EsExportador is not null
        || d.EsImportadorHistorico is not null
        || d.EsExportadorHistorico is not null
        || d.SujetoObligado is not null
        || d.Pep is not null
        || d.ScCantidad60m is not null
        || d.Consultas12m is not null
        || d.Consultas6m is not null
        || d.Consultas3m is not null
        || d.Consultas1m is not null;

    // ── Defensive value parsing ───────────────────────────────────────────────
    // Every leaf arrives as a string. An unparsable field becomes null and never brings the whole
    // projection down.

    private static string? ParseString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return value.Trim();
    }

    private static decimal? ParseDecimal(string? value)
    {
        var s = ParseString(value);
        if (s is null)
            return null;
        return decimal.TryParse(s, NumberStyles.Number, EsArNumbers, out var d)
            ? d
            : null;
    }

    private static int? ParseInt(string? value)
    {
        var s = ParseString(value);
        if (s is null)
            return null;
        // Tolerate a decimal part (e.g. "3,0") by parsing as decimal then truncating.
        if (!decimal.TryParse(s, NumberStyles.Number, EsArNumbers, out var d))
            return null;
        // Out-of-range values map to null rather than overflowing the (int) cast — a single bad
        // field must never fail the whole lookup.
        if (d > int.MaxValue || d < int.MinValue)
            return null;
        return (int)decimal.Truncate(d);
    }

    /// <summary>
    /// NOSIS "Booleano" variables arrive as the TEXT "Si"/"No" — never a JSON boolean. Matched
    /// case-insensitively and tolerating the accented "Sí"; anything else (including "S", "1",
    /// "true" and the empty string) is null rather than a guess.
    /// </summary>
    private static bool? ParseBool(string? value)
    {
        var s = ParseString(value)?.ToLowerInvariant();
        return s switch
        {
            "si" or "sí" => true,
            "no"         => false,
            _            => null
        };
    }
}

// ── Raw API response shapes (deserialization only) ────────────────────────────

/// <summary>Envelope returned by <c>GET rest/variables</c>.</summary>
internal sealed class NosisApiResponse
{
    [JsonPropertyName("Contenido")]
    public NosisContenidoNode? Contenido { get; set; }
}

internal sealed class NosisContenidoNode
{
    [JsonPropertyName("Resultado")]
    public NosisResultadoNode? Resultado { get; set; }

    [JsonPropertyName("Datos")]
    public NosisDatosNode? Datos { get; set; }
}

internal sealed class NosisResultadoNode
{
    /// <summary>
    /// Vendor outcome code; 200 means OK. Held as a string because the vendor types it
    /// inconsistently (number vs. string) and it is compared, not arithmetic.
    ///
    /// What makes that safe is <c>FlexibleStringConverter</c>, registered on
    /// <c>NosisService.JsonOpts</c>: without it an unquoted <c>200</c> throws and takes the whole
    /// response down. Deserializing these nodes with any OTHER options object reintroduces the bug.
    /// </summary>
    [JsonPropertyName("Estado")]
    public string? Estado { get; set; }

    [JsonPropertyName("Novedad")]
    public string? Novedad { get; set; }
}

internal sealed class NosisDatosNode
{
    /// <summary>
    /// The FLAT variable list — NOSIS exposes no nested object, so every value is found by
    /// looking up its <c>Nombre</c>.
    /// </summary>
    [JsonPropertyName("Variables")]
    public List<NosisVariableNode>? Variables { get; set; }
}

internal sealed class NosisVariableNode
{
    [JsonPropertyName("Nombre")]
    public string? Nombre { get; set; }

    [JsonPropertyName("Valor")]
    public string? Valor { get; set; }

    [JsonPropertyName("Tipo")]
    public string? Tipo { get; set; }

    [JsonPropertyName("FechaAct")]
    public string? FechaAct { get; set; }
}
