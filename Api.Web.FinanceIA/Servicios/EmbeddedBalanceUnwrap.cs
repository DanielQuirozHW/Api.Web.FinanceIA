// Project : Api.Web.FinanceIA
// Created : 2026-11-03

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// The result of <see cref="PdfAttachmentExtractor.Unwrap"/>: the bytes to analyse, plus whether an
/// embedded attachment the outer PDF does carry was left unread. Counts and an exception TYPE only —
/// never a name, a path or content.
/// </summary>
/// <param name="Bytes">The innermost qualifying attachment, or the outer bytes unchanged.</param>
/// <param name="AttachmentUnread">
/// True when <paramref name="Bytes"/> is the outer document although it carries embedded files:
/// one could not be read or was over the decoded-size cap, the size rule was ambiguous, an attachment
/// presented as a PDF was not one, or an attachment named as the statements did not qualify.
/// </param>
/// <param name="AttachmentsSeen">Embedded attachments seen at the outer level.</param>
/// <param name="FailureType">The first read failure's exception type when unread; null otherwise.</param>
internal sealed record EmbeddedBalanceUnwrap(
    byte[]  Bytes,
    bool    AttachmentUnread,
    int     AttachmentsSeen,
    string? FailureType)
{
    /// <summary>
    /// True when the attachment taken — or the one left unread — was found by the orphaned file
    /// specification fallback (<see cref="PdfAttachmentExtractor.OrphanedFileSpecSource"/>): a re-saved
    /// PDF whose live catalog no longer reaches its attachments. Persisted by the analysis pipeline as
    /// <c>integrity.extraction.embeddedAttachmentSource</c> (<see cref="EmbeddedAttachmentJson.Mark"/>).
    /// </summary>
    public bool FromOrphanedFileSpec { get; init; }
}
