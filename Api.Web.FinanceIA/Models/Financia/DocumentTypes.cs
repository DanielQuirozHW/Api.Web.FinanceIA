// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The [dbo].[Documents].[DocumentType] values — the vocabulary of CK_Documents_DocumentType.
/// </summary>
/// <remarks>
/// Replaces the two hard-coded lists that used to live in <c>DocumentsController</c> and
/// <c>EntitiesController.MapLufeDocumentType</c>. The four <see cref="LegacyTypes"/> are the ones
/// 001_initial_schema admitted; DDL 070 widened the constraint with the persona-humana legajo types.
/// A test parses 070 and pins <see cref="Known"/> to the constraint, so a value added here without a
/// DDL (or the reverse) fails the build rather than a production INSERT.
///
/// <para>
/// Two upload paths, two lists: a document uploaded WITHOUT a legajo keeps the legacy vocabulary
/// byte-for-byte (<see cref="LegacyTypes"/>); a document uploaded INTO a legajo takes
/// <see cref="LegajoTypes"/>. Balance extraction never sees a legajo type.
/// </para>
/// </remarks>
public static class DocumentTypes
{
    // ── Legacy (company) documents — 001 ─────────────────────────────────────

    /// <summary>A balance sheet (estados contables). The only type balance extraction reads.</summary>
    public const string Balance = "Balance";

    /// <summary>A supplementary document for a balance.</summary>
    public const string Complemento = "Complemento";

    /// <summary>A certificate (e.g. a LUFE certificado).</summary>
    public const string Certificado = "Certificado";

    /// <summary>Anything else. Legal in both vocabularies.</summary>
    public const string Otro = "Otro";

    // ── Persona humana legajo documents — DDL 070 ────────────────────────────

    /// <summary>Manifestación de bienes (sworn statement of assets and liabilities).</summary>
    public const string ManifestacionBienes = "ManifestacionBienes";

    /// <summary>DDJJ Impuesto sobre los Bienes Personales.</summary>
    public const string BienesPersonales = "BienesPersonales";

    /// <summary>REIBP (Régimen Especial de Ingreso del Impuesto sobre los Bienes Personales).</summary>
    public const string Reibp = "Reibp";

    /// <summary>DDJJ Impuesto a las Ganancias (personas humanas).</summary>
    public const string Ganancias = "Ganancias";

    /// <summary>Certificación de ingresos issued by a contador público.</summary>
    public const string CertificacionIngresos = "CertificacionIngresos";

    /// <summary>DDJJ IVA (F.731 / F.2002).</summary>
    public const string DdjjIva = "DdjjIva";

    /// <summary>DDJJ Ingresos Brutos.</summary>
    public const string DdjjIibb = "DdjjIibb";

    /// <summary>Monotributo: constancia, credencial or recategorización.</summary>
    public const string Monotributo = "Monotributo";

    /// <summary>Projected cash flow. Optional in every regime.</summary>
    public const string FlujoProyectado = "FlujoProyectado";

    /// <summary>
    /// The person's signed data-processing authorization. Never sent to the model: it goes straight
    /// to <see cref="LegajoStatuses.Ready"/> on upload and is what consent is recorded against.
    /// </summary>
    public const string AutorizacionDatos = "AutorizacionDatos";

    /// <summary>The four types 001 admitted, in the order the old validation message listed them.</summary>
    public static readonly IReadOnlyList<string> LegacyTypes = [Balance, Complemento, Certificado, Otro];

    /// <summary>
    /// The types a legajo document may be declared as: the ten DDL 070 types plus <see cref="Otro"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> LegajoTypes =
    [
        ManifestacionBienes, BienesPersonales, Reibp, Ganancias, CertificacionIngresos,
        DdjjIva, DdjjIibb, Monotributo, FlujoProyectado, AutorizacionDatos, Otro
    ];

    /// <summary>Every value CK_Documents_DocumentType admits after DDL 070.</summary>
    public static readonly IReadOnlySet<string> Known =
        new HashSet<string>(LegacyTypes.Concat(LegajoTypes), StringComparer.Ordinal);

    /// <summary>
    /// The canonical spelling of <paramref name="value"/> in <paramref name="list"/>, matched
    /// case-insensitively (the legacy upload always accepted any casing), or null when absent.
    /// </summary>
    public static string? Canonical(string? value, IReadOnlyList<string> list)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        foreach (var candidate in list)
        {
            if (string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        return null;
    }
}
