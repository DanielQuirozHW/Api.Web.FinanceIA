// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-02

using Api.Web.FinanceIA.Servicios;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Fiscal year read off the document vs the one requested. Convention: a fiscal year is its CLOSING
/// year (31/08/2025 → 2025), with no ±1 tolerance; a document one year ahead — a non-calendar
/// ejercicio labelled with its starting year — gets a hint, not a pass.
/// </summary>
public sealed class FiscalYearCheckTests
{
    [Theory]
    [InlineData(2024, 2024, FiscalYearCheck.Match)]
    [InlineData(2024, 2023, FiscalYearCheck.Mismatch)]
    [InlineData(2024, 2025, FiscalYearCheck.Mismatch)]   // non-calendar close labelled by its starting year
    [InlineData(2024, null, FiscalYearCheck.NotDetected)]
    public void Evaluate(int requested, int? detected, string expected)
        => Assert.Equal(expected, FiscalYearCheck.Evaluate(requested, detected));

    [Fact]
    public void A_document_a_year_behind_gets_the_plain_anomaly()
        => Assert.Equal(
            "El ejercicio detectado en el documento (2023) no coincide con el ejercicio del análisis (2024).",
            FiscalYearCheck.Anomaly(2024, 2023));

    [Fact]
    public void A_document_a_year_ahead_gets_the_closing_year_hint()
        => Assert.Equal(
            "El ejercicio detectado en el documento (2025) no coincide con el ejercicio del análisis (2024). " +
            "Si el ejercicio no cierra en diciembre, el ejercicio del análisis se identifica por el año de cierre.",
            FiscalYearCheck.Anomaly(2024, 2025));

    [Fact]
    public void Two_years_apart_gets_no_hint()
        => Assert.DoesNotContain("año de cierre", FiscalYearCheck.Anomaly(2024, 2026));

    [Theory]
    [InlineData(2024)]
    [InlineData(null)]
    public void Match_or_not_detected_has_no_anomaly(int? detected)
        => Assert.Null(FiscalYearCheck.Anomaly(2024, detected));

    [Fact]
    public void The_anomaly_never_carries_a_percent_sign_so_the_narrative_reconciler_leaves_it_alone()
        => Assert.DoesNotContain("%", FiscalYearCheck.Anomaly(2024, 2025));
}
