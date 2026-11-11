// Project : Api.Web.FinanceIA
// Created : 2026-08-31

using System.Text.Json.Serialization;
using Api.Web.FinanceIA.Clases.Json;

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Request body for PUT /api/billing/config (SuperAdmin only).
///
/// <para>
/// Accepts EITHER a commercial preset OR custom numbers. <see cref="PlanName"/> is always
/// required and is always what gets stored, because the column is an informational label — see
/// <see cref="TenantBillingConfig.PlanName"/>. When it matches a <see cref="BillingPlans"/> preset,
/// that preset supplies the limits; any limit sent explicitly overrides the preset's. With no
/// matching preset, BOTH limits must be sent, since the missing one has nowhere to come from.
/// </para>
/// </summary>
public sealed class UpdateBillingConfigRequest
{
    /// <summary>
    /// Human-readable plan label, stored verbatim (trimmed). Required, max 100 characters to
    /// match the column. Matching a preset name is what makes the two limits optional.
    /// </summary>
    public string PlanName { get; set; } = string.Empty;

    /// <summary>
    /// Analyses per calendar month. Optional when <see cref="PlanName"/> names a preset;
    /// when supplied it wins over the preset's figure. Must be greater than zero.
    /// </summary>
    public int? AnalysisMonthlyLimit { get; set; }

    /// <summary>
    /// Claude chat tokens per calendar month. Optional when <see cref="PlanName"/> names a preset;
    /// when supplied it wins over the preset's figure. Must not be negative.
    /// </summary>
    public long? ChatTokensMonthlyLimit { get; set; }

    /// <summary>
    /// Claude tokens a SINGLE chat conversation's context may occupy. Optional; when supplied it
    /// wins over the preset's figure. Must be greater than zero.
    ///
    /// <para>
    /// Unlike the two limits above this one can ALWAYS be resolved, so omitting it never produces
    /// a 400: with no matching preset it falls back to <see cref="BillingPlans.Minimum"/>'s budget.
    /// That keeps an off-preset caller that sends only the two monthly limits working exactly as it
    /// did before this field existed.
    /// </para>
    /// </summary>
    public int? ChatContextBudgetTokens { get; set; }

    /// <summary>
    /// Ids of the Claude chat models this tenant may use. Accepts EITHER a JSON array
    /// (<c>["claude-haiku-4-5","claude-sonnet-4-6"]</c>) OR one comma-separated string
    /// (<c>"claude-haiku-4-5,claude-sonnet-4-6"</c>) — the Portal sends the first, an operator
    /// replaying a value out of SQL has the second.
    ///
    /// <para>
    /// Every id must name an entry in <see cref="ChatModelCatalog"/>; an unknown one is a 400 that
    /// names it, rather than being dropped, so a typo cannot silently narrow a tenant's access.
    /// </para>
    ///
    /// <para>
    /// OMITTED and EMPTY are different. Omitting it resolves like the other optional fields — the
    /// preset's list, else <see cref="BillingPlans.Minimum"/>'s — so a caller that predates this
    /// field keeps working unchanged. Sending an empty list is an explicit request to leave the
    /// tenant with no chat model at all, which is refused: it would break the chat outright, and it
    /// is far more likely to be a client bug than an intention.
    /// </para>
    /// </summary>
    [JsonConverter(typeof(CsvOrStringListConverter))]
    public IReadOnlyList<string>? ChatEnabledModels { get; set; }

    /// <summary>
    /// Whether the configuration is in force. Optional, defaults to true. Sending false switches
    /// the row off without deleting it, and the tenant falls back to
    /// <see cref="BillingPlans.Minimum"/> — the documented <see cref="TenantBillingConfig.IsActive"/>
    /// semantics.
    /// </summary>
    public bool? IsActive { get; set; }
}
