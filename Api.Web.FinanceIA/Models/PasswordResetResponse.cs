// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Models;

/// <summary>
/// The one 202 body POST api/bff/auth/password-reset ever returns. It is identical whether or not
/// the address belongs to an account, so the response cannot be used to enumerate users.
/// </summary>
public sealed record PasswordResetResponse
{
    /// <summary>Generic confirmation shown to the user, in Spanish.</summary>
    public required string Message { get; init; }
}
