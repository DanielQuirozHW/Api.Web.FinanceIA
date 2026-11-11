// Project : Api.Web.FinanceIA
// Created : 2026-11-02

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// <c>balanceCorrections</c> on the analysis detail: that figures of the extracted balance were corrected
/// by hand after the reading, read from <c>BalanceJson.manual_corrections</c>. Null when none was. Detail
/// only — never on the list.
/// </summary>
public sealed record BalanceCorrectionsResponse
{
    /// <summary>The es-AR note the Portal shows next to the anomalies, which are the ORIGINAL reading's.</summary>
    public const string CorrectedAfterReadingNote =
        "Las cifras del balance se corrigieron manualmente después de la lectura automática; " +
        "las anomalías listadas corresponden a la lectura original.";

    /// <summary>Distinct corrected figures.</summary>
    public required int       Count      { get; init; }

    /// <summary>UTC instant of the latest correction.</summary>
    public DateTime?          LastAt     { get; init; }

    /// <summary>Display name of the analyst who made the latest correction ("Usuario" when unresolvable).</summary>
    public string?            LastByName { get; init; }

    /// <summary><see cref="CorrectedAfterReadingNote"/>.</summary>
    public string             Note       { get; init; } = CorrectedAfterReadingNote;
}
