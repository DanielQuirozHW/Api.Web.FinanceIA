// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-02

using System.Text.Json;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Lufe;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Api.Web.FinanceIA.Tests.AnalysisRuns;

/// <summary>
/// The pipeline and the re-score build the reliability facts through the SAME builder, from the same
/// inputs, so they record the same facts (score credibility 1b-2a). End to end over the in-memory
/// database: the facts are read back from the persisted <c>ScoreBreakdownJson</c>.
/// </summary>
public sealed class ScoreReliabilityFactsPipelineTests
{
    private readonly RescoreTestWorld _world = new();

    private async Task<Entity> SeedRichEntityAsync()
    {
        var entity = await _world.SeedEntityAsync();

        var lufe = new LufeData
        {
            VentasComprasMensual =
            [
                new LufeVentaCompraMes { Periodo = "202607", Ventas = 100m },
                new LufeVentaCompraMes { Periodo = "202608", Ventas = 110m },
            ],
            VentasComprasMensualEstado = LufeVentasComprasEstados.CarriedForward,
            VentasComprasMensualAsOf   = RescoreTestWorld.Utc(2026, 8, 15),
        };
        var casfog = new CasfogData { Estado = CasfogConsultaEstado.DataFound, FetchedAt = RescoreTestWorld.Utc(2026, 9, 2) };

        await using var db = _world.NewContext();
        var live = await db.Entities.SingleAsync(e => e.EntityId == entity.EntityId);
        live.Sector         = "Agropecuario";
        live.LufeDataJson   = JsonSerializer.Serialize(lufe);
        live.CasfogDataJson = JsonSerializer.Serialize(casfog);
        db.ChangeTracker.DetectChanges();
        await db.SaveChangesAsync();

        return live;
    }

    private static JsonElement Facts(AnalysisResult result)
        => JsonDocument.Parse(result.ScoreBreakdownJson!).RootElement.GetProperty("reliabilityFacts").Clone();

    [Fact]
    public async Task Pipeline_RecordsTheFacts_FromWhatItAlreadyHeld()
    {
        var entity = await SeedRichEntityAsync();

        var fresh = await _world.RunPipelineAsync(entity);
        var facts = Facts(fresh);

        Assert.Equal(6, facts.GetProperty("version").GetInt32());  // facts version 6 added netProfitColumnMix
        Assert.Equal("computed", facts.GetProperty("ratiosSource").GetString());
        Assert.Equal(2024, facts.GetProperty("fiscalYear").GetInt32());
        Assert.True(facts.GetProperty("sectorFallback").GetBoolean());
        Assert.Equal("Agropecuario", facts.GetProperty("sectorRaw").GetString());
        Assert.Equal("defaults", facts.GetProperty("configSource").GetString());
        Assert.False(facts.GetProperty("negativeEquity").GetBoolean());

        var bcra = facts.GetProperty("bcra");
        Assert.Equal("DebtFound", bcra.GetProperty("deudaEstado").GetString());
        Assert.Equal("202609", bcra.GetProperty("periodoConsultado").GetString());
        Assert.True(bcra.GetProperty("chequesEvaluated").GetBoolean());

        var lufe = facts.GetProperty("lufe");
        Assert.Equal("carried_forward", lufe.GetProperty("ventasEstado").GetString());
        Assert.Equal("202608", lufe.GetProperty("ventasUltimoPeriodo").GetString());
        Assert.Equal(entity.LufeDataUpdatedAt, lufe.GetProperty("snapshotAt").GetDateTime());

        Assert.Equal("DataFound", facts.GetProperty("casfog").GetProperty("estado").GetString());

        // The world's balance predates prompt 2.18: no integrity verdict, so the member is omitted.
        Assert.False(facts.TryGetProperty("integrity", out _));

        // No identifying data from the entity or the BCRA payload.
        var raw = facts.GetRawText();
        Assert.DoesNotContain(RescoreTestWorld.Cuit, raw);
        Assert.DoesNotContain(entity.BusinessName!, raw);
    }

    [Fact]
    public async Task Rescore_OfTheSameInputs_RecordsTheIdenticalFacts()
    {
        var entity = await SeedRichEntityAsync();
        var fresh  = await _world.RunPipelineAsync(entity);

        var outcome = await _world.Service().RescoreAsync(
            fresh.ResultId, RescoreTestWorld.Tenant, _world.UserId, "10.0.0.1", "trace-1", CancellationToken.None);

        Assert.True(outcome.IsSuccess);
        var rescored = (await _world.ReadResultAsync(fresh.ResultId))!;

        Assert.Equal(Facts(fresh).GetRawText(), Facts(rescored).GetRawText());
        Assert.Equal(fresh.AiSuggestedScore, rescored.AiSuggestedScore);
    }

    [Fact]
    public async Task Rescore_OfARowScoredBeforeTheFacts_AddsThem()
    {
        var entity = await _world.SeedEntityAsync();
        var (_, seeded, _) = await _world.SeedAnalysisAsync(entity);
        Assert.DoesNotContain("reliabilityFacts", seeded.ScoreBreakdownJson);

        var outcome = await _world.Service().RescoreAsync(
            seeded.ResultId, RescoreTestWorld.Tenant, _world.UserId, "10.0.0.1", "trace-1", CancellationToken.None);

        Assert.True(outcome.IsSuccess);
        var facts = Facts((await _world.ReadResultAsync(seeded.ResultId))!);
        Assert.Equal(2024, facts.GetProperty("fiscalYear").GetInt32());
        Assert.Equal("computed", facts.GetProperty("ratiosSource").GetString());
    }
}
