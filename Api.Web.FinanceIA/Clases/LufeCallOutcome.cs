// Project : Api.Web.FinanceIA
// Created : 2026-10-28

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// The one rule that reads a Lufe HTTP status as "the vendor answered, and holds no legajo for this
/// CUIT". <see cref="Servicios.LufeService"/> decides NoLegajoReported with it, and the operations
/// dashboard (<see cref="ApiCallNoDataClassifier"/>) reads the ApiCallLogs.ResponseStatus that service
/// wrote with it, so the two can never disagree about what a LUFE "no data" is.
/// </summary>
/// <remarks>
/// It also owns the two ErrorMessage texts <see cref="Servicios.LufeService"/> writes when Lufe answered
/// 2xx with nothing usable — a sub-section with an empty body (<see cref="DescribeEmptyAnswer"/>) and an
/// entidad with no valid entity (<see cref="DescribeNoLegajo"/>). The dashboard query reads those rows
/// back by these prefixes: they are OUR markers, never vendor text, and "Parse error" (a malformed 2xx
/// body, a real failure) shares neither.
/// </remarks>
public static class LufeCallOutcome
{
    /// <summary>Prefix of the ErrorMessage of a section Lufe answered 2xx with no usable body.</summary>
    public const string EmptyAnswerPrefix = "Empty (HTTP ";

    /// <summary>Prefix of the ErrorMessage of an entidad call read as NoLegajoReported.</summary>
    public const string NoLegajoPrefix = "No legajo (HTTP ";

    /// <summary>ApiCallLogs.ErrorMessage for a section Lufe answered with no usable body: "Empty (HTTP 200)".</summary>
    public static string DescribeEmptyAnswer(int? statusCode) => $"{EmptyAnswerPrefix}{statusCode})";

    /// <summary>ApiCallLogs.ErrorMessage for an entidad call read as no legajo: "No legajo (HTTP 404)".</summary>
    public static string DescribeNoLegajo(int? statusCode) => $"{NoLegajoPrefix}{statusCode})";

    /// <summary>
    /// True for the statuses read as "no legajo": every 4xx except 408 and 429, which are transient.
    /// </summary>
    public static bool IsNoLegajoStatus(int statusCode)
        => statusCode is >= 400 and <= 499
           && statusCode != (int)System.Net.HttpStatusCode.RequestTimeout
           && statusCode != (int)System.Net.HttpStatusCode.TooManyRequests;
}
