// Project : Api.Web.FinanceIA
// Created : 2026-10-29

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>Whose inventory an item of a fiador's asset breakdown comes from (M10 Phase 5a-2).</summary>
public static class FiadorAssetOrigins
{
    /// <summary>The fiador's own legajo.</summary>
    public const string Propio = "propio";

    /// <summary>
    /// The solicitante's legajo: its housing, executable through the spouse's guarantee when the spouse
    /// consents (<see cref="GuaranteeTypes.Conyuge"/>).
    /// </summary>
    public const string Solicitante = "solicitante";
}
