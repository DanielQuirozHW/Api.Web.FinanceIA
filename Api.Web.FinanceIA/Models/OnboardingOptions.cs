// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Models;

/// <summary>
/// Settings for POST api/bff/auth/onboarding-token, bound from the "Onboarding" configuration
/// section. Every value has a working default, so a deployment without the section behaves exactly
/// as the endpoint did when the threshold was a literal.
/// </summary>
public sealed record OnboardingOptions
{
    /// <summary>Minimum reCAPTCHA V3 score accepted. Default 0.5, the value every other anonymous endpoint uses.</summary>
    public double RecaptchaMinScore { get; init; } = 0.5;
}
