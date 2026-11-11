// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-09

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// QA Q48: which section a statement printed its financial placements in. Cases 1–5 are the SAME numbers as the
/// Portal's PR #452 — keep them identical so the screen and the report agree.
/// </summary>
public sealed class CashFlowPlacementSectionTests
{
    private static CashFlowData Flow(
        double? investing, double? financing, CashFlowComponents components,
        double? recpam = null, string? recpamSection = null) => new()
    {
        Present                     = true,
        Investing                   = investing,
        Financing                   = financing,
        Components                  = components,
        MonetaryResultOnCash        = recpam,
        MonetaryResultOnCashSection = recpamSection,
    };

    // The shared case by number (1–5), as the Portal's test names them.
    private static CashFlowData SharedCase(int number) => number switch
    {
        // Placements printed in Financing.
        1 => Flow(-1000, -300, new CashFlowComponents { Capex = -1000, ShortTermDebtRaised = 200, FinancialPlacements = -500 }),
        // Placements in Investing: every section reconciles as read.
        2 => Flow(-1500, -300, new CashFlowComponents { Capex = -1000, FinancialPlacements = -500, ShortTermDebtRepaid = -300 }),
        // Neither reading reconciles both sections.
        3 => Flow(-1200, -300, new CashFlowComponents { Capex = -1000, FinancialPlacements = -500, ShortTermDebtRaised = 200 }),
        // No financing subtotal.
        4 => Flow(-1000, null, new CashFlowComponents { Capex = -1000, FinancialPlacements = -500 }),
        // A redemption printed in Financing.
        5 => Flow(-1000, 700, new CashFlowComponents { Capex = -1000, ShortTermDebtRaised = 200, FinancialPlacementsRedeemed = 500 }),
        _ => throw new ArgumentOutOfRangeException(nameof(number)),
    };

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    public void SharedCases_MatchThePortal(int number, bool financing)
    {
        Assert.Equal(
            financing ? CashFlowPlacementSide.Financing : CashFlowPlacementSide.Investing,
            CashFlowPlacementSection.Resolve(SharedCase(number)));
    }

    [Fact]
    public void TheRecpamOnCash_AttributedToFinancing_CountsInFinancingsResidual()
    {
        // B: financing −250 − (200 − 500 + 50) = 0 and investing 0; A: financing −250 − (200 + 50) = −500.
        CashFlowData cashFlow = Flow(
            -1000, -250,
            new CashFlowComponents { Capex = -1000, ShortTermDebtRaised = 200, FinancialPlacements = -500 },
            recpam: 50, recpamSection: "financing");

        Assert.Equal(CashFlowPlacementSide.Financing, CashFlowPlacementSection.Resolve(cashFlow));
    }

    [Fact]
    public void TheRecpamOnCash_OutsideTheSections_CountsInNeither()
    {
        // Same as case 1, with a RECPAM figure placed outside the sections: the decision does not move.
        CashFlowData cashFlow = Flow(
            -1000, -300,
            new CashFlowComponents { Capex = -1000, ShortTermDebtRaised = 200, FinancialPlacements = -500 },
            recpam: 50, recpamSection: "outside");

        Assert.Equal(CashFlowPlacementSide.Financing, CashFlowPlacementSection.Resolve(cashFlow));
    }

    [Fact]
    public void NoPlacements_OrPlacementsThatNetToZero_StayInInvesting()
    {
        Assert.Equal(CashFlowPlacementSide.Investing, CashFlowPlacementSection.Resolve(
            Flow(-1000, -300, new CashFlowComponents { Capex = -1000, ShortTermDebtRaised = 200 })));
        Assert.Equal(CashFlowPlacementSide.Investing, CashFlowPlacementSection.Resolve(
            Flow(-1000, -300, new CashFlowComponents { Capex = -1000, FinancialPlacements = -500, FinancialPlacementsRedeemed = 500 })));
    }

    [Fact]
    public void NoInvestingSubtotal_StaysInInvesting()
    {
        Assert.Equal(CashFlowPlacementSide.Investing, CashFlowPlacementSection.Resolve(
            Flow(null, -300, new CashFlowComponents { ShortTermDebtRaised = 200, FinancialPlacements = -500 })));
    }

    [Fact]
    public void AResidualUnderOnePeso_CountsAsNone()
    {
        // Case 1 with half a peso of noise in each subtotal: still Financing.
        Assert.Equal(CashFlowPlacementSide.Financing, CashFlowPlacementSection.Resolve(
            Flow(-1000.4, -300.4, new CashFlowComponents { Capex = -1000, ShortTermDebtRaised = 200, FinancialPlacements = -500 })));
    }
}
