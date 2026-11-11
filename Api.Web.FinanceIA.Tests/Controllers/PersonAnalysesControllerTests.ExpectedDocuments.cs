// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-31

using System.Text.Json;
using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Models.Settings;
using Api.Web.FinanceIA.Servicios.PersonEvaluation;
using Api.Web.FinanceIA.Tests.PersonAnalysis;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Controllers;

/// <summary>
/// Engine 0.10 through POST /api/person-analyses and "Reanalizar": the coverage measures against the tenant's
/// CURRENT expected-documents matrix and the sources freeze it; a settings fault never fails either.
/// </summary>
public sealed partial class PersonAnalysesControllerTests
{
    private static readonly DateTime MatrixSavedAt = new(2026, 10, 30, 15, 0, 0, DateTimeKind.Utc);

    /// <summary>A responsable inscripto matrix: the consent and Ganancias (24-month cut) required, DDJJ IVA optional.</summary>
    private static string LeanRiMatrix() => PersonExpectedDocumentsMatrix.Serialize(
        new Dictionary<string, List<PersonExpectedDocumentLineDto?>?>
        {
            [ExpectedDocumentRegimes.ResponsableInscripto] =
            [
                ExpectedDocumentsTestKit.Line(DocumentTypes.AutorizacionDatos),
                ExpectedDocumentsTestKit.Line(DocumentTypes.Ganancias, true, 24),
                ExpectedDocumentsTestKit.Line(DocumentTypes.DdjjIva, required: false)
            ]
        });

    private static (PersonEvaluationSources Sources, PersonCoverageResult Coverage) Frozen(AnalysisParticipant row)
        => (PersonEvaluationJson.TryDeserialize<PersonEvaluationSources>(row.DocumentVersionsJson)!,
            PersonEvaluationJson.TryDeserialize<PersonCoverageResult>(row.CoverageJson)!);

    [Fact]
    public async Task Create_WithoutATenantMatrix_MeasuresAndFreezesTheDefault()
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();

        AssertCreated(await h.Sut.CreateAsync(Request(entity.EntityId), CancellationToken.None));

        var (sources, coverage) = Frozen(Assert.Single(h.Db.AnalysisParticipants.AsNoTracking()));
        Assert.NotNull(sources.ExpectedDocuments);
        Assert.Equal(ExpectedDocumentsSources.Default, sources.ExpectedDocuments.Source);
        Assert.Equal(ExpectedDocumentRegimes.ResponsableInscripto, sources.ExpectedDocuments.Regime);
        Assert.Null(sources.ExpectedDocuments.UpdatedAt);
        Assert.Equal(LegajoExpectedDocuments.ResponsableInscripto.Count, sources.ExpectedDocuments.Lines.Count);
        Assert.Equal(7, coverage.RequiredTotal);
    }

    [Fact]
    public async Task Create_OnATenantMatrix_MeasuresAgainstIt_AndFreezesIt()
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();
        ExpectedDocumentsTestKit.Seed(h.Db, Tenant, LeanRiMatrix(), MatrixSavedAt);

        var created = AssertCreated(await h.Sut.CreateAsync(Request(entity.EntityId), CancellationToken.None));

        var (sources, coverage) = Frozen(Assert.Single(h.Db.AnalysisParticipants.AsNoTracking()));
        Assert.Equal(ExpectedDocumentsSources.Tenant, sources.ExpectedDocuments!.Source);
        Assert.Equal(MatrixSavedAt, sources.ExpectedDocuments.UpdatedAt);
        Assert.Equal(
            [DocumentTypes.AutorizacionDatos, DocumentTypes.Ganancias, DocumentTypes.DdjjIva],
            sources.ExpectedDocuments.Lines.Select(l => l.DocumentType));

        Assert.Equal(2, coverage.RequiredTotal);
        var ganancias = Assert.Single(coverage.Items, i => i.DocumentType == DocumentTypes.Ganancias);
        Assert.Equal(24, ganancias.MaxAgeMonths);
        Assert.True(ganancias.Present);
        Assert.DoesNotContain(coverage.Items, i => i.DocumentType == DocumentTypes.DdjjIibb);
        Assert.Equal(2, Assert.IsType<PersonCoverageResult>(created.PersonEvaluation!.Coverage).RequiredTotal);
    }

    [Fact]
    public async Task Create_OnACorruptMatrixRow_StillEvaluates_OnTheDefaults()
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();
        ExpectedDocumentsTestKit.Seed(h.Db, Tenant, "{ corrupt");

        AssertCreated(await h.Sut.CreateAsync(Request(entity.EntityId), CancellationToken.None));

        var (sources, coverage) = Frozen(Assert.Single(h.Db.AnalysisParticipants.AsNoTracking()));
        Assert.Equal(ExpectedDocumentsSources.Default, sources.ExpectedDocuments!.Source);
        Assert.Equal(7, coverage.RequiredTotal);
    }

    [Fact]
    public async Task Reevaluate_UsesTheCurrentMatrix_AndFreezesItAnew_WhileTheHistoryKeepsTheOldOne()
    {
        var h = new Harness();
        var (entity, _) = h.PersonWithReadyLegajo();
        var created = AssertCreated(await h.Sut.CreateAsync(Request(entity.EntityId), CancellationToken.None));
        var before  = Assert.Single(h.Db.AnalysisParticipants.AsNoTracking());

        // The SGR configures its matrix after the analysis was created.
        ExpectedDocumentsTestKit.Seed(h.Db, Tenant, LeanRiMatrix(), MatrixSavedAt);

        var outcome = await h.Rescore().RescoreAsync(created.ResultId, Tenant, h.Users.Analyst.UserId, null, null, CancellationToken.None);
        Assert.Equal(200, outcome.StatusCode);

        await using var db = h.NewContext();
        var after = Assert.Single(db.AnalysisParticipants.AsNoTracking());
        var (sources, coverage) = Frozen(after);
        Assert.Equal(ExpectedDocumentsSources.Tenant, sources.ExpectedDocuments!.Source);
        Assert.Equal(MatrixSavedAt, sources.ExpectedDocuments.UpdatedAt);
        Assert.Equal(2, coverage.RequiredTotal);
        Assert.Equal(PersonEvaluationService.Version, after.EvaluationVersion);

        // The superseded evaluation is kept exactly as frozen: the default matrix, 7 required.
        Assert.Equal(ExpectedDocumentsSources.Default, Frozen(before).Sources.ExpectedDocuments!.Source);
        Assert.Equal(7, Frozen(before).Coverage.RequiredTotal);
        var history  = Assert.Single(db.AnalysisRescores.AsNoTracking());
        var previous = JsonSerializer.Deserialize<PersonRescorePreviousValues>(history.PreviousValuesJson, PersonEvaluationJson.Options)!;
        Assert.Equal(before.CoverageJson, Assert.Single(previous.Participants).CoverageJson);
        Assert.Equal(before.DocumentVersionsJson, previous.Participants.Single().DocumentVersionsJson);
    }
}
