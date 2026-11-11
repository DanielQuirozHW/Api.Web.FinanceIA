// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-24

using System.Text.Json;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Models;

/// <summary>
/// Pins <see cref="BalanceAccounts.InPrintedUnits"/>: a balance "expresado en miles" persists pesos,
/// but its narrative quotes printed amounts, so the percentage reconciler must see printed figures
/// or it would rewrite a correct percentage by the scale.
/// </summary>
public sealed class BalanceAccountsScaleTests
{
    private const string ThousandsBalanceJson =
        """{"assets": 12239430766710, "liabilities": 4462924519890, "equity": 7776506246820, "amounts_unit": "miles", "amounts_scale": 1000}""";

    private const string CorrectNarrative =
        "Los bienes de cambio ascienden a $6.236.534.182,74, representando el 51,0% del activo total.";

    [Fact]
    public void InPrintedUnits_DividesByTheAppliedScale()
    {
        var accounts = JsonSerializer.Deserialize<BalanceAccounts>(ThousandsBalanceJson)!;

        var printed = accounts.InPrintedUnits();

        Assert.Equal(12239430766.71, printed.TotalAssets!.Value, 4);
        Assert.Equal(4462924519.89, printed.TotalLiabilities!.Value, 4);
        Assert.Null(printed.CurrentAssets);
        Assert.Null(printed.AmountsScale);
    }

    [Fact]
    public void InPrintedUnits_WithoutAScale_IsTheSameInstance()
    {
        var accounts = JsonSerializer.Deserialize<BalanceAccounts>("""{"assets": 1000}""")!;

        Assert.Same(accounts, accounts.InPrintedUnits());
    }

    [Fact]
    public void Reconciler_OnPrintedUnits_LeavesACorrectPercentageAlone_ButOnPesosWouldRewriteIt()
    {
        var reconciler = new AlertRatioReconciliationService();
        var accounts   = JsonSerializer.Deserialize<BalanceAccounts>(ThousandsBalanceJson)!;

        var onPrinted = reconciler.ReconcileNarrative(CorrectNarrative, accounts.InPrintedUnits());
        var onPesos   = reconciler.ReconcileNarrative(CorrectNarrative, accounts);

        Assert.Equal(CorrectNarrative, onPrinted.Text);
        Assert.NotEqual(CorrectNarrative, onPesos.Text); // the regression InPrintedUnits prevents
    }

    [Fact]
    public void PriorYearShape_DoesNotEmitAmountsScale()
    {
        var json = JsonSerializer.Serialize(new BalanceAccounts { TotalAssets = 1d });

        Assert.DoesNotContain("amounts_scale", json);
    }
}
