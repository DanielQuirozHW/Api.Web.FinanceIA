// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-03

using System.IO.Compression;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Filespec;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// iText-built PDFs for the "attachment present but unread" cases: unencrypted wrappers carrying
/// name-tree attachments, one of which may be made undecodable. Compression is off so the padding
/// reliably clears the extractor's 10 KB threshold.
/// </summary>
internal static class AttachmentPdfBuilder
{
    internal const int PaddingChars = 16 * 1024;

    /// <summary>A standalone PDF, padded past the extractor's size threshold unless <paramref name="padChars"/> is 0.</summary>
    internal static byte[] Pdf(int pageCount = 3, int padChars = PaddingChars)
    {
        using var buffer = new MemoryStream();
        var properties = new WriterProperties().SetCompressionLevel(CompressionConstants.NO_COMPRESSION);

        using (var doc = new PdfDocument(new PdfWriter(buffer, properties)))
        {
            for (var i = 0; i < pageCount; i++)
                doc.AddNewPage();

            if (padChars > 0)
                doc.GetDocumentInfo().SetMoreInfo("Padding", new string('x', padChars));
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// A 1-page wrapper with one name-tree attachment per entry. An entry whose data is null is
    /// written as an embedded stream with a filter PdfPig does not support, so decoding it throws
    /// (PdfPig's Flate filter is lenient and would hand back the raw bytes instead).
    /// </summary>
    internal static byte[] Wrapper(params (string Name, byte[]? Data)[] attachments)
    {
        using var buffer = new MemoryStream();
        var properties = new WriterProperties().SetCompressionLevel(CompressionConstants.NO_COMPRESSION);

        using (var doc = new PdfDocument(new PdfWriter(buffer, properties)))
        {
            doc.AddNewPage();

            foreach (var (name, data) in attachments)
            {
                if (data is not null)
                {
                    doc.AddFileAttachment(name, PdfFileSpec.CreateEmbeddedFileSpec(
                        doc, data, name, name, PdfName.ApplicationPdf, null, null));
                    continue;
                }

                var stream = new PdfStream(new byte[2048], CompressionConstants.NO_COMPRESSION);
                stream.Put(PdfName.Filter, new PdfName("NoSuchDecode"));

                var embedded = new PdfDictionary();
                embedded.Put(PdfName.F, stream.MakeIndirect(doc));

                var spec = new PdfDictionary();
                spec.Put(PdfName.Type, PdfName.Filespec);
                spec.Put(PdfName.F, new PdfString(name));
                spec.Put(PdfName.EF, embedded);

                doc.AddFileAttachment(name, PdfFileSpec.WrapFileSpecObject(spec.MakeIndirect(doc)));
            }
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// One attachment of <see cref="OrphanedCatalogWrapper"/>: <paramref name="Key"/> is its name-tree
    /// key, <paramref name="FileName"/> the /F and /UF of its file specification (the key when null).
    /// <paramref name="Data"/> null writes a stream PdfPig cannot decode; <paramref name="MimeType"/> is
    /// the /EF stream's /Subtype.
    /// </summary>
    internal sealed record OrphanAttachment(
        string  Key,
        byte[]? Data,
        string  MimeType = "application/pdf",
        string? FileName = null)
    {
        /// <summary>When set, the /EF stream is written from these bytes as they are: <see cref="RawStream"/>.</summary>
        public RawStream? Raw { get; init; }
    }

    /// <summary>
    /// An embedded file stream written byte for byte: <paramref name="Encoded"/> under
    /// <paramref name="Filter"/> (none when null), with an optional /Params /Size and /DL.
    /// </summary>
    internal sealed record RawStream(
        byte[]  Encoded,
        string? Filter        = "FlateDecode",
        double? DeclaredSize  = null,
        double? DecodedLength = null);

    /// <summary>A <see cref="RawStream"/> holding <paramref name="data"/> zlib-compressed, as a PDF writer would.</summary>
    internal static RawStream Flate(byte[] data, double? declaredSize = null, double? decodedLength = null) =>
        new(Zlib(data), "FlateDecode", declaredSize, decodedLength);

    /// <summary>zlib-compresses <paramref name="data"/> (header + deflate + Adler-32).</summary>
    internal static byte[] Zlib(byte[] data)
    {
        using var buffer = new MemoryStream();
        using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(data, 0, data.Length);
        return buffer.ToArray();
    }

    /// <summary>
    /// A 1-page wrapper whose name tree carries one attachment per entry, each written as a
    /// <see cref="RawStream"/> in a hand-built file specification.
    /// </summary>
    internal static byte[] RawWrapper(params (string Name, RawStream Stream)[] attachments)
    {
        using var buffer = new MemoryStream();
        var properties = new WriterProperties().SetCompressionLevel(CompressionConstants.NO_COMPRESSION);

        using (var doc = new PdfDocument(new PdfWriter(buffer, properties)))
        {
            doc.AddNewPage();

            foreach (var (name, raw) in attachments)
                doc.AddFileAttachment(name, PdfFileSpec.WrapFileSpecObject(RawFileSpec(doc, name, raw).MakeIndirect(doc)));
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// A 1-page cover carrying ONE attachment as a page /FileAttachment annotation (what the CPCEN
    /// obleas use), its /EF stream written as <paramref name="raw"/>.
    /// </summary>
    internal static byte[] AnnotationWrapper(string name, RawStream raw)
    {
        using var buffer = new MemoryStream();
        var properties = new WriterProperties().SetCompressionLevel(CompressionConstants.NO_COMPRESSION);

        using (var doc = new PdfDocument(new PdfWriter(buffer, properties)))
        {
            var page = doc.AddNewPage();
            var spec = PdfFileSpec.WrapFileSpecObject(RawFileSpec(doc, name, raw).MakeIndirect(doc));
            page.AddAnnotation(new PdfFileAttachmentAnnotation(new Rectangle(10, 10, 20, 20), spec));
        }

        return buffer.ToArray();
    }

    private static PdfStream RawEmbeddedStream(RawStream raw)
    {
        var stream = new PdfStream(raw.Encoded, CompressionConstants.NO_COMPRESSION);
        if (raw.Filter is not null)
            stream.Put(PdfName.Filter, new PdfName(raw.Filter));
        if (raw.DeclaredSize is { } size)
        {
            var parameters = new PdfDictionary();
            parameters.Put(PdfName.Size, new PdfNumber(size));
            stream.Put(new PdfName("Params"), parameters);
        }
        if (raw.DecodedLength is { } decodedLength)
            stream.Put(new PdfName("DL"), new PdfNumber(decodedLength));
        stream.Put(PdfName.Type, PdfName.EmbeddedFile);
        return stream;
    }

    private static PdfDictionary RawFileSpec(PdfDocument doc, string name, RawStream raw)
    {
        var embedded = new PdfDictionary();
        embedded.Put(PdfName.F, RawEmbeddedStream(raw).MakeIndirect(doc));

        var spec = new PdfDictionary();
        spec.Put(PdfName.Type, PdfName.Filespec);
        spec.Put(PdfName.F, new PdfString(name));
        spec.Put(PdfName.EF, embedded);
        return spec;
    }

    /// <summary>Where the orphaned /EmbeddedFiles tree hangs in <see cref="OrphanedCatalogWrapper"/>.</summary>
    internal enum OrphanNamesHolder
    {
        /// <summary>
        /// The PDFium re-save shape: the Names dictionary is its OWN indirect object holding
        /// /EmbeddedFiles (a direct tree) and /JavaScript; the old catalog points at it by reference.
        /// </summary>
        IndirectNamesDictionary,

        /// <summary>The Names dictionary is written DIRECTLY inside the old catalog object.</summary>
        DirectInOldCatalog,
    }

    /// <summary>
    /// A PDFium-style re-save: a 1-page cover whose LIVE catalog has no /Names, while an old
    /// /Type /Catalog object — and the Names dictionary, the /EmbeddedFiles tree, the file
    /// specifications and their /EF streams it reaches — is still written to the file but referenced
    /// by nothing live. One classic xref table; the /EF streams are Flate-compressed.
    /// File specifications are built by hand so their placement is exact: indirect objects
    /// referenced from the tree's /Names array, or, with <paramref name="inlineSpecs"/>, DIRECT
    /// dictionaries inside that array (only their /EF stream is an object of its own).
    /// <paramref name="liveAttachments"/> adds ordinary name-tree attachments to the live catalog.
    /// </summary>
    internal static byte[] OrphanedCatalogWrapper(
        IReadOnlyList<OrphanAttachment>             orphans,
        IReadOnlyList<(string Name, byte[] Data)>?  liveAttachments = null,
        bool                                        inlineSpecs     = false,
        OrphanNamesHolder                           holder          = OrphanNamesHolder.IndirectNamesDictionary)
    {
        using var buffer = new MemoryStream();

        using (var doc = new PdfDocument(new PdfWriter(buffer)))
        {
            doc.SetFlushUnusedObjects(true); // the orphaned objects are unreferenced by design
            var page = doc.AddNewPage();

            foreach (var (name, data) in liveAttachments ?? [])
            {
                doc.AddFileAttachment(name, PdfFileSpec.CreateEmbeddedFileSpec(
                    doc, data, name, name, PdfName.ApplicationPdf, null, null));
            }

            var pairs = new PdfArray();
            foreach (var orphan in orphans)
            {
                var spec = OrphanFileSpec(doc, orphan);
                pairs.Add(new PdfString(orphan.Key));
                pairs.Add(inlineSpecs ? spec : spec.MakeIndirect(doc));
            }

            var tree = new PdfDictionary();
            tree.Put(PdfName.Names, pairs);

            var javaScript = new PdfDictionary();
            javaScript.Put(PdfName.Names, new PdfArray());

            var names = new PdfDictionary();
            names.Put(PdfName.EmbeddedFiles, tree);
            names.Put(PdfName.JavaScript, javaScript);

            var oldCatalog = new PdfDictionary();
            oldCatalog.Put(PdfName.Type, PdfName.Catalog);
            // The superseded page tree, as the re-save leaves it (the live one is only built on close).
            var oldPages = new PdfDictionary();
            oldPages.Put(PdfName.Type, PdfName.Pages);
            oldPages.Put(PdfName.Count, new PdfNumber(1));
            oldPages.Put(PdfName.Kids, new PdfArray(page.GetPdfObject()));

            oldCatalog.Put(PdfName.Pages, oldPages.MakeIndirect(doc));
            oldCatalog.Put(PdfName.Names, holder == OrphanNamesHolder.IndirectNamesDictionary
                ? names.MakeIndirect(doc)
                : names);
            oldCatalog.MakeIndirect(doc);
        }

        return buffer.ToArray();
    }

    private static PdfDictionary OrphanFileSpec(PdfDocument doc, OrphanAttachment orphan)
    {
        PdfStream stream;
        if (orphan.Raw is not null)
        {
            stream = RawEmbeddedStream(orphan.Raw);
        }
        else if (orphan.Data is not null)
        {
            stream = new PdfStream(orphan.Data); // compressed with the writer's default (Flate)
        }
        else
        {
            stream = new PdfStream(new byte[2048], CompressionConstants.NO_COMPRESSION);
            stream.Put(PdfName.Filter, new PdfName("NoSuchDecode"));
        }

        stream.Put(PdfName.Type, PdfName.EmbeddedFile);
        stream.Put(PdfName.Subtype, new PdfName(orphan.MimeType));

        var embedded = new PdfDictionary();
        embedded.Put(PdfName.F, stream.MakeIndirect(doc));
        embedded.Put(PdfName.UF, stream.GetIndirectReference());

        var fileName = orphan.FileName ?? orphan.Key;
        var spec     = new PdfDictionary();
        spec.Put(PdfName.Type, PdfName.Filespec);
        spec.Put(PdfName.F, new PdfString(fileName));
        spec.Put(PdfName.UF, new PdfString(fileName));
        spec.Put(PdfName.EF, embedded);
        return spec;
    }

    /// <summary>Content of the given size that is not a PDF (no %PDF magic).</summary>
    internal static byte[] NotAPdf(int length = 12 * 1024)
    {
        var data = new byte[length];
        for (var i = 0; i < data.Length; i++)
            data[i] = (byte)('a' + (i % 26));
        return data;
    }
}
