// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-28

using System.Reflection;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.Legajo;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// <see cref="LegajoFailureCodes"/>: LegajoDocumentResponse.LastErrorCode carries a vocabulary code, only
/// for a Failed document, and never whatever else the shared column may hold.
/// </summary>
public sealed class LegajoFailureCodesTests
{
    [Fact]
    public void Known_CoversEveryCodeOfBothStages_AsIsAndPrefixed()
    {
        var codes = Constants(typeof(LegajoClassificationErrors))
            .Concat(Constants(typeof(LegajoExtractionErrors)).Where(c => c != LegajoExtractionErrors.StagePrefix))
            .ToList();

        Assert.NotEmpty(codes);
        foreach (var code in codes)
        {
            Assert.Contains(code, LegajoFailureCodes.Known);
            Assert.Contains(LegajoExtractionErrors.ForDocument(code), LegajoFailureCodes.Known);
        }
    }

    [Theory]
    [InlineData("rate_limited")]
    [InlineData("claude_timeout")]
    [InlineData("attempts_exhausted")]
    [InlineData("extraction_timeout")]
    [InlineData("extraction_rate_limited")]
    [InlineData("extraction_too_many_pages")]
    [InlineData("extraction_unexpected")]
    public void Failed_KnownCode_AsStored(string code)
        => Assert.Equal(code, LegajoFailureCodes.ForResponse(LegajoStatuses.Failed, code));

    [Theory]
    [InlineData("HttpRequestException")]
    [InlineData("claude_api_error: 500 Internal Server Error")]
    [InlineData("extraction_")]
    [InlineData("")]
    [InlineData(null)]
    public void Failed_AnythingElse_Unexpected(string? stored)
        => Assert.Equal("unexpected", LegajoFailureCodes.ForResponse(LegajoStatuses.Failed, stored));

    [Theory]
    [InlineData(null)]
    [InlineData(LegajoStatuses.Uploaded)]
    [InlineData(LegajoStatuses.Queued)]
    [InlineData(LegajoStatuses.ReadyForExtraction)]
    [InlineData(LegajoStatuses.NeedsReview)]
    [InlineData(LegajoStatuses.Ready)]
    [InlineData(LegajoStatuses.Blocked)]
    public void NotFailed_Null_EvenWithAStaleCode(string? status)
        => Assert.Null(LegajoFailureCodes.ForResponse(status, "extraction_rate_limited"));

    private static IEnumerable<string> Constants(Type type)
        => type.GetFields(BindingFlags.Public | BindingFlags.Static)
               .Where(f => f.IsLiteral && f.FieldType == typeof(string))
               .Select(f => (string)f.GetRawConstantValue()!);
}
