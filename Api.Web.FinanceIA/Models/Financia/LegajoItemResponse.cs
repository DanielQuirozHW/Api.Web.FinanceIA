// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// One item of GET /api/person-legajos/{id}/inventory (M10 Phase 3b): an asset or a debt with the market
/// value (manifestación de bienes) and the fiscal value (Bienes Personales) side by side, each with the
/// document it was read from. Amounts in pesos. PERSONAL DATA: returned to the tenant's users only.
/// </summary>
public sealed record LegajoItemResponse
{
    /// <summary>The item; stable across rebuilds while its key is.</summary>
    public required Guid ItemId { get; init; }

    /// <summary>Activo or Pasivo.</summary>
    public required string Side { get; init; }

    /// <summary>One of LegajoItemKinds.</summary>
    public required string Kind { get; init; }

    /// <summary>
    /// For an Inversion item: TituloPublico, AccionCotizante, ObligacionNegociable, CuotaparteFci or Otra
    /// (LegajoInvestmentClasses); null when not classified, and always null for every other kind. Picks the fiador
    /// haircut (engine 0.9). Editable through PATCH …/items/{itemId}.
    /// </summary>
    public string? InvestmentClass { get; init; }

    /// <summary>Partida, Dominio, Cuit, Cbu, Otro or Ninguno.</summary>
    public required string MatchKeyKind { get; init; }

    /// <summary>The normalized identifier, or null.</summary>
    public string? MatchKey { get; init; }

    /// <summary>As printed.</summary>
    public string? Description { get; init; }

    /// <summary>0..100, or null.</summary>
    public decimal? OwnershipPct { get; init; }

    /// <summary>Pesos, from the manifestación; null when it does not list the item.</summary>
    public decimal? MarketValue { get; init; }

    /// <summary>The document <see cref="MarketValue"/> was read from.</summary>
    public Guid? MarketValueDocumentId { get; init; }

    /// <summary>Pesos, from Bienes Personales: the floor of the market value. Null when it does not list the item.</summary>
    public decimal? FiscalValue { get; init; }

    /// <summary>The document <see cref="FiscalValue"/> was read from.</summary>
    public Guid? FiscalValueDocumentId { get; init; }

    /// <summary>The asset's currency (ISO) when printed. The values above are in pesos regardless.</summary>
    public string? Currency { get; init; }

    /// <summary>Assigned to the person's business activity.</summary>
    public required bool IsActivityAsset { get; init; }

    /// <summary>The casa-habitación.</summary>
    public required bool IsHousing { get; init; }

    /// <summary>XX-XXXXXXXX-X of the company held, when the match key is a CUIT.</summary>
    public string? LinkedCuit { get; init; }

    /// <summary>Matched, OnlyInBienesPersonales, OnlyInManifestacion, Conflict or AnalystConfirmed.</summary>
    public required string ReconciliationStatus { get; init; }

    /// <summary>For a conflict (or a confirmed one): OwnershipDiffers, BelowFiscalFloor, CurrencyDiffers.</summary>
    public required IReadOnlyList<string> ConflictReasons { get; init; }

    /// <summary>The fields an analyst set; a rebuild keeps them.</summary>
    public required IReadOnlyList<string> EditedFields { get; init; }

    /// <summary>The analyst's note.</summary>
    public string? AnalystNote { get; init; }

    /// <summary>UTC. Also the row's version: a PATCH racing a rebuild answers 409 item_changed.</summary>
    public required DateTime UpdatedAt { get; init; }

    /// <summary>Who last edited it (a user of this tenant); null when a rebuild last wrote it.</summary>
    public string? UpdatedByName { get; init; }
}
