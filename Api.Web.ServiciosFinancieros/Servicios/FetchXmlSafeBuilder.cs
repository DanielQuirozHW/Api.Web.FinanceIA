// Project : Api.Web.ServiciosFinancieros
// Created : 2026-04-27
// Modified: 2026-04-27

using System.Collections;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using Api.Web.ServiciosFinancieros.Models;

namespace Api.Web.ServiciosFinancieros.Servicios;

/// <summary>
/// Builds validated, server-side FetchXML from a <see cref="SearchRequest"/>.
/// All field names, operators and entity names are validated against the
/// <see cref="FetchMeta"/> whitelist before being written to XML.
/// Returns only the <c>&lt;entity&gt;...&lt;/entity&gt;</c> block;
/// the caller is responsible for wrapping it with a <c>&lt;fetch&gt;</c> element.
/// </summary>
public static partial class FetchXmlSafeBuilder
{
    private static readonly Regex SafeNameRegex = SafeName();

    private static readonly HashSet<FilterOp> AllowedOps = new()
    {
        FilterOp.Eq, FilterOp.Neq, FilterOp.Lt, FilterOp.Lte,
        FilterOp.Gt, FilterOp.Gte, FilterOp.StartsWith, FilterOp.Contains,
        FilterOp.In, FilterOp.Between, FilterOp.IsNull, FilterOp.IsNotNull
    };

    [GeneratedRegex("^[a-zA-Z0-9_]+$", RegexOptions.Compiled)]
    private static partial Regex SafeName();

    /// <summary>
    /// Builds a validated FetchXML <c>&lt;entity&gt;</c> block from the request.
    /// </summary>
    /// <param name="req">The sanitized search request. Must not be null.</param>
    /// <param name="meta">The metadata whitelist for the target entity. Must not be null.</param>
    /// <returns>
    /// A string containing the FetchXML <c>&lt;entity&gt;</c> block, ready to be
    /// wrapped with <c>&lt;fetch count='...' page='...'&gt;</c>.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when any field name, entity name or operator fails whitelist validation.
    /// </exception>
    public static string Build(SearchRequest req, FetchMeta meta)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(meta);

        if (!SafeNameRegex.IsMatch(req.Entity))
        {
            throw new ArgumentException($"Invalid entity name: '{req.Entity}'");
        }

        var pageSize = Math.Clamp(req.PageSize, 1, meta.MaxPageSize);

        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = false
        };

        using var sw = new System.IO.StringWriter();
        using var writer = XmlWriter.Create(sw, settings);

        writer.WriteStartElement("entity");
        writer.WriteAttributeString("name", req.Entity);

        WriteAttributes(writer, req.Select, meta);
        WriteOrderElements(writer, req.Sort, meta);
        WriteFilterElement(writer, req.Filters, meta.Filterable, req.Entity);
        WriteLinkEntities(writer, req.LinkEntities);

        writer.WriteEndElement(); // </entity>
        writer.Flush();

        return sw.ToString();
    }

    private static void WriteAttributes(
        XmlWriter writer,
        string[]? select,
        FetchMeta meta)
    {
        var fields = select is { Length: > 0 } ? select : meta.Selectable.ToArray();

        foreach (var field in fields)
        {
            if (!meta.Selectable.Contains(field))
            {
                throw new ArgumentException(
                    $"Field '{field}' is not selectable for entity '{meta.EntityLogicalName}'");
            }

            writer.WriteStartElement("attribute");
            writer.WriteAttributeString("name", field);
            writer.WriteEndElement();
        }
    }

    private static void WriteOrderElements(
        XmlWriter writer,
        List<SearchSort>? sort,
        FetchMeta meta)
    {
        var sortDirectives = sort is { Count: > 0 }
            ? sort
            : [new SearchSort { Field = meta.DefaultSort, Dir = "asc" }];

        foreach (var s in sortDirectives)
        {
            if (!meta.Sortable.Contains(s.Field))
            {
                throw new ArgumentException(
                    $"Field '{s.Field}' is not sortable for entity '{meta.EntityLogicalName}'");
            }

            writer.WriteStartElement("order");
            writer.WriteAttributeString("attribute", s.Field);
            writer.WriteAttributeString("descending", s.Dir == "desc" ? "true" : "false");
            writer.WriteEndElement();
        }
    }

    private static void WriteFilterElement(
        XmlWriter writer,
        List<SearchFilter>? filters,
        HashSet<string> filterableFields,
        string entityName)
    {
        if (filters is not { Count: > 0 })
        {
            return;
        }

        writer.WriteStartElement("filter");
        writer.WriteAttributeString("type", "and");

        foreach (var filter in filters)
        {
            WriteCondition(writer, filter, filterableFields, entityName);
        }

        writer.WriteEndElement(); // </filter>
    }

    private static void WriteCondition(
        XmlWriter writer,
        SearchFilter filter,
        HashSet<string> filterableFields,
        string entityName)
    {
        if (!SafeNameRegex.IsMatch(filter.Field))
        {
            throw new ArgumentException($"Invalid field name: '{filter.Field}'");
        }

        if (!filterableFields.Contains(filter.Field))
        {
            throw new ArgumentException(
                $"Field '{filter.Field}' is not filterable for entity '{entityName}'");
        }

        if (!AllowedOps.Contains(filter.Op))
        {
            throw new ArgumentException($"Operator '{filter.Op}' is not allowed");
        }

        var opName = MapOperator(filter.Op);

        writer.WriteStartElement("condition");
        writer.WriteAttributeString("attribute", filter.Field);
        writer.WriteAttributeString("operator", opName);

        switch (filter.Op)
        {
            case FilterOp.IsNull:
            case FilterOp.IsNotNull:
                writer.WriteEndElement();
                break;

            case FilterOp.In:
                WriteInValues(writer, filter.Value);
                writer.WriteEndElement();
                break;

            case FilterOp.Between:
                writer.WriteStartElement("value");
                writer.WriteString(filter.Value?.ToString() ?? string.Empty);
                writer.WriteEndElement();
                writer.WriteStartElement("value");
                writer.WriteString(filter.Value2?.ToString() ?? string.Empty);
                writer.WriteEndElement();
                writer.WriteEndElement();
                break;

            default:
                if (filter.Value is not null)
                {
                    writer.WriteAttributeString("value", filter.Value.ToString() ?? string.Empty);
                }
                writer.WriteEndElement();
                break;
        }
    }

    private static void WriteInValues(XmlWriter writer, object? value)
    {
        if (value is JsonElement jsonElement &&
            jsonElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in jsonElement.EnumerateArray())
            {
                writer.WriteStartElement("value");
                writer.WriteString(item.ToString());
                writer.WriteEndElement();
            }
        }
        else if (value is IEnumerable enumerable and not string)
        {
            foreach (var item in enumerable)
            {
                writer.WriteStartElement("value");
                writer.WriteString(item?.ToString() ?? string.Empty);
                writer.WriteEndElement();
            }
        }
    }

    private static void WriteLinkEntities(
        XmlWriter writer,
        List<LinkEntity>? linkEntities)
    {
        if (linkEntities is null)
        {
            return;
        }

        foreach (var link in linkEntities)
        {
            WriteLinkEntity(writer, link);
        }
    }

    private static void WriteLinkEntity(XmlWriter writer, LinkEntity link)
    {
        if (!MetaRegistry.Entities.TryGetValue(link.EntityName, out var linkMeta))
        {
            throw new ArgumentException(
                $"Entity '{link.EntityName}' is not registered in MetaRegistry");
        }

        if (!SafeNameRegex.IsMatch(link.FromAttribute))
        {
            throw new ArgumentException($"Invalid from-attribute: '{link.FromAttribute}'");
        }

        if (!SafeNameRegex.IsMatch(link.ToAttribute))
        {
            throw new ArgumentException($"Invalid to-attribute: '{link.ToAttribute}'");
        }

        if (!SafeNameRegex.IsMatch(link.Alias))
        {
            throw new ArgumentException($"Invalid alias: '{link.Alias}'");
        }

        var linkType = link.Type == LinkType.Outer ? "outer" : "inner";

        writer.WriteStartElement("link-entity");
        writer.WriteAttributeString("name", link.EntityName);
        writer.WriteAttributeString("from", link.FromAttribute);
        writer.WriteAttributeString("to", link.ToAttribute);
        writer.WriteAttributeString("alias", link.Alias);
        writer.WriteAttributeString("link-type", linkType);

        if (link.Select is { Length: > 0 })
        {
            foreach (var field in link.Select)
            {
                if (!linkMeta.Selectable.Contains(field))
                {
                    throw new ArgumentException(
                        $"Field '{field}' is not selectable for entity '{link.EntityName}'");
                }

                writer.WriteStartElement("attribute");
                writer.WriteAttributeString("name", field);
                writer.WriteEndElement();
            }
        }

        WriteFilterElement(writer, link.Filters, linkMeta.Filterable, link.EntityName);

        writer.WriteEndElement(); // </link-entity>
    }

    private static string MapOperator(FilterOp op) => op switch
    {
        FilterOp.Eq => "eq",
        FilterOp.Neq => "ne",
        FilterOp.Lt => "lt",
        FilterOp.Lte => "le",
        FilterOp.Gt => "gt",
        FilterOp.Gte => "ge",
        FilterOp.StartsWith => "begins-with",
        FilterOp.Contains => "contains",
        FilterOp.In => "in",
        FilterOp.Between => "between",
        FilterOp.IsNull => "null",
        FilterOp.IsNotNull => "not-null",
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Unknown operator")
    };
}
