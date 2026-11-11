// Project : Api.Web.FinanceIA
// Created : 2026-10-22

using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.AnalysisRuns;

/// <summary>
/// How a re-score ended. <see cref="Summary"/> is set only on success (200); every other outcome
/// changed nothing and carries the status, errorCode and, where it applies, Retry-After.
/// </summary>
public sealed record AnalysisRescoreOutcome
{
    public required int                     StatusCode        { get; init; }
    public string?                          ErrorCode         { get; init; }
    public int?                             RetryAfterSeconds { get; init; }
    public AnalysisRescoreSummary?          Summary           { get; init; }

    /// <summary>
    /// The es-AR detail, when the refusal brings its own — the persona humana evaluation refusals, worded
    /// as POST /api/person-analyses words them. Null: the endpoint answers RescoreEligibility.Detail.
    /// </summary>
    public string?                          Detail            { get; init; }

    /// <summary>ProblemDetails extensions besides errorCode (a fiador refusal's fiadorId and reason).</summary>
    public IReadOnlyDictionary<string, object?>? Extensions   { get; init; }

    /// <summary>The field-level errors of a refused balance correction (400 validation_error).</summary>
    public IReadOnlyList<ValidationError>?   ValidationErrors  { get; init; }

    public bool IsSuccess => Summary is not null;

    public static AnalysisRescoreOutcome Rescored(AnalysisRescoreSummary summary)
        => new() { StatusCode = 200, Summary = summary };

    public static AnalysisRescoreOutcome NotFound()
        => new() { StatusCode = 404 };

    /// <summary>A balance correction refused with field-level errors (400 validation_error). Nothing written.</summary>
    public static AnalysisRescoreOutcome Invalid(IReadOnlyList<ValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        return new() { StatusCode = 400, ErrorCode = RescoreErrorCodes.ValidationError, ValidationErrors = errors };
    }

    public static AnalysisRescoreOutcome Refused(int statusCode, string errorCode, int? retryAfterSeconds = null)
        => new() { StatusCode = statusCode, ErrorCode = errorCode, RetryAfterSeconds = retryAfterSeconds };

    /// <summary>A refusal of the persona humana evaluation, with its own status, detail and extensions.</summary>
    public static AnalysisRescoreOutcome Refused(PersonEvaluationRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);

        return new()
        {
            StatusCode = refusal.StatusCode,
            ErrorCode  = refusal.ErrorCode,
            Detail     = refusal.Detail,
            Extensions = refusal.Extensions()
        };
    }
}
