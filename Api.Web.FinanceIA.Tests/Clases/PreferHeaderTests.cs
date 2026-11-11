// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-19

using Api.Web.FinanceIA.Clases;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>Covers <see cref="PreferHeader"/>: RFC 7240 parsing of the respond-async preference.</summary>
public sealed class PreferHeaderTests
{
    [Theory]
    [InlineData("respond-async")]
    [InlineData("Respond-Async")]
    [InlineData("respond-async, wait=10")]
    [InlineData("wait=10, respond-async")]
    [InlineData("  respond-async  ")]
    [InlineData("respond-async; foo=bar")]
    [InlineData("handling=lenient,respond-async")]
    public void RecognisesRespondAsync(string value)
    {
        var headers = new HeaderDictionary { ["Prefer"] = value };

        Assert.True(PreferHeader.RequestsRespondAsync(headers));
    }

    [Theory]
    [InlineData("")]
    [InlineData("return=minimal")]
    [InlineData("respond-sync")]
    [InlineData("wait=10")]
    [InlineData("respond-asynchronously")]
    public void IgnoresEverythingElse(string value)
    {
        var headers = new HeaderDictionary { ["Prefer"] = value };

        Assert.False(PreferHeader.RequestsRespondAsync(headers));
    }

    [Fact]
    public void NoHeader_IsNotAsync()
        => Assert.False(PreferHeader.RequestsRespondAsync(new HeaderDictionary()));

    [Fact]
    public void RecognisesRespondAsync_InASecondPreferHeader()
    {
        var headers = new HeaderDictionary { ["Prefer"] = new StringValues(["return=minimal", "respond-async"]) };

        Assert.True(PreferHeader.RequestsRespondAsync(headers));
    }
}
