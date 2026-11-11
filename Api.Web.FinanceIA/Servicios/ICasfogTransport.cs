// Project : Api.Web.FinanceIA
// Created : 2026-07-29
// Modified: 2026-07-29

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Raw HTTP transport for the CASFOG API. Exists as a seam so <see cref="CasfogService"/>
/// keeps its orchestration/caching/mapping logic while the wire mechanism (an
/// <c>HttpWebRequest</c> round-trip, mirroring the proven Api.Web.Dynamics365 client) lives
/// in one place and can be substituted in tests.
/// </summary>
/// <remarks>
/// Contract: on a completed HTTP round-trip the numeric status is returned (never null) —
/// a 2xx yields <c>(status, body)</c>, any non-2xx (including 401) yields <c>(status, null)</c>
/// so the caller can special-case the status. A genuine transport failure (DNS/connect/timeout)
/// throws <see cref="System.Net.WebException"/>; caller cancellation throws
/// <see cref="System.OperationCanceledException"/>. All of that is absorbed by the service's
/// fault-isolation catch blocks.
/// </remarks>
public interface ICasfogTransport
{
    /// <summary>Executes a single CASFOG HTTP request and returns the numeric status + body.</summary>
    Task<(int? StatusCode, string? Body)> SendAsync(CasfogHttpRequest request, CancellationToken ct);
}

/// <summary>
/// An immutable description of one CASFOG request. <paramref name="Url"/> is absolute (the
/// service composes it from <c>Casfog:BaseUrl</c>); the transport stays configuration-free.
/// </summary>
/// <param name="Method">HTTP method — <c>"GET"</c> or <c>"POST"</c>.</param>
/// <param name="Url">Absolute request URL.</param>
/// <param name="BearerToken">Bearer token for authenticated calls; <c>null</c> for the login POST.</param>
/// <param name="JsonBody">JSON request body for POST; <c>null</c> for GET.</param>
public sealed record CasfogHttpRequest(string Method, string Url, string? BearerToken, string? JsonBody);
