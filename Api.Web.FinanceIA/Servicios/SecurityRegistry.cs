// Project : Api.Web.FinanceIA
// Created : 2026-04-27
// Modified: 2026-04-27

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Security rules per entity. Defines which fields must be
/// filtered by user identity (contactid, accountid, email).
/// </summary>
public static class SecurityRegistry
{
    /// <summary>
    /// Gets the dictionary of security rules keyed by entity logical name.
    /// </summary>
    public static readonly Dictionary<string, EntitySecurityRule> Rules = new()
    {
        ["contact"] = new EntitySecurityRule
        {
            FieldRules = new()
            {
                new FieldSecurityRule
                {
                    Field = "ownerid",
                    Binding = IdentityBinding.Contact,
                    Required = true,
                    OverrideClientValue = true
                }
            }
        },
        ["account"] = new EntitySecurityRule
        {
            FieldRules = new()
            {
                new FieldSecurityRule
                {
                    Field = "accountid",
                    Binding = IdentityBinding.Account,
                    Required = true,
                    OverrideClientValue = true
                }
            }
        }
    };
}
