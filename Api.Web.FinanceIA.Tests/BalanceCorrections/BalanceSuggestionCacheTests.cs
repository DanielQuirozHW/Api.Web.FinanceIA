// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-03

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios.BalanceCorrections;
using Xunit;

namespace Api.Web.FinanceIA.Tests.BalanceCorrections;

/// <summary>The suggestion cache's key and size cap (<see cref="BalanceSuggestionCache"/>).</summary>
public sealed class BalanceSuggestionCacheTests
{
    private static readonly Guid ResultId = Guid.Parse("8d3b0d0e-3f6a-4f0b-9f4e-0c1a2b3c4d5e");

    [Fact]
    public void The_key_is_stable_for_the_same_tenant_result_and_figures()
    {
        Assert.Equal(
            BalanceSuggestionCache.Key("tenant-a", ResultId, """{"assets":1}"""),
            BalanceSuggestionCache.Key("tenant-a", ResultId, """{"assets":1}"""));
    }

    [Fact]
    public void The_key_changes_with_the_tenant_the_result_or_one_byte_of_the_figures()
    {
        var key = BalanceSuggestionCache.Key("tenant-a", ResultId, """{"assets":1}""");

        Assert.NotEqual(key, BalanceSuggestionCache.Key("tenant-b", ResultId, """{"assets":1}"""));
        Assert.NotEqual(key, BalanceSuggestionCache.Key("tenant-a", Guid.NewGuid(), """{"assets":1}"""));
        Assert.NotEqual(key, BalanceSuggestionCache.Key("tenant-a", ResultId, """{"assets":2}"""));
    }

    [Fact]
    public void The_key_carries_a_hash_never_the_figures()
    {
        var key = BalanceSuggestionCache.Key("tenant-a", ResultId, """{"assets":123456789}""");

        Assert.DoesNotContain("123456789", key);
        Assert.Equal(64, key[(key.LastIndexOf(':') + 1)..].Length);   // SHA-256, hex
    }

    [Fact]
    public void A_small_response_is_cacheable()
    {
        Assert.True(BalanceSuggestionCache.IsCacheable(Response(pages: 3, pageBytes: 450 * 1024)));
    }

    [Fact]
    public void A_response_over_8_mb_is_not_cacheable()
    {
        var response = Response(pages: 20, pageBytes: 450 * 1024);

        Assert.True(BalanceSuggestionCache.EstimatedBytes(response) > BalanceSuggestionCache.MaxEntryBytes);
        Assert.False(BalanceSuggestionCache.IsCacheable(response));
    }

    private static BalanceCorrectionSuggestionResponse Response(int pages, int pageBytes) => new()
    {
        Outcome = BalanceSuggestionOutcomes.Fixes,
        Message = "ok",
        Model   = "claude-sonnet-4-6",
        Suggestions =
        [
            new BalanceCorrectionSuggestionItem
            {
                Path = "current_liabilities", SuggestedValue = 1d, Page = 1,
                Evidence = new BalanceSuggestionEvidenceResponse("image/jpeg", new string('A', 200 * 1024)),
            },
        ],
        Pages = Enumerable.Range(1, pages)
            .Select(p => new BalanceSuggestionPageResponse(p, new BalanceSuggestionEvidenceResponse("image/jpeg", new string('A', pageBytes))))
            .ToList(),
    };
}
