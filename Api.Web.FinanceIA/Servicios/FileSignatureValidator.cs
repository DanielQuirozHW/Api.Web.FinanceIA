// Project : Api.Web.FinanceIA
// Created : 2026-08-13

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Kinds of file whose real type <see cref="FileSignatureValidator"/> can confirm from a file's
/// leading bytes. Add a member here (with its signature in the validator's table) to support a
/// new type without changing any call site.
/// </summary>
public enum FileSignatureType
{
    /// <summary>Portable Document Format — files carry the ASCII bytes <c>%PDF-</c> near the start.</summary>
    Pdf,

    /// <summary>
    /// Office Open XML spreadsheet. An .xlsx is a ZIP container, so its signature is the ZIP local
    /// file header <c>50 4B 03 04</c> — bytes it shares with .docx, .pptx, .odt, .jar and any plain
    /// .zip. This signature therefore proves only "this is a ZIP", never "this is a spreadsheet":
    /// a caller MUST follow it by actually opening the file as an OOXML workbook before trusting it.
    /// </summary>
    Xlsx
}

/// <summary>
/// Decides a file's real type from its leading "magic" bytes, never from its name or its
/// client-supplied Content-Type header. The byte check is authoritative: a file whose declared
/// type disagrees with its actual bytes is judged by its bytes alone.
/// The class is dependency-free and side-effect-free so it can be unit-tested directly.
/// </summary>
public static class FileSignatureValidator
{
    /// <summary>
    /// How many leading bytes are scanned for a signature. Real-world PDFs are routinely produced
    /// with bytes before the <c>%PDF-</c> header (UTF-8 BOMs, re-save preambles from scanners and
    /// PDF tooling), and every real reader — Acrobat, poppler, pdf.js — as well as the extraction
    /// path this upload feeds scans the start of the file rather than demanding offset 0. Requiring
    /// the signature at offset 0 would reject balances that upload and analyse fine today, so the
    /// signature is accepted anywhere it fits fully within this window.
    /// Accepted limitation: a container format that happens to embed a PDF early (a "polyglot")
    /// would pass. That is a deliberate trade — the threat this closes is a spoofed Content-Type on
    /// an ordinary non-PDF file, and rejecting valid balances would be the worse failure.
    /// </summary>
    public const int SignatureSearchWindowBytes = 1024;

    // Known signatures keyed by type. Extend this table to support new types; the public API and
    // every call site stay unchanged. Each value is the exact byte sequence a file of that type
    // must contain within the first SignatureSearchWindowBytes bytes.
    private static readonly IReadOnlyDictionary<FileSignatureType, byte[]> Signatures =
        new Dictionary<FileSignatureType, byte[]>
        {
            // "%PDF-" — 0x25 0x50 0x44 0x46 0x2D
            [FileSignatureType.Pdf] = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D },

            // "PK\x03\x04" — the ZIP local file header that opens every OOXML package.
            [FileSignatureType.Xlsx] = new byte[] { 0x50, 0x4B, 0x03, 0x04 }
        };

    // Types whose signature counts ONLY at offset 0, rather than anywhere in the search window.
    // The search-anywhere default exists for PDF, where leading junk is common and harmless (see
    // SignatureSearchWindowBytes). It would be actively wrong for a ZIP: the local file header is
    // defined to sit at offset 0, so searching the whole window would accept any file that merely
    // happens to contain those four bytes early — a PDF with an embedded archive, for instance.
    // Anchoring keeps the cheap reject meaningful. Membership here changes nothing for PDF.
    private static readonly IReadOnlySet<FileSignatureType> AnchoredAtStart =
        new HashSet<FileSignatureType> { FileSignatureType.Xlsx };

    /// <summary>
    /// Length of the signature for <paramref name="type"/> itself. This is NOT the number of bytes
    /// scanned — see <see cref="SignatureSearchWindowBytes"/> for the search window.
    /// </summary>
    public static int GetSignatureLength(FileSignatureType type) => Signature(type).Length;

    /// <summary>
    /// Returns <c>true</c> when the signature of <paramref name="type"/> is present in the first
    /// <see cref="SignatureSearchWindowBytes"/> bytes of <paramref name="leadingBytes"/>. Where it
    /// must be present depends on the type: most are matched anywhere within the window, while a
    /// type listed as anchored must carry its signature at offset 0 (see the remarks on the
    /// anchored-types table). A signature that would extend past the window, or a buffer shorter
    /// than the signature, is invalid and returns <c>false</c> — never an exception. The file name
    /// and Content-Type are ignored entirely.
    /// </summary>
    public static bool Matches(ReadOnlySpan<byte> leadingBytes, FileSignatureType type)
    {
        var signature = Signature(type);

        // Only the first window counts: a signature beyond it, or straddling its edge, does not.
        var window = leadingBytes.Length > SignatureSearchWindowBytes
            ? leadingBytes[..SignatureSearchWindowBytes]
            : leadingBytes;

        return AnchoredAtStart.Contains(type)
            ? window.StartsWith(signature)
            : window.IndexOf(signature) >= 0;
    }

    /// <summary>
    /// Reads at most <see cref="SignatureSearchWindowBytes"/> leading bytes from
    /// <paramref name="stream"/> — never the whole stream, never more than the window — and returns
    /// whether the signature of <paramref name="type"/> is present within them, under the same
    /// anywhere-vs-anchored rule as <see cref="Matches"/>. A stream shorter than
    /// the window is fine (whatever is there is searched); a stream shorter than the signature (an
    /// empty stream included) yields <c>false</c>, not an exception.
    /// </summary>
    public static async Task<bool> MatchesAsync(
        Stream            stream,
        FileSignatureType type,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var buffer = new byte[SignatureSearchWindowBytes];
        var read = await stream
            .ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken)
            .ConfigureAwait(false);

        return Matches(buffer.AsSpan(0, read), type);
    }

    private static byte[] Signature(FileSignatureType type) =>
        Signatures.TryGetValue(type, out var signature)
            ? signature
            : throw new ArgumentOutOfRangeException(
                nameof(type), type, "Unknown file signature type.");
}
