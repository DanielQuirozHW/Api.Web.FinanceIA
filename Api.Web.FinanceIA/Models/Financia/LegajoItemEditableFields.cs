// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>The [dbo].[LegajoItems] fields an analyst can set, stored as CSV in EditedFields (DDL 071; no CHECK). A rebuild never overwrites a field listed there.</summary>
public static class LegajoItemEditableFields
{
    /// <summary>The casa-habitación flag.</summary>
    public const string IsHousing = "IsHousing";

    /// <summary>The asset-of-the-activity flag.</summary>
    public const string IsActivityAsset = "IsActivityAsset";

    /// <summary>The ownership percentage.</summary>
    public const string OwnershipPct = "OwnershipPct";

    /// <summary>The class of an investment (DDL 080, engine 0.9).</summary>
    public const string InvestmentClass = "InvestmentClass";

    /// <summary>Every value, in the order the API writes them.</summary>
    public static readonly IReadOnlyList<string> Known = [IsHousing, IsActivityAsset, OwnershipPct, InvestmentClass];
}
