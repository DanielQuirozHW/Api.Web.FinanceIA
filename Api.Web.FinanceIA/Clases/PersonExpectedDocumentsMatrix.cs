// Project : Api.Web.FinanceIA
// Created : 2026-10-31

using System.Text.Json;
using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Settings;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// The tenant's expected-documents matrix as text: validation (es-AR messages, field paths such as
/// <c>regimes.Monotributo[2].documentType</c>), the stored JSON and its parse. Pure — no I/O, no logging.
/// </summary>
public static class PersonExpectedDocumentsMatrix
{
    /// <summary>The <c>requirement</c> of a required line.</summary>
    public const string RequirementRequired = "required";

    /// <summary>The <c>requirement</c> of an optional line.</summary>
    public const string RequirementOptional = "optional";

    /// <summary>The smallest <c>maxAgeMonths</c> a line may state (the pf_ age keys' bound).</summary>
    public const int MinMaxAgeMonths = 1;

    /// <summary>The largest <c>maxAgeMonths</c> a line may state (the pf_ age keys' bound).</summary>
    public const int MaxMaxAgeMonths = 60;

    /// <summary>Why a stored row was not used: it is not JSON of the expected shape.</summary>
    public const string ErrorUnparseable = "unparseable";

    /// <summary>Why a stored row was not used: it parses but breaks a validation rule.</summary>
    public const string ErrorInvalid = "invalid";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly IReadOnlySet<string> Configurable =
        new HashSet<string>(LegajoExpectedDocuments.ConfigurableTypes, StringComparer.Ordinal);

    private const string MsgNoRegimes      = "Se requiere al menos un régimen.";
    private const string MsgUnknownRegime  = "Régimen desconocido. Los regímenes válidos son Monotributo, ResponsableInscripto y ExentoOrDesconocido.";
    private const string MsgEmptyRegime    = "El régimen necesita al menos un documento.";
    private const string MsgEmptyLine      = "La línea está vacía.";
    private const string MsgTypeMissing    = "Se requiere el tipo de documento.";
    private const string MsgTypeInvalid    = "El tipo de documento no es uno de los tipos configurables del legajo.";
    private const string MsgTypeRepeated   = "El tipo de documento está repetido en el régimen.";
    private const string MsgRequirement    = "El requisito debe ser \"required\" (obligatorio) u \"optional\" (opcional).";
    private const string MsgAltInvalid     = "La alternativa no es uno de los tipos configurables del legajo.";
    private const string MsgAltSelf        = "Un documento no puede ser alternativa de sí mismo.";
    private const string MsgAltIsLine      = "La alternativa ya es un documento del régimen.";
    private const string MsgAltRepeated    = "La alternativa ya figura en otra línea del régimen.";
    private const string MsgMaxAgeRange    = "La antigüedad máxima debe ser un número entero de meses entre 1 y 60.";
    private const string MsgMaxAgeTimeless = "La autorización de datos y el flujo proyectado no tienen antigüedad máxima.";
    private const string MsgAutorizacion   = "La autorización de datos (AutorizacionDatos) debe ser obligatoria en todos los regímenes.";
    private const string MsgNoRequired     = "El régimen necesita al menos un documento obligatorio además de la autorización de datos.";

    /// <summary>
    /// Every rule a matrix must meet; empty when it is valid. One to three regimes may be sent: a regime left
    /// out keeps its code default.
    /// </summary>
    public static IReadOnlyList<ValidationError> Validate(
        IReadOnlyDictionary<string, List<PersonExpectedDocumentLineDto?>?>? regimes)
    {
        var errors = new List<ValidationError>();

        if (regimes is null || regimes.Count == 0)
        {
            errors.Add(new("regimes", MsgNoRegimes));
            return errors;
        }

        foreach (var (regime, lines) in regimes)
        {
            var path = $"regimes.{regime}";

            if (!ExpectedDocumentRegimes.All.Contains(regime, StringComparer.Ordinal))
            {
                errors.Add(new(path, MsgUnknownRegime));
                continue;
            }

            if (lines is null || lines.Count == 0)
            {
                errors.Add(new(path, MsgEmptyRegime));
                continue;
            }

            ValidateRegime(path, lines, errors);
        }

        return errors;
    }

    /// <summary>The lines of one valid regime, in order.</summary>
    public static IReadOnlyList<LegajoExpectedDocument> ToLines(IReadOnlyList<PersonExpectedDocumentLineDto?> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        return lines
            .OfType<PersonExpectedDocumentLineDto>()
            .Select(line => new LegajoExpectedDocument(
                line.DocumentType!,
                string.Equals(line.Requirement, RequirementRequired, StringComparison.Ordinal),
                (line.Alternatives ?? []).OfType<string>().ToList(),
                line.MaxAgeMonths is { } months ? (int)months : null))
            .ToList();
    }

    /// <summary>
    /// The canonical stored JSON of a VALID matrix: the regimes in <see cref="ExpectedDocumentRegimes.All"/>
    /// order, camelCase members, integral <c>maxAgeMonths</c>.
    /// </summary>
    public static string Serialize(IReadOnlyDictionary<string, List<PersonExpectedDocumentLineDto?>?> regimes)
    {
        ArgumentNullException.ThrowIfNull(regimes);

        var canonical = new Dictionary<string, List<PersonExpectedDocumentLineDto?>?>(StringComparer.Ordinal);
        foreach (var regime in ExpectedDocumentRegimes.All)
        {
            if (!regimes.TryGetValue(regime, out var lines) || lines is null)
                continue;

            canonical[regime] = ToLines(lines)
                .Select(line => (PersonExpectedDocumentLineDto?)new PersonExpectedDocumentLineDto
                {
                    DocumentType = line.DocumentType,
                    Requirement  = line.Required ? RequirementRequired : RequirementOptional,
                    Alternatives = line.Alternatives.Select(a => (string?)a).ToList(),
                    MaxAgeMonths = line.MaxAgeMonths
                })
                .ToList();
        }

        return JsonSerializer.Serialize(new PersonExpectedDocumentsMatrixDto { Regimes = canonical }, JsonOptions);
    }

    /// <summary>
    /// Parses a stored row. False — with <paramref name="errorKind"/> <see cref="ErrorUnparseable"/> or
    /// <see cref="ErrorInvalid"/> — when it cannot be used; then NO regime of it is used. Never throws.
    /// </summary>
    public static bool TryParseStored(
        string? json,
        out IReadOnlyDictionary<string, IReadOnlyList<LegajoExpectedDocument>> regimes,
        out string? errorKind)
    {
        regimes   = new Dictionary<string, IReadOnlyList<LegajoExpectedDocument>>(StringComparer.Ordinal);
        errorKind = null;

        PersonExpectedDocumentsMatrixDto? dto;
        try
        {
            dto = string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<PersonExpectedDocumentsMatrixDto>(json, JsonOptions);
        }
        catch (JsonException)
        {
            dto = null;
        }

        if (dto is null)
        {
            errorKind = ErrorUnparseable;
            return false;
        }

        if (Validate(dto.Regimes).Count > 0)
        {
            errorKind = ErrorInvalid;
            return false;
        }

        regimes = dto.Regimes!.ToDictionary(
            r => r.Key,
            r => ToLines(r.Value!),
            StringComparer.Ordinal);
        return true;
    }

    private static void ValidateRegime(string path, List<PersonExpectedDocumentLineDto?> lines, List<ValidationError> errors)
    {
        // The line types first: an alternative may not name the type of ANY line, earlier or later.
        var lineTypes = new HashSet<string>(
            lines.Select(l => l?.DocumentType).OfType<string>().Where(Configurable.Contains),
            StringComparer.Ordinal);

        var seenTypes        = new HashSet<string>(StringComparer.Ordinal);
        var seenAlternatives = new HashSet<string>(StringComparer.Ordinal);
        int? autorizacionIndex = null;
        var  otherRequired     = 0;

        for (var i = 0; i < lines.Count; i++)
        {
            var linePath = $"{path}[{i}]";
            var line     = lines[i];

            if (line is null)
            {
                errors.Add(new(linePath, MsgEmptyLine));
                continue;
            }

            var type      = line.DocumentType;
            var typeValid = false;

            if (string.IsNullOrWhiteSpace(type))
                errors.Add(new($"{linePath}.documentType", MsgTypeMissing));
            else if (!Configurable.Contains(type))
                errors.Add(new($"{linePath}.documentType", MsgTypeInvalid));
            else if (!seenTypes.Add(type))
                errors.Add(new($"{linePath}.documentType", MsgTypeRepeated));
            else
                typeValid = true;

            var isRequired = string.Equals(line.Requirement, RequirementRequired, StringComparison.Ordinal);
            if (!isRequired && !string.Equals(line.Requirement, RequirementOptional, StringComparison.Ordinal))
                errors.Add(new($"{linePath}.requirement", MsgRequirement));

            if (typeValid && type == DocumentTypes.AutorizacionDatos)
                autorizacionIndex = i;
            else if (typeValid && isRequired)
                otherRequired++;

            var alternatives = line.Alternatives ?? [];
            for (var j = 0; j < alternatives.Count; j++)
            {
                var alternative = alternatives[j];
                var altPath     = $"{linePath}.alternatives[{j}]";

                if (string.IsNullOrWhiteSpace(alternative) || !Configurable.Contains(alternative))
                    errors.Add(new(altPath, MsgAltInvalid));
                else if (string.Equals(alternative, type, StringComparison.Ordinal))
                    errors.Add(new(altPath, MsgAltSelf));
                else if (lineTypes.Contains(alternative))
                    errors.Add(new(altPath, MsgAltIsLine));
                else if (!seenAlternatives.Add(alternative))
                    errors.Add(new(altPath, MsgAltRepeated));
            }

            if (line.MaxAgeMonths is { } months)
            {
                if (typeValid && LegajoDocumentAgeClasses.TimelessTypes.Contains(type!))
                    errors.Add(new($"{linePath}.maxAgeMonths", MsgMaxAgeTimeless));
                else if (months != decimal.Truncate(months) || months < MinMaxAgeMonths || months > MaxMaxAgeMonths)
                    errors.Add(new($"{linePath}.maxAgeMonths", MsgMaxAgeRange));
            }
        }

        // The consent invariant: without an authorization no document of the legajo reaches the model.
        if (autorizacionIndex is not { } index)
            errors.Add(new(path, MsgAutorizacion));
        else if (!string.Equals(lines[index]!.Requirement, RequirementRequired, StringComparison.Ordinal))
            errors.Add(new($"{path}[{index}].requirement", MsgAutorizacion));

        if (otherRequired == 0)
            errors.Add(new(path, MsgNoRequired));
    }
}
