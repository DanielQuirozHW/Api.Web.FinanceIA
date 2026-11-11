// Project : Api.Web.FinanceIA
// Created : 2026-06-14
// Modified: 2026-06-14

using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Models.Lufe;

/// <summary>Impuesto registrado para la entidad en el padrón Lufe.</summary>
public sealed class LufeImpuesto
{
    [JsonPropertyName("estado")]
    public string? Estado { get; set; }

    [JsonPropertyName("origen")]
    public string? Origen { get; set; }

    [JsonPropertyName("periodo_vigencia")]
    public int? PeriodoVigencia { get; set; }

    [JsonPropertyName("fecha_actualizacion")]
    public string? FechaActualizacion { get; set; }

    [JsonPropertyName("codigo_caracterizacion")]
    public int? CodigoCaracterizacion { get; set; }

    [JsonPropertyName("identificacion_estado_vigente")]
    public string? IdentificacionEstadoVigente { get; set; }
}

/// <summary>Actividad económica registrada para la entidad en el padrón Lufe.</summary>
public sealed class LufeActividad
{
    [JsonPropertyName("orden")]
    public string? Orden { get; set; }

    [JsonPropertyName("codigo")]
    public int? Codigo { get; set; }

    [JsonPropertyName("estado")]
    public string? Estado { get; set; }

    [JsonPropertyName("origen")]
    public string? Origen { get; set; }

    [JsonPropertyName("vigente")]
    public string? Vigente { get; set; }

    [JsonPropertyName("nomenclador")]
    public string? Nomenclador { get; set; }

    [JsonPropertyName("periodo_vigencia")]
    public int? PeriodoVigencia { get; set; }

    [JsonPropertyName("fecha_actualizacion")]
    public string? FechaActualizacion { get; set; }
}

/// <summary>Certificado MiPyME (SEPYME) asociado a la entidad.</summary>
public sealed class LufeCertificadoPyme
{
    [JsonPropertyName("categoria")]
    public string? Categoria { get; set; }

    [JsonPropertyName("desde")]
    public string? Desde { get; set; }

    [JsonPropertyName("fecha_emision")]
    public string? FechaEmision { get; set; }

    [JsonPropertyName("hasta")]
    public string? Hasta { get; set; }

    [JsonPropertyName("nro_registro")]
    public int? NroRegistro { get; set; }

    [JsonPropertyName("sector")]
    public string? Sector { get; set; }

    [JsonPropertyName("transaccion")]
    public long? Transaccion { get; set; }
}

/// <summary>Datos de empleo informados por la entidad en Lufe.</summary>
public sealed class LufeEmpleo
{
    [JsonPropertyName("Periodo")]
    public int? Periodo { get; set; }

    [JsonPropertyName("Fecha_present")]
    public string? FechaPresent { get; set; }

    [JsonPropertyName("numero_rectif")]
    public int? NumeroRectif { get; set; }

    [JsonPropertyName("Empleo")]
    public int? Empleo { get; set; }

    [JsonPropertyName("Masa_salarial_bruta")]
    public decimal? MasaSalarialBruta { get; set; }
}

/// <summary>Régimen impositivo al que pertenece la entidad en Lufe.</summary>
public sealed class LufeRegimen
{
    [JsonPropertyName("descripcionRegimen")]
    public string? DescripcionRegimen { get; set; }

    [JsonPropertyName("estado")]
    public string? Estado { get; set; }

    [JsonPropertyName("idImpuesto")]
    public int? IdImpuesto { get; set; }

    [JsonPropertyName("idRegimen")]
    public int? IdRegimen { get; set; }

    [JsonPropertyName("periodo")]
    public int? Periodo { get; set; }

    [JsonPropertyName("tipoRegimen")]
    public string? TipoRegimen { get; set; }
}

/// <summary>Datos básicos de la entidad en el padrón Lufe.</summary>
public sealed class LufeEntidad
{
    [JsonPropertyName("cuit")]
    public long Cuit { get; set; }

    [JsonPropertyName("nombre")]
    public string? Nombre { get; set; }

    [JsonPropertyName("forma_juridica")]
    public string? FormaJuridica { get; set; }

    [JsonPropertyName("actividad_principal")]
    public int ActividadPrincipal { get; set; }

    [JsonPropertyName("personeria")]
    public string? Personeria { get; set; }

    [JsonPropertyName("fecha_contrato_social")]
    public string? FechaContratoSocial { get; set; }

    [JsonPropertyName("impuestos")]
    public LufeImpuesto[]? Impuestos { get; set; }

    [JsonPropertyName("actividades")]
    public LufeActividad[]? Actividades { get; set; }

    [JsonPropertyName("certificado_pyme")]
    public LufeCertificadoPyme? CertificadoPyme { get; set; }

    [JsonPropertyName("domicilio_fiscal")]
    public string? DomicilioFiscal { get; set; }

    [JsonPropertyName("mes_cierre")]
    public int? MesCierre { get; set; }

    [JsonPropertyName("fecha_modificacion")]
    public string? FechaModificacion { get; set; }

    [JsonPropertyName("empleo")]
    public LufeEmpleo[]? Empleo { get; set; }

    [JsonPropertyName("regimenes")]
    public LufeRegimen[]? Regimenes { get; set; }
}

/// <summary>Documento publicado en Lufe para la entidad.</summary>
public sealed class LufeDocumento
{
    [JsonPropertyName("nombre")]
    public string? Nombre { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    // Populated by LufeService from LufeDocumentosResponse.Periodo (not from Lufe JSON per-document)
    [JsonPropertyName("periodo")]
    public string? Periodo { get; set; }
}

/// <summary>Respuesta del endpoint entidades/{cuit}/documentos.</summary>
public sealed class LufeDocumentosResponse
{
    [JsonPropertyName("periodo")]
    public string? Periodo { get; set; }

    [JsonPropertyName("documentos")]
    public LufeDocumento[]? Documentos { get; set; }
}

/// <summary>
/// Respuesta del endpoint entidades/{cuit}/indicadorespostbalance: cuatro series
/// independientes, cada una un mapa "YYYYMM" → monto. Lufe devuelve las claves en
/// orden DESCENDENTE y usa null para un mes sin declaración presentada (distinto de 0).
/// Ventas se descompone en melo (mercado local) + expo (exportaciones) cuando Lufe informa
/// el detalle; en los meses sin detalle ambas llegan null aunque Ventas tenga valor. Esos dos
/// nombres son la jerga del proveedor y se conservan SÓLO acá, en el binding: hacia afuera
/// salen como VentasMercadoLocal / VentasExportacion (ver <see cref="LufeVentaCompraMes"/>).
/// Los importes viajan como NÚMEROS JSON, no como strings, así que no interviene ninguna
/// cultura al deserializar (a diferencia de CasfogService, cuyo proveedor manda strings).
///
/// DTO interno de transporte: se deserializa, se fusiona en <see cref="LufeVentaCompraMes"/>
/// y se descarta. NUNCA debe colgar de <see cref="LufeData"/> — los diccionarios se
/// persistirían en LufeDataJson, y dos de los lectores del blob no llevan
/// AllowReadingFromString (además, PropertyNameCaseInsensitive no aplica a claves de
/// diccionario, sólo a nombres de propiedad).
/// </summary>
public sealed class LufePostBalanceResponse
{
    [JsonPropertyName("ventas")]
    public Dictionary<string, decimal?>? Ventas { get; set; }

    [JsonPropertyName("compras")]
    public Dictionary<string, decimal?>? Compras { get; set; }

    [JsonPropertyName("melo")]
    public Dictionary<string, decimal?>? Melo { get; set; }

    [JsonPropertyName("expo")]
    public Dictionary<string, decimal?>? Expo { get; set; }
}

/// <summary>
/// Ventas y compras declaradas para un mes calendario, derivadas de
/// entidades/{cuit}/indicadorespostbalance. Actividad posterior al último balance
/// presentado: la señal más fresca que hay sobre una entidad entre presentaciones anuales.
///
/// Casing: cada miembro anidado fija su clave con [JsonPropertyName], así que el blob
/// (JsonSerializer.Serialize pelado, raíz PascalCase) y la respuesta HTTP (política
/// camelCase de MVC) publican EXACTAMENTE la misma clave anidada. De eso dependen los
/// scripts de diagnóstico: las rutas JSON_VALUE de SQL Server distinguen mayúsculas.
/// Los nombres de los dos campos de la descomposición son NUESTROS, no los del proveedor
/// (melo → ventasMercadoLocal, expo → ventasExportacion); ver LufePostBalanceResponse.
/// </summary>
public sealed class LufeVentaCompraMes
{
    /// <summary>
    /// Período en formato "YYYYMM", tal cual lo devuelve Lufe. Es string (no numérico como
    /// <see cref="LufeIndicador.Periodo"/>) para preservar la clave textual del proveedor;
    /// coincide con la convención de BcraSituacionPeriodo.
    /// </summary>
    [JsonPropertyName("periodo")]
    public string Periodo { get; set; } = string.Empty;

    /// <summary>Ventas totales declaradas en el mes. Null = mes sin declaración, NO cero.</summary>
    [JsonPropertyName("ventas")]
    public decimal? Ventas { get; set; }

    /// <summary>Compras totales declaradas en el mes. Null = mes sin declaración, NO cero.</summary>
    [JsonPropertyName("compras")]
    public decimal? Compras { get; set; }

    /// <summary>
    /// Ventas al mercado local. Viene de la clave "melo" del proveedor, renombrada de este lado:
    /// la abreviatura de Lufe no significa nada fuera de su propio vocabulario.
    /// Null cuando Lufe no informa la descomposición.
    /// </summary>
    [JsonPropertyName("ventasMercadoLocal")]
    public decimal? VentasMercadoLocal { get; set; }

    /// <summary>
    /// Ventas por exportación. Viene de la clave "expo" del proveedor, renombrada de este lado.
    /// Null cuando Lufe no informa la descomposición; 0 es un valor real (mes sin exportar).
    /// </summary>
    [JsonPropertyName("ventasExportacion")]
    public decimal? VentasExportacion { get; set; }
}

/// <summary>Result of a Lufe document download attempt.</summary>
public sealed record LufeDocumentDownload(bool Found, byte[]? Content, string? ContentType);

/// <summary>Autoridad (directivo/accionista) de la entidad registrada en Lufe.</summary>
public sealed class LufeAutoridad
{
    [JsonPropertyName("cuit")]
    public long Cuit { get; set; }

    [JsonPropertyName("denominacion")]
    public string? Denominacion { get; set; }

    [JsonPropertyName("es_accionista")]
    public int? EsAccionista { get; set; }

    [JsonPropertyName("porc_accionista")]
    public decimal? PorcAccionista { get; set; }

    [JsonPropertyName("cargo")]
    public string? Cargo { get; set; }
}

/// <summary>
/// Indicadores financieros calculados por Lufe a partir de los estados contables.
/// Todos los campos son opcionales — Lufe puede omitir indicadores no calculados.
/// </summary>
public sealed class LufeIndicador
{
    [JsonPropertyName("periodo")]
    public decimal? Periodo { get; set; }

    [JsonPropertyName("fechapresentacion")]
    public decimal? FechaPresentacion { get; set; }

    [JsonPropertyName("rentabilidad")]
    public decimal? Rentabilidad { get; set; }

    [JsonPropertyName("liquidez_cte")]
    public decimal? LiquidezCte { get; set; }

    [JsonPropertyName("endeudamiento")]
    public decimal? Endeudamiento { get; set; }

    [JsonPropertyName("capital_trabajo")]
    public decimal? CapitalTrabajo { get; set; }

    [JsonPropertyName("plazo_medio_ctas_a_cobar")]
    public decimal? PlazoMedioCtasACobar { get; set; }

    [JsonPropertyName("rotacion_inventarios")]
    public decimal? RotacionInventarios { get; set; }

    [JsonPropertyName("plazo_medio_ctas_a_pagar")]
    public decimal? PlazoMedioCtasAPagar { get; set; }

    [JsonPropertyName("compras_totales_insumos")]
    public decimal? ComprasTotalesInsumos { get; set; }

    [JsonPropertyName("vtas_mensuales_prom")]
    public decimal? VtasMensualesProm { get; set; }

    [JsonPropertyName("inmovilizacion_bienes_de_uso")]
    public decimal? InmovilizacionBienesDeUso { get; set; }

    [JsonPropertyName("productividad_bs_de_uso_afectados_exportacion")]
    public decimal? ProductividadBsDeUsoAfectadosExportacion { get; set; }

    [JsonPropertyName("incidencia_amortizaciones_bs_uso_sobre_costos")]
    public decimal? IncidenciaAmortizacionesBsUsoSobreCostos { get; set; }

    [JsonPropertyName("solvencia")]
    public decimal? Solvencia { get; set; }

    [JsonPropertyName("endeudamiento_diferido")]
    public decimal? EndeudamientoDiferido { get; set; }

    [JsonPropertyName("liquidez_acida")]
    public decimal? LiquidezAcida { get; set; }

    [JsonPropertyName("ebitda")]
    public decimal? Ebitda { get; set; }

    [JsonPropertyName("retorno_activo_total")]
    public decimal? RetornoActivoTotal { get; set; }

    [JsonPropertyName("retorno_patrimonio_neto")]
    public decimal? RetornoPatrimonioNeto { get; set; }

    [JsonPropertyName("utilidad_neta_patrimonio_neto")]
    public decimal? UtilidadNetaPatrimonioNeto { get; set; }

    [JsonPropertyName("utilidad_bruta_costos")]
    public decimal? UtilidadBrutaCostos { get; set; }

    [JsonPropertyName("ebitda_vtas")]
    public decimal? EbitdaVtas { get; set; }

    [JsonPropertyName("ebit")]
    public decimal? Ebit { get; set; }

    [JsonPropertyName("ventas_pb")]
    public decimal? VentasPb { get; set; }

    [JsonPropertyName("compras_pb")]
    public decimal? ComprasPb { get; set; }

    [JsonPropertyName("ebit_vtas")]
    public decimal? EbitVtas { get; set; }

    // evolucion_vta is null in the sample and its true shape (scalar/array/object) is undocumented.
    // Mapped as JsonElement? so any JSON shape deserializes without throwing — a decimal? mapping
    // would throw JsonException on an array and null the entire LufeIndicador, losing every other
    // field. JsonElement? round-trips back into LufeDataJson unchanged.
    [JsonPropertyName("evolucion_vta")]
    public System.Text.Json.JsonElement? EvolucionVta { get; set; }
}

/// <summary>
/// Root DTO persisted as JSON in <c>Entities.LufeDataJson</c> (and copied write-once into
/// <c>AnalysisResults.LufeDataJson</c>). Aggregates entidad, autoridades, indicadores,
/// documentos, periodos and post-balance monthly activity from six parallel Lufe API calls.
///
/// Root properties deliberately carry NO <c>[JsonPropertyName]</c>: the blob is written with a
/// plain <c>JsonSerializer.Serialize(lufeData)</c> (PascalCase) while HTTP responses go through
/// MVC's camelCase policy, which is what surfaces <c>indicadoresHistoricos</c> /
/// <c>ventasComprasMensual</c> to the portal. Nested members do carry the attribute.
///
/// PII: this type holds identifying data (<c>Entidad.Nombre</c>, <c>Entidad.Cuit</c>,
/// <c>Entidad.DomicilioFiscal</c>, <c>Autoridades[].Denominacion</c>). Per the project rule it
/// must NEVER be serialized wholesale into a Claude prompt — the model analyses figures, not
/// identities. No prompt builder reads <c>LufeDataJson</c> today; keep it that way.
/// </summary>
public sealed class LufeData
{
    public LufeEntidad? Entidad { get; set; }
    public LufeAutoridad[]? Autoridades { get; set; }
    public LufeIndicador? Indicadores { get; set; }
    public LufeDocumento[]? Documentos { get; set; }

    /// <summary>
    /// Historical indicators, one entry per available period (capped, chronological ascending),
    /// fetched from <c>entidades/{cuit}/periodos/{periodo}/indicadores</c>. Enables charting the
    /// evolution of sales/purchases/ratios over time. Null when no historical data is available.
    /// </summary>
    public LufeIndicador[]? IndicadoresHistoricos { get; set; }

    /// <summary>
    /// Post-balance monthly sales/purchases series, one entry per calendar month
    /// (chronological ascending), fetched from <c>entidades/{cuit}/indicadorespostbalance</c>.
    /// Lufe returns a rolling ~18-month window with the keys in DESCENDING order; they are
    /// re-sorted here. A month whose every value is null is omitted, so the series is RAGGED —
    /// variable length, and a gap is not a zero. Null when the call failed or yielded nothing
    /// (never an empty list).
    /// </summary>
    public List<LufeVentaCompraMes>? VentasComprasMensual { get; set; }

    /// <summary>
    /// Why <see cref="VentasComprasMensual"/> is what it is, when that is not "freshly answered":
    /// <see cref="LufeVentasComprasEstados.LookupFailed"/> — the indicadorespostbalance call FAILED and
    /// nothing usable was stored to keep, so the null series is an outage, not "no data"; or
    /// <see cref="LufeVentasComprasEstados.CarriedForward"/> — the call failed and the series is the
    /// previously stored one, as of <see cref="VentasComprasMensualAsOf"/>. Null (and omitted from the
    /// JSON) for a fresh answer, including a genuine empty one, so an untouched snapshot serializes
    /// byte-identical to before.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VentasComprasMensualEstado { get; set; }

    /// <summary>
    /// When a <see cref="LufeVentasComprasEstados.CarriedForward"/> series was actually fetched (UTC):
    /// the snapshot date it was carried from, kept across repeated carries. Null otherwise, and omitted.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? VentasComprasMensualAsOf { get; set; }
}

/// <summary>Values of <see cref="LufeData.VentasComprasMensualEstado"/>.</summary>
public static class LufeVentasComprasEstados
{
    /// <summary>The post-balance call failed and no stored series was available to keep.</summary>
    public const string LookupFailed = "lookup_failed";

    /// <summary>The post-balance call failed and the stored series was kept.</summary>
    public const string CarriedForward = "carried_forward";
}

/// <summary>
/// Discriminates between a CUIT with a legajo in Lufe, a CUIT Lufe answered for but has no legajo
/// on file, and a transport failure. Same shape as <c>BcraDeudaConsultaEstado</c>: collapsing
/// NoLegajoReported into LookupFailed turns a legitimate not-found into a retryable outage.
/// </summary>
public enum LufeConsultaEstado
{
    /// <summary>Lufe returned data for the CUIT (possibly a partial snapshot).</summary>
    LegajoFound,

    /// <summary>
    /// Lufe answered, but the CUIT has no legajo or its PyME has not authorized sharing it with this
    /// entity: any 4xx except 408/429 (classified by status class, not body text — Lufe's 401 body
    /// does not reliably carry the documented "No se encontró ninguna Entidad" message), or a 200
    /// with no entity / Cuit 0.
    /// </summary>
    NoLegajoReported,

    /// <summary>
    /// Network error, timeout, 5xx, 408, 429, or a 200 whose body is not valid JSON — nothing
    /// usable came back and the answer says nothing about the legajo.
    /// </summary>
    LookupFailed
}

/// <summary>
/// Outcome of <c>ILufeService.GetLufeDataAsync</c>. <see cref="Data"/> is non-null exactly when
/// <see cref="Estado"/> is <see cref="LufeConsultaEstado.LegajoFound"/>.
/// </summary>
public sealed record LufeLookupResult(LufeConsultaEstado Estado, LufeData? Data);
