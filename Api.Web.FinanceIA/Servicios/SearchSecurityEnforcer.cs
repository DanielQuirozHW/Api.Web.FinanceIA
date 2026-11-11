// Project : Api.Web.FinanceIA
// Created : 2026-04-27
// Modified: 2026-04-27

using Api.Web.FinanceIA.Models;
using Microsoft.AspNetCore.Mvc;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Enforces row-level security on search requests by injecting or validating
/// user-identity-bound filters. Client-provided values are overwritten or
/// validated depending on the <see cref="FieldSecurityRule"/> configuration.
/// </summary>
public static class SearchSecurityEnforcer
{
    /// <summary>
    /// Applies the security rules in <paramref name="rule"/> to the incoming request,
    /// producing a sanitized request with all identity filters guaranteed to be present.
    /// </summary>
    /// <param name="req">The original request from the client.</param>
    /// <param name="accountId">The caller's resolved Dynamics accountid.</param>
    /// <param name="contactId">The caller's resolved Dynamics contactid.</param>
    /// <param name="email">The caller's email address.</param>
    /// <param name="rule">
    /// The entity security rule to enforce, or <c>null</c> when the entity has no security rules.
    /// </param>
    /// <param name="sanitized">
    /// Output: the request with all required security filters injected or overridden.
    /// Equal to <paramref name="req"/> when <paramref name="rule"/> is <c>null</c>.
    /// </param>
    /// <returns>
    /// <c>null</c> when enforcement succeeds; a <see cref="ForbidResult"/> when a required
    /// identity value is missing or a client value violates a non-override rule.
    /// </returns>
    public static IActionResult? Enforce(
        SearchRequest req,
        string? accountId,
        string? contactId,
        string? employeeId,
        string? email,
        EntitySecurityRule? rule,
        out SearchRequest sanitized)
    {
        sanitized = req;

        if (rule is null)
        {
            return null;
        }

        var filters = req.Filters is not null
            ? new List<SearchFilter>(req.Filters)
            : new List<SearchFilter>();

        foreach (var fieldRule in rule.FieldRules)
        {
            var expectedValue = fieldRule.Binding switch
            {
                IdentityBinding.Account  => accountId,
                IdentityBinding.Contact  => contactId,
                IdentityBinding.Employee => employeeId,
                IdentityBinding.Email    => email,
                _                        => null
            };

            if (fieldRule.Required && string.IsNullOrWhiteSpace(expectedValue))
            {
                return new ForbidResult();
            }

            var existingIndex = filters.FindIndex(
                f => string.Equals(f.Field, fieldRule.Field, StringComparison.OrdinalIgnoreCase));

            if (existingIndex < 0)
            {
                filters.Add(new SearchFilter
                {
                    Field = fieldRule.Field,
                    Op = FilterOp.Eq,
                    Value = expectedValue
                });
            }
            else if (fieldRule.OverrideClientValue)
            {
                filters[existingIndex] = filters[existingIndex] with { Value = expectedValue };
            }
            else
            {
                var clientValue = filters[existingIndex].Value?.ToString();
                if (!string.Equals(clientValue, expectedValue, StringComparison.OrdinalIgnoreCase))
                {
                    return new ForbidResult();
                }
            }
        }

        sanitized = req with { Filters = filters };
        return null;
    }
}
