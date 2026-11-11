// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-08

using System.Net;
using Api.Web.FinanceIA.Servicios.Pdf;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios.PdfBranding;

/// <summary>The SSRF address rules of the tenant-logo fetch: public unicast only, every embedded-IPv4 form included.</summary>
public sealed class BrandingLogoAddressPolicyTests
{
    [Theory]
    // IPv4
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]   // the cloud metadata endpoint
    [InlineData("169.254.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("192.0.0.170")]
    [InlineData("198.18.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.250")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    // IPv6
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("febf::1")]
    [InlineData("fec0::1")]
    [InlineData("fc00::1")]
    [InlineData("fd00::1")]
    [InlineData("ff02::1")]
    // IPv4 embedded in IPv6
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::10.0.0.1")]
    [InlineData("64:ff9b::a00:1")]            // NAT64 of 10.0.0.1
    [InlineData("64:ff9b::a9fe:a9fe")]        // NAT64 of 169.254.169.254
    [InlineData("2002:a00:1::1")]             // 6to4 of 10.0.0.1
    [InlineData("2002:a9fe:a9fe::1")]         // 6to4 of 169.254.169.254
    public void NonPublicAddresses_AreBlocked(string address) =>
        Assert.False(BrandingLogoAddressPolicy.IsAllowed(IPAddress.Parse(address)), address);

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.1")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("169.253.1.1")]
    [InlineData("192.169.0.1")]
    [InlineData("198.20.0.1")]
    [InlineData("223.255.255.254")]
    [InlineData("2606:4700::1111")]
    [InlineData("2a00:1450:4001::200e")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("64:ff9b::808:808")]          // NAT64 of 8.8.8.8
    [InlineData("2002:808:808::1")]           // 6to4 of 8.8.8.8
    public void PublicAddresses_AreAllowed(string address) =>
        Assert.True(BrandingLogoAddressPolicy.IsAllowed(IPAddress.Parse(address)), address);
}
