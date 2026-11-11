// Project : Api.Web.FinanceIA
// Created : 2026-10-20

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Values of [dbo].[Analyses].[AnalysisKind] (DDL 072, CK_Analyses_AnalysisKind). A test pins this list
/// to the script.
/// </summary>
public static class AnalysisKinds
{
    /// <summary>The PyME analysis of one balance file: extraction, scoring, narrative. The default.</summary>
    public const string Balance = "Balance";

    /// <summary>
    /// The persona humana evaluation on the legajo (M10 Phase 4a): filters, indicators and coverage, no
    /// score and no model call. The only kind whose DocumentId may be NULL.
    /// </summary>
    public const string PersonaHumana = "PersonaHumana";

    /// <summary>Every kind the CHECK admits.</summary>
    public static readonly IReadOnlyList<string> Known = [Balance, PersonaHumana];
}
