// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// Resolves a JSON Pointer against a schema tree (not against data): which node a path names, and
/// whether it passes through an array item. Pure.
/// </summary>
public static class LegajoSchemaResolver
{
    /// <summary>
    /// The schema node <paramref name="pointer"/> names, or null when the schema has no such path.
    /// Array tokens must be indices; the pointer to an array item resolves to the item's object.
    /// </summary>
    /// <param name="root">The schema root.</param>
    /// <param name="pointer">A JSON Pointer.</param>
    /// <param name="insideItem">True when the path passes through an array item.</param>
    public static LegajoSchemaNode? Resolve(LegajoSchemaObject root, string pointer, out bool insideItem)
    {
        ArgumentNullException.ThrowIfNull(root);

        insideItem = false;
        var tokens = LegajoJsonPointer.Parse(pointer);
        if (tokens is null || tokens.Count == 0)
            return null;

        LegajoSchemaNode current = root;
        foreach (var token in tokens)
        {
            switch (current)
            {
                case LegajoSchemaObject obj when obj.Find(token) is { } child:
                    current = child;
                    break;
                case LegajoSchemaArray array when LegajoJsonPointer.TryIndex(token, out _):
                    current = array.Items;
                    insideItem = true;
                    break;
                default:
                    return null;
            }
        }

        return current;
    }
}
