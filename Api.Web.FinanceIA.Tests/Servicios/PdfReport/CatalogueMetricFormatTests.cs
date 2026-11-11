// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-09

using Api.Web.FinanceIA.Servicios.Pdf;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios.PdfReport;

/// <summary>
/// The PDF prints a catalogue figure exactly as the Portal's <c>formatMetricValue</c> does: its own test cases
/// (<c>apps/portal/src/lib/analysis-alerts.test.ts</c>) verbatim, then edge cases whose expected text was produced by
/// <c>Intl.NumberFormat('es-AR', …)</c> in Node 22 (ICU 78), the formatter the Portal runs.
/// </summary>
public sealed class CatalogueMetricFormatTests
{
    // ── The Portal's cases ──────────────────────────────────────────────────

    [Fact]
    public void AProportionTheWireDeclared_RendersAsAPercentage_ForAKeyThisBuildHasNeverSeen()
    {
        Assert.Equal("55,4 %", CatalogueMetricFormat.Format("some_future_ratio", "proportion", 0.5543));
    }

    [Fact]
    public void AnAbsoluteWireUnit_WinsOverARegistryEntryThatSaysProportion()
    {
        Assert.Equal("1.500", CatalogueMetricFormat.Format("inventory_over_current_assets", "absolute", 1500));
    }

    [Fact]
    public void AnUnknownWireToken_RendersPlain_NeverScaled()
    {
        Assert.Equal("0,5543", CatalogueMetricFormat.Format("inventory_over_current_assets", "furlongs", 0.5543));
    }

    [Theory]
    [InlineData(0.5543, "55,4 %")]
    [InlineData(0.5, "50,0 %")]
    public void ARowStoredBeforeMetricUnit_FallsBackToTheRegistry(double value, string expected)
    {
        Assert.Equal(expected, CatalogueMetricFormat.Format("inventory_over_current_assets", null, value));
    }

    [Theory]
    [InlineData(0.6123, "0,6123")]
    [InlineData(1500, "1.500")]
    public void AKeyUnknownToBothSources_RendersPlain_NeverScaled(double value, string expected)
    {
        Assert.Equal(expected, CatalogueMetricFormat.Format("some_future_absolute_metric", null, value));
    }

    [Fact]
    public void NoKeyAndNoUnit_RendersPlain()
    {
        Assert.Equal("0,5", CatalogueMetricFormat.Format(null, null, 0.5));
    }

    [Fact]
    public void ANullValue_IsNull_SoTheCallerOmitsIt()
    {
        Assert.Null(CatalogueMetricFormat.Format("inventory_over_current_assets", "proportion", null));
    }

    // ── Intl edge cases (Node 22, es-AR) ────────────────────────────────────

    [Theory]
    [InlineData(0.41837, "41,8 %")]
    [InlineData(0.4, "40,0 %")]
    [InlineData(0.00049, "0,0 %")]
    [InlineData(0.00051, "0,1 %")]
    [InlineData(12.3456, "1.234,6 %")]
    [InlineData(-0.0123, "-1,2 %")]
    [InlineData(-0.0001, "-0,0 %")]
    [InlineData(0.12345, "12,3 %")]
    [InlineData(0.00125, "0,1 %")]
    public void Proportions(double value, string expected)
    {
        Assert.Equal(expected, CatalogueMetricFormat.Format(null, "proportion", value));
    }

    [Theory]
    [InlineData(1234567.891234, "1.234.567,8912")]
    [InlineData(-1500, "-1.500")]
    [InlineData(0, "0")]
    [InlineData(9999, "9.999")]
    [InlineData(10000, "10.000")]
    [InlineData(0.00005, "0,0001")]
    [InlineData(0.123456, "0,1235")]
    [InlineData(0.125, "0,125")]
    [InlineData(2.5, "2,5")]
    [InlineData(1.00005, "1,0001")]
    [InlineData(-0.00001, "-0")]
    [InlineData(123456789012.5, "123.456.789.012,5")]
    public void PlainFigures(double value, string expected)
    {
        Assert.Equal(expected, CatalogueMetricFormat.Format(null, "absolute", value));
    }

    // ── The line the PDF prints ─────────────────────────────────────────────

    [Fact]
    public void TheMetricLine_NamesBothFigures()
    {
        var alert = new CatalogueAlertChannel.Item
        {
            Description = "d", MetricKey = "liabilities_over_assets", MetricUnit = "proportion", MetricValue = 0.4184, ThresholdValue = 0.4,
        };

        Assert.Equal("Medido: 41,8 % · Umbral: 40,0 %", CatalogueMetricFormat.MetricLine(alert));
    }

    [Fact]
    public void TheMetricLine_OmitsAMissingHalf_AndIsNullWithNeither()
    {
        Assert.Equal("Medido: 1.500.000",
            CatalogueMetricFormat.MetricLine(new CatalogueAlertChannel.Item { Description = "d", MetricUnit = "absolute", MetricValue = 1_500_000 }));
        Assert.Equal("Umbral: 0,5",
            CatalogueMetricFormat.MetricLine(new CatalogueAlertChannel.Item { Description = "d", ThresholdValue = 0.5 }));
        Assert.Null(CatalogueMetricFormat.MetricLine(new CatalogueAlertChannel.Item { Description = "d" }));
    }

    // ── The page-1 bullet figure (QA Q47) ───────────────────────────────────

    [Fact]
    public void Inline_PrintsTheMeasuredValue_AndItsThreshold()
    {
        var alert = new CatalogueAlertChannel.Item
        {
            Description = "d", MetricKey = "liabilities_over_assets", MetricUnit = "proportion", MetricValue = 0.844, ThresholdValue = 0.5,
        };

        Assert.Equal("84,4 % (umbral 50,0 %)", CatalogueMetricFormat.Inline(alert));
    }

    [Fact]
    public void Inline_WithoutAThreshold_IsTheMeasuredValueAlone()
    {
        Assert.Equal("1.500", CatalogueMetricFormat.Inline(
            new CatalogueAlertChannel.Item { Description = "d", MetricUnit = "absolute", MetricValue = 1500 }));
    }

    [Fact]
    public void Inline_IsNull_WithoutAMeasuredValue_EvenWithAThreshold()
    {
        Assert.Null(CatalogueMetricFormat.Inline(new CatalogueAlertChannel.Item { Description = "d", MetricUnit = "proportion", ThresholdValue = 0.5 }));
        Assert.Null(CatalogueMetricFormat.Inline(new CatalogueAlertChannel.Item { Description = "d" }));
    }
}
