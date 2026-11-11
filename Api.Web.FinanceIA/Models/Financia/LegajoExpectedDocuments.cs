// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// One line of a legajo's checklist: a document type the legajo is expected to hold, whether it is
/// required, the types that satisfy it just as well (e.g. a REIBP in place of the Bienes Personales
/// DDJJ), and an optional recency cut of its own.
/// </summary>
/// <param name="DocumentType">The type the line is named after (one of <see cref="DocumentTypes.LegajoTypes"/>).</param>
/// <param name="Required">False for an optional document (FlujoProyectado).</param>
/// <param name="Alternatives">Other types that satisfy the line. Empty when there are none.</param>
/// <param name="MaxAgeMonths">
/// The line's own recency cut in months (engine 0.10, per-tenant configuration). Null inherits the class
/// key of <see cref="LegajoDocumentAgeClasses"/> — pf_doc_antiguedad_max_meses or
/// pf_ddjj_anual_antiguedad_max_meses — and is always null on a timeless type.
/// </param>
public sealed record LegajoExpectedDocument(
    string DocumentType,
    bool Required,
    IReadOnlyList<string> Alternatives,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxAgeMonths = null)
{
    /// <summary>True when a document of <paramref name="documentType"/> satisfies this line.</summary>
    public bool IsSatisfiedBy(string documentType)
        => string.Equals(DocumentType, documentType, StringComparison.Ordinal)
        || Alternatives.Contains(documentType, StringComparer.Ordinal);
}

/// <summary>
/// The three buckets of the expected-documents matrix. Exento and Desconocido share one: a regime ARCA did
/// not settle reads the exento checklist (and the coverage says so with <c>regimen_desconocido</c>).
/// </summary>
public static class ExpectedDocumentRegimes
{
    /// <summary>Monotributo.</summary>
    public const string Monotributo = "Monotributo";

    /// <summary>IVA responsable inscripto.</summary>
    public const string ResponsableInscripto = "ResponsableInscripto";

    /// <summary>IVA exento, and a regime ARCA did not settle.</summary>
    public const string ExentoOrDesconocido = "ExentoOrDesconocido";

    /// <summary>Every bucket, in display order.</summary>
    public static readonly IReadOnlyList<string> All = [Monotributo, ResponsableInscripto, ExentoOrDesconocido];

    /// <summary>The bucket a <see cref="FiscalRegimes"/> value reads; anything else reads <see cref="ExentoOrDesconocido"/>.</summary>
    public static string Of(string? fiscalRegime) => fiscalRegime switch
    {
        FiscalRegimes.Monotributo          => Monotributo,
        FiscalRegimes.ResponsableInscripto => ResponsableInscripto,
        _                                  => ExentoOrDesconocido
    };
}

/// <summary>
/// The recency class of a legajo document type: what the coverage measures its age against when its line
/// states no <see cref="LegajoExpectedDocument.MaxAgeMonths"/> of its own.
/// </summary>
public static class LegajoDocumentAgeClasses
{
    /// <summary>No recency rule: the consent and the projected cash flow.</summary>
    public const string Timeless = "timeless";

    /// <summary>An annual return, measured against pf_ddjj_anual_antiguedad_max_meses.</summary>
    public const string Annual = "annual";

    /// <summary>Everything else, measured against pf_doc_antiguedad_max_meses.</summary>
    public const string PointInTime = "pointInTime";

    /// <summary>Types with no recency rule.</summary>
    public static readonly IReadOnlySet<string> TimelessTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        DocumentTypes.AutorizacionDatos, DocumentTypes.FlujoProyectado
    };

    /// <summary>Annual returns.</summary>
    public static readonly IReadOnlySet<string> AnnualReturnTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        DocumentTypes.Ganancias, DocumentTypes.BienesPersonales, DocumentTypes.Reibp
    };

    /// <summary>The class of <paramref name="documentType"/>.</summary>
    public static string Of(string documentType)
        => TimelessTypes.Contains(documentType) ? Timeless
         : AnnualReturnTypes.Contains(documentType) ? Annual
         : PointInTime;

    /// <summary>The class cut of <paramref name="documentType"/> under <paramref name="parameters"/>; null when timeless.</summary>
    public static int? ClassMaxAge(string documentType, PersonEvaluationParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        return Of(documentType) switch
        {
            Timeless => null,
            Annual   => parameters.DdjjAnualAntiguedadMaxMeses,
            _        => parameters.DocAntiguedadMaxMeses
        };
    }

    /// <summary>
    /// The cut the coverage applies to <paramref name="line"/>: none on a timeless type, else the line's own
    /// <see cref="LegajoExpectedDocument.MaxAgeMonths"/>, else its class key.
    /// </summary>
    public static int? EffectiveMaxAge(LegajoExpectedDocument line, PersonEvaluationParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(line);

        return TimelessTypes.Contains(line.DocumentType)
            ? null
            : line.MaxAgeMonths ?? ClassMaxAge(line.DocumentType, parameters);
    }
}

/// <summary>
/// The CODE DEFAULTS of the matrix fiscal regime → the documents a persona humana's legajo is expected to hold.
/// </summary>
/// <remarks>
/// Since engine 0.10 the matrix is per-tenant configuration (TenantSettings key
/// <see cref="TenantSettingKeys.PersonExpectedDocuments"/>, resolved by <c>IPersonExpectedDocumentsService</c>);
/// these lists are what a tenant without a row — or without a given regime in its row — reads. The matrix only
/// drives the legajo checklist and the coverage index: nothing blocks an upload, a classification, an analysis
/// or a suggestion on it.
///
/// <para>
/// AutorizacionDatos is required in every regime: without it no document of the legajo can be sent to the
/// model, and a tenant matrix must keep it so. FlujoProyectado is optional in every regime. An unknown regime
/// string reads as <see cref="FiscalRegimes.Desconocido"/>.
/// </para>
/// </remarks>
public static class LegajoExpectedDocuments
{
    private static readonly LegajoExpectedDocument Autorizacion =
        new(DocumentTypes.AutorizacionDatos, Required: true, Alternatives: []);

    private static readonly LegajoExpectedDocument BienesPersonalesOrReibp =
        new(DocumentTypes.BienesPersonales, Required: true, Alternatives: [DocumentTypes.Reibp]);

    private static readonly LegajoExpectedDocument Flujo =
        new(DocumentTypes.FlujoProyectado, Required: false, Alternatives: []);

    private static LegajoExpectedDocument RequiredOf(string documentType)
        => new(documentType, Required: true, Alternatives: []);

    /// <summary>Monotributo.</summary>
    public static readonly IReadOnlyList<LegajoExpectedDocument> Monotributo =
    [
        Autorizacion,
        RequiredOf(DocumentTypes.Monotributo),
        RequiredOf(DocumentTypes.CertificacionIngresos),
        RequiredOf(DocumentTypes.ManifestacionBienes),
        RequiredOf(DocumentTypes.DdjjIibb),
        BienesPersonalesOrReibp,
        Flujo
    ];

    /// <summary>IVA responsable inscripto.</summary>
    public static readonly IReadOnlyList<LegajoExpectedDocument> ResponsableInscripto =
    [
        Autorizacion,
        RequiredOf(DocumentTypes.Ganancias),
        RequiredOf(DocumentTypes.DdjjIva),
        RequiredOf(DocumentTypes.DdjjIibb),
        BienesPersonalesOrReibp,
        RequiredOf(DocumentTypes.ManifestacionBienes),
        RequiredOf(DocumentTypes.CertificacionIngresos),
        Flujo
    ];

    /// <summary>IVA exento, and a regime ARCA did not settle.</summary>
    public static readonly IReadOnlyList<LegajoExpectedDocument> ExentoOrDesconocido =
    [
        Autorizacion,
        RequiredOf(DocumentTypes.ManifestacionBienes),
        BienesPersonalesOrReibp,
        RequiredOf(DocumentTypes.CertificacionIngresos),
        Flujo
    ];

    /// <summary>The defaults by bucket (<see cref="ExpectedDocumentRegimes"/>).</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<LegajoExpectedDocument>> Defaults =
        new Dictionary<string, IReadOnlyList<LegajoExpectedDocument>>(StringComparer.Ordinal)
        {
            [ExpectedDocumentRegimes.Monotributo]          = Monotributo,
            [ExpectedDocumentRegimes.ResponsableInscripto] = ResponsableInscripto,
            [ExpectedDocumentRegimes.ExentoOrDesconocido]  = ExentoOrDesconocido
        };

    /// <summary>
    /// The types a matrix line may name, as its type or as an alternative: every legajo type but
    /// <see cref="DocumentTypes.Otro"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> ConfigurableTypes =
        DocumentTypes.LegajoTypes.Where(t => t != DocumentTypes.Otro).ToList();

    /// <summary>The DEFAULT checklist for <paramref name="fiscalRegime"/>.</summary>
    public static IReadOnlyList<LegajoExpectedDocument> For(string? fiscalRegime)
        => Defaults[ExpectedDocumentRegimes.Of(fiscalRegime)];
}
