// Project : Api.Web.FinanceIA
// Created : 2026-06-08

namespace Api.Web.FinanceIA.Models.Settings;

/// <summary>
/// Response containing the user's onboarding flags.
/// </summary>
public sealed record OnboardingFlagsResponse
{
    public Dictionary<string, bool> Flags { get; init; } = new();
}

/// <summary>
/// Request to merge onboarding flags into the user's stored flags.
/// Only the keys present in this request are upserted; existing keys not in the request are preserved.
/// </summary>
public sealed record UpdateOnboardingFlagsRequest
{
    public Dictionary<string, bool> Flags { get; init; } = new();
}
