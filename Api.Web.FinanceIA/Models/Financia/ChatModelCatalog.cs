// Project : Api.Web.FinanceIA
// Created : 2026-09-02

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// One Claude model the analyst chat is allowed to call, together with everything the call needs to
/// be built correctly for it.
///
/// <para>
/// THE PER-MODEL FIELDS ARE NOT DECORATION. It would be tempting to reduce this record to an id and a
/// label and treat the model as a string the payload interpolates, because that is true of
/// <c>claude-haiku-4-5</c> and <c>claude-sonnet-4-6</c>, which share a request surface. It is NOT true
/// of <c>claude-sonnet-5</c>, and a request built for one of the first two fails outright against it:
/// </para>
///
/// <list type="bullet">
/// <item>
/// <see cref="Temperature"/> is nullable because Claude Sonnet 5 REJECTS a non-default sampling
/// parameter with HTTP 400. <c>temperature = 0</c> — which every Claude call in this project sends,
/// and which CLAUDE.md states as a rule — is a non-default value. Null means "omit the field
/// entirely", and <c>ClaudeService.ChatAsync</c> builds a different payload shape for it.
/// </item>
/// <item>
/// <see cref="MaxTokens"/> is per-model because Claude Sonnet 5 runs ADAPTIVE THINKING BY DEFAULT
/// (omitting the <c>thinking</c> field enables it, the opposite of Sonnet 4.6) at effort <c>high</c>,
/// and <c>max_tokens</c> caps thinking and answer TOGETHER. The 1000 that has always been enough for
/// the two non-thinking models would be spent on reasoning before a word of the answer was written,
/// returning a truncated response or none at all. Its tokenizer also emits ~30% more tokens for the
/// same text than Sonnet 4.6's.
/// </item>
/// </list>
///
/// <para>
/// The two cost fields exist because <c>AnalysisController</c> used to compute every chat turn's
/// estimated cost from Sonnet 4.6's $3/$15 written inline. Once the model varies per conversation
/// that arithmetic is simply wrong — it would over-report a Haiku turn threefold — so the rates live
/// beside the id that determines them.
/// </para>
/// </summary>
/// <param name="Id">
/// The exact Anthropic model id sent on the wire. Complete as written: these ids carry NO date
/// suffix, and appending one produces a model that does not exist.
/// </param>
/// <param name="DisplayName">Short human label, e.g. "Sonnet 4.6". For the Portal's selector.</param>
/// <param name="Description">One-line es-AR note describing the trade-off, rendered by the Portal.</param>
/// <param name="MaxTokens">Output ceiling for the chat call — thinking plus answer, where the model thinks.</param>
/// <param name="Temperature">
/// Value for the payload's <c>temperature</c> field, or null when the model rejects the field and it
/// must be omitted.
/// </param>
/// <param name="InputCostPerMTokUsd">USD per million input tokens.</param>
/// <param name="OutputCostPerMTokUsd">USD per million output tokens.</param>
public sealed record ChatModel(
    string   Id,
    string   DisplayName,
    string   Description,
    int      MaxTokens,
    decimal? Temperature,
    decimal  InputCostPerMTokUsd,
    decimal  OutputCostPerMTokUsd);

/// <summary>
/// The ONLY place a chat model id lives server-side: the global catalogue of models the analyst chat
/// supports, and the source every other component reads from.
///
/// <para>
/// It sits here rather than in a database table for the same reason <see cref="BillingPlans"/> holds
/// the commercial presets and <see cref="TenantSettingKeys"/> holds the settings defaults: a tenant
/// with no configuration must resolve to something sensible and must never fail, and adding a model
/// must not require inserting a row for every existing tenant. What the DATABASE holds is only WHICH
/// of this catalogue a given tenant may use — <c>TenantBillingConfig.ChatEnabledModels</c>.
/// </para>
///
/// <para>
/// The catalogue is a CEILING, not an entitlement. A tenant's allow-list can only ever be a subset of
/// <see cref="All"/>: <see cref="SanitizeOrDefault"/> discards anything else, so an id retired from
/// here stops being callable everywhere at once without a data migration.
/// </para>
///
/// <para>
/// ORDER IS LOAD-BEARING. <see cref="All"/> is the canonical order of the allow-list, so the first
/// entry a tenant has enabled is the model a chat turn falls back to when the analyst named none — or
/// named one they are not entitled to. Reordering this array changes that default for every tenant.
/// </para>
/// </summary>
public static class ChatModelCatalog
{
    // ── The supported models ────────────────────────────────────────────────────
    //
    // Ids verified against the Anthropic Messages API. All three are called through the same
    // https://api.anthropic.com/v1/messages endpoint with anthropic-version 2023-06-01 that
    // ClaudeService already uses; what differs between them is captured in the fields above and
    // nowhere else.

    /// <summary>Cheapest and fastest. 200K context window; no thinking.</summary>
    public static readonly ChatModel Haiku45 = new(
        Id:                   "claude-haiku-4-5",
        DisplayName:          "Haiku 4.5",
        Description:          "El más rápido y económico. Ideal para consultas puntuales sobre el análisis.",
        MaxTokens:            1000,
        Temperature:          0m,
        InputCostPerMTokUsd:  1m,
        OutputCostPerMTokUsd: 5m);

    /// <summary>
    /// The model the chat has always used, and still its default. Its payload is byte-identical to
    /// the one this project sent before the catalogue existed.
    /// </summary>
    public static readonly ChatModel Sonnet46 = new(
        Id:                   "claude-sonnet-4-6",
        DisplayName:          "Sonnet 4.6",
        Description:          "Equilibrio entre calidad y costo. Es el modelo por defecto del chat.",
        MaxTokens:            1000,
        Temperature:          0m,
        InputCostPerMTokUsd:  3m,
        OutputCostPerMTokUsd: 15m);

    /// <summary>
    /// The most capable of the three, and the one with a different request surface — see the two
    /// per-model fields on <see cref="ChatModel"/>. Cheaper per token than Sonnet 4.6, but it thinks
    /// before answering, so a turn spends more tokens and takes longer.
    /// </summary>
    public static readonly ChatModel Sonnet5 = new(
        Id:                   "claude-sonnet-5",
        DisplayName:          "Sonnet 5",
        Description:          "El más capaz. Razona antes de responder: tarda más y consume más tokens por respuesta.",
        MaxTokens:            8000,
        Temperature:          null,
        InputCostPerMTokUsd:  2m,
        OutputCostPerMTokUsd: 10m);

    /// <summary>
    /// The model a chat turn uses when nothing narrows it further. Sonnet 4.6, because that is what
    /// the chat called before it was configurable — this delivery changes who MAY be called, not who
    /// IS called by default.
    /// </summary>
    public static readonly ChatModel Default = Sonnet46;

    // ── Lookup ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every supported model, in the catalogue's canonical order (cheapest first). This order defines
    /// which model a tenant's allow-list falls back to — see the class remarks.
    /// </summary>
    public static readonly IReadOnlyList<ChatModel> All = [Haiku45, Sonnet46, Sonnet5];

    /// <summary>Every supported id, in the same order as <see cref="All"/>.</summary>
    public static readonly IReadOnlyList<string> AllIds = [Haiku45.Id, Sonnet46.Id, Sonnet5.Id];

    /// <summary>
    /// <see cref="AllIds"/> as the comma-separated string the database column stores. Kept here so
    /// the in-code default and the DF_ constraint in docs/database/052 state the same list, and so a
    /// new entity instance is born with a valid value rather than an empty one.
    /// </summary>
    public static readonly string DefaultCsv = string.Join(',', AllIds);

    /// <summary>
    /// Finds a model by its <see cref="ChatModel.Id"/>, case-insensitively and ignoring surrounding
    /// whitespace, or null when the catalogue has no such model. Mirrors
    /// <see cref="BillingPlans.Find"/>.
    /// </summary>
    public static ChatModel? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var trimmed = id.Trim();

        foreach (var model in All)
        {
            if (string.Equals(model.Id, trimmed, StringComparison.OrdinalIgnoreCase))
                return model;
        }

        return null;
    }

    /// <summary>True when <paramref name="id"/> names a model in this catalogue.</summary>
    public static bool Contains(string? id) => Find(id) is not null;

    /// <summary>
    /// Turns a stored comma-separated allow-list into the ids that are actually callable: split on
    /// commas, trimmed, blanks dropped, anything outside the catalogue dropped, duplicates collapsed,
    /// and re-ordered to match <see cref="All"/> so the fallback model is deterministic regardless of
    /// the order the column happened to be written in.
    ///
    /// <para>
    /// NEVER RETURNS EMPTY. A null, blank, or entirely unrecognised value resolves to
    /// <see cref="AllIds"/>, not to "no models". This is the single place that rule lives, and it is
    /// the reason a chat turn can always name a model: a row written before script 052, a column a
    /// DBA blanked by hand, and a list naming only models since retired from the catalogue all leave
    /// the chat working rather than dead. The alternative — an empty list — would take the feature
    /// away from a tenant as the result of bad data, which is a worse failure than over-permitting a
    /// capability every tenant already had when it was hardcoded.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> SanitizeOrDefault(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
            return AllIds;

        var requested = csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        // Built by walking All rather than the input, which collapses duplicates and imposes the
        // canonical order in one pass.
        var resolved = new List<string>(All.Count);

        foreach (var model in All)
        {
            foreach (var candidate in requested)
            {
                if (string.Equals(model.Id, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    resolved.Add(model.Id);
                    break;
                }
            }
        }

        return resolved.Count > 0 ? resolved : AllIds;
    }

    /// <summary>
    /// Renders a resolved allow-list back to the column's comma-separated form. The inverse of
    /// <see cref="SanitizeOrDefault"/> and the only thing that should build a value for
    /// <c>TenantBillingConfig.ChatEnabledModelsRaw</c>.
    /// </summary>
    public static string ToCsv(IEnumerable<string> ids) => string.Join(',', ids);
}
