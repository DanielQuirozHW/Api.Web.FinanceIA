// Project : Api.Web.FinanceIA
// Created : 2026-10-01

using System.Globalization;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Servicios.Legajo;
using V = Api.Web.FinanceIA.Servicios.Legajo.LegajoExtractionVocabulary;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// The Phase 3a checks over the three tax returns: Ganancias, DDJJ IVA and DDJJ Ingresos Brutos. Same
/// conventions as the Phase 2b checks; tax-rule nuances (quebrantos, topes, prorrateos, exenciones,
/// impuestos mínimos) are deliberately NOT checked — only the arithmetic the form itself prints.
/// </summary>
public sealed partial class LegajoIntegrityService
{
    // ── Check names (persisted in ChecksJson) ─────────────────────────────────────
    public const string GanJustificationOrigenesCheck     = "gan_justification_origenes";
    public const string GanJustificationAplicacionesCheck = "gan_justification_aplicaciones";
    public const string GanPatrimonioVariacionCheck       = "gan_patrimonio_variacion";
    public const string GanFiscalYearPeriodCheck          = "gan_fiscal_year_period";

    public const string IvaAlicuotaLineCheck              = "iva_alicuota_line";
    public const string IvaDebitoLinesCheck               = "iva_debito_lines";
    public const string IvaSaldoTecnicoCheck              = "iva_saldo_tecnico";
    public const string IvaPeriodCheck                    = "iva_period";

    public const string IibbRowTaxCheck                   = "iibb_row_tax";
    public const string IibbTotalTaxCheck                 = "iibb_total_tax";
    public const string IibbPeriodCheck                   = "iibb_period";

    // ── Ganancias ─────────────────────────────────────────────────────────────────

    // No category-sum check: the ganancia neta is not the plain sum of the category results (cedular income,
    // fuente extranjera, quebrantos usable only against the same income, deducciones generales). No
    // origenes = aplicaciones check: on the AFIP justification the two columns are not meant to balance.
    private static void EvaluateGanancias(Accumulator acc, DateOnly? periodEnd)
    {
        var lines = Rows(acc.Root, "/justification/lines");

        JustificationSide(acc, lines, V.Origen, "/justification/totalOrigenes", GanJustificationOrigenesCheck, "orígenes");
        JustificationSide(acc, lines, V.Aplicacion, "/justification/totalAplicaciones", GanJustificationAplicacionesCheck,
                          "aplicaciones");

        // patrimonio al cierre − patrimonio al inicio = the variación printed, when it is printed.
        var inicio = acc.Field("/justification/patrimonioInicio");
        var cierre = acc.Field("/justification/patrimonioCierre");
        acc.Compare(GanPatrimonioVariacionCheck, null, actual: acc.Field("/justification/variacionPatrimonial"),
            expected: cierre is { } c && inicio is { } i ? c - i : null, operands: 2,
            ["/justification/variacionPatrimonial", "/justification/patrimonioCierre", "/justification/patrimonioInicio"],
            (e, a) => $"La variación patrimonial informada ({acc.Money(a)}) no coincide con el patrimonio al cierre menos el " +
                      $"patrimonio al inicio ({acc.Money(e)}).",
            flagged: ["/justification/variacionPatrimonial"]);

        FiscalYearPeriod(acc, GanFiscalYearPeriodCheck, periodEnd);
    }

    /// <summary>The lines printed on one side of the justification = that side's printed total.</summary>
    private static void JustificationSide(
        Accumulator acc, IReadOnlyList<Row> lines, string side, string totalPath, string name, string noun)
    {
        var members = lines.Where(l => acc.Text(l, "side") == side).ToList();
        if (members.Count == 0)
        {
            acc.Skip(name);
            return;
        }

        acc.Compare(name, null, actual: acc.Field(totalPath), expected: SumRows(acc, members, "amount"), operands: members.Count,
            [totalPath, .. members.Select(m => m.Path + "/amount")],
            (e, a) => $"La suma de las líneas de {noun} de la justificación patrimonial ({acc.Money(e)}) no coincide con el " +
                      $"total de {noun} informado ({acc.Money(a)}).",
            flagged: [totalPath]);
    }

    // ── DDJJ IVA ──────────────────────────────────────────────────────────────────

    // Only the débito side is checked line by line: the crédito fiscal computable legitimately departs from
    // neto × alícuota (prorrateo, crédito no computable), so a crédito check would fire on correct documents.
    private static void EvaluateDdjjIva(Accumulator acc, DateOnly? periodEnd)
    {
        var periods     = Rows(acc.Root, "/periods");
        var debitLines  = Rows(acc.Root, "/alicuotaLines").Where(l => acc.Text(l, "side") == V.Debito).ToList();

        // Each débito line with an alícuota: impuesto = neto gravado × alícuota. Two peers: both flagged.
        var rated = debitLines.Where(l => acc.Decimal(l, "alicuotaPct") is not null).ToList();
        if (rated.Count == 0)
            acc.Skip(IvaAlicuotaLineCheck);

        foreach (var line in rated)
        {
            var pct     = acc.Decimal(line, "alicuotaPct")!.Value;
            var neto    = acc.Decimal(line, "netoGravado");
            var ddjj    = Ddjj(acc, line, annual: false);
            var subject = $"{ddjj.Subject}/{pct.ToString("0.###", CultureInfo.InvariantCulture)}";

            acc.Compare(IvaAlicuotaLineCheck, subject, actual: acc.Decimal(line, "impuesto"), expected: neto * pct / 100m,
                operands: 1,
                [line.Path + "/impuesto", line.Path + "/netoGravado", line.Path + "/alicuotaPct"],
                (e, a) => $"El IVA de la línea de débito con alícuota {PercentText(pct)} {ddjj.Of} ({acc.Money(a)}) no " +
                          $"coincide con el neto gravado por la alícuota ({acc.Money(e)}).",
                subjectParts: ddjj.PartsWith("alicuotaPct", pct.ToString("0.###", CultureInfo.InvariantCulture)));
        }

        if (periods.Count == 0)
        {
            acc.Skip(IvaDebitoLinesCheck);
            acc.Skip(IvaSaldoTecnicoCheck);
        }

        foreach (var period in periods)
        {
            var ddjj    = Ddjj(acc, period, annual: false);
            var subject = ddjj.Subject;

            // The débito lines of this DDJJ = its débito fiscal.
            var members = debitLines.Where(l => SameDdjj(acc, l, period)).ToList();
            if (members.Count == 0)
            {
                acc.Skip(IvaDebitoLinesCheck, subject);
            }
            else
            {
                acc.Compare(IvaDebitoLinesCheck, subject, actual: acc.Decimal(period, "debitoFiscal"),
                    expected: SumRows(acc, members, "impuesto"), operands: members.Count,
                    [period.Path + "/debitoFiscal", .. members.Select(m => m.Path + "/impuesto")],
                    (e, a) => $"La suma de las líneas de débito {ddjj.Of} ({acc.Money(e)}) no coincide con el débito fiscal " +
                              $"informado ({acc.Money(a)}).",
                    flagged: [period.Path + "/debitoFiscal"],
                    subjectParts: ddjj.Parts);
            }

            // débito − crédito − saldo técnico a favor del período anterior = the saldo técnico, signed by its
            // side (a favor del fisco positive).
            var debito   = acc.Decimal(period, "debitoFiscal");
            var credito  = acc.Decimal(period, "creditoFiscal");
            var anterior = acc.Decimal(period, "saldoTecnicoAnterior");
            var saldo    = period.Node?["saldoTecnico"] as JsonObject;
            var amount   = acc.Amount(saldo?["amount"]);
            var side     = LegajoJsonValues.String(saldo?["side"]);
            decimal? signed = amount is { } m && side is not null ? (side == V.Fisco ? m : -m) : null;

            acc.Compare(IvaSaldoTecnicoCheck, subject, actual: signed,
                expected: debito is { } d && credito is { } c && anterior is { } p ? d - c - p : null, operands: 3,
                [period.Path + "/saldoTecnico/amount", period.Path + "/saldoTecnico/side", period.Path + "/debitoFiscal",
                 period.Path + "/creditoFiscal", period.Path + "/saldoTecnicoAnterior"],
                (e, a) => $"El saldo técnico informado para {ddjj.The} ({SaldoText(acc, a)}) no coincide con el " +
                          $"débito fiscal menos el crédito fiscal y el saldo técnico a favor del período anterior " +
                          $"({SaldoText(acc, e)}).",
                flagged: [period.Path + "/saldoTecnico/amount", period.Path + "/saldoTecnico/side"],
                subjectParts: ddjj.Parts);
        }

        LatestPeriodMatchesPeriodEnd(acc, IvaPeriodCheck, periods, periodEnd,
            (read, expected) => $"El último período de IVA informado ({read}) no coincide con el mes de cierre del " +
                                $"documento ({expected}).");
    }

    // A signed saldo técnico in words: "$100.000 a favor del fisco", "$42.000 a favor del contribuyente".
    private static string SaldoText(Accumulator acc, decimal signed)
        => signed >= 0m ? $"{acc.Money(signed)} a favor del fisco" : $"{acc.Money(-signed)} a favor del contribuyente";

    // ── DDJJ Ingresos Brutos ──────────────────────────────────────────────────────

    private static void EvaluateDdjjIibb(Accumulator acc, DateOnly? periodEnd)
    {
        var periods = Rows(acc.Root, "/periods");
        var rows    = Rows(acc.Root, "/rows");

        // Each determinación line: impuesto determinado = base imponible × alícuota. Two peers: both flagged.
        // A jurisdiction's impuesto mínimo can exceed it on a correct DDJJ — the one known false alarm.
        if (rows.Count == 0)
            acc.Skip(IibbRowTaxCheck);

        foreach (var row in rows)
        {
            var pct     = acc.Decimal(row, "alicuotaPct");
            var ddjj    = Ddjj(acc, row, annual: true);
            var subject = $"{ddjj.Subject}/{acc.Text(row, "jurisdiction") ?? "-"}";

            acc.Compare(IibbRowTaxCheck, subject, actual: acc.Decimal(row, "impuestoDeterminado"),
                expected: acc.Decimal(row, "baseImponible") * pct / 100m, operands: 1,
                [row.Path + "/impuestoDeterminado", row.Path + "/baseImponible", row.Path + "/alicuotaPct"],
                (e, a) => $"El impuesto determinado de {acc.Text(row, "jurisdiction") ?? "la jurisdicción"} en {ddjj.The} " +
                          $"({acc.Money(a)}) no coincide con la base imponible por la alícuota {PercentText(pct ?? 0m)} " +
                          $"({acc.Money(e)}).",
                subjectParts: ddjj.PartsWith("jurisdiction", acc.Text(row, "jurisdiction") ?? "-"));
        }

        // The rows of each DDJJ = its impuesto total.
        if (periods.Count == 0)
            acc.Skip(IibbTotalTaxCheck);

        foreach (var period in periods)
        {
            var ddjj    = Ddjj(acc, period, annual: true);
            var subject = ddjj.Subject;
            var members = rows.Where(r => SameDdjj(acc, r, period)).ToList();
            if (members.Count == 0)
            {
                acc.Skip(IibbTotalTaxCheck, subject);
                continue;
            }

            acc.Compare(IibbTotalTaxCheck, subject, actual: acc.Decimal(period, "impuestoTotal"),
                expected: SumRows(acc, members, "impuestoDeterminado"), operands: members.Count,
                [period.Path + "/impuestoTotal", .. members.Select(m => m.Path + "/impuestoDeterminado")],
                (e, a) => $"La suma del impuesto determinado por jurisdicción {ddjj.Of} ({acc.Money(e)}) no coincide con " +
                          $"el impuesto total informado ({acc.Money(a)}).",
                flagged: [period.Path + "/impuestoTotal"],
                subjectParts: ddjj.Parts);
        }

        // The latest monthly DDJJ = the document's period end; an annual DDJJ: its fiscal year.
        var monthly = periods.Any(p => acc.Period(p, "period") is not null) ? periods : rows;
        if (monthly.Any(r => acc.Period(r, "period") is not null))
        {
            LatestPeriodMatchesPeriodEnd(acc, IibbPeriodCheck, monthly, periodEnd,
                (read, expected) => $"El último período de Ingresos Brutos informado ({read}) no coincide con el mes de " +
                                    $"cierre del documento ({expected}).");
        }
        else
        {
            FiscalYearPeriod(acc, IibbPeriodCheck, periodEnd);
        }
    }

    // ── Shared by the DDJJ with several periods ───────────────────────────────────

    /// <summary>Whether <paramref name="line"/> belongs to the DDJJ <paramref name="ddjj"/>: same period and rectificativa.</summary>
    private static bool SameDdjj(Accumulator acc, Row line, Row ddjj)
        => acc.Period(line, "period") == acc.Period(ddjj, "period")
           && acc.Integer(line, "rectificativaNumber") == acc.Integer(ddjj, "rectificativaNumber");

    /// <summary>
    /// How a DDJJ (a row's period + rectificativa) is named. Subject: "2025-11", "2025-11/R1", "anual". The: "el
    /// período 11/2025", "el período 11/2025 (rectificativa 1)", "la DDJJ anual". Of: the same after "de", with
    /// the article contracted ("del período 11/2025"). A row without a period is an annual DDJJ for Ingresos
    /// Brutos, and an anomaly for IVA ("sin-periodo").
    /// </summary>
    private static DdjjNames Ddjj(Accumulator acc, Row row, bool annual)
    {
        var number = acc.Integer(row, "rectificativaNumber");
        var rectificativa = number is { } n
            ? (Subject: $"/R{n.ToString(CultureInfo.InvariantCulture)}", Text: $" (rectificativa {n.ToString(CultureInfo.InvariantCulture)})")
            : (Subject: string.Empty, Text: string.Empty);

        if (acc.Period(row, "period") is { } period)
        {
            var month = MonthText(period);
            var key   = period.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            return new DdjjNames(key + rectificativa.Subject,
                                 $"el período {month}{rectificativa.Text}", $"del período {month}{rectificativa.Text}",
                                 DdjjParts(key, number));
        }

        return annual
            ? new DdjjNames("anual" + rectificativa.Subject, $"la DDJJ anual{rectificativa.Text}", $"de la DDJJ anual{rectificativa.Text}",
                            DdjjParts("anual", number))
            : new DdjjNames("sin-periodo" + rectificativa.Subject, $"la DDJJ sin período{rectificativa.Text}",
                            $"de la DDJJ sin período{rectificativa.Text}", DdjjParts("sin-periodo", number));
    }

    /// <summary>The structured subject of a DDJJ: its period (YYYY-MM, anual, sin-periodo) and, for a rectificativa, its number.</summary>
    private static Dictionary<string, string> DdjjParts(string period, int? rectificativa)
    {
        var parts = new Dictionary<string, string> { ["period"] = period };
        if (rectificativa is { } n)
            parts["rectificativa"] = n.ToString(CultureInfo.InvariantCulture);

        return parts;
    }

    private readonly record struct DdjjNames(string Subject, string The, string Of, IReadOnlyDictionary<string, string> Parts)
    {
        /// <summary><see cref="Parts"/> plus one more part (the alícuota, the jurisdiction).</summary>
        public IReadOnlyDictionary<string, string> PartsWith(string key, string value)
            => new Dictionary<string, string>(Parts) { [key] = value };
    }
}
