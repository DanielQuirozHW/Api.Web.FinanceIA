// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-09

using System.Text.Json;
using Api.Web.FinanceIA.Servicios.Pdf;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios.PdfReport;

/// <summary>
/// The PDF reads the catalogue channel exactly as the Portal does: every case of
/// <c>apps/portal/src/lib/analysis-alerts.test.ts</c> (<c>parseCatalogueAlerts</c>), ported.
/// </summary>
public sealed class CatalogueAlertChannelTests
{
    private static readonly Dictionary<string, object?> InventoryAlert = new()
    {
        ["level"]           = "medium",
        ["code"]            = "INVENTARIO_ELEVADO",
        ["title"]           = "Participación elevada de bienes de cambio",
        ["description"]     = "Los bienes de cambio representan el 61,2 % del activo corriente, por encima del umbral configurado de 50,0 %.",
        ["metric_key"]      = "inventory_over_current_assets",
        ["metric_value"]    = 0.6123,
        ["threshold_value"] = 0.5,
        ["metric_unit"]     = "proportion",
    };

    private static readonly Dictionary<string, object?> StructuralAlert = new()
    {
        ["level"]           = "high",
        ["code"]            = "PASIVO_100_CORRIENTE",
        ["title"]           = "Pasivo íntegramente corriente",
        ["description"]     = "La totalidad del pasivo es corriente: no se informa pasivo no corriente.",
        ["metric_key"]      = null,
        ["metric_value"]    = null,
        ["threshold_value"] = null,
        ["metric_unit"]     = null,
    };

    private static Dictionary<string, object?> With(Dictionary<string, object?> element, string key, object? value)
    {
        var copy = new Dictionary<string, object?>(element) { [key] = value };
        return copy;
    }

    private static Dictionary<string, object?> Without(Dictionary<string, object?> element, string key)
    {
        var copy = new Dictionary<string, object?>(element);
        copy.Remove(key);
        return copy;
    }

    private static string Json(params object[] elements) => JsonSerializer.Serialize(elements);

    // ── Channel states ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    public void NullBlankOrTheLiteralNull_IsAbsent(string? raw)
    {
        CatalogueAlertChannel.Result result = CatalogueAlertChannel.Parse(raw);

        Assert.Equal(AlertChannelState.Absent, result.State);
        Assert.Empty(result.Alerts);
    }

    [Theory]
    [InlineData("[{\"code\": ")]
    [InlineData("42")]
    [InlineData("{\"code\":\"X\"}")]
    public void MalformedJsonOrANonArray_IsUnreadable_NeverThrows(string raw)
    {
        CatalogueAlertChannel.Result result = CatalogueAlertChannel.Parse(raw);

        Assert.Equal(AlertChannelState.Unreadable, result.State);
        Assert.Empty(result.Alerts);
    }

    [Fact]
    public void AnEmptyArray_IsReady()
    {
        CatalogueAlertChannel.Result result = CatalogueAlertChannel.Parse("[]");

        Assert.Equal(AlertChannelState.Ready, result.State);
        Assert.Empty(result.Alerts);
    }

    [Fact]
    public void ANonEmptyPayload_WithNoUsableRow_IsUnreadable()
    {
        Assert.Equal(AlertChannelState.Unreadable, CatalogueAlertChannel.Parse("""[{"foo":1},{"bar":2}]""").State);
    }

    [Fact]
    public void UsableSiblings_AreKept_WhenOnlySomeElementsAreUnusable()
    {
        CatalogueAlertChannel.Result result = CatalogueAlertChannel.Parse(Json(InventoryAlert, new Dictionary<string, int> { ["foo"] = 1 }, 7));

        Assert.Equal(AlertChannelState.Ready, result.State);
        Assert.Single(result.Alerts);
        Assert.Equal(3, result.Received);
        Assert.Equal(2, result.Dropped);
    }

    // ── Element mapping ─────────────────────────────────────────────────────

    [Fact]
    public void TheInventoryRule_KeepsItsFiguresAsRawProportions()
    {
        CatalogueAlertChannel.Item alert = Assert.Single(CatalogueAlertChannel.Parse(Json(InventoryAlert)).Alerts);

        Assert.Equal("medium", alert.Level);
        Assert.Equal("INVENTARIO_ELEVADO", alert.Code);
        Assert.Equal("Participación elevada de bienes de cambio", alert.Title);
        Assert.Equal((string)InventoryAlert["description"]!, alert.Description);
        Assert.Equal("inventory_over_current_assets", alert.MetricKey);
        Assert.Equal(0.6123, alert.MetricValue);
        Assert.Equal(0.5, alert.ThresholdValue);
        Assert.Equal("proportion", alert.MetricUnit);
    }

    [Fact]
    public void AStructuralRule_HasEveryMetricFieldNull()
    {
        CatalogueAlertChannel.Item alert = Assert.Single(CatalogueAlertChannel.Parse(Json(StructuralAlert)).Alerts);

        Assert.Equal("High", Severity(alert));
        Assert.Null(alert.MetricKey);
        Assert.Null(alert.MetricValue);
        Assert.Null(alert.ThresholdValue);
        Assert.Null(alert.MetricUnit);
    }

    [Fact]
    public void ARowStoredBeforeMetricUnit_HasANullUnit_AndKeepsItsKey()
    {
        CatalogueAlertChannel.Item alert = Assert.Single(CatalogueAlertChannel.Parse(Json(Without(InventoryAlert, "metric_unit"))).Alerts);

        Assert.Null(alert.MetricUnit);
        Assert.Equal("inventory_over_current_assets", alert.MetricKey);
    }

    [Fact]
    public void AStringifiedNumber_IsNotCoercedIntoAFigure()
    {
        CatalogueAlertChannel.Item alert = Assert.Single(CatalogueAlertChannel.Parse(Json(With(InventoryAlert, "metric_value", "0.6123"))).Alerts);

        Assert.Null(alert.MetricValue);
    }

    [Fact]
    public void AZeroMetric_IsARealFigure()
    {
        CatalogueAlertChannel.Item alert = Assert.Single(CatalogueAlertChannel.Parse(Json(With(InventoryAlert, "metric_value", 0))).Alerts);

        Assert.Equal(0d, alert.MetricValue);
    }

    [Theory]
    [InlineData("critical", "Critical")]
    [InlineData("high", "High")]
    [InlineData("medium", "Medium")]
    [InlineData("low", "Low")]
    public void TheLowercaseLevel_MapsToItsSeverity(string wire, string expected)
    {
        CatalogueAlertChannel.Item alert = Assert.Single(CatalogueAlertChannel.Parse(Json(With(StructuralAlert, "level", wire))).Alerts);

        Assert.Equal(expected, Severity(alert));
    }

    [Theory]
    [InlineData("an unknown string")]
    [InlineData("a missing level")]
    [InlineData("a non-string")]
    public void AnUnusableLevel_DegradesToLow(string label)
    {
        Dictionary<string, object?> element = label switch
        {
            "an unknown string" => With(StructuralAlert, "level", "severe"),
            "a missing level"   => Without(StructuralAlert, "level"),
            _                   => With(StructuralAlert, "level", 7),
        };

        CatalogueAlertChannel.Item alert = Assert.Single(CatalogueAlertChannel.Parse(Json(element)).Alerts);

        Assert.Equal("Low", Severity(alert));
    }

    [Fact]
    public void AnElementWithoutDescription_IsDropped_OneWithoutCode_IsKept()
    {
        string raw = """[{"level":"high","code":"NO_DESCRIPTION"},{"level":"high","description":"Sin código, pero con hallazgo."}]""";

        CatalogueAlertChannel.Item alert = Assert.Single(CatalogueAlertChannel.Parse(raw).Alerts);

        Assert.Equal(string.Empty, alert.Code);
        Assert.Equal("Sin código, pero con hallazgo.", alert.Description);
    }

    [Fact]
    public void ABlankTitle_IsAbsent()
    {
        CatalogueAlertChannel.Item alert = Assert.Single(CatalogueAlertChannel.Parse(Json(With(StructuralAlert, "title", "   "))).Alerts);

        Assert.Null(alert.Title);
    }

    // The Portal's RiskLevel, from the PDF's severity rank (PdfLabels.AlertSeverity is the PDF's severityFromWire).
    private static string Severity(CatalogueAlertChannel.Item alert) => PdfLabels.AlertSeverity(alert.Level).Rank switch
    {
        4 => "Critical",
        3 => "High",
        2 => "Medium",
        _ => "Low",
    };
}
