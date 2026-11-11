// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-10

using Api.Web.FinanceIA.Clases;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// Covers <see cref="LocalDocumentStorageService.ResolveBaseDirectory"/> — the pure
/// resolution of the storage base directory (no I/O, no environment access):
/// an explicit <c>Storage:DocumentsPath</c> wins; otherwise <c>%HOME%\financia-uploads</c>
/// when HOME is present; otherwise the legacy temp fallback so local dev and tests keep working.
/// </summary>
public sealed class LocalDocumentStorageServiceTests
{
    [Fact]
    public void ResolveBaseDirectory_WhenConfiguredPathSet_ReturnsConfiguredPath()
    {
        var result = LocalDocumentStorageService.ResolveBaseDirectory("/data/docs", "/home/site");

        Assert.Equal("/data/docs", result);
    }

    [Fact]
    public void ResolveBaseDirectory_WhenConfiguredPathSetAndHomePresent_PrefersConfiguredPath()
    {
        // The explicit setting must win over HOME.
        var result = LocalDocumentStorageService.ResolveBaseDirectory("/data/docs", "/home/site");

        Assert.Equal("/data/docs", result);
    }

    [Fact]
    public void ResolveBaseDirectory_WhenNoConfiguredPathButHomePresent_ReturnsHomeCombined()
    {
        var result = LocalDocumentStorageService.ResolveBaseDirectory(null, "/home/site");

        Assert.Equal(Path.Combine("/home/site", "financia-uploads"), result);
    }

    [Fact]
    public void ResolveBaseDirectory_WhenNeitherSet_ReturnsLegacyTempFallback()
    {
        var result = LocalDocumentStorageService.ResolveBaseDirectory(null, null);

        Assert.Equal("/tmp/financia-uploads", result);
    }

    [Fact]
    public void ResolveBaseDirectory_WhenConfiguredPathIsWhitespace_FallsThroughToHome()
    {
        // Whitespace-only settings (a common "unset" shape from empty appsettings keys)
        // are treated as absent.
        var result = LocalDocumentStorageService.ResolveBaseDirectory("   ", "/home/site");

        Assert.Equal(Path.Combine("/home/site", "financia-uploads"), result);
    }
}
