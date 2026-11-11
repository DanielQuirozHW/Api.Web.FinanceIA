// Project : Api.Web.FinanceIA
// Created : 2026-10-19

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using V = Api.Web.FinanceIA.Servicios.Legajo.LegajoExtractionVocabulary;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// The reconciliation of a legajo (M10 Phase 3b): derives the reconciled inventory and the incomes by
/// period from the approved documents' EFFECTIVE values, and merges them with the stored rows. Pure and
/// deterministic — no model call, no I/O; the service around it loads and saves.
///
/// <para>
/// Precedence when two documents disagree (design "Reglas de precedencia"):
/// existence and ownership of an asset → Bienes Personales, contrasted with the manifestación;
/// value of an asset → manifestación, with Bienes Personales kept beside it as the floor;
/// debts → manifestación; sales → DDJJ IVA (or facturación), contrasted with certificación, IIBB and
/// Ganancias; result and consumption → Ganancias. Assets are paired across documents by an EXACT
/// normalized match key (partida, dominio, CUIT, CBU) only; everything else stays unpaired with its status.
/// </para>
///
/// <para>
/// Amounts are brought to PESOS with the document's amountsUnit and never multiplied by the ownership
/// percentage (both documents already state the person's share). Totals, ratios and coherence figures
/// are not derived here: they are computed at read time (<see cref="LegajoReconciliationReadModel"/>).
/// </para>
/// </summary>
public static partial class LegajoReconciliation
{
    /// <summary>The largest amount [dbo].[LegajoItems]/[LegajoIncomes] can hold (DECIMAL(19,2)), exclusive.</summary>
    private const decimal MaxStoredAmount = 100_000_000_000_000_000m;

    /// <summary>The manifestación's avales y/o coobligaciones otorgados (mb-1.2).</summary>
    public const string GuaranteesGivenPath = "/contingencies/guaranteesGiven";

    /// <summary>Derives the items and incomes from <paramref name="inputs"/>.</summary>
    /// <param name="tenantId">Stamped on every row.</param>
    /// <param name="personLegajoId">Stamped on every row.</param>
    /// <param name="inputs">The legajo's approved documents.</param>
    public static LegajoReconciliationBuild Build(
        string tenantId, Guid personLegajoId, IReadOnlyList<LegajoReconciliationInput> inputs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(inputs);

        var skipped = new List<LegajoReconciliationSkip>();

        // ── Selection ───────────────────────────────────────────────────────────
        var superseded = inputs
            .Where(i => i.SupersedesDocumentId is not null)
            .Select(i => i.SupersedesDocumentId!.Value)
            .ToHashSet();

        var usable = new List<Readable>();
        foreach (var input in inputs.OrderBy(i => i.DocumentId))
        {
            if (superseded.Contains(input.DocumentId))
            {
                skipped.Add(new LegajoReconciliationSkip(input.DocumentId, LegajoReconciliationSkipReasons.Superseded));
                continue;
            }

            var tree = input.Extraction.Effective;
            if (V.Factor(LegajoJsonValues.FieldString(tree, V.AmountsUnitPath)) is not { } factor)
            {
                skipped.Add(new LegajoReconciliationSkip(input.DocumentId, LegajoReconciliationSkipReasons.AmountsUnitMissing));
                continue;
            }

            usable.Add(new Readable(input, tree, factor));
        }

        var contributed   = new HashSet<Guid>();
        var olderSnapshot = new HashSet<Guid>();
        var replaced      = new HashSet<Guid>();

        var items   = BuildItems(tenantId, personLegajoId, usable, contributed, olderSnapshot);
        var incomes = BuildIncomes(tenantId, personLegajoId, usable, contributed, replaced);

        // A document is reported only when it contributed NOTHING: an older manifestación may still be the
        // one whose estado de resultados covers a year.
        foreach (var readable in usable)
        {
            var id = readable.Input.DocumentId;
            if (contributed.Contains(id))
                continue;

            skipped.Add(new LegajoReconciliationSkip(id,
                olderSnapshot.Contains(id) ? LegajoReconciliationSkipReasons.OlderSnapshot
                : replaced.Contains(id)    ? LegajoReconciliationSkipReasons.Replaced
                                           : LegajoReconciliationSkipReasons.NoFigures));
        }

        return new LegajoReconciliationBuild(
            items,
            incomes,
            usable.Select(r => r.Input.DocumentId).Where(contributed.Contains).ToList(),
            skipped.OrderBy(s => s.DocumentId).ToList());
    }

    // ── Shared readers ─────────────────────────────────────────────────────────

    /// <summary>A usable input: its effective tree and the factor that brings its amounts to pesos.</summary>
    private sealed record Readable(LegajoReconciliationInput Input, JsonObject Tree, decimal Factor)
    {
        public string DocumentType => Input.Extraction.Run.DocumentType;

        public Guid ExtractionId => Input.Extraction.Run.ExtractionId;

        /// <summary>An amount in pesos, rounded to cents; null when absent or beyond what the column holds.</summary>
        public decimal? Pesos(JsonNode? node) => ToPesos(node, Factor);

        /// <summary>The provenance field's value in pesos.</summary>
        public decimal? FieldPesos(string fieldPath) => Pesos(LegajoJsonPointer.Get(Tree, fieldPath + "/value"));

        /// <summary>The rows of the array at <paramref name="path"/>.</summary>
        public IEnumerable<JsonObject> Rows(string path) => LegajoJsonValues.ArrayAt(Tree, path).OfType<JsonObject>();
    }

    /// <summary>
    /// The avales y/o coobligaciones otorgados a manifestación declares (<see cref="GuaranteesGivenPath"/>), in
    /// pesos, from its EFFECTIVE values; null when it prints none, was extracted before mb-1.2 (no such node), or
    /// states no amounts unit. Not reconciled into any table: the inventory read exposes it beside the items.
    /// </summary>
    public static decimal? GuaranteesGivenOf(JsonObject effective)
    {
        ArgumentNullException.ThrowIfNull(effective);

        return V.Factor(LegajoJsonValues.FieldString(effective, V.AmountsUnitPath)) is { } factor
            ? ToPesos(LegajoJsonPointer.Get(effective, GuaranteesGivenPath + "/value"), factor)
            : null;
    }

    /// <summary>A printed amount brought to pesos and rounded to cents; null when absent or beyond what the columns hold.</summary>
    private static decimal? ToPesos(JsonNode? node, decimal factor)
    {
        if (LegajoJsonValues.Decimal(node) is not { } printed)
            return null;

        decimal pesos;
        try
        {
            pesos = Math.Round(printed * factor, 2, MidpointRounding.AwayFromZero);
        }
        catch (OverflowException)
        {
            return null;
        }

        return Math.Abs(pesos) < MaxStoredAmount ? pesos : null;
    }

    /// <summary>The node as a boolean, or null.</summary>
    private static bool? Boolean(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;

        if (value.TryGetValue<bool>(out var b)) return b;
        if (value.TryGetValue<JsonElement>(out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return e.ValueKind == JsonValueKind.True;

        return null;
    }

    /// <summary>The rectificativa rank of a DDJJ: its number, 1 for an unnumbered rectificativa, 0 for an original.</summary>
    private static int RectificativaRank(int? number, bool? isRectificativa)
        => number is { } n and > 0 ? n : isRectificativa == true ? 1 : 0;

    /// <summary>A <c>yyyy-MM</c> or <c>yyyy</c> label when the range is exactly one month or one calendar year; else null.</summary>
    public static string? PeriodLabel(DateOnly from, DateOnly to)
    {
        if (from.Day == 1 && to == from.AddMonths(1).AddDays(-1))
            return from.ToString("yyyy-MM", CultureInfo.InvariantCulture);

        if (from.Month == 1 && from.Day == 1 && to.Year == from.Year && to.Month == 12 && to.Day == 31)
            return from.ToString("yyyy", CultureInfo.InvariantCulture);

        return null;
    }

    /// <summary>The calendar year as a range, or null when the year is not plausible.</summary>
    private static (DateOnly From, DateOnly To)? YearRange(int? year)
        => year is >= 1900 and <= 2100 ? (new DateOnly(year.Value, 1, 1), new DateOnly(year.Value, 12, 31)) : null;

    /// <summary>The month (its first day) as a range.</summary>
    private static (DateOnly From, DateOnly To) MonthRange(DateOnly month)
        => (month, month.AddMonths(1).AddDays(-1));
}
