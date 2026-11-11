// Project : Api.Web.FinanceIA
// Created : 2026-06-25

using System.Globalization;
using System.IO.Compression;
using System.Text;
using Api.Web.FinanceIA.Models.Financia;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Annotations;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Tokenization.Scanner;
using UglyToad.PdfPig.Tokens;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Unwraps CPCEN "oblea de legalización" PDFs that embed the real balance sheet
/// as an internal PDF attachment. Both embedding styles are handled:
/// the document-level EmbeddedFiles name tree, and /FileAttachment annotations on
/// the pages.
/// The attachments nest — a real oblea is a cover sheet carrying the signed oblea as a
/// page annotation, and the oblea in turn carries the balance and the professional's
/// report in its name tree — so extraction repeats until a pass finds nothing further.
/// Among several attachments at one level, exactly one named as the financial statements
/// (EECC, estados contables/financieros, balance) is taken outright; otherwise the largest
/// wins unless the top two sizes are within 20 %.
/// When a qualifying attachment is found its bytes are returned for analysis;
/// otherwise the original bytes pass through unchanged.
/// All errors are fault-isolated: a failing pass ends the descent and keeps the bytes
/// from the previous level, a failure reading one source never prevents the other
/// from being scanned, and a failure reading one name-tree attachment never prevents
/// the others from being read.
/// <para>
/// The name tree is walked from the catalog as the token scanner returns it — decrypted —
/// and NOT through <c>Advanced.TryGetEmbeddedFiles</c>. PdfPig 0.1.15 reads that from a copy
/// of the catalog taken before decryption, so in an ENCRYPTED document whose name tree sits
/// directly in the catalog (the CPCE Córdoba legalization wrapper) the keys come back as
/// ciphertext, "Balance" stops being recognisable, and the size rule alone keeps the cover.
/// </para>
/// <para>
/// A third source, the ORPHANED file specifications, is read only when a level's live document
/// shows no embedded file at all (no name-tree entry, no /FileAttachment annotation, no read
/// failure). A PDFium re-save (LUFE's CPCE Santa Fe "Presentación Única de Balances") writes a new
/// catalog with only /Type and /Pages and leaves the original catalog — its /Names → /EmbeddedFiles
/// with the statements and the auditor's report — in the file as an object nothing references.
/// The fallback walks the cross-reference table through the same decrypting token scanner (capped
/// at <see cref="MaxOrphanScanObjects"/> objects and <see cref="MaxOrphanFileSpecs"/> decodes) and
/// feeds what it finds through the same selection. Because nothing in the live document vouches
/// for these objects, an orphan qualifies only when its decoded bytes start with %PDF: its name is
/// never enough. A file whose live structure already carries attachments never runs the walk.
/// </para>
/// <para>
/// Every decode is bounded by <see cref="MaxDecodedAttachmentBytes"/>: a stream that declares a larger
/// size (/Params /Size or /DL) is skipped without decoding, a lone FlateDecode stream is inflated into a
/// buffer that stops at the cap, and any other filter chain is decoded by PdfPig and checked afterwards.
/// An attachment over the cap is treated exactly like one that failed to decode at that site. A LIVE
/// attachment is a candidate only when its bytes carry the %PDF header (within the first
/// <see cref="PdfHeaderSearchBytes"/> bytes, as the PDF specification allows); a ".pdf" name alone is not
/// enough, and a ".pdf" attachment over the size threshold that is not a PDF counts as unreadable.
/// </para>
/// </summary>
internal static class PdfAttachmentExtractor
{
    private const int MinAttachmentBytes = 10 * 1024; // 10 KB
    private const double AmbiguousSizeRatioThreshold = 0.20; // 20 % relative difference
    private const int MaxNestingDepth = 4; // guards against attachment cycles
    private const int MaxNameTreeDepth = 16; // guards against malformed /Kids chains
    private const int MaxReferenceHops = 8;
    internal const int MaxOrphanScanObjects = 20_000; // cross-reference entries examined by the fallback
    private const int MaxOrphanFileSpecs = 16;        // orphaned embedded streams decoded per level
    private const int PdfHeaderSearchBytes = 1024;    // the %PDF header may sit anywhere in the first 1024 bytes
    private const int InflateChunkBytes = 81_920;

    /// <summary>
    /// The largest decoded attachment read, at every decode site (64 MiB). Larger ones — declared or
    /// actually produced — are treated as an attachment that failed to decode at that site.
    /// </summary>
    internal const long MaxDecodedAttachmentBytes = 64L * 1024 * 1024;

    /// <summary>The failure type recorded for an attachment over <see cref="MaxDecodedAttachmentBytes"/>.</summary>
    internal const string AttachmentTooLargeFailure = "AttachmentTooLarge";

    // Decode sites, as logged when an attachment is skipped for its size.
    private const string NameTreeSite   = "name_tree";
    private const string AnnotationSite = "annotation";
    private const string OrphanSite     = "orphan";

    /// <summary>The attachment source recorded when the fallback found file specifications.</summary>
    internal const string OrphanedFileSpecSource = ExtractionPassInfo.OrphanedFileSpecSource;

    // File specification entries. Note /F and /UF mean a file *name* on the file
    // specification dictionary but an embedded *stream* under its /EF entry.
    private static readonly NameToken FileSpecificationEntry = NameToken.Create("FS");
    private static readonly NameToken EmbeddedFilesEntry     = NameToken.Create("EF");
    private static readonly NameToken FileEntry              = NameToken.Create("F");
    private static readonly NameToken UnicodeFileEntry       = NameToken.Create("UF");

    // Name tree entries: /Root → /Names → /EmbeddedFiles, then /Names pairs and /Kids.
    private static readonly NameToken NamesEntry             = NameToken.Create("Names");
    private static readonly NameToken EmbeddedFilesTreeEntry = NameToken.Create("EmbeddedFiles");
    private static readonly NameToken KidsEntry              = NameToken.Create("Kids");
    private static readonly NameToken SubtypeEntry           = NameToken.Create("Subtype");

    // Stream dictionary entries read by the size guard.
    private static readonly NameToken FilterEntry            = NameToken.Create("Filter");
    private static readonly NameToken FlateDecodeName        = NameToken.Create("FlateDecode");
    private static readonly NameToken FlateDecodeAbbreviation = NameToken.Create("Fl");
    private static readonly NameToken DecodeParmsEntry       = NameToken.Create("DecodeParms");
    private static readonly NameToken DecodeParmsAbbreviation = NameToken.Create("DP");
    private static readonly NameToken PredictorEntry         = NameToken.Create("Predictor");
    private static readonly NameToken ParamsEntry            = NameToken.Create("Params");
    private static readonly NameToken SizeEntry              = NameToken.Create("Size");
    private static readonly NameToken DecodedLengthEntry     = NameToken.Create("DL");
    private const string PdfMediaType = "application/pdf";

    // Attachment-name terms, already normalized (lower case, no accents). See IsPreferredName.
    private static readonly string[] PreferredNameTerms =
        ["eecc", "estados contables", "estados financieros", "balance", "trabajo profesional"];
    private static readonly string[] DeprioritizedNameTerms =
        ["informe", "auditor", "oblea", "certificacion"];

    /// <summary>
    /// Inspects <paramref name="outerBytes"/> for a qualifying embedded PDF attachment,
    /// descending through nested attachments until a pass finds nothing further.
    /// Returns the innermost attachment bytes if found unambiguously, otherwise returns
    /// <paramref name="outerBytes"/> unchanged. See <see cref="Unwrap"/> for the diagnostics.
    /// </summary>
    internal static byte[] UnwrapEmbeddedBalance(
        byte[]  outerBytes,
        ILogger logger,
        Guid    documentId,
        string  tenantId) =>
        Unwrap(outerBytes, logger, documentId, tenantId).Bytes;

    /// <summary>
    /// <see cref="UnwrapEmbeddedBalance"/> with its diagnostics. The bytes are the same.
    /// <see cref="EmbeddedBalanceUnwrap.AttachmentUnread"/> is true only when the outer document
    /// itself was returned although it does carry embedded files: one could not be read, the
    /// size rule was ambiguous, or an attachment named as the statements did not qualify. Logged
    /// once as a Warning with the exception TYPE and the attachment count, never names or paths.
    /// </summary>
    internal static EmbeddedBalanceUnwrap Unwrap(
        byte[]  outerBytes,
        ILogger logger,
        Guid    documentId,
        string  tenantId) =>
        Unwrap(outerBytes, logger, documentId, tenantId, MaxOrphanScanObjects);

    /// <summary>
    /// <see cref="Unwrap(byte[], ILogger, Guid, string)"/> with the orphaned-file-specification walk
    /// capped at <paramref name="maxOrphanScanObjects"/> cross-reference entries (tests lower it).
    /// </summary>
    internal static EmbeddedBalanceUnwrap Unwrap(
        byte[]  outerBytes,
        ILogger logger,
        Guid    documentId,
        string  tenantId,
        int     maxOrphanScanObjects)
    {
        var current = outerBytes;
        LevelOutcome? outerLevel = null;
        var tookOrphan = false;

        for (var depth = 0; depth < MaxNestingDepth; depth++)
        {
            LevelOutcome level;

            try
            {
                level = ExtractOnce(current, maxOrphanScanObjects, logger, documentId, tenantId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    "PDF attachment extraction failed for document {DocumentId} tenant {TenantId} " +
                    "({ExceptionType}), falling back to the last successfully extracted bytes.",
                    documentId, tenantId, ex.GetType().Name);
                return Finish(outerBytes, current, outerLevel, tookOrphan, logger, documentId, tenantId);
            }

            if (depth == 0)
                outerLevel = level;

            // A pass that finds nothing returns its own input — the descent is done.
            if (ReferenceEquals(level.Selected, current))
                return Finish(outerBytes, current, outerLevel, tookOrphan, logger, documentId, tenantId);

            tookOrphan |= level.HasOrphanedFileSpec;
            current = level.Selected;
        }

        logger.LogWarning(
            "Embedded PDF attachments nested deeper than {MaxDepth} levels for document " +
            "{DocumentId} tenant {TenantId}. Stopping the descent and analysing the " +
            "attachment reached so far.",
            MaxNestingDepth, documentId, tenantId);

        return Finish(outerBytes, current, outerLevel, tookOrphan, logger, documentId, tenantId);
    }

    /// <summary>
    /// Builds the result. Flags it unread only when the OUTER bytes are returned and the outer
    /// level carried embedded files that were lost (failure, ambiguity, a statements-named
    /// attachment that did not qualify, or an attachment presented as a PDF whose bytes are not one).
    /// An outer document that could not be opened at all (<paramref name="outerLevel"/> null)
    /// proves nothing about attachments and is not flagged.
    /// </summary>
    private static EmbeddedBalanceUnwrap Finish(
        byte[]        outerBytes,
        byte[]        result,
        LevelOutcome? outerLevel,
        bool          tookOrphan,
        ILogger       logger,
        Guid          documentId,
        string        tenantId)
    {
        var unread = ReferenceEquals(result, outerBytes)
                  && outerLevel is { HasEmbeddedFiles: true } level
                  && (level.Failed > 0 || level.Ambiguous || level.PreferredUnqualified || level.NotAPdfRejected > 0);

        if (unread)
        {
            logger.LogWarning(
                "Embedded PDF attachment present but unread for document {DocumentId} tenant {TenantId}: " +
                "{ExceptionType}, {AttachmentsSeen} attachments seen. Analysing the outer document.",
                documentId, tenantId, outerLevel!.FailureType ?? "none", outerLevel.Seen);
        }

        return new EmbeddedBalanceUnwrap(
            result,
            unread,
            outerLevel?.Seen ?? 0,
            unread ? outerLevel!.FailureType : null)
        {
            FromOrphanedFileSpec = tookOrphan || (unread && outerLevel!.HasOrphanedFileSpec),
        };
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One level of extraction. <see cref="LevelOutcome.Selected"/> is <paramref name="outerBytes"/>
    /// unchanged (same reference) when no qualifying attachment is selected, which is what ends
    /// the descent.
    /// </summary>
    private static LevelOutcome ExtractOnce(
        byte[]  outerBytes,
        int     maxOrphanScanObjects,
        ILogger logger,
        Guid    documentId,
        string  tenantId)
    {
        using var pdfDoc = PdfDocument.Open(outerBytes);

        // ── Collect qualifying attachments from both sources ──────────────────

        var candidates = new List<(string Name, byte[] Data)>();
        var scan       = new ScanState();

        CollectFromNameTree(pdfDoc, candidates, scan, logger, documentId, tenantId);
        CollectFromAnnotations(pdfDoc, candidates, scan, logger, documentId, tenantId);

        // Only when the live document shows no embedded file at all: a re-save may have dropped the
        // catalog's /Names while leaving the original attachments in the file (see the class remarks).
        if (scan.Seen == 0 && scan.Failed == 0)
            CollectFromOrphanedFileSpecs(pdfDoc, candidates, scan, maxOrphanScanObjects, logger, documentId, tenantId);

        LevelOutcome Keep(bool ambiguous = false) => scan.Outcome(outerBytes, ambiguous);

        LevelOutcome Take(byte[] data) => scan.Outcome(data, ambiguous: false);

        if (scan.Seen == 0)
            return Keep(); // normal PDF — nothing embedded anywhere (or a name tree that could not be walked)

        Dedupe(candidates);

        // ── Select ────────────────────────────────────────────────────────────

        if (candidates.Count == 0)
        {
            logger.LogDebug(
                "Embedded attachments present but no qualifying PDF attachment found " +
                "for document {DocumentId} tenant {TenantId}",
                documentId, tenantId);
            return Keep();
        }

        // ≥ 2 candidates — a single attachment NAMED as the financial statements wins outright,
        // whatever the sizes. Only when names do not settle it does the size rule below apply.
        var preferred = candidates.Where(c => IsPreferredName(c.Name)).ToList();

        // When an attachment at this level could not be read — or one that presents itself as a PDF
        // turned out not to be one — the lost one may well be the statements, so only a
        // survivor NAMED as the statements may be taken. Otherwise the lone auditor's report
        // ("Informe") would be analysed in place of the balance.
        if ((scan.Failed > 0 || scan.NotAPdfRejected > 0) && preferred.Count != 1)
        {
            logger.LogWarning(
                "{FailedCount} embedded PDF attachment(s) could not be read for document {DocumentId} " +
                "tenant {TenantId} and no remaining one is named as the statements; keeping the outer document.",
                scan.Failed + scan.NotAPdfRejected, documentId, tenantId);
            return Keep();
        }

        if (candidates.Count == 1)
        {
            var single = candidates[0];
            logger.LogInformation(
                "Embedded PDF attachment selected for analysis: " +
                "document {DocumentId} tenant {TenantId}, " +
                "outer size {OuterBytes} bytes ({OuterPages} pages), " +
                "attachment size {AttachmentBytes} bytes",
                documentId, tenantId,
                outerBytes.Length, pdfDoc.NumberOfPages,
                single.Data.Length);
            return Take(single.Data);
        }

        if (preferred.Count == 1)
        {
            logger.LogInformation(
                "Embedded PDF attachment selected for analysis by name: " +
                "document {DocumentId} tenant {TenantId}, " +
                "{CandidateCount} candidates, attachment size {AttachmentBytes} bytes",
                documentId, tenantId, candidates.Count, preferred[0].Data.Length);
            return Take(preferred[0].Data);
        }

        // Pick the largest unless sizes are ambiguously close
        candidates.Sort((a, b) => b.Data.Length.CompareTo(a.Data.Length));
        var largest       = candidates[0];
        var secondLargest = candidates[1];

        double relDiff = (double)(largest.Data.Length - secondLargest.Data.Length)
                       / largest.Data.Length;

        if (relDiff < AmbiguousSizeRatioThreshold)
        {
            logger.LogWarning(
                "Multiple embedded PDF attachments found for document {DocumentId} " +
                "tenant {TenantId} and sizes are ambiguously close " +
                "({LargestBytes} vs {SecondBytes} bytes, {RelDiff:P0} diff). " +
                "Falling back to original bytes.",
                documentId, tenantId,
                largest.Data.Length, secondLargest.Data.Length,
                relDiff);
            return Keep(ambiguous: true);
        }

        logger.LogInformation(
            "Embedded PDF attachment selected for analysis: " +
            "document {DocumentId} tenant {TenantId}, " +
            "outer size {OuterBytes} bytes ({OuterPages} pages), " +
            "attachment size {AttachmentBytes} bytes",
            documentId, tenantId,
            outerBytes.Length, pdfDoc.NumberOfPages,
            largest.Data.Length);

        return Take(largest.Data);
    }

    // ── Sources ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the document-level EmbeddedFiles name tree (/Root → /Names → /EmbeddedFiles) from the
    /// DECRYPTED catalog (see the class remarks). Every entry with an embedded stream counts as
    /// seen, whether or not it qualified or could be read; a failure on one entry is recorded and
    /// the remaining entries are still read. One over <see cref="MaxDecodedAttachmentBytes"/> is a failure.
    /// </summary>
    private static void CollectFromNameTree(
        PdfDocument                      pdfDoc,
        List<(string Name, byte[] Data)> candidates,
        ScanState                        scan,
        ILogger                          logger,
        Guid                             documentId,
        string                           tenantId)
    {
        try
        {
            var scanner = pdfDoc.Structure.TokenScanner;
            var catalog = Resolve<DictionaryToken>(pdfDoc.Structure.Trailer.Root, scanner)
                       ?? pdfDoc.Structure.Catalog.CatalogDictionary;

            if (!catalog.TryGet<DictionaryToken>(NamesEntry, scanner, out var names) || names is null)
                return;

            if (!names.TryGet<DictionaryToken>(EmbeddedFilesTreeEntry, scanner, out var tree) || tree is null)
                return;

            scan.HasNameTree = true;

            var entries = new List<(string Key, IToken Value)>();
            FlattenNameTree(tree, scanner, entries, depth: 0, visited: []);

            foreach (var (key, value) in entries)
            {
                var fileSpec = Resolve<DictionaryToken>(value, scanner);
                if (fileSpec is null || !TryReadFileSpecStream(fileSpec, scanner, out var stream))
                    continue; // not an embedded file (e.g. a reference to an external one)

                scan.Seen++;

                byte[] data;
                try
                {
                    if (!TryDecodeBounded(stream, scanner, NameTreeSite, logger, documentId, tenantId, out data))
                    {
                        scan.RecordFailure(AttachmentTooLargeFailure);
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    scan.RecordFailure(ex);
                    logger.LogWarning(
                        "An EmbeddedFiles attachment could not be decoded for document {DocumentId} " +
                        "({ExceptionType}), continuing with the remaining attachments.",
                        documentId, ex.GetType().Name);
                    continue;
                }

                var name = string.IsNullOrEmpty(key) ? ReadFileName(fileSpec, scanner) : key;
                scan.Consider(name, data, candidates);
            }
        }
        catch (Exception ex)
        {
            scan.RecordFailure(ex);
            logger.LogWarning(
                "EmbeddedFiles name tree could not be read for document {DocumentId} ({ExceptionType}), " +
                "continuing with the remaining sources.",
                documentId, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Reads /FileAttachment annotations from every page (/Annots → /FS → /EF → /F).
    /// Every attachment found counts as seen, whether or not it qualified. One that cannot be decoded,
    /// or is over <see cref="MaxDecodedAttachmentBytes"/>, is a failure — as on the name tree — and the
    /// page's remaining annotations are still read. Recording it also keeps the orphan walk from running
    /// and claiming this LIVE file specification as an orphan.
    /// </summary>
    private static void CollectFromAnnotations(
        PdfDocument                      pdfDoc,
        List<(string Name, byte[] Data)> candidates,
        ScanState                        scan,
        ILogger                          logger,
        Guid                             documentId,
        string                           tenantId)
    {
        try
        {
            var scanner = pdfDoc.Structure.TokenScanner;

            for (var pageNumber = 1; pageNumber <= pdfDoc.NumberOfPages; pageNumber++)
            {
                // Annotations are lazily evaluated, so a malformed one throws here
                // rather than at GetAnnotations(). Isolate per page so the rest of
                // the document is still scanned.
                try
                {
                    foreach (var annotation in pdfDoc.GetPage(pageNumber).GetAnnotations())
                    {
                        if (annotation.Type != AnnotationType.FileAttachment)
                            continue;

                        if (!TryReadAnnotationAttachment(annotation, scanner, out var name, out var stream))
                            continue;

                        scan.Seen++;
                        scan.HasAnnotationAttachment = true;

                        byte[] data;
                        try
                        {
                            if (!TryDecodeBounded(stream, scanner, AnnotationSite, logger, documentId, tenantId, out data))
                            {
                                scan.RecordFailure(AttachmentTooLargeFailure);
                                continue;
                            }
                        }
                        catch (Exception ex)
                        {
                            scan.RecordFailure(ex);
                            logger.LogWarning(
                                "A /FileAttachment annotation on page {PageNumber} could not be decoded for document " +
                                "{DocumentId} ({ExceptionType}), continuing with the remaining attachments.",
                                pageNumber, documentId, ex.GetType().Name);
                            continue;
                        }

                        scan.Consider(name, data, candidates);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        "Annotations on page {PageNumber} could not be read for document " +
                        "{DocumentId} ({ExceptionType}), continuing with the remaining pages.",
                        pageNumber, documentId, ex.GetType().Name);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Page annotations could not be read for document {DocumentId} ({ExceptionType}), " +
                "continuing with the remaining sources.",
                documentId, ex.GetType().Name);
        }
    }

    /// <summary>
    /// The fallback source: embedded file specifications that the live document no longer reaches,
    /// found by walking the cross-reference table through the decrypting token scanner, in object
    /// number order, at most <paramref name="maxObjects"/> entries. Collects every dictionary whose
    /// /EF resolves to a stream, plus any file specification written inline in an orphaned
    /// /EmbeddedFiles name tree, whose keys also name the indirect ones (as on the live path).
    /// An orphan counts as seen only when it presents itself as a PDF (name, /Subtype, a statements
    /// name) or its bytes are one, so an unrelated non-PDF file specification is ignored; it is a
    /// candidate only when its bytes start with %PDF (<see cref="QualifiesAsOrphan"/>).
    /// </summary>
    private static void CollectFromOrphanedFileSpecs(
        PdfDocument                      pdfDoc,
        List<(string Name, byte[] Data)> candidates,
        ScanState                        scan,
        int                              maxObjects,
        ILogger                          logger,
        Guid                             documentId,
        string                           tenantId)
    {
        try
        {
            var scanner    = pdfDoc.Structure.TokenScanner;
            var references = pdfDoc.Structure.CrossReferenceTable.ObjectOffsets.Keys
                .OrderBy(r => r.ObjectNumber)
                .ThenBy(r => r.Generation)
                .ToList();

            if (references.Count > maxObjects)
            {
                logger.LogWarning(
                    "Orphaned file specification scan capped at {MaxObjects} of {ObjectCount} objects " +
                    "for document {DocumentId} tenant {TenantId}.",
                    maxObjects, references.Count, documentId, tenantId);
            }

            var indirectSpecs = new List<(IndirectReference Reference, DictionaryToken Spec)>();
            var seenSpecs     = new HashSet<IndirectReference>();
            var treeKeys      = new Dictionary<IndirectReference, string>();
            var inlineSpecs   = new List<(string Key, DictionaryToken Spec)>();
            var seenTrees     = new HashSet<IndirectReference>();

            foreach (var reference in references.Take(maxObjects))
            {
                DictionaryToken? dictionary;
                try
                {
                    dictionary = scanner.Get(reference)?.Data as DictionaryToken;
                }
                catch (Exception)
                {
                    continue; // one unreadable object never stops the walk
                }

                if (dictionary is null)
                    continue;

                if (TryReadFileSpecStream(dictionary, scanner, out _) && seenSpecs.Add(reference))
                    indirectSpecs.Add((reference, dictionary));

                // The /EmbeddedFiles tree hangs off a Names dictionary: an indirect one is reached here as
                // its own object (the PDFium re-save shape); a direct one only through the old catalog
                // that holds it. An indirect /Names on a catalog is skipped — it is visited on its own.
                CollectOrphanedTree(dictionary, scanner, seenTrees, treeKeys, inlineSpecs);

                if (dictionary.TryGet(NamesEntry, out var namesToken) && namesToken is DictionaryToken directNames)
                    CollectOrphanedTree(directNames, scanner, seenTrees, treeKeys, inlineSpecs);
            }

            var specs = indirectSpecs
                .Select(s => (Name: treeKeys.TryGetValue(s.Reference, out var key) ? key : string.Empty, s.Spec))
                .Concat(inlineSpecs.Select(s => (Name: s.Key, s.Spec)))
                .ToList();

            if (specs.Count > MaxOrphanFileSpecs)
            {
                logger.LogWarning(
                    "{SpecCount} orphaned file specifications for document {DocumentId} tenant {TenantId}; " +
                    "reading the first {MaxSpecs}.",
                    specs.Count, documentId, tenantId, MaxOrphanFileSpecs);
            }

            foreach (var (key, fileSpec) in specs.Take(MaxOrphanFileSpecs))
            {
                if (!TryReadFileSpecStream(fileSpec, scanner, out var stream))
                    continue;

                var name = string.IsNullOrEmpty(key) ? ReadFileName(fileSpec, scanner) : key;

                byte[] data;
                try
                {
                    if (!TryDecodeBounded(stream, scanner, OrphanSite, logger, documentId, tenantId, out data))
                    {
                        scan.Seen++;
                        scan.HasOrphanedFileSpec = true;
                        scan.RecordFailure(AttachmentTooLargeFailure);
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    scan.Seen++;
                    scan.HasOrphanedFileSpec = true;
                    scan.RecordFailure(ex);
                    logger.LogWarning(
                        "An orphaned embedded file could not be decoded for document {DocumentId} " +
                        "({ExceptionType}), continuing with the remaining ones.",
                        documentId, ex.GetType().Name);
                    continue;
                }

                if (!StartsWithPdfMagic(data) && !PresentsAsPdf(name, stream))
                    continue; // not a PDF by name, type or content — some other kind of attachment

                scan.Seen++;
                scan.HasOrphanedFileSpec = true;
                scan.ConsiderOrphan(name, data, candidates);
            }

            if (scan.HasOrphanedFileSpec)
            {
                logger.LogInformation(
                    "Embedded PDF attachments found through the orphaned file specification fallback " +
                    "for document {DocumentId} tenant {TenantId}: {OrphanCount} file specifications, " +
                    "source {AttachmentSource}",
                    documentId, tenantId, scan.Seen, OrphanedFileSpecSource);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Orphaned file specifications could not be scanned for document {DocumentId} " +
                "({ExceptionType}), analysing the document as it is.",
                documentId, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Reads the /EmbeddedFiles name tree of a Names dictionary found by the orphan walk, once per
    /// indirect tree: indirect file specifications get their key as name (the only readable name
    /// when /F and /UF hold a server path), inline ones (a dictionary written straight into the
    /// /Names array, which the cross-reference walk cannot see on its own) are collected with their
    /// key. A malformed tree costs only its names.
    /// </summary>
    private static void CollectOrphanedTree(
        DictionaryToken                          namesDictionary,
        IPdfTokenScanner                         scanner,
        HashSet<IndirectReference>               seenTrees,
        Dictionary<IndirectReference, string>    treeKeys,
        List<(string Key, DictionaryToken Spec)> inlineSpecs)
    {
        if (!namesDictionary.TryGet(EmbeddedFilesTreeEntry, out var treeToken))
            return;

        if (treeToken is IndirectReferenceToken treeReference && !seenTrees.Add(treeReference.Data))
            return;

        if (Resolve<DictionaryToken>(treeToken, scanner) is not { } tree)
            return;

        var entries = new List<(string Key, IToken Value)>();
        try
        {
            FlattenNameTree(tree, scanner, entries, depth: 0, visited: []);
        }
        catch (Exception)
        {
            return;
        }

        foreach (var (key, value) in entries)
        {
            if (value is IndirectReferenceToken reference)
            {
                treeKeys.TryAdd(reference.Data, key);
                continue;
            }

            if (value is DictionaryToken inline && TryReadFileSpecStream(inline, scanner, out _))
                inlineSpecs.Add((key, inline));
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a /FileAttachment annotation down to its embedded file stream (not yet decoded):
    /// /FS → /EF → /F, falling back to /UF.
    /// </summary>
    private static bool TryReadAnnotationAttachment(
        Annotation       annotation,
        IPdfTokenScanner scanner,
        out string       name,
        out StreamToken  stream)
    {
        name   = string.Empty;
        stream = null!;

        if (!annotation.AnnotationDictionary.TryGet<DictionaryToken>(
                FileSpecificationEntry, scanner, out var fileSpec) || fileSpec is null)
            return false;

        if (!TryReadFileSpecStream(fileSpec, scanner, out stream))
            return false;

        name = ReadFileName(fileSpec, scanner);
        return true;
    }

    /// <summary>
    /// Decodes an embedded file stream within <see cref="MaxDecodedAttachmentBytes"/>. False — with one
    /// Warning carrying the site and the sizes, never a name or content — when the stream declares a
    /// larger size (/Params /Size or /DL; nothing is decoded then) or decodes past the cap. Decode
    /// exceptions propagate exactly as <see cref="StreamToken"/> decoding throws them today.
    /// </summary>
    private static bool TryDecodeBounded(
        StreamToken      stream,
        IPdfTokenScanner scanner,
        string           site,
        ILogger          logger,
        Guid             documentId,
        string           tenantId,
        out byte[]       data)
    {
        data = [];

        var declared = DeclaredSize(stream, scanner);
        if (declared > MaxDecodedAttachmentBytes)
        {
            LogTooLarge(logger, documentId, tenantId, site, declared, decodedBytes: null);
            return false;
        }

        long? decodedPastCap;
        if (IsLoneFlate(stream, scanner))
        {
            decodedPastCap = InflateBounded(stream.Data.ToArray(), out data);
        }
        else
        {
            data = stream.Decode(DefaultFilterProvider.Instance).ToArray();
            decodedPastCap = data.Length > MaxDecodedAttachmentBytes ? data.Length : null;
        }

        if (decodedPastCap is not null || data.Length > MaxDecodedAttachmentBytes)
        {
            LogTooLarge(logger, documentId, tenantId, site, declared, decodedPastCap ?? data.Length);
            data = [];
            return false;
        }

        return true;
    }

    private static void LogTooLarge(
        ILogger logger, Guid documentId, string tenantId, string site, double? declaredBytes, long? decodedBytes) =>
        logger.LogWarning(
            "Embedded attachment skipped for its size for document {DocumentId} tenant {TenantId}: site {Site}, " +
            "declared {DeclaredBytes} bytes, decoded at least {DecodedBytes} bytes, cap {MaxBytes} bytes.",
            documentId, tenantId, site, declaredBytes, decodedBytes, MaxDecodedAttachmentBytes);

    /// <summary>
    /// The decoded size the stream declares: the larger of /Params /Size (an embedded file's size) and
    /// /DL (a stream's decoded length). Null when it declares neither.
    /// </summary>
    private static double? DeclaredSize(StreamToken stream, IPdfTokenScanner scanner)
    {
        double? declared = null;
        var dictionary   = stream.StreamDictionary;

        if (dictionary.TryGet<DictionaryToken>(ParamsEntry, scanner, out var parameters) && parameters is not null
            && parameters.TryGet<NumericToken>(SizeEntry, scanner, out var size) && size is not null)
        {
            declared = size.Double;
        }

        if (dictionary.TryGet<NumericToken>(DecodedLengthEntry, scanner, out var length) && length is not null)
            declared = Math.Max(declared ?? double.MinValue, length.Double);

        return declared;
    }

    /// <summary>
    /// Whether the stream's only filter is FlateDecode (or its /Fl abbreviation) with no predictor —
    /// the case <see cref="InflateBounded"/> reproduces byte for byte. Anything else is left to PdfPig.
    /// </summary>
    private static bool IsLoneFlate(StreamToken stream, IPdfTokenScanner scanner)
    {
        var dictionary = stream.StreamDictionary;
        if (!dictionary.TryGet(FilterEntry, out var filterToken))
            return false;

        var filter = Resolve<IToken>(filterToken, scanner);
        if (filter is ArrayToken { Data.Count: 1 } array)
            filter = Resolve<IToken>(array.Data[0], scanner);

        if (filter is not NameToken name || !(name.Equals(FlateDecodeName) || name.Equals(FlateDecodeAbbreviation)))
            return false;

        foreach (var entry in new[] { DecodeParmsEntry, DecodeParmsAbbreviation })
        {
            if (!dictionary.TryGet(entry, out var parmsToken))
                continue;

            var parms = Resolve<IToken>(parmsToken, scanner);
            if (parms is ArrayToken { Data.Count: 1 } parmsArray)
                parms = Resolve<IToken>(parmsArray.Data[0], scanner);

            if (parms is DictionaryToken parmsDictionary && parmsDictionary.TryGet(PredictorEntry, out _))
                return false;

            if (parms is not (null or NullToken or DictionaryToken))
                return false; // a shape PdfPig resolves its own way
        }

        return true;
    }

    /// <summary>
    /// PdfPig 0.1.15's FlateFilter, bounded: the two zlib header bytes are skipped unchecked, the rest is
    /// inflated with <see cref="DeflateStream"/> (no Adler-32 check), and when inflation fails the RAW
    /// input is returned, exactly as PdfPig does. Returns the size reached when the output would pass
    /// <see cref="MaxDecodedAttachmentBytes"/> — inflation stops there — and null otherwise.
    /// </summary>
    internal static long? InflateBounded(byte[] encoded, out byte[] data)
    {
        try
        {
            using var input  = new MemoryStream(encoded, writable: false);
            using var output = new MemoryStream();

            input.ReadByte();
            input.ReadByte();

            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            {
                var buffer = new byte[InflateChunkBytes];
                int read;
                while ((read = deflate.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length + read > MaxDecodedAttachmentBytes)
                    {
                        data = [];
                        return output.Length + read;
                    }

                    output.Write(buffer, 0, read);
                }
            }

            data = output.ToArray();
            return null;
        }
        catch (Exception)
        {
            // PdfPig swallows any inflate failure and hands back the undecoded bytes.
            data = encoded;
            return null;
        }
    }

    /// <summary>
    /// The embedded file stream of a file specification: /EF → /F, falling back to /UF.
    /// </summary>
    private static bool TryReadFileSpecStream(
        DictionaryToken  fileSpec,
        IPdfTokenScanner scanner,
        out StreamToken  stream)
    {
        stream = null!;

        if (!fileSpec.TryGet<DictionaryToken>(
                EmbeddedFilesEntry, scanner, out var embeddedFiles) || embeddedFiles is null)
            return false;

        if (embeddedFiles.TryGet<StreamToken>(FileEntry, scanner, out var file) && file is not null)
        {
            stream = file;
            return true;
        }

        if (embeddedFiles.TryGet<StreamToken>(UnicodeFileEntry, scanner, out var unicode) && unicode is not null)
        {
            stream = unicode;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Flattens a name tree into (key, value) pairs: the node's /Names pairs, then its /Kids.
    /// Keys may be literal or hex strings. Guarded against cycles and runaway depth.
    /// </summary>
    private static void FlattenNameTree(
        DictionaryToken                node,
        IPdfTokenScanner               scanner,
        List<(string Key, IToken Value)> entries,
        int                            depth,
        HashSet<IndirectReference>     visited)
    {
        if (depth > MaxNameTreeDepth)
            throw new InvalidOperationException("EmbeddedFiles name tree is nested too deeply.");

        if (node.TryGet<ArrayToken>(NamesEntry, scanner, out var pairs) && pairs is not null)
        {
            for (var i = 0; i + 1 < pairs.Data.Count; i += 2)
            {
                if (Resolve<IToken>(pairs.Data[i], scanner) is IDataToken<string> key)
                    entries.Add((key.Data, pairs.Data[i + 1]));
            }
        }

        if (node.TryGet<ArrayToken>(KidsEntry, scanner, out var kids) && kids is not null)
        {
            foreach (var kid in kids.Data)
            {
                if (kid is IndirectReferenceToken reference && !visited.Add(reference.Data))
                    continue;

                if (Resolve<DictionaryToken>(kid, scanner) is { } child)
                    FlattenNameTree(child, scanner, entries, depth + 1, visited);
            }
        }
    }

    /// <summary>Follows indirect references (through the scanner, which decrypts) to a direct token.</summary>
    private static T? Resolve<T>(IToken? token, IPdfTokenScanner scanner) where T : class, IToken
    {
        for (var hops = 0; token is IndirectReferenceToken reference && hops < MaxReferenceHops; hops++)
            token = scanner.Get(reference.Data)?.Data;

        return token as T;
    }

    private static T? Resolve<T>(IndirectReference reference, IPdfTokenScanner scanner) where T : class, IToken =>
        Resolve<T>(new IndirectReferenceToken(reference), scanner);

    /// <summary>
    /// Best-effort file name from the file specification dictionary. An empty name is
    /// harmless — <see cref="Qualifies"/> reads the %PDF header, never the name.
    /// </summary>
    private static string ReadFileName(DictionaryToken fileSpec, IPdfTokenScanner scanner)
    {
        foreach (var entry in new[] { UnicodeFileEntry, FileEntry })
        {
            if (fileSpec.TryGet<StringToken>(entry, scanner, out var text) && text is not null)
                return text.Data;

            if (fileSpec.TryGet<HexToken>(entry, scanner, out var hex) && hex is not null)
                return hex.Data;
        }

        return string.Empty;
    }

    /// <summary>
    /// Whether an attachment's file name identifies it as the financial statements
    /// ("EECC AL 30-06-2025.pdf", "Estados Contables 2024.pdf", "Balance.pdf"). A name that
    /// also carries a deprioritized term — the auditor's report, the oblea, a certification —
    /// never counts, so "Informe del auditor sobre el balance" does not beat the statements it
    /// reports on. Case- and accent-insensitive; '_' and '-' read as spaces.
    /// </summary>
    internal static bool IsPreferredName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var normalized = NormalizeName(name);

        return PreferredNameTerms.Any(t => normalized.Contains(t, StringComparison.Ordinal))
            && !DeprioritizedNameTerms.Any(t => normalized.Contains(t, StringComparison.Ordinal));
    }

    private static string NormalizeName(string name)
    {
        var decomposed = name.Normalize(NormalizationForm.FormD);
        var builder    = new StringBuilder(decomposed.Length);

        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;

            builder.Append(ch is '_' or '-' ? ' ' : char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }

    /// <summary>
    /// A LIVE attachment qualifies on its content: over the size threshold and carrying the %PDF header
    /// (<see cref="HasPdfHeader"/>). Its name only decides between qualifying candidates.
    /// </summary>
    private static bool Qualifies(byte[] data) =>
        data.Length > MinAttachmentBytes && HasPdfHeader(data);

    /// <summary>
    /// The %PDF header anywhere in the first <see cref="PdfHeaderSearchBytes"/> bytes — the PDF
    /// specification lets a reader accept junk before it.
    /// </summary>
    internal static bool HasPdfHeader(byte[] data) =>
        data.AsSpan(0, Math.Min(data.Length, PdfHeaderSearchBytes)).IndexOf("%PDF"u8) >= 0;

    /// <summary>
    /// The stricter rule for an orphaned attachment: nothing in the live document points at it, so
    /// only its content can vouch for it — %PDF magic and the size threshold, never the name.
    /// </summary>
    private static bool QualifiesAsOrphan(byte[] data) =>
        data.Length > MinAttachmentBytes && StartsWithPdfMagic(data);

    private static bool StartsWithPdfMagic(byte[] data) =>
        data.Length >= 4 &&
        data[0] == 0x25 && data[1] == 0x50 &&  // %PDF magic
        data[2] == 0x44 && data[3] == 0x46;

    /// <summary>
    /// Whether an orphaned attachment presents itself as a PDF — a ".pdf" name, an
    /// application/pdf /Subtype on its stream, or a name identifying the statements.
    /// </summary>
    private static bool PresentsAsPdf(string name, StreamToken stream) =>
        name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
        || IsPreferredName(name)
        || (stream.StreamDictionary.TryGet(SubtypeEntry, out var subtype)
            && subtype is NameToken { Data: PdfMediaType });

    /// <summary>
    /// Drops candidates that are byte-identical duplicates of an earlier one, which
    /// happens when the same attachment is reachable from both sources. The byte
    /// comparison only runs once name and length already match.
    /// </summary>
    private static void Dedupe(List<(string Name, byte[] Data)> candidates)
    {
        for (var i = candidates.Count - 1; i > 0; i--)
        {
            for (var j = 0; j < i; j++)
            {
                if (candidates[i].Name == candidates[j].Name &&
                    candidates[i].Data.Length == candidates[j].Data.Length &&
                    candidates[i].Data.AsSpan().SequenceEqual(candidates[j].Data))
                {
                    candidates.RemoveAt(i);
                    break;
                }
            }
        }
    }

    // ── Per-level bookkeeping ────────────────────────────────────────────────

    /// <summary>What one level's scan saw, beyond its candidates. Mutable, local to one pass.</summary>
    private sealed class ScanState
    {
        public int     Seen                    { get; set; }
        public int     Failed                  { get; private set; }
        public string? FailureType             { get; private set; }
        public bool    HasNameTree             { get; set; }
        public bool    HasAnnotationAttachment { get; set; }
        public bool    HasOrphanedFileSpec     { get; set; }
        public bool    PreferredUnqualified    { get; private set; }
        public int     NotAPdfRejected         { get; private set; }

        public void RecordFailure(Exception ex) => RecordFailure(ex.GetType().Name);

        public void RecordFailure(string failureType)
        {
            Failed++;
            FailureType ??= failureType;
        }

        /// <summary>
        /// A live attachment: a candidate under <see cref="Qualifies"/>. One over the size threshold that
        /// is named as a PDF but carries no %PDF header — which a name alone used to let through — counts
        /// as rejected (and flags the level unread), as a failed decode would.
        /// </summary>
        public void Consider(string name, byte[] data, List<(string Name, byte[] Data)> candidates)
        {
            if (Qualifies(data))
            {
                candidates.Add((name, data));
                return;
            }

            if (data.Length > MinAttachmentBytes
                && name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                && !HasPdfHeader(data))
            {
                NotAPdfRejected++;
            }

            if (IsPreferredName(name))
                PreferredUnqualified = true;
        }

        /// <summary>
        /// An orphan that presents itself as a PDF: a candidate only under <see cref="QualifiesAsOrphan"/>.
        /// Bytes that are not a PDF at all count as rejected (and flag the level unread); a real PDF
        /// under the size threshold is treated as on the live path.
        /// </summary>
        public void ConsiderOrphan(string name, byte[] data, List<(string Name, byte[] Data)> candidates)
        {
            if (QualifiesAsOrphan(data))
            {
                candidates.Add((name, data));
                return;
            }

            if (!StartsWithPdfMagic(data))
                NotAPdfRejected++;

            if (IsPreferredName(name))
                PreferredUnqualified = true;
        }

        public LevelOutcome Outcome(byte[] selected, bool ambiguous) => new(
            selected,
            Seen,
            HasNameTree || HasAnnotationAttachment || HasOrphanedFileSpec,
            Failed,
            FailureType,
            ambiguous,
            PreferredUnqualified,
            HasOrphanedFileSpec,
            NotAPdfRejected);
    }

    private sealed record LevelOutcome(
        byte[]  Selected,
        int     Seen,
        bool    HasEmbeddedFiles,
        int     Failed,
        string? FailureType,
        bool    Ambiguous,
        bool    PreferredUnqualified,
        bool    HasOrphanedFileSpec,
        int     NotAPdfRejected);
}
