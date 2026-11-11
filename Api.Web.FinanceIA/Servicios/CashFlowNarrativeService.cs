// Project : Api.Web.FinanceIA
// Created : 2026-08-10

using System.Globalization;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios;

// ── Service ───────────────────────────────────────────────────────────────────

/// <summary>
/// Stateless, deterministic implementation of <see cref="ICashFlowNarrativeService"/>.
/// No DI dependencies — can be instantiated directly in tests.
///
/// GOVERNING RULE: state WHAT happened, never WHY. Section labels never describe the decision behind
/// them (a financing section that is 100% RECPAM is an inflation adjustment, not borrowing; an
/// investing residual is NOT assumed to be long-term just because it sits in the investing section).
/// Every sentence rests on the SIGN of a non-null subtotal or on a non-null component — nothing else.
/// Causal connectors ("porque", "para", "debido a", "gracias a", …) never appear in the output.
/// </summary>
public sealed class CashFlowNarrativeService : ICashFlowNarrativeService
{
    // A named line item within a section: its human label and its signed amount as extracted.
    private readonly record struct NamedComponent(string Label, double Value);

    // es-AR money format: thousands separated by ".", no decimals. Applied to the ABSOLUTE value; the
    // sign is rendered explicitly by Magnitude / Signed so wording controls how direction is shown.
    private static readonly NumberFormatInfo PesoFormat = new()
    {
        NumberGroupSeparator = ".",
        NumberGroupSizes     = new[] { 3 },
        NumberDecimalDigits  = 0,
    };

    // A residual (or figure) only counts as non-zero once it rounds to at least one whole peso.
    private const double PesoEpsilon = 1.0;

    /// <inheritdoc />
    public IReadOnlyList<string> Read(CashFlowData cashFlow, CashFlowReconciliationResult reconciliation)
    {
        // ── R0 GATES ──────────────────────────────────────────────────────────
        // Not present → nothing to read.
        if (cashFlow.Present != true)
            return Array.Empty<string>();

        // Either check failed → never narrate figures that don't add up. One caution sentence, alone.
        if (reconciliation.SectionsVsNetChange.State == ReconciliationState.Failed ||
            reconciliation.BeginningPlusChangeVsEnd.State == ReconciliationState.Failed)
        {
            return new[]
            {
                "El estado de flujo de efectivo no reconcilia internamente: las cifras se muestran " +
                "tal como fueron extraídas del balance y no se interpretan.",
            };
        }

        var lines  = new List<string>();
        var comp   = cashFlow.Components;

        // ── R1 OPERATING ──────────────────────────────────────────────────────
        if (cashFlow.Operating is { } operating && !IsZero(operating))
        {
            lines.Add(operating < 0
                ? $"El flujo operativo fue negativo: la operación consumió {Magnitude(operating)}."
                : $"El flujo operativo fue positivo: la operación generó {Magnitude(operating)}.");
        }

        // ── R2 INVESTING (+ R3 term discipline, + R4 residual) ─────────────────
        // R3 term discipline: ONLY capex and asset_disposals may carry a term — bienes de uso are
        // non-current BY DEFINITION. The financial-placement keys explicitly may NOT: a plazo fijo /
        // FCI / títulos placement is not non-current just because it sits in the investing section.
        // Their labels stay term-neutral, and the unitemised remainder is never termed either.
        // AppendSection decides whether to speak (subtotal OR a component).
        //
        // The placement keys are section-neutral: they are narrated in the section the statement printed them in
        // (CashFlowPlacementSection — the same decision the PDF composition makes), last in that section's list.
        var placementComponents = new List<NamedComponent>();
        if (comp?.FinancialPlacements is { } placements)
            placementComponents.Add(new NamedComponent("colocaciones financieras", placements));
        if (comp?.FinancialPlacementsRedeemed is { } placementsRedeemed)
            placementComponents.Add(new NamedComponent("rescate de colocaciones financieras", placementsRedeemed));
        bool placementsInFinancing = CashFlowPlacementSection.Resolve(cashFlow) == CashFlowPlacementSide.Financing;

        var investingComponents = new List<NamedComponent>();
        if (comp?.Capex is { } capex)
            investingComponents.Add(new NamedComponent("inversión en bienes de uso", capex));
        if (comp?.AssetDisposals is { } disposals)
            investingComponents.Add(new NamedComponent("ventas de bienes de uso", disposals));
        if (!placementsInFinancing)
            investingComponents.AddRange(placementComponents);

        AppendSection(
            lines, cashFlow.Investing, investingComponents,
            sectionName: "inversión", pluralNoun: "actividades de inversión",
            singularSubject: "La actividad de inversión");

        // ── R5 FINANCING ──────────────────────────────────────────────────────
        // Only what components prove: debt WITH its term, dividends, capital contributions. Never infer
        // borrowing from a subtotal sign. AppendSection speaks when the subtotal OR a component exists —
        // so a reported dividend is narrated even when the statement omits the financing subtotal.
        var financingComponents = new List<NamedComponent>();
        if (comp?.LongTermDebtRaised is { } v1)
            financingComponents.Add(new NamedComponent("toma de deuda de largo plazo", v1));
        if (comp?.LongTermDebtRepaid is { } v2)
            financingComponents.Add(new NamedComponent("cancelación de deuda de largo plazo", v2));
        if (comp?.ShortTermDebtRaised is { } v3)
            financingComponents.Add(new NamedComponent("toma de deuda de corto plazo", v3));
        if (comp?.ShortTermDebtRepaid is { } v4)
            financingComponents.Add(new NamedComponent("cancelación de deuda de corto plazo", v4));
        if (comp?.DividendsPaid is { } v5)
            financingComponents.Add(new NamedComponent("pago de dividendos", v5));
        if (comp?.DirectorFeesPaid is { } directorFees)
            financingComponents.Add(new NamedComponent("honorarios al directorio", directorFees));
        if (comp?.CapitalContributions is { } v6)
            financingComponents.Add(new NamedComponent("aportes de capital", v6));
        if (placementsInFinancing)
            financingComponents.AddRange(placementComponents);

        AppendSection(
            lines, cashFlow.Financing, financingComponents,
            sectionName: "financiación", pluralNoun: "actividades de financiación",
            singularSubject: "La actividad de financiación");

        // ── R6 STRUCTURAL PAIRING (the MAYTOM case) ────────────────────────────
        // Operating consumed cash while investing contributed it. Add the ONE fact R1 and R2 do not
        // carry — the PROPORTION between the two magnitudes — as pure arithmetic. This must never say or
        // imply that investing funded the operation. Gated exactly as before: operating < 0 AND
        // investing > 0.
        if (cashFlow.Operating is { } op && op < 0 &&
            cashFlow.Investing is { } iv && iv > 0)
        {
            var consumed = Math.Abs(op);
            if (consumed >= PesoEpsilon) // guard the division (op < 0 ⇒ non-zero; asserted here)
            {
                var proportion = (int)Math.Round(iv / consumed * 100.0, MidpointRounding.AwayFromZero);
                lines.Add(
                    $"El aporte de la actividad de inversión equivale al {proportion} % de lo que " +
                    "consumió la operación.");
            }
        }

        // ── R7 INFLATION ──────────────────────────────────────────────────────
        if (cashFlow.MonetaryResultOnCash is { } monetary && !IsZero(monetary) &&
            cashFlow.NetChange is { } netChange)
        {
            var realMovement = netChange - monetary;
            if (!IsZero(realMovement))
            {
                lines.Add(realMovement > 0
                    ? $"Excluyendo el resultado monetario por exposición a la inflación, el efectivo real aumentó {Magnitude(realMovement)}."
                    : $"Excluyendo el resultado monetario por exposición a la inflación, el efectivo real disminuyó {Magnitude(realMovement)}.");
            }

            // Guard the comparison: net_change may be zero (multiplicative form avoids division).
            if (!IsZero(netChange) && Math.Abs(monetary) >= 0.5 * Math.Abs(netChange))
            {
                lines.Add(
                    "La mayor parte de la variación del efectivo corresponde a un efecto de la inflación " +
                    "y no a un movimiento real de fondos.");
            }
        }

        return lines;
    }

    // Emits the sentences for one section (investing or financing), covering every combination of
    // subtotal (present / null / zero) and components. Component amounts and the residual carry their
    // SIGN — direction lives in the figure, never in the reader's inference from the label.
    //   · null subtotal + component(s)      → state the components; say the statement reports no
    //                                          subtotal; NO residual (a residual needs a subtotal).
    //                                          Never invent, infer or sum a subtotal.
    //   · zero subtotal, no non-zero comp    → say nothing.
    //   · zero subtotal + non-zero comp(s)   → net-to-zero; state the (offsetting) components.
    //   · non-zero subtotal, no components   → state it and that this reading identifies none of its concepts.
    //   · non-zero subtotal + component(s)   → base + itemisation (+ residual when it survives rounding).
    private static void AppendSection(
        List<string> lines,
        double? subtotal,
        IReadOnlyList<NamedComponent> allComponents,
        string sectionName,
        string pluralNoun,
        string singularSubject)
    {
        // A zero-valued component is neither an inflow nor an outflow; it carries no narratable
        // information, so drop it up front. (A component that is merely absent was never added.)
        var components = new List<NamedComponent>(allComponents.Count);
        foreach (var c in allComponents)
            if (!IsZero(c.Value))
                components.Add(c);

        // No subtotal reported by the statement.
        if (subtotal is not { } value)
        {
            if (components.Count == 0)
                return; // nothing reported at all → silent

            // Components without a subtotal: state them, and that the statement reports no subtotal.
            // Do NOT invent, infer or sum one; there is no residual to compute.
            lines.Add(
                $"El estado no informa el subtotal de la {sectionName}, pero detalla {ItemList(components)}.");
            return;
        }

        // Subtotal reported as zero.
        if (IsZero(value))
        {
            if (components.Count == 0)
                return; // a genuine, empty zero → nothing to say

            // Zero net with real movements underneath (offsetting flows): state them, say it nets to zero.
            lines.Add(
                $"El resultado neto de la {sectionName} fue nulo; el estado informa {ItemList(components)}.");
            return;
        }

        var baseClause = value < 0
            ? $"En {pluralNoun} se aplicaron {Magnitude(value)}"
            : $"{singularSubject} aportó {Magnitude(value)}";

        // Non-zero subtotal, no components: state it and that this reading identifies none of its
        // concepts. This is a limitation of our own component catalogue, not a fact about the
        // statement (which may itemise the section in lines we have no key for). Never infer concepts.
        if (components.Count == 0)
        {
            lines.Add(
                $"{singularSubject} arrojó un resultado neto de {Signed(value)}; esta lectura no " +
                "identifica ninguno de sus conceptos.");
            return;
        }

        // R2: "íntegramente" only when a single named component equals the subtotal within one peso.
        if (components.Count == 1 && Math.Abs(components[0].Value - value) <= PesoEpsilon)
        {
            lines.Add($"{baseClause}, íntegramente en concepto de {components[0].Label}.");
            return;
        }

        lines.Add(baseClause + ".");
        lines.Add($"El detalle informado incluye {ItemList(components)}.");

        // R4: residual = subtotal − Σ named components. Report it (signed) only when it rounds to ≥ 1 peso.
        // The residual is BY DEFINITION what no identified component accounts for — it never claims the
        // statement failed to detail the amount (it may well be itemised in a line we have no key for).
        var residual = value;
        foreach (var c in components)
            residual -= c.Value;

        if (Math.Abs(Math.Round(residual)) >= PesoEpsilon)
            lines.Add($"El resto, {Signed(residual)}, corresponde a conceptos que esta lectura no identifica por separado.");
    }

    // Signed, comma/"y"-joined component clauses: "$ 200.000.000 en concepto de X y -$ 50.000.000 …".
    private static string ItemList(IReadOnlyList<NamedComponent> components)
    {
        var clauses = new List<string>(components.Count);
        foreach (var c in components)
            clauses.Add($"{Signed(c.Value)} en concepto de {c.Label}");
        return JoinEs(clauses);
    }

    // "$ 971.123.240" — magnitude only; direction is carried by the surrounding wording.
    private static string Magnitude(double value) =>
        $"$ {Math.Abs(Math.Round(value, MidpointRounding.AwayFromZero)).ToString("N0", PesoFormat)}";

    // "-$ 971.123.240" / "$ 971.123.240" — signed, for stating a raw subtotal figure directly.
    private static string Signed(double value)
    {
        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        var body    = Math.Abs(rounded).ToString("N0", PesoFormat);
        return rounded < 0 ? $"-$ {body}" : $"$ {body}";
    }

    // es-AR list join: "A", "A y B", "A, B y C".
    private static string JoinEs(IReadOnlyList<string> items) => items.Count switch
    {
        0 => string.Empty,
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " y " + items[^1],
    };

    // A figure counts as zero once it rounds below a whole peso (extracted figures are whole pesos).
    private static bool IsZero(double value) => Math.Abs(value) < PesoEpsilon;
}
