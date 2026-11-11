// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-08

using System.Net;
using Api.Web.FinanceIA.Servicios.Pdf;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios.PdfBranding;

/// <summary>
/// The logo client's ConnectCallback refuses before opening any socket. Only refusals are exercised: an allowed
/// address would open a real connection, and these tests never touch the network.
/// </summary>
public sealed class BrandingLogoConnectPolicyTests
{
    private static Func<string, CancellationToken, Task<IPAddress[]>> Resolving(params string[] addresses)
    {
        return (_, _) => Task.FromResult(addresses.Select(IPAddress.Parse).ToArray());
    }

    [Fact]
    public async Task HostWithOnePrivateAddressAmongPublicOnes_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<BrandingLogoBlockedAddressException>(async () =>
            await BrandingLogoConnectPolicy.ConnectAsync(
                new DnsEndPoint("cdn.example.com", 443), Resolving("93.184.216.34", "10.0.0.1"), CancellationToken.None));

        Assert.Contains("cdn.example.com", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("10.0.0.1", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:192.168.0.10")]
    public async Task HostResolvingToABlockedAddress_IsRefused(string address)
    {
        await Assert.ThrowsAsync<BrandingLogoBlockedAddressException>(async () =>
            await BrandingLogoConnectPolicy.ConnectAsync(
                new DnsEndPoint("logo.example.com", 443), Resolving(address), CancellationToken.None));
    }

    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("10.0.0.5")]
    [InlineData("::1")]
    public async Task LiteralIpHost_IsChecked_WithoutCallingTheResolver(string literal)
    {
        bool resolverCalled = false;
        Task<IPAddress[]> Resolve(string host, CancellationToken ct)
        {
            resolverCalled = true;
            return Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") });
        }

        await Assert.ThrowsAsync<BrandingLogoBlockedAddressException>(async () =>
            await BrandingLogoConnectPolicy.ConnectAsync(new DnsEndPoint(literal, 443), Resolve, CancellationToken.None));
        Assert.False(resolverCalled);
    }

    [Fact]
    public async Task HostResolvingToNothing_IsRefused()
    {
        await Assert.ThrowsAsync<BrandingLogoBlockedAddressException>(async () =>
            await BrandingLogoConnectPolicy.ConnectAsync(
                new DnsEndPoint("nowhere.example.com", 443), Resolving(), CancellationToken.None));
    }
}
