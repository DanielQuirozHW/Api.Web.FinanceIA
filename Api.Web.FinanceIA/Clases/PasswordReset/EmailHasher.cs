// Project : Api.Web.FinanceIA
// Created : 2026-10-19

using System.Security.Cryptography;
using System.Text;

namespace Api.Web.FinanceIA.Clases.PasswordReset;

/// <summary>
/// Turns an email address into a short, stable, non-reversible tag for logs and throttle keys,
/// so the password-reset flow can correlate requests for the same address without ever writing
/// the address itself.
/// </summary>
/// <remarks>
/// Unsalted on purpose: the same address must hash to the same tag across requests and instances
/// so an operator can follow one user's attempts in Application Insights. That makes the tag
/// confirmable by someone who already holds a candidate address and can read our logs — it hides
/// the address from log readers, it is not a secret.
/// </remarks>
public static class EmailHasher
{
    private const string Purpose = "pwreset:";

    /// <summary>
    /// Returns the first 16 hex characters of SHA-256 over the trimmed, lower-cased address.
    /// Case and surrounding whitespace do not change the result.
    /// </summary>
    public static string Hash(string email)
    {
        ArgumentNullException.ThrowIfNull(email);

        var normalised = email.Trim().ToLowerInvariant();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(Purpose + normalised));

        return Convert.ToHexString(digest, 0, 8).ToLowerInvariant();
    }
}
