// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-27
// Modified: 2026-04-27

namespace Api.Web.ServiciosFinancieros.Servicios;

/// <summary>
/// Defines how a field should be secured.
/// If <see cref="Required"/> is <c>true</c> and the user doesn't have the identity, the request is forbidden.
/// If <see cref="OverrideClientValue"/> is <c>true</c>, the server overwrites any client-provided value.
/// </summary>
public sealed record FieldSecurityRule
{
    /// <summary>Gets the field name this rule applies to.</summary>
    public string Field { get; init; } = string.Empty;

    /// <summary>Gets the identity value this field is bound to.</summary>
    public IdentityBinding Binding { get; init; }

    /// <summary>
    /// Gets a value indicating whether the user must have a non-null identity value
    /// for this binding. If <c>true</c> and the identity is missing, the request is forbidden.
    /// </summary>
    public bool Required { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether the server should overwrite any client-provided
    /// filter value for this field with the user's resolved identity value.
    /// </summary>
    public bool OverrideClientValue { get; init; } = true;
}
