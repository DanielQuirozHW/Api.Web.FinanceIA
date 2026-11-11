// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-21
// Modified: 2026-04-21

using System.Net.Http;
using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Api.Web.ServiciosFinancieros.Clases.Exceptions;
using Api.Web.ServiciosFinancieros.Models;

namespace Api.Web.ServiciosFinancieros.Servicios;

/// <summary>
/// HTTP client for Dynamics 365 OData v9.2 API.
/// Acquires access tokens per-request via <see cref="IDynamicsTokenService"/>,
/// maps HTTP error codes to domain exceptions, and handles OData pagination transparently.
/// </summary>
public sealed class DynamicsClient : IDynamicsClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDynamicsTokenService _dynamicsTokenService;
    private readonly ILogger<DynamicsClient> _logger;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Initializes a new instance of <see cref="DynamicsClient"/>.
    /// </summary>
    /// <param name="httpClientFactory">Factory for creating the named <c>DynamicsClient</c> HTTP client.</param>
    /// <param name="dynamicsTokenService">Service that acquires Dynamics 365 access tokens.</param>
    /// <param name="logger">Logger for structured diagnostics.</param>
    public DynamicsClient(
        IHttpClientFactory httpClientFactory,
        IDynamicsTokenService dynamicsTokenService,
        ILogger<DynamicsClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _dynamicsTokenService = dynamicsTokenService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(
        string entitySetName,
        string? oDataQuery,
        TenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entitySetName);
        ArgumentNullException.ThrowIfNull(tenantContext);

        var url = string.IsNullOrWhiteSpace(oDataQuery)
            ? entitySetName
            : $"{entitySetName}?{oDataQuery}";

        try
        {
            var client = _httpClientFactory.CreateClient("DynamicsClient");
            var request = await BuildRequestAsync(
                HttpMethod.Get, url, tenantContext, cancellationToken)
                .ConfigureAwait(false);

            var response = await client
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                await HandleErrorResponseAsync(response, tenantContext, cancellationToken)
                    .ConfigureAwait(false);
            }

            var json = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(json))
            {
                return default;
            }

            return JsonSerializer.Deserialize<T>(json, _jsonOptions);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex,
                "HTTP error calling Dynamics for tenant {TenantId}",
                tenantContext.TenantId);

            throw new DynamicsUnavailableException("Failed to connect to Dynamics 365.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex,
                "Dynamics request timeout for tenant {TenantId}",
                tenantContext.TenantId);

            throw new DynamicsTimeoutException("Dynamics 365 request timed out.", ex);
        }
    }

    /// <inheritdoc />
    public async Task<TResponse?> PostAsync<TRequest, TResponse>(
        string entitySetName,
        TRequest body,
        TenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entitySetName);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(tenantContext);

        try
        {
            var client = _httpClientFactory.CreateClient("DynamicsClient");
            var request = await BuildRequestAsync(
                HttpMethod.Post, entitySetName, tenantContext, cancellationToken, body)
                .ConfigureAwait(false);

            var response = await client
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                await HandleErrorResponseAsync(response, tenantContext, cancellationToken)
                    .ConfigureAwait(false);
            }

            var json = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(json))
            {
                return default;
            }

            return JsonSerializer.Deserialize<TResponse>(json, _jsonOptions);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex,
                "HTTP error calling Dynamics for tenant {TenantId}",
                tenantContext.TenantId);

            throw new DynamicsUnavailableException("Failed to connect to Dynamics 365.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex,
                "Dynamics request timeout for tenant {TenantId}",
                tenantContext.TenantId);

            throw new DynamicsTimeoutException("Dynamics 365 request timed out.", ex);
        }
    }

    /// <inheritdoc />
    public async Task PatchAsync<T>(
        string entitySetName,
        Guid recordId,
        T body,
        TenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entitySetName);
        if (recordId == Guid.Empty)
        {
            throw new ArgumentException("Record ID cannot be empty.", nameof(recordId));
        }

        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(tenantContext);

        var url = $"{entitySetName}({recordId})";

        try
        {
            var client = _httpClientFactory.CreateClient("DynamicsClient");
            var request = await BuildRequestAsync(
                HttpMethod.Patch, url, tenantContext, cancellationToken, body)
                .ConfigureAwait(false);

            var response = await client
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                await HandleErrorResponseAsync(response, tenantContext, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex,
                "HTTP error calling Dynamics for tenant {TenantId}",
                tenantContext.TenantId);

            throw new DynamicsUnavailableException("Failed to connect to Dynamics 365.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex,
                "Dynamics request timeout for tenant {TenantId}",
                tenantContext.TenantId);

            throw new DynamicsTimeoutException("Dynamics 365 request timed out.", ex);
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(
        string entitySetName,
        Guid recordId,
        TenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entitySetName);
        if (recordId == Guid.Empty)
        {
            throw new ArgumentException("Record ID cannot be empty.", nameof(recordId));
        }

        ArgumentNullException.ThrowIfNull(tenantContext);

        var url = $"{entitySetName}({recordId})";

        try
        {
            var client = _httpClientFactory.CreateClient("DynamicsClient");
            var request = await BuildRequestAsync(
                HttpMethod.Delete, url, tenantContext, cancellationToken)
                .ConfigureAwait(false);

            var response = await client
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                await HandleErrorResponseAsync(response, tenantContext, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex,
                "HTTP error calling Dynamics for tenant {TenantId}",
                tenantContext.TenantId);

            throw new DynamicsUnavailableException("Failed to connect to Dynamics 365.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex,
                "Dynamics request timeout for tenant {TenantId}",
                tenantContext.TenantId);

            throw new DynamicsTimeoutException("Dynamics 365 request timed out.", ex);
        }
    }

    /// <inheritdoc />
    public async Task<List<T>> GetMultipleAsync<T>(
        string entitySetName,
        string? oDataQuery,
        TenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entitySetName);
        ArgumentNullException.ThrowIfNull(tenantContext);

        var results = new List<T>();
        string? url = string.IsNullOrWhiteSpace(oDataQuery)
            ? entitySetName
            : $"{entitySetName}?{oDataQuery}";

        try
        {
            while (url is not null)
            {
                var client = _httpClientFactory.CreateClient("DynamicsClient");
                var request = await BuildRequestAsync(
                    HttpMethod.Get, url, tenantContext, cancellationToken)
                    .ConfigureAwait(false);

                var response = await client
                    .SendAsync(request, cancellationToken)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    await HandleErrorResponseAsync(response, tenantContext, cancellationToken)
                        .ConfigureAwait(false);
                }

                var json = await response.Content
                    .ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(json))
                {
                    break;
                }

                var collection = JsonSerializer.Deserialize<ODataCollection<T>>(json, _jsonOptions);

                if (collection is not null)
                {
                    results.AddRange(collection.Value);
                    url = collection.NextLink;

                    _logger.LogDebug(
                        "Fetched {Count} records from Dynamics, NextLink: {HasMore}",
                        collection.Value.Count,
                        collection.NextLink is not null);
                }
                else
                {
                    break;
                }
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex,
                "HTTP error calling Dynamics for tenant {TenantId}",
                tenantContext.TenantId);

            throw new DynamicsUnavailableException("Failed to connect to Dynamics 365.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex,
                "Dynamics request timeout for tenant {TenantId}",
                tenantContext.TenantId);

            throw new DynamicsTimeoutException("Dynamics 365 request timed out.", ex);
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<List<T>> ExecuteFetchXmlAsync<T>(
        string entitySetName,
        string fetchXml,
        TenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entitySetName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fetchXml);
        ArgumentNullException.ThrowIfNull(tenantContext);

        var results = new List<T>();
        var pageNumber = 1;
        var moreRecords = true;
        var pagingCookie = string.Empty;

        while (moreRecords)
        {
            var currentFetchXml = pageNumber > 1
                ? fetchXml.Replace("<fetch",
                    $"<fetch page=\"{pageNumber}\" paging-cookie=\"{SecurityElement.Escape(pagingCookie)}\"")
                : fetchXml;

            var encodedFetchXml = Uri.EscapeDataString(currentFetchXml);
            var url = $"{entitySetName}?fetchXml={encodedFetchXml}";

            try
            {
                var client = _httpClientFactory.CreateClient("DynamicsClient");
                var request = await BuildRequestAsync(
                    HttpMethod.Get, url, tenantContext, cancellationToken)
                    .ConfigureAwait(false);

                var response = await client
                    .SendAsync(request, cancellationToken)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    await HandleErrorResponseAsync(response, tenantContext, cancellationToken)
                        .ConfigureAwait(false);
                }

                var json = await response.Content
                    .ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);

                var wrapper = JsonSerializer.Deserialize<ODataCollectionWrapper<T>>(
                    json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (wrapper?.Value is not null)
                {
                    results.AddRange(wrapper.Value);

                    _logger.LogDebug(
                        "FetchXML page {PageNumber} returned {Count} records for tenant {TenantId}",
                        pageNumber,
                        wrapper.Value.Count,
                        tenantContext.TenantId);
                }

                if (wrapper?.MoreRecords == true && !string.IsNullOrWhiteSpace(wrapper.PagingCookie))
                {
                    pagingCookie = wrapper.PagingCookie;
                    pageNumber++;
                }
                else
                {
                    moreRecords = false;
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex,
                    "HTTP error executing FetchXML for tenant {TenantId}",
                    tenantContext.TenantId);

                throw new DynamicsUnavailableException("Failed to execute FetchXML query.", ex);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex,
                    "FetchXML timeout for tenant {TenantId}",
                    tenantContext.TenantId);

                throw new DynamicsTimeoutException("FetchXML query timed out.", ex);
            }
        }

        _logger.LogInformation(
            "FetchXML query completed. Total records: {TotalCount}, Pages: {PageCount} for tenant {TenantId}",
            results.Count,
            pageNumber,
            tenantContext.TenantId);

        return results;
    }

    /// <inheritdoc />
    public async Task<DynamicsFetchXmlPagedResult<T>> ExecuteFetchXmlPageAsync<T>(
        string entitySetName,
        string fetchXml,
        int pageNumber,
        string? pagingCookie,
        TenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entitySetName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fetchXml);
        ArgumentNullException.ThrowIfNull(tenantContext);

        if (pageNumber < 1)
        {
            throw new ArgumentException("Page number must be >= 1", nameof(pageNumber));
        }

        if (pageNumber > 1 || pagingCookie is not null)
        {
            var xdoc = XDocument.Parse(fetchXml);
            var fetchElement = xdoc.Root!;

            fetchElement.SetAttributeValue("page", pageNumber.ToString());

            if (!string.IsNullOrEmpty(pagingCookie))
            {
                fetchElement.SetAttributeValue(
                    "paging-cookie",
                    SecurityElement.Escape(pagingCookie));
            }

            fetchXml = xdoc.ToString();
        }

        var encodedFetchXml = Uri.EscapeDataString(fetchXml);
        var url = $"{entitySetName}?fetchXml={encodedFetchXml}";

        try
        {
            var client = _httpClientFactory.CreateClient("DynamicsClient");
            var request = await BuildRequestAsync(
                HttpMethod.Get, url, tenantContext, cancellationToken)
                .ConfigureAwait(false);

            var response = await client
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                await HandleErrorResponseAsync(response, tenantContext, cancellationToken)
                    .ConfigureAwait(false);
            }

            var json = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var dataArray = root.GetProperty("value");
            var data = JsonSerializer.Deserialize<List<T>>(dataArray.GetRawText(), _jsonOptions);

            var moreRecords = false;
            string? nextCookie = null;

            if (root.TryGetProperty("@Microsoft.Dynamics.CRM.morerecords", out var moreRecordsElement))
            {
                moreRecords = moreRecordsElement.GetBoolean();
            }

            if (root.TryGetProperty("@Microsoft.Dynamics.CRM.pagingcookie", out var cookieElement))
            {
                nextCookie = cookieElement.GetString();
            }

            _logger.LogDebug(
                "FetchXML page {PageNumber} executed for tenant {TenantId}. Records in page: {Count}, More records: {MoreRecords}",
                pageNumber,
                tenantContext.TenantId,
                data?.Count ?? 0,
                moreRecords);

            return new DynamicsFetchXmlPagedResult<T>
            {
                Data = data ?? new List<T>(),
                MoreRecords = moreRecords,
                PagingCookie = nextCookie,
                CurrentPage = pageNumber
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex,
                "HTTP error executing FetchXML page for tenant {TenantId}",
                tenantContext.TenantId);

            throw new DynamicsUnavailableException("Failed to execute FetchXML query.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex,
                "FetchXML page request timeout for tenant {TenantId}",
                tenantContext.TenantId);

            throw new DynamicsTimeoutException("FetchXML query timed out.", ex);
        }
    }

    private async Task<HttpRequestMessage> BuildRequestAsync(
        HttpMethod method,
        string relativeUrl,
        TenantContext tenantContext,
        CancellationToken cancellationToken,
        object? body = null)
    {
        var token = await _dynamicsTokenService
            .GetAccessTokenAsync(tenantContext, cancellationToken)
            .ConfigureAwait(false);

        var baseUrl = $"{tenantContext.DynamicsUrl}/api/data/v9.2/";
        var requestUri = new Uri(new Uri(baseUrl), relativeUrl);

        var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("OData-MaxVersion", "4.0");
        request.Headers.Add("OData-Version", "4.0");
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("Prefer", "odata.include-annotations=\"*\"");

        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return request;
    }

    private async Task HandleErrorResponseAsync(
        HttpResponseMessage response,
        TenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        var statusCode = (int)response.StatusCode;
        var content = await response.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        _logger.LogError(
            "Dynamics API error for tenant {TenantId}. Status: {StatusCode}, Response: {Response}",
            tenantContext.TenantId,
            statusCode,
            content);

        throw statusCode switch
        {
            401 or 403 => new DynamicsAuthException("Dynamics 365 authentication failed."),
            404 => new DynamicsNotFoundException("Dynamics 365 record not found."),
            422 => new DynamicsBusinessException("Dynamics 365 business rule violation.", content),
            503 => new DynamicsUnavailableException("Dynamics 365 service unavailable."),
            _ => new DynamicsBusinessException(
                $"Dynamics 365 request failed with status {statusCode}.", content)
        };
    }

    private sealed record ODataCollection<T>
    {
        [JsonPropertyName("value")]
        public List<T> Value { get; init; } = [];

        [JsonPropertyName("@odata.nextLink")]
        public string? NextLink { get; init; }
    }

    private sealed record ODataCollectionWrapper<T>
    {
        [JsonPropertyName("value")]
        public List<T> Value { get; init; } = [];

        [JsonPropertyName("@Microsoft.Dynamics.CRM.morerecords")]
        public bool MoreRecords { get; init; }

        [JsonPropertyName("@Microsoft.Dynamics.CRM.pagingcookie")]
        public string? PagingCookie { get; init; }
    }
}
