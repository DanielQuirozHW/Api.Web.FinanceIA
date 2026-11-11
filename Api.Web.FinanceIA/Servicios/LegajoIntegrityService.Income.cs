// Project : Api.Web.FinanceIA
// Created : 2026-10-01

using Api.Web.FinanceIA.Servicios.Legajo;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// The Phase 3a checks over the income documents: certificación de ingresos and monotributo. Only what is
/// safe: the certified amounts add up to their total, nothing is negative, the period is the document's.
/// Whether a monotributo category fits the income is scoring (Phase 4), not a check.
/// </summary>
public sealed partial class LegajoIntegrityService
{
    // ── Check names (persisted in ChecksJson) ─────────────────────────────────────
    public const string CiRowsTotalCheck    = "ci_rows_total";
    public const string CiPeriodCheck       = "ci_period";

    public const string MonoNonNegativeCheck = "mono_non_negative";
    public const string MonoPeriodCheck      = "mono_period";

    // ── Certificación de ingresos ─────────────────────────────────────────────────

    private static void EvaluateCertificacionIngresos(Accumulator acc, DateOnly? periodEnd)
    {
        // The certified amounts = the total certified.
        var rows = Rows(acc.Root, "/rows");
        if (rows.Count == 0)
        {
            acc.Skip(CiRowsTotalCheck);
        }
        else
        {
            acc.Compare(CiRowsTotalCheck, null, actual: acc.Field("/totals/totalIngresos"),
                expected: SumRows(acc, rows, "amount"), operands: rows.Count,
                ["/totals/totalIngresos", .. rows.Select(r => r.Path + "/amount")],
                (e, a) => $"La suma de los ingresos certificados ({acc.Money(e)}) no coincide con el total de ingresos " +
                          $"informado ({acc.Money(a)}).",
                flagged: ["/totals/totalIngresos"]);
        }

        // The end of the certified period = the month of the document's period end.
        var periodTo = LegajoJsonValues.Date(LegajoJsonPointer.Get(acc.Root, "/header/periodTo/value"));
        if (periodTo is null || periodEnd is null)
        {
            acc.Skip(CiPeriodCheck);
            return;
        }

        MonthMatchesPeriodEnd(acc, CiPeriodCheck, periodTo.Value, ["/header/periodTo"], periodEnd.Value,
            (read, expected) => $"El fin del período certificado ({read}) no coincide con el mes de cierre del documento " +
                                $"({expected}).");
    }

    // ── Monotributo ───────────────────────────────────────────────────────────────

    private static void EvaluateMonotributo(Accumulator acc, DateOnly? periodEnd)
    {
        var payments = Rows(acc.Root, "/payments");

        // No negative cuota or payment.
        var amounts = payments.Select(p => (Path: p.Path + "/amount", Value: acc.Decimal(p, "amount")))
            .Prepend((Path: "/header/cuotaMensual", Value: acc.Field("/header/cuotaMensual")))
            .Where(x => x.Value is not null)
            .ToList();

        if (amounts.Count == 0)
        {
            acc.Skip(MonoNonNegativeCheck);
        }
        else
        {
            var negative = amounts.Where(x => x.Value < 0m).Select(x => x.Path).ToList();
            acc.Flag(MonoNonNegativeCheck, negative.Count == 0, negative,
                Plural(negative.Count, "importe informado es negativo", "importes informados son negativos") +
                "; la cuota y los pagos del monotributo no pueden ser negativos.");
        }

        // The latest payment = the month of the document's period end.
        LatestPeriodMatchesPeriodEnd(acc, MonoPeriodCheck, payments, periodEnd,
            (read, expected) => $"El último pago informado corresponde a {read} y no coincide con el mes de cierre del " +
                                $"documento ({expected}).");
    }
}
