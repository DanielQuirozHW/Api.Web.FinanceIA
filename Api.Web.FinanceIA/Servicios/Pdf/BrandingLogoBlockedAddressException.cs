// Project : Api.Web.FinanceIA
// Created : 2026-11-08

namespace Api.Web.FinanceIA.Servicios.Pdf;

/// <summary>
/// Thrown by <see cref="BrandingLogoConnectPolicy"/> when the logo host resolves to an address
/// <see cref="BrandingLogoAddressPolicy"/> refuses (or to none). No socket has been opened when it is thrown.
/// The message names the host only, never an address list or the URL.
/// </summary>
public sealed class BrandingLogoBlockedAddressException : IOException
{
    /// <summary>Creates the exception for <paramref name="host"/>.</summary>
    public BrandingLogoBlockedAddressException(string host)
        : base($"The logo host '{host}' does not resolve to a public address.")
    {
    }
}
