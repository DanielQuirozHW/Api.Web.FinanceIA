// Project : Api.Web.FinanceIA
// Created : 2026-10-19

using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>The incomes half of <see cref="LegajoReconciliation"/>.</summary>
public static partial class LegajoReconciliation
{
    /// <summary>
    /// What one document says for one source and range, at one rectificativa rank: the amounts by concept,
    /// already summed inside the document.
    /// </summary>
    private sealed record Candidate(
        string Source, DateOnly From, DateOnly To, int Rank, Readable Document, Dictionary<string, decimal> Amounts);

    private static List<LegajoIncome> BuildIncomes(
        string tenantId, Guid personLegajoId, IReadOnlyList<Readable> usable,
        HashSet<Guid> contributed, HashSet<Guid> replaced)
    {
        var candidates = new List<Candidate>();
        foreach (var readable in usable)
        {
            var own = new Dictionary<(string Source, DateOnly From, DateOnly To, int Rank), Candidate>();

            void Add(string source, (DateOnly From, DateOnly To) range, int rank, string concept, decimal? amount)
            {
                if (amount is not { } value)
                    return;

                var key = (source, range.From, range.To, rank);
                if (!own.TryGetValue(key, out var candidate))
                {
                    candidate = new Candidate(source, range.From, range.To, rank, readable, new Dictionary<string, decimal>());
                    own[key] = candidate;
                }

                candidate.Amounts[concept] = candidate.Amounts.GetValueOrDefault(concept) + value;
            }

            switch (readable.DocumentType)
            {
                case DocumentTypes.DdjjIva:               IvaIncomes(readable, Add); break;
                case DocumentTypes.DdjjIibb:              IibbIncomes(readable, Add); break;
                case DocumentTypes.CertificacionIngresos: CertificacionIncomes(readable, Add); break;
                case DocumentTypes.Ganancias:             GananciasIncomes(readable, Add); break;
                case DocumentTypes.ManifestacionBienes:   ManifestacionIncomes(readable, Add); break;
                case DocumentTypes.Monotributo:           MonotributoIncomes(readable, Add); break;
            }

            // Inside one document, a period declared twice (an original and its rectificativa) keeps the
            // highest rectificativa.
            candidates.AddRange(own.Values
                .GroupBy(c => (c.Source, c.From, c.To))
                .Select(g => g.MaxBy(c => c.Rank)!));
        }

        // Across documents: the highest rectificativa, then the newest upload, per source and range.
        var incomes = new List<LegajoIncome>();
        foreach (var group in candidates.GroupBy(c => (c.Source, c.From, c.To)).OrderBy(g => g.Key))
        {
            var ordered = group
                .OrderByDescending(c => c.Rank)
                .ThenByDescending(c => c.Document.Input.UploadedAt)
                .ThenByDescending(c => c.Document.Input.DocumentId)
                .ToList();

            var winner = ordered[0];
            contributed.Add(winner.Document.Input.DocumentId);
            foreach (var loser in ordered.Skip(1))
                replaced.Add(loser.Document.Input.DocumentId);

            foreach (var (concept, amount) in winner.Amounts.OrderBy(a => a.Key, StringComparer.Ordinal))
            {
                if (Math.Abs(amount) >= MaxStoredAmount)
                    continue;

                incomes.Add(new LegajoIncome
                {
                    TenantId       = tenantId,
                    PersonLegajoId = personLegajoId,
                    Period         = PeriodLabel(winner.From, winner.To),
                    PeriodFrom     = winner.From,
                    PeriodTo       = winner.To,
                    Source         = winner.Source,
                    Concept        = concept,
                    Amount         = amount,
                    Currency       = "ARS",
                    DocumentId     = winner.Document.Input.DocumentId,
                    ExtractionId   = winner.Document.ExtractionId
                });
            }
        }

        return incomes;
    }

    private delegate void AddIncome(string source, (DateOnly From, DateOnly To) range, int rank, string concept, decimal? amount);

    /// <summary>DDJJ IVA: per period, the sales (neto gravado + exentas y no gravadas).</summary>
    private static void IvaIncomes(Readable doc, AddIncome add)
    {
        foreach (var row in doc.Rows("/periods"))
        {
            if (LegajoJsonValues.Period(row["period"]) is not { } month)
                continue;

            var gravado = doc.Pesos(row["ventasNetoGravado"]);
            var exentas = doc.Pesos(row["ventasExentasNoGravadas"]);
            if (gravado is null && exentas is null)
                continue;

            add(LegajoIncomeSources.Iva, MonthRange(month), RectificativaRank(LegajoJsonValues.Integer(row["rectificativaNumber"]), null),
                LegajoIncomeConcepts.Ventas, (gravado ?? 0m) + (exentas ?? 0m));
        }
    }

    /// <summary>
    /// DDJJ IIBB: per DDJJ (a month, or the year of an annual one), the total ingresos as printed; when the
    /// DDJJ prints no total, the sum of its determinación rows' base imponible.
    /// </summary>
    private static void IibbIncomes(Readable doc, AddIncome add)
    {
        var annual = YearRange(LegajoJsonValues.Integer(LegajoJsonPointer.Get(doc.Tree, "/header/fiscalYear/value")));

        (DateOnly From, DateOnly To)? RangeOf(JsonObject row)
            => LegajoJsonValues.Period(row["period"]) is { } month ? MonthRange(month) : annual;

        var totals = new Dictionary<((DateOnly, DateOnly) Range, int Rank), decimal>();
        foreach (var row in doc.Rows("/periods"))
        {
            if (RangeOf(row) is not { } range || doc.Pesos(row["ingresosTotales"]) is not { } total)
                continue;

            var key = (range, RectificativaRank(LegajoJsonValues.Integer(row["rectificativaNumber"]), null));
            totals[key] = totals.GetValueOrDefault(key) + total;
        }

        var bases = new Dictionary<((DateOnly, DateOnly) Range, int Rank), decimal>();
        foreach (var row in doc.Rows("/rows"))
        {
            if (RangeOf(row) is not { } range || doc.Pesos(row["baseImponible"]) is not { } amount)
                continue;

            var key = (range, RectificativaRank(LegajoJsonValues.Integer(row["rectificativaNumber"]), null));
            bases[key] = bases.GetValueOrDefault(key) + amount;
        }

        foreach (var key in totals.Keys.Union(bases.Keys))
        {
            var amount = totals.TryGetValue(key, out var total) ? total : bases[key];
            add(LegajoIncomeSources.Iibb, key.Range, key.Rank, LegajoIncomeConcepts.Ventas, amount);
        }
    }

    /// <summary>
    /// Certificación de ingresos: the certified amounts per month; when no row is monthly, the total over
    /// the certified range (printed, else the sum of the rows).
    /// </summary>
    private static void CertificacionIncomes(Readable doc, AddIncome add)
    {
        var rows = doc.Rows("/rows").ToList();
        var monthly = rows.Where(r => LegajoJsonValues.Period(r["period"]) is not null).ToList();

        if (monthly.Count > 0)
        {
            foreach (var row in monthly)
            {
                add(LegajoIncomeSources.Certificacion, MonthRange(LegajoJsonValues.Period(row["period"])!.Value), 0,
                    LegajoIncomeConcepts.IngresoCertificado, doc.Pesos(row["amount"]));
            }

            return;
        }

        var from = LegajoJsonValues.Date(LegajoJsonPointer.Get(doc.Tree, "/header/periodFrom/value"));
        var to   = LegajoJsonValues.Date(LegajoJsonPointer.Get(doc.Tree, "/header/periodTo/value"));
        if (from is not { } f || to is not { } t || f > t)
            return;

        var amounts = rows.Select(r => doc.Pesos(r["amount"])).OfType<decimal>().ToList();
        var total = doc.FieldPesos("/totals/totalIngresos") ?? (amounts.Count > 0 ? amounts.Sum() : null);
        add(LegajoIncomeSources.Certificacion, (f, t), 0, LegajoIncomeConcepts.IngresoCertificado, total);
    }

    /// <summary>
    /// Ganancias, per fiscal year: the third-category result (Renta3ra), the fourth (Renta4ta), every other
    /// result — first and second category, and anything taxed by the impuesto cedular — (Otro), and the
    /// monto consumido. The rectificativa with the highest number wins.
    /// </summary>
    private static void GananciasIncomes(Readable doc, AddIncome add)
    {
        if (YearRange(LegajoJsonValues.Integer(LegajoJsonPointer.Get(doc.Tree, "/header/fiscalYear/value"))) is not { } year)
            return;

        var rank = RectificativaRank(
            LegajoJsonValues.Integer(LegajoJsonPointer.Get(doc.Tree, "/header/rectificativaNumber/value")),
            Boolean(LegajoJsonPointer.Get(doc.Tree, "/header/isRectificativa/value")));

        foreach (var row in doc.Rows("/categoryResults"))
        {
            var concept = (LegajoJsonValues.String(row["category"]), Boolean(row["isCedular"]) == true) switch
            {
                ("Tercera", false) => LegajoIncomeConcepts.Renta3ra,
                ("Cuarta", false)  => LegajoIncomeConcepts.Renta4ta,
                _                  => LegajoIncomeConcepts.Otro
            };

            add(LegajoIncomeSources.Ganancias, year, rank, concept, doc.Pesos(row["amount"]));
        }

        add(LegajoIncomeSources.Ganancias, year, rank, LegajoIncomeConcepts.MontoConsumido,
            doc.FieldPesos("/justification/montoConsumido"));
    }

    /// <summary>The manifestación's estado de resultados, when it has one: ingresos (Ventas) and resultado (Otro).</summary>
    private static void ManifestacionIncomes(Readable doc, AddIncome add)
    {
        var from = LegajoJsonValues.Date(LegajoJsonPointer.Get(doc.Tree, "/incomeStatement/periodFrom/value"));
        var to   = LegajoJsonValues.Date(LegajoJsonPointer.Get(doc.Tree, "/incomeStatement/periodTo/value"));
        if (from is not { } f || to is not { } t || f > t)
            return;

        add(LegajoIncomeSources.Manifestacion, (f, t), 0, LegajoIncomeConcepts.Ventas, doc.FieldPesos("/incomeStatement/ingresos"));
        add(LegajoIncomeSources.Manifestacion, (f, t), 0, LegajoIncomeConcepts.Otro, doc.FieldPesos("/incomeStatement/resultado"));
    }

    /// <summary>Monotributo: the payments per month (a cuota, not an income: kept as Otro, outside the coherence).</summary>
    private static void MonotributoIncomes(Readable doc, AddIncome add)
    {
        foreach (var row in doc.Rows("/payments"))
        {
            if (LegajoJsonValues.Period(row["period"]) is not { } month)
                continue;

            add(LegajoIncomeSources.Monotributo, MonthRange(month), 0, LegajoIncomeConcepts.Otro, doc.Pesos(row["amount"]));
        }
    }
}
