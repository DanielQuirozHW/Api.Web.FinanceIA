// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>[dbo].[LegajoItems].[Side] — CK_LegajoItems_Side (DDL 071).</summary>
public static class LegajoItemSides
{
    /// <summary>An asset.</summary>
    public const string Activo = "Activo";

    /// <summary>A debt.</summary>
    public const string Pasivo = "Pasivo";

    /// <summary>Every value the CHECK admits, in DDL order.</summary>
    public static readonly IReadOnlyList<string> Known = [Activo, Pasivo];
}
