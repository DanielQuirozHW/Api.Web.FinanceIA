// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-10

using System.Text.Json;
using Api.Web.FinanceIA.Models.Nosis;
using Api.Web.FinanceIA.Servicios.NosisReport;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios.NosisReport;

/// <summary>
/// <see cref="NosisReportGroundTruth"/>, the harness's comparison with a hand-written expected file,
/// over a synthetic report. Only verdicts come out of it, never values.
/// </summary>
public sealed class NosisReportGroundTruthTests
{
    private static NosisManagerReport Report(NosisReportPdfBuilder? builder = null)
        => new NosisManagerReportParser().Parse((builder ?? new NosisReportPdfBuilder()).Build(), CancellationToken.None).Report!;

    private static JsonElement Entry(string json) => JsonDocument.Parse(json).RootElement;

    /// <summary>What the synthetic builder prints, in the ground truth's vocabulary.</summary>
    private const string Matching = """
        {
          "alias": "x", "sha256": "ignored",
          "reportDate": "2026-11-03T14:36:00",
          "summary": { "score": 742, "situacionBcra": 3, "endeudamientoTotal": 7258316409, "compromisosMensuales": 31472.6,
                       "tieneChequesRechazados": true, "consultas3m": 4, "consultas12m": 11 },
          "criteriosAceptacion": { "resultado": "Aprobado", "enPrueba": true },
          "evolucion24m": { "count": 24, "first": "2024-10", "last": "2026-09", "lastScore": 723 },
          "chequesRechazadosPorMes": { "latest": { "periodo": "2026-10", "cantidad": 3, "monto": 750000.004 } },
          "juicios": { "comoDemandadoCantidad": 2 }
        }
        """;

    [Fact]
    public void A_matching_entry_is_all_ok_and_amounts_compare_within_a_cent()
    {
        var fields = NosisReportGroundTruth.Compare(Report(), Entry(Matching));

        Assert.Equal(18, fields.Count);
        Assert.All(fields, f => Assert.True(f.Verdict == GroundTruthVerdict.Ok, f.Path));
        Assert.DoesNotContain(fields, f => f.Path is "alias" or "sha256");
    }

    [Fact]
    public void A_different_value_is_diff_and_only_that_field()
    {
        var fields = NosisReportGroundTruth.Compare(Report(), Entry(Matching.Replace("\"score\": 742", "\"score\": 1", StringComparison.Ordinal)));

        var diff = Assert.Single(fields, f => f.Verdict != GroundTruthVerdict.Ok);
        Assert.Equal("summary.score", diff.Path);
        Assert.Equal(GroundTruthVerdict.Diff, diff.Verdict);
    }

    [Fact]
    public void A_section_the_parser_did_not_produce_is_missing()
    {
        var fields = NosisReportGroundTruth.Compare(
            Report(new NosisReportPdfBuilder().Without("juicios")),
            Entry("""{ "juicios": { "comoDemandadoCantidad": 2 }, "sisa": { "estado": "Activo" } }"""));

        Assert.Equal(GroundTruthVerdict.Missing, Assert.Single(fields, f => f.Path == "juicios.comoDemandadoCantidad").Verdict);
        Assert.Equal(GroundTruthVerdict.Ok, Assert.Single(fields, f => f.Path == "sisa.estado").Verdict);
    }

    [Fact]
    public void Fields_absent_from_the_entry_are_not_compared()
    {
        var fields = NosisReportGroundTruth.Compare(Report(), Entry("""{ "summary": { "score": 742 } }"""));

        Assert.Equal("summary.score", Assert.Single(fields).Path);
    }

    [Fact]
    public void The_outcome_key_is_compared_ok_meaning_parsed()
    {
        var parsed = new NosisManagerReportParser().Parse(new NosisReportPdfBuilder().Build(), CancellationToken.None);
        Assert.Equal(GroundTruthVerdict.Ok, Assert.Single(NosisReportGroundTruth.Compare(parsed, Entry("""{ "outcome": "ok" }"""))).Verdict);

        var refused = new NosisManagerReportParser().Parse(NosisReportPdfBuilder.PlainPdf("x"), CancellationToken.None);
        var fields = NosisReportGroundTruth.Compare(refused, Entry("""{ "outcome": "ok", "summary": { "score": 1 } }"""));

        Assert.Equal(GroundTruthVerdict.Diff, fields.Single(f => f.Path == "outcome").Verdict);
        Assert.Equal(GroundTruthVerdict.Missing, fields.Single(f => f.Path == "summary.score").Verdict);
        Assert.Equal(GroundTruthVerdict.Ok, Assert.Single(
            NosisReportGroundTruth.Compare(refused, Entry("""{ "outcome": "not_recognized" }"""))).Verdict);
    }

    [Fact]
    public void The_documented_example_entry_is_understood()
    {
        // The synthetic example of the guide, values deliberately unrelated to the report.
        const string example = """
            { "files": [ { "alias": "x", "sha256": "...", "reportDate": "2026-11-03T14:36:00",
              "summary": { "score": 1, "situacionBcra": 3, "endeudamientoTotal": 1, "compromisosMensuales": 1,
                           "tieneChequesRechazados": true, "consultas3m": 1, "consultas12m": 1 },
              "criteriosAceptacion": { "resultado": "Observado", "enPrueba": true },
              "evolucion24m": { "count": 24, "first": "2024-12", "last": "2026-11", "lastScore": 1 },
              "chequesRechazadosPorMes": { "latest": { "periodo": "2026-10", "cantidad": 5, "monto": 1.0 } },
              "juicios": { "comoDemandadoCantidad": 1 } } ] }
            """;

        var entry = Entry(example).GetProperty("files")[0];
        var fields = NosisReportGroundTruth.Compare(Report(), entry);

        Assert.Equal(18, fields.Count);
        Assert.Equal(GroundTruthVerdict.Ok, fields.Single(f => f.Path == "reportDate").Verdict);
        Assert.Equal(GroundTruthVerdict.Ok, fields.Single(f => f.Path == "summary.situacionBcra").Verdict);
        Assert.Equal(GroundTruthVerdict.Diff, fields.Single(f => f.Path == "summary.score").Verdict);
        Assert.Equal(GroundTruthVerdict.Diff, fields.Single(f => f.Path == "evolucion24m.first").Verdict);
    }
}
