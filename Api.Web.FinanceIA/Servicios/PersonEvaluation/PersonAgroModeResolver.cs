// Project : Api.Web.FinanceIA
// Created : 2026-10-30

using Api.Web.FinanceIA.Models.Arca;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.PersonEvaluation;

/// <summary>
/// Resolves the solicitante's <see cref="PersonAgroMode"/> (engine 0.7) from the main ARCA activity, the
/// operation's terms and the tenant's pf_agro_ parameters. Pure.
/// </summary>
public static class PersonAgroModeResolver
{
    /// <summary>
    /// The CLAE sections that make an activity agro: 01 agriculture and livestock, 02 forestry, 03 fishing.
    /// A classification fact, not a tenant choice — the tenant switches the mode on or off with
    /// pf_agro_modo_anual, never redefines what agro is.
    /// </summary>
    public static readonly IReadOnlyList<string> AgroClaePrefixes = ["01", "02", "03"];

    /// <summary>The cuotas a year of a monthly cuota: the 0.6 behaviour.</summary>
    public const int CuotasMensuales = 12;

    /// <summary>The length of a CLAE code (F.883 nomenclator).</summary>
    private const int ClaeLength = 6;

    /// <summary>
    /// Resolves the mode. The analyst's override wins in both directions. Otherwise the mode is on when the main
    /// activity is agro and pf_agro_modo_anual is on; no ARCA activity is never agro. The cuotas a year are the
    /// stated ones, else the tenant's pf_agro_cuotas_por_anio in agro mode, else 12.
    /// </summary>
    public static PersonAgroMode Resolve(ArcaFiscalData? arca, PersonOperationTerms terms, PersonEvaluationParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(parameters);

        var clae     = MainClae(arca);
        var isAgro   = IsAgroClae(clae);
        var @override = terms.AgroModeOverride;

        var (active, origin, evidence) = @override switch
        {
            true  => (true, PersonAgroModeOrigins.Analista, "Modo anual activado por el analista"),
            false => (false, PersonAgroModeOrigins.Analista, "Modo anual desactivado por el analista"),
            null when isAgro && parameters.ResolvedAgroModoAnual
                  => (true, PersonAgroModeOrigins.Actividad, $"Actividad agropecuaria (CLAE {clae}): evaluación anual"),
            null when isAgro
                  => (false, PersonAgroModeOrigins.Actividad,
                      $"Actividad agropecuaria (CLAE {clae}): el modo anual está desactivado para el tenant (pf_agro_modo_anual)"),
            null  => (false, PersonAgroModeOrigins.Actividad, (string?)null)
        };

        var cuotasPorAnio = terms.CuotasPorAnio
                         ?? (active ? parameters.ResolvedAgroCuotasPorAnio : CuotasMensuales);

        return new PersonAgroMode
        {
            Active            = active,
            Origin            = origin,
            Override          = @override,
            Clae              = clae,
            CuotasPorAnio     = cuotasPorAnio,
            MesInicioCampania = parameters.ResolvedAgroMesInicioCampania,
            Evidence          = evidence
        };
    }

    /// <summary>
    /// The main activity's code: the one ARCA orders first (orden 1), else the first listed — the same rule
    /// <see cref="ArcaConstanciaService"/> resolves the principal activity with. Normalised to six digits,
    /// since ARCA may send the code as a number and drop its leading zero ("11111" is 011111). Null when the
    /// snapshot lists no activity or the code has no digits.
    /// </summary>
    public static string? MainClae(ArcaFiscalData? arca)
    {
        if (arca?.Actividades is not { Count: > 0 } actividades)
            return null;

        var principal = actividades.FirstOrDefault(a => a.Orden == 1) ?? actividades[0];
        var digits = new string((principal.IdActividad ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 0)
            return null;

        return digits.Length < ClaeLength ? digits.PadLeft(ClaeLength, '0') : digits;
    }

    /// <summary>True when <paramref name="clae"/> belongs to an agro section (<see cref="AgroClaePrefixes"/>).</summary>
    public static bool IsAgroClae(string? clae)
        => clae is not null && AgroClaePrefixes.Any(prefix => clae.StartsWith(prefix, StringComparison.Ordinal));
}
