// Project : Api.Web.FinanceIA
// Created : 2026-10-25

namespace Api.Web.FinanceIA.Models;

/// <summary>
/// Settings for the first-admin bootstrap (POST /api/bff/setup/first-admin), bound from the "Setup"
/// configuration section. In Azure the key is the App Setting <c>Setup__BootstrapKey</c>; it is set
/// only while a new client's first administrator is being invited, then deleted.
/// </summary>
public sealed record SetupOptions
{
    /// <summary>Shortest key that turns the endpoint on. Anything shorter leaves it off (404).</summary>
    public const int MinimumBootstrapKeyLength = 32;

    /// <summary>
    /// Shared secret the caller must send in <c>X-Setup-Key</c>. Unset, empty or shorter than
    /// <see cref="MinimumBootstrapKeyLength"/> means the endpoint does not exist.
    /// </summary>
    public string? BootstrapKey { get; init; }
}
