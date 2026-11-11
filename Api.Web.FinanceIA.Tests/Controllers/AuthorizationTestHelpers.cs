// Project : Api.Web.FinanceIA.Tests
// Created : 2026-07-29

using System.Reflection;
using Microsoft.AspNetCore.Authorization;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Reflection helpers that read the declared <c>[Authorize(Roles = ...)]</c> metadata on
/// controller actions, so authorization tests can lock the role matrix without an HTTP
/// integration harness. Role gates in this BFF are method-level; the class-level
/// <c>[Authorize(AuthenticationSchemes = "Internal")]</c> carries no roles and is ignored.
/// </summary>
internal static class AuthorizationTestHelpers
{
    /// <summary>
    /// Returns the set of roles allowed to call <paramref name="method"/> on
    /// <paramref name="controller"/>, parsed from the effective <c>[Authorize(Roles=...)]</c>
    /// attribute (method-level, falling back to controller-level). An empty set means no
    /// role restriction is declared (authenticated-only). Comparison is ordinal/case-sensitive
    /// to mirror how <c>[Authorize(Roles=...)]</c> matches the role claim.
    /// </summary>
    public static ISet<string> AuthorizeRolesOf(Type controller, string method)
    {
        var mi = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                           .Single(m => m.Name == method);

        var roles =
            RolesFrom(mi.GetCustomAttributes<AuthorizeAttribute>(inherit: true))
            ?? RolesFrom(controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true))
            ?? string.Empty;

        return roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>True if <paramref name="role"/> appears in the action's declared role set.</summary>
    public static bool RoleAllowed(Type controller, string method, string role) =>
        AuthorizeRolesOf(controller, method).Contains(role);

    /// <summary>
    /// The raw <c>Roles</c> string declared by the METHOD-level <c>[Authorize]</c> on
    /// <paramref name="method"/>, exactly as written (after const folding), or <c>null</c> when the
    /// action declares none. Unlike <see cref="AuthorizeRolesOf"/> it does not fall back to the
    /// controller, so it pins which constant an action names, not merely the set it resolves to.
    /// </summary>
    public static string? DeclaredRolesOf(Type controller, string method)
    {
        var mi = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                           .Single(m => m.Name == method);

        return RolesFrom(mi.GetCustomAttributes<AuthorizeAttribute>(inherit: true));
    }

    /// <summary>True if <paramref name="controller"/> declares a public instance method named <paramref name="method"/>.</summary>
    public static bool HasAction(Type controller, string method) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance).Any(m => m.Name == method);

    private static string? RolesFrom(IEnumerable<AuthorizeAttribute> attrs) =>
        attrs.Select(a => a.Roles).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));
}
