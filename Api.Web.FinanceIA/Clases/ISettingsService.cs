// Project : Api.Web.FinanceIA
// Created : 2026-05-11

using Api.Web.FinanceIA.Models.Settings;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// Settings operations for user profile view/edit, tenant user listing and tenant-level
/// configuration (scoring parameters and the tenant profile).
/// </summary>
public interface ISettingsService
{
    /// <summary>
    /// Returns the tenant profile in force — entity type plus the optional integrations it
    /// entitles — together with how it was resolved. Never throws.
    /// </summary>
    Task<TenantProfileDto> GetTenantProfileAsync(string tenantId, CancellationToken ct);

    /// <summary>
    /// Sets the tenant's entity type and returns the resulting profile.
    ///
    /// <para>
    /// THROWS <see cref="ArgumentException"/> when <paramref name="request"/> carries a value
    /// outside the known entity types, and propagates a failed write — the caller must not report
    /// a change that did not happen. Contrast <see cref="GetTenantProfileAsync"/>.
    /// </para>
    /// </summary>
    Task<TenantProfileDto> UpdateTenantProfileAsync(
        Guid userId,
        string tenantId,
        UpdateTenantProfileRequest request,
        string? ipAddress,
        string? requestId,
        CancellationToken ct);

    /// <summary>
    /// Returns the profile of the user identified by UserId within a tenant,
    /// or null if not found.
    /// </summary>
    Task<ProfileDto?> GetProfileAsync(Guid userId, string tenantId, CancellationToken ct);

    /// <summary>
    /// Updates the display name of the user identified by UserId.
    /// Returns the updated profile, or null if the user is not found.
    /// </summary>
    Task<ProfileDto?> UpdateProfileAsync(
        Guid userId,
        string tenantId,
        UpdateProfileRequest request,
        string? ipAddress,
        string? requestId,
        CancellationToken ct);

    /// <summary>
    /// Returns all users belonging to the specified tenant, ordered by creation date descending.
    /// </summary>
    Task<List<UserDto>> GetUsersAsync(string tenantId, CancellationToken ct);

    /// <summary>
    /// Returns the full scoring configuration for a tenant, merging DB overrides with defaults. The persona
    /// humana pf_ keys come in their own PersonaHumana array, with default, override flag, bounds and group.
    /// </summary>
    Task<ScoringConfigDto> GetScoringConfigAsync(string tenantId, CancellationToken ct);

    /// <summary>
    /// Updates one or more scoring configuration parameters for a tenant. A pf_ item with Reset = true
    /// deletes the tenant's row so the key goes back to its code default. A request made only of pf_ items
    /// validates only the persona humana config; any other key brings the full validation.
    /// Returns null if any submitted key is not recognized.
    /// Throws <see cref="InvalidOperationException"/> if validation fails (e.g. weights don't sum to 1.0).
    /// </summary>
    Task<ScoringConfigDto?> UpdateScoringConfigAsync(
        Guid userId,
        string tenantId,
        UpdateScoringConfigRequest request,
        string? ipAddress,
        string? requestId,
        CancellationToken ct);

    /// <summary>
    /// Returns one item per known sector indicating whether the tenant has threshold overrides.
    /// </summary>
    Task<IReadOnlyList<SectorListItemDto>> GetSectorsAsync(string tenantId, CancellationToken ct);

    /// <summary>
    /// Returns the effective threshold profile for a sector, merging base + sector overrides + defaults.
    /// </summary>
    Task<SectorProfileDto> GetSectorProfileAsync(string tenantId, string sector, CancellationToken ct);

    /// <summary>
    /// Creates or updates threshold overrides for a non-General sector.
    /// Returns null if sector is "General" or any key is unrecognized.
    /// Throws <see cref="InvalidOperationException"/> if threshold ordering is incoherent.
    /// </summary>
    Task<SectorProfileDto?> UpdateSectorProfileAsync(
        Guid userId,
        string tenantId,
        string sector,
        UpdateSectorProfileRequest request,
        string? ipAddress,
        string? requestId,
        CancellationToken ct);
}
