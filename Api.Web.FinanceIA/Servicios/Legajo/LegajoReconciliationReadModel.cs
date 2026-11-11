// Project : Api.Web.FinanceIA
// Created : 2026-10-19

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// Builds the inventory and incomes read models from the stored rows (M10 Phase 3b). Pure. Every total,
/// difference and precedence pick is computed HERE, at read time — none of it is stored.
/// </summary>
public static class LegajoReconciliationReadModel
{
    /// <summary>The sales-like figures the coherence compares: (source, concept).</summary>
    private static readonly HashSet<(string Source, string Concept)> SalesFigures =
    [
        (LegajoIncomeSources.Iva, LegajoIncomeConcepts.Ventas),
        (LegajoIncomeSources.Facturacion, LegajoIncomeConcepts.Ventas),
        (LegajoIncomeSources.Iibb, LegajoIncomeConcepts.Ventas),
        (LegajoIncomeSources.Manifestacion, LegajoIncomeConcepts.Ventas),
        (LegajoIncomeSources.Certificacion, LegajoIncomeConcepts.IngresoCertificado)
    ];

    /// <summary>Sales precedence: DDJJ IVA (or facturación), then the contrasting sources.</summary>
    private static readonly IReadOnlyList<string> SalesPrecedence =
    [
        LegajoIncomeSources.Iva, LegajoIncomeSources.Facturacion, LegajoIncomeSources.Iibb,
        LegajoIncomeSources.Certificacion, LegajoIncomeSources.Manifestacion
    ];

    /// <summary>Result and consumption precedence: Ganancias (F.711), then the manifestación's estado de resultados.</summary>
    private static readonly IReadOnlyList<string> ResultPrecedence =
        [LegajoIncomeSources.Ganancias, LegajoIncomeSources.Manifestacion];

    /// <summary>The inventory: groups by side and kind, totals, status counts.</summary>
    /// <param name="personLegajoId">The legajo.</param>
    /// <param name="items">Its stored items.</param>
    /// <param name="userNames">Display names of this tenant's users, by UserId.</param>
    public static LegajoInventoryResponse BuildInventory(
        Guid personLegajoId, IReadOnlyList<LegajoItem> items, IReadOnlyDictionary<Guid, string> userNames)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(userNames);

        var groups = items
            .GroupBy(i => (i.Side, i.Kind))
            .OrderBy(g => g.Key.Side == LegajoItemSides.Activo ? 0 : 1)
            .ThenBy(g => IndexOf(LegajoItemKinds.Known, g.Key.Kind))
            .Select(g => new LegajoInventoryGroupResponse
            {
                Side             = g.Key.Side,
                Kind             = g.Key.Kind,
                MarketValueTotal = g.Sum(i => i.MarketValue ?? 0m),
                FiscalValueTotal = g.Sum(i => i.FiscalValue ?? 0m),
                Items            = g.OrderBy(i => i.Description ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                                    .ThenBy(i => i.ItemKey, StringComparer.Ordinal)
                                    .Select(i => Item(i, userNames))
                                    .ToList()
            })
            .ToList();

        var activo = items.Where(i => i.Side == LegajoItemSides.Activo).ToList();
        var activoMarket = activo.Sum(i => i.MarketValue ?? 0m);
        var pasivoMarket = items.Where(i => i.Side == LegajoItemSides.Pasivo).Sum(i => i.MarketValue ?? 0m);

        return new LegajoInventoryResponse
        {
            PersonLegajoId = personLegajoId,
            Groups         = groups,
            Totals         = new LegajoInventoryTotalsResponse
            {
                ActivoMarketValue         = activoMarket,
                ActivoFiscalValue         = activo.Sum(i => i.FiscalValue ?? 0m),
                PasivoMarketValue         = pasivoMarket,
                PatrimonioNetoMarketValue = activoMarket - pasivoMarket,
                ActivityAssetsMarketValue = activo.Where(i => i.IsActivityAsset).Sum(i => i.MarketValue ?? 0m),
                ActivoWithoutMarketValue  = activo.Count(i => i.MarketValue is null),
                ActivoWithoutFiscalValue  = activo.Count(i => i.FiscalValue is null)
            },
            StatusCounts = LegajoReconciliationStatuses.Known.ToDictionary(
                s => s, s => items.Count(i => i.ReconciliationStatus == s)),
            BienesPersonalesDocumentIds = items.Select(i => i.FiscalValueDocumentId).OfType<Guid>().Distinct().Order().ToList(),
            ManifestacionDocumentIds    = items.Select(i => i.MarketValueDocumentId).OfType<Guid>().Distinct().Order().ToList(),
            LastUpdatedAt               = items.Count > 0 ? items.Max(i => i.UpdatedAt) : null
        };
    }

    /// <summary>
    /// <paramref name="inventory"/> with the avales y/o coobligaciones otorgados of its manifestación — the one
    /// document of <see cref="LegajoInventoryResponse.ManifestacionDocumentIds"/> — read from that document's
    /// current run on its effective values (mb-1.2, <see cref="LegajoReconciliation.GuaranteesGivenOf"/>). Unchanged
    /// when there is no such document, its run is missing or failed, or it declares none.
    /// </summary>
    /// <param name="inventory">The inventory built from the stored items.</param>
    /// <param name="runs">The current extraction runs, keyed by document; may hold more documents than needed.</param>
    /// <param name="corrections">The corrections of those runs, oldest first.</param>
    public static LegajoInventoryResponse WithGuaranteesGiven(
        LegajoInventoryResponse inventory,
        IReadOnlyDictionary<Guid, LegajoExtraction> runs,
        IReadOnlyList<LegajoCorrection> corrections)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(corrections);

        if (inventory.ManifestacionDocumentIds is not [var documentId]
            || !runs.TryGetValue(documentId, out var run)
            || run.Status != LegajoExtractionStatuses.Succeeded
            || run.DocumentType != DocumentTypes.ManifestacionBienes)
        {
            return inventory;
        }

        var (effective, _) = LegajoEffectiveValues.Apply(
            LegajoExtractionPrompts.ManifestacionBienes, run.ResultJson,
            corrections.Where(c => c.ExtractionId == run.ExtractionId));

        return LegajoReconciliation.GuaranteesGivenOf(effective) is { } amount
            ? inventory with { GarantiasOtorgadasDeclaradas = amount, GarantiasOtorgadasDocumentId = documentId }
            : inventory;
    }

    /// <summary>The incomes by period, newest first, each with its coherence and precedence picks.</summary>
    public static LegajoIncomesResponse BuildIncomes(Guid personLegajoId, IReadOnlyList<LegajoIncome> incomes)
    {
        ArgumentNullException.ThrowIfNull(incomes);

        var periods = incomes
            .GroupBy(i => (i.PeriodFrom, i.PeriodTo))
            .OrderByDescending(g => g.Key.PeriodTo)
            .ThenByDescending(g => g.Key.PeriodFrom)
            .Select(g =>
            {
                var rows = g.OrderBy(i => IndexOf(LegajoIncomeSources.Known, i.Source))
                            .ThenBy(i => IndexOf(LegajoIncomeConcepts.Known, i.Concept))
                            .ToList();
                var period = rows[0].Period;

                return new LegajoIncomePeriodResponse
                {
                    Period      = period,
                    PeriodFrom  = g.Key.PeriodFrom,
                    PeriodTo    = g.Key.PeriodTo,
                    Granularity = period is null ? "range" : period.Length == 4 ? "year" : "month",
                    Entries     = rows.Select(i => new LegajoIncomeEntryResponse
                    {
                        IncomeId     = i.IncomeId,
                        Source       = i.Source,
                        Concept      = i.Concept,
                        Amount       = i.Amount,
                        Currency     = i.Currency,
                        DocumentId   = i.DocumentId,
                        ExtractionId = i.ExtractionId
                    }).ToList(),
                    Coherence           = Coherence(rows),
                    PrimarySalesSource  = SalesPrecedence.FirstOrDefault(s => rows.Any(i => i.Source == s && SalesFigures.Contains((i.Source, i.Concept)))),
                    PrimaryResultSource = ResultPrecedence.FirstOrDefault(s => rows.Any(i => i.Source == s
                                                                                     && !SalesFigures.Contains((i.Source, i.Concept))))
                };
            })
            .ToList();

        return new LegajoIncomesResponse { PersonLegajoId = personLegajoId, Periods = periods };
    }

    /// <summary>The coherence of one period's sales figures (one per source; a source's figures are summed).</summary>
    public static LegajoIncomeCoherenceResponse Coherence(IReadOnlyList<LegajoIncome> period)
    {
        ArgumentNullException.ThrowIfNull(period);

        var bySource = period
            .Where(i => SalesFigures.Contains((i.Source, i.Concept)))
            .GroupBy(i => i.Source)
            .Select(g => (Source: g.Key, Amount: g.Sum(i => i.Amount)))
            .OrderBy(s => IndexOf(LegajoIncomeSources.Known, s.Source))
            .ToList();

        if (bySource.Count < 2)
            return new LegajoIncomeCoherenceResponse { Sources = bySource.Count };

        var high = bySource.MaxBy(s => s.Amount);
        var low  = bySource.MinBy(s => s.Amount);

        return new LegajoIncomeCoherenceResponse
        {
            Sources         = bySource.Count,
            MaxRelativeDiff = high.Amount > 0 ? Math.Round((high.Amount - low.Amount) / high.Amount, 4) : null,
            HighSource      = high.Source,
            LowSource       = low.Source
        };
    }

    /// <summary>One item, its CSV columns split, its editor named only when a user of this tenant.</summary>
    public static LegajoItemResponse Item(LegajoItem item, IReadOnlyDictionary<Guid, string> userNames)
    {
        ArgumentNullException.ThrowIfNull(item);

        return new LegajoItemResponse
        {
            ItemId                = item.ItemId,
            Side                  = item.Side,
            Kind                  = item.Kind,
            // A value this code does not know, or one on a non-Inversion row, reads as unclassified.
            InvestmentClass       = LegajoInvestmentClasses.Normalize(item.Kind, item.InvestmentClass),
            MatchKeyKind          = item.MatchKeyKind,
            MatchKey              = item.MatchKey,
            Description           = item.Description,
            OwnershipPct          = item.OwnershipPct,
            MarketValue           = item.MarketValue,
            MarketValueDocumentId = item.MarketValueDocumentId,
            FiscalValue           = item.FiscalValue,
            FiscalValueDocumentId = item.FiscalValueDocumentId,
            Currency              = item.Currency,
            IsActivityAsset       = item.IsActivityAsset,
            IsHousing             = item.IsHousing,
            LinkedCuit            = item.LinkedCuit,
            ReconciliationStatus  = item.ReconciliationStatus,
            ConflictReasons       = Split(item.ConflictReasons),
            EditedFields          = Split(item.EditedFields),
            AnalystNote           = item.AnalystNote,
            UpdatedAt             = item.UpdatedAt,
            UpdatedByName         = item.UpdatedBy is { } by && userNames.TryGetValue(by, out var name) ? name : null
        };
    }

    private static IReadOnlyList<string> Split(string? csv)
        => string.IsNullOrWhiteSpace(csv)
            ? []
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == value)
                return i;
        }

        return list.Count;
    }
}
