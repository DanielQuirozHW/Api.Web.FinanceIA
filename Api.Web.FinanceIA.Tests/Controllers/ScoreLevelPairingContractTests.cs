// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-04

using System.Reflection;
using Api.Web.FinanceIA.Models.Financia;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// The score level contract, by reflection: every response that returns a risk score for display
/// also returns the level the BFF stored for it, so no client has a reason to recompute a level from
/// a score with cutoffs of its own. The pairing rule is <c>XScore</c> → <c>XLevel</c> (e.g.
/// <c>aiSuggestedScore</c> / <c>aiSuggestedLevel</c>), with the entity pair <c>latestScore</c> /
/// <c>latestRiskLevel</c>.
/// <para>
/// The sweep runs over EVERY public type in <c>Models.Financia</c> carrying a decimal <c>*Score</c>,
/// so a DTO added later without its level fails here instead of shipping. The handful of exceptions
/// are listed by name, each with its reason.
/// </para>
/// </summary>
public sealed class ScoreLevelPairingContractTests
{
    /// <summary>
    /// Score properties that are deliberately NOT paired with a level, and why. Keyed "Type.Property".
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Unpaired = new Dictionary<string, string>
    {
        // Entities.RiskScore: a legacy column no code writes, with no level column beside it. Kept on
        // the wire only for compatibility and documented as such; the pair to render is latestScore +
        // latestRiskLevel (asserted below).
        ["Entity.RiskScore"]                       = "legacy column, never written",
        ["EntityListProjection.RiskScore"]         = "legacy column, never written",
        ["EntityDetailProjection.RiskScore"]       = "legacy column, never written",
        ["EntityListItemResponse.RiskScore"]       = "legacy column, superseded by LatestScore",
        ["EntityResponse.RiskScore"]               = "legacy column, superseded by LatestScore",
        // Always null: no code computes an extraction confidence score into these responses.
        ["AnalysisListItemResponse.ConfidenceScore"] = "not a risk score",
        ["AnalysisResultResponse.ConfidenceScore"]   = "not a risk score",
        // The model's extraction confidence, internal to the pipeline — not a risk score.
        ["ClaudeAnalysisResult.ConfidenceScore"]     = "not a risk score",
        // A per-ratio partial score inside the breakdown; it carries its own status label
        // (Saludable / Atencion / Critico), not a composite risk level.
        ["RatioBreakdownEntry.Score"]                = "per-ratio partial score with its status",
    };

    /// <summary>The responses the Portal renders a score from. Each must carry the stored level.</summary>
    public static TheoryData<Type, string, string> DisplayedPairs => new()
    {
        { typeof(AnalysisListItemResponse),  nameof(AnalysisListItemResponse.AiSuggestedScore), nameof(AnalysisListItemResponse.AiSuggestedLevel) },
        { typeof(AnalysisListItemResponse),  nameof(AnalysisListItemResponse.FinalRiskScore),   nameof(AnalysisListItemResponse.FinalRiskLevel) },
        { typeof(AnalysisResultResponse),    nameof(AnalysisResultResponse.AiSuggestedScore),   nameof(AnalysisResultResponse.AiSuggestedLevel) },
        { typeof(AnalysisResultResponse),    nameof(AnalysisResultResponse.FinalRiskScore),     nameof(AnalysisResultResponse.FinalRiskLevel) },
        { typeof(MyWorkPendingItem),           nameof(MyWorkPendingItem.AiSuggestedScore),           nameof(MyWorkPendingItem.AiSuggestedLevel) },
        { typeof(MyWorkReturnedItem),          nameof(MyWorkReturnedItem.AiSuggestedScore),          nameof(MyWorkReturnedItem.AiSuggestedLevel) },
        { typeof(MyWorkAwaitingSignatureItem), nameof(MyWorkAwaitingSignatureItem.AiSuggestedScore), nameof(MyWorkAwaitingSignatureItem.AiSuggestedLevel) },
        { typeof(MyWorkRecentItem),            nameof(MyWorkRecentItem.AiSuggestedScore),            nameof(MyWorkRecentItem.AiSuggestedLevel) },
        { typeof(WaitingForMeItem),            nameof(WaitingForMeItem.AiSuggestedScore),            nameof(WaitingForMeItem.AiSuggestedLevel) },
        { typeof(TeamQueueItem),               nameof(TeamQueueItem.AiSuggestedScore),               nameof(TeamQueueItem.AiSuggestedLevel) },
        { typeof(RecentRescoreItem),           nameof(RecentRescoreItem.PreviousScore),              nameof(RecentRescoreItem.PreviousLevel) },
        { typeof(RecentRescoreItem),           nameof(RecentRescoreItem.NewScore),                   nameof(RecentRescoreItem.NewLevel) },
        { typeof(AnalysisRescoreSummary),      nameof(AnalysisRescoreSummary.PreviousScore),         nameof(AnalysisRescoreSummary.PreviousLevel) },
        { typeof(AnalysisRescoreSummary),      nameof(AnalysisRescoreSummary.NewScore),              nameof(AnalysisRescoreSummary.NewLevel) },
        { typeof(EntityListItemResponse),      nameof(EntityListItemResponse.LatestScore),           nameof(EntityListItemResponse.LatestRiskLevel) },
        { typeof(EntityResponse),              nameof(EntityResponse.LatestScore),                   nameof(EntityResponse.LatestRiskLevel) },
    };

    [Theory]
    [MemberData(nameof(DisplayedPairs))]
    public void EveryDisplayedScore_ShipsWithItsStoredLevel(Type dto, string scoreProperty, string levelProperty)
    {
        var score = dto.GetProperty(scoreProperty, BindingFlags.Public | BindingFlags.Instance);
        var level = dto.GetProperty(levelProperty, BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(score);
        Assert.NotNull(level);
        Assert.Equal(typeof(decimal?), score!.PropertyType);
        Assert.Equal(typeof(string),   level!.PropertyType);
    }

    /// <summary>
    /// The sweep. Any public decimal <c>*Score</c> property in <c>Models.Financia</c> has its level
    /// beside it, or is a named exception above.
    /// </summary>
    [Fact]
    public void EveryScoreProperty_InTheModels_HasItsLevel_OrIsANamedException()
    {
        var missing = new List<string>();

        var types = typeof(AnalysisListItemResponse).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(AnalysisListItemResponse).Namespace
                     && t.IsClass && !t.IsAbstract && !t.Name.StartsWith('<'));

        foreach (var type in types)
        {
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            var names = props.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var p in props.Where(p => p.Name.EndsWith("Score", StringComparison.Ordinal)
                                            && (p.PropertyType == typeof(decimal) || p.PropertyType == typeof(decimal?))))
            {
                string key = $"{type.Name}.{p.Name}";
                if (Unpaired.ContainsKey(key)) continue;

                string expected = p.Name == "LatestScore"
                    ? "LatestRiskLevel"
                    : p.Name[..^"Score".Length] + "Level";

                if (!names.Contains(expected)) missing.Add($"{key} (expected {expected})");
            }
        }

        Assert.True(missing.Count == 0,
            "Score without its stored level: " + string.Join(", ", missing));
    }

    /// <summary>The exceptions list stays honest: every entry still names a real property.</summary>
    [Fact]
    public void EveryNamedException_StillExists()
    {
        var types = typeof(AnalysisListItemResponse).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(AnalysisListItemResponse).Namespace)
            .GroupBy(t => t.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var key in Unpaired.Keys)
        {
            var parts = key.Split('.');
            Assert.True(types.TryGetValue(parts[0], out var type), key);
            Assert.NotNull(type!.GetProperty(parts[1]));
        }
    }
}
