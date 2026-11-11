// Project : Api.Web.FinanceIA
// Created : 2026-10-19

using Microsoft.AspNetCore.Http;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Reads the RFC 7240 <c>Prefer</c> request header.
/// </summary>
/// <remarks>
/// The header may repeat and each value may list several preferences, each with an optional
/// <c>=value</c> and <c>;parameters</c> (<c>Prefer: respond-async, wait=10</c>). Preference tokens are
/// case-insensitive. A preference the server does not understand is ignored, never an error.
/// </remarks>
internal static class PreferHeader
{
    /// <summary>The request header name.</summary>
    public const string HeaderName = "Prefer";

    /// <summary>The response header that tells the client which preference was honoured.</summary>
    public const string AppliedHeaderName = "Preference-Applied";

    /// <summary>The preference asking the server to answer 202 and finish the work asynchronously.</summary>
    public const string RespondAsync = "respond-async";

    /// <summary>True when any <c>Prefer</c> value carries the <c>respond-async</c> preference.</summary>
    public static bool RequestsRespondAsync(IHeaderDictionary headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        foreach (var value in headers[HeaderName])
        {
            if (string.IsNullOrWhiteSpace(value)) continue;

            foreach (var preference in value.Split(','))
            {
                var token = preference.Split(';')[0];
                var equals = token.IndexOf('=');
                if (equals >= 0) token = token[..equals];

                if (string.Equals(token.Trim(), RespondAsync, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }
}
