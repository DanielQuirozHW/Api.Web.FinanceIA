// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>Why a matched [dbo].[LegajoItems] row is a Conflict, stored as CSV in ConflictReasons (DDL 071; no CHECK).</summary>
public static class LegajoItemConflictReasons
{
    /// <summary>The two documents state a different ownership percentage.</summary>
    public const string OwnershipDiffers = "OwnershipDiffers";

    /// <summary>The market value (manifestación) is below the fiscal value (Bienes Personales), which is its floor.</summary>
    public const string BelowFiscalFloor = "BelowFiscalFloor";

    /// <summary>The two documents state a different currency.</summary>
    public const string CurrencyDiffers = "CurrencyDiffers";

    /// <summary>Every value, in the order the API writes them.</summary>
    public static readonly IReadOnlyList<string> Known = [OwnershipDiffers, BelowFiscalFloor, CurrencyDiffers];
}
