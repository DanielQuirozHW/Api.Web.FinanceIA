// Project : Api.Web.FinanceIA
// Created : 2026-08-31

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// EF Core entity that maps to [dbo].[TenantBillingConfig].
/// The monthly billing/quota limits a tenant was sold: how many analyses it may run and
/// how many Claude chat tokens it may spend per calendar month. There is exactly one row
/// per tenant (UQ_TenantBillingConfig_Tenant).
///
/// <para>
/// These limits are enforced. <c>AnalysisMonthlyLimit</c> hard-blocks a new analysis once the
/// tenant's calendar-month count reaches it; <c>ChatTokensMonthlyLimit</c> is measured and logged
/// but only blocks when <c>Billing:EnforceChatTokens</c> is switched on, because the per-plan
/// token figures in <see cref="BillingPlans"/> are still placeholders. Both gates run in
/// <c>BillingQuotaFilter</c> and both fail OPEN if the numbers cannot be read. The 80%/100%
/// usage alerts are a later delivery.
/// </para>
///
/// <para>
/// A tenant with no row, or with <see cref="IsActive"/> false, resolves to
/// <see cref="BillingPlans.Minimum"/> rather than failing — the same contract
/// <see cref="TenantSettings"/> has with <see cref="TenantSettingKeys"/>. That is why
/// the commercial presets live in <see cref="BillingPlans"/> and not in a catalogue table.
/// </para>
///
/// Database First — schema changes via DDL scripts only
/// (see docs/database/047_tenant_billing_config.sql).
/// </summary>
public sealed class TenantBillingConfig
{
    /// <summary>Primary key (database-assigned via NEWSEQUENTIALID()).</summary>
    public Guid BillingConfigId { get; set; }

    /// <summary>Firebase tenant slug that owns this billing configuration.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable label for what was sold, e.g. <c>"100 análisis"</c>.
    /// INFORMATIONAL ONLY: it is not a foreign key and is never looked up to derive the
    /// limits. The effective limits come from <see cref="AnalysisMonthlyLimit"/> and
    /// <see cref="ChatTokensMonthlyLimit"/> on this row, so a tenant can be priced
    /// off-preset without a code change.
    /// </summary>
    public string PlanName { get; set; } = string.Empty;

    /// <summary>Analyses the tenant may run per calendar month.</summary>
    public int AnalysisMonthlyLimit { get; set; }

    /// <summary>
    /// Claude chat tokens the tenant may spend per calendar month. <c>long</c> to match the
    /// column's BIGINT: a monthly token pool passes int.MaxValue at plausible volumes.
    /// </summary>
    public long ChatTokensMonthlyLimit { get; set; }

    /// <summary>
    /// Claude tokens a SINGLE chat conversation's context may occupy.
    ///
    /// <para>
    /// Orthogonal to <see cref="ChatTokensMonthlyLimit"/> above, not a smaller version of it. That
    /// one is a tenant-wide POOL for a calendar month, measured against what has already been
    /// spent; this one bounds how large ONE prompt may get at ONE moment, and is not consumed by
    /// anything — a conversation that ends releases nothing, because there was never a running
    /// total.
    /// </para>
    ///
    /// <para>
    /// <c>int</c> to match the column's INT, and INT is right where
    /// <see cref="ChatTokensMonthlyLimit"/> needs BIGINT: a monthly pool passes int.MaxValue at
    /// plausible volumes, a single conversation's context cannot — the chat model's window is
    /// 200.000 tokens.
    /// </para>
    ///
    /// <para>
    /// NOTHING ENFORCES THIS YET. It is stored, resolved and reported; trimming a conversation's
    /// context against it is a later delivery. The per-tier figures in <see cref="BillingPlans"/>
    /// are placeholders until then.
    /// </para>
    /// </summary>
    public int ChatContextBudgetTokens { get; set; }

    /// <summary>
    /// The Claude chat models this tenant may use, as the comma-separated list of model ids stored in
    /// [ChatEnabledModels]. Read through <see cref="ChatEnabledModels"/> rather than directly.
    ///
    /// <para>
    /// NOT AN ALLOWANCE, unlike every other configuration column on this row. Those are quantities
    /// that get consumed — analyses per month, tokens per month, tokens per conversation — and
    /// exceeding one refuses a request. This is a CAPABILITY SET: which models the analyst's
    /// per-conversation picker may offer. Using it does not spend it, and there is nothing to run
    /// out of.
    /// </para>
    ///
    /// <para>
    /// The catalogue of models that exist is <see cref="ChatModelCatalog"/>, in code; this column
    /// records only which of them were sold. An id here that the catalogue does not know is
    /// discarded on read, so a retired model stops being callable without a data migration.
    /// </para>
    ///
    /// <para>
    /// Defaults to the whole catalogue on a new instance, matching
    /// DF_TenantBillingConfig_ChatEnabledModels in script 052 — the chat model was hardcoded ON for
    /// every tenant before this column existed, so "unconfigured" here means "what they already had",
    /// not "the least" as it does for the allowances above.
    /// </para>
    /// </summary>
    public string ChatEnabledModelsRaw { get; set; } = ChatModelCatalog.DefaultCsv;

    /// <summary>
    /// <see cref="ChatEnabledModelsRaw"/> parsed into the ids that are actually callable: trimmed,
    /// de-duplicated, filtered to <see cref="ChatModelCatalog.All"/> and ordered by it.
    ///
    /// <para>
    /// NEVER EMPTY — a blank or wholly unrecognised stored value resolves to the full catalogue
    /// rather than to "no models", so bad data cannot take the chat away from a tenant. The rule
    /// lives in <see cref="ChatModelCatalog.SanitizeOrDefault"/>; this property only exposes it.
    /// </para>
    ///
    /// <para>
    /// Computed, not mapped: TenantBillingConfigConfiguration ignores it explicitly.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> ChatEnabledModels => ChatModelCatalog.SanitizeOrDefault(ChatEnabledModelsRaw);

    /// <summary>
    /// Whether this configuration is in force. False switches it off without deleting the
    /// row, so the commercial history survives; an inactive row resolves to
    /// <see cref="BillingPlans.Minimum"/>, exactly as a missing row does.
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>UTC timestamp of the last update.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Who last changed this row, for audit. Free text rather than a foreign key to
    /// [dbo].[Users] because the writer is a SuperAdmin, which is not necessarily a row in this
    /// tenant's Users table. Set to the caller's user id by PUT /api/billing/config, to a free
    /// label by the DBA when seeding, or null on a row that predates both.
    /// </summary>
    public string? UpdatedBy { get; set; }
}
