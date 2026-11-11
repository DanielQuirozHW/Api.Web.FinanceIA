// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-09

using Api.Web.FinanceIA.Servicios.Pdf;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios.PdfReport;

/// <summary>
/// The row-break plan of a multi-row table (<see cref="TableRowBreaks"/>): kept whole up to six body rows; longer, never
/// fewer than two body rows on either side of a break, and every chunk fits where it is placed.
/// </summary>
public sealed class TableRowBreaksTests
{
    private const float Heading = 20f, Header = 15f, Row = 10f, Caption = 12f, Page = 200f;

    private static float HeightOf(int start, int end, bool first, bool last) =>
        (first ? Heading : 0f) + Header + Row * (end - start) + (last ? Caption : 0f);

    // The space left on the first page when exactly `rows` body rows (not the last) fit under the sub-heading.
    private static float RoomFor(int rows) => Heading + Header + Row * rows + 5f;

    private static void AssertValid(IReadOnlyList<TableRowBreaks.Chunk> chunks, int rowCount, float firstAvailable)
    {
        Assert.NotEmpty(chunks);
        Assert.Equal(0, chunks[0].Start);
        Assert.Equal(rowCount, chunks[^1].End);

        for (int i = 0; i < chunks.Count; i++)
        {
            TableRowBreaks.Chunk chunk = chunks[i];
            if (i > 0)
            {
                Assert.Equal(chunks[i - 1].End, chunk.Start);
                Assert.True(chunk.BreakBefore, $"chunk {i} continues on the same page.");
            }

            Assert.True(chunk.End - chunk.Start >= TableRowBreaks.MinRowsPerSide,
                $"chunk {i} [{chunk.Start}, {chunk.End}) holds fewer than {TableRowBreaks.MinRowsPerSide} rows.");

            float room = i == 0 && !chunk.BreakBefore ? firstAvailable : Page;
            Assert.True(HeightOf(chunk.Start, chunk.End, chunk.Start == 0, chunk.End == rowCount) <= room,
                $"chunk {i} [{chunk.Start}, {chunk.End}) does not fit where it is placed.");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    public void UpToSixRows_IsOneWholeChunk_WhereverItStarts(int rows)
    {
        foreach (float room in new[] { 0f, RoomFor(1), RoomFor(rows) + Caption, Page })
        {
            IReadOnlyList<TableRowBreaks.Chunk> chunks = TableRowBreaks.Plan(rows, room, Page, HeightOf);
            Assert.Equal([new TableRowBreaks.Chunk(0, rows, false)], chunks);
        }
    }

    [Fact]
    public void NoRows_NoChunks() => Assert.Empty(TableRowBreaks.Plan(0, Page, Page, HeightOf));

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(20)]
    [InlineData(45)]
    public void LongerTables_NeverLeaveFewerThanTwoRows_OnEitherSide(int rows)
    {
        // Every fit on the first page: none, one, two … all but one, and all of it with its caption.
        for (int fit = 0; fit <= rows; fit++)
        {
            float room = fit == rows ? RoomFor(rows) + Caption : RoomFor(fit);
            IReadOnlyList<TableRowBreaks.Chunk> chunks = TableRowBreaks.Plan(rows, room, Page, HeightOf);
            AssertValid(chunks, rows, room);
        }
    }

    [Fact]
    public void OneRowFits_TheTableStartsOnTheNextPage()
    {
        IReadOnlyList<TableRowBreaks.Chunk> chunks = TableRowBreaks.Plan(12, RoomFor(1), Page, HeightOf);
        Assert.Equal([new TableRowBreaks.Chunk(0, 12, true)], chunks);
    }

    [Fact]
    public void AllButOneFit_TwoRowsCarryOver()
    {
        IReadOnlyList<TableRowBreaks.Chunk> chunks = TableRowBreaks.Plan(12, RoomFor(11), Page, HeightOf);
        Assert.Equal([new TableRowBreaks.Chunk(0, 10, false), new TableRowBreaks.Chunk(10, 12, true)], chunks);
    }

    [Fact]
    public void EveryRowFits_ButNotTheCaption_TwoRowsCarryOverWithIt()
    {
        IReadOnlyList<TableRowBreaks.Chunk> chunks = TableRowBreaks.Plan(12, RoomFor(12), Page, HeightOf);
        Assert.Equal([new TableRowBreaks.Chunk(0, 10, false), new TableRowBreaks.Chunk(10, 12, true)], chunks);
    }

    [Fact]
    public void EverythingFits_OneChunk_OnThisPage()
    {
        IReadOnlyList<TableRowBreaks.Chunk> chunks = TableRowBreaks.Plan(12, RoomFor(12) + Caption, Page, HeightOf);
        Assert.Equal([new TableRowBreaks.Chunk(0, 12, false)], chunks);
    }

    [Fact]
    public void APageThatHoldsNoRow_StillEnds_OneRowAPage()
    {
        IReadOnlyList<TableRowBreaks.Chunk> chunks = TableRowBreaks.Plan(8, 0f, 0f, HeightOf);
        Assert.Equal(8, chunks.Count);
        Assert.All(chunks, c => Assert.Equal(1, c.End - c.Start));
    }
}
