// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// The classification prompt and its tool schema. Versioned: a test fingerprints
/// <see cref="SystemPrompt"/> + <see cref="ToolSchemaJson"/> against <see cref="PromptVersion"/>
/// (the ExtractionPromptTests pattern), so neither can change without a version bump.
/// </summary>
/// <remarks>
/// ⚠ PERSONAL DATA. Nothing here, and nothing the request builder appends, names the legajo's
/// person: no name, no CUIT, not even the type the analyst DECLARED (so the detection stays
/// independent of it). The model reads the document; C# compares what it read with the legajo
/// afterwards. A test asserts the serialized request contains neither the legajo's CUIT nor its
/// FullName.
/// </remarks>
public static class LegajoClassificationPrompt
{
    /// <summary>Bump together with the fingerprint registered in LegajoClassificationPromptTests.</summary>
    public const string PromptVersion = "1.0";

    /// <summary>Version of the tool's input schema, stored on every run.</summary>
    public const string SchemaVersion = "1.0";

    /// <summary>The single tool the model is forced to call.</summary>
    public const string ToolName = "record_document_classification";

    /// <summary>The detected type when the document is none of the legajo types.</summary>
    public const string UnknownType = "Desconocido";

    /// <summary>The types the model may answer with: the legajo types (incl. Otro) plus <see cref="UnknownType"/>.</summary>
    public static readonly IReadOnlyList<string> DetectableTypes = [.. DocumentTypes.LegajoTypes, UnknownType];

    /// <summary>The system prompt. English instructions, Argentine document vocabulary, es-AR note.</summary>
    public const string SystemPrompt =
        "You classify ONE Argentine tax or patrimonial document belonging to a natural person (persona humana). " +
        "You do not extract figures. Record your reading by calling the record_document_classification tool exactly once.\n" +
        "\n" +
        "Document types (use these exact codes):\n" +
        "- ManifestacionBienes: manifestación de bienes / estado de situación patrimonial of a person, usually with an accountant's report.\n" +
        "- BienesPersonales: DDJJ del Impuesto sobre los Bienes Personales (F.762/A and its annexes).\n" +
        "- Reibp: Régimen Especial de Ingreso del Impuesto sobre los Bienes Personales (REIBP) filing or receipt.\n" +
        "- Ganancias: DDJJ del Impuesto a las Ganancias de personas humanas (F.711 / F.715 and annexes).\n" +
        "- CertificacionIngresos: certificación de ingresos issued by a contador público.\n" +
        "- DdjjIva: DDJJ de IVA (F.731 / F.2002).\n" +
        "- DdjjIibb: DDJJ de Ingresos Brutos (Convenio Multilateral CM03/CM05 or a provincial local regime).\n" +
        "- Monotributo: monotributo constancia de inscripción, credencial de pago or recategorización.\n" +
        "- FlujoProyectado: a projected cash flow.\n" +
        "- AutorizacionDatos: a signed authorization to process personal data.\n" +
        "- Otro: a recognisable document that is none of the above.\n" +
        "- Desconocido: you cannot tell what the document is.\n" +
        "\n" +
        "Rules:\n" +
        "- holder_cuit: the CUIT/CUIL of the person the document is ABOUT (the taxpayer or the declarant), as 11 digits " +
        "without hyphens. Never the CUIT of the accountant, the consejo profesional, an employer, a bank or AFIP/ARCA. " +
        "If the holder's CUIT is not printed, or you are not sure which CUIT is the holder's, use null. " +
        "When you give one, give the 1-based page and the literal text you read it from.\n" +
        "- period_end: the close of the period the document covers (for an annual DDJJ, 31 December of the fiscal year). " +
        "measurement_date: the date a statement of assets is measured at. Dates as YYYY-MM-DD, or null.\n" +
        "- fiscal_year: the fiscal year (período fiscal) the document refers to, or null.\n" +
        "- engagement_type: the accountant's work on the document. Certificacion for a certificación, Compilacion for an " +
        "informe de compilación, Ninguno when no accountant signed it, Desconocido when you cannot tell.\n" +
        "- is_composite: true when the PDF contains several distinct documents (for example a Ganancias DDJJ followed by a " +
        "Bienes Personales DDJJ). Then list each part in segments with its type and 1-based page range; otherwise segments is empty.\n" +
        "- confidence: from 0 to 1, how sure you are of document_type and holder_cuit together.\n" +
        "- note: one short sentence in Spanish (Argentina) for the analyst. Do not copy personal names, addresses or ID numbers into it.";

    /// <summary>
    /// The tool's input_schema, sent verbatim with <c>strict: true</c>. Strict tool use accepts no
    /// numeric or length constraints, so ranges (confidence 0..1, pages ≥ 1) are enforced in C#.
    /// </summary>
    public const string ToolSchemaJson =
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["document_type", "fiscal_year", "period_end", "measurement_date", "holder_cuit", "engagement_type", "is_composite", "segments", "confidence", "note"],
          "properties": {
            "document_type": { "type": "string", "enum": ["ManifestacionBienes", "BienesPersonales", "Reibp", "Ganancias", "CertificacionIngresos", "DdjjIva", "DdjjIibb", "Monotributo", "FlujoProyectado", "AutorizacionDatos", "Otro", "Desconocido"] },
            "fiscal_year": { "type": ["integer", "null"] },
            "period_end": { "type": ["string", "null"], "description": "YYYY-MM-DD" },
            "measurement_date": { "type": ["string", "null"], "description": "YYYY-MM-DD" },
            "holder_cuit": {
              "type": "object",
              "additionalProperties": false,
              "required": ["cuit", "page", "quote"],
              "properties": {
                "cuit": { "type": ["string", "null"], "description": "11 digits, no hyphens" },
                "page": { "type": ["integer", "null"] },
                "quote": { "type": ["string", "null"] }
              }
            },
            "engagement_type": { "type": "string", "enum": ["Certificacion", "Compilacion", "Ninguno", "Desconocido"] },
            "is_composite": { "type": "boolean" },
            "segments": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["document_type", "from_page", "to_page"],
                "properties": {
                  "document_type": { "type": "string", "enum": ["ManifestacionBienes", "BienesPersonales", "Reibp", "Ganancias", "CertificacionIngresos", "DdjjIva", "DdjjIibb", "Monotributo", "FlujoProyectado", "AutorizacionDatos", "Otro", "Desconocido"] },
                  "from_page": { "type": "integer" },
                  "to_page": { "type": "integer" }
                }
              }
            },
            "confidence": { "type": "number" },
            "note": { "type": "string" }
          }
        }
        """;

    /// <summary>The tool description sent beside the schema.</summary>
    public const string ToolDescription =
        "Records what the document is, its period, whose it is, and whether it bundles several documents.";

    /// <summary>The closing text block of the user turn. Carries page counts only.</summary>
    public static string BuildUserText(int totalPages, int includedPages)
        => includedPages < totalPages
            ? $"Classify this document. It has {totalPages} pages; only the first {includedPages} are included as images."
            : "Classify this document.";
}
