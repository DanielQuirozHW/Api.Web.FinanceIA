// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-07

using System.Text.Json;
using Api.Web.FinanceIA.Models.Financia;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Models;

/// <summary>
/// Locks the WIRE CONTRACT of GET /api/billing/usage, which the Portal is built against.
///
/// <para>
/// <see cref="BillingControllerTests"/> asserts the projection at CLR level and never serializes,
/// so nothing there catches a casing change. The casing is camelCase because <c>Program.cs</c>
/// calls <c>AddControllers().AddJsonOptions(...)</c> and adds only the two UTC DateTime converters,
/// setting no <c>PropertyNamingPolicy</c>; MVC therefore builds its options from
/// <see cref="JsonSerializerDefaults.Web"/>, which is what this test reproduces.
/// </para>
/// </summary>
public sealed class BillingUsageResponseSerializationTests
{
    // The same starting point AddControllers() uses, so this test tracks the real pipeline rather
    // than a guess about it.
    private static readonly JsonSerializerOptions WebDefaults = new(JsonSerializerDefaults.Web);

    private static BillingUsageResponse Response() => new()
    {
        PlanName                = "100 análisis",
        PeriodStart             = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        PeriodEnd               = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        AnalysesUsed            = 40,
        AnalysesLimit           = 100,
        AnalysesRemaining       = 60,
        AnalysesPercent         = 40.00m,
        ChatTokensUsed          = 1_000_000L,
        ChatTokensLimit         = 4_000_000L,
        ChatTokensRemaining     = 3_000_000L,
        ChatTokensPercent       = 25.00m,
        ChatContextBudgetTokens = 60_000,
        ChatEnabledModels       = ChatModelCatalog.AllIds,
        DefaultChatModel        = ChatModelCatalog.Default.Id,
        ChatTokensSource        = nameof(ChatTokenSource.ApiCallLogs),
        ChatTokensEnforced      = false,
        IsDegraded              = false,
        DegradedReason          = null
    };

    [Fact]
    public void BillingUsageResponse_SerialisesEveryFieldInCamelCase()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(Response(), WebDefaults));
        var actual = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();

        Assert.Equal(
            [
                "planName", "periodStart", "periodEnd",
                "analysesUsed", "analysesLimit", "analysesRemaining", "analysesPercent",
                "chatTokensUsed", "chatTokensLimit", "chatTokensRemaining", "chatTokensPercent",
                "chatContextBudgetTokens", "chatEnabledModels", "defaultChatModel",
                "chatTokensSource", "chatTokensEnforced", "isDegraded", "degradedReason",
                // Score level contract: the tenant's current level cutoffs, null when unreadable.
                "levelCutoffs"
            ],
            actual);
    }

    /// <summary>
    /// levelCutoffs goes out as { low, medium, high } — the same shape as ScoreBreakdownJson's — and as
    /// an explicit null when it could not be read, never as a missing key.
    /// </summary>
    [Fact]
    public void BillingUsageResponse_LevelCutoffs_IsLowMediumHigh_OrAnExplicitNull()
    {
        using var withCutoffs = JsonDocument.Parse(JsonSerializer.Serialize(
            Response() with { LevelCutoffs = new ScoreLevelCutoffs(30m, 55m, 75m) }, WebDefaults));
        var cutoffs = withCutoffs.RootElement.GetProperty("levelCutoffs");
        Assert.Equal(["low", "medium", "high"], cutoffs.EnumerateObject().Select(p => p.Name).ToList());
        Assert.Equal(30m, cutoffs.GetProperty("low").GetDecimal());
        Assert.Equal(55m, cutoffs.GetProperty("medium").GetDecimal());
        Assert.Equal(75m, cutoffs.GetProperty("high").GetDecimal());

        using var unreadable = JsonDocument.Parse(JsonSerializer.Serialize(Response(), WebDefaults));
        Assert.Equal(JsonValueKind.Null, unreadable.RootElement.GetProperty("levelCutoffs").ValueKind);
    }

    /// <summary>
    /// The two model fields the Portal's selector is built from: the list it renders and the id it
    /// pre-selects. Named separately from the roster test above so a break points at the cause.
    /// </summary>
    [Fact]
    public void BillingUsageResponse_CarriesTheModelListAndThePreselectedId()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(Response(), WebDefaults));
        var root = doc.RootElement;

        Assert.Equal(
            ChatModelCatalog.AllIds,
            root.GetProperty("chatEnabledModels").EnumerateArray().Select(e => e.GetString()!).ToList());

        Assert.Equal("claude-sonnet-4-6", root.GetProperty("defaultChatModel").GetString());
    }
}
