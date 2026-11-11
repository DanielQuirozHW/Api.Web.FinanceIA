// Project : Api.Web.FinanceIA
// Created : 2026-09-24

using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// One rubro line of a balance-sheet subtotal (e.g. "Deudas comerciales" under pasivo corriente),
/// exactly as printed, with its signed amount. Extraction prompt 2.18+ emits these so the subtotal
/// can be checked against the sum of its own lines — the only check that catches a subtotal read
/// from the wrong column when the totals still add up. Persisted inside BalanceJson under
/// <c>current_assets_items</c>, <c>current_liabilities_items</c> and
/// <c>non_current_liabilities_items</c>, in pesos.
/// </summary>
public sealed record BalanceLineItem
{
    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("amount")]
    public double? Amount { get; init; }
}
