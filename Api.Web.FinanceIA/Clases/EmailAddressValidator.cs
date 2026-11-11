// Project : Api.Web.FinanceIA
// Created : 2026-10-25

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// The email format check of the invite endpoints. One rule for the Settings invite and the
/// first-admin bootstrap, so the two cannot drift.
/// </summary>
public static class EmailAddressValidator
{
    /// <summary>
    /// True when <paramref name="email"/> parses as a single address and is exactly that address
    /// (no display name, no surrounding text).
    /// </summary>
    public static bool IsValid(string email)
    {
        try
        {
            var addr = new System.Net.Mail.MailAddress(email);
            return addr.Address == email;
        }
        catch
        {
            return false;
        }
    }
}
