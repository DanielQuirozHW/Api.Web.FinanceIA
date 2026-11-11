// Project : Api.Web.FinanceIA
// Created : 2026-11-10

using System.Text.Json;
using System.Text.Json.Serialization;
using Api.Web.FinanceIA.Clases.Json;

namespace Api.Web.FinanceIA.Models.Nosis;

// The NOSIS Manager "Informe Individual" as FinancIA keeps it (DDL 087, Entities.NosisReportJson) and
// serves it (EntityNosisResponse.report). One file for the whole contract, like NosisModels.cs: the
// records below are one wire shape and change together.
//
// COMPANY-LEVEL DATA ONLY. Nothing here names a person or carries a CUIT, DNI, e-mail, phone, address
// or CBU — the parser never reads those sections, and NosisReportPrivacyGuard drops any string shaped
// like one. Display only: no score, alert or prompt reads this. Property names are explicit so the
// stored JSON is the served JSON whatever the serializer options.

/// <summary>The parsed report plus the provenance of its import.</summary>
public sealed record NosisManagerReport
{
    /// <summary>The report's own date and time, from its footer: Argentina wall-clock, "yyyy-MM-ddTHH:mm:ss", no offset.</summary>
    [JsonPropertyName("reportDate")] public required string ReportDate { get; init; }

    /// <summary>When it was imported, UTC.</summary>
    [JsonPropertyName("importedAt")] public DateTime ImportedAt { get; init; }

    /// <summary>The parser that read it, e.g. "nm-1.0".</summary>
    [JsonPropertyName("parserVersion")] public required string ParserVersion { get; init; }

    /// <summary>Lower-case hex SHA-256 of the uploaded file (the file itself is not kept).</summary>
    [JsonPropertyName("fileSha256")] public string FileSha256 { get; init; } = string.Empty;

    /// <summary>Every section key → "ok" | "unreadable" | "absent". All keys are always present.</summary>
    [JsonPropertyName("sections")] public required IReadOnlyDictionary<string, string> Sections { get; init; }

    [JsonPropertyName("summary")] public NosisReportSummary? Summary { get; init; }

    [JsonPropertyName("evolucion24m")] public IReadOnlyList<NosisReportEvolucionMes>? Evolucion24m { get; init; }

    [JsonPropertyName("deudaPorEntidad")] public IReadOnlyList<NosisReportDeudaEntidad>? DeudaPorEntidad { get; init; }

    [JsonPropertyName("atrasos")] public IReadOnlyList<NosisReportAtrasosMes>? Atrasos { get; init; }

    [JsonPropertyName("aportesPatronales")] public IReadOnlyList<NosisReportAportesMes>? AportesPatronales { get; init; }

    [JsonPropertyName("chequesRechazadosPorMes")] public IReadOnlyList<NosisReportChequesMes>? ChequesRechazadosPorMes { get; init; }

    [JsonPropertyName("criteriosAceptacion")] public NosisReportCriteriosAceptacion? CriteriosAceptacion { get; init; }

    [JsonPropertyName("juicios")] public NosisReportJuicios? Juicios { get; init; }

    [JsonPropertyName("sisa")] public NosisReportSisa? Sisa { get; init; }

    [JsonPropertyName("srt")] public NosisReportSrt? Srt { get; init; }

    [JsonPropertyName("referenciasComerciales")] public NosisReportReferenciasComerciales? ReferenciasComerciales { get; init; }

    /// <summary>ÚLTIMOS BALANCES REDUCIDOS: up to the 3 newest closings, ascending. Not the ratios nor the PyME size table.</summary>
    [JsonPropertyName("balancesReducidos")] public IReadOnlyList<NosisReportBalanceReducido>? BalancesReducidos { get; init; }

    /// <summary>MERCADO ARGENTINO DE VALORES: amounts by instrument and by currency.</summary>
    [JsonPropertyName("mav")] public NosisReportMav? Mav { get; init; }

    /// <summary>ANTECEDENTES FISCALES: one entry per regime (no CUIT, no free text beyond the type).</summary>
    [JsonPropertyName("antecedentesFiscales")] public IReadOnlyList<NosisReportAntecedenteFiscal>? AntecedentesFiscales { get; init; }

    /// <summary>COMUNICADOS DEL BCRA Y DE FUENTES DIRECTAS: how many and when — never their text.</summary>
    [JsonPropertyName("comunicadosBcra")] public NosisReportComunicadosBcra? ComunicadosBcra { get; init; }

    /// <summary>IMPORTACIONES / EXPORTACIONES: flags and years — no chapter descriptions.</summary>
    [JsonPropertyName("comercioExterior")] public NosisReportComercioExterior? ComercioExterior { get; init; }

    /// <summary>MARCAS REGISTRADAS: the count only.</summary>
    [JsonPropertyName("marcasCantidad")] public int? MarcasCantidad { get; init; }

    /// <summary>
    /// The options the report is stored AND read with: the API's UTC DateTime converters, so
    /// <see cref="ImportedAt"/> is written exactly as the response writes it.
    /// </summary>
    [JsonIgnore]
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        Converters = { new UtcDateTimeConverter(), new NullableUtcDateTimeConverter() }
    };
}

/// <summary>Headline figures from PERFIL / INFORMACIÓN GENERAL, ARCA, MI PYME and RESUMEN CONFIGURABLE.</summary>
public sealed record NosisReportSummary
{
    [JsonPropertyName("score")] public int? Score { get; init; }
    [JsonPropertyName("situacionBcra")] public int? SituacionBcra { get; init; }
    /// <summary>Pesos.</summary>
    [JsonPropertyName("endeudamientoTotal")] public decimal? EndeudamientoTotal { get; init; }
    /// <summary>Pesos.</summary>
    [JsonPropertyName("compromisosMensuales")] public decimal? CompromisosMensuales { get; init; }
    /// <summary>Pesos.</summary>
    [JsonPropertyName("facturacionEstimadaMin")] public decimal? FacturacionEstimadaMin { get; init; }
    /// <summary>Pesos.</summary>
    [JsonPropertyName("facturacionEstimadaMax")] public decimal? FacturacionEstimadaMax { get; init; }
    [JsonPropertyName("tieneChequesRechazados")] public bool? TieneChequesRechazados { get; init; }
    [JsonPropertyName("referenciasComercialesCantidad")] public int? ReferenciasComercialesCantidad { get; init; }
    [JsonPropertyName("aportesImpagos12m")] public bool? AportesImpagos12m { get; init; }
    [JsonPropertyName("consultas3m")] public int? Consultas3m { get; init; }
    [JsonPropertyName("consultas12m")] public int? Consultas12m { get; init; }
    [JsonPropertyName("antiguedadAnios")] public int? AntiguedadAnios { get; init; }
    [JsonPropertyName("empleadosRango")] public string? EmpleadosRango { get; init; }
    [JsonPropertyName("actividadPrincipal")] public string? ActividadPrincipal { get; init; }
    [JsonPropertyName("certificadoMiPyme")] public NosisReportCertificadoMiPyme? CertificadoMiPyme { get; init; }
    [JsonPropertyName("arca")] public NosisReportArca? Arca { get; init; }
}

public sealed record NosisReportCertificadoMiPyme
{
    [JsonPropertyName("vigente")] public bool Vigente { get; init; }
    [JsonPropertyName("desde")] public DateOnly? Desde { get; init; }
    [JsonPropertyName("hasta")] public DateOnly? Hasta { get; init; }
}

public sealed record NosisReportArca
{
    [JsonPropertyName("inscripcion")] public DateOnly? Inscripcion { get; init; }
    [JsonPropertyName("ganancias")] public string? Ganancias { get; init; }
    [JsonPropertyName("iva")] public string? Iva { get; init; }
}

/// <summary>One month of EVOLUCIÓN ANTECEDENTES.</summary>
public sealed record NosisReportEvolucionMes
{
    /// <summary>"YYYY-MM".</summary>
    [JsonPropertyName("periodo")] public required string Periodo { get; init; }
    [JsonPropertyName("score")] public int? Score { get; init; }
    [JsonPropertyName("cantidadBancos")] public int? CantidadBancos { get; init; }
    [JsonPropertyName("situacion")] public int? Situacion { get; init; }
    [JsonPropertyName("cantidadTarjetas")] public int? CantidadTarjetas { get; init; }
    [JsonPropertyName("chequesRechazados")] public int? ChequesRechazados { get; init; }
    [JsonPropertyName("chequesRecuperados")] public int? ChequesRecuperados { get; init; }
    [JsonPropertyName("chequesNoRecuperados")] public int? ChequesNoRecuperados { get; init; }
    [JsonPropertyName("chequesNoPagoMulta")] public int? ChequesNoPagoMulta { get; init; }
    [JsonPropertyName("chequesPositivos")] public int? ChequesPositivos { get; init; }
    /// <summary>"P" | "PP" | "I" | null.</summary>
    [JsonPropertyName("aportesEmpleador")] public string? AportesEmpleador { get; init; }
    [JsonPropertyName("referenciasComerciales")] public int? ReferenciasComerciales { get; init; }
    [JsonPropertyName("consultasRecibidas")] public int? ConsultasRecibidas { get; init; }
}

/// <summary>One financial entity of BUREAU DE CRÉDITO and its months.</summary>
public sealed record NosisReportDeudaEntidad
{
    [JsonPropertyName("entidad")] public required string Entidad { get; init; }
    [JsonPropertyName("periodos")] public required IReadOnlyList<NosisReportDeudaPeriodo> Periodos { get; init; }
}

public sealed record NosisReportDeudaPeriodo
{
    [JsonPropertyName("periodo")] public required string Periodo { get; init; }
    [JsonPropertyName("situacion")] public int? Situacion { get; init; }
    /// <summary>Pesos.</summary>
    [JsonPropertyName("monto")] public decimal? Monto { get; init; }
}

/// <summary>One month of EVOLUCIÓN DE ATRASOS, pesos per bucket.</summary>
public sealed record NosisReportAtrasosMes
{
    [JsonPropertyName("periodo")] public required string Periodo { get; init; }
    [JsonPropertyName("normal")] public decimal? Normal { get; init; }
    [JsonPropertyName("d31a90")] public decimal? D31a90 { get; init; }
    [JsonPropertyName("d91a180")] public decimal? D91a180 { get; init; }
    [JsonPropertyName("d181a365")] public decimal? D181a365 { get; init; }
    [JsonPropertyName("mayor365")] public decimal? Mayor365 { get; init; }
}

/// <summary>One month of APORTES PATRONALES, as the report words each item.</summary>
public sealed record NosisReportAportesMes
{
    [JsonPropertyName("periodo")] public required string Periodo { get; init; }
    [JsonPropertyName("seguridadSocial")] public string? SeguridadSocial { get; init; }
    [JsonPropertyName("obraSocial")] public string? ObraSocial { get; init; }
    [JsonPropertyName("contribucionObraSocial")] public string? ContribucionObraSocial { get; init; }
}

/// <summary>One month's TOTAL of CHEQUES RECHAZADOS DEL BCRA. The per-cheque detail is never kept.</summary>
public sealed record NosisReportChequesMes
{
    [JsonPropertyName("periodo")] public required string Periodo { get; init; }
    [JsonPropertyName("cantidad")] public int Cantidad { get; init; }
    /// <summary>Pesos.</summary>
    [JsonPropertyName("monto")] public decimal Monto { get; init; }
}

public sealed record NosisReportCriteriosAceptacion
{
    [JsonPropertyName("nombre")] public string? Nombre { get; init; }
    [JsonPropertyName("resultado")] public string? Resultado { get; init; }
    /// <summary>True when the report marks the criteria "INFORMACIÓN SIN VALIDEZ - EN PROCESO DE PRUEBA".</summary>
    [JsonPropertyName("enPrueba")] public bool EnPrueba { get; init; }
    [JsonPropertyName("criterios")] public required IReadOnlyList<NosisReportCriterio> Criterios { get; init; }
}

public sealed record NosisReportCriterio
{
    [JsonPropertyName("nombre")] public required string Nombre { get; init; }
    [JsonPropertyName("resultado")] public required string Resultado { get; init; }
}

/// <summary>JUICIOS COMO DEMANDADO: count and date/type/jurisdiction only — never the parties or the case number.</summary>
public sealed record NosisReportJuicios
{
    [JsonPropertyName("comoDemandadoCantidad")] public int ComoDemandadoCantidad { get; init; }
    [JsonPropertyName("items")] public required IReadOnlyList<NosisReportJuicio> Items { get; init; }
}

public sealed record NosisReportJuicio
{
    [JsonPropertyName("fecha")] public DateOnly? Fecha { get; init; }
    [JsonPropertyName("tipo")] public string? Tipo { get; init; }
    [JsonPropertyName("jurisdiccion")] public string? Jurisdiccion { get; init; }
}

/// <summary>SISA status and categories — never the CBU.</summary>
public sealed record NosisReportSisa
{
    [JsonPropertyName("estado")] public string? Estado { get; init; }
    [JsonPropertyName("categorias")] public required IReadOnlyList<string> Categorias { get; init; }
}

/// <summary>OTROS ANTECEDENTES (SRT).</summary>
public sealed record NosisReportSrt
{
    [JsonPropertyName("aseguradora")] public string? Aseguradora { get; init; }
    [JsonPropertyName("vigente")] public bool? Vigente { get; init; }
    [JsonPropertyName("extinguidoPorFaltaPago")] public bool? ExtinguidoPorFaltaPago { get; init; }
}

/// <summary>REFERENCIAS COMERCIALES: count and source names — never the contact persons.</summary>
public sealed record NosisReportReferenciasComerciales
{
    [JsonPropertyName("cantidad")] public int Cantidad { get; init; }
    [JsonPropertyName("fuentes")] public required IReadOnlyList<string> Fuentes { get; init; }
}

/// <summary>One closing of ÚLTIMOS BALANCES REDUCIDOS, in pesos.</summary>
public sealed record NosisReportBalanceReducido
{
    /// <summary>The closing date ("MM/yyyy" → the month's last day; "yyyy" → 31 December).</summary>
    [JsonPropertyName("cierre")] public DateOnly Cierre { get; init; }
    [JsonPropertyName("activoCorriente")] public decimal? ActivoCorriente { get; init; }
    [JsonPropertyName("activoNoCorriente")] public decimal? ActivoNoCorriente { get; init; }
    [JsonPropertyName("totalActivo")] public decimal? TotalActivo { get; init; }
    [JsonPropertyName("pasivoCorriente")] public decimal? PasivoCorriente { get; init; }
    [JsonPropertyName("pasivoNoCorriente")] public decimal? PasivoNoCorriente { get; init; }
    [JsonPropertyName("totalPasivo")] public decimal? TotalPasivo { get; init; }
    [JsonPropertyName("patrimonioNeto")] public decimal? PatrimonioNeto { get; init; }
    [JsonPropertyName("ventasNetas")] public decimal? VentasNetas { get; init; }
    [JsonPropertyName("resultadoEjercicio")] public decimal? ResultadoEjercicio { get; init; }
}

public sealed record NosisReportMav
{
    [JsonPropertyName("porInstrumento")] public required IReadOnlyList<NosisReportMavInstrumento> PorInstrumento { get; init; }
    [JsonPropertyName("porMoneda")] public required IReadOnlyList<NosisReportMavMoneda> PorMoneda { get; init; }
}

public sealed record NosisReportMavInstrumento
{
    [JsonPropertyName("instrumento")] public required string Instrumento { get; init; }
    /// <summary>Pesos; null when the printed figure is ambiguous in a mixed-format block.</summary>
    [JsonPropertyName("monto")] public decimal? Monto { get; init; }
}

public sealed record NosisReportMavMoneda
{
    [JsonPropertyName("moneda")] public required string Moneda { get; init; }
    [JsonPropertyName("monto")] public decimal? Monto { get; init; }
}

public sealed record NosisReportAntecedenteFiscal
{
    [JsonPropertyName("organismo")] public string? Organismo { get; init; }
    [JsonPropertyName("tipo")] public string? Tipo { get; init; }
    /// <summary>The rate as a number ("5,00 %" → 5.00), never its text.</summary>
    [JsonPropertyName("alicuota")] public decimal? Alicuota { get; init; }
    [JsonPropertyName("vigenciaDesde")] public DateOnly? VigenciaDesde { get; init; }
    [JsonPropertyName("vigenciaHasta")] public DateOnly? VigenciaHasta { get; init; }
}

public sealed record NosisReportComunicadosBcra
{
    [JsonPropertyName("cantidad")] public int Cantidad { get; init; }
    [JsonPropertyName("fechas")] public required IReadOnlyList<DateOnly> Fechas { get; init; }
}

public sealed record NosisReportComercioExterior
{
    [JsonPropertyName("importador")] public bool? Importador { get; init; }
    [JsonPropertyName("exportador")] public bool? Exportador { get; init; }
    [JsonPropertyName("anios")] public required IReadOnlyList<int> Anios { get; init; }
}
