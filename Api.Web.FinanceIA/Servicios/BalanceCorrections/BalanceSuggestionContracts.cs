// Project : Api.Web.FinanceIA
// Created : 2026-11-03

using System.Text.Json.Serialization;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.BalanceCorrections;

/// <summary>
/// What the correction suggestion asks the model: the figures of the FAILED targeted integrity checks (the
/// structure checks and net_profit_reconciliation), each with the label it is usually printed under and its
/// stored value IN PRINTED UNITS (pesos ÷ amounts_scale), so the model compares like with like. Numbers and fixed label hints only — no stored label, no entity
/// name, no CUIT, no reason: nothing that identifies anyone goes into the prompt.
/// </summary>
/// <param name="FiscalYear">Analysis.FiscalYear (the closing year of the current column), when known.</param>
/// <param name="Fields">The top-level figures asked about.</param>
/// <param name="ItemGroups">The rubro lists of the failed <c>*_items</c> checks.</param>
/// <param name="AsksNetProfit">
/// True when net_profit_reconciliation failed: the question then asks net_profit, income_tax and
/// result_before_tax, and tells the model that result_before_tax may have no printed line.
/// </param>
/// <param name="NetProfitColumnMix">
/// True when the STORED net_profit_reconciliation failure recorded a column mix (<c>checks[].columnMix</c>): the
/// stored result_before_tax and income_tax are the comparative column's, and the question says so.
/// </param>
public sealed record BalanceSuggestionQuestion(
    int? FiscalYear,
    IReadOnlyList<BalanceSuggestionField> Fields,
    IReadOnlyList<BalanceSuggestionItemGroup> ItemGroups,
    bool AsksNetProfit = false,
    bool NetProfitColumnMix = false);

/// <summary>One top-level figure asked about.</summary>
/// <param name="Path">The BalanceCorrectionPath, e.g. <c>current_liabilities</c>.</param>
/// <param name="LabelHint">The Spanish label it is usually printed under, e.g. "Total Pasivo Corriente".</param>
/// <param name="StoredPrinted">The stored value in printed units; null when it was not extracted.</param>
public sealed record BalanceSuggestionField(string Path, string LabelHint, double? StoredPrinted);

/// <summary>One rubro list asked about: every stored line's amount, in stored order, in printed units.</summary>
/// <param name="Array">The BalanceJson array, e.g. <c>current_liabilities_items</c>.</param>
/// <param name="LabelHint">The Spanish name of the rubro, e.g. "rubros del Pasivo Corriente".</param>
/// <param name="StoredPrinted">Each stored line's amount (null when not extracted), index = stored index.</param>
public sealed record BalanceSuggestionItemGroup(string Array, string LabelHint, IReadOnlyList<double?> StoredPrinted);

/// <summary>
/// The model's answer, as JSON in its first text block. Amounts are AS PRINTED (the document's unit).
/// Every member is optional: a missing one is "not found", never a parse failure.
/// </summary>
public sealed class BalanceSuggestionAnswer
{
    [JsonPropertyName("fields")]
    public List<BalanceSuggestionFieldAnswer>? Fields { get; set; }

    [JsonPropertyName("items")]
    public List<BalanceSuggestionItemGroupAnswer>? Items { get; set; }
}

/// <summary>Where a value is printed: image, half, the row's vertical band and the label as printed.</summary>
public abstract class BalanceSuggestionLocatedAnswer
{
    [JsonPropertyName("printed_value")]
    public double? PrintedValue { get; set; }

    [JsonPropertyName("matches_stored")]
    public bool? MatchesStored { get; set; }

    [JsonPropertyName("image")]
    public int? Image { get; set; }

    [JsonPropertyName("half")]
    public string? Half { get; set; }

    [JsonPropertyName("band")]
    public List<double>? Band { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }
}

/// <summary>The answer for one top-level figure.</summary>
public sealed class BalanceSuggestionFieldAnswer : BalanceSuggestionLocatedAnswer
{
    [JsonPropertyName("path")]
    public string? Path { get; set; }
}

/// <summary>The answer for one rubro list: every line printed under that rubro.</summary>
public sealed class BalanceSuggestionItemGroupAnswer
{
    [JsonPropertyName("array")]
    public string? Array { get; set; }

    [JsonPropertyName("lines")]
    public List<BalanceSuggestionLineAnswer>? Lines { get; set; }
}

/// <summary>One printed rubro line, with the stored line it is (null: none of them).</summary>
public sealed class BalanceSuggestionLineAnswer : BalanceSuggestionLocatedAnswer
{
    [JsonPropertyName("stored_index")]
    public int? StoredIndex { get; set; }
}

/// <summary>
/// The outcome of <see cref="IClaudeService.SuggestBalanceCorrectionsAsync"/>. Never an exception (only
/// cancellation propagates): a failure is a <see cref="FailureCode"/>, so the caller can log the cost of
/// what WAS spent — the orientation call, and the request's tokens when the API answered.
/// </summary>
public sealed record BalanceSuggestionReply
{
    /// <summary>The parsed answer; null on a failure.</summary>
    public BalanceSuggestionAnswer? Answer { get; init; }

    /// <summary>The images the request carried, in order; empty when nothing was rendered.</summary>
    public IReadOnlyList<RenderedImage> Images { get; init; } = [];

    /// <summary>The resolved extraction model the request went to.</summary>
    public required string Model { get; init; }

    /// <summary>True when the request reached the API (a cost row is owed).</summary>
    public bool RequestSent { get; init; }

    /// <summary>The HTTP status of the answer, when one came back.</summary>
    public int? HttpStatus { get; init; }

    public int InputTokens  { get; init; }

    public int OutputTokens { get; init; }

    /// <summary>Wall time of the request (not of the rendering), in milliseconds.</summary>
    public int DurationMs { get; init; }

    /// <summary>The scan-orientation call, when one reached the API — priced with ITS model.</summary>
    public ScanOrientationUsage? OrientationDetection { get; init; }

    /// <summary>
    /// Null on success; otherwise a fixed code (<c>not_configured</c>, <c>render_failed</c>,
    /// <c>timeout</c>, <c>network_error</c>, <c>http_&lt;n&gt;</c>, <c>max_tokens</c>, <c>refusal</c>,
    /// <c>no_text</c>, <c>envelope_invalid</c>, <c>invalid_response</c>) — never a body, a figure or a label.
    /// </summary>
    public string? FailureCode { get; init; }

    /// <summary>
    /// For an HTTP error, the <see cref="Clases.Exceptions.ClaudeApiException.DiagnosticMessage"/>: the
    /// status, error type, a clamped message and the request id of the API's error envelope. Null
    /// otherwise. Written after <see cref="FailureCode"/> in the ApiCallLogs row; never the raw body.
    /// </summary>
    public string? FailureDetail { get; init; }

    /// <summary>True when the call produced an answer.</summary>
    public bool Succeeded => FailureCode is null && Answer is not null;
}
