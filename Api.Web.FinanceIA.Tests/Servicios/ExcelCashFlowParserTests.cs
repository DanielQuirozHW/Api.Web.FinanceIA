// Project : Api.Web.FinanceIA.Tests
// Created : 2026-08-28

using System.IO.Compression;
using System.Text;

using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;

using ClosedXML.Excel;

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Every fixture is built in-process, so no binary files are committed and each test states the
/// exact workbook it is about. The parser is the gate that decides whether an upload is really a
/// spreadsheet — the magic-byte check upstream can only prove the bytes are a ZIP — so the
/// rejection tests here are the ones carrying the security weight.
/// </summary>
public sealed class ExcelCashFlowParserTests
{
    private static ExcelCashFlowParser Parser() =>
        new(NullLogger<ExcelCashFlowParser>.Instance);

    private static MemoryStream Workbook(Action<XLWorkbook> build)
    {
        using var wb = new XLWorkbook();
        build(wb);
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        return ms;
    }

    private static ExcelParseResult ParseWorkbook(Action<XLWorkbook> build)
    {
        using var stream = Workbook(build);
        return Parser().Parse(stream);
    }

    // ── Happy path ────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_MultiSheetWorkbook_RendersOneTablePerSheet()
    {
        var result = ParseWorkbook(wb =>
        {
            var a = wb.AddWorksheet("Flujo");
            a.Cell("A1").Value = "Concepto";
            a.Cell("B1").Value = "Enero";
            a.Cell("A2").Value = "Cobranzas";
            a.Cell("B2").Value = 1500;

            var b = wb.AddWorksheet("Supuestos");
            b.Cell("A1").Value = "Tasa";
            b.Cell("A2").Value = 0.35;
        });

        Assert.Equal(ExcelParseStatus.Ok, result.Status);
        Assert.False(result.Truncated);
        Assert.Null(result.Note);

        Assert.Equal(2, result.SheetCount);
        Assert.Equal(4, result.RowCount);   // two header rows + two data rows
        Assert.Equal(6, result.CellCount);  // 2x2 on the first sheet, 2x1 on the second

        Assert.Contains("## Flujo", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("## Supuestos", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("| Concepto | Enero |", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("| --- | --- |", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("| Cobranzas | 1500 |", result.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_EmptyRowsAndColumns_AreDropped()
    {
        // The gap columns and the blank row exist in the used range but carry nothing; spending
        // caps on them would push real data out of the output.
        var result = ParseWorkbook(wb =>
        {
            var ws = wb.AddWorksheet("Hoja");
            ws.Cell("A1").Value = "Concepto";
            ws.Cell("D1").Value = "Total";
            ws.Cell("A3").Value = "Cobranzas";
            ws.Cell("D3").Value = 10;
        });

        Assert.Equal(ExcelParseStatus.Ok, result.Status);
        Assert.Equal(2, result.RowCount);   // the blank row 2 is gone
        Assert.Equal(4, result.CellCount);  // the blank columns B and C are gone
        Assert.Contains("| Concepto | Total |", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("| Cobranzas | 10 |", result.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_SheetsWithoutData_AreSkippedNotCounted()
    {
        var result = ParseWorkbook(wb =>
        {
            wb.AddWorksheet("Vacía");
            var ws = wb.AddWorksheet("Datos");
            ws.Cell("A1").Value = "Concepto";
            ws.Cell("A2").Value = "Cobranzas";
        });

        Assert.Equal(ExcelParseStatus.Ok, result.Status);
        Assert.Equal(1, result.SheetCount);
        Assert.False(result.Truncated);
        Assert.DoesNotContain("Vacía", result.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_WorkbookWithNoDataAtAll_IsRejected()
    {
        var result = ParseWorkbook(wb => wb.AddWorksheet("Vacía"));

        Assert.Equal(ExcelParseStatus.NoWorksheets, result.Status);
        Assert.Equal(string.Empty, result.Markdown);
    }

    [Fact]
    public void Parse_LeavesTheCallerStream_Open()
    {
        // The controller hashes the same stream and disposes it itself; a parser that closed it
        // would break that with no compile-time signal.
        using var stream = Workbook(wb => wb.AddWorksheet("Hoja").Cell("A1").Value = "x");

        Parser().Parse(stream);

        Assert.True(stream.CanRead);
    }

    // ── Cell rendering: figures pass through, never reformatted ───────────────────

    [Fact]
    public void Parse_Numbers_AreNotLocalized()
    {
        // The es-AR trap: rendered through a display culture, 1234.56 becomes "1.234,56" and the
        // decimal separator flips. That silently changes a figure by three orders of magnitude.
        var result = ParseWorkbook(wb =>
        {
            var ws = wb.AddWorksheet("Hoja");
            ws.Cell("A1").Value = "Monto";
            ws.Cell("A2").Value = 1234.56;
            ws.Cell("A3").Value = -890123.4;
        });

        Assert.Contains("| 1234.56 |", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("| -890123.4 |", result.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("1.234,56", result.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_Dates_AreRenderedIso8601()
    {
        // The one type where passing the stored value through would be wrong: an Excel date is an
        // OLE serial number, so "raw" would turn 2026-03-31 into 46112.
        var result = ParseWorkbook(wb =>
        {
            var ws = wb.AddWorksheet("Hoja");
            ws.Cell("A1").Value = "Fecha";
            ws.Cell("A2").Value = new DateTime(2026, 3, 31);
            ws.Cell("A3").Value = new DateTime(2026, 3, 31, 14, 5, 0);
        });

        Assert.Contains("| 2026-03-31 |", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("| 2026-03-31 14:05 |", result.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_Booleans_AreRenderedPlainly()
    {
        var result = ParseWorkbook(wb =>
        {
            var ws = wb.AddWorksheet("Hoja");
            ws.Cell("A1").Value = "Aprobado";
            ws.Cell("A2").Value = true;
            ws.Cell("A3").Value = false;
        });

        Assert.Contains("| true |", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("| false |", result.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_PipesAndNewlinesInText_AreEscapedNotDropped()
    {
        // An unescaped pipe would split one cell into two and shift every column after it.
        var result = ParseWorkbook(wb =>
        {
            var ws = wb.AddWorksheet("Hoja");
            ws.Cell("A1").Value = "Nota";
            ws.Cell("B1").Value = "Otro";
            ws.Cell("A2").Value = "a|b";
            ws.Cell("B2").Value = "linea1\nlinea2";
        });

        Assert.Contains(@"| a\|b | linea1 linea2 |", result.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ErrorCells_RenderTheExcelToken()
    {
        using var stream = SheetXmlWorkbook(SpreadsheetDocumentType.Workbook, sheetData =>
        {
            sheetData.Append(NumberRow(1, "A1", "7"));
            sheetData.Append(new Row(
                new Cell { CellReference = "A2", DataType = CellValues.Error, CellValue = new CellValue("#DIV/0!") })
            { RowIndex = 2 });
        });

        var result = Parser().Parse(stream);

        Assert.Equal(ExcelParseStatus.Ok, result.Status);
        Assert.Contains("#DIV/0!", result.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_FormulaCells_UseTheCachedValueAndDoNotRecalculate()
    {
        // The cached value is deliberately wrong for the formula: SUM(A1:A1) over 5 would evaluate
        // to 5, so seeing 999 is proof the parser read the cache instead of running the formula.
        // Recalculating a stranger's workbook would be both slow and work done on their behalf.
        using var stream = SheetXmlWorkbook(SpreadsheetDocumentType.Workbook, sheetData =>
        {
            sheetData.Append(NumberRow(1, "A1", "5"));
            sheetData.Append(new Row(
                new Cell
                {
                    CellReference = "A2",
                    CellFormula   = new CellFormula("SUM(A1:A1)"),
                    CellValue     = new CellValue("999")
                })
            { RowIndex = 2 });
        });

        var result = Parser().Parse(stream);

        Assert.Equal(ExcelParseStatus.Ok, result.Status);
        Assert.Contains("| 999 |", result.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_Markdown_UsesBareLineFeeds_OnEveryHost()
    {
        // The Markdown goes to Claude, so it must not pick up CRLF from AppendLine on the Windows App
        // Service. Every separator the parser writes is exercised: a cell carrying CR LF, the blank
        // line between two sheets, a table, and the truncation note.
        var result = ParseWorkbook(wb =>
        {
            var a = wb.AddWorksheet("Flujo");
            a.Cell("A1").Value = "Concepto";
            a.Cell("A2").Value = "linea uno\r\nlinea dos";

            var b = wb.AddWorksheet("Detalle");
            b.Cell("A1").Value = "Fila";
            for (var r = 2; r <= ExcelCashFlowParser.MaxRowsPerSheet + 1; r++)
                b.Cell(r, 1).Value = r;
        });

        Assert.Equal(ExcelParseStatus.Ok, result.Status);
        Assert.True(result.Truncated);
        Assert.DoesNotContain('\r', result.Markdown);
        Assert.Contains("| linea uno linea dos |", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("\n\n## Detalle\n\n| Fila |\n| --- |\n", result.Markdown, StringComparison.Ordinal);
        Assert.EndsWith("\n\n> " + result.Note + "\n", result.Markdown, StringComparison.Ordinal);
    }

    // ── Caps: always truncate loudly, never silently ──────────────────────────────

    [Fact]
    public void Parse_MoreRowsThanTheCap_TruncatesAndNamesWhatWasCut()
    {
        var result = ParseWorkbook(wb =>
        {
            var ws = wb.AddWorksheet("Hoja");
            ws.Cell("A1").Value = "Concepto";
            ws.Cell("B1").Value = "Monto";
            for (var r = 2; r <= ExcelCashFlowParser.MaxRowsPerSheet + 1; r++)
            {
                ws.Cell(r, 1).Value = $"fila {r}";
                ws.Cell(r, 2).Value = r;
            }
        });

        Assert.Equal(ExcelParseStatus.Ok, result.Status);
        Assert.True(result.Truncated);
        Assert.Equal(ExcelCashFlowParser.MaxRowsPerSheet, result.RowCount);
        Assert.NotNull(result.Note);
        Assert.Contains("fila(s)", result.Note!, StringComparison.Ordinal);
        Assert.Contains("«Hoja»", result.Note!, StringComparison.Ordinal);
        Assert.Contains(result.Note!, result.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_MoreColumnsThanTheCap_TruncatesAndNamesWhatWasCut()
    {
        var result = ParseWorkbook(wb =>
        {
            var ws = wb.AddWorksheet("Hoja");
            for (var c = 1; c <= ExcelCashFlowParser.MaxColumnsPerSheet + 1; c++)
            {
                ws.Cell(1, c).Value = $"col{c}";
                ws.Cell(2, c).Value = c;
            }
        });

        Assert.True(result.Truncated);
        Assert.NotNull(result.Note);
        Assert.Contains("columna(s)", result.Note!, StringComparison.Ordinal);
        Assert.Equal(ExcelCashFlowParser.MaxColumnsPerSheet * 2, result.CellCount);
    }

    [Fact]
    public void Parse_MoreSheetsThanTheCap_TruncatesAndNamesWhatWasCut()
    {
        var result = ParseWorkbook(wb =>
        {
            for (var s = 1; s <= ExcelCashFlowParser.MaxSheets + 1; s++)
            {
                var ws = wb.AddWorksheet($"Hoja{s}");
                ws.Cell("A1").Value = "Concepto";
                ws.Cell("A2").Value = s;
            }
        });

        Assert.True(result.Truncated);
        Assert.Equal(ExcelCashFlowParser.MaxSheets, result.SheetCount);
        Assert.NotNull(result.Note);
        Assert.Contains("hoja(s)", result.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_MoreCellsThanTheTotalCap_StopsAtTheBudget()
    {
        // Two sheets that individually respect the per-sheet caps but together exceed the workbook
        // budget: the first is trimmed to what fits, the second cannot start and is reported.
        const int columns = 50;
        const int rows    = 100;

        var result = ParseWorkbook(wb =>
        {
            foreach (var name in new[] { "Uno", "Dos" })
            {
                var ws = wb.AddWorksheet(name);
                for (var r = 1; r <= rows; r++)
                    for (var c = 1; c <= columns; c++)
                        ws.Cell(r, c).Value = r * c;
            }
        });

        Assert.True(result.Truncated);
        Assert.True(result.CellCount <= ExcelCashFlowParser.MaxTotalCells,
            $"rendered {result.CellCount} cells, budget is {ExcelCashFlowParser.MaxTotalCells}");
        Assert.Equal(1, result.SheetCount);
        Assert.NotNull(result.Note);
        Assert.Contains("hoja(s)", result.Note!, StringComparison.Ordinal);
    }

    // ── Rejections: this is the gate the magic-byte check cannot be ───────────────

    [Fact]
    public void Parse_MacroEnabledWorkbook_IsRejectedByTypeNotExtension()
    {
        // Nothing here is named .xlsm — the package is macro-enabled by its OOXML document type, so
        // renaming the file would not get it past this.
        using var stream = SheetXmlWorkbook(SpreadsheetDocumentType.MacroEnabledWorkbook,
            sheetData => sheetData.Append(NumberRow(1, "A1", "1")));

        var result = Parser().Parse(stream);

        Assert.Equal(ExcelParseStatus.MacroEnabledOrTemplate, result.Status);
        Assert.Equal(string.Empty, result.Markdown);
    }

    [Fact]
    public void Parse_Template_IsRejected()
    {
        using var stream = SheetXmlWorkbook(SpreadsheetDocumentType.Template,
            sheetData => sheetData.Append(NumberRow(1, "A1", "1")));

        var result = Parser().Parse(stream);

        Assert.Equal(ExcelParseStatus.MacroEnabledOrTemplate, result.Status);
    }

    [Fact]
    public void Parse_WordDocument_IsRejected()
    {
        // A .docx passes the ZIP magic-byte check upstream byte for byte. This is where it dies.
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(
                new DocumentFormat.OpenXml.Wordprocessing.Body());
        }
        stream.Position = 0;

        var result = Parser().Parse(stream);

        Assert.Equal(ExcelParseStatus.NotAValidWorkbook, result.Status);
    }

    [Fact]
    public void Parse_PlainZip_IsRejected()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = archive.CreateEntry("hola.txt").Open();
            entry.Write(Encoding.UTF8.GetBytes("no soy una planilla"));
        }
        stream.Position = 0;

        var result = Parser().Parse(stream);

        Assert.Equal(ExcelParseStatus.NotAValidWorkbook, result.Status);
    }

    [Fact]
    public void Parse_Garbage_IsRejectedWithoutThrowing()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("definitivamente no es un zip"));

        var result = Parser().Parse(stream);

        Assert.Equal(ExcelParseStatus.NotAValidWorkbook, result.Status);
    }

    [Fact]
    public void Parse_DecompressionBomb_IsRejectedBeforeTheWorkbookIsMaterialised()
    {
        // A few hundred KB of zeros that expands past the ceiling. The reader loads a workbook whole
        // into memory, so a ratio check on the central directory is the last cheap place to refuse.
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = archive.CreateEntry("xl/worksheets/sheet1.xml").Open();
            var chunk = new byte[1024 * 1024];
            for (var i = 0; i < (ExcelCashFlowParser.MaxUncompressedBytes / chunk.Length) + 2; i++)
                entry.Write(chunk);
        }
        stream.Position = 0;

        var result = Parser().Parse(stream);

        Assert.Equal(ExcelParseStatus.DecompressionBombSuspected, result.Status);
    }

    // ── Fixture helpers ───────────────────────────────────────────────────────────

    private static Row NumberRow(uint rowIndex, string reference, string value) =>
        new(new Cell { CellReference = reference, CellValue = new CellValue(value) })
        { RowIndex = rowIndex };

    /// <summary>
    /// Builds a minimal package with the sheet XML written directly, for the cases ClosedXML cannot
    /// express: a chosen document type, an error cell, or a formula whose cached value is known.
    /// </summary>
    private static MemoryStream SheetXmlWorkbook(
        SpreadsheetDocumentType type, Action<SheetData> build)
    {
        var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, type))
        {
            var workbookPart = doc.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();

            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData();
            build(sheetData);
            worksheetPart.Worksheet = new Worksheet(sheetData);

            workbookPart.Workbook.AppendChild(new Sheets()).Append(new Sheet
            {
                Id      = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name    = "Hoja"
            });
            workbookPart.Workbook.Save();
        }

        ms.Position = 0;
        return ms;
    }
}
