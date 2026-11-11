// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-28

using System.Net;
using System.Security.Cryptography;
using System.Text;
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
/// Golden payloads of the legajo CLASSIFICATION request, registered against the Phase 2a code before
/// the Messages plumbing moved into the shared client (M10 Phase 2b). The classification wire format
/// must stay byte-identical: any change to these hashes is a behaviour change of 2a, which needs its
/// own PromptVersion bump — not a refactor.
///
/// <para>
/// Both inputs are synthetic and deterministic: a fixed document block (iText stamps a creation date,
/// so a generated PDF cannot be hashed), and the image mode over a blank PDF whose pages the stub
/// renderer turns into two fixed four-byte JPEGs.
/// </para>
/// </summary>
public sealed class LegajoClassificationPayloadGoldenTests
{
    private const string DocumentModeGolden = "3c472e9ab366c657e287d62f3830e78ae7d1da009c50054038cb70e2a87f54a9";
    private const string ImagesModeGolden   = "36a750be1ac1bdf1f69df0b8a3a5e3266c43766f6059d248b3b10d3627f4812a";

    [Fact]
    public void DocumentMode_RequestBody_IsByteIdenticalToPhase2a()
    {
        var blocks = new List<JsonNode>
        {
            new JsonObject
            {
                ["type"] = "document",
                ["source"] = new JsonObject
                {
                    ["type"]       = "base64",
                    ["media_type"] = "application/pdf",
                    ["data"]       = Convert.ToBase64String(Encoding.ASCII.GetBytes("%PDF-1.7 synthetic"))
                }
            },
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = LegajoClassificationPrompt.BuildUserText(1, 1)
            }
        };

        var body = LegajoClassificationService.BuildRequestJson("claude-haiku-4-5", blocks);

        AssertGolden(DocumentModeGolden, body);
    }

    [Fact]
    public async Task ImagesMode_RequestBody_IsByteIdenticalToPhase2a()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, ClassificationEnvelopes.ToolUse()));
        var sut = new LegajoClassificationService(
            LegajoTestClients.Messages(handler),
            new StubPageRenderer(),
            Options.Create(new LegajoClassificationOptions { ApiKey = "test-key" }),
            NullLogger<LegajoClassificationService>.Instance);
        var pdf = LegajoPdfFactory.BlankPdf(2);

        var result = await sut.ClassifyAsync(
            new LegajoCallContext(LegajoTestData.Tenant, Guid.NewGuid()),
            pdf, PageTextQualityAnalyzer.Analyze(pdf), _ => Task.FromResult(true), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(LegajoReadModes.Images, result.ReadMode);
        AssertGolden(ImagesModeGolden, Assert.Single(handler.RequestBodies));
    }

    private static void AssertGolden(string expected, string body)
    {
        var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        Assert.True(expected == actual,
            $"The classification request body changed (SHA-256 {actual}). Phase 2a's wire format must stay byte-identical.");
    }
}
