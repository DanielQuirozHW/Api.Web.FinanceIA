// Project : Api.Web.FinanceIA
// Created : 2026-08-28

using System.Globalization;
using System.IO.Compression;
using System.Text;

using Api.Web.FinanceIA.Models.Financia;

using ClosedXML.Excel;

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;

namespace Api.Web.FinanceIA.Servicios;

/// <summary>
/// Renders an uploaded .xlsx as Markdown tables. Deterministic and stateless — it holds no tenant
/// state and touches no I/O beyond the stream it is handed, so it is registered as a singleton and
/// unit-tested with no DI at all.
/// </summary>
public sealed class ExcelCashFlowParser : IExcelCashFlowParser
{
    // ── Caps ──────────────────────────────────────────────────────────────────────
    // The binding constraint on an attachment is the model's token budget, not bytes: the byte cap
    // only stops abuse, while these bound what is actually injected into a conversation. Sized so a
    // realistic cash flow (a few sheets, ~60 line items, ~25 month columns) fits whole and never
    // truncates, while a pathological workbook cannot dominate the turn.

    /// <summary>Largest attachment accepted, 5 MB. Deliberately separate from the PDF limit (<see cref="Api.Web.FinanceIA.Clases.PdfSizeLimits.MaxPdfBytes"/>).</summary>
    public const long MaxFileSizeBytes = 5L * 1024 * 1024;

    /// <summary>Most sheets rendered; sheets with no data do not count against it.</summary>
    public const int MaxSheets = 5;

    /// <summary>Most rows rendered per sheet, the header row included.</summary>
    public const int MaxRowsPerSheet = 500;

    /// <summary>Most columns rendered per sheet.</summary>
    public const int MaxColumnsPerSheet = 60;

    /// <summary>Most cells rendered across the whole workbook — roughly 20-30K tokens.</summary>
    public const int MaxTotalCells = 5_000;

    /// <summary>
    /// Most uncompressed content the package may declare. A ZIP can expand by orders of magnitude,
    /// and the reader materialises the whole workbook in memory before any cap below can apply, so
    /// this is checked from the central directory first — the only point at which refusing is cheap.
    /// </summary>
    public const long MaxUncompressedBytes = 100L * 1024 * 1024;

    // Work bound, distinct from the output caps above. Finding empty rows and columns means reading
    // cells that may not survive, so a crafted sheet could cost far more reads than it yields rows.
    // Scanning stops here regardless of shape, and stopping early reports as truncation.
    private const int MaxScannedCells = MaxTotalCells * 8;
    private const int MaxScannedColumnsPerSheet = MaxColumnsPerSheet * 4;

    private readonly ILogger<ExcelCashFlowParser> _logger;

    /// <summary>Initializes a new instance of <see cref="ExcelCashFlowParser"/>.</summary>
    public ExcelCashFlowParser(ILogger<ExcelCashFlowParser> logger) => _logger = logger;

    /// <inheritdoc />
    public ExcelParseResult Parse(Stream xlsxStream)
    {
        ArgumentNullException.ThrowIfNull(xlsxStream);

        // Buffered once so each gate below reads from its own stream. The gates use libraries with
        // differing opinions about whether disposing a reader also disposes the stream underneath
        // it; owning the bytes removes the question rather than depending on the answer.
        var bytes = ReadAllBytes(xlsxStream);

        if (!DeclaredSizeIsSane(bytes))
            return ExcelParseResult.Rejected(ExcelParseStatus.DecompressionBombSuspected);

        var typeStatus = CheckDocumentType(bytes);
        if (typeStatus != ExcelParseStatus.Ok)
            return ExcelParseResult.Rejected(typeStatus);

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var workbook = new XLWorkbook(stream);
            return Render(workbook);
        }
        catch (Exception ex)
        {
            // The document-type gate already proved this is a spreadsheet package, so a throw here
            // means damaged internals. Logged at Warning, not Error: a bad upload is the user's
            // problem to fix, and the caller turns this into a clean 400.
            _logger.LogWarning(ex, "Excel attachment opened as a workbook but could not be read");
            return ExcelParseResult.Rejected(ExcelParseStatus.NotAValidWorkbook);
        }
    }

    // ── Gates ─────────────────────────────────────────────────────────────────────

    private static byte[] ReadAllBytes(Stream source)
    {
        if (source.CanSeek) source.Position = 0;
        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        return buffer.ToArray();
    }

    private bool DeclaredSizeIsSane(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            long declared = 0;
            foreach (var entry in archive.Entries)
            {
                declared += entry.Length;
                if (declared > MaxUncompressedBytes)
                {
                    _logger.LogWarning(
                        "Excel attachment rejected: declares over {LimitBytes} bytes uncompressed from {PackedBytes} packed",
                        MaxUncompressedBytes, bytes.LongLength);
                    return false;
                }
            }

            return true;
        }
        catch (InvalidDataException)
        {
            // Not a readable ZIP at all. Let the document-type gate below name the failure, so a
            // corrupt package and a .docx come back as the same, accurate "not a workbook".
            return true;
        }
    }

    private ExcelParseStatus CheckDocumentType(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var document = SpreadsheetDocument.Open(stream, isEditable: false);

            // By document type, never by file extension: renaming a .xlsm to .xlsx changes nothing
            // here. Anything that is not a plain workbook — macro-enabled, template, add-in — is
            // refused, because a cash flow needs none of them.
            return document.DocumentType == SpreadsheetDocumentType.Workbook
                ? ExcelParseStatus.Ok
                : ExcelParseStatus.MacroEnabledOrTemplate;
        }
        catch (Exception ex)
        {
            // This is the gate that does the real work. The magic-byte check upstream can only
            // prove the bytes are a ZIP, so a .docx, a .jar or a plain .zip arrives here intact and
            // fails to open as a spreadsheet package.
            _logger.LogWarning(ex, "Excel attachment is not an OOXML spreadsheet package");
            return ExcelParseStatus.NotAValidWorkbook;
        }
    }

    // ── Rendering ─────────────────────────────────────────────────────────────────

    private static ExcelParseResult Render(XLWorkbook workbook)
    {
        var sheets = workbook.Worksheets
            .Select(ws => (Sheet: ws, Used: ws.RangeUsed()))
            .Where(x => x.Used is not null)
            .ToList();

        if (sheets.Count == 0)
            return ExcelParseResult.Rejected(ExcelParseStatus.NoWorksheets);

        // Line breaks are a literal '\n', never AppendLine: this Markdown is sent to Claude, and
        // AppendLine would write CRLF on the Windows App Service, so the same workbook would produce
        // a different prompt on the host that serves it than on the Linux CI that tests it.
        var markdown  = new StringBuilder();
        var notes     = new List<string>();
        var scanned   = 0;
        var cellsUsed = 0;
        var rowsUsed  = 0;
        var rendered  = 0;

        for (var i = 0; i < sheets.Count; i++)
        {
            if (rendered == MaxSheets || cellsUsed >= MaxTotalCells || scanned >= MaxScannedCells)
            {
                notes.Add($"Se omitieron {sheets.Count - i} hoja(s) de {sheets.Count}.");
                break;
            }

            var (sheet, used) = sheets[i];
            var table = BuildTable(used!, MaxTotalCells - cellsUsed, MaxScannedCells - scanned, out var scannedHere);

            scanned += scannedHere;

            if (table is null)
            {
                // No budget left even for this sheet's header row; everything from here is omitted.
                notes.Add($"Se omitieron {sheets.Count - i} hoja(s) de {sheets.Count}.");
                break;
            }

            if (rendered > 0) markdown.Append('\n');
            markdown.Append($"## {Escape(sheet.Name)}").Append('\n');
            markdown.Append('\n');
            markdown.Append(table.Markdown);

            cellsUsed += table.Cells;
            rowsUsed  += table.Rows;
            rendered++;

            if (table.RowsOmitted > 0 || table.ColumnsOmitted > 0)
            {
                var cut = new List<string>();
                if (table.RowsOmitted > 0)
                    cut.Add($"{table.RowsOmitted} fila(s) de {table.RowsTotal}");
                if (table.ColumnsOmitted > 0)
                    cut.Add($"{table.ColumnsOmitted} columna(s) de {table.ColumnsTotal}");

                notes.Add($"En «{sheet.Name.Trim()}» se omitieron {string.Join(" y ", cut)}.");
            }
        }

        if (rendered == 0)
            return ExcelParseResult.Rejected(ExcelParseStatus.NoWorksheets);

        string? note = null;
        if (notes.Count > 0)
        {
            var sb = new StringBuilder();
            sb.Append("**Nota:** el archivo se truncó para respetar los límites de procesamiento. ");
            sb.Append(string.Join(" ", notes));
            note = sb.ToString();

            markdown.Append('\n');
            markdown.Append($"> {note}").Append('\n');
        }

        return new ExcelParseResult(
            ExcelParseStatus.Ok, markdown.ToString(), rendered, rowsUsed, cellsUsed, note is not null, note);
    }

    private sealed record Table(
        string Markdown, int Rows, int Cells,
        int RowsOmitted, int RowsTotal, int ColumnsOmitted, int ColumnsTotal);

    /// <summary>
    /// Renders one sheet's used range, dropping wholly empty rows and columns first so the caps are
    /// spent on real data rather than on padding. Returns null when not even a header row fits in
    /// the remaining budget.
    /// </summary>
    private static Table? BuildTable(IXLRange used, int cellBudget, int scanBudget, out int scanned)
    {
        scanned = 0;

        var columnsSeen = Math.Min(used.ColumnCount(), MaxScannedColumnsPerSheet);
        if (columnsSeen == 0 || cellBudget <= 0 || scanBudget <= 0) return null;

        // Read rows until enough survive the empty-row filter, or the scan budget runs out. Values
        // are rendered once, here, and reused below — re-reading cells is the expensive part.
        var rows = new List<string[]>();
        var rowsSeen = 0;

        foreach (var row in used.Rows())
        {
            if (rows.Count >= MaxRowsPerSheet || scanned + columnsSeen > scanBudget) break;

            rowsSeen++;
            var values = new string[columnsSeen];
            var any = false;

            for (var c = 1; c <= columnsSeen; c++)
            {
                var text = RenderCell(row.Cell(c));
                values[c - 1] = text;
                if (text.Length > 0) any = true;
            }

            scanned += columnsSeen;
            if (any) rows.Add(values);
        }

        if (rows.Count == 0) return null;

        // A column is kept only when some surviving row puts something in it.
        var keptColumns = new List<int>(columnsSeen);
        for (var c = 0; c < columnsSeen; c++)
        {
            if (rows.Any(r => r[c].Length > 0)) keptColumns.Add(c);
        }

        if (keptColumns.Count == 0) return null;

        var columnsTotal   = keptColumns.Count;
        var columnLimit    = Math.Min(columnsTotal, MaxColumnsPerSheet);
        var columnsOmitted = columnsTotal - columnLimit;

        var rowsTotal   = rows.Count + Math.Max(0, used.RowCount() - rowsSeen);
        var rowLimit    = Math.Min(rows.Count, cellBudget / columnLimit);
        if (rowLimit < 1) return null;
        var rowsOmitted = rowsTotal - rowLimit;

        var markdown = new StringBuilder();

        AppendRow(markdown, rows[0], keptColumns, columnLimit);
        markdown.Append('|');
        for (var c = 0; c < columnLimit; c++) markdown.Append(" --- |");
        markdown.Append('\n');

        for (var r = 1; r < rowLimit; r++)
            AppendRow(markdown, rows[r], keptColumns, columnLimit);

        return new Table(
            markdown.ToString(), rowLimit, rowLimit * columnLimit,
            rowsOmitted, rowsTotal, columnsOmitted, columnsTotal);
    }

    private static void AppendRow(StringBuilder sb, string[] values, List<int> keptColumns, int columnLimit)
    {
        sb.Append('|');
        for (var c = 0; c < columnLimit; c++)
        {
            sb.Append(' ').Append(Escape(values[keptColumns[c]])).Append(" |");
        }
        sb.Append('\n');
    }

    /// <summary>
    /// Renders one cell as the file stores it. Figures are never reformatted or localized: the
    /// analyst's workbook already carries its own formatting, and re-rendering a number through a
    /// display culture is how a thousands separator turns into a decimal point and corrupts it.
    /// </summary>
    private static string RenderCell(IXLCell cell)
    {
        try
        {
            // Formulas are read from their cached result, never evaluated. Recalculating attacker-
            // supplied formulas would be both slow and a way to make the parser do work for them.
            var value = cell.HasFormula ? cell.CachedValue : cell.Value;

            return value.Type switch
            {
                XLDataType.Blank    => string.Empty,
                XLDataType.Boolean  => value.GetBoolean() ? "true" : "false",

                // InvariantCulture, deliberately: .xlsx stores numeric cells as invariant-format
                // values in the package XML no matter what locale authored the file, so this is a
                // pass-through of the stored figure, not a formatting choice.
                XLDataType.Number   => value.GetNumber().ToString(CultureInfo.InvariantCulture),

                // The one type where "raw" would be wrong — an unformatted Excel date is an OLE
                // serial number, so passing it through would turn a date into a meaningless integer.
                XLDataType.DateTime => FormatDateTime(value.GetDateTime()),
                XLDataType.TimeSpan => value.GetTimeSpan().ToString("c", CultureInfo.InvariantCulture),

                XLDataType.Error    => FormatError(value.GetError()),
                _                   => value.GetText() ?? string.Empty
            };
        }
        catch (Exception)
        {
            // One unreadable cell costs that cell, never the attachment.
            return string.Empty;
        }
    }

    private static string FormatDateTime(DateTime value) =>
        value.TimeOfDay == TimeSpan.Zero
            ? value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    // Rendered as the tokens the analyst sees in Excel rather than the library's enum names, so the
    // model reads "#DIV/0!" and not "DivisionByZero".
    private static string FormatError(XLError error) => error switch
    {
        XLError.CellReference     => "#REF!",
        XLError.DivisionByZero    => "#DIV/0!",
        XLError.NameNotRecognized => "#NAME?",
        XLError.NoValueAvailable  => "#N/A",
        XLError.NullValue         => "#NULL!",
        XLError.NumberInvalid     => "#NUM!",
        XLError.IncompatibleValue => "#VALUE!",
        _                         => "#ERROR!"
    };

    /// <summary>Makes a value safe to sit inside a Markdown table cell without changing its meaning.</summary>
    private static string Escape(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return string.Empty;
        if (trimmed.IndexOfAny(['|', '\r', '\n']) < 0) return trimmed;

        var sb = new StringBuilder(trimmed.Length + 8);
        foreach (var ch in trimmed)
        {
            switch (ch)
            {
                case '|':  sb.Append("\\|"); break;
                case '\r': break;
                case '\n': sb.Append(' '); break;
                default:   sb.Append(ch); break;
            }
        }

        return sb.ToString();
    }
}
