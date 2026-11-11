// Project : Api.Web.FinanceIA
// Created : 2026-10-31

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>One TenantSettings row's value and the instant it was last written.</summary>
/// <param name="Value">The raw <c>SettingValue</c>.</param>
/// <param name="UpdatedAt">The row's <c>UpdatedAt</c> (UTC).</param>
public sealed record TenantSettingEntry(string Value, DateTime UpdatedAt);
