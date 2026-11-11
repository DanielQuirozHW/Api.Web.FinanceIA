// Project : Api.Web.FinanceIA
// Created : 2026-09-28

using System.Globalization;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.Legajo;
using V = Api.Web.FinanceIA.Servicios.Legajo.LegajoExtractionVocabulary;

namespace Api.Web.FinanceIA.Servicios;

// ── Implementation ──────────────────────────────────────────────────────────────

/// <summary>
/// Stateless, deterministic arithmetic checks over a legajo extraction (M10 Phase 2b; the Phase 3a types
/// live in the partial files LegajoIntegrityService.TaxReturns.cs and LegajoIntegrityService.Income.cs), in
/// BalanceIntegrityService's conventions: named checks, a check runs only when all of its operands were
/// extracted (otherwise its name is listed under <c>skipped</c>), es-AR anomaly texts with no "%", and
/// figures quoted in the unit the document prints. It FLAGS — it never corrects a figure. No DI
/// dependencies; can be instantiated directly in tests.
///
/// <para>
/// Arithmetic is decimal, in pesos: every printed amount is scaled by the document's amountsUnit (an
/// unread unit is assumed Pesos and reported so). A comparison passes when the difference is within
/// <see cref="RelativeTolerance"/> of the larger figure OR within the absolute rounding allowance: each
/// printed line may be rounded by up to half a printed unit, so a sum of n lines may drift by n/2 units
/// (at least one unit).
/// </para>
/// </summary>
public sealed partial class LegajoIntegrityService : ILegajoIntegrityService
{
    /// <summary>A difference within this share of the larger figure passes (0.5 in a hundred).</summary>
    public const decimal RelativeTolerance = 0.005m;

    /// <summary>Printed units a single summed operand may be rounded by.</summary>
    public const decimal AbsoluteRoundingAllowanceUnits = 0.5m;

    // ── Check names (persisted in ChecksJson.checks[].name / skipped[].name) ─────
    public const string BpRubroItemsCheck         = "bp_rubro_items";
    public const string BpRubroTotalsPaisCheck    = "bp_rubro_totals_pais";
    public const string BpRubroTotalsExteriorCheck = "bp_rubro_totals_exterior";
    public const string BpTotalBienesCheck        = "bp_total_bienes";
    public const string BpOwnershipPctCheck       = "bp_ownership_pct";
    public const string BpNonNegativeCheck        = "bp_non_negative";
    public const string BpFiscalYearPeriodCheck   = "bp_fiscal_year_period";

    public const string MbSectionLinesCheck       = "mb_section_lines";
    public const string MbTotalActivoCheck        = "mb_total_activo";
    public const string MbTotalPasivoCheck        = "mb_total_pasivo";
    public const string MbPatrimonioNetoCheck     = "mb_patrimonio_neto";
    public const string MbAnnexItemsCheck         = "mb_annex_items";
    public const string MbIncomeResultCheck       = "mb_income_result";
    public const string MbOwnershipPctCheck       = "mb_ownership_pct";

    /// <summary>
    /// Not a rule of the document: the figures could not be computed (a decimal overflow). A FAILED check,
    /// so the document goes to NeedsReview instead of the request failing.
    /// </summary>
    public const string ArithmeticErrorCheck      = "arithmetic_error";

    /// <summary>The row properties that hold a percentage, never scaled by the amounts unit.</summary>
    private static readonly IReadOnlySet<string> PercentProperties =
        new HashSet<string>(StringComparer.Ordinal) { "ownershipPct", "alicuotaPct" };

    // es-AR nouns for the messages.
    private static readonly IReadOnlyDictionary<string, string> RubroText = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Inmuebles"]                 = "inmuebles",
        ["Rodados"]                   = "rodados",
        ["DineroEfectivo"]            = "dinero en efectivo",
        ["Depositos"]                 = "depósitos",
        ["TitulosYAcciones"]          = "títulos y acciones",
        ["Creditos"]                  = "créditos",
        ["BienesMueblesRegistrables"] = "bienes muebles registrables",
        ["OtrosBienes"]               = "otros bienes",
        ["Otro"]                      = "otros"
    };

    private static readonly IReadOnlyDictionary<string, (string TotalPath, string Noun)> SectionTotals =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            [V.ActivoCorriente]   = ("/totals/activoCorriente",   "activo corriente"),
            [V.ActivoNoCorriente] = ("/totals/activoNoCorriente", "activo no corriente"),
            [V.PasivoCorriente]   = ("/totals/pasivoCorriente",   "pasivo corriente"),
            [V.PasivoNoCorriente] = ("/totals/pasivoNoCorriente", "pasivo no corriente")
        };

    // ── ILegajoIntegrityService ───────────────────────────────────────────────────

    /// <inheritdoc />
    public LegajoIntegrityReport Evaluate(string documentType, JsonObject result, DateOnly? periodEnd)
    {
        ArgumentNullException.ThrowIfNull(result);

        var unitRead = LegajoJsonValues.FieldString(result, V.AmountsUnitPath);
        var factor   = V.Factor(unitRead);
        var acc      = new Accumulator(result, factor is null ? V.Pesos : unitRead!, factor ?? 1m);

        // Arithmetic must never become a 500: the reader bounds every amount, but a hand-edited row (or a
        // future schema) could still overflow a scaled sum. The checks made so far are kept.
        try
        {
            switch (documentType)
            {
                case DocumentTypes.BienesPersonales:
                    EvaluateBienesPersonales(acc, periodEnd);
                    break;
                case DocumentTypes.ManifestacionBienes:
                    EvaluateManifestacionBienes(acc);
                    break;
                case DocumentTypes.Ganancias:
                    EvaluateGanancias(acc, periodEnd);
                    break;
                case DocumentTypes.DdjjIva:
                    EvaluateDdjjIva(acc, periodEnd);
                    break;
                case DocumentTypes.DdjjIibb:
                    EvaluateDdjjIibb(acc, periodEnd);
                    break;
                case DocumentTypes.CertificacionIngresos:
                    EvaluateCertificacionIngresos(acc, periodEnd);
                    break;
                case DocumentTypes.Monotributo:
                    EvaluateMonotributo(acc, periodEnd);
                    break;
            }
        }
        catch (ArithmeticException)
        {
            acc.Add(ArithmeticFailure());
        }

        return new LegajoIntegrityReport
        {
            Passed             = acc.Checks.All(c => c.Passed),
            AmountsUnit        = acc.Unit,
            AmountsUnitAssumed = factor is null,
            Checks             = acc.Checks,
            Skipped            = acc.Skipped
        };
    }

    /// <summary>The failed <see cref="ArithmeticErrorCheck"/>, as a check.</summary>
    public static LegajoIntegrityCheck ArithmeticFailure() => new()
    {
        Name    = ArithmeticErrorCheck,
        Passed  = false,
        Message = "No se pudieron verificar los importes: exceden el rango de cálculo."
    };

    // ── Bienes Personales ─────────────────────────────────────────────────────────

    private static void EvaluateBienesPersonales(Accumulator acc, DateOnly? periodEnd)
    {
        var items  = Rows(acc.Root, "/items");
        var totals = Rows(acc.Root, "/rubroTotals");

        // Items per (rubro, location) = the rubro total printed for that group.
        foreach (var group in totals.GroupBy(t => (Rubro: acc.Text(t, "rubro"), Location: acc.Text(t, "location"))))
        {
            var subject = $"{group.Key.Rubro}/{group.Key.Location}";
            var members = items.Where(i => acc.Text(i, "rubro") == group.Key.Rubro
                                        && acc.Text(i, "location") == group.Key.Location).ToList();

            // A rubro can be printed as a total without a detail (dinero en efectivo, typically): nothing to add up.
            if (members.Count == 0)
            {
                acc.Skip(BpRubroItemsCheck, subject);
                continue;
            }

            var printed = SumRows(acc, group, "total");
            var summed  = SumRows(acc, members, "fiscalValue");
            var rubro   = RubroText.GetValueOrDefault(group.Key.Rubro ?? string.Empty, "otros");
            var place   = group.Key.Location == V.Exterior ? "en el exterior" : "en el país";

            acc.Compare(BpRubroItemsCheck, subject, actual: printed, expected: summed, operands: members.Count,
                [.. group.Select(t => t.Path + "/total"), .. members.Select(m => m.Path + "/fiscalValue")],
                (e, a) => $"La suma de los bienes del rubro {rubro} {place} ({acc.Money(e)}) no coincide con el total " +
                          $"informado para ese rubro ({acc.Money(a)}).",
                flagged: [.. group.Select(t => t.Path + "/total")],
                subjectParts: new Dictionary<string, string>
                {
                    ["rubro"]    = group.Key.Rubro ?? "-",
                    ["location"] = group.Key.Location ?? "-"
                });
        }

        // Rubro totals per location = the location's total.
        RubroTotalsByLocation(acc, totals, V.Pais, "/totals/bienesPais", BpRubroTotalsPaisCheck, "en el país");
        RubroTotalsByLocation(acc, totals, V.Exterior, "/totals/bienesExterior", BpRubroTotalsExteriorCheck, "en el exterior");

        // bienesPais + bienesExterior = totalBienes.
        var pais     = acc.Field("/totals/bienesPais");
        var exterior = acc.Field("/totals/bienesExterior");
        acc.Compare(BpTotalBienesCheck, null, actual: acc.Field("/totals/totalBienes"), expected: Sum(pais, exterior), operands: 2,
            ["/totals/totalBienes", "/totals/bienesPais", "/totals/bienesExterior"],
            (e, a) => $"El total de bienes informado ({acc.Money(a)}) no coincide con la suma de los bienes en el país y " +
                      $"en el exterior ({acc.Money(e)}).",
            flagged: ["/totals/totalBienes"]);

        OwnershipRange(acc, BpOwnershipPctCheck, items, "bienes");

        // No negative fiscal values.
        var amounts = items.Select(i => (Path: i.Path + "/fiscalValue", Value: acc.Decimal(i, "fiscalValue")))
            .Concat(totals.Select(t => (Path: t.Path + "/total", Value: acc.Decimal(t, "total"))))
            .Concat(new[] { "bienesPais", "bienesExterior", "totalBienes", "bienesExentos", "minimoNoImponible", "baseImponible", "impuestoDeterminado" }
                .Select(n => (Path: "/totals/" + n, Value: acc.Field("/totals/" + n))))
            .Where(x => x.Value is not null)
            .ToList();

        if (amounts.Count == 0)
        {
            acc.Skip(BpNonNegativeCheck);
        }
        else
        {
            var negative = amounts.Where(x => x.Value < 0m).Select(x => x.Path).ToList();
            acc.Flag(BpNonNegativeCheck, negative.Count == 0, negative,
                Plural(negative.Count, "importe informado es negativo", "importes informados son negativos") +
                "; los valores fiscales no pueden ser negativos.");
        }

        FiscalYearPeriod(acc, BpFiscalYearPeriodCheck, periodEnd);
    }

    private static void RubroTotalsByLocation(
        Accumulator acc, IReadOnlyList<Row> totals, string location, string totalPath, string name, string place)
    {
        var printed = acc.Field(totalPath);

        // Nothing was printed per rubro: no sum to compare.
        if (totals.Count == 0 || printed is null)
        {
            acc.Skip(name);
            return;
        }

        var rows = totals.Where(t => acc.Text(t, "location") == location).ToList();
        acc.Compare(name, null, actual: printed, expected: SumRows(acc, rows, "total"), operands: Math.Max(1, rows.Count),
            [totalPath, .. rows.Select(r => r.Path + "/total")],
            (e, a) => $"La suma de los totales por rubro de los bienes {place} ({acc.Money(e)}) no coincide con el total de " +
                      $"bienes {place} informado ({acc.Money(a)}).",
            flagged: [totalPath]);
    }

    // ── Manifestación de bienes ───────────────────────────────────────────────────

    private static void EvaluateManifestacionBienes(Accumulator acc)
    {
        var lines = Rows(acc.Root, "/lines");

        // Lines per section = the section's total.
        foreach (var (section, (totalPath, noun)) in SectionTotals)
        {
            var members = lines.Where(l => acc.Text(l, "section") == section).ToList();
            if (members.Count == 0)
            {
                acc.Skip(MbSectionLinesCheck, section);
                continue;
            }

            acc.Compare(MbSectionLinesCheck, section, actual: acc.Field(totalPath), expected: SumRows(acc, members, "amount"),
                operands: members.Count,
                [totalPath, .. members.Select(m => m.Path + "/amount")],
                (e, a) => $"La suma de las partidas del {noun} ({acc.Money(e)}) no coincide con el {noun} informado ({acc.Money(a)}).",
                flagged: [totalPath],
                subjectParts: new Dictionary<string, string> { ["section"] = section });
        }

        var activoCorriente   = acc.Field("/totals/activoCorriente");
        var activoNoCorriente = acc.Field("/totals/activoNoCorriente");
        var totalActivo       = acc.Field("/totals/totalActivo");
        var pasivoCorriente   = acc.Field("/totals/pasivoCorriente");
        var pasivoNoCorriente = acc.Field("/totals/pasivoNoCorriente");
        var totalPasivo       = acc.Field("/totals/totalPasivo");

        acc.Compare(MbTotalActivoCheck, null, actual: totalActivo, expected: Sum(activoCorriente, activoNoCorriente), operands: 2,
            ["/totals/totalActivo", "/totals/activoCorriente", "/totals/activoNoCorriente"],
            (e, a) => $"El activo total informado ({acc.Money(a)}) no coincide con la suma del activo corriente y el activo " +
                      $"no corriente ({acc.Money(e)}).",
            flagged: ["/totals/totalActivo"]);

        acc.Compare(MbTotalPasivoCheck, null, actual: totalPasivo, expected: Sum(pasivoCorriente, pasivoNoCorriente), operands: 2,
            ["/totals/totalPasivo", "/totals/pasivoCorriente", "/totals/pasivoNoCorriente"],
            (e, a) => $"El pasivo total informado ({acc.Money(a)}) no coincide con la suma del pasivo corriente y el pasivo " +
                      $"no corriente ({acc.Money(e)}).",
            flagged: ["/totals/totalPasivo"]);

        acc.Compare(MbPatrimonioNetoCheck, null, actual: acc.Field("/totals/patrimonioNeto"),
            expected: totalActivo is { } ta && totalPasivo is { } tp ? ta - tp : null, operands: 2,
            ["/totals/patrimonioNeto", "/totals/totalActivo", "/totals/totalPasivo"],
            (e, a) => $"El patrimonio neto informado ({acc.Money(a)}) no coincide con el activo total menos el pasivo total " +
                      $"({acc.Money(e)}).",
            flagged: ["/totals/patrimonioNeto"]);

        // Annex items per reference = the amount of the lines that reference that annex.
        var annexItems = Rows(acc.Root, "/annexItems");
        foreach (var group in annexItems.GroupBy(i => AnnexKey(acc.Text(i, "annexRef"))))
        {
            if (group.Key is null)
                continue;

            var referencing = lines.Where(l => AnnexKey(acc.Text(l, "annexRef")) == group.Key).ToList();
            var annexLabel  = acc.Text(group.First(), "annexRef")!.Trim();
            if (referencing.Count == 0)
            {
                acc.Skip(MbAnnexItemsCheck, annexLabel);
                continue;
            }

            acc.Compare(MbAnnexItemsCheck, annexLabel, actual: SumRows(acc, referencing, "amount"),
                expected: SumRows(acc, group, "marketValue"), operands: group.Count(),
                [.. group.Select(i => i.Path + "/marketValue"), .. referencing.Select(l => l.Path + "/amount")],
                (e, a) => $"La suma de los ítems del anexo {annexLabel} ({acc.Money(e)}) no coincide con el importe de las " +
                          $"partidas que lo referencian ({acc.Money(a)}).",
                flagged: [.. referencing.Select(l => l.Path + "/amount")],
                subjectParts: new Dictionary<string, string> { ["annexRef"] = annexLabel });
        }

        // ingresos − gastos = resultado, when the document has an income statement.
        var ingresos = acc.Field("/incomeStatement/ingresos");
        var gastos   = acc.Field("/incomeStatement/gastos");
        acc.Compare(MbIncomeResultCheck, null, actual: acc.Field("/incomeStatement/resultado"),
            expected: ingresos is { } i && gastos is { } g ? i - g : null, operands: 2,
            ["/incomeStatement/resultado", "/incomeStatement/ingresos", "/incomeStatement/gastos"],
            (e, a) => $"El resultado informado ({acc.Money(a)}) no coincide con los ingresos menos los gastos ({acc.Money(e)}).",
            flagged: ["/incomeStatement/resultado"]);

        OwnershipRange(acc, MbOwnershipPctCheck, annexItems, "ítems de los anexos");
    }

    // ── Shared ────────────────────────────────────────────────────────────────────

    /// <summary>The fiscal year the DDJJ declares (/header/fiscalYear) = the year of the document's period end.</summary>
    private static void FiscalYearPeriod(Accumulator acc, string name, DateOnly? periodEnd)
    {
        var fiscalYear = LegajoJsonValues.Integer(LegajoJsonPointer.Get(acc.Root, "/header/fiscalYear/value"));
        if (fiscalYear is null || periodEnd is null)
        {
            acc.Skip(name);
            return;
        }

        var passed = fiscalYear.Value == periodEnd.Value.Year;
        acc.Add(new LegajoIntegrityCheck
        {
            Name       = name,
            FigureKind = LegajoFigureKinds.Year,
            Passed     = passed,
            Expected   = periodEnd.Value.Year,
            Actual     = fiscalYear.Value,
            Diff       = Math.Abs(fiscalYear.Value - periodEnd.Value.Year),
            FieldPaths = ["/header/fiscalYear"],
            Message    = passed
                ? null
                : $"El período fiscal informado ({fiscalYear.Value.ToString(CultureInfo.InvariantCulture)}) no coincide con " +
                  $"el año de cierre del documento ({periodEnd.Value.Year.ToString(CultureInfo.InvariantCulture)})."
        });
    }

    /// <summary>
    /// The latest month among <paramref name="rows"/>' <c>period</c> = the month of the document's period end.
    /// Skipped when no row has a period or the document has no period end. Flags every row of that month.
    /// </summary>
    private static void LatestPeriodMatchesPeriodEnd(
        Accumulator acc, string name, IReadOnlyList<Row> rows, DateOnly? periodEnd, Func<string, string, string> message)
    {
        var dated = rows.Select(r => (Row: r, Month: acc.Period(r, "period"))).Where(x => x.Month is not null).ToList();
        if (dated.Count == 0 || periodEnd is null)
        {
            acc.Skip(name);
            return;
        }

        var latest = dated.Max(x => x.Month!.Value);
        MonthMatchesPeriodEnd(acc, name, latest, [.. dated.Where(x => x.Month == latest).Select(x => x.Row.Path + "/period")],
                              periodEnd.Value, message);
    }

    /// <summary>
    /// <paramref name="month"/> = the month of <paramref name="periodEnd"/>. Expected and actual are reported as
    /// YYYYMM, the difference in months. <paramref name="message"/> receives both months as MM/AAAA.
    /// </summary>
    private static void MonthMatchesPeriodEnd(
        Accumulator acc, string name, DateOnly month, IReadOnlyList<string> fieldPaths, DateOnly periodEnd,
        Func<string, string, string> message)
    {
        var read     = new DateOnly(month.Year, month.Month, 1);
        var expected = new DateOnly(periodEnd.Year, periodEnd.Month, 1);
        var passed   = read == expected;

        acc.Add(new LegajoIntegrityCheck
        {
            Name       = name,
            FigureKind = LegajoFigureKinds.Month,
            Passed     = passed,
            Expected   = expected.Year * 100 + expected.Month,
            Actual     = read.Year * 100 + read.Month,
            Diff       = Math.Abs((read.Year - expected.Year) * 12 + read.Month - expected.Month),
            FieldPaths = fieldPaths,
            Message    = passed ? null : message(MonthText(read), MonthText(expected))
        });
    }

    /// <summary>A month as es-AR prints it: MM/AAAA.</summary>
    private static string MonthText(DateOnly month) => month.ToString("MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>A percentage as es-AR prints it, without the sign: 21, 10,5.</summary>
    private static string PercentText(decimal pct)
        => pct.ToString("0.###", CultureInfo.InvariantCulture).Replace('.', ',');

    private static void OwnershipRange(Accumulator acc, string name, IReadOnlyList<Row> rows, string noun)
    {
        var withPct = rows.Where(r => acc.Decimal(r, "ownershipPct") is not null).ToList();
        if (withPct.Count == 0)
        {
            acc.Skip(name);
            return;
        }

        var outOfRange = withPct.Where(r => acc.Decimal(r, "ownershipPct") is not (> 0m and <= 100m))
            .Select(r => r.Path + "/ownershipPct")
            .ToList();

        acc.Flag(name, outOfRange.Count == 0, outOfRange,
            $"{outOfRange.Count.ToString(CultureInfo.InvariantCulture)} de los {noun} informa" +
            (outOfRange.Count == 1 ? string.Empty : "n") +
            " un porcentaje de titularidad fuera de rango (debe ser mayor que cero y no superar cien).");
    }

    // "Anexo A", "anexo a" and "A" are the same annex; null or blank is no annex.
    private static string? AnnexKey(string? annexRef)
    {
        if (string.IsNullOrWhiteSpace(annexRef))
            return null;

        var key = annexRef.Trim().ToUpperInvariant();
        return key.StartsWith("ANEXO ", StringComparison.Ordinal) ? key["ANEXO ".Length..].Trim() : key;
    }

    private static List<Row> Rows(JsonNode root, string path)
        => LegajoJsonValues.ArrayAt(root, path)
            .Select((node, index) => new Row(LegajoJsonPointer.Child(path, index), node as JsonObject))
            .Where(r => r.Node is not null)
            .ToList();

    // Null when there are no rows or any row's amount was not extracted: a partial sum proves nothing.
    private static decimal? SumRows(Accumulator acc, IEnumerable<Row> rows, string property)
    {
        var values = rows.Select(r => acc.Decimal(r, property)).ToList();
        return values.Count == 0 || values.Any(v => v is null) ? null : values.Sum(v => v!.Value);
    }

    private static decimal? Sum(decimal? x, decimal? y) => x is { } a && y is { } b ? a + b : null;

    private static string Plural(int count, string singular, string plural)
        => $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? singular : plural)}";

    // ── Nested types ──────────────────────────────────────────────────────────────

    private sealed record Row(string Path, JsonObject? Node);

    private sealed class Accumulator(JsonObject root, string unit, decimal factor)
    {
        public JsonObject Root { get; } = root;
        public string Unit { get; } = unit;
        public List<LegajoIntegrityCheck> Checks { get; } = [];
        public List<LegajoIntegritySkip> Skipped { get; } = [];

        /// <summary>A provenance field's value in pesos.</summary>
        public decimal? Field(string path) => LegajoJsonValues.FieldDecimal(Root, path) * factor;

        /// <summary>A row's plain amount in pesos; a percentage (<see cref="PercentProperties"/>) is not scaled.</summary>
        public decimal? Decimal(Row row, string property)
        {
            var value = LegajoJsonValues.Decimal(row.Node?[property]);
            return PercentProperties.Contains(property) ? value : value * factor;
        }

        public string? Text(Row row, string property) => LegajoJsonValues.String(row.Node?[property]);

        /// <summary>A whole number of a row (a rectificativa number), unscaled.</summary>
        public int? Integer(Row row, string property) => LegajoJsonValues.Integer(row.Node?[property]);

        /// <summary>A row's period (the first day of its month), or null.</summary>
        public DateOnly? Period(Row row, string property) => LegajoJsonValues.Period(row.Node?[property]);

        /// <summary>Any amount node in pesos (a value nested below a row, such as a saldo técnico).</summary>
        public decimal? Amount(JsonNode? node) => LegajoJsonValues.Decimal(node) * factor;

        public void Skip(string name, string? subject = null) => Skipped.Add(new LegajoIntegritySkip(name, subject));

        public void Add(LegajoIntegrityCheck check) => Checks.Add(check);

        /// <summary>
        /// A comparison: skipped when an operand is missing, else within the tolerance. <paramref name="flagged"/>
        /// names the printed total of a total-vs-parts comparison (the only path a failure flags); null for a
        /// comparison of two peers, which flags every field path.
        /// </summary>
        public void Compare(
            string name, string? subject, decimal? actual, decimal? expected, int operands, string[] fieldPaths,
            Func<decimal, decimal, string> message, string[]? flagged = null,
            IReadOnlyDictionary<string, string>? subjectParts = null)
        {
            if (actual is not { } a || expected is not { } e)
            {
                Skip(name, subject);
                return;
            }

            var diff   = Math.Abs(e - a);
            var passed = diff <= Allowance(e, a, operands);

            Checks.Add(new LegajoIntegrityCheck
            {
                Name         = name,
                Subject      = subject,
                SubjectParts = subject is null ? null : subjectParts,
                FigureKind   = LegajoFigureKinds.Money,
                Passed       = passed,
                Expected     = e,
                Actual       = a,
                Diff         = diff,
                FieldPaths   = fieldPaths,
                FlaggedPaths = flagged,
                Message      = passed ? null : message(e, a)
            });
        }

        /// <summary>A rule over many fields (a range, a sign): the offending paths, and one message.</summary>
        public void Flag(string name, bool passed, IReadOnlyList<string> offending, string message)
            => Checks.Add(new LegajoIntegrityCheck
            {
                Name       = name,
                Passed     = passed,
                FieldPaths = offending,
                Message    = passed ? null : message
            });

        private decimal Allowance(decimal expected, decimal actual, int operands)
        {
            var relative = RelativeTolerance * Math.Max(Math.Abs(expected), Math.Abs(actual));
            var rounding = Math.Max(1m, AbsoluteRoundingAllowanceUnits * operands) * factor;
            return Math.Max(relative, rounding);
        }

        // Quotes a figure in the unit the document prints, as BalanceIntegrityService does: whole units,
        // es-AR grouping, built culture-independently. "$1.700.001", "-$1.234", "$12.000, en miles".
        public string Money(decimal pesos)
        {
            var suffix = Unit switch
            {
                V.Miles    => ", en miles",
                V.Millones => ", en millones",
                _          => string.Empty
            };

            var rounded = Math.Round(pesos / factor, MidpointRounding.AwayFromZero);
            var body    = Math.Abs(rounded).ToString("N0", CultureInfo.InvariantCulture).Replace(',', '.');
            return (rounded < 0 ? $"-${body}" : $"${body}") + suffix;
        }
    }
}
