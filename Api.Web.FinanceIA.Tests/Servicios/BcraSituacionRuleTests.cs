// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-23

using System.Text.Json;
using Api.Web.FinanceIA.Data;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// <see cref="BcraSituacionRule"/>: the scoring engine's reading of a BCRA payload, now shared with the
/// dashboard. The engine's own suites (ScoringServiceBcraTests, ScoringServiceTests) pin that scores did
/// not move; these pin the reading, and that the JSON_VALUE path reads a stored blob exactly as the object
/// path reads the payload it was serialized from.
/// </summary>
public sealed class BcraSituacionRuleTests
{
    [Fact]
    public void Read_FollowsTheEngineGuardOrder()
    {
        Assert.Equal(new BcraDeudaReading(BcraDeudaReadingKind.Excluded, BcraExclusionReasons.NoBcraData, null),
            BcraSituacionRule.Read((BcraData?)null));
        Assert.Equal(new BcraDeudaReading(BcraDeudaReadingKind.Excluded, BcraExclusionReasons.NoDeudaBlock, null),
            BcraSituacionRule.Read(new BcraData()));
        Assert.Equal(new BcraDeudaReading(BcraDeudaReadingKind.Excluded, BcraExclusionReasons.LookupFailed, null),
            BcraSituacionRule.Read(Payload(BcraDeudaConsultaEstado.LookupFailed, 5)));
        Assert.Equal(new BcraDeudaReading(BcraDeudaReadingKind.NoDebt, null, null),
            BcraSituacionRule.Read(Payload(BcraDeudaConsultaEstado.NoDebtReported, 4)));
        Assert.Equal(new BcraDeudaReading(BcraDeudaReadingKind.Debt, null, 3),
            BcraSituacionRule.Read(Payload(BcraDeudaConsultaEstado.DebtFound, 3)));
    }

    [Theory]
    [InlineData(BcraDeudaConsultaEstado.DebtFound, 1, 1)]
    [InlineData(BcraDeudaConsultaEstado.DebtFound, 6, 6)]
    [InlineData(BcraDeudaConsultaEstado.DebtFound, 0, null)]    // the engine scores it neutral, not as a situación
    [InlineData(BcraDeudaConsultaEstado.DebtFound, 7, null)]
    [InlineData(BcraDeudaConsultaEstado.DebtFound, null, null)]
    [InlineData(BcraDeudaConsultaEstado.NoDebtReported, null, 1)] // "equivalent to situación 1"
    [InlineData(BcraDeudaConsultaEstado.NoDebtReported, 5, 1)]    // history and stale fields are ignored
    [InlineData(BcraDeudaConsultaEstado.LookupFailed, 2, null)]
    public void Worst_IsTheStoredMaximum_OrOneForNoDebt_OrNothing(BcraDeudaConsultaEstado estado, int? situacion, int? expected)
    {
        Assert.Equal(expected, BcraSituacionRule.Worst(BcraSituacionRule.Read(Payload(estado, situacion))));
    }

    [Theory]
    [InlineData(BcraDeudaConsultaEstado.DebtFound, 4)]
    [InlineData(BcraDeudaConsultaEstado.DebtFound, null)]
    [InlineData(BcraDeudaConsultaEstado.NoDebtReported, null)]
    [InlineData(BcraDeudaConsultaEstado.LookupFailed, 3)]
    public void Read_OfTheJsonValueScalars_EqualsRead_OfThePayloadTheyCameFrom(BcraDeudaConsultaEstado estado, int? situacion)
    {
        var payload = Payload(estado, situacion);
        // Serialized exactly as every writer does — no options — and read back with the dashboard's paths.
        var json = JsonSerializer.Serialize(payload);

        var fromText = BcraSituacionRule.Read(
            SqlJsonFunctions.JsonValue(json, "$.deuda.estado"),
            SqlJsonFunctions.JsonValue(json, "$.deuda.situacionMaxima"));

        Assert.Equal(BcraSituacionRule.Read(payload), fromText);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "3")]
    [InlineData("x", "3")]
    [InlineData("9", "3")]   // not an estado the enum knows
    public void Read_OfScalars_WithoutAUsableEstado_IsExcludedAsNoDeudaBlock(string? estado, string? situacion)
    {
        var reading = BcraSituacionRule.Read(estado, situacion);

        Assert.Equal(BcraDeudaReadingKind.Excluded, reading.Kind);
        Assert.Equal(BcraExclusionReasons.NoDeudaBlock, reading.ExclusionReason);
        Assert.Null(BcraSituacionRule.Worst(reading));
    }

    [Fact]
    public void Read_OfAnEntityWithNoSnapshot_IsExcluded()
    {
        Assert.Null(BcraSituacionRule.Worst(BcraSituacionRule.Read(
            SqlJsonFunctions.JsonValue(null, "$.deuda.estado"),
            SqlJsonFunctions.JsonValue(null, "$.deuda.situacionMaxima"))));
    }

    private static BcraData Payload(BcraDeudaConsultaEstado estado, int? situacionMaxima)
        => new() { Deuda = new BcraDeudaData { Estado = estado, SituacionMaxima = situacionMaxima } };
}
