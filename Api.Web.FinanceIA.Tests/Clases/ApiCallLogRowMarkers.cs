// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-28

using Api.Web.FinanceIA.Clases;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// The C# mirror of the flag <c>DashboardRepository.ApiCallOutcomesQuery</c> computes in SQL, so a test
/// can classify an ApiCallLogs row a writer recorded in memory exactly as the dashboard would read it.
/// </summary>
internal static class ApiCallLogRowMarkers
{
    public static bool HasLufeNoDataMarker(string? errorMessage)
        => errorMessage is not null
           && (errorMessage.StartsWith(LufeCallOutcome.EmptyAnswerPrefix, StringComparison.Ordinal)
               || errorMessage.StartsWith(LufeCallOutcome.NoLegajoPrefix, StringComparison.Ordinal));

    /// <summary><see cref="ApiCallNoDataClassifier.IsNoData"/> over a row as a writer recorded it.</summary>
    public static bool IsNoData(string service, bool isSuccess, int? responseStatus, string? errorMessage)
        => ApiCallNoDataClassifier.IsNoData(
            service, isSuccess, responseStatus, errorMessage is not null, HasLufeNoDataMarker(errorMessage));
}
