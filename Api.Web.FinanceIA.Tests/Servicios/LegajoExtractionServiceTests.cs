// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-28

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Tests.Legajo;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// <see cref="LegajoExtractionService"/>: parsing the BP and MB tool outputs (valid, invalid, a leading
/// thinking block, max_tokens), the request shape (forced tool WITHOUT strict, its own named client,
/// no identity in the payload), and the read modes with their limits. HTTP is a hand-written handler.
/// </summary>
public sealed class LegajoExtractionServiceTests
{
    private static readonly Func<CancellationToken, Task<bool>> Allowed = _ => Task.FromResult(true);

    private static readonly LegajoCallContext Context = new(LegajoTestData.Tenant, Guid.NewGuid());

    private sealed class Harness
    {
        public ScriptedHandler Handler { get; }
        public StubHttpClientFactory Factory { get; }
        public StubPageRenderer Renderer { get; } = new();
        public RecordingApiCallLogService ApiCalls { get; } = new();
        public LegajoExtractionService Sut { get; }

        public Harness(string responseBody, string? apiKey = "test-key", HttpStatusCode status = HttpStatusCode.OK,
                       ScriptedHandler? handler = null)
        {
            Handler = handler ?? new ScriptedHandler((status, responseBody));
            Factory = new StubHttpClientFactory(Handler);
            Sut = new LegajoExtractionService(
                LegajoTestClients.Messages(Handler, ApiCalls, Factory),
                Renderer,
                Options.Create(new LegajoExtractionOptions { ApiKey = apiKey }),
                Options.Create(new LegajoOptions()),
                NullLogger<LegajoExtractionService>.Instance);
        }

        public Task<LegajoExtractionPassResult> RunAsync(
            LegajoExtractionSchema schema, byte[] pdf, PageTextQualityReport quality, string readMode)
            => Sut.ExtractAsync(Context, schema, pdf, quality, readMode, Allowed, CancellationToken.None);
    }

    private static (byte[] Pdf, PageTextQualityReport Quality) NativeDocument()
    {
        var pdf = LegajoPdfFactory.NativePdf(LegajoPdfFactory.SampleText);
        return (pdf, PageTextQualityAnalyzer.Analyze(pdf));
    }

    private static PageTextQualityReport GoodPages(int pages) => new(true,
        Enumerable.Range(1, pages).Select(p => new PageTextQuality(p, 100, 90, 10, 0m, 5m, PageTextVerdicts.Good)).ToList());

    // ── Transport ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Timeout_NotRetried_ExtractionTimeout_OneApiCallLogRow()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, LegajoExtractionFixtures.BpEnvelope())) { TimeoutsFirst = 3 };
        var h = new Harness(string.Empty, handler: handler);
        var (pdf, quality) = NativeDocument();

        var result = await h.RunAsync(LegajoExtractionPrompts.BienesPersonales, pdf, quality, LegajoReadModes.Document);

        Assert.False(result.Succeeded);
        Assert.Equal(LegajoExtractionErrors.Timeout, result.ErrorCode);
        Assert.True(result.RequestSent);
        Assert.Equal(1, h.Handler.Hits);
        Assert.Equal("extraction_timeout", Assert.Single(h.ApiCalls.Calls).Error);
    }

    [Fact]
    public async Task ServerError_StillRetried()
    {
        var handler = new ScriptedHandler(
            (HttpStatusCode.InternalServerError, "{}"),
            (HttpStatusCode.OK, LegajoExtractionFixtures.BpEnvelope()));
        var h = new Harness(string.Empty, handler: handler);
        var (pdf, quality) = NativeDocument();

        var result = await h.RunAsync(LegajoExtractionPrompts.BienesPersonales, pdf, quality, LegajoReadModes.Document);

        Assert.True(result.Succeeded);
        Assert.Equal(2, h.Handler.Hits);
    }

    // ── Parsing ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Bp_ValidOutput_BehindAThinkingBlock_IsNormalized()
    {
        var h = new Harness(LegajoExtractionFixtures.BpEnvelope(leadingThinking: true));
        var (pdf, quality) = NativeDocument();

        var result = await h.RunAsync(LegajoExtractionPrompts.BienesPersonales, pdf, quality, LegajoReadModes.Document);

        Assert.True(result.Succeeded);
        Assert.Equal(20000, result.InputTokens);
        var root = result.Reading!.Result;
        Assert.Equal(2025, root["header"]!["fiscalYear"]!["value"]!.GetValue<int>());
        Assert.Equal(4, root["items"]!.AsArray().Count);
        Assert.Equal(1_700_000.50m, root["totals"]!["totalBienes"]!["value"]!.GetValue<decimal>());
        Assert.Equal("Partida", root["items"]![0]!["matchKey"]!["kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task Mb_ValidOutput_IsNormalized()
    {
        var h = new Harness(LegajoExtractionFixtures.MbEnvelope());
        var (pdf, quality) = NativeDocument();

        var result = await h.RunAsync(LegajoExtractionPrompts.ManifestacionBienes, pdf, quality, LegajoReadModes.Document);

        Assert.True(result.Succeeded);
        var root = result.Reading!.Result;
        Assert.Equal("Miles", root["header"]!["amountsUnit"]!["value"]!.GetValue<string>());
        Assert.Equal(6, root["lines"]!.AsArray().Count);
        Assert.Equal(1_500m, root["incomeStatement"]!["resultado"]!["value"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task InvalidOutput_FailsWithInvalidOutput_KeepsTheViolations_AndLogsAFailedCall()
    {
        var input = LegajoExtractionFixtures.BienesPersonales();
        input["items"]![0]!["rubro"] = "Yates";                      // not in the enum
        input["totals"]!["totalBienes"]!["value"] = "1.700.000,50";   // a string where a number belongs
        var h = new Harness(LegajoExtractionFixtures.BpEnvelope(input));
        var (pdf, quality) = NativeDocument();

        var result = await h.RunAsync(LegajoExtractionPrompts.BienesPersonales, pdf, quality, LegajoReadModes.Document);

        Assert.False(result.Succeeded);
        Assert.True(result.RequestSent);
        Assert.Equal(LegajoExtractionErrors.InvalidOutput, result.ErrorCode);
        Assert.Contains("/items/0/rubro: unknown_enum_value", result.Reading!.Violations);
        Assert.Contains("/totals/totalBienes/value: expected_number", result.Reading.Violations);
        Assert.DoesNotContain(result.Reading.Violations, v => v.Contains("1.700.000"));

        var call = Assert.Single(h.ApiCalls.Calls);
        Assert.False(call.IsSuccess);
        Assert.Equal(LegajoMessagesClient.ExtractionOperation, call.Operation);
    }

    [Fact]
    public async Task MaxTokensStop_IsTruncated()
    {
        var h = new Harness(LegajoExtractionFixtures.Envelope(
            LegajoExtractionPrompts.BienesPersonales.ToolName, LegajoExtractionFixtures.BienesPersonales(), stopReason: "max_tokens"));
        var (pdf, quality) = NativeDocument();

        var result = await h.RunAsync(LegajoExtractionPrompts.BienesPersonales, pdf, quality, LegajoReadModes.Document);

        Assert.Equal(LegajoClassificationErrors.Truncated, result.ErrorCode);
        Assert.Null(result.Reading);
    }

    // ── Request shape ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Request_ForcesTheTool_WithoutStrict_OnItsOwnClient_WithTheConfiguredModel()
    {
        var h = new Harness(LegajoExtractionFixtures.BpEnvelope());
        var (pdf, quality) = NativeDocument();

        await h.RunAsync(LegajoExtractionPrompts.BienesPersonales, pdf, quality, LegajoReadModes.Document);

        Assert.Equal([LegajoExtractionService.HttpClientName], h.Factory.ClientNames);

        using var request = JsonDocument.Parse(Assert.Single(h.Handler.RequestBodies));
        var root = request.RootElement;
        Assert.Equal("claude-sonnet-4-6", root.GetProperty("model").GetString());
        Assert.Equal(16000, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0, root.GetProperty("temperature").GetInt32());
        Assert.False(root.TryGetProperty("thinking", out _));
        Assert.Equal("tool", root.GetProperty("tool_choice").GetProperty("type").GetString());

        var tool = Assert.Single(root.GetProperty("tools").EnumerateArray().ToList());
        Assert.False(tool.TryGetProperty("strict", out _));
        Assert.Equal(LegajoExtractionPrompts.BienesPersonales.ToolName, tool.GetProperty("name").GetString());

        var content = root.GetProperty("messages")[0].GetProperty("content").EnumerateArray().ToList();
        Assert.Equal(["document", "text"], content.Select(b => b.GetProperty("type").GetString()));
    }

    public static TheoryData<string> AllTypes => [.. LegajoExtractionPrompts.SupportedTypes];

    [Theory]
    [MemberData(nameof(AllTypes))]
    public async Task Request_NeverContainsTheLegajosCuitOrName(string documentType)
    {
        var h = new Harness(LegajoExtractionFixtures.EnvelopeFor(documentType));
        var (pdf, quality) = NativeDocument();

        await h.RunAsync(LegajoExtractionPrompts.For(documentType)!, pdf, quality, LegajoReadModes.Document);

        var body = Assert.Single(h.Handler.RequestBodies);
        Assert.DoesNotContain(LegajoTestData.LegajoCuit, body);
        Assert.DoesNotContain(LegajoTestData.LegajoCuitDigits, body);
        Assert.DoesNotContain(LegajoTestData.LegajoFullName, body);
    }

    // ── Phase 3a types ──────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllTypes))]
    public async Task EveryType_ValidOutput_IsNormalized_WithItsOwnToolAndMaxTokens(string documentType)
    {
        var schema = LegajoExtractionPrompts.For(documentType)!;
        var h = new Harness(LegajoExtractionFixtures.EnvelopeFor(documentType));
        var (pdf, quality) = NativeDocument();

        var result = await h.RunAsync(schema, pdf, quality, LegajoReadModes.Document);

        Assert.True(result.Succeeded, result.ErrorCode);
        Assert.True(result.Reading!.IsValid);
        Assert.Equal(LegajoExtractionFixtures.NormalizedClean(documentType).ToJsonString(), result.Reading.Result.ToJsonString());

        using var request = JsonDocument.Parse(Assert.Single(h.Handler.RequestBodies));
        var root = request.RootElement;
        Assert.Equal(schema.MaxTokens ?? 16000, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal(schema.ToolName, root.GetProperty("tool_choice").GetProperty("name").GetString());
        Assert.Equal(schema.SystemPrompt, root.GetProperty("system").GetString());
    }

    [Theory]
    [InlineData(DocumentTypes.DdjjIva, "/periods/0/period", "\"11/2025\"", "expected_period")]
    [InlineData(DocumentTypes.DdjjIva, "/periods/0/saldoTecnico/side", "\"AFIP\"", "unknown_enum_value")]
    [InlineData(DocumentTypes.DdjjIva, "/alicuotaLines/0/side", "null", "required")]
    [InlineData(DocumentTypes.DdjjIibb, "/rows/0/period", "\"2025\"", "expected_period")]
    [InlineData(DocumentTypes.Ganancias, "/categoryResults/0/category", "\"Quinta\"", "unknown_enum_value")]
    [InlineData(DocumentTypes.Ganancias, "/justification/lines/0/side", "\"Ingreso\"", "unknown_enum_value")]
    [InlineData(DocumentTypes.Monotributo, "/header/categoria/value", "\"L\"", "unknown_enum_value")]
    [InlineData(DocumentTypes.Monotributo, "/payments/0/paymentDate", "\"20/10/2025\"", "expected_date")]
    [InlineData(DocumentTypes.CertificacionIngresos, "/header/periodTo/value", "\"2025-12\"", "expected_date")]
    public async Task EveryType_InvalidOutput_IsInvalidOutput_NamingThePointer(
        string documentType, string pointer, string json, string code)
    {
        var input = LegajoExtractionFixtures.Clean(documentType);
        Assert.True(LegajoJsonPointer.Set(input, pointer, JsonNode.Parse(json)));
        var h = new Harness(LegajoExtractionFixtures.EnvelopeFor(documentType, input));
        var (pdf, quality) = NativeDocument();

        var result = await h.RunAsync(LegajoExtractionPrompts.For(documentType)!, pdf, quality, LegajoReadModes.Document);

        Assert.False(result.Succeeded);
        Assert.Equal(LegajoExtractionErrors.InvalidOutput, result.ErrorCode);
        Assert.Contains($"{pointer}: {code}", result.Reading!.Violations);
    }

    [Theory]
    [MemberData(nameof(AllTypes))]
    public async Task EveryType_MaxTokensStop_IsTruncated(string documentType)
    {
        var h = new Harness(LegajoExtractionFixtures.EnvelopeFor(documentType, stopReason: "max_tokens"));
        var (pdf, quality) = NativeDocument();

        var result = await h.RunAsync(LegajoExtractionPrompts.For(documentType)!, pdf, quality, LegajoReadModes.Document);

        Assert.Equal(LegajoClassificationErrors.Truncated, result.ErrorCode);
        Assert.Null(result.Reading);
    }

    [Fact]
    public void Iva_SaldoTecnico_MayBeNull_AsAWhole_ButNotItsSide()
    {
        var withoutSaldo = LegajoExtractionFixtures.DdjjIva();
        LegajoJsonPointer.Set(withoutSaldo, "/periods/0/saldoTecnico", null);
        var normalized = LegajoExtractionFixtures.Normalize(LegajoExtractionPrompts.DdjjIva, withoutSaldo);
        Assert.Null(normalized["periods"]![0]!["saldoTecnico"]);

        var negativeLine = LegajoExtractionFixtures.NormalizedClean(DocumentTypes.DdjjIva);
        Assert.Equal(-1_000m, negativeLine["alicuotaLines"]![2]!["impuesto"]!.GetValue<decimal>());
        Assert.Null(negativeLine["alicuotaLines"]![2]!["alicuotaPct"]);
    }

    // ── Read modes and limits ───────────────────────────────────────────────────

    [Fact]
    public void ChooseReadMode_DocumentOnlyWhenEveryPageIsGood_SmallAndAtMost40Pages()
    {
        Assert.Equal(LegajoReadModes.Document, LegajoExtractionService.ChooseReadMode(1000, GoodPages(40)));
        Assert.Equal(LegajoReadModes.Images, LegajoExtractionService.ChooseReadMode(1000, GoodPages(41)));
        Assert.Equal(LegajoReadModes.Images,
            LegajoExtractionService.ChooseReadMode(LegajoExtractionService.DocumentBlockMaxBytes, GoodPages(3)));

        var pdf = LegajoPdfFactory.BlankPdf(2);
        Assert.Equal(LegajoReadModes.Images, LegajoExtractionService.ChooseReadMode(1000, PageTextQualityAnalyzer.Analyze(pdf)));
    }

    [Fact]
    public async Task ImagesMode_RendersAtTheConfiguredDpi_DownscaledToTheLongEdge()
    {
        var h = new Harness(LegajoExtractionFixtures.BpEnvelope());
        var pdf = LegajoPdfFactory.BlankPdf(2);

        var result = await h.RunAsync(LegajoExtractionPrompts.BienesPersonales, pdf, PageTextQualityAnalyzer.Analyze(pdf), LegajoReadModes.Images);

        Assert.True(result.Succeeded);
        Assert.Equal(LegajoReadModes.Images, result.ReadMode);
        Assert.Equal((200, LegajoExtractionService.MaxPages + 1, (int?)LegajoExtractionService.MaxImageLongEdgePx),
            Assert.Single(h.Renderer.Requests));

        using var request = JsonDocument.Parse(Assert.Single(h.Handler.RequestBodies));
        var types = request.RootElement.GetProperty("messages")[0].GetProperty("content")
            .EnumerateArray().Select(b => b.GetProperty("type").GetString()).ToList();
        Assert.Equal(["image", "image", "text"], types);
    }

    [Fact]
    public async Task MoreThan40Pages_KnownFromTheAnalyzer_FailsWithoutACall()
    {
        var h = new Harness(LegajoExtractionFixtures.BpEnvelope());

        var result = await h.RunAsync(LegajoExtractionPrompts.BienesPersonales, [1, 2, 3], GoodPages(41), LegajoReadModes.Images);

        Assert.Equal(LegajoExtractionErrors.TooManyPages, result.ErrorCode);
        Assert.False(result.RequestSent);
        Assert.Equal(0, h.Handler.Hits);
        Assert.Equal(0, h.Renderer.Calls);
    }

    [Fact]
    public async Task MoreThan40Pages_FoundWhileRendering_FailsWithoutACall()
    {
        var h = new Harness(LegajoExtractionFixtures.BpEnvelope());
        h.Renderer.Extraction = (41, 10);

        var result = await h.RunAsync(LegajoExtractionPrompts.BienesPersonales, [1, 2, 3], PageTextQualityReport.Unreadable, LegajoReadModes.Images);

        Assert.Equal(LegajoExtractionErrors.TooManyPages, result.ErrorCode);
        Assert.Equal(0, h.Handler.Hits);
    }

    [Fact]
    public async Task PayloadOver30MB_FailsWithoutACall()
    {
        var h = new Harness(LegajoExtractionFixtures.BpEnvelope());
        h.Renderer.Extraction = (40, 600 * 1024);   // 40 × 800 KB of base64

        var result = await h.RunAsync(LegajoExtractionPrompts.BienesPersonales, [1, 2, 3], GoodPages(40), LegajoReadModes.Images);

        Assert.Equal(LegajoExtractionErrors.RequestTooLarge, result.ErrorCode);
        Assert.Equal(0, h.Handler.Hits);
    }

    [Fact]
    public async Task RenderFails_PdfUnreadable_NoCall()
    {
        var h = new Harness(LegajoExtractionFixtures.BpEnvelope());
        h.Renderer.Throw = true;

        var result = await h.RunAsync(LegajoExtractionPrompts.BienesPersonales, [1, 2, 3], GoodPages(2), LegajoReadModes.Images);

        Assert.Equal(LegajoClassificationErrors.PdfUnreadable, result.ErrorCode);
        Assert.Equal(0, h.Handler.Hits);
    }

    [Fact]
    public async Task NoApiKey_NoCall()
    {
        var h = new Harness(LegajoExtractionFixtures.BpEnvelope(), apiKey: null);
        var (pdf, quality) = NativeDocument();

        var result = await h.RunAsync(LegajoExtractionPrompts.BienesPersonales, pdf, quality, LegajoReadModes.Document);

        Assert.Equal(LegajoClassificationErrors.NotConfigured, result.ErrorCode);
        Assert.Equal(0, h.Handler.Hits);
    }

    // ── Reader ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Reader_Lenient_WhereNothingIsLost()
    {
        var input = LegajoExtractionFixtures.BienesPersonales();
        input["header"]!["fiscalYear"] = 2025;                                 // a bare value
        input["header"]!.AsObject().Remove("rectificativaNumber");             // a missing leaf
        input.Remove("rubroTotals");                                           // a missing array
        input["items"]![0]!["page"] = 0;                                       // page below 1
        input["items"]![1]!["confidence"] = 3;                                 // out of range
        input["items"]![2]!["matchKey"] = null;                                // a nullable object
        input["header"]!["amountsUnit"]!["value"] = "miles";                   // enum casing

        var result = LegajoExtractionFixtures.Normalize(LegajoExtractionPrompts.BienesPersonales, input);

        Assert.Equal(2025, result["header"]!["fiscalYear"]!["value"]!.GetValue<int>());
        Assert.Equal(0m, result["header"]!["fiscalYear"]!["confidence"]!.GetValue<decimal>());
        Assert.Null(result["header"]!["rectificativaNumber"]!["value"]);
        Assert.Empty(result["rubroTotals"]!.AsArray());
        Assert.Null(result["items"]![0]!["page"]);
        Assert.Equal(1m, result["items"]![1]!["confidence"]!.GetValue<decimal>());
        Assert.Null(result["items"]![2]!["matchKey"]);
        Assert.Equal("Miles", result["header"]!["amountsUnit"]!["value"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("/header/fiscalYear/value", "\"2025\"", "expected_integer")]
    [InlineData("/items/0/acquisitionDate", "\"15/03/2020\"", "expected_date")]
    [InlineData("/items/0/isExempt", "\"no\"", "expected_boolean")]
    [InlineData("/items/0/location", "null", "required")]
    [InlineData("/header", "[]", "expected_object")]
    [InlineData("/items", "{}", "expected_array")]
    [InlineData("/totals/totalBienes/value", "1000000000000000.01", "out_of_range")]
    [InlineData("/items/0/fiscalValue", "-2e15", "out_of_range")]
    [InlineData("/items/0/ownershipPct", "1e40", "out_of_range")]
    public void Reader_Strict_OnStructure(string pointer, string json, string code)
    {
        var input = LegajoExtractionFixtures.BienesPersonales();
        LegajoJsonPointer.Set(input, pointer, JsonNode.Parse(json));

        using var document = JsonDocument.Parse(input.ToJsonString());
        var reading = LegajoToolInputReader.Read(LegajoExtractionPrompts.BienesPersonales.Root, document.RootElement);

        Assert.False(reading.IsValid);
        Assert.Contains($"{pointer}: {code}", reading.Violations);
    }

    [Fact]
    public void Reader_Cuit_KeepsElevenDigitsOnly()
    {
        var input = LegajoExtractionFixtures.BienesPersonales(holderCuit: LegajoTestData.LegajoCuit);   // hyphenated
        var result = LegajoExtractionFixtures.Normalize(LegajoExtractionPrompts.BienesPersonales, input);
        Assert.Equal(LegajoTestData.LegajoCuitDigits, result["header"]!["holderCuit"]!["value"]!.GetValue<string>());

        input = LegajoExtractionFixtures.BienesPersonales(holderCuit: "20-123");
        result = LegajoExtractionFixtures.Normalize(LegajoExtractionPrompts.BienesPersonales, input);
        Assert.Null(result["header"]!["holderCuit"]!["value"]);
    }
}
