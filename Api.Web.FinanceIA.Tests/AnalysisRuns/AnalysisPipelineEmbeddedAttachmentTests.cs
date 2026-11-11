// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-03

using System.Text.Json;
using Api.Web.FinanceIA.Servicios;
using Api.Web.FinanceIA.Tests.Servicios;
using Xunit;
using static Api.Web.FinanceIA.Tests.AnalysisRuns.AnalysisRunTestData;

namespace Api.Web.FinanceIA.Tests.AnalysisRuns;

/// <summary>
/// The pipeline's side of the embedded-attachment unwrap: an encrypted CPCE wrapper hands the model
/// its "Balance"; a wrapper whose attachments could not be taken is flagged on the result
/// (integrity.extraction.embeddedAttachment + an es-AR anomaly) and never silently read as normal.
/// </summary>
public sealed class AnalysisPipelineEmbeddedAttachmentTests
{
    private const string ModelBalanceJson =
        """{"assets":1000,"integrity":{"passed":true,"checks":[],"extraction":{"method":"document","secondPassRan":false,"keptPass":"first","keptReason":"only_pass"}}}""";

    private const string ModelAnomaly = "Anomalía del modelo.";

    private static async Task<(PipelineHarness Harness, Api.Web.FinanceIA.Models.Financia.AnalysisResult Result)> RunAsync(
        byte[] storedBytes, string balanceJson = ModelBalanceJson)
    {
        var h   = new PipelineHarness();
        var doc = Document(TenantA);
        h.Store.Add(doc);
        h.Storage.Bytes = storedBytes;
        h.Claude.Result = ClaudeResult() with
        {
            BalanceJson   = balanceJson,
            AnomaliesJson = $"[\"{ModelAnomaly}\"]",
        };

        await h.Build().RunAsync(Run(doc), CancellationToken.None);

        return (h, Assert.Single(h.Results.Created));
    }

    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "EncryptedOblea", name));

    [Fact]
    public async Task An_encrypted_CPCE_wrapper_hands_the_model_its_balance_and_is_not_flagged()
    {
        var (h, result) = await RunAsync(Fixture("cpce_aes128.pdf"));

        Assert.Equal(Fixture("balance.expected.pdf"), h.Claude.LastPdfBytes);
        Assert.Equal(ModelBalanceJson, result.BalanceJson);
        Assert.Equal([ModelAnomaly], JsonSerializer.Deserialize<List<string>>(result.Anomalies!));
    }

    [Fact]
    public async Task An_unread_attachment_is_recorded_in_the_extraction_node_and_as_an_anomaly()
    {
        var outer = AttachmentPdfBuilder.Wrapper(
            ("Balance.pdf", null), ("Informe del auditor.pdf", AttachmentPdfBuilder.Pdf()));

        var (h, result) = await RunAsync(outer);

        Assert.Same(outer, h.Claude.LastPdfBytes); // the wrapper itself went to the model

        using var balance = JsonDocument.Parse(result.BalanceJson!);
        Assert.Equal("unread", balance.RootElement
            .GetProperty("integrity").GetProperty("extraction").GetProperty("embeddedAttachment").GetString());

        Assert.Equal(
            [ModelAnomaly, EmbeddedAttachmentJson.UnreadAnomaly],
            JsonSerializer.Deserialize<List<string>>(result.Anomalies!));

        Assert.Equal("unread", ExtractionIntegrityReader.Read(result.BalanceJson)!.EmbeddedAttachment);
    }

    [Fact]
    public async Task An_unread_attachment_with_no_extraction_node_still_gets_the_anomaly()
    {
        const string noIntegrity = """{"assets":1000,"integrity":null}""";
        var outer = AttachmentPdfBuilder.Wrapper(
            ("anexo_a.pdf", AttachmentPdfBuilder.Pdf(padChars: AttachmentPdfBuilder.PaddingChars)),
            ("anexo_b.pdf", AttachmentPdfBuilder.Pdf(padChars: AttachmentPdfBuilder.PaddingChars + 1024)));

        var (_, result) = await RunAsync(outer, noIntegrity);

        Assert.Equal(noIntegrity, result.BalanceJson);
        Assert.Contains(EmbeddedAttachmentJson.UnreadAnomaly, JsonSerializer.Deserialize<List<string>>(result.Anomalies!)!);
    }

    [Fact]
    public async Task An_orphaned_balance_records_its_source_without_an_anomaly()
    {
        var statements = AttachmentPdfBuilder.Pdf(pageCount: 20);
        var outer      = AttachmentPdfBuilder.OrphanedCatalogWrapper(
            [new AttachmentPdfBuilder.OrphanAttachment("Trabajo Profesional", statements)]);

        var (h, result) = await RunAsync(outer);

        Assert.Equal(statements, h.Claude.LastPdfBytes);
        Assert.Equal(
            ModelBalanceJson.Replace("\"only_pass\"}", "\"only_pass\",\"embeddedAttachmentSource\":\"orphaned_filespec\"}"),
            result.BalanceJson);
        Assert.Equal([ModelAnomaly], JsonSerializer.Deserialize<List<string>>(result.Anomalies!));

        var integrity = ExtractionIntegrityReader.Read(result.BalanceJson)!;
        Assert.Equal("orphaned_filespec", integrity.EmbeddedAttachmentSource);
        Assert.Null(integrity.EmbeddedAttachment);
    }

    [Fact]
    public async Task An_unreadable_orphan_records_both_unread_and_its_source()
    {
        var outer = AttachmentPdfBuilder.OrphanedCatalogWrapper(
            [new AttachmentPdfBuilder.OrphanAttachment("Trabajo Profesional.pdf", AttachmentPdfBuilder.NotAPdf())]);

        var (_, result) = await RunAsync(outer);

        var integrity = ExtractionIntegrityReader.Read(result.BalanceJson)!;
        Assert.Equal("unread", integrity.EmbeddedAttachment);
        Assert.Equal("orphaned_filespec", integrity.EmbeddedAttachmentSource);
        Assert.Contains(EmbeddedAttachmentJson.UnreadAnomaly, JsonSerializer.Deserialize<List<string>>(result.Anomalies!)!);
    }

    [Fact]
    public async Task A_live_attachment_leaves_the_balance_byte_identical()
    {
        var statements = AttachmentPdfBuilder.Pdf();
        var outer      = AttachmentPdfBuilder.Wrapper(("Balance.pdf", statements));

        var (h, result) = await RunAsync(outer);

        Assert.Equal(statements, h.Claude.LastPdfBytes);
        Assert.Equal(ModelBalanceJson, result.BalanceJson);
        Assert.Equal([ModelAnomaly], JsonSerializer.Deserialize<List<string>>(result.Anomalies!));
    }

    [Fact]
    public async Task A_plain_pdf_leaves_the_balance_and_the_anomalies_exactly_as_the_model_wrote_them()
    {
        var plain = AttachmentPdfBuilder.Pdf();

        var (h, result) = await RunAsync(plain);

        Assert.Same(plain, h.Claude.LastPdfBytes);
        Assert.Equal(ModelBalanceJson, result.BalanceJson);
        Assert.Equal([ModelAnomaly], JsonSerializer.Deserialize<List<string>>(result.Anomalies!));
    }
}
