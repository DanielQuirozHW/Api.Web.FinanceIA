// Project : Api.Web.FinanceIA
// Created : 2026-10-19

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>The merge half of <see cref="LegajoReconciliation"/>: how a rebuild meets the stored rows.</summary>
public static partial class LegajoReconciliation
{
    /// <summary>
    /// Merges <paramref name="build"/> into the stored rows. Items pair by ItemKey: a kept row keeps its
    /// ItemId, its <see cref="LegajoItem.AnalystNote"/>, and every field listed in its
    /// <see cref="LegajoItem.EditedFields"/>; its other columns take the build's values. The investment class follows
    /// the same rule, except that a row no longer of kind Inversion always loses it (and its edit flag). An
    /// <see cref="LegajoReconciliationStatuses.AnalystConfirmed"/> row stays confirmed only while the build
    /// still finds a conflict with the same <see cref="Fingerprint"/>; otherwise the confirmation lapses.
    /// Stored items the build no longer computes are removed. Incomes pair by source, concept and range
    /// and carry no analyst state.
    /// </summary>
    /// <param name="existingItems">The legajo's stored items — TRACKED: kept rows are modified in place.</param>
    /// <param name="existingIncomes">The legajo's stored incomes — TRACKED, likewise.</param>
    /// <param name="build">What the documents say now.</param>
    /// <param name="now">UTC; stamped on what changes.</param>
    public static LegajoReconciliationMergeResult Merge(
        IReadOnlyList<LegajoItem> existingItems,
        IReadOnlyList<LegajoIncome> existingIncomes,
        LegajoReconciliationBuild build,
        DateTime now)
    {
        ArgumentNullException.ThrowIfNull(existingItems);
        ArgumentNullException.ThrowIfNull(existingIncomes);
        ArgumentNullException.ThrowIfNull(build);

        // ── Items ───────────────────────────────────────────────────────────────
        var stored = existingItems.ToDictionary(i => i.ItemKey, StringComparer.Ordinal);
        var itemsAdded = new List<LegajoItem>();
        int itemsUpdated = 0, lapsed = 0;

        foreach (var built in build.Items)
        {
            if (!stored.Remove(built.ItemKey, out var row))
            {
                built.ItemId    = Guid.NewGuid();
                built.CreatedAt = now;
                built.UpdatedAt = now;
                built.UpdatedBy = null;
                itemsAdded.Add(built);
                continue;
            }

            var before  = Snapshot(row);
            var edited  = EditedFieldsOf(row);
            var wasConfirmed = row.ReconciliationStatus == LegajoReconciliationStatuses.AnalystConfirmed;

            row.Side                  = built.Side;
            row.Kind                  = built.Kind;
            row.MatchKeyKind          = built.MatchKeyKind;
            row.MatchKey              = built.MatchKey;
            row.Description           = built.Description;
            row.MarketValue           = built.MarketValue;
            row.MarketValueDocumentId = built.MarketValueDocumentId;
            row.FiscalValue           = built.FiscalValue;
            row.FiscalValueDocumentId = built.FiscalValueDocumentId;
            row.Currency              = built.Currency;
            row.LinkedCuit            = built.LinkedCuit;
            row.ConflictReasons       = built.ConflictReasons;

            if (!edited.Contains(LegajoItemEditableFields.OwnershipPct))    row.OwnershipPct    = built.OwnershipPct;
            if (!edited.Contains(LegajoItemEditableFields.IsActivityAsset)) row.IsActivityAsset = built.IsActivityAsset;
            if (!edited.Contains(LegajoItemEditableFields.IsHousing))       row.IsHousing       = built.IsHousing;

            // The class: the analyst's while the item is still an investment. An item that stopped being one loses
            // it — and the edit with it — because only an Inversion row may carry a class (CK_LegajoItems_InvestmentClass).
            if (built.Kind != LegajoItemKinds.Inversion)
            {
                row.InvestmentClass = null;
                if (edited.Contains(LegajoItemEditableFields.InvestmentClass))
                    row.EditedFields = EditedFieldsCsv(edited.Where(f => f != LegajoItemEditableFields.InvestmentClass));
            }
            else if (!edited.Contains(LegajoItemEditableFields.InvestmentClass))
            {
                row.InvestmentClass = built.InvestmentClass;
            }

            var keepsConfirmation = wasConfirmed
                && built.ReconciliationStatus == LegajoReconciliationStatuses.Conflict
                && string.Equals(row.ConfirmedFingerprint, Fingerprint(row), StringComparison.Ordinal);

            row.ReconciliationStatus = keepsConfirmation ? LegajoReconciliationStatuses.AnalystConfirmed : built.ReconciliationStatus;
            if (wasConfirmed && !keepsConfirmation)
            {
                row.ConfirmedFingerprint = null;
                lapsed++;
            }

            if (Snapshot(row) != before)
            {
                row.UpdatedAt = now;
                row.UpdatedBy = null;
                itemsUpdated++;
            }
        }

        // ── Incomes ─────────────────────────────────────────────────────────────
        var storedIncomes = existingIncomes.ToDictionary(IncomeKey);
        var incomesAdded = new List<LegajoIncome>();
        var incomesUpdated = 0;

        foreach (var built in build.Incomes)
        {
            if (!storedIncomes.Remove(IncomeKey(built), out var row))
            {
                built.IncomeId  = Guid.NewGuid();
                built.CreatedAt = now;
                incomesAdded.Add(built);
                continue;
            }

            if (row.Amount == built.Amount && row.Period == built.Period && row.Currency == built.Currency
                && row.DocumentId == built.DocumentId && row.ExtractionId == built.ExtractionId)
                continue;

            row.Amount       = built.Amount;
            row.Period       = built.Period;
            row.Currency     = built.Currency;
            row.DocumentId   = built.DocumentId;
            row.ExtractionId = built.ExtractionId;
            incomesUpdated++;
        }

        // A removed item that was confirmed loses its confirmation too.
        lapsed += stored.Values.Count(i => i.ReconciliationStatus == LegajoReconciliationStatuses.AnalystConfirmed);

        return new LegajoReconciliationMergeResult(
            itemsAdded, itemsUpdated, stored.Values.ToList(), lapsed,
            incomesAdded, incomesUpdated, storedIncomes.Values.ToList());
    }

    /// <summary>
    /// The conflict an analyst confirms: SHA-256 (hex) of the figures that make it — market value, fiscal
    /// value, currency and the conflict reasons. A confirmation stands while this does not change; the
    /// ownership the analyst may have edited is not part of it (OwnershipDiffers is).
    /// </summary>
    public static string Fingerprint(LegajoItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var text = string.Join("|",
            item.MarketValue?.ToString("0.00", CultureInfo.InvariantCulture) ?? "-",
            item.FiscalValue?.ToString("0.00", CultureInfo.InvariantCulture) ?? "-",
            item.Currency ?? "-",
            item.ConflictReasons ?? "-");

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    /// <summary>The fields listed in <see cref="LegajoItem.EditedFields"/>.</summary>
    public static IReadOnlySet<string> EditedFieldsOf(LegajoItem item)
        => string.IsNullOrWhiteSpace(item.EditedFields)
            ? new HashSet<string>()
            : item.EditedFields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();

    /// <summary>The CSV for <see cref="LegajoItem.EditedFields"/>, in <see cref="LegajoItemEditableFields.Known"/> order.</summary>
    public static string? EditedFieldsCsv(IEnumerable<string> fields)
    {
        var set = fields.ToHashSet();
        var ordered = LegajoItemEditableFields.Known.Where(set.Contains).ToList();
        return ordered.Count > 0 ? string.Join(",", ordered) : null;
    }

    private static (string, DateOnly, DateOnly) IncomeKey(LegajoIncome income)
        => ($"{income.Source}|{income.Concept}", income.PeriodFrom, income.PeriodTo);

    /// <summary>Every column a rebuild may write, as one string: compared before and after to tell whether the row changed.</summary>
    private static string Snapshot(LegajoItem i)
        => string.Join("|", i.Side, i.Kind, i.InvestmentClass, i.MatchKeyKind, i.MatchKey, i.Description,
            Figure(i.OwnershipPct), Figure(i.MarketValue), i.MarketValueDocumentId, Figure(i.FiscalValue),
            i.FiscalValueDocumentId, i.Currency, i.IsActivityAsset, i.IsHousing, i.LinkedCuit, i.ReconciliationStatus,
            i.ConflictReasons, i.ConfirmedFingerprint, i.EditedFields);

    /// <summary>A decimal by value, whatever its scale (100 and 100.00 read back from SQL are the same figure).</summary>
    private static string? Figure(decimal? value) => value?.ToString("0.############", CultureInfo.InvariantCulture);
}
