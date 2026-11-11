// Project : Api.Web.FinanceIA
// Created : 2026-10-20

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// Values of [dbo].[AnalysisParticipants].[Role] (DDL 072, CK_AnalysisParticipants_Role). A test pins this
/// list to the script.
/// </summary>
public static class ParticipantRoles
{
    /// <summary>The applicant whose operation is analysed. The only role written in Phase 4a.</summary>
    public const string Solicitante = "Solicitante";

    /// <summary>
    /// A guarantor of the operation: its fiador columns are DDL 077, written by POST /api/person-analyses since
    /// Phase 5a-2 (one row per fiador, frozen like the solicitante's).
    /// </summary>
    public const string Fiador = "Fiador";

    /// <summary>Every role the CHECK admits.</summary>
    public static readonly IReadOnlyList<string> Known = [Solicitante, Fiador];
}
