// Project : Api.Web.FinanceIA
// Created : 2026-09-24

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Every monetary figure one extraction pass read from a balance document, in a single unit —
/// the input of <see cref="Api.Web.FinanceIA.Servicios.IBalanceIntegrityService"/>. Only
/// <see cref="Current"/> is always present; every other member is optional because older model
/// output (prompt &lt; 2.18) and partial documents do not carry it.
/// </summary>
public sealed record ExtractedBalance
{
    /// <summary>Current-year figures (the fiscal year being analysed).</summary>
    public required BalanceAccounts Current { get; init; }

    /// <summary>Comparative prior-year column, when the document has one.</summary>
    public BalanceAccounts? PriorYear { get; init; }

    public IReadOnlyList<BalanceLineItem>? CurrentAssetsItems { get; init; }

    public IReadOnlyList<BalanceLineItem>? CurrentLiabilitiesItems { get; init; }

    public IReadOnlyList<BalanceLineItem>? NonCurrentLiabilitiesItems { get; init; }

    /// <summary>Resultado antes del impuesto a las ganancias, signed.</summary>
    public double? ResultBeforeTax { get; init; }

    /// <summary>Impuesto a las ganancias, signed as printed (negative when it is a charge).</summary>
    public double? IncomeTax { get; init; }

    /// <summary>
    /// Product of every <see cref="Scale"/> factor applied so far: 1 while the figures are still in
    /// the printed unit, 1000 once a balance "expresado en miles" has been brought to pesos. Lets a
    /// consumer quote a figure back in the unit the analyst sees on the document.
    /// </summary>
    public double AppliedScale { get; init; } = 1d;

    /// <summary>Returns a copy with every figure — rubro lines included — multiplied by <paramref name="factor"/>.</summary>
    public ExtractedBalance Scale(double factor) => factor == 1d ? this : this with
    {
        AppliedScale               = AppliedScale * factor,
        Current                    = Current.Scale(factor),
        PriorYear                  = PriorYear?.Scale(factor),
        CurrentAssetsItems         = ScaleItems(CurrentAssetsItems, factor),
        CurrentLiabilitiesItems    = ScaleItems(CurrentLiabilitiesItems, factor),
        NonCurrentLiabilitiesItems = ScaleItems(NonCurrentLiabilitiesItems, factor),
        ResultBeforeTax            = ResultBeforeTax * factor,
        IncomeTax                  = IncomeTax * factor,
    };

    private static IReadOnlyList<BalanceLineItem>? ScaleItems(IReadOnlyList<BalanceLineItem>? items, double factor) =>
        items?.Select(i => i with { Amount = i.Amount * factor }).ToList();
}
