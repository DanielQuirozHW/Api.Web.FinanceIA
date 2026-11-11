// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-16

using System.Text.Json;
using Api.Web.FinanceIA.Models.Financia;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// Locks the one behaviour the whole partial-update contract rests on: an OMITTED field and an
/// EXPLICIT NULL must be distinguishable after deserialization.
///
/// If this collapses, PATCH silently clears every field the client did not mention — or, in the
/// other direction, a threshold can never be cleared. Both failures are silent at the HTTP layer,
/// which is why they are pinned here rather than left to the controller tests.
/// </summary>
public sealed class PatchJsonTests
{
    private static UpdateAlertRuleRequest Parse(string json) =>
        JsonSerializer.Deserialize<UpdateAlertRuleRequest>(
            json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    // ── Absent ──────────────────────────────────────────────────────────────────

    [Fact]
    public void OmittedField_IsNotSet()
    {
        var request = Parse("""{"enabled": true}""");

        Assert.False(request.ThresholdValue.IsSet);
        Assert.False(request.Severity.IsSet);
        Assert.True(request.Enabled.IsSet);
    }

    [Fact]
    public void EmptyPayload_LeavesEveryFieldUnset()
    {
        var request = Parse("{}");

        Assert.False(request.Enabled.IsSet);
        Assert.False(request.Severity.IsSet);
        Assert.False(request.ThresholdValue.IsSet);
    }

    // ── Present-and-null: the case a nullable property cannot express ────────────

    /// <summary>
    /// THE DISTINCTION: <c>"thresholdValue": null</c> is an instruction to clear the threshold,
    /// not an absent field. A plain <c>decimal?</c> would report both as null.
    /// </summary>
    [Fact]
    public void ExplicitNull_IsSet_WithNullValue()
    {
        var request = Parse("""{"thresholdValue": null}""");

        Assert.True(request.ThresholdValue.IsSet);
        Assert.Null(request.ThresholdValue.Value);
    }

    [Fact]
    public void ExplicitNull_AndOmitted_AreDistinguishable()
    {
        var cleared = Parse("""{"thresholdValue": null}""");
        var omitted = Parse("""{"enabled": false}""");

        Assert.NotEqual(cleared.ThresholdValue.IsSet, omitted.ThresholdValue.IsSet);
    }

    // ── Present with a value ────────────────────────────────────────────────────

    [Fact]
    public void PresentValue_IsSet_WithThatValue()
    {
        var request = Parse("""{"thresholdValue": 0.55, "severity": "high", "enabled": true}""");

        Assert.True(request.ThresholdValue.IsSet);
        Assert.Equal(0.55m, request.ThresholdValue.Value);
        Assert.True(request.Severity.IsSet);
        Assert.Equal("high", request.Severity.Value);
        Assert.True(request.Enabled.IsSet);
        Assert.True(request.Enabled.Value);
    }

    /// <summary>
    /// <c>false</c> is a real instruction (disable the rule), not an absent field — the trap a
    /// truthiness-based implementation would fall into.
    /// </summary>
    [Fact]
    public void ExplicitFalse_IsSet()
    {
        var request = Parse("""{"enabled": false}""");

        Assert.True(request.Enabled.IsSet);
        Assert.False(request.Enabled.Value);
    }

    // ── Or(): the collapse used by the controller ───────────────────────────────

    [Fact]
    public void Or_ReturnsCurrent_WhenUnset()
    {
        var request = Parse("{}");

        Assert.Equal(0.30m, request.ThresholdValue.Or(0.30m));
        Assert.True(request.Enabled.Or(true));
        Assert.Equal("medium", request.Severity.Or("medium"));
    }

    [Fact]
    public void Or_ReturnsNull_WhenExplicitlyNulled()
    {
        var request = Parse("""{"thresholdValue": null}""");

        Assert.Null(request.ThresholdValue.Or(0.30m));
    }

    [Fact]
    public void Or_ReturnsNewValue_WhenSet()
    {
        var request = Parse("""{"thresholdValue": 0.55}""");

        Assert.Equal(0.55m, request.ThresholdValue.Or(0.30m));
    }
}
