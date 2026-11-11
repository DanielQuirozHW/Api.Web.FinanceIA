// Project : Api.Web.FinanceIA
// Created : 2026-11-04

namespace Api.Web.FinanceIA.Clases;

/// <summary>
/// The one size ceiling for a PDF entering FinancIA: the multipart upload (company documents and the
/// persona humana legajo, which share <c>POST /api/documents/upload</c>) and the LUFE document import
/// (manual endpoint and the automatic balance import, which share one core). Raise it here, nowhere else.
/// <para>
/// Not to be confused with <c>ClaudeService</c>'s 5 / 10 MB tiers, which only decide HOW a stored file is
/// sent to the model (document block below 5 MB, rendered pages above) — they are not a refusal.
/// </para>
/// </summary>
public static class PdfSizeLimits
{
    /// <summary>The ceiling in (binary) megabytes, as shown in the refusal messages.</summary>
    public const int MaxPdfMegabytes = 25;

    /// <summary>Largest PDF accepted, in bytes (25 MiB = 26,214,400). A file of exactly this size is accepted.</summary>
    public const long MaxPdfBytes = MaxPdfMegabytes * 1024L * 1024;

    /// <summary>
    /// Room for the multipart envelope around the file: boundaries, part headers and the form fields.
    /// <c>[RequestSizeLimit]</c> bounds the WHOLE body, so a body limit equal to the file limit refuses a
    /// file just under it in the framework, before the action's own ProblemDetails can answer.
    /// </summary>
    public const long MultipartEnvelopeBytes = 1024L * 1024;

    /// <summary>
    /// The request body limit of the upload endpoint. Must stay at or under 30,000,000 bytes: that is both
    /// Kestrel's default and IIS's default <c>maxAllowedContentLength</c> on the Windows App Service, and no
    /// web.config in this repo raises it — a test pins the bound.
    /// </summary>
    public const long MaxUploadRequestBytes = MaxPdfBytes + MultipartEnvelopeBytes;

    /// <summary>"25 MB" — the figure the refusal messages show.</summary>
    public static readonly string MaxPdfLabel = $"{MaxPdfMegabytes} MB";
}
