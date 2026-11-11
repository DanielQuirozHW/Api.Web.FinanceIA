// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-06

using Api.Web.FinanceIA.Servicios;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig.Tokens;
using Xunit;
using static Api.Web.FinanceIA.Tests.Servicios.AttachmentPdfBuilder;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// The orphaned file specification fallback: a PDFium re-save leaves the original catalog — and the
/// attachments its /Names → /EmbeddedFiles reached — in the file, unreferenced, behind a new catalog
/// with only /Type and /Pages. All fixtures are synthetic (<see cref="OrphanedCatalogWrapper"/>).
/// </summary>
public sealed class PdfAttachmentExtractorOrphanTests
{
    private static readonly Guid DocumentId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static readonly NameToken NamesKey         = NameToken.Create("Names");
    private static readonly NameToken EmbeddedFilesKey = NameToken.Create("EmbeddedFiles");
    private static readonly NameToken JavaScriptKey    = NameToken.Create("JavaScript");
    private static readonly NameToken TypeKey          = NameToken.Create("Type");
    private const string TenantId = "tenant-test";

    // Distinguishable inner documents, both past the 10 KB threshold and within 20 % of each other,
    // so the size rule alone could not decide between them.
    private static readonly byte[] Informe            = Pdf(pageCount: 3,  padChars: PaddingChars + 2048);
    private static readonly byte[] TrabajoProfesional = Pdf(pageCount: 20, padChars: PaddingChars);

    private static EmbeddedBalanceUnwrap Unwrap(byte[] outer, ILogger? logger = null, int? maxObjects = null) =>
        PdfAttachmentExtractor.Unwrap(
            outer,
            logger ?? NullLogger<PdfAttachmentExtractorOrphanTests>.Instance,
            DocumentId,
            TenantId,
            maxObjects ?? PdfAttachmentExtractor.MaxOrphanScanObjects);

    // Synthetic server paths, as a PDFium re-save leaves in /F and /UF. Both contain "balances", so
    // read as names they would BOTH count as the statements: only the tree keys can tell them apart.
    private const string InformePath = @"\\srv\e\balances\filesnw\00000001_00000002_i.pdf";
    private const string TrabajoPath = @"\\srv\e\balances\filesnw\00000001_00000002_t.pdf";

    private static byte[] ReSavedLegalization(
        bool              inlineSpecs = false,
        OrphanNamesHolder holder      = OrphanNamesHolder.IndirectNamesDictionary) =>
        OrphanedCatalogWrapper(
        [
            new OrphanAttachment("Informe", Informe, FileName: InformePath),
            new OrphanAttachment("Trabajo Profesional", TrabajoProfesional, FileName: TrabajoPath),
        ],
        inlineSpecs: inlineSpecs,
        holder: holder);

    /// <summary>(inlineSpecs, namesIndirect): namesIndirect true is <see cref="OrphanNamesHolder.IndirectNamesDictionary"/>.</summary>
    public static TheoryData<bool, bool> Variants => new()
    {
        { false, true },  // the real PDFium shape: indirect Names object, indirect file specifications
        { true,  true },
        { false, false }, // Names dictionary direct inside the old catalog
        { true,  false },
    };

    private static OrphanNamesHolder Holder(bool namesIndirect) => namesIndirect
        ? OrphanNamesHolder.IndirectNamesDictionary
        : OrphanNamesHolder.DirectInOldCatalog;

    [Fact]
    public void Fixture_has_the_pdfium_resave_shape()
    {
        var outer = ReSavedLegalization();

        using var doc = PigDocument.Open(outer);

        Assert.False(doc.Structure.Catalog.CatalogDictionary.ContainsKey(NamesKey));
        Assert.False(doc.Advanced.TryGetEmbeddedFiles(out var files) && files is { Count: > 0 });
        Assert.Single(doc.Structure.CrossReferenceTable.Parts); // one xref section, no incremental update
        Assert.True(Informe.Length > 10 * 1024 && TrabajoProfesional.Length > 10 * 1024);
        Assert.NotEqual(Informe, TrabajoProfesional);

        var larger = Math.Max(Informe.Length, TrabajoProfesional.Length);
        Assert.True(Math.Abs(Informe.Length - TrabajoProfesional.Length) / (double)larger < 0.20,
            "fixture sizes must be within 20 % so only the name can decide");

        Assert.True(PdfAttachmentExtractor.IsPreferredName(InformePath));
        Assert.True(PdfAttachmentExtractor.IsPreferredName(TrabajoPath));
    }

    /// <summary>
    /// Guards each variant's layout, read back with PdfPig: where the /EmbeddedFiles tree hangs (its own
    /// indirect Names object, or a direct Names dictionary inside the old catalog), whether the tree's
    /// /Names array holds indirect references or DIRECT file specification dictionaries, and that every
    /// /EF stream is Flate-compressed.
    /// </summary>
    [Theory]
    [MemberData(nameof(Variants))]
    public void Fixture_variants_place_the_tree_and_the_specs_as_declared(bool inlineSpecs, bool namesIndirect)
    {
        var holder = Holder(namesIndirect);
        using var doc = PigDocument.Open(ReSavedLegalization(inlineSpecs, holder));
        var scanner = doc.Structure.TokenScanner;

        var objects = doc.Structure.CrossReferenceTable.ObjectOffsets.Keys
            .Select(r => scanner.Get(r)?.Data)
            .OfType<DictionaryToken>()
            .ToList();

        var namesObjects = objects.Where(d => d.ContainsKey(EmbeddedFilesKey)).ToList();
        var oldCatalog   = Assert.Single(objects, d =>
            d.TryGet(TypeKey, out var type) && type is NameToken { Data: "Catalog" } && d.ContainsKey(NamesKey));
        Assert.NotSame(doc.Structure.Catalog.CatalogDictionary, oldCatalog);

        DictionaryToken namesDictionary;
        if (holder == OrphanNamesHolder.IndirectNamesDictionary)
        {
            namesDictionary = Assert.Single(namesObjects);                    // its own indirect object …
            Assert.True(namesDictionary.ContainsKey(JavaScriptKey));
            Assert.IsType<IndirectReferenceToken>(oldCatalog.Data["Names"]); // … referenced only by the old catalog
        }
        else
        {
            Assert.Empty(namesObjects);                                       // no object holds /EmbeddedFiles itself
            namesDictionary = Assert.IsType<DictionaryToken>(oldCatalog.Data["Names"]);
        }

        var tree   = Assert.IsType<DictionaryToken>(namesDictionary.Data["EmbeddedFiles"]);
        var pairs  = Assert.IsType<ArrayToken>(tree.Data["Names"]).Data;
        var values = pairs.Where((_, i) => i % 2 == 1).ToList();
        Assert.Equal(2, values.Count);

        foreach (var value in values)
        {
            if (inlineSpecs)
                Assert.IsType<DictionaryToken>(value);
            else
                Assert.IsType<IndirectReferenceToken>(value);

            var spec     = value as DictionaryToken ?? (DictionaryToken)scanner.Get(((IndirectReferenceToken)value).Data)!.Data;
            var embedded = Assert.IsType<DictionaryToken>(spec.Data["EF"]);
            var stream   = Assert.IsType<StreamToken>(scanner.Get(((IndirectReferenceToken)embedded.Data["F"]).Data)!.Data);
            Assert.Equal("FlateDecode", Assert.IsType<NameToken>(stream.StreamDictionary.Data["Filter"]).Data);
            Assert.StartsWith(@"\\srv\", Assert.IsType<StringToken>(spec.Data["F"]).Data);
        }
    }

    /// <summary>
    /// The real PDFium re-save: the Names dictionary is its own unreferenced-by-anything-live indirect
    /// object holding /EmbeddedFiles directly, the two file specifications are indirect objects whose
    /// /F and /UF are server paths, and the names "Informe" / "Trabajo Profesional" exist ONLY as the
    /// tree keys. The statements must be chosen through the key.
    /// </summary>
    [Fact]
    public void The_real_pdfium_shape_selects_the_statements_through_the_tree_key()
    {
        var result = Unwrap(ReSavedLegalization(inlineSpecs: false, OrphanNamesHolder.IndirectNamesDictionary));

        Assert.Equal(TrabajoProfesional, result.Bytes);
        Assert.False(result.AttachmentUnread);
        Assert.True(result.FromOrphanedFileSpec);
        Assert.Equal(2, result.AttachmentsSeen);
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public void Every_variant_selects_the_statements_through_the_tree_key(bool inlineSpecs, bool namesIndirect)
    {
        var logger = new CapturingLogger();

        var result = Unwrap(ReSavedLegalization(inlineSpecs, Holder(namesIndirect)), logger);

        Assert.Equal(TrabajoProfesional, result.Bytes);
        Assert.False(result.AttachmentUnread);
        Assert.True(result.FromOrphanedFileSpec);
        Assert.Equal(2, result.AttachmentsSeen); // each file specification read once
        Assert.Contains(logger.Messages, m => m.Contains(PdfAttachmentExtractor.OrphanedFileSpecSource));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("Trabajo") || m.Contains("Informe") || m.Contains("srv"));
    }

    [Fact]
    public void A_live_name_tree_file_never_runs_the_fallback()
    {
        var balance = Pdf();
        var outer   = OrphanedCatalogWrapper(
            [new OrphanAttachment("Trabajo Profesional", TrabajoProfesional)],
            liveAttachments: [("Balance.pdf", balance)]);
        var logger  = new CapturingLogger();

        var result = Unwrap(outer, logger);

        Assert.Equal(balance, result.Bytes);
        Assert.False(result.FromOrphanedFileSpec);
        Assert.False(result.AttachmentUnread);
        Assert.DoesNotContain(logger.Messages, m => m.Contains(PdfAttachmentExtractor.OrphanedFileSpecSource));
    }

    /// <summary>
    /// The name alone never qualifies an orphan: "Trabajo Profesional.pdf" whose bytes are not a PDF is
    /// rejected, and the lone report is NOT analysed in its place — the cover is kept and flagged.
    /// </summary>
    [Fact]
    public void An_orphan_whose_bytes_are_not_a_pdf_is_rejected()
    {
        var outer = OrphanedCatalogWrapper(
        [
            new OrphanAttachment("Trabajo Profesional.pdf", NotAPdf()),
            new OrphanAttachment("Informe", Informe),
        ]);

        var result = Unwrap(outer);

        Assert.Same(outer, result.Bytes);
        Assert.True(result.AttachmentUnread);
        Assert.True(result.FromOrphanedFileSpec);
    }

    [Fact]
    public void Orphans_that_all_fail_flag_the_attachment_unread()
    {
        var outer = OrphanedCatalogWrapper(
        [
            new OrphanAttachment("Trabajo Profesional", null),     // undecodable
            new OrphanAttachment("Informe.pdf", NotAPdf()),        // not a PDF
        ]);

        var result = Unwrap(outer);

        Assert.Same(outer, result.Bytes);
        Assert.True(result.AttachmentUnread);
        Assert.True(result.FromOrphanedFileSpec);
        Assert.Equal(2, result.AttachmentsSeen);
        Assert.Equal(nameof(NotSupportedException), result.FailureType);
    }

    [Fact]
    public void A_pdf_with_no_file_specification_anywhere_passes_through()
    {
        var plain = Pdf();

        var result = Unwrap(plain);

        Assert.Same(plain, result.Bytes);
        Assert.False(result.AttachmentUnread);
        Assert.False(result.FromOrphanedFileSpec);
        Assert.Equal(0, result.AttachmentsSeen);
    }

    [Fact]
    public void An_orphan_that_is_not_a_pdf_at_all_is_ignored()
    {
        var outer = OrphanedCatalogWrapper(
            [new OrphanAttachment("datos.xml", NotAPdf(), MimeType: "text/xml")]);

        var result = Unwrap(outer);

        Assert.Same(outer, result.Bytes);
        Assert.False(result.AttachmentUnread);
        Assert.False(result.FromOrphanedFileSpec);
        Assert.Equal(0, result.AttachmentsSeen);
    }

    [Fact]
    public void The_object_cap_is_honoured()
    {
        var outer = ReSavedLegalization();

        // The cover's catalog and page tree come first; the orphaned objects are written after them.
        var capped = Unwrap(outer, maxObjects: 2);

        Assert.Same(outer, capped.Bytes);
        Assert.Equal(0, capped.AttachmentsSeen);
        Assert.False(capped.AttachmentUnread);
        Assert.False(capped.FromOrphanedFileSpec);

        Assert.Equal(TrabajoProfesional, Unwrap(outer).Bytes);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel                         logLevel,
            EventId                          eventId,
            TState                           state,
            Exception?                       exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
