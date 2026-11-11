// Project : Api.Web.FinanceIA
// Created : 2026-10-28

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// ApiCallLogs rows of one Service over a window that share an outcome, aggregated in SQL — what
/// <c>ApiCallNoDataClassifier</c> needs to tell a vendor's "no data" answer from a failure. Never
/// ErrorMessage itself, never a hash.
/// </summary>
/// <param name="Service">ApiCallLogs.Service.</param>
/// <param name="IsSuccess">ApiCallLogs.IsSuccess.</param>
/// <param name="ResponseStatus">ApiCallLogs.ResponseStatus.</param>
/// <param name="HasErrorMessage">Whether ApiCallLogs.ErrorMessage is non-NULL.</param>
/// <param name="HasLufeNoDataMarker">
/// Whether ApiCallLogs.ErrorMessage starts with one of the "no data" markers LufeService writes
/// (<c>LufeCallOutcome.EmptyAnswerPrefix</c>, <c>LufeCallOutcome.NoLegajoPrefix</c>) — decided in SQL.
/// </param>
/// <param name="Calls">Rows.</param>
/// <param name="LastCalledAt">MAX(CalledAt) over the rows.</param>
public sealed record DashboardApiCallOutcomeRow(
    string Service, bool IsSuccess, int? ResponseStatus, bool HasErrorMessage, bool HasLufeNoDataMarker,
    int Calls, DateTime LastCalledAt);
