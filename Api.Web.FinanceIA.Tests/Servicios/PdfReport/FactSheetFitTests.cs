// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-08

using Api.Web.FinanceIA.Servicios.Pdf;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios.PdfReport;

/// <summary>The first page degrades in a fixed order and never drops the score.</summary>
public sealed class FactSheetFitTests
{
    // Fixed 500 + attention 100 (60 trimmed) + vigencia 60 + comparación 40 = 700.
    private static FactSheetFit.Heights Heights(float? both = null) => new()
    {
        Fixed            = 500f,
        AttentionFull    = 100f,
        AttentionTrimmed = 60f,
        Vigencia         = 60f,
        Comparacion      = 40f,
        Both             = both,
    };

    [Fact]
    public void EverythingFits_EverythingRenders()
    {
        Assert.Equal(new FactSheetFit.Plan(true, true, false, true), FactSheetFit.Choose(Heights(), 700f));
    }

    [Fact]
    public void FirstDropsVigencia()
    {
        Assert.Equal(new FactSheetFit.Plan(false, true, false, true), FactSheetFit.Choose(Heights(), 699f));
    }

    [Fact]
    public void ThenDropsComparacion()
    {
        Assert.Equal(new FactSheetFit.Plan(false, false, false, true), FactSheetFit.Choose(Heights(), 639f));
    }

    [Fact]
    public void ThenTrimsTheAttentionPointsToThree()
    {
        Assert.Equal(new FactSheetFit.Plan(false, false, true, true), FactSheetFit.Choose(Heights(), 599f));
    }

    [Fact]
    public void NeverDropsTheFixedBlocks_EvenWhenNothingFits()
    {
        FactSheetFit.Plan plan = FactSheetFit.Choose(Heights(), 400f);

        // Only the optional items move; the identity, strip, score card and composition always render, and
        // the caller logs the overflow.
        Assert.Equal(new FactSheetFit.Plan(false, false, true, false), plan);
    }

    [Fact]
    public void SideBySide_IsMeasuredAsOneRow()
    {
        // Side by side, vigencia and comparación take 60, not 100: everything fits in 660.
        Assert.Equal(new FactSheetFit.Plan(true, true, false, true), FactSheetFit.Choose(Heights(both: 60f), 660f));
        Assert.Equal(new FactSheetFit.Plan(false, true, false, true), FactSheetFit.Choose(Heights(both: 60f), 659f));
    }
}
