// Project : Api.Web.FinanceIA
// Created : 2026-10-28

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Splits a failed ApiCallLogs row into "the vendor answered, but has no data for this subject" and a
/// real integration failure. ApiCallLogs keeps recording what the vendor answered (IsSuccess = 0 for a
/// LUFE no-legajo); this is where a reader decides it is not an outage. One rule per vendor, each
/// owned by that vendor's outcome class — never string matching on ErrorMessage.
/// </summary>
/// <remarks>
/// LUFE: <see cref="LufeCallOutcome.IsNoLegajoStatus"/> on ResponseStatus (a NULL status is a network
/// error or a timeout, so a failure), or a 2xx row carrying one of the "no data" markers LufeService
/// writes itself — "Empty (HTTP 200)" for a section answered with no usable body, "No legajo (HTTP 200)"
/// for an entidad with no valid entity (<see cref="LufeCallOutcome.EmptyAnswerPrefix"/>,
/// <see cref="LufeCallOutcome.NoLegajoPrefix"/>). A 2xx "Parse error" carries neither and stays a
/// failure. ARCA: <see cref="ArcaCallOutcome.IsNotFound"/>. Every other
/// service has no "no data" failure row: NOSIS logs its 200-with-nothing as a success already, and a
/// failed Claude / ClaudeChat row is always a real failure.
/// </remarks>
public static class ApiCallNoDataClassifier
{
    /// <summary>True when a row is a vendor's "no data" answer rather than a failure. Never for a success.</summary>
    /// <param name="service">ApiCallLogs.Service, ordinal.</param>
    /// <param name="isSuccess">ApiCallLogs.IsSuccess.</param>
    /// <param name="responseStatus">ApiCallLogs.ResponseStatus.</param>
    /// <param name="hasErrorMessage">Whether ApiCallLogs.ErrorMessage is non-NULL — its text is never read.</param>
    /// <param name="hasLufeNoDataMarker">Whether ApiCallLogs.ErrorMessage starts with a LUFE "no data" marker.</param>
    public static bool IsNoData(
        string service, bool isSuccess, int? responseStatus, bool hasErrorMessage, bool hasLufeNoDataMarker)
        => !isSuccess && service switch
        {
            RescoreSource.Lufe => responseStatus is { } status
                && (LufeCallOutcome.IsNoLegajoStatus(status)
                    || (hasLufeNoDataMarker && status is >= 200 and <= 299)),
            RescoreSource.Arca => ArcaCallOutcome.IsNotFound(isSuccess, hasErrorMessage),
            _                  => false
        };
}
