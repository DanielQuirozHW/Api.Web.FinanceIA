// Project : Api.Web.FinanceIA
// Created : 2026-10-19

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>[dbo].[LegajoItems].[ReconciliationStatus] — CK_LegajoItems_ReconciliationStatus (DDL 071).</summary>
public static class LegajoReconciliationStatuses
{
    /// <summary>Found in both documents, and they agree.</summary>
    public const string Matched = "Matched";

    /// <summary>Declared to the fisco only.</summary>
    public const string OnlyInBienesPersonales = "OnlyInBienesPersonales";

    /// <summary>Declared to the creditor only (every debt, since Bienes Personales lists none).</summary>
    public const string OnlyInManifestacion = "OnlyInManifestacion";

    /// <summary>Found in both documents, and they disagree (see LegajoItemConflictReasons).</summary>
    public const string Conflict = "Conflict";

    /// <summary>A conflict the analyst reviewed and accepted as it stands.</summary>
    public const string AnalystConfirmed = "AnalystConfirmed";

    /// <summary>Every value the CHECK admits, in DDL order.</summary>
    public static readonly IReadOnlyList<string> Known = [Matched, OnlyInBienesPersonales, OnlyInManifestacion, Conflict, AnalystConfirmed];
}
