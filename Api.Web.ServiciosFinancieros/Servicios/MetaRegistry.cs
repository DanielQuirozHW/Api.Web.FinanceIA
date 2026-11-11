// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-27
// Modified: 2026-04-27

namespace Api.Web.ServiciosFinancieros.Servicios;

/// <summary>
/// Registry of queryable entities. Only entities registered here
/// can be queried via the search endpoint. Acts as a whitelist.
/// </summary>
public static class MetaRegistry
{
    /// <summary>
    /// Gets the dictionary of registered entities keyed by logical name.
    /// </summary>
    public static readonly Dictionary<string, FetchMeta> Entities = new()
    {
        ["contact"] = new FetchMeta
        {
            EntityLogicalName = "contact",
            EntitySetName = "contacts",
            Selectable = new() { "contactid", "fullname", "emailaddress1",
                                 "telephone1", "address1_city", "statuscode" },
            Filterable = new() { "ownerid", "fullname", "emailaddress1",
                                 "address1_city", "statuscode" },
            Sortable = new() { "fullname", "createdon" },
            DefaultSort = "fullname",
            MaxPageSize = 500
        },
        ["account"] = new FetchMeta
        {
            EntityLogicalName = "account",
            EntitySetName = "accounts",
            Selectable = new() { "accountid", "name", "emailaddress1",
                                 "telephone1", "address1_city" },
            Filterable = new() { "ownerid", "name", "address1_city", "accountid" },
            Sortable = new() { "name", "createdon" },
            DefaultSort = "name",
            MaxPageSize = 500
        }
        // Add more entities as needed
    };
}
