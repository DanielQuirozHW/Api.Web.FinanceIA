// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-18

using System.Text.Json;
using System.Text.Json.Serialization;
using Api.Web.FinanceIA.Models.Lufe;
using Api.Web.FinanceIA.Servicios;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Covers LufeService.MergeVentasComprasMensual / IsYearMonth — the pure part of the
/// entidades/{cuit}/indicadorespostbalance integration. Reached via InternalsVisibleTo, so no
/// HttpClient stub and no DI are needed.
///
/// The main fixture is a VERBATIM live response (captured 2026-08-18): four series, 18 keys each,
/// keys in DESCENDING order, amounts as unquoted JSON numbers, nulls for unfiled months.
/// </summary>
public sealed class LufePostBalanceMergeTests
{
    // Options must match LufeService.JsonOpts, so the tests exercise the same binding the
    // service does — including AllowReadingFromString.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    // ── Fixture: real response, unmodified ────────────────────────────────────
    // 18 periods, 202502..202607 descending. 202607 is null in all four series (the month is
    // open, nothing filed yet) so it must be dropped, leaving 17 rows.
    // 202606 shows the decomposition: ventas 105223176.11 = melo 105223176.11 + expo 0. The keys
    // stay melo/expo here — this is the vendor's payload verbatim; we rename only on the way out,
    // to VentasMercadoLocal / VentasExportacion.
    // compras trails ventas by several months (reporting lag) — those nulls are NOT zeros.
    private const string FixtureJson = """
        {"ventas":{"202607":null,"202606":105223176.11,"202605":100692601.04,"202604":97509174.18,"202603":176631392.4,"202602":103604967.23,"202601":108161965.47,"202512":104604678.37,"202511":106581749.06,"202510":101994547.81,"202509":95474985.29,"202508":89786544.8,"202507":85778425.46,"202506":89094760.55,"202505":76593118.49,"202504":79182939.72,"202503":77367734.62,"202502":77595125.65},"compras":{"202607":null,"202606":null,"202605":null,"202604":28322675.78,"202603":28612333.58,"202602":15175959.02,"202601":null,"202512":36173694.79,"202511":33300412.5,"202510":null,"202509":null,"202508":null,"202507":null,"202506":null,"202505":47304029.18,"202504":25419230.24,"202503":41474399.46,"202502":27266833.8},"melo":{"202607":null,"202606":105223176.11,"202605":100692601.04,"202604":97509174.18,"202603":176631392.4,"202602":103604967.23,"202601":108161965.47,"202512":null,"202511":106581749.06,"202510":null,"202509":null,"202508":null,"202507":null,"202506":null,"202505":null,"202504":null,"202503":null,"202502":null},"expo":{"202607":null,"202606":0,"202605":0,"202604":0,"202603":0,"202602":0,"202601":0,"202512":null,"202511":0,"202510":null,"202509":null,"202508":null,"202507":null,"202506":null,"202505":null,"202504":null,"202503":null,"202502":null}}
        """;

    private static List<LufeVentaCompraMes>? MergeOf(string json) =>
        LufeService.MergeVentasComprasMensual(
            JsonSerializer.Deserialize<LufePostBalanceResponse>(json, JsonOpts));

    // ── The real response ─────────────────────────────────────────────────────

    [Fact]
    public void RealResponse_DropsTheAllNullMonth_AndReturnsTheRestAscending()
    {
        var merged = MergeOf(FixtureJson);

        Assert.NotNull(merged);
        Assert.Equal(17, merged!.Count);                 // 18 keys − 202607 (null everywhere)
        Assert.DoesNotContain(merged, m => m.Periodo == "202607");

        var periodos = merged.Select(m => m.Periodo).ToArray();
        Assert.Equal("202502", periodos[0]);
        Assert.Equal("202606", periodos[^1]);
        Assert.Equal(periodos.OrderBy(p => p, StringComparer.Ordinal), periodos);
    }

    [Fact]
    public void RealResponse_KeepsAmountsExact_NoBinaryFloatingPointDrift()
    {
        var merged = MergeOf(FixtureJson)!;
        var junio  = merged.Single(m => m.Periodo == "202606");

        // Exact decimal equality: a double round-trip would not survive this.
        Assert.Equal(105223176.11m, junio.Ventas);
        Assert.Equal(105223176.11m, junio.VentasMercadoLocal);
        Assert.Equal(0m, junio.VentasExportacion);
        Assert.Null(junio.Compras);                      // reporting lag, not a zero

        // Ventas decomposes into local + export where Lufe reports the split (vendor melo + expo).
        Assert.Equal(junio.Ventas, junio.VentasMercadoLocal + junio.VentasExportacion);
    }

    [Fact]
    public void RealResponse_MonthWithVentasButNoDecomposition_KeepsVentasAndNullsTheSplit()
    {
        var merged   = MergeOf(FixtureJson)!;
        var diciembre = merged.Single(m => m.Periodo == "202512");

        Assert.Equal(104604678.37m, diciembre.Ventas);
        Assert.Equal(36173694.79m, diciembre.Compras);
        Assert.Null(diciembre.VentasMercadoLocal);
        Assert.Null(diciembre.VentasExportacion);
    }

    // ── Union / omission rules ────────────────────────────────────────────────

    [Fact]
    public void PeriodPresentInOnlyOneSeries_IsEmitted_WithNullsOnTheMissingSides()
    {
        var merged = MergeOf("""{"compras":{"202503":123.45}}""");

        var row = Assert.Single(merged!);
        Assert.Equal("202503", row.Periodo);
        Assert.Equal(123.45m, row.Compras);
        Assert.Null(row.Ventas);
        Assert.Null(row.VentasMercadoLocal);
        Assert.Null(row.VentasExportacion);
    }

    [Fact]
    public void UnionSpansEverySeries_EvenWhenNoSeriesHasEveryPeriod()
    {
        var merged = MergeOf("""
            {"ventas":{"202501":1},"compras":{"202502":2},"melo":{"202503":3},"expo":{"202504":4}}
            """)!;

        Assert.Equal(new[] { "202501", "202502", "202503", "202504" },
                     merged.Select(m => m.Periodo));
    }

    [Fact]
    public void PeriodNullInEverySeries_IsOmitted_ButAZeroIsKept()
    {
        var merged = MergeOf("""
            {"ventas":{"202501":null,"202502":null},
             "compras":{"202501":null,"202502":null},
             "melo":{"202501":null,"202502":null},
             "expo":{"202501":null,"202502":0}}
            """)!;

        // 202501 is empty and goes; 202502 survives on the vendor's expo:0 alone — 0 is a real figure.
        var row = Assert.Single(merged);
        Assert.Equal("202502", row.Periodo);
        Assert.Equal(0m, row.VentasExportacion);
    }

    // ── Degenerate responses collapse to null, never to an empty list ─────────

    [Theory]
    [InlineData("null")]                                        // HTTP 200 with a literal null body
    [InlineData("{}")]                                          // no series at all
    [InlineData("""{"ventas":null,"compras":null}""")]           // series present but null
    [InlineData("""{"ventas":{},"compras":{}}""")]               // series present but empty
    [InlineData("""{"ventas":{"202501":null},"compras":{"202501":null}}""")] // only empty months
    [InlineData("""{"otracosa":{"202501":1}}""")]                // no series we recognise
    public void NothingUsable_YieldsNull_NotAnEmptyList(string json)
    {
        Assert.Null(MergeOf(json));
    }

    [Fact]
    public void NullResponse_YieldsNull()
    {
        Assert.Null(LufeService.MergeVentasComprasMensual(null));
    }

    // ── Vendor-shape hazards ──────────────────────────────────────────────────

    [Fact]
    public void QuotedAmounts_StillParse_ViaAllowReadingFromString()
    {
        // Guards the culture question: were Lufe ever to switch to strings, the invariant
        // form must still bind. (Today the wire carries unquoted JSON numbers.)
        var merged = MergeOf("""{"ventas":{"202501":"105223176.11"}}""")!;

        Assert.Equal(105223176.11m, Assert.Single(merged).Ventas);
    }

    [Fact]
    public void DuplicateKeysInOneSeries_AreLastWins_AndDoNotThrow()
    {
        // System.Text.Json assigns dictionary entries through the indexer, so a repeated key
        // overwrites rather than throwing the way ToDictionary would. Pinned, not assumed.
        var merged = MergeOf("""{"ventas":{"202501":1,"202501":2}}""")!;

        Assert.Equal(2m, Assert.Single(merged).Ventas);
    }

    [Theory]
    [InlineData("""{"ventas":{"202501":1e40}}""")]   // overflows decimal
    [InlineData("""{"ventas":[]}""")]                // array where an object belongs
    [InlineData("""{"ventas":{"202501":"abc"}}""")]  // unparseable amount
    [InlineData("")]                                 // empty body
    public void MalformedPayloads_SurfaceAsJsonException_ForTheCallerToNull(string json)
    {
        // The service wraps this in catch(Exception) → the section becomes null while the rest
        // of LufeData is untouched. Asserted here at the deserialization boundary.
        Assert.Throws<JsonException>(() => MergeOf(json));
    }

    // ── Period-key validation ─────────────────────────────────────────────────

    [Theory]
    [InlineData("202502", true)]
    [InlineData("199001", true)]     // lower year bound, inclusive
    [InlineData("202512", true)]     // month 12, inclusive
    [InlineData("202613", false)]    // month out of range → phantom future point
    [InlineData("202600", false)]    // Lufe's annual "YYYY00" shape is not a month
    [InlineData("198912", false)]    // below the year floor
    [InlineData("2026", false)]      // year only — would sort mid-series
    [InlineData("20260", false)]
    [InlineData("2026011", false)]
    [InlineData("total", false)]     // a summary key must never become a chart point
    [InlineData("2026-1", false)]
    [InlineData(" 20261", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsYearMonth_AcceptsOnlyWellFormedMonthKeys(string? periodo, bool expected)
    {
        Assert.Equal(expected, LufeService.IsYearMonth(periodo));
    }

    // ── Wire contract ─────────────────────────────────────────────────────────

    [Fact]
    public void BlobIsPascalCaseAtTheRoot_HttpIsCamelCase_NestedMembersKeepTheirExplicitKey()
    {
        // The portal consumes
        // ventasComprasMensual[].{periodo,ventas,compras,ventasMercadoLocal,ventasExportacion}.
        // That casing comes from two different serializers, so pin both ends here rather than
        // trusting that nobody changes a naming policy.
        var lufe = new LufeData { VentasComprasMensual = MergeOf(FixtureJson) };

        // As persisted: EntitiesController uses a plain JsonSerializer.Serialize(lufeData),
        // no options → root PascalCase, nested keys fixed by [JsonPropertyName].
        var blob = JsonSerializer.Serialize(lufe);
        Assert.Contains("\"VentasComprasMensual\":[", blob);
        Assert.Contains("\"periodo\":\"202502\"", blob);
        Assert.Contains("\"ventasMercadoLocal\":105223176.11", blob);
        Assert.Contains("\"ventasExportacion\":0", blob);
        Assert.DoesNotContain("\"Periodo\":", blob);

        // As served: MVC's JsonSerializerDefaults.Web → root camelCase, nested unchanged.
        var http = JsonSerializer.Serialize(lufe, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"ventasComprasMensual\":[", http);
        Assert.Contains("\"periodo\":\"202502\"", http);
        Assert.Contains("\"compras\":27266833.8", http);
        Assert.Contains("\"ventasMercadoLocal\":105223176.11", http);
        Assert.Contains("\"ventasExportacion\":0", http);

        // The vendor's abbreviations stop at the transport DTO — they must not reach either
        // output layer. This is the assertion that actually proves the rename took.
        foreach (var output in new[] { blob, http })
        {
            Assert.DoesNotContain("\"melo\"", output);
            Assert.DoesNotContain("\"expo\"", output);
            Assert.DoesNotContain("\"Melo\"", output);
            Assert.DoesNotContain("\"Expo\"", output);
        }
    }

    [Fact]
    public void PersistedBlob_RoundTripsThroughBothReaderConfigurations()
    {
        var blob = JsonSerializer.Serialize(
            new LufeData { VentasComprasMensual = MergeOf(FixtureJson) });

        // Two reader shapes exist in the controllers: one with AllowReadingFromString, one
        // without. The section must survive both, since the amounts persist as JSON numbers.
        var readers = new[]
        {
            JsonOpts,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        };

        foreach (var reader in readers)
        {
            var round = JsonSerializer.Deserialize<LufeData>(blob, reader);

            Assert.NotNull(round?.VentasComprasMensual);
            Assert.Equal(17, round!.VentasComprasMensual!.Count);
            Assert.Equal("202502", round.VentasComprasMensual[0].Periodo);
            Assert.Equal(105223176.11m, round.VentasComprasMensual[^1].Ventas);
            Assert.Equal(105223176.11m, round.VentasComprasMensual[^1].VentasMercadoLocal);
            Assert.Equal(0m, round.VentasComprasMensual[^1].VentasExportacion);
        }
    }

    [Fact]
    public void OlderBlobsWithoutTheSection_StillDeserialize_WithTheFieldNull()
    {
        // Every row stored before this change lacks the property; it must read back as null
        // rather than failing the whole snapshot.
        var legacy = JsonSerializer.Deserialize<LufeData>("""{"Entidad":{"cuit":30716327279}}""", JsonOpts);

        Assert.NotNull(legacy);
        Assert.Null(legacy!.VentasComprasMensual);
        Assert.Equal(30716327279, legacy.Entidad!.Cuit);
    }

    [Fact]
    public void MalformedKeys_AreDroppedFromTheSeries()
    {
        var merged = MergeOf("""
            {"ventas":{"total":999,"2026":888,"202613":777,"":666,"202505":1}}
            """)!;

        var row = Assert.Single(merged);
        Assert.Equal("202505", row.Periodo);
        Assert.Equal(1m, row.Ventas);
    }
}
