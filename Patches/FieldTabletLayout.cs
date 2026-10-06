using System;

namespace Y4NGZUpgrades.Patches
{
    internal enum FieldTabletTextAlign
    {
        Left,
        Center,
        Right
    }

    /// <summary>
    /// The tablet screens a layout check walks. The tab bar is part of every one of them.
    /// </summary>
    internal enum FieldTabletView
    {
        Boot,
        Scan,
        Hack,
        Map,
        Drone,
        DroneCommands,
        Mainframe
    }

    /// <summary>
    /// One text box on the tablet canvas. The anchor is a normalized point; the box hangs off it
    /// the way CreateText builds it: a Left box grows right from the anchor (pivot x 0), a Right
    /// box grows left (pivot x 1), a Center box straddles it (pivot x 0.5), and every box is
    /// vertically centred on the anchor (pivot y 0.5). Width, height and font size are canvas units.
    /// An Overflow box holds fixed strings that already fit and is never cut; every other box is
    /// cut at its width (Ellipsis, or Truncate when the font has no ellipsis glyph).
    /// </summary>
    internal readonly struct FieldTabletTextBox
    {
        internal FieldTabletTextBox(string name, float anchorX, float anchorY, float width, float height, float fontSize, FieldTabletTextAlign align, bool bold = false, bool overflow = false)
        {
            Name = name;
            AnchorX = anchorX;
            AnchorY = anchorY;
            Width = width;
            Height = height;
            FontSize = fontSize;
            Align = align;
            Bold = bold;
            Overflow = overflow;
        }

        internal string Name { get; }
        internal float AnchorX { get; }
        internal float AnchorY { get; }
        internal float Width { get; }
        internal float Height { get; }
        internal float FontSize { get; }
        internal FieldTabletTextAlign Align { get; }
        internal bool Bold { get; }
        internal bool Overflow { get; }

        internal float PivotX => Align == FieldTabletTextAlign.Right ? 1f : Align == FieldTabletTextAlign.Center ? 0.5f : 0f;
        internal float Left => AnchorX * FieldTabletLayout.ScreenWidth - PivotX * Width;
        internal float Right => Left + Width;
        internal float Bottom => AnchorY * FieldTabletLayout.ScreenHeight - Height * 0.5f;
        internal float Top => Bottom + Height;

        internal FieldTabletTextBox AtRow(string name, int slot)
            => new FieldTabletTextBox(name, AnchorX, FieldTabletLayout.RowY(slot), Width, Height, FontSize, Align, Bold, Overflow);
    }

    /// <summary>
    /// Every text box on the Field Tablet screen, as data. FieldTabletScreenRuntime builds its
    /// TextMeshPro elements from these entries, and verification/FieldTabletChecks proves from the
    /// same entries that no two boxes on one screen intersect. Unity-free on purpose.
    /// </summary>
    internal static class FieldTabletLayout
    {
        // Canvas resolution matches the physical screen region aspect, which is 0.21328m x
        // 0.14803m = 1.4408:1 measured off the prop mesh - not the 16:10 this file assumed. These
        // are world-space canvas UNITS, not render-texture pixels: every UI number on the tablet is
        // expressed in them, and they stay fixed while the RT resizes underneath. Width and height
        // must stay in the aperture's ratio or the render texture is stretched onto the quad; at
        // 1844 x 1280 a canvas unit is square in both axes (8646 units/m).
        internal const int ScreenWidth = 1844;
        internal const int ScreenHeight = 1280;

        // Typography budget. fontSize is the em size in canvas units, and for the all-caps strings
        // this UI uses the visible glyph height is the cap height, ~0.70 x fontSize. The screen
        // occupies ~219x164 real pixels at Lethal Company's default 860x520 gameplay render
        // resolution, so 7.8 canvas units = 1 real pixel in both axes. The legibility floor is 8
        // real pixels of cap height: fontSize = (target_real_px x 7.8) / 0.70. Nothing on the
        // tablet screen goes below 80 - if a string does not fit at 80, cut the string. The
        // aperture correction narrowed the canvas from 2048 to 1844 units without touching these
        // sizes, so every string now costs 11% more of the width than it did.
        internal const float TitleFontSize = 132f;    // 12.0 real px of cap height
        internal const float TabFontSize = 112f;      // 10.2
        internal const float RowFontSize = 104f;      //  9.4
        internal const float ValueFontSize = 88f;     //  8.0
        internal const float HintFontSize = 80f;      //  7.3, hard floor
        internal const float ReadoutFontSize = 140f;  // 12.7, single big readouts
        internal const float MinFontSize = HintFontSize;

        // Width budget per character. The vanilla HUD font (IBM 3270, monospaced) advances 0.54 em
        // per glyph; 0.55 keeps a little slack.
        internal const float AdvanceEm = 0.55f;
        // TextMeshPro's Ellipsis and Truncate modes also cut vertically: a box shorter than the
        // font's ascender-to-descender line blanks its first line. The 3270 line height is not
        // measured offline and most fonts run 1.1-1.3 em, so every box that can be cut is at least
        // 1.4 em tall. Overflow boxes are never cut and are exempt.
        internal const float MinCutBoxHeightEm = 1.4f;

        // The suffix a cut string ends in. The 3270 SDF atlas is not guaranteed to carry U+2026,
        // so FieldTabletScreenRuntime probes the resolved font once and sets this; until then, and
        // when the glyph is missing, it is the ASCII "..." (TMP Truncate mode instead of Ellipsis).
        internal const string EllipsisGlyph = "\u2026";
        internal const string AsciiEllipsis = "...";
        internal static string EllipsisSuffix { get; private set; } = AsciiEllipsis;

        internal static void SetEllipsisGlyphSupported(bool supported)
            => EllipsisSuffix = supported ? EllipsisGlyph : AsciiEllipsis;

        // Density budget. 1280 units tall, less a ~150-unit header band and a ~130-unit footer
        // band, leaves 1000 usable; at fontSize 104 a row needs a 140-unit pitch, or 164 with a
        // selection band you can actually see. That is five content rows per screen.
        internal const int VisibleRows = 5;
        internal const float RowPitch = 0.128f;       // 164 canvas units
        internal const float RowHalfBand = 0.062f;
        internal const float FirstRowY = 0.70f;
        internal const float StatusY = 0.815f;
        internal const float FooterY = 0.05f;
        // The selection band, and the span every full-width line has to stay inside.
        internal const float BandMinX = 0.045f;
        internal const float BandMaxX = 0.955f;
        // Any line meant to be seen needs 2 real pixels, and 1 real pixel is 7.7 canvas units.
        internal const float DividerThickness = 0.0125f;  // 16 canvas units
        internal const float HeaderDividerY = 0.895f;

        // Row columns: a one-glyph selection marker, the label, and a right-aligned status. The
        // selected row keeps the row font and weight, so nothing reflows when the selection moves.
        internal const float RowMarkerX = 0.05f;
        internal const float RowLabelX = 0.10f;
        internal const float RowDetailX = 0.935f;
        internal const string RowMarkerText = ">";

        // Four tabs share the band in equal, disjoint slots. The selected one is marked by colour,
        // weight and an underline sitting just above the header divider, never by a prefix. A slot
        // holds six characters at fontSize 112, so MAINFRAME is abbreviated rather than shrunk.
        internal static readonly string[] TabNames = { "SCAN", "MAP", "DRONE", "MFRM" };
        internal const float TabY = 0.9545f;
        internal const float TabSlotWidth = (BandMaxX - BandMinX) * ScreenWidth / 4f;  // 419.51
        internal const float TabUnderlineMinY = 0.9095f;  // 16 units, 2.6 above the divider
        internal const float TabUnderlineMaxY = 0.922f;
        internal const float TabUnderlineWidthFraction = 0.70f;

        // The drone camera feed; its fallback text is centred in it.
        internal const float DroneFeedMinX = 0.045f;
        internal const float DroneFeedMaxX = 0.60f;
        internal const float DroneFeedMinY = 0.30f;
        internal const float DroneFeedMaxY = 0.86f;

        private const float FullLineWidth = 1678f;

        internal static float RowY(int slot) => FirstRowY - slot * RowPitch;

        internal static float TabAnchorX(int index) => BandMinX + (BandMaxX - BandMinX) * (index + 0.5f) / TabNames.Length;

        internal static float TabUnderlineMinX(int index) => TabAnchorX(index) - (BandMaxX - BandMinX) * TabUnderlineWidthFraction * 0.5f / TabNames.Length;

        internal static float TabUnderlineMaxX(int index) => TabAnchorX(index) + (BandMaxX - BandMinX) * TabUnderlineWidthFraction * 0.5f / TabNames.Length;

        internal static readonly FieldTabletTextBox[] Tabs = BuildTabs();

        internal static readonly FieldTabletTextBox BootTitle = new FieldTabletTextBox("BootTitle", 0.5f, 0.58f, 1700f, 185f, TitleFontSize, FieldTabletTextAlign.Center);

        internal static readonly FieldTabletTextBox ScanStatus = new FieldTabletTextBox("ScanStatus", BandMinX, StatusY, FullLineWidth, 146f, RowFontSize, FieldTabletTextAlign.Left);
        // Raised from 0.30 when the boxes grew to 1.4 em, so it clears SPLICE READY below it.
        internal static readonly FieldTabletTextBox ScanTarget = new FieldTabletTextBox("ScanTarget", 0.5f, 0.31f, FullLineWidth, 146f, RowFontSize, FieldTabletTextAlign.Center);
        internal static readonly FieldTabletTextBox ScanLinkReady = new FieldTabletTextBox("ScanLinkReady", 0.5f, 0.195f, FullLineWidth, 146f, RowFontSize, FieldTabletTextAlign.Center);

        internal static readonly FieldTabletTextBox RadarStatus = new FieldTabletTextBox("RadarStatus", BandMaxX, 0.845f, 810f, 124f, ValueFontSize, FieldTabletTextAlign.Right);
        internal static readonly FieldTabletTextBox RadarZoomHint = new FieldTabletTextBox("RadarZoomHint", 0.5f, FooterY, 1080f, 112f, HintFontSize, FieldTabletTextAlign.Center);

        internal static readonly FieldTabletTextBox DroneFeedFallback = new FieldTabletTextBox("DroneFeedFallback", (DroneFeedMinX + DroneFeedMaxX) * 0.5f, (DroneFeedMinY + DroneFeedMaxY) * 0.5f, 900f, 146f, RowFontSize, FieldTabletTextAlign.Center);
        internal static readonly FieldTabletTextBox DroneHpLabel = new FieldTabletTextBox("DroneHpLabel", 0.66f, 0.82f, 540f, 124f, ValueFontSize, FieldTabletTextAlign.Left);
        internal static readonly FieldTabletTextBox DroneCargoLabel = new FieldTabletTextBox("DroneCargoLabel", 0.66f, 0.64f, 540f, 124f, ValueFontSize, FieldTabletTextAlign.Left);
        internal static readonly FieldTabletTextBox DroneStatusLabel = new FieldTabletTextBox("DroneStatusLabel", BandMinX, 0.185f, 300f, 124f, ValueFontSize, FieldTabletTextAlign.Left);
        internal static readonly FieldTabletTextBox DroneStatusValue = new FieldTabletTextBox("DroneStatusValue", 0.22f, 0.185f, 1355f, 124f, ValueFontSize, FieldTabletTextAlign.Left);

        // Row column templates; AtRow places them on a slot.
        internal static readonly FieldTabletTextBox RowMarker = new FieldTabletTextBox("RowMarker", RowMarkerX, FirstRowY, 80f, 146f, RowFontSize, FieldTabletTextAlign.Left);
        internal static readonly FieldTabletTextBox RowLabel = new FieldTabletTextBox("RowLabel", RowLabelX, FirstRowY, 920f, 146f, RowFontSize, FieldTabletTextAlign.Left);
        internal static readonly FieldTabletTextBox RowDetail = new FieldTabletTextBox("RowDetail", RowDetailX, FirstRowY, 600f, 124f, ValueFontSize, FieldTabletTextAlign.Right);

        internal static readonly FieldTabletTextBox CommandTelemetry = new FieldTabletTextBox("CommandTelemetry", BandMinX, StatusY, FullLineWidth, 124f, ValueFontSize, FieldTabletTextAlign.Left);
        internal static readonly FieldTabletTextBox[] CommandMarkers = BuildRows(RowMarker, "CommandMarker");
        internal static readonly FieldTabletTextBox[] CommandLabels = BuildRows(RowLabel, "CommandLabel");
        internal static readonly FieldTabletTextBox[] CommandReasons = BuildRows(RowDetail, "CommandReason");
        internal static readonly FieldTabletTextBox CommandHint = new FieldTabletTextBox("CommandHint", BandMinX, FooterY, 1261f, 112f, HintFontSize, FieldTabletTextAlign.Left);
        internal static readonly FieldTabletTextBox CommandPage = new FieldTabletTextBox("CommandPage", BandMaxX, FooterY, 378f, 112f, HintFontSize, FieldTabletTextAlign.Right);

        internal static readonly FieldTabletTextBox HackStatus = new FieldTabletTextBox("HackStatus", BandMinX, StatusY, FullLineWidth, 124f, ValueFontSize, FieldTabletTextAlign.Left);
        internal static readonly FieldTabletTextBox HackTarget = new FieldTabletTextBox("HackTarget", 0.5f, 0.60f, FullLineWidth, 146f, RowFontSize, FieldTabletTextAlign.Center);
        internal static readonly FieldTabletTextBox HackPercent = new FieldTabletTextBox("HackPercent", 0.5f, 0.27f, FullLineWidth, 196f, ReadoutFontSize, FieldTabletTextAlign.Center, bold: true);
        internal static readonly FieldTabletTextBox HackHint = new FieldTabletTextBox("HackHint", 0.5f, 0.105f, 1170f, 112f, HintFontSize, FieldTabletTextAlign.Center);

        internal static readonly FieldTabletTextBox MainframeStatus = new FieldTabletTextBox("MainframeStatus", BandMinX, StatusY, FullLineWidth, 124f, ValueFontSize, FieldTabletTextAlign.Left);
        internal static readonly FieldTabletTextBox[] MainframeMarkers = BuildRows(RowMarker, "MainframeMarker");
        internal static readonly FieldTabletTextBox[] MainframeLabels = BuildRows(RowLabel, "MainframeLabel");
        internal static readonly FieldTabletTextBox[] MainframeDetails = BuildRows(RowDetail, "MainframeDetail");
        internal static readonly FieldTabletTextBox MainframePage = new FieldTabletTextBox("MainframePage", BandMaxX, FooterY, 378f, 112f, HintFontSize, FieldTabletTextAlign.Right);

        // Dev aperture-calibration overlay: drawn over every screen on its own panel, so it is
        // held to the bounds and font floor but belongs to no view. Its three 1.4 em boxes are
        // taller than their 102-unit pitch; the 0.6 em caps they centre stay well apart.
        internal static readonly FieldTabletTextBox CalTitle = new FieldTabletTextBox("CalTitle", 0.5f, 0.265f, 1200f, 124f, ValueFontSize, FieldTabletTextAlign.Center, bold: true);
        internal static readonly FieldTabletTextBox CalOffset = new FieldTabletTextBox("CalOffset", 0.5f, 0.185f, 1200f, 124f, ValueFontSize, FieldTabletTextAlign.Center);
        internal static readonly FieldTabletTextBox CalSize = new FieldTabletTextBox("CalSize", 0.5f, 0.105f, 1200f, 124f, ValueFontSize, FieldTabletTextAlign.Center);
        internal static readonly FieldTabletTextBox[] CalibrationBoxes = { CalTitle, CalOffset, CalSize };

        // Composite strings whose suffix must survive reserve these characters for it.
        internal const string ShutdownSentPrefix = "SHUTDOWN SENT ";
        internal const int ScanTargetSuffixChars = 7;  // "  00.0M"; scan range keeps it under 100 m

        private static readonly FieldTabletTextBox[][] ViewBoxes = BuildViews();

        /// <summary>The text boxes one screen shows, tab bar included.</summary>
        internal static FieldTabletTextBox[] BoxesFor(FieldTabletView view) => ViewBoxes[(int)view];

        /// <summary>Characters of the budgeted advance that fit the box's width.</summary>
        internal static int MaxChars(FieldTabletTextBox box)
            => (int)Math.Floor(box.Width / (AdvanceEm * box.FontSize));

        /// <summary>
        /// The value unchanged when it fits, otherwise cut to maxChars characters that end in the
        /// current <see cref="EllipsisSuffix"/>. Returns the same instance when nothing is cut, so
        /// callers can cache it.
        /// </summary>
        internal static string FitChars(string value, int maxChars) => FitChars(value, maxChars, EllipsisSuffix);

        /// <summary>
        /// The value unchanged when it fits, otherwise its first maxChars - suffix.Length characters
        /// and the suffix; the suffix counts against maxChars, and is itself cut when maxChars is
        /// no longer than it.
        /// </summary>
        internal static string FitChars(string value, int maxChars, string suffix)
        {
            if (string.IsNullOrEmpty(value) || maxChars <= 0)
                return string.Empty;
            if (value.Length <= maxChars)
                return value;
            int keep = maxChars - suffix.Length;
            return keep > 0 ? value.Substring(0, keep) + suffix : suffix.Substring(0, maxChars);
        }

        private static FieldTabletTextBox[] BuildTabs()
        {
            FieldTabletTextBox[] tabs = new FieldTabletTextBox[TabNames.Length];
            for (int i = 0; i < tabs.Length; i++)
                tabs[i] = new FieldTabletTextBox("Tab_" + TabNames[i], TabAnchorX(i), TabY, TabSlotWidth, 116f, TabFontSize, FieldTabletTextAlign.Center, overflow: true);
            return tabs;
        }

        private static FieldTabletTextBox[] BuildRows(FieldTabletTextBox column, string name)
        {
            FieldTabletTextBox[] rows = new FieldTabletTextBox[VisibleRows];
            for (int i = 0; i < rows.Length; i++)
                rows[i] = column.AtRow(name + i, i);
            return rows;
        }

        private static FieldTabletTextBox[][] BuildViews()
        {
            FieldTabletTextBox[][] views = new FieldTabletTextBox[Enum.GetValues(typeof(FieldTabletView)).Length][];
            views[(int)FieldTabletView.Boot] = WithTabs(BootTitle);
            views[(int)FieldTabletView.Scan] = WithTabs(ScanStatus, ScanTarget, ScanLinkReady);
            views[(int)FieldTabletView.Hack] = WithTabs(HackStatus, HackTarget, HackPercent, HackHint);
            views[(int)FieldTabletView.Map] = WithTabs(RadarStatus, RadarZoomHint);
            views[(int)FieldTabletView.Drone] = WithTabs(DroneFeedFallback, DroneHpLabel, DroneCargoLabel, DroneStatusLabel, DroneStatusValue);
            views[(int)FieldTabletView.DroneCommands] = WithTabs(Concat(new[] { CommandTelemetry, CommandHint, CommandPage }, CommandMarkers, CommandLabels, CommandReasons));
            views[(int)FieldTabletView.Mainframe] = WithTabs(Concat(new[] { MainframeStatus, MainframePage }, MainframeMarkers, MainframeLabels, MainframeDetails));
            return views;
        }

        private static FieldTabletTextBox[] WithTabs(params FieldTabletTextBox[] boxes) => Concat(Tabs, boxes);

        private static FieldTabletTextBox[] Concat(params FieldTabletTextBox[][] parts)
        {
            int length = 0;
            for (int i = 0; i < parts.Length; i++)
                length += parts[i].Length;
            FieldTabletTextBox[] result = new FieldTabletTextBox[length];
            int offset = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                Array.Copy(parts[i], 0, result, offset, parts[i].Length);
                offset += parts[i].Length;
            }
            return result;
        }
    }
}
