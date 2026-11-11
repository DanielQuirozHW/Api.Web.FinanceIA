// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-28

using System.Net;
using System.Text.Json;
using Api.Web.FinanceIA.Models;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.Legajo;
using Api.Web.FinanceIA.Tests.Legajo;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// <see cref="LegajoClassificationService"/>: the request shape (forced strict tool, no thinking, read
/// mode), parsing by block TYPE (an envelope that starts with a thinking block), retries, and the
/// consent gate before every request. HTTP is a hand-written handler; nothing leaves the process.
/// </summary>
public sealed class LegajoClassificationServiceTests
{
    private static readonly Func<CancellationToken, Task<bool>> Allowed = _ => Task.FromResult(true);

    private static readonly LegajoCallContext Context = new(LegajoTestData.Tenant, Guid.NewGuid());

    private static LegajoClassificationService BuildSut(
        HttpMessageHandler handler, StubPageRenderer? renderer = null, string? apiKey = "test-key")
    {
        return new LegajoClassificationService(
            LegajoTestClients.Messages(handler),
            renderer ?? new StubPageRenderer(),
            Options.Create(new LegajoClassificationOptions { ApiKey = apiKey }),
            NullLogger<LegajoClassificationService>.Instance);
    }

    private static (byte[] Pdf, PageTextQualityReport Quality) NativeDocument()
    {
        var pdf = LegajoPdfFactory.NativePdf(LegajoPdfFactory.SampleText);
        return (pdf, PageTextQualityAnalyzer.Analyze(pdf));
    }

    // ── Parsing ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseResponse_EnvelopeStartingWithAThinkingBlock_ReadsTheToolUseBlock()
    {
        var (output, error, inTok, outTok) = LegajoClassificationService.ParseResponse(
            ClassificationEnvelopes.ToolUse(leadingThinking: true));

        Assert.Null(error);
        Assert.NotNull(output);
        Assert.Equal(DocumentTypes.Monotributo, output!.DocumentType);
        Assert.Equal(LegajoTestData.LegajoCuitDigits, output.HolderCuit);
        Assert.Equal(1, output.HolderCuitPage);
        Assert.Equal(new DateOnly(2025, 12, 31), output.PeriodEnd);
        Assert.Equal(2025, output.FiscalYear);
        Assert.Equal(EngagementTypes.Certificacion, output.EngagementType);
        Assert.Equal(0.95m, output.Confidence);
        Assert.Equal(4200, inTok);
        Assert.Equal(310, outTok);
    }

    [Fact]
    public void ParseResponse_CompositeSegments_AreRead()
    {
        var (output, _, _, _) = LegajoClassificationService.ParseResponse(ClassificationEnvelopes.ToolUse(isComposite: true));

        Assert.True(output!.IsComposite);
        Assert.Equal(2, output.Segments.Count);
        Assert.Equal(new LegajoClassificationSegment(DocumentTypes.Ganancias, 1, 3), output.Segments[0]);
    }

    [Fact]
    public void ParseResponse_OutOfRangeValues_AreClampedOrDropped()
    {
        var body = ClassificationEnvelopes.ToolUse(documentType: "SomethingElse", cuit: "12-3", confidence: 7m);

        var (output, error, _, _) = LegajoClassificationService.ParseResponse(body);

        Assert.Null(error);
        Assert.Equal(LegajoClassificationPrompt.UnknownType, output!.DocumentType);
        Assert.Null(output.HolderCuit);
        Assert.Null(output.HolderCuitQuote);
        Assert.Equal(1m, output.Confidence);
    }

    [Fact]
    public void ParseResponse_TypesAreCanonicalisedCaseInsensitively()
    {
        var (output, error, _, _) = LegajoClassificationService.ParseResponse(
            ClassificationEnvelopes.ToolUse(documentType: "ganancias", engagementType: "COMPILACION", isComposite: true));

        Assert.Null(error);
        Assert.Equal(DocumentTypes.Ganancias, output!.DocumentType);
        Assert.Equal(EngagementTypes.Compilacion, output.EngagementType);

        // So a casing difference alone never reads as a type mismatch.
        var outcome = LegajoClassificationOutcome.Decide(output with { IsComposite = false }, DocumentTypes.Ganancias,
            LegajoTestData.LegajoCuit, 0.7m);
        Assert.DoesNotContain(LegajoClassificationOutcome.ReasonTypeMismatch, outcome.ReviewReasons);
    }

    [Fact]
    public void ParseResponse_UnknownEngagement_IsNull()
    {
        var (output, _, _, _) = LegajoClassificationService.ParseResponse(
            ClassificationEnvelopes.ToolUse(engagementType: "Desconocido"));

        Assert.Null(output!.EngagementType);
    }

    [Theory]
    [InlineData("max_tokens", LegajoClassificationErrors.Truncated)]
    [InlineData("refusal", LegajoClassificationErrors.Refused)]
    public void ParseResponse_StopReason_IsAnError(string stopReason, string expected)
    {
        var (output, error, inTok, _) = LegajoClassificationService.ParseResponse(
            ClassificationEnvelopes.ToolUse(stopReason: stopReason));

        Assert.Null(output);
        Assert.Equal(expected, error);
        Assert.Equal(4200, inTok);
    }

    [Theory]
    [InlineData("""{"content":[{"type":"text","text":"no tool"}],"usage":{"input_tokens":1,"output_tokens":1}}""")]
    [InlineData("""{"content":[{"type":"tool_use","name":"other_tool","input":{"document_type":"Otro"}}]}""")]
    [InlineData("not json")]
    public void ParseResponse_WithoutOurToolUseBlock_IsInvalid(string body)
    {
        var (output, error, _, _) = LegajoClassificationService.ParseResponse(body);

        Assert.Null(output);
        Assert.Equal(LegajoClassificationErrors.InvalidResponse, error);
    }

    // ── Request shape ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ClassifyAsync_AllPagesGood_SendsOneDocumentBlock_ForcedStrictTool_NoThinking()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, ClassificationEnvelopes.ToolUse()));
        var (pdf, quality) = NativeDocument();

        var result = await BuildSut(handler).ClassifyAsync(Context, pdf, quality, Allowed, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(LegajoReadModes.Document, result.ReadMode);
        Assert.Equal("claude-haiku-4-5", result.Model);

        using var request = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        var root = request.RootElement;

        Assert.Equal("claude-haiku-4-5", root.GetProperty("model").GetString());
        Assert.False(root.TryGetProperty("thinking", out _));
        Assert.Equal(0, root.GetProperty("temperature").GetInt32());
        Assert.Equal("tool", root.GetProperty("tool_choice").GetProperty("type").GetString());
        Assert.Equal(LegajoClassificationPrompt.ToolName, root.GetProperty("tool_choice").GetProperty("name").GetString());

        var tool = Assert.Single(root.GetProperty("tools").EnumerateArray().ToList());
        Assert.True(tool.GetProperty("strict").GetBoolean());
        Assert.Equal(LegajoClassificationPrompt.ToolName, tool.GetProperty("name").GetString());

        var content = root.GetProperty("messages")[0].GetProperty("content").EnumerateArray().ToList();
        Assert.Equal(["document", "text"], content.Select(b => b.GetProperty("type").GetString()));
    }

    [Fact]
    public async Task ClassifyAsync_SuspectOrScannedPages_SendsImages()
    {
        var handler  = new ScriptedHandler((HttpStatusCode.OK, ClassificationEnvelopes.ToolUse()));
        var renderer = new StubPageRenderer();
        var pdf      = LegajoPdfFactory.BlankPdf(2);

        var result = await BuildSut(handler, renderer)
            .ClassifyAsync(Context, pdf, PageTextQualityAnalyzer.Analyze(pdf), Allowed, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(LegajoReadModes.Images, result.ReadMode);
        Assert.Equal(1, renderer.Calls);

        using var request = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        var types = request.RootElement.GetProperty("messages")[0].GetProperty("content")
            .EnumerateArray().Select(b => b.GetProperty("type").GetString()).ToList();
        Assert.Equal(["image", "image", "text"], types);
    }

    [Fact]
    public async Task ClassifyAsync_RenderFails_NoRequest_PdfUnreadable()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, ClassificationEnvelopes.ToolUse()));
        var pdf     = LegajoPdfFactory.BlankPdf(1);

        var result = await BuildSut(handler, new StubPageRenderer { Throw = true })
            .ClassifyAsync(Context, pdf, PageTextQualityAnalyzer.Analyze(pdf), Allowed, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.RequestSent);
        Assert.Equal(LegajoClassificationErrors.PdfUnreadable, result.ErrorCode);
        Assert.Equal(0, handler.Hits);
    }

    [Fact]
    public void ChooseReadMode_LargeOrLongDocument_GoesAsImages()
    {
        var (_, quality) = NativeDocument();

        Assert.Equal(LegajoReadModes.Document, LegajoClassificationService.ChooseReadMode(1000, quality));
        Assert.Equal(LegajoReadModes.Images,
            LegajoClassificationService.ChooseReadMode(LegajoClassificationService.DocumentBlockMaxBytes, quality));

        var longGood = new PageTextQualityReport(true,
            Enumerable.Range(1, LegajoClassificationService.MaxPages + 1)
                .Select(p => new PageTextQuality(p, 100, 90, 10, 0m, 5m, PageTextVerdicts.Good))
                .ToList());
        Assert.Equal(LegajoReadModes.Images, LegajoClassificationService.ChooseReadMode(1000, longGood));
    }

    // ── Consent gate and configuration ──────────────────────────────────────────

    [Fact]
    public async Task ClassifyAsync_NotAllowed_NeverHitsTheApi()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, ClassificationEnvelopes.ToolUse()));
        var (pdf, quality) = NativeDocument();

        var result = await BuildSut(handler)
            .ClassifyAsync(Context, pdf, quality, _ => Task.FromResult(false), CancellationToken.None);

        Assert.Equal(LegajoClassificationErrors.ConsentRevoked, result.ErrorCode);
        Assert.False(result.RequestSent);
        Assert.Equal(0, handler.Hits);
    }

    [Fact]
    public async Task ClassifyAsync_ConsentRevokedDuringTheBackoff_StopsTheRetry()
    {
        var handler = new ScriptedHandler(
            (HttpStatusCode.InternalServerError, "{}"),
            (HttpStatusCode.OK, ClassificationEnvelopes.ToolUse()));
        var (pdf, quality) = NativeDocument();
        var checks = 0;

        var result = await BuildSut(handler)
            .ClassifyAsync(Context, pdf, quality, _ => Task.FromResult(++checks == 1), CancellationToken.None);

        Assert.Equal(LegajoClassificationErrors.ConsentRevoked, result.ErrorCode);
        Assert.Equal(1, handler.Hits);
        Assert.True(result.RequestSent);
    }

    [Fact]
    public async Task ClassifyAsync_NoApiKey_NoRequest()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, ClassificationEnvelopes.ToolUse()));
        var (pdf, quality) = NativeDocument();

        var result = await BuildSut(handler, apiKey: null).ClassifyAsync(Context, pdf, quality, Allowed, CancellationToken.None);

        Assert.Equal(LegajoClassificationErrors.NotConfigured, result.ErrorCode);
        Assert.Equal(0, handler.Hits);
    }

    // ── Retries ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ClassifyAsync_ServerErrorThenSuccess_Retries()
    {
        var handler = new ScriptedHandler(
            (HttpStatusCode.ServiceUnavailable, "{}"),
            (HttpStatusCode.OK, ClassificationEnvelopes.ToolUse()));
        var (pdf, quality) = NativeDocument();

        var result = await BuildSut(handler).ClassifyAsync(Context, pdf, quality, Allowed, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, handler.Hits);
    }

    [Fact]
    public async Task ClassifyAsync_TimeoutThenSuccess_StillRetries()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, ClassificationEnvelopes.ToolUse())) { TimeoutsFirst = 2 };
        var (pdf, quality) = NativeDocument();

        var result = await BuildSut(handler).ClassifyAsync(Context, pdf, quality, Allowed, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(3, handler.Hits);
    }

    [Fact]
    public async Task ClassifyAsync_ServerErrorEveryTime_GivesUpAfterThreeAttempts()
    {
        var handler = new ScriptedHandler((HttpStatusCode.InternalServerError, "{}"));
        var (pdf, quality) = NativeDocument();

        var result = await BuildSut(handler).ClassifyAsync(Context, pdf, quality, Allowed, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(LegajoClassificationErrors.ApiError, result.ErrorCode);
        Assert.Equal(3, handler.Hits);
        Assert.Equal(500, result.HttpStatus);
    }

    [Fact]
    public async Task ClassifyAsync_RateLimited_DoesNotRetry()
    {
        var handler = new ScriptedHandler((HttpStatusCode.TooManyRequests, "{}"));
        var (pdf, quality) = NativeDocument();

        var result = await BuildSut(handler).ClassifyAsync(Context, pdf, quality, Allowed, CancellationToken.None);

        Assert.Equal(LegajoClassificationErrors.RateLimited, result.ErrorCode);
        Assert.Equal(1, handler.Hits);
    }
}
