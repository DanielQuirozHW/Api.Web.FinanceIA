// Project : Api.Web.FinanceIA
// Created : 2026-09-27

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Person-type rules derived from an Argentine CUIT/CUIL.
/// </summary>
/// <remarks>
/// The two-digit prefix of a CUIT says what kind of taxpayer it identifies: 20, 23, 24 and 27 are
/// natural persons (the CUIL of a persona humana), 30, 33 and 34 are legal persons. Anything else
/// (e.g. the 50/51/55 series for foreign subjects) is not decided by the prefix.
///
/// <para>
/// ONE resolver — <see cref="ResolvePersonType"/> — serves both POST /api/entities/lookup-arca and
/// POST /api/entities, so the type a user saw at lookup is the type the entity gets on create.
/// The existing private <c>FormatCuit</c> copies in the controllers are deliberately left alone.
/// </para>
/// </remarks>
public static class CuitHelper
{
    private static readonly IReadOnlySet<string> HumanaPrefixes =
        new HashSet<string>(StringComparer.Ordinal) { "20", "23", "24", "27" };

    private static readonly IReadOnlySet<string> JuridicaPrefixes =
        new HashSet<string>(StringComparer.Ordinal) { "30", "33", "34" };

    /// <summary>
    /// The person type the CUIT prefix determines: <see cref="PersonTypes.Humana"/> for 20/23/24/27,
    /// <see cref="PersonTypes.Juridica"/> for 30/33/34, and null for any other prefix or for input
    /// that is not a CUIT. Accepts the CUIT with or without hyphens.
    /// </summary>
    public static string? PersonTypeFromPrefix(string? cuit)
    {
        if (string.IsNullOrWhiteSpace(cuit)) return null;

        var digits = cuit.Replace("-", string.Empty).Trim();
        if (digits.Length != 11 || !digits.All(char.IsAsciiDigit)) return null;

        var prefix = digits[..2];

        if (HumanaPrefixes.Contains(prefix)) return PersonTypes.Humana;
        if (JuridicaPrefixes.Contains(prefix)) return PersonTypes.Juridica;

        return null;
    }

    /// <summary>
    /// Maps ARCA's raw <c>datosGenerales/tipoPersona</c> onto a person type, ignoring case:
    /// FISICA → <see cref="PersonTypes.Humana"/>, JURIDICA → <see cref="PersonTypes.Juridica"/>,
    /// anything else → null.
    /// </summary>
    public static string? PersonTypeFromArca(string? arcaTipoPersona)
    {
        if (string.IsNullOrWhiteSpace(arcaTipoPersona)) return null;

        var value = arcaTipoPersona.Trim();

        if (string.Equals(value, "FISICA", StringComparison.OrdinalIgnoreCase)) return PersonTypes.Humana;
        if (string.Equals(value, "JURIDICA", StringComparison.OrdinalIgnoreCase)) return PersonTypes.Juridica;

        return null;
    }

    /// <summary>
    /// Resolves the person type of <paramref name="cuit"/>.
    /// <list type="number">
    /// <item>An unambiguous prefix (<see cref="PersonTypeFromPrefix"/>) is authoritative.</item>
    /// <item>Otherwise ARCA's <paramref name="arcaTipoPersona"/> (FISICA / JURIDICA, any case).</item>
    /// <item>Otherwise <paramref name="requested"/>, when it is a known person type (any case).</item>
    /// <item>Otherwise <see cref="PersonTypes.Juridica"/>, the column default.</item>
    /// </list>
    /// When ARCA or the request disagrees with an unambiguous prefix, a Warning is logged and the
    /// prefix is kept. The Warning names only the source and the two type values — never the CUIT,
    /// which for a persona humana is personal data.
    /// </summary>
    public static string ResolvePersonType(
        string cuit,
        string? arcaTipoPersona,
        string? requested,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var fromPrefix    = PersonTypeFromPrefix(cuit);
        var fromArca      = PersonTypeFromArca(arcaTipoPersona);
        var fromRequested = PersonTypes.Canonicalize(requested);

        if (fromPrefix is not null)
        {
            WarnIfDisagrees(logger, "ARCA", fromArca, fromPrefix);
            WarnIfDisagrees(logger, "request", fromRequested, fromPrefix);
            return fromPrefix;
        }

        return fromArca ?? fromRequested ?? PersonTypes.Juridica;
    }

    private static void WarnIfDisagrees(ILogger logger, string source, string? other, string fromPrefix)
    {
        if (other is null || string.Equals(other, fromPrefix, StringComparison.Ordinal)) return;

        logger.LogWarning(
            "Person type from {PersonTypeSource} ({OtherPersonType}) disagrees with the CUIT prefix ({PrefixPersonType}); keeping the prefix.",
            source, other, fromPrefix);
    }
}
