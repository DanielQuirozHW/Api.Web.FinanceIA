// Project : Api.Web.FinanceIA
// Created : 2026-10-28

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// The one rule that reads an "ARCA" ApiCallLogs row as "ARCA answered, and registers no persona for
/// this CUIT / no CUIT for this DNI".
/// </summary>
/// <remarks>
/// It rests on the writers' convention, not on a status: every ARCA writer
/// (<c>EntitiesController.LogArcaCallAsync</c>, <c>FiadoresController.LogArcaCallAsync</c>) writes
/// ResponseStatus 502 on EVERY failed row, and tells the two apart only by the error text — NULL when
/// ARCA answered with no persona (getPersona_v2 → null, getIdPersonaListByDocumento → no CUIT), the
/// exception message or type when the call itself failed. Tests pin that writer shape; a writer that
/// starts passing a null error on a real failure would hide it here.
/// </remarks>
public static class ArcaCallOutcome
{
    /// <summary>True for a failed row that carries no error text: ARCA's "not found".</summary>
    public static bool IsNotFound(bool isSuccess, bool hasErrorMessage)
        => !isSuccess && !hasErrorMessage;
}
