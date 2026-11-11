// Project : Api.Web.FinanceIA
// Created : 2026-10-29

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Values of [dbo].[AnalysisParticipants].[GuaranteeType] (DDL 077) for a Fiador row (M10 Phase 5a-2). The
/// column has no CHECK by design: the vocabulary is validated here, at POST /api/person-analyses, and a test
/// pins this list.
/// </summary>
public static class GuaranteeTypes
{
    /// <summary>A personal guarantee (fianza) by a third person.</summary>
    public const string Fianza = "Fianza";

    /// <summary>
    /// The solicitante's spouse. With SpouseConsent the solicitante's housing becomes executable through this
    /// guarantee; the spouse's own coverage counts only assets that are not also in the solicitante's inventory.
    /// </summary>
    public const string Conyuge = "Conyuge";

    /// <summary>Every accepted value.</summary>
    public static readonly IReadOnlyList<string> Known = [Fianza, Conyuge];

    /// <summary>True when <paramref name="value"/> is one of <see cref="Known"/> (ordinal).</summary>
    public static bool IsKnown(string? value) => value is not null && Known.Contains(value, StringComparer.Ordinal);
}
