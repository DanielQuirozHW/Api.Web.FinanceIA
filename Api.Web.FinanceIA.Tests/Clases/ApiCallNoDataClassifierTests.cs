// Project : Api.Web.FinanceIA.Tests
// Created : 2026-10-28

using Api.Web.FinanceIA.Clases;
using Api.Web.FinanceIA.Models.Financia;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Clases;

/// <summary>
/// <see cref="ApiCallNoDataClassifier"/>: which failed ApiCallLogs rows are a vendor's "no data" answer
/// rather than an integration failure. Only LUFE (by status, or by the "no data" marker its writer puts on a
/// 2xx) and ARCA (by the writer's null error) have one.
/// </summary>
public sealed class ApiCallNoDataClassifierTests
{
    [Theory]
    // LUFE: any 4xx except 408/429 is "no legajo / not shared with this entity"
    [InlineData("LUFE", 404, true,  true)]
    [InlineData("LUFE", 401, true,  true)]
    [InlineData("LUFE", 400, true,  true)]
    [InlineData("LUFE", 403, true,  true)]
    [InlineData("LUFE", 404, false, true)]
    [InlineData("LUFE", 408, true,  false)]
    [InlineData("LUFE", 429, true,  false)]
    [InlineData("LUFE", 500, true,  false)]
    [InlineData("LUFE", 503, true,  false)]
    [InlineData("LUFE", 200, true,  false)]   // "Parse error": a 2xx with no LUFE marker stays a failure
    [InlineData("LUFE", null, true, false)]   // network error or timeout
    // ARCA: a failed row with no error text is "not found"; one with text is a failed call
    [InlineData("ARCA", 502, false, true)]
    [InlineData("ARCA", 502, true,  false)]
    [InlineData("ARCA", null, false, true)]
    // Every other service: a failure is a failure
    [InlineData("NOSIS", 404, false, false)]
    [InlineData("NOSIS", 500, true,  false)]
    [InlineData("Claude", 400, false, false)]
    [InlineData("ClaudeChat", 404, false, false)]
    [InlineData("BYMA", 404, false, false)]
    [InlineData("lufe", 404, true, false)]   // ordinal, like ApiCallLogs.Service everywhere else
    public void AFailedRow_IsNoDataOnlyForTheVendorRule(string service, int? status, bool hasErrorMessage, bool expected)
        => Assert.Equal(expected, ApiCallNoDataClassifier.IsNoData(
            service, isSuccess: false, status, hasErrorMessage, hasLufeNoDataMarker: false));

    /// <summary>
    /// The texts LufeService writes for "Lufe answered 2xx with nothing usable" read as no data; "Parse
    /// error" (a malformed body) and every failure text stay failures. The flag is what the outcomes
    /// query computes in SQL from the LufeCallOutcome prefixes.
    /// </summary>
    [Theory]
    [InlineData("Empty (HTTP 200)",     200, true)]
    [InlineData("Empty (HTTP 204)",     204, true)]
    [InlineData("No legajo (HTTP 200)", 200, true)]
    [InlineData("No legajo (HTTP 404)", 404, true)]    // already no data by its status
    [InlineData("Parse error",          200, false)]
    [InlineData("Lookup failed (HTTP 500)", 500, false)]
    [InlineData("Fetch failed (HTTP 503)",  503, false)]
    [InlineData("Fetch failed (network error or timeout)", null, false)]
    [InlineData("Empty (HTTP 500)",     500, false)]   // a marker never turns a 5xx into no data
    [InlineData("Empty (HTTP )",        null, false)]  // nor a row with no status
    public void ALufeRow_IsNoDataFromTheMarkerOurCodeWrote(string errorMessage, int? status, bool expected)
        => Assert.Equal(expected, ApiCallLogRowMarkers.IsNoData(RescoreSource.Lufe, isSuccess: false, status, errorMessage));

    [Fact]
    public void TheLufeMarkers_AreTheTextsLufeServiceHasAlwaysWritten()
    {
        // Byte-identical to the rows already in production, so they classify retroactively.
        Assert.Equal("Empty (HTTP 200)",     LufeCallOutcome.DescribeEmptyAnswer(200));
        Assert.Equal("No legajo (HTTP 200)", LufeCallOutcome.DescribeNoLegajo(200));
        Assert.Equal("No legajo (HTTP 404)", LufeCallOutcome.DescribeNoLegajo(404));
        Assert.False(ApiCallLogRowMarkers.HasLufeNoDataMarker("Parse error"));
    }

    [Theory]
    [InlineData("ARCA",  502)]
    [InlineData("NOSIS", 200)]
    [InlineData("Claude", 200)]
    public void TheLufeMarker_MeansNothingForAnotherService(string service, int status)
        => Assert.False(ApiCallNoDataClassifier.IsNoData(
            service, isSuccess: false, status, hasErrorMessage: true, hasLufeNoDataMarker: true));

    [Theory]
    [InlineData("LUFE", 200)]
    [InlineData("LUFE", 404)]
    [InlineData("ARCA", 200)]
    [InlineData("NOSIS", 200)]
    public void ASuccessfulRow_IsNeverNoData(string service, int status)
        => Assert.False(ApiCallNoDataClassifier.IsNoData(
            service, isSuccess: true, status, hasErrorMessage: false, hasLufeNoDataMarker: true));
}
