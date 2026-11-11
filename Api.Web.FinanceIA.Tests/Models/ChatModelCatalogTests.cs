// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-02

using Api.Web.FinanceIA.Models.Financia;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Models;

/// <summary>
/// Covers <see cref="ChatModelCatalog"/>, the single server-side source of chat model ids.
///
/// <para>
/// Two things are being protected here. The first is <c>SanitizeOrDefault</c>'s promise that it NEVER
/// returns an empty list: it is the only thing standing between a blanked or garbled
/// <c>[ChatEnabledModels]</c> column and a tenant whose analysts cannot chat, and the failure would
/// be silent — a shorter dropdown, not an error.
/// </para>
///
/// <para>
/// The second is the ORDER of <see cref="ChatModelCatalog.All"/>. It is not presentation: the first
/// entry of a tenant's sanitised list is the model that answers a turn when the analyst named none,
/// so re-ordering that array changes which model every tenant's chat defaults to. Tests that pin
/// order exist to make that change deliberate.
/// </para>
///
/// No stubs and no DI — the catalogue is static data and pure functions.
/// </summary>
public sealed class ChatModelCatalogTests
{
    // ── The catalogue itself ──────────────────────────────────────────────────

    [Fact]
    public void The_catalogue_holds_the_three_supported_models_in_ascending_capability()
    {
        // Pinned as literals rather than derived from All, so this test fails when a model is added
        // or retired instead of silently agreeing with whatever the catalogue now says. Adding one is
        // a deliberate act with a DDL and a Portal deploy behind it — see docs/db-changes/052.
        Assert.Equal(
            new[] { "claude-haiku-4-5", "claude-sonnet-4-6", "claude-sonnet-5" },
            ChatModelCatalog.AllIds);
    }

    [Fact]
    public void Every_id_carries_no_date_suffix()
    {
        // A recurring, silent failure mode: model ids from training data often carry a date
        // (claude-sonnet-4-6-20251114). Those name nothing, and the API answers 404. The ids here are
        // complete as written.
        foreach (var model in ChatModelCatalog.All)
        {
            Assert.DoesNotContain("-2025", model.Id, StringComparison.Ordinal);
            Assert.DoesNotContain("-2026", model.Id, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_default_is_the_model_the_chat_used_before_it_was_configurable()
    {
        // This delivery changed which models MAY be called, not which one IS called by default.
        Assert.Equal("claude-sonnet-4-6", ChatModelCatalog.Default.Id);
    }

    [Fact]
    public void AllIds_matches_All_in_both_content_and_order()
    {
        // Two arrays maintained by hand beside each other; this is what stops them drifting.
        Assert.Equal(ChatModelCatalog.All.Select(m => m.Id).ToArray(), ChatModelCatalog.AllIds);
    }

    [Fact]
    public void DefaultCsv_states_the_same_list_the_database_default_does()
    {
        // The literal below is DF_TenantBillingConfig_ChatEnabledModels from
        // docs/database/052_tenantbillingconfig_add_chatenabledmodels.sql. The column backfills every
        // pre-existing row with it, so a mismatch would make a migrated tenant and a freshly written
        // one disagree about what they are entitled to.
        Assert.Equal("claude-haiku-4-5,claude-sonnet-4-6,claude-sonnet-5", ChatModelCatalog.DefaultCsv);
    }

    // ── Per-model call parameters ─────────────────────────────────────────────

    [Fact]
    public void Only_the_thinking_model_omits_temperature()
    {
        // The single most consequential field on the record: Claude Sonnet 5 answers 400 to any
        // non-default sampling parameter, so its entry must carry null and the other two must not.
        Assert.Null(ChatModelCatalog.Sonnet5.Temperature);
        Assert.Equal(0m, ChatModelCatalog.Haiku45.Temperature);
        Assert.Equal(0m, ChatModelCatalog.Sonnet46.Temperature);
    }

    [Fact]
    public void The_thinking_model_gets_more_output_headroom_than_the_others()
    {
        // max_tokens caps thinking and answer together, so the model that thinks needs room the other
        // two do not. The exact figure is tunable; that it is larger is not.
        Assert.True(ChatModelCatalog.Sonnet5.MaxTokens > ChatModelCatalog.Sonnet46.MaxTokens);
        Assert.True(ChatModelCatalog.Sonnet5.MaxTokens > ChatModelCatalog.Haiku45.MaxTokens);
    }

    [Fact]
    public void Every_model_carries_positive_pricing_and_display_copy()
    {
        // The cost fields feed ApiCallLogs.EstimatedCostUsd. A zero rate would not throw anywhere —
        // it would quietly report every turn on that model as free.
        foreach (var model in ChatModelCatalog.All)
        {
            Assert.True(model.InputCostPerMTokUsd  > 0m, $"{model.Id} has no input rate");
            Assert.True(model.OutputCostPerMTokUsd > 0m, $"{model.Id} has no output rate");
            Assert.True(model.OutputCostPerMTokUsd > model.InputCostPerMTokUsd);
            Assert.False(string.IsNullOrWhiteSpace(model.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(model.Description));
        }
    }

    // ── Find / Contains ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("claude-sonnet-4-6")]
    [InlineData("CLAUDE-SONNET-4-6")]
    [InlineData("  claude-sonnet-4-6  ")]
    public void Find_matches_case_insensitively_and_ignores_surrounding_whitespace(string id)
    {
        Assert.Equal(ChatModelCatalog.Sonnet46, ChatModelCatalog.Find(id));
        Assert.True(ChatModelCatalog.Contains(id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("gpt-4")]
    [InlineData("claude-opus-5")]
    [InlineData("claude-sonnet-4-6-20251114")]
    public void Find_returns_null_for_anything_outside_the_catalogue(string? id)
    {
        Assert.Null(ChatModelCatalog.Find(id));
        Assert.False(ChatModelCatalog.Contains(id));
    }

    [Fact]
    public void Every_catalogue_id_round_trips_through_Find()
    {
        foreach (var model in ChatModelCatalog.All)
            Assert.Same(model, ChatModelCatalog.Find(model.Id));
    }

    // ── SanitizeOrDefault ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",,,")]
    [InlineData("gpt-4,bogus")]
    [InlineData("claude-sonnet-4-6-20251114")]
    public void A_blank_or_wholly_unrecognised_value_resolves_to_the_whole_catalogue(string? stored)
    {
        // THE load-bearing guarantee. Every one of these inputs is reachable in production: a row
        // written before script 052, a column a DBA blanked, a list naming only models since retired.
        // None of them may leave a tenant with no model — that would take the chat away as a result
        // of bad data, which is worse than over-permitting a capability every tenant already had.
        Assert.Equal(ChatModelCatalog.AllIds, ChatModelCatalog.SanitizeOrDefault(stored));
    }

    [Fact]
    public void Unknown_ids_are_dropped_but_a_surviving_known_one_is_kept()
    {
        // Partial recognition must NOT fall back to everything — that would silently re-grant a
        // model a SuperAdmin had removed. Only a wholly empty result triggers the default.
        Assert.Equal(
            new[] { "claude-haiku-4-5" },
            ChatModelCatalog.SanitizeOrDefault("claude-haiku-4-5,gpt-4,bogus"));
    }

    [Fact]
    public void Entries_are_trimmed_and_matched_case_insensitively()
    {
        Assert.Equal(
            new[] { "claude-haiku-4-5", "claude-sonnet-5" },
            ChatModelCatalog.SanitizeOrDefault("  CLAUDE-HAIKU-4-5 , claude-sonnet-5  "));
    }

    [Fact]
    public void Duplicates_collapse_to_one_entry()
    {
        Assert.Equal(
            new[] { "claude-sonnet-4-6" },
            ChatModelCatalog.SanitizeOrDefault("claude-sonnet-4-6,claude-sonnet-4-6,CLAUDE-SONNET-4-6"));
    }

    [Fact]
    public void The_result_is_re_ordered_to_the_catalogue_regardless_of_how_it_was_stored()
    {
        // Not cosmetic. The FIRST entry is the model a chat turn falls back to, so a tenant's default
        // must not depend on the order a SuperAdmin happened to tick the checkboxes in.
        Assert.Equal(
            new[] { "claude-haiku-4-5", "claude-sonnet-4-6", "claude-sonnet-5" },
            ChatModelCatalog.SanitizeOrDefault("claude-sonnet-5,claude-sonnet-4-6,claude-haiku-4-5"));
    }

    [Fact]
    public void Sanitize_and_ToCsv_round_trip()
    {
        // ToCsv is what writes the column and SanitizeOrDefault is what reads it; a value that
        // survived one must survive the other unchanged, or a saved plan would not equal itself.
        var models = ChatModelCatalog.SanitizeOrDefault("claude-sonnet-5,claude-haiku-4-5");
        var csv    = ChatModelCatalog.ToCsv(models);

        Assert.Equal("claude-haiku-4-5,claude-sonnet-5", csv);
        Assert.Equal(models, ChatModelCatalog.SanitizeOrDefault(csv));
    }

    [Fact]
    public void The_stored_csv_for_the_whole_catalogue_fits_the_columns_width()
    {
        // [ChatEnabledModels] is NVARCHAR(400) (script 052) and the write path does not truncate, so
        // an over-long list would fail as a 500 rather than as validation. The margin is deliberate
        // headroom for the catalogue to grow; this fails long before a real row could.
        Assert.True(ChatModelCatalog.DefaultCsv.Length <= 400);
    }
}
