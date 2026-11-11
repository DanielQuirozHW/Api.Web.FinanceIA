// Project : Api.Web.FinanceIA
// Created : 2026-11-09

using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>The cash-flow section the statement printed its financial placements in.</summary>
internal enum CashFlowPlacementSide
{
    Investing,
    Financing,
}

/// <summary>
/// The component rows of the cash-flow composition and the residual each section leaves — the arithmetic the PDF's
/// "Composición de cada sección" prints. ONE place, so <see cref="CashFlowPlacementSection"/> decides a placement's
/// section with exactly the residuals the report would show.
/// </summary>
internal static class CashFlowCompositionRows
{
    internal const string InvestingKey = "investing";
    internal const string FinancingKey = "financing";

    /// <summary>The investing rows, in print order; the placements only when they sit in Investing.</summary>
    internal static List<(string Label, double Value)> Investing(CashFlowComponents? c, bool withPlacements)
    {
        var rows = new List<(string Label, double Value)>();
        Add(rows, "Inversión en bienes de uso", c?.Capex);
        Add(rows, "Ventas de bienes de uso",    c?.AssetDisposals);
        if (withPlacements)
            rows.AddRange(Placements(c));
        return rows;
    }

    /// <summary>The financing rows, in print order; the placements last, only when they sit in Financing.</summary>
    internal static List<(string Label, double Value)> Financing(CashFlowComponents? c, bool withPlacements)
    {
        var rows = new List<(string Label, double Value)>();
        Add(rows, "Deuda de largo plazo tomada",    c?.LongTermDebtRaised);
        Add(rows, "Deuda de largo plazo cancelada", c?.LongTermDebtRepaid);
        Add(rows, "Deuda de corto plazo tomada",    c?.ShortTermDebtRaised);
        Add(rows, "Deuda de corto plazo cancelada", c?.ShortTermDebtRepaid);
        Add(rows, "Dividendos pagados",             c?.DividendsPaid);
        Add(rows, "Honorarios al directorio",       c?.DirectorFeesPaid);
        Add(rows, "Aportes de capital",             c?.CapitalContributions);
        if (withPlacements)
            rows.AddRange(Placements(c));
        return rows;
    }

    /// <summary>The two section-neutral placement rows, each only when it rounds to at least one peso.</summary>
    internal static List<(string Label, double Value)> Placements(CashFlowComponents? c)
    {
        var rows = new List<(string Label, double Value)>();
        Add(rows, "Colocaciones financieras",            c?.FinancialPlacements);
        Add(rows, "Rescate de colocaciones financieras", c?.FinancialPlacementsRedeemed);
        return rows;
    }

    /// <summary>
    /// Subtotal − Σ rows − the RECPAM on cash when the statement attributes it to <paramref name="sectionKey"/>
    /// (its amount is inside that subtotal). Null when the subtotal is not reported.
    /// </summary>
    internal static double? Residual(
        double? subtotal, IEnumerable<(string Label, double Value)> rows, double? recpam, string? recpamSection, string sectionKey)
    {
        if (subtotal is not { } value)
            return null;

        double known = 0;
        if (recpam.HasValue && string.Equals(recpamSection, sectionKey, StringComparison.Ordinal))
            known += recpam.Value;
        foreach ((_, double row) in rows)
            known += row;

        return value - known;
    }

    /// <summary>Whether a residual survives rounding — the test the report uses to print "Resto sin identificar".</summary>
    internal static bool Survives(double residual) => Math.Round(Math.Abs(residual)) >= 1.0;

    private static void Add(List<(string Label, double Value)> rows, string label, double? value)
    {
        if (value is { } v && Math.Abs(Math.Round(v)) >= 1.0)
            rows.Add((label, v));
    }
}

/// <summary>
/// Which section a statement printed its financial placements in. The keys are section-neutral: the model reads the
/// line wherever it is, and a placement printed under Financing, read as Investing, shows up as an unexplained residual
/// in each section, one the mirror of the other. Pure; read by the PDF composition and by the cash-flow reading.
/// <para>
/// Investing — today's reading — unless the placements are non-zero, both subtotals are reported, and putting the
/// placements in Financing leaves BOTH sections without a residual while keeping them in Investing leaves at least one.
/// </para>
/// </summary>
internal static class CashFlowPlacementSection
{
    internal static CashFlowPlacementSide Resolve(CashFlowData cashFlow)
    {
        ArgumentNullException.ThrowIfNull(cashFlow);

        CashFlowComponents? c = cashFlow.Components;
        double placements = (c?.FinancialPlacements ?? 0d) + (c?.FinancialPlacementsRedeemed ?? 0d);
        if (placements == 0d || cashFlow.Investing is null || cashFlow.Financing is null)
            return CashFlowPlacementSide.Investing;

        bool asInvestingLeavesARest =
            Rest(cashFlow, CashFlowCompositionRows.Investing(c, withPlacements: true), CashFlowCompositionRows.InvestingKey, cashFlow.Investing)
            || Rest(cashFlow, CashFlowCompositionRows.Financing(c, withPlacements: false), CashFlowCompositionRows.FinancingKey, cashFlow.Financing);

        bool asFinancingLeavesARest =
            Rest(cashFlow, CashFlowCompositionRows.Investing(c, withPlacements: false), CashFlowCompositionRows.InvestingKey, cashFlow.Investing)
            || Rest(cashFlow, CashFlowCompositionRows.Financing(c, withPlacements: true), CashFlowCompositionRows.FinancingKey, cashFlow.Financing);

        return asInvestingLeavesARest && !asFinancingLeavesARest
            ? CashFlowPlacementSide.Financing
            : CashFlowPlacementSide.Investing;
    }

    private static bool Rest(CashFlowData cashFlow, List<(string Label, double Value)> rows, string sectionKey, double? subtotal) =>
        CashFlowCompositionRows.Residual(subtotal, rows, cashFlow.MonetaryResultOnCash, cashFlow.MonetaryResultOnCashSection, sectionKey)
            is { } residual
        && CashFlowCompositionRows.Survives(residual);
}
