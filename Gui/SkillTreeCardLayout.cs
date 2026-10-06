using System;

namespace Y4NGZUpgrades.Gui;

// Shared by every class: optional/hidden nodes never change another row's pitch.
// A tier is one logical band carrying one caption, and its nodes wrap onto as
// many physical card rows as the actual content width holds. A catalog that
// registers more nodes in a tier than a row fits therefore draws every one of
// them instead of overflowing the frame, and one-, two- and three-card tiers
// keep the geometry they always had.
internal static class SkillTreeCardLayout
{
    internal const float NodeWidth = 144f;
    internal const float NodeHeight = 92f;
    internal const float SidePadding = 22f;
    internal const float ColumnGap = 32f;
    internal const float CaptionHeight = 18f;
    internal const float Clearance = 8f;
    internal const float CaptionInset = CaptionHeight + Clearance;
    // A tier holding a single physical row of cards - caption, card, clearance.
    internal const float RowHeight = CaptionInset + NodeHeight + Clearance;
    // Free band between two wrapped rows of one tier: the clearance a tier
    // reserves below its cards and above the next ones, back to back, so a
    // wrapped row keeps the same breathing room as the next tier's cards.
    internal const float StackGap = Clearance * 2f;
    // CardWidth divides the usable width into thirds, so three columns fit at
    // every width the menu is drawn at. Further columns are earned by measured
    // width, never assumed.
    internal const int MinColumns = 3;

    internal static float CardWidth(float contentWidth)
        => Math.Min(NodeWidth, Math.Max(1f, (contentWidth - SidePadding * 2f - 24f) / 3f));

    internal static float Gap(float contentWidth, float cardWidth)
        => Math.Max(0f, Math.Min(ColumnGap, (contentWidth - SidePadding * 2f - cardWidth * 3f) / 2f));

    internal static float ColumnLeft(float contentWidth, float cardWidth, int count, int column)
    {
        float gap = Gap(contentWidth, cardWidth);
        float rowWidth = count * cardWidth + Math.Max(0, count - 1) * gap;
        return (contentWidth - rowWidth) * 0.5f + column * (cardWidth + gap);
    }

    /// <summary>
    /// Cards one physical row may hold at this content width, at the same pitch
    /// every row uses. Three always fit; a wider content rect earns more.
    /// </summary>
    internal static int MaxColumns(float contentWidth)
    {
        float card = CardWidth(contentWidth);
        float usable = contentWidth - SidePadding * 2f;
        int columns = (int)Math.Floor((usable + ColumnGap) / (card + ColumnGap));
        return Math.Max(MinColumns, columns);
    }

    /// <summary>Physical card rows one logical tier needs for its nodes.</summary>
    internal static int PhysicalRowCount(int nodeCount, int columns)
    {
        if (nodeCount <= 0)
            return 0;

        int max = Math.Max(1, columns);
        return (nodeCount + max - 1) / max;
    }

    /// <summary>
    /// Cards on one physical row. Row sizes differ by at most one, so a wrapped
    /// tier never ends on a lone card hanging under a full row.
    /// </summary>
    internal static int RowSize(int nodeCount, int columns, int row)
    {
        int rows = PhysicalRowCount(nodeCount, columns);
        if (row < 0 || row >= rows)
            return 0;

        int even = nodeCount / rows;
        int wide = nodeCount % rows;
        return row < wide ? even + 1 : even;
    }

    /// <summary>Top of a wrapped row, measured from the tier's first row.</summary>
    internal static float RowTop(float tierTop, int row)
        => tierTop + Math.Max(0, row) * (NodeHeight + StackGap);

    /// <summary>Cards plus the gaps between them, without caption or clearance.</summary>
    internal static float TierCardsHeight(int rows)
        => rows <= 1 ? NodeHeight : rows * NodeHeight + (rows - 1) * StackGap;

    /// <summary>Pitch from one tier's caption to the next. RowHeight when a tier does not wrap.</summary>
    internal static float TierHeight(int rows)
        => CaptionInset + TierCardsHeight(rows) + Clearance;

    /// <summary>
    /// Where one node's card lands inside its tier. The single placement rule:
    /// the menu builds cards from it, and the layout checks measure it.
    /// </summary>
    internal static CardSlot Slot(float contentWidth, int nodeCount, float tierTop, int index)
    {
        int columns = MaxColumns(contentWidth);
        int rows = PhysicalRowCount(nodeCount, columns);
        if (rows <= 0 || index < 0 || index >= nodeCount)
            return new CardSlot(0, 0, rows, 0, SidePadding, tierTop);

        int even = nodeCount / rows;
        int wide = nodeCount % rows;
        int wideCards = wide * (even + 1);
        int row;
        int column;
        if (index < wideCards)
        {
            row = index / (even + 1);
            column = index % (even + 1);
        }
        else
        {
            int rest = index - wideCards;
            row = wide + rest / even;
            column = rest % even;
        }

        int inRow = RowSize(nodeCount, columns, row);
        return new CardSlot(row, column, rows, inRow,
            ColumnLeft(contentWidth, CardWidth(contentWidth), inRow, column),
            RowTop(tierTop, row));
    }

    /// <summary>A node's physical row inside its tier, its column on that row, and the card's top-left.</summary>
    internal readonly struct CardSlot
    {
        internal CardSlot(int row, int column, int rowCount, int columnsInRow, float left, float top)
        {
            Row = row;
            Column = column;
            RowCount = rowCount;
            ColumnsInRow = columnsInRow;
            Left = left;
            Top = top;
        }

        internal int Row { get; }
        internal int Column { get; }
        internal int RowCount { get; }
        internal int ColumnsInRow { get; }
        internal float Left { get; }
        internal float Top { get; }
    }

    // Split at a word boundary using the active TMP font's actual measurements.
    // Every name keeps the same font size and at most two deliberate lines.
    internal static string FormatName(string name, float width, Func<string, float> measure)
    {
        string text = (name ?? "").ToUpperInvariant();
        if (measure(text) <= width)
            return text;

        string best = text;
        float bestWidth = float.MaxValue;
        for (int i = 1; i < text.Length - 1; i++)
        {
            if (text[i] != ' ') continue;
            string first = text.Substring(0, i).TrimEnd();
            string second = text.Substring(i + 1).TrimStart();
            float widest = Math.Max(measure(first), measure(second));
            if (widest >= bestWidth) continue;
            bestWidth = widest;
            best = first + "\n" + second;
        }
        return best;
    }
}
