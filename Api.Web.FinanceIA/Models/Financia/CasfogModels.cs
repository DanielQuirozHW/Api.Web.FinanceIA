// Project : Api.Web.FinanceIA
// Created : 2026-07-29
// Modified: 2026-07-29

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Models.Financia;

// ── Enums ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Discriminates between a successful CASFOG lookup with content, a CUIT with
/// nothing on file (a valid "no data" answer), and a transient API failure.
/// </summary>
public enum CasfogConsultaEstado
{
    DataFound,
    NoDataReported,
    LookupFailed
}

// ── Stored / response models ──────────────────────────────────────────────────

/// <summary>
/// Normalised CASFOG (SGR central de deudores / garantías) snapshot for a CUIT,
/// persisted as JSON in <c>Entities.CasfogDataJson</c> and copied write-once into
/// <c>AnalysisResults.CasfogDataJson</c> when an analysis runs. Surfaced on the entity and analysis
/// responses, and — since §7.4, scoring algorithm version 8 — the <c>Garantias.PorSgr</c> rows feed
/// the guarantee-arrears penalty on the final score. Nothing else here reaches the engine.
/// Every collection is non-null (empty on NoDataReported / LookupFailed) so callers
/// never null-ref. Amounts come off the wire as strings and are parsed defensively;
/// the vendor decimal/thousands format is UNCONFIRMED (see CasfogService parsing).
/// </summary>
public sealed class CasfogData
{
    /// <summary>Outcome of the central de deudores query for this CUIT.</summary>
    [JsonPropertyName("estado")]
    public CasfogConsultaEstado Estado { get; set; }

    /// <summary>Guarantee metrics and per-SGR breakdown. Empty on NoDataReported / LookupFailed.</summary>
    [JsonPropertyName("garantias")]
    public CasfogGarantias Garantias { get; set; } = new();

    /// <summary>PyME certificates on file. Empty on NoDataReported / LookupFailed.</summary>
    [JsonPropertyName("certificados")]
    public List<CasfogCertificadoPyme> Certificados { get; set; } = new();

    /// <summary>Related parties (protector / partícipe / terceros). Empty on NoDataReported / LookupFailed.</summary>
    [JsonPropertyName("socios")]
    public CasfogSocios Socios { get; set; } = new();

    [JsonPropertyName("fetchedAt")]
    public DateTime FetchedAt { get; set; }
}

/// <summary>Aggregate guarantee metrics plus the per-SGR breakdown.</summary>
public sealed class CasfogGarantias
{
    /// <summary>Total number of guarantees ever granted. Null when absent or unparsable.</summary>
    [JsonPropertyName("cantidadGarantiasOtorgadas")]
    public int? CantidadGarantiasOtorgadas { get; set; }

    /// <summary>Total amount of guarantees ever granted. Null when absent or unparsable.</summary>
    [JsonPropertyName("montoGarantiasOtorgadas")]
    public decimal? MontoGarantiasOtorgadas { get; set; }

    /// <summary>Number of currently in-force guarantees. Null when absent or unparsable.</summary>
    [JsonPropertyName("cantidadGarantiasVigentes")]
    public int? CantidadGarantiasVigentes { get; set; }

    /// <summary>Gross outstanding balance of in-force guarantees. Null when absent or unparsable.</summary>
    [JsonPropertyName("saldoBrutoGarantiasVigentes")]
    public decimal? SaldoBrutoGarantiasVigentes { get; set; }

    /// <summary>Per-SGR guarantee detail. Empty when none reported.</summary>
    [JsonPropertyName("porSgr")]
    public List<CasfogGarantiaSgr> PorSgr { get; set; } = new();
}

/// <summary>Guarantee detail reported by a single SGR.</summary>
public sealed class CasfogGarantiaSgr
{
    [JsonPropertyName("sgr")]                public string?  Sgr                { get; set; }
    [JsonPropertyName("saldoBrutoVigente")] public decimal? SaldoBrutoVigente  { get; set; }
    [JsonPropertyName("saldoDeudaAbonadas")] public decimal? SaldoDeudaAbonadas { get; set; }
    [JsonPropertyName("cantidadEnMora")]    public int?     CantidadEnMora     { get; set; }

    /// <summary>
    /// Debt classification. Parsed as an integer and capped at 10; the cap follows the
    /// vendor's own convention (this repo's BCRA situación is a distinct 1–6 scale).
    /// </summary>
    [JsonPropertyName("situacionDeuda")]    public int?     SituacionDeuda     { get; set; }

    [JsonPropertyName("diasAtraso")]        public int?     DiasAtraso         { get; set; }
}

/// <summary>A PyME certificate on file at CASFOG. Dates are nullable (timezone-safe parse).</summary>
public sealed class CasfogCertificadoPyme
{
    [JsonPropertyName("numero")]          public string?   Numero          { get; set; }
    [JsonPropertyName("fechaRegistro")]   public DateTime? FechaRegistro   { get; set; }
    [JsonPropertyName("fechaVencimiento")] public DateTime? FechaVencimiento { get; set; }
    [JsonPropertyName("sector")]          public string?   Sector          { get; set; }
    [JsonPropertyName("categoria")]       public string?   Categoria       { get; set; }
}

/// <summary>Related parties grouped by role, from <c>data.general</c>.</summary>
public sealed class CasfogSocios
{
    [JsonPropertyName("socioProtector")] public List<CasfogSocio> SocioProtector { get; set; } = new();
    [JsonPropertyName("socioParticipe")] public List<CasfogSocio> SocioParticipe { get; set; } = new();
    [JsonPropertyName("cuitTerceros")]   public List<CasfogSocio> CuitTerceros   { get; set; } = new();
}

/// <summary>A single related party. CUIT is kept as a string for fidelity (vendor sends strings).</summary>
public sealed class CasfogSocio
{
    [JsonPropertyName("businessName")] public string? BusinessName { get; set; }
    [JsonPropertyName("cuit")]         public string? Cuit         { get; set; }
}

// ── Raw API response shapes (deserialization only) ────────────────────────────
// The vendor returns every leaf as a string, and uses "-" as the empty sentinel.
// These internal shapes mirror the wire exactly; CasfogService parses them into the
// public models above defensively (see the Parse* helpers there).

/// <summary>Envelope returned by <c>POST login</c>: <c>{ "success": { "token", "user" } }</c>.</summary>
internal sealed class CasfogLoginApiResponse
{
    [JsonPropertyName("success")]
    public CasfogLoginSuccessNode? Success { get; set; }
}

internal sealed class CasfogLoginSuccessNode
{
    [JsonPropertyName("token")]
    public string? Token { get; set; }

    /// <summary>The <c>user</c> object is captured but unused; kept opaque to avoid coupling to its shape.</summary>
    [JsonPropertyName("user")]
    public JsonElement? User { get; set; }
}

/// <summary>Envelope returned by <c>GET central_deudores/{cuit}</c>.</summary>
internal sealed class CasfogCentralDeudoresApiResponse
{
    /// <summary>
    /// Top-level success flag. Captured as a raw element because the vendor's typing is
    /// inconsistent (bool vs. string); interpreted defensively in CasfogService.
    /// </summary>
    [JsonPropertyName("success")]
    public JsonElement? Success { get; set; }

    [JsonPropertyName("data")]
    public CasfogDataNode? Data { get; set; }
}

internal sealed class CasfogDataNode
{
    [JsonPropertyName("general")]
    public CasfogGeneralNode? General { get; set; }

    [JsonPropertyName("informacion_de_garantias_de_la_pyme")]
    public CasfogGarantiasNode? Garantias { get; set; }

    [JsonPropertyName("certificados_pymes")]
    public List<CasfogCertificadoNode>? CertificadosPymes { get; set; }

    [JsonPropertyName("error")]
    public CasfogErrorNode? Error { get; set; }
}

internal sealed class CasfogGeneralNode
{
    [JsonPropertyName("socio_protector")]  public List<CasfogSocioNode>? SocioProtector { get; set; }
    [JsonPropertyName("socio_participe")]  public List<CasfogSocioNode>? SocioParticipe { get; set; }
    [JsonPropertyName("cuit_de_terceros")] public List<CasfogSocioNode>? CuitDeTerceros { get; set; }
}

internal sealed class CasfogSocioNode
{
    [JsonPropertyName("business_name")] public string? BusinessName { get; set; }
    [JsonPropertyName("cuit")]          public string? Cuit         { get; set; }
}

internal sealed class CasfogGarantiasNode
{
    [JsonPropertyName("metricas")] public CasfogMetricasNode? Metricas { get; set; }
    [JsonPropertyName("data")]     public List<CasfogGarantiaSgrNode>? Data { get; set; }
}

internal sealed class CasfogMetricasNode
{
    [JsonPropertyName("cantidad_total_garantias_otorgadas")] public string? CantidadTotalGarantiasOtorgadas { get; set; }
    [JsonPropertyName("monto_total_garantias_otorgadas")]    public string? MontoTotalGarantiasOtorgadas    { get; set; }
    [JsonPropertyName("cantidad_total_garantias_vigentes")]  public string? CantidadTotalGarantiasVigentes  { get; set; }
    [JsonPropertyName("saldo_bruto_garantias_vigentes")]     public string? SaldoBrutoGarantiasVigentes     { get; set; }
}

internal sealed class CasfogGarantiaSgrNode
{
    [JsonPropertyName("SGR")]                            public string? Sgr                { get; set; }
    [JsonPropertyName("SALDO_BRUTO_DE_GTIAS_VIGENTE")]   public string? SaldoBrutoVigente  { get; set; }
    [JsonPropertyName("SALDO_DEUDA_POR_GTIAS_ABONADAS")] public string? SaldoDeudaAbonadas { get; set; }
    [JsonPropertyName("CANTIDAD_GTIAS_EN_MORA")]         public string? CantidadEnMora     { get; set; }
    [JsonPropertyName("SITUACION_DE_LA_DEUDA")]          public string? SituacionDeuda     { get; set; }
    [JsonPropertyName("DIAS_ATRASO")]                    public string? DiasAtraso         { get; set; }
}

internal sealed class CasfogCertificadoNode
{
    [JsonPropertyName("NUMERO")]            public string? Numero           { get; set; }
    [JsonPropertyName("FECHA_REGISTRO")]    public string? FechaRegistro    { get; set; }
    [JsonPropertyName("FECHA_VENCIMIENTO")] public string? FechaVencimiento { get; set; }
    [JsonPropertyName("SECTOR")]            public string? Sector           { get; set; }
    [JsonPropertyName("CATEGORIA")]         public string? Categoria        { get; set; }
}

internal sealed class CasfogErrorNode
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
