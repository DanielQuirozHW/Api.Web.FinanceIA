// Project : Api.Web.FinanceIA
// Created : 2026-09-24

using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// One deterministic check run over the extracted balance figures, persisted inside
/// <c>BalanceJson.integrity.checks</c>. Amounts are in pesos.
/// </summary>
public sealed record BalanceIntegrityCheck
{
    /// <summary>Stable check identifier, e.g. <c>current_liabilities_items</c>.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("passed")]
    public required bool Passed { get; init; }

    /// <summary>The figure the other lines imply (null for a sign check).</summary>
    [JsonPropertyName("expected")]
    public double? Expected { get; init; }

    /// <summary>The figure the extraction reported.</summary>
    [JsonPropertyName("actual")]
    public double? Actual { get; init; }

    /// <summary>|expected − actual| as a percentage of the larger magnitude (null for a sign check).</summary>
    [JsonPropertyName("diffPct")]
    public double? DiffPct { get; init; }

    /// <summary>BalanceJson keys of the lines this check involves.</summary>
    [JsonPropertyName("lines")]
    public IReadOnlyList<string> Lines { get; init; } = [];

    /// <summary>For <c>column_swap</c> only: the prior-year key the current figure matched.</summary>
    [JsonPropertyName("comparedWith")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ComparedWith { get; init; }

    /// <summary>
    /// For <c>assets_components</c> / <c>liabilities_components</c> only: the component the statement
    /// does not print (null, no rubros, the other component alone equals the total) that this check
    /// evaluated as zero. The assumption lives here only — the extracted figure itself stays null.
    /// Omitted when nothing was assumed.
    /// </summary>
    [JsonPropertyName("inferredZero")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? InferredZero { get; init; }

    /// <summary>
    /// For <c>assets_components</c> / <c>liabilities_components</c> only: the component whose subtotal was not
    /// read (null) but whose rubros were, every amount read and summing to zero or more, so this check evaluated
    /// it as the sum of those rubros — zero included (a section printed with zero lines). The rubros must then
    /// explain the total: one that contradicts it fails the check. The assumption lives here only — the
    /// extracted figures stay as read. Omitted when nothing was assumed, and on every blob written before it
    /// existed (that check was skipped then).
    /// </summary>
    [JsonPropertyName("inferredFromItems")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? InferredFromItems { get; init; }

    /// <summary>
    /// For a FAILED <c>net_profit_reconciliation</c> only: true when the reported net profit is (within the
    /// check's tolerance) the NEGATIVE of what result before tax and income tax imply — a sign lost on reading
    /// (parentheses), a real misread, never a presentation of the income statement. Omitted otherwise.
    /// </summary>
    [JsonPropertyName("signFlip")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SignFlip { get; init; }

    /// <summary>
    /// For a FAILED <c>net_profit_reconciliation</c> only: true when result before tax and income tax, as read,
    /// reconcile (within the column-swap tolerance) to the PRIOR year's net profit — both taken from the
    /// comparative column, a real misread, never a presentation of the income statement; false when that was
    /// checked and they do not. Omitted when it could not be checked (no comparative net profit, or a zero one)
    /// — and on every blob written before it was recorded, which a reader must take as "unknown".
    /// </summary>
    [JsonPropertyName("columnMix")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ColumnMix { get; init; }
}
