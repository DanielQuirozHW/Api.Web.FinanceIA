// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-01

using Api.Web.FinanceIA.Clases;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// Pins <see cref="CorsOriginCheck"/>, the startup check behind the Warning for a
/// <c>Cors:AllowedOrigins</c> entry that can never match a browser <c>Origin</c> header. The trailing
/// slash case is the one that actually shipped (<c>https://financia-portal.vercel.app/</c>).
/// </summary>
public sealed class CorsOriginCheckTests
{
    [Theory]
    [InlineData("https://financia-portal.vercel.app/")]
    [InlineData("https://a.com/path")]
    [InlineData("https://a.com?x=1")]
    [InlineData("https://a.com:443")]
    [InlineData("notaurl")]
    [InlineData("")]
    [InlineData("   ")]
    public void Flags_an_entry_that_is_not_a_bare_origin(string origin)
    {
        Assert.Equal(new[] { 0 }, CorsOriginCheck.FindNonOriginIndexes([origin]));
    }

    [Theory]
    [InlineData("http://localhost:3000")]
    [InlineData("https://sgr-portales.financia.click")]
    [InlineData("https://SGR-Portales.financia.click")]
    [InlineData(" https://x.financia.click ")]
    public void Accepts_a_bare_origin(string origin)
    {
        Assert.Empty(CorsOriginCheck.FindNonOriginIndexes([origin]));
    }

    [Fact]
    public void Reports_every_offending_index_in_order()
    {
        string[] origins =
        [
            "http://localhost:3000",
            "https://portal.example/",
            "https://x.financia.click",
            "https://y.financia.click/login"
        ];

        Assert.Equal(new[] { 1, 3 }, CorsOriginCheck.FindNonOriginIndexes(origins));
    }

    [Fact]
    public void An_empty_list_has_nothing_to_report()
    {
        Assert.Empty(CorsOriginCheck.FindNonOriginIndexes([]));
    }
}
