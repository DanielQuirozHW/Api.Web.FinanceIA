// Project : Api.Web.FinanceIA
// Created : 2026-11-03

using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Records, in an already-serialized BalanceJson, what <see cref="PdfAttachmentExtractor.Unwrap"/> found
/// about an embedded attachment, beside the rest of the extraction provenance under
/// <c>integrity.extraction</c>:
/// <list type="bullet">
/// <item><c>embeddedAttachment</c> = "unread": the uploaded PDF carries the balance as an attachment that
/// could not be opened, so the extraction read only the wrapper;</item>
/// <item><c>embeddedAttachmentSource</c> = "orphaned_filespec": the attachment taken — or the one left
/// unread — was found through the orphaned file specification fallback (a re-saved PDF whose live
/// catalog no longer reaches it).</item>
/// </list>
/// Read back as <c>extractionIntegrity.embeddedAttachment</c> / <c>embeddedAttachmentSource</c>. Neither
/// key is written otherwise, so every other BalanceJson is byte-identical.
///
/// Fault-isolated like <see cref="CashFlowReconciliationJson"/>: any problem (null/blank/unparseable
/// input, no <c>integrity.extraction</c> object) returns the input UNCHANGED and never throws.
/// </summary>
public static class EmbeddedAttachmentJson
{
    /// <summary>
    /// The es-AR anomaly appended to a new analysis whose embedded balance could not be opened. No
    /// "%" — the percentage reconciler rewrites "X% del …" clauses in the anomalies list.
    /// </summary>
    public const string UnreadAnomaly =
        "El PDF trae el balance como archivo adjunto y no se pudo abrir. Subí el balance directamente.";

    /// <summary>
    /// Returns <paramref name="balanceJson"/> with <c>integrity.extraction.embeddedAttachment</c> set
    /// to <see cref="ExtractionPassInfo.UnreadAttachment"/>. When <c>integrity</c> or
    /// <c>integrity.extraction</c> is not an object (absent, or <c>integrity: null</c> because the
    /// integrity evaluation itself failed), or the input is null/blank/unparseable, the input is
    /// returned UNCHANGED. Never throws.
    /// </summary>
    public static string? MarkUnread(string? balanceJson) =>
        Mark(balanceJson, unread: true, fromOrphanedFileSpec: false);

    /// <summary>
    /// Returns <paramref name="balanceJson"/> with <c>integrity.extraction.embeddedAttachment</c> set to
    /// <see cref="ExtractionPassInfo.UnreadAttachment"/> when <paramref name="unread"/>, and
    /// <c>integrity.extraction.embeddedAttachmentSource</c> set to
    /// <see cref="ExtractionPassInfo.OrphanedFileSpecSource"/> when <paramref name="fromOrphanedFileSpec"/>.
    /// With neither, the SAME string is returned untouched. Fault-isolated exactly like
    /// <see cref="MarkUnread"/>: never throws.
    /// </summary>
    public static string? Mark(string? balanceJson, bool unread, bool fromOrphanedFileSpec)
    {
        if (string.IsNullOrWhiteSpace(balanceJson) || !(unread || fromOrphanedFileSpec))
            return balanceJson;

        try
        {
            if (JsonNode.Parse(balanceJson) is not JsonObject root
                || root["integrity"] is not JsonObject integrity
                || integrity["extraction"] is not JsonObject extraction)
            {
                return balanceJson;
            }

            if (unread)
                extraction["embeddedAttachment"] = ExtractionPassInfo.UnreadAttachment;

            if (fromOrphanedFileSpec)
                extraction["embeddedAttachmentSource"] = ExtractionPassInfo.OrphanedFileSpecSource;

            return root.ToJsonString();
        }
        catch (Exception)
        {
            // Fault-isolated: a malformed blob must never prevent persistence of the analysis.
            return balanceJson;
        }
    }
}
