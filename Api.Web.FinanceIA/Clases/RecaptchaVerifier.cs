// Project : Api.Web.FinanceIA
// Created : 2026-04-21
// Modified: 2026-04-21

using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Models;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Verifies reCAPTCHA V3 tokens against Google's siteverify API using the
/// tenant-specific secret key resolved from <see cref="TenantContext"/>.
/// Uses a named <see cref="HttpClient"/> from <see cref="IHttpClientFactory"/>
/// — never instantiates <see cref="HttpClient"/> directly.
/// </summary>
public sealed class RecaptchaVerifier : IRecaptchaVerifier
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RecaptchaVerifier> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="RecaptchaVerifier"/>.
    /// </summary>
    /// <param name="httpClientFactory">Factory used to create the named <c>RecaptchaClient</c>.</param>
    /// <param name="logger">Logger for structured diagnostics.</param>
    public RecaptchaVerifier(IHttpClientFactory httpClientFactory, ILogger<RecaptchaVerifier> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RecaptchaResponse> VerifyAsync(
        string token,
        TenantContext tenantContext,
        string? remoteIp,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(tenantContext);

        var requestData = new Dictionary<string, string>
        {
            { "secret", tenantContext.RecaptchaSecretKey },
            { "response", token }
        };

        if (!string.IsNullOrWhiteSpace(remoteIp))
        {
            requestData["remoteip"] = remoteIp;
        }

        try
        {
            var httpClient = _httpClientFactory.CreateClient("RecaptchaClient");
            var content = new FormUrlEncodedContent(requestData);

            var response = await httpClient
                .PostAsync("https://www.google.com/recaptcha/api/siteverify", content, cancellationToken)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var jsonResponse = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            var recaptchaResponse = JsonSerializer.Deserialize<RecaptchaResponse>(
                jsonResponse,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

            if (recaptchaResponse is null)
            {
                _logger.LogError(
                    "Failed to deserialize reCAPTCHA response for tenant {TenantId}",
                    tenantContext.TenantId);

                throw new RecaptchaException("Invalid reCAPTCHA response format.");
            }

            _logger.LogInformation(
                "reCAPTCHA verified for tenant {TenantId}. Success: {Success}, Score: {Score}",
                tenantContext.TenantId,
                recaptchaResponse.Success,
                recaptchaResponse.Score);

            return recaptchaResponse;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex,
                "reCAPTCHA API request failed for tenant {TenantId}",
                tenantContext.TenantId);

            throw new RecaptchaException("Failed to verify reCAPTCHA token.", ex);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex,
                "Failed to parse reCAPTCHA response for tenant {TenantId}",
                tenantContext.TenantId);

            throw new RecaptchaException("Invalid reCAPTCHA response format.", ex);
        }
    }
}
