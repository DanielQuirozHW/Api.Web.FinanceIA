// Project : Api.Web.FinanceIA
// Created : 2026-04-27
// Modified: 2026-04-27

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Holds the Dynamics 365 identifiers resolved for the authenticated user.
/// </summary>
public sealed record UserContext
{
    /// <summary>Gets the Dynamics 365 contactid GUID for the user.</summary>
    public string ContactId { get; init; } = string.Empty;

    /// <summary>Gets the Dynamics 365 accountid GUID for the user.</summary>
    public string AccountId { get; init; } = string.Empty;

    /// <summary>Gets the Dynamics 365 new_empleadoid for the user.</summary>
    public string EmployeeId { get; init; } = string.Empty;

    /// <summary>Gets the user's email address, or <c>null</c> if not available.</summary>
    public string? Email { get; init; }
}
