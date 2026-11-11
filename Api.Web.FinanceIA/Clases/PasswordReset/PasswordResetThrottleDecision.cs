// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Clases.PasswordReset;

/// <summary>Result of one per-email throttle check.</summary>
/// <param name="IsAllowed">False once the address has used up its window.</param>
/// <param name="RetryAfterSeconds">Seconds until the window resets; 0 when allowed.</param>
public readonly record struct PasswordResetThrottleDecision(bool IsAllowed, int RetryAfterSeconds);
