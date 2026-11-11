// Project : Api.Web.FinanceIA
// Created : 2026-10-30

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Why a persona humana evaluation cannot run, as the endpoint answers it: the status, the ProblemDetails
/// title, the stable errorCode and the es-AR detail, plus the <c>fiadorId</c> / <c>reason</c> extensions of
/// the fiador refusals. Shared by POST /api/person-analyses and the re-evaluation, so both refuse with the
/// same words.
/// </summary>
public sealed record PersonEvaluationRefusal(
    int     StatusCode,
    string  Title,
    string  ErrorCode,
    string  Detail,
    Guid?   FiadorId = null,
    string? Reason   = null)
{
    /// <summary>The ProblemDetails extensions besides errorCode: fiadorId and reason, when set.</summary>
    public IReadOnlyDictionary<string, object?> Extensions()
    {
        var extensions = new Dictionary<string, object?>();
        if (FiadorId is { } fiadorId)
            extensions["fiadorId"] = fiadorId;
        if (Reason is not null)
            extensions["reason"] = Reason;
        return extensions;
    }
}
