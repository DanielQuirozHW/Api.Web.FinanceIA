// Project : Api.Web.FinanceIA
// Created : 2026-11-09

namespace Api.Web.FinanceIA.Servicios.Pdf;

/// <summary>
/// Where a multi-row table may break across pages. iText has no widow/orphan control for table rows, so the
/// report measures first and places the breaks itself: a table of <see cref="KeepWholeMaxRows"/> body rows or
/// fewer is never split, and a longer one never leaves fewer than <see cref="MinRowsPerSide"/> body rows on
/// either side of a break. Pure arithmetic over measured heights — the renderer supplies them.
/// </summary>
internal static class TableRowBreaks
{
    /// <summary>The fewest body rows a page may carry of a split table, before or after the break.</summary>
    internal const int MinRowsPerSide = 2;

    /// <summary>A table with this many body rows or fewer is kept whole.</summary>
    internal const int KeepWholeMaxRows = 6;

    // Layout rounding: a block measured to fit exactly must not be the one that overflows.
    private const float Slack = 0.5f;

    /// <summary>Body rows [<paramref name="Start"/>, <paramref name="End"/>), on a new page when <paramref name="BreakBefore"/>.</summary>
    internal readonly record struct Chunk(int Start, int End, bool BreakBefore);

    /// <summary>
    /// The chunks of a table of <paramref name="rowCount"/> body rows. <paramref name="heightOf"/>(start, end,
    /// first, last) is the height of the block that prints rows [start, end) — under the table's sub-heading when
    /// <c>first</c>, above its caption when <c>last</c>, with the header row either way.
    /// <paramref name="firstAvailable"/> is what is left of the current page, <paramref name="pageAvailable"/> a
    /// whole page.
    /// </summary>
    internal static IReadOnlyList<Chunk> Plan(
        int rowCount, float firstAvailable, float pageAvailable, Func<int, int, bool, bool, float> heightOf)
    {
        ArgumentNullException.ThrowIfNull(heightOf);
        if (rowCount <= 0) return [];
        if (rowCount <= KeepWholeMaxRows) return [new Chunk(0, rowCount, false)];

        var   chunks      = new List<Chunk>();
        int   start       = 0;
        float available   = firstAvailable;
        bool  freshPage   = false;
        bool  breakBefore = false;

        while (start < rowCount)
        {
            bool first = start == 0;

            // As many rows as fit, the caption counted with the last one.
            int end = start;
            while (end < rowCount && heightOf(start, end + 1, first, end + 1 == rowCount) <= available - Slack)
                end++;

            if (end == rowCount)
            {
                chunks.Add(new Chunk(start, rowCount, breakBefore));
                break;
            }

            // Too few fit here: start on the next page instead.
            if (end - start < MinRowsPerSide && !freshPage)
            {
                (available, freshPage, breakBefore) = (pageAvailable, true, true);
                continue;
            }

            // Too few would be left over: carry MinRowsPerSide over with them.
            if (rowCount - end < MinRowsPerSide)
                end = rowCount - MinRowsPerSide;

            if (end - start < MinRowsPerSide)
            {
                if (!freshPage)
                {
                    (available, freshPage, breakBefore) = (pageAvailable, true, true);
                    continue;
                }

                // A whole page that cannot hold two rows (never for this report's rows): at least one row per
                // page, so the plan always ends.
                end = Math.Min(rowCount, start + Math.Max(end - start, 1));
            }

            chunks.Add(new Chunk(start, end, breakBefore));
            start = end;
            (available, freshPage, breakBefore) = (pageAvailable, true, true);
        }

        return chunks;
    }
}
