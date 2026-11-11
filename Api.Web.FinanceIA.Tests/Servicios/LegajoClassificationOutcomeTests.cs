// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-28

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Tests.Legajo;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// The C# decision after a classification: holder CUIT match / mismatch / missing / invalid, and the
/// review reasons. Synthetic CUITs only.
/// </summary>
public sealed class LegajoClassificationOutcomeTests
{
    private const decimal Threshold = 0.7m;

    private static LegajoClassificationOutput Output(
        string type = DocumentTypes.Monotributo,
        string? cuit = LegajoTestData.LegajoCuitDigits,
        bool composite = false,
        decimal confidence = 0.95m) => new()
    {
        DocumentType = type,
        HolderCuit   = cuit,
        IsComposite  = composite,
        Confidence   = confidence
    };

    private static LegajoClassificationOutcome Decide(LegajoClassificationOutput output, string declared = DocumentTypes.Monotributo)
        => LegajoClassificationOutcome.Decide(output, declared, LegajoTestData.LegajoCuit, Threshold);

    [Fact]
    public void MatchingCuit_SameType_Confident_IsReadyForExtraction()
    {
        var outcome = Decide(Output());

        Assert.Equal(LegajoStatuses.ReadyForExtraction, outcome.Status);
        Assert.Empty(outcome.ReviewReasons);
        Assert.True(outcome.HolderCuitMatches);
        Assert.Equal(LegajoTestData.LegajoCuit, outcome.HolderCuit);
    }

    [Fact]
    public void MismatchingCuit_IsBlocked_EvenWhenEverythingElseWouldNeedReview()
    {
        var outcome = Decide(Output(type: DocumentTypes.Ganancias, cuit: LegajoTestData.OtherCuitDigits,
                                    composite: true, confidence: 0.1m));

        Assert.Equal(LegajoStatuses.Blocked, outcome.Status);
        Assert.Equal([LegajoClassificationOutcome.ReasonCuitMismatch], outcome.ReviewReasons);
        Assert.False(outcome.HolderCuitMatches);
        Assert.Equal("27-11223344-5", outcome.HolderCuit);
    }

    [Fact]
    public void MissingCuit_NeedsReview()
    {
        var outcome = Decide(Output(cuit: null));

        Assert.Equal(LegajoStatuses.NeedsReview, outcome.Status);
        Assert.Equal([LegajoClassificationOutcome.ReasonCuitMissing], outcome.ReviewReasons);
        Assert.Null(outcome.HolderCuitMatches);
        Assert.Null(outcome.HolderCuit);
    }

    [Fact]
    public void CuitWithAWrongCheckDigit_IsAMisread_NeedsReview_NotBlocked()
    {
        var outcome = Decide(Output(cuit: LegajoTestData.InvalidCuitDigits));

        Assert.Equal(LegajoStatuses.NeedsReview, outcome.Status);
        Assert.Equal([LegajoClassificationOutcome.ReasonCuitInvalid], outcome.ReviewReasons);
        Assert.Null(outcome.HolderCuit);
    }

    [Fact]
    public void TypeMismatch_NeedsReview()
    {
        var outcome = Decide(Output(type: DocumentTypes.Ganancias));

        Assert.Equal(LegajoStatuses.NeedsReview, outcome.Status);
        Assert.Equal([LegajoClassificationOutcome.ReasonTypeMismatch], outcome.ReviewReasons);
    }

    [Fact]
    public void Composite_NeedsReview()
    {
        var outcome = Decide(Output(composite: true));

        Assert.Equal([LegajoClassificationOutcome.ReasonComposite], outcome.ReviewReasons);
        Assert.Equal(LegajoStatuses.NeedsReview, outcome.Status);
    }

    [Fact]
    public void LowConfidence_NeedsReview()
    {
        var outcome = Decide(Output(confidence: 0.69m));

        Assert.Equal([LegajoClassificationOutcome.ReasonLowConfidence], outcome.ReviewReasons);
    }

    [Fact]
    public void EveryApplicableReason_IsListed()
    {
        var outcome = Decide(Output(type: LegajoClassificationPrompt.UnknownType, cuit: null, composite: true, confidence: 0.2m));

        Assert.Equal(LegajoStatuses.NeedsReview, outcome.Status);
        Assert.Equal(
            [
                LegajoClassificationOutcome.ReasonCuitMissing,
                LegajoClassificationOutcome.ReasonTypeMismatch,
                LegajoClassificationOutcome.ReasonComposite,
                LegajoClassificationOutcome.ReasonLowConfidence
            ],
            outcome.ReviewReasons);
    }

    [Fact]
    public void Comparison_IsByDigits_WhateverTheFormat()
    {
        var outcome = LegajoClassificationOutcome.Decide(
            Output(cuit: "20-12345678-6"), DocumentTypes.Monotributo, LegajoTestData.LegajoCuitDigits, Threshold);

        Assert.True(outcome.HolderCuitMatches);
        Assert.Equal(LegajoStatuses.ReadyForExtraction, outcome.Status);
    }

    [Theory]
    [InlineData("20123456786", true)]
    [InlineData("27112233445", true)]
    [InlineData("20123456780", false)]
    [InlineData("2012345678", false)]
    [InlineData(null, false)]
    public void LegajoCuit_CheckDigit(string? digits, bool valid)
    {
        Assert.Equal(valid, LegajoCuit.HasValidCheckDigit(digits));
    }
}
