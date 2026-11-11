// Project : Api.Web.FinanceIA
// Created : 2026-11-10

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The identity of a stored NOSIS Manager report: the file's SHA-256 and the parser version that read
/// it, both read with <c>JSON_VALUE</c>. A repository projection, not a wire contract.
/// </summary>
public sealed class NosisReportFingerprint
{
    public string? Sha256 { get; set; }

    public string? ParserVersion { get; set; }
}
