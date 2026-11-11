// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-26

using Api.Web.FinanceIA.Data;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Data;

/// <summary>
/// Covers the C# body of <see cref="SqlJsonFunctions.JsonValue"/>.
///
/// <para>
/// That body is NOT dead code behind a translation. It runs whenever the provider is
/// non-relational — which is every behavioural test in this project, since they all use the InMemory
/// provider. So the risk it carries is specific and nasty: if it disagrees with T-SQL, the tests
/// assert one thing and production does another, and both look green. These cases pin it to SQL
/// Server's default LAX path mode.
/// </para>
///
/// <para>
/// The one deliberate divergence is unparseable input. SQL Server raises; this returns null. It
/// cannot be reached through the mapped column — <c>CK_AnalysisResults_BcraDataJson</c> enforces
/// <c>ISJSON</c> — and on a read path that must not fail, null is the safer of the two.
/// </para>
/// </summary>
public sealed class SqlJsonFunctionsTests
{
    private const string Doc = """
        {"deuda":{"situacionMaxima":3,"estado":0,"denominacion":"Metalúrgica del Sur S.A.",
        "montoTotal":48500.75,"enRevision":false,"tieneProcesoJudicial":true,
        "periodoConsultado":null,"entidadesInformantes":["BANCO NACION"],
        "nested":{"leaf":"value"}}}
        """;

    // ── Scalars come back as text, exactly as JSON_VALUE returns them ──────────

    [Theory]
    [InlineData("$.deuda.estado",           "0")]
    [InlineData("$.deuda.situacionMaxima",  "3")]
    [InlineData("$.deuda.denominacion",     "Metalúrgica del Sur S.A.")]
    [InlineData("$.deuda.enRevision",       "false")]
    [InlineData("$.deuda.tieneProcesoJudicial", "true")]
    [InlineData("$.deuda.nested.leaf",      "value")]
    public void ReturnsScalarsAsText(string path, string expected)
        => Assert.Equal(expected, SqlJsonFunctions.JsonValue(Doc, path));

    /// <summary>
    /// The document's own text, not a re-formatted number. JSON_VALUE returns nvarchar, so
    /// <c>48500.75</c> must not come back as <c>48500.750000</c> or in exponent form — a caller
    /// parsing it would still land on the right value, but a caller comparing strings would not.
    /// </summary>
    [Fact]
    public void ReturnsNumbersAsWrittenRatherThanReformatted()
        => Assert.Equal("48500.75", SqlJsonFunctions.JsonValue(Doc, "$.deuda.montoTotal"));

    /// <summary>Trailing zeros and exponent notation survive, for the same reason.</summary>
    [Theory]
    [InlineData("""{"a":{"b":2.50}}""", "2.50")]
    [InlineData("""{"a":{"b":1e3}}""",  "1e3")]
    public void PreservesTheRawNumericText(string json, string expected)
        => Assert.Equal(expected, SqlJsonFunctions.JsonValue(json, "$.a.b"));

    // ── Everything absent or non-scalar is null, per LAX mode ──────────────────

    /// <summary>
    /// A missing leaf, a missing intermediate node, a JSON null, and a path that resolves to an
    /// object or an array are all NULL in lax mode rather than errors. The intermediate-node case is
    /// the one the detail projection actually depends on: an analysis whose blob has no
    /// <c>deuda</c> must yield null, not throw.
    /// </summary>
    [Theory]
    [InlineData("$.deuda.noSuchField")]           // missing leaf
    [InlineData("$.noSuchNode.estado")]           // missing intermediate
    [InlineData("$.deuda.periodoConsultado")]     // JSON null
    [InlineData("$.deuda.nested")]                // object
    [InlineData("$.deuda.entidadesInformantes")]  // array
    [InlineData("$.deuda.estado.deeper")]         // descending through a scalar
    public void ReturnsNullForAnythingNotAScalarAtThePath(string path)
        => Assert.Null(SqlJsonFunctions.JsonValue(Doc, path));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ReturnsNullForAbsentInput(string? json)
        => Assert.Null(SqlJsonFunctions.JsonValue(json, "$.deuda.estado"));

    /// <summary>
    /// The documented divergence from T-SQL. Unreachable through the mapped column thanks to the
    /// ISJSON check constraint, and a null keeps a read path alive where an exception would 500 it.
    /// </summary>
    [Fact]
    public void ReturnsNullForUnparseableJson()
        => Assert.Null(SqlJsonFunctions.JsonValue("{ this is not json", "$.deuda.estado"));

    /// <summary>
    /// JSON paths are CASE-SENSITIVE in T-SQL, and the stored keys are camelCase. Mirroring that
    /// here is what stops a wrong-cased path passing in tests and returning NULL for every row in
    /// production — the exact failure this whole extraction would be silent about.
    /// </summary>
    [Fact]
    public void IsCaseSensitive()
    {
        Assert.Equal("0", SqlJsonFunctions.JsonValue(Doc, "$.deuda.estado"));
        Assert.Null(SqlJsonFunctions.JsonValue(Doc, "$.Deuda.Estado"));
    }

    // ── Unsupported paths fail loudly rather than diverging ───────────────────

    /// <summary>
    /// SQL Server would happily evaluate these; this implementation does not. Throwing is the point:
    /// a path that means one thing on the server and another on the client is a defect that only
    /// shows up in production, so it is refused at the first call instead.
    /// </summary>
    [Theory]
    [InlineData("deuda.estado")]                  // not rooted
    [InlineData("$")]                             // root itself
    [InlineData("$.deuda.entidadesInformantes[0]")]
    [InlineData("$.deuda.*")]
    [InlineData("$.deuda..estado")]               // empty segment
    public void ThrowsForPathsItCannotEvaluateTheSameWayAsSqlServer(string path)
        => Assert.Throws<ArgumentException>(() => SqlJsonFunctions.JsonValue(Doc, path));

    /// <summary>
    /// The path check runs before the document is looked at, so an unsupported path is caught even
    /// on rows where the column is null — otherwise the mistake would hide until the first row that
    /// happened to have data.
    /// </summary>
    [Fact]
    public void ValidatesThePathEvenWhenTheDocumentIsNull()
        => Assert.Throws<ArgumentException>(() => SqlJsonFunctions.JsonValue(null, "$.deuda[0]"));
}
