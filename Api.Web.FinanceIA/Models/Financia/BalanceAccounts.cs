// Project : Api.Web.FinanceIA
// Created : 2026-06-10

using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Models.Financia;

// ── Input model ──────────────────────────────────────────────────────────────

/// <summary>
/// Raw balance-sheet and income-statement figures required to compute financial
/// ratios deterministically. All properties are nullable because any line may
/// be absent in a partially extracted balance.
/// </summary>
public sealed record BalanceAccounts
{
    // ── Balance sheet — assets ────────────────────────────────────────────────
    [JsonPropertyName("current_assets")]
    public double? CurrentAssets      { get; init; }

    [JsonPropertyName("non_current_assets")]
    public double? NonCurrentAssets   { get; init; }

    [JsonPropertyName("assets")]
    public double? TotalAssets        { get; init; }

    // ── Balance sheet — liabilities ───────────────────────────────────────────
    [JsonPropertyName("current_liabilities")]
    public double? CurrentLiabilities    { get; init; }

    [JsonPropertyName("non_current_liabilities")]
    public double? NonCurrentLiabilities { get; init; }

    [JsonPropertyName("liabilities")]
    public double? TotalLiabilities      { get; init; }

    // ── Balance sheet — equity ────────────────────────────────────────────────
    [JsonPropertyName("equity")]
    public double? Equity { get; init; }

    // ── Income statement ──────────────────────────────────────────────────────
    [JsonPropertyName("net_revenue")]
    public double? NetRevenue       { get; init; }

    [JsonPropertyName("net_profit")]
    public double? NetProfit        { get; init; }

    [JsonPropertyName("operating_result")]
    public double? OperatingResult  { get; init; }

    [JsonPropertyName("financial_expenses")]
    public double? FinancialExpenses { get; init; }

    // ── Working-capital detail ────────────────────────────────────────────────
    [JsonPropertyName("inventory")]
    public double? Inventory         { get; init; }

    [JsonPropertyName("prepaid_expenses")]
    public double? PrepaidExpenses   { get; init; }

    // ── Scale ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Factor the extraction already applied to every persisted figure to bring it from the
    /// document's printed unit ("expresado en miles" → 1000) to pesos. Read from the top-level
    /// <c>amounts_scale</c> key of BalanceJson (prompt 2.18+); null for older analyses, whose
    /// figures were persisted as the model returned them. Omitted from output when null, so reusing
    /// this type as the <c>prior_year</c> shape adds no key there.
    /// </summary>
    [JsonPropertyName("amounts_scale")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? AmountsScale { get; init; }

    // ── Public methods ────────────────────────────────────────────────────────

    /// <summary>Returns a copy with every figure multiplied by <paramref name="factor"/>.</summary>
    public BalanceAccounts Scale(double factor) => factor == 1d ? this : Map(v => v * factor);

    /// <summary>
    /// Returns the figures back in the document's PRINTED unit — for reconciling narrative text
    /// (executive summary, alerts, anomalies), which quotes amounts as printed. A balance
    /// "expresado en miles" persists pesos, so comparing its prose ("$ 33.190.632") against the
    /// persisted denominator would be off by exactly the scale. Unchanged when no scale >1 was
    /// applied, so analyses predating prompt 2.18 read exactly as before. Ratios and rules keep
    /// using the persisted pesos: a ratio is scale-invariant, and pesos are the true figures.
    /// </summary>
    public BalanceAccounts InPrintedUnits() =>
        AmountsScale is { } scale && scale > 1d
            ? Map(v => v / scale) with { AmountsScale = null }
            : this;

    // ── Private methods ───────────────────────────────────────────────────────

    private BalanceAccounts Map(Func<double, double> f) => this with
    {
        CurrentAssets         = CurrentAssets         is { } ca  ? f(ca)  : null,
        NonCurrentAssets      = NonCurrentAssets      is { } nca ? f(nca) : null,
        TotalAssets           = TotalAssets           is { } ta  ? f(ta)  : null,
        CurrentLiabilities    = CurrentLiabilities    is { } cl  ? f(cl)  : null,
        NonCurrentLiabilities = NonCurrentLiabilities is { } ncl ? f(ncl) : null,
        TotalLiabilities      = TotalLiabilities      is { } tl  ? f(tl)  : null,
        Equity                = Equity                is { } eq  ? f(eq)  : null,
        NetRevenue            = NetRevenue            is { } nr  ? f(nr)  : null,
        NetProfit             = NetProfit             is { } np  ? f(np)  : null,
        OperatingResult       = OperatingResult       is { } op  ? f(op)  : null,
        FinancialExpenses     = FinancialExpenses     is { } fe  ? f(fe)  : null,
        Inventory             = Inventory             is { } inv ? f(inv) : null,
        PrepaidExpenses       = PrepaidExpenses       is { } pe  ? f(pe)  : null,
    };
}
