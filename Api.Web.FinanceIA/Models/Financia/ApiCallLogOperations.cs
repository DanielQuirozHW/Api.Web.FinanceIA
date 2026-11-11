// Project : Api.Web.FinanceIA
// Created : 2026-09-29

namespace Api.Web.FinanceIA.Models.Financia;

/// <summary>
/// The [dbo].[ApiCallLogs].[Operation] values this API writes — every <c>operation:</c> argument to
/// <c>IApiCallLogService.LogAsync</c>. A row with no operation (ARCA, LUFE, NOSIS) writes NULL.
/// </summary>
/// <remarks>
/// <c>CK_ApiCallLogs_Operation</c> was created out of band on Financia-db, by no numbered script, and
/// also admits values this repo never writes (another application may write them). DDL 070 widens it
/// to a SUPERSET: those legacy values plus <see cref="Known"/>. A test parses 070 and pins
/// <see cref="Known"/> as a SUBSET of that list, and another fails on any string literal passed as an
/// operation, so a new operation cannot ship without widening the CHECK. The API swallows a failed
/// log write, so a value the CHECK rejects would lose its cost rows silently rather than fail loudly.
/// Do NOT add the legacy out-of-band values here: this list is what the code writes.
/// </remarks>
public static class ApiCallLogOperations
{
    /// <summary>Balance extraction (AnalysisController).</summary>
    public const string Extraction = "Extraction";

    /// <summary>Scoring narrative (AnalysisController).</summary>
    public const string Narrative = "Narrative";

    /// <summary>Analyst chat turn (AnalysisController).</summary>
    public const string Chat = "Chat";

    /// <summary>Legajo classification pass (LegajoClassificationService, via LegajoMessagesClient).</summary>
    public const string LegajoClassification = "LegajoClassification";

    /// <summary>Legajo extraction pass (LegajoExtractionService, via LegajoMessagesClient).</summary>
    public const string LegajoExtraction = "LegajoExtraction";

    /// <summary>Every operation the API writes.</summary>
    public static readonly IReadOnlyList<string> Known =
        [Extraction, Narrative, Chat, LegajoClassification, LegajoExtraction];
}
