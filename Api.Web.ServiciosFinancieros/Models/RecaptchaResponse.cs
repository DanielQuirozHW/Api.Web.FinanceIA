// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-21
// Modified: 2026-04-21

using System.Text.Json.Serialization;

namespace Api.Web.ServiciosFinancieros.Models;

/// <summary>
/// Response from Google reCAPTCHA V3 siteverify API.
/// Success indicates token validation result. Score ranges from 0.0
/// (very likely bot) to 1.0 (very likely human). Threshold of 0.5
/// is recommended.
/// </summary>
public sealed record RecaptchaResponse
{
    /// <summary>Whether the token was successfully validated by Google.</summary>
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    /// <summary>
    /// Score from 0.0 (very likely bot) to 1.0 (very likely human).
    /// Only present when <see cref="Success"/> is <c>true</c>.
    /// </summary>
    [JsonPropertyName("score")]
    public double Score { get; init; }

    /// <summary>The action name associated with the token, as specified in the frontend call.</summary>
    [JsonPropertyName("action")]
    public string Action { get; init; } = string.Empty;

    /// <summary>Timestamp of the challenge in UTC.</summary>
    [JsonPropertyName("challenge_ts")]
    public DateTime ChallengeTs { get; init; }

    /// <summary>Hostname of the site where the reCAPTCHA was solved.</summary>
    [JsonPropertyName("hostname")]
    public string Hostname { get; init; } = string.Empty;

    /// <summary>
    /// Error codes returned by Google when <see cref="Success"/> is <c>false</c>.
    /// Common values: missing-input-secret, invalid-input-secret,
    /// missing-input-response, invalid-input-response, bad-request, timeout-or-duplicate.
    /// </summary>
    [JsonPropertyName("error-codes")]
    public string[] ErrorCodes { get; init; } = Array.Empty<string>();
}
