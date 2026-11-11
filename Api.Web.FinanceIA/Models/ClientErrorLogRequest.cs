// Project : Api.Web.FinanceIA
// Created : 2026-09-02

namespace Api.Web.FinanceIA.Models;

/// <summary>
/// Body of POST /api/errorlog/client — a browser-side error reported by the Portal.
/// Every field is free-form text produced by the client, so nothing here is trusted:
/// <see cref="Controllers.ErrorLogController"/> trims and clamps each value before it
/// reaches <see cref="ErrorLogEntry"/>.
/// </summary>
/// <remarks>
/// <para>
/// Only <paramref name="Message"/> is required. The remaining fields are best-effort
/// context: a browser that crashed hard may have none of them.
/// </para>
/// <para>
/// No <c>required</c> modifier is used deliberately. With <c>[ApiController]</c> the
/// automatic model-state 400 would fire before the controller runs, producing a
/// different error shape than the rest of the API; validation is done explicitly instead.
/// </para>
/// </remarks>
/// <param name="Message">Short description of the failure. Required.</param>
/// <param name="ExceptionDetails">Client-side exception type and/or message.</param>
/// <param name="Url">Browser URL where the crash happened — NOT the API request path.</param>
/// <param name="StackTrace">Client-side stack trace.</param>
/// <param name="Digest">
/// Next.js error digest. The ErrorLogs table has no column for it, so the controller folds
/// it into ExceptionDetails rather than dropping it.
/// </param>
public sealed record ClientErrorLogRequest(
    string  Message,
    string? ExceptionDetails = null,
    string? Url              = null,
    string? StackTrace       = null,
    string? Digest           = null);
