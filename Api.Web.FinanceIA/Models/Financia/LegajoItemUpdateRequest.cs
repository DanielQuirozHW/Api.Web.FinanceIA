// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Body of PATCH /api/person-legajos/{id}/items/{itemId}: the analyst's edits to one inventory item. Every
/// field is optional, at least one must be sent, and the reason is mandatory. Edited fields survive a
/// rebuild; a confirmation survives while the same conflict stands.
/// </summary>
public sealed record LegajoItemUpdateRequest
{
    /// <summary>Sets the casa-habitación flag.</summary>
    public bool? IsHousing { get; init; }

    /// <summary>Sets the asset-of-the-activity flag.</summary>
    public bool? IsActivityAsset { get; init; }

    /// <summary>Sets the ownership percentage, 0..100.</summary>
    public decimal? OwnershipPct { get; init; }

    /// <summary>
    /// Sets the class of an Inversion item: one of LegajoInvestmentClasses (TituloPublico, AccionCotizante,
    /// ObligacionNegociable, CuotaparteFci, Otra). An empty string clears it — the item stays unclassified and a rebuild
    /// does not classify it again. 400 validation_error on another value or on an item that is not an Inversion.
    /// </summary>
    public string? InvestmentClass { get; init; }

    /// <summary>Sets the note (at most 1000 characters); an empty string clears it.</summary>
    public string? AnalystNote { get; init; }

    /// <summary>True confirms a Conflict as it stands (409 item_not_in_conflict otherwise).</summary>
    public bool? ConfirmConflict { get; init; }

    /// <summary>Why — at least five non-blank characters, at most 1000. Audited; never the values.</summary>
    public string? Reason { get; init; }
}
