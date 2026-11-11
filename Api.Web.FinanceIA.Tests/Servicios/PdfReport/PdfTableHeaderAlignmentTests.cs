// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-09

using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios.PdfReport;

/// <summary>
/// A table header aligns like the cells under it: over right-aligned figures it is right-aligned on the same edge,
/// over centred status labels it is centred. Measured on the rendered page, header row against a body row.
/// </summary>
public sealed class PdfTableHeaderAlignmentTests
{
    private const float Tolerance = 0.5f;

    private sealed record Chunk(int Page, string Text, float X0, float X1, float Y)
    {
        public float Centre => (X0 + X1) / 2f;
    }

    private sealed class Collector(int page, List<Chunk> sink) : IEventListener
    {
        public void EventOccurred(IEventData data, EventType type)
        {
            if (data is not TextRenderInfo info || string.IsNullOrWhiteSpace(info.GetText()))
                return;

            LineSegment baseline = info.GetBaseline();
            sink.Add(new Chunk(page, info.GetText(), baseline.GetStartPoint().Get(0), baseline.GetEndPoint().Get(0), baseline.GetStartPoint().Get(1)));
        }

        public ICollection<EventType> GetSupportedEvents() => new HashSet<EventType> { EventType.RENDER_TEXT };
    }

    private static async Task<List<Chunk>> RenderChunksAsync(params string[] sections)
    {
        byte[] pdf = await PdfExportServiceRenderTests.NewService().GenerateAsync(
            PdfSyntheticReport.Result(), PdfSyntheticReport.Analysis(), "tenant-synthetic", sections,
            CancellationToken.None, PdfSyntheticReport.Report());

        var raw = new List<Chunk>();
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        for (int i = 1; i <= doc.GetNumberOfPages(); i++)
            new PdfCanvasProcessor(new Collector(i, raw)).ProcessPageContent(doc.GetPage(i));

        // One chunk per word run: pieces of one show-text operation that touch on the same baseline are joined.
        var merged = new List<Chunk>();
        foreach (Chunk chunk in raw.OrderBy(c => c.Page).ThenByDescending(c => Math.Round(c.Y, 1)).ThenBy(c => c.X0))
        {
            if (merged.Count > 0 && merged[^1] is var last && last.Page == chunk.Page
                && Math.Abs(last.Y - chunk.Y) < 0.1f && chunk.X0 - last.X1 is > -0.5f and < 1f)
            {
                merged[^1] = last with { Text = last.Text + chunk.Text, X1 = chunk.X1 };
                continue;
            }

            merged.Add(chunk);
        }

        return merged.Select(c => c with { Text = c.Text.Trim() }).ToList();
    }

    // The cells of the row that holds `first` (page 2 on: page 1 is the fact sheet), left to right. Cells of one row
    // sit within a few points of each other (a smaller status font has its own baseline); rows are ~20 pt apart.
    private static List<Chunk> Row(List<Chunk> chunks, string first)
    {
        Chunk anchor = chunks.First(c => c.Page > 1 && c.Text == first);
        return chunks
            .Where(c => c.Page == anchor.Page && Math.Abs(c.Y - anchor.Y) < 4f && c.X0 >= anchor.X0)
            .OrderBy(c => c.X0)
            .ToList();
    }

    private static void RightEdgesMatch(List<Chunk> header, List<Chunk> row, int column)
    {
        Assert.True(Math.Abs(header[column].X1 - row[column].X1) <= Tolerance,
            $"'{header[column].Text}' ends at {header[column].X1:0.00}, '{row[column].Text}' at {row[column].X1:0.00}.");
    }

    private static void CentresMatch(List<Chunk> header, List<Chunk> row, int column)
    {
        Assert.True(Math.Abs(header[column].Centre - row[column].Centre) <= Tolerance,
            $"'{header[column].Text}' centred at {header[column].Centre:0.00}, '{row[column].Text}' at {row[column].Centre:0.00}.");
    }

    [Theory]
    [InlineData("Liquidez corriente")]
    [InlineData("Endeudamiento")]
    [InlineData("ROE")]
    public async Task RatiosTable_ValorAndPuntajeAlignRight_EstadoCentres(string ratio)
    {
        List<Chunk> chunks = await RenderChunksAsync("ratios", "ratio_breakdown");

        List<Chunk> header = Row(chunks, "Indicador");
        List<Chunk> row    = Row(chunks, ratio);
        Assert.Equal(["Indicador", "Valor", "Puntaje", "Estado"], header.Select(c => c.Text));
        Assert.Equal(4, row.Count);

        RightEdgesMatch(header, row, 1);
        RightEdgesMatch(header, row, 2);
        CentresMatch(header, row, 3);
    }

    [Fact]
    public async Task ComponentTable_PuntajeAndPesoAlignRight_EstadoCentres()
    {
        List<Chunk> chunks = await RenderChunksAsync("risk");

        List<Chunk> header = Row(chunks, "Componente");
        List<Chunk> row    = Row(chunks, "Ratios financieros");
        Assert.Equal(["Componente", "Puntaje", "Peso", "Estado"], header.Select(c => c.Text));
        Assert.Equal(4, row.Count);

        RightEdgesMatch(header, row, 1);
        RightEdgesMatch(header, row, 2);
        CentresMatch(header, row, 3);
    }

    [Fact]
    public async Task TwoColumnTable_TheFigureHeaderAlignsRight()
    {
        List<Chunk> chunks = await RenderChunksAsync("cash_flow");

        List<Chunk> header = Row(chunks, "Movimiento");
        List<Chunk> row    = Row(chunks, "Efectivo al inicio");
        Assert.Equal(["Movimiento", "Importe (ARS)"], header.Select(c => c.Text));
        Assert.Equal(2, row.Count);

        RightEdgesMatch(header, row, 1);
    }
}
