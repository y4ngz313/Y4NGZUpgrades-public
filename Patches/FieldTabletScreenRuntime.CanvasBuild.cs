using System;
using System.Collections.Generic;
using System.Reflection;
using GameNetcodeStuff;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.UI;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Patches
{
    internal static partial class FieldTabletScreenRuntime
    {
        private static void BuildBootUi(Transform parent)
        {
            _bootTitle = CreateText(parent, FieldTabletLayout.BootTitle, "Y4NGZ INDUSTRIES", ScreenGreen);
            _bootTitle.characterSpacing = 8f;
            Image back = CreateImage("BootBarBack", parent, new Vector2(0.30f, 0.405f), new Vector2(0.70f, 0.45f), Vector2.zero, Vector2.zero, new Color(ScreenGreen.r, ScreenGreen.g, ScreenGreen.b, 0.22f));
            Image fill = CreateImage("BootBarFill", parent, new Vector2(0.30f, 0.405f), new Vector2(0.30f, 0.45f), Vector2.zero, Vector2.zero, ScreenGreen);
            _bootFill = fill.rectTransform;
            CreateImage("BootBaseline", parent, new Vector2(0.30f, 0.353f), new Vector2(0.70f, 0.353f + FieldTabletLayout.DividerThickness), Vector2.zero, Vector2.zero, new Color(ScreenGreen.r, ScreenGreen.g, ScreenGreen.b, 0.35f));
            back.raycastTarget = false;
        }

        private static void BuildScanUi(Transform parent)
        {
            _scanStatus = CreateText(parent, FieldTabletLayout.ScanStatus, "SCAN READY", ScreenGreen);

            GameObject reticle = new GameObject("ScanReticle");
            reticle.transform.SetParent(parent, false);
            _scanReticleRoot = reticle.AddComponent<RectTransform>();
            _scanReticleRoot.anchorMin = new Vector2(0.5f, 0.55f);
            _scanReticleRoot.anchorMax = new Vector2(0.5f, 0.55f);
            _scanReticleRoot.pivot = new Vector2(0.5f, 0.5f);
            _scanReticleRoot.sizeDelta = new Vector2(220f, 220f);
            _scanPulse = CreateImage("ScanPulse", _scanReticleRoot, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-116f, -116f), new Vector2(116f, 116f), new Color(ScreenGreen.r, ScreenGreen.g, ScreenGreen.b, 0.10f));
            BuildSquareReticle(_scanReticleRoot, ScanReticleLines, 16f, 92f, ScreenDim);
            _scanCenterDot = CreateImage("ScanCenterDot", _scanReticleRoot, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-20f, -20f), new Vector2(20f, 20f), ScreenGreen);

            _scanTarget = CreateText(parent, FieldTabletLayout.ScanTarget, string.Empty, ScreenGreen);
            // The splice binding itself lives on the HUD prompt stack now, so the screen only has
            // to say that a target is locked.
            _scanLinkReady = CreateText(parent, FieldTabletLayout.ScanLinkReady, "SPLICE READY", ScreenAmber);
            _scanLinkReady.characterSpacing = 4f;
            _scanLinkGlyph = BuildLinkGlyph(parent, new Vector2(0.5f, 0.095f));
            _scanLinkReady.gameObject.SetActive(false);
            _scanLinkGlyph.SetActive(false);
        }

        private static void BuildRadarUi(Transform parent)
        {
            _radarStatus = CreateText(parent, FieldTabletLayout.RadarStatus, "ZOOM 1.0X", ScreenDim);
            _radarZoomHint = CreateText(parent, FieldTabletLayout.RadarZoomHint, string.Empty, ScreenDim);
            _zoomInBindingPath = null;
            GameObject imageGo = new GameObject("InteriorMapTexture");
            imageGo.transform.SetParent(parent, false);
            RectTransform imageRect = imageGo.AddComponent<RectTransform>();
            imageRect.anchorMin = new Vector2(0.045f, 0.125f);
            imageRect.anchorMax = new Vector2(0.955f, 0.795f);
            imageRect.offsetMin = Vector2.zero;
            imageRect.offsetMax = Vector2.zero;
            _radarImage = imageGo.AddComponent<RawImage>();
            _radarImage.raycastTarget = false;

            GameObject markerGo = new GameObject("RadarMarkers");
            markerGo.transform.SetParent(parent, false);
            _radarMarkerLayer = markerGo.AddComponent<RectTransform>();
            _radarMarkerLayer.anchorMin = imageRect.anchorMin;
            _radarMarkerLayer.anchorMax = imageRect.anchorMax;
            _radarMarkerLayer.offsetMin = Vector2.zero;
            _radarMarkerLayer.offsetMax = Vector2.zero;
        }

        private static void BuildDroneUi(Transform parent)
        {
            GameObject feedGo = new GameObject("DroneFeed");
            feedGo.transform.SetParent(parent, false);
            RectTransform feedRect = feedGo.AddComponent<RectTransform>();
            feedRect.anchorMin = new Vector2(FieldTabletLayout.DroneFeedMinX, FieldTabletLayout.DroneFeedMinY);
            feedRect.anchorMax = new Vector2(FieldTabletLayout.DroneFeedMaxX, FieldTabletLayout.DroneFeedMaxY);
            feedRect.offsetMin = Vector2.zero;
            feedRect.offsetMax = Vector2.zero;
            _droneFeedImage = feedGo.AddComponent<RawImage>();
            _droneFeedImage.raycastTarget = false;
            _droneMarker = CreateImage("DroneMarker", feedRect, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-16f, -16f), new Vector2(16f, 16f), ScreenCyan);
            _droneMarker.rectTransform.localEulerAngles = new Vector3(0f, 0f, 45f);
            // Centred on the feed, but placed in canvas space so its box is in the layout table.
            _droneFeedFallback = CreateText(parent, FieldTabletLayout.DroneFeedFallback, "NO LINK", ScreenDim);

            CreateImage("DroneColumnDivider", parent, new Vector2(0.615f, 0.30f), new Vector2(0.615f + FieldTabletLayout.DividerThickness, 0.86f), Vector2.zero, Vector2.zero, new Color(ScreenGreen.r, ScreenGreen.g, ScreenGreen.b, 0.55f));

            CreateText(parent, FieldTabletLayout.DroneHpLabel, "HP", ScreenDim);
            BuildSegmentRow(parent, "DroneHpSegments", 0.66f, 0.955f, 0.70f, 0.775f, DroneTelemetrySegments, DroneHpSegments);
            CreateText(parent, FieldTabletLayout.DroneCargoLabel, "CARGO", ScreenDim);
            BuildSegmentRow(parent, "DroneCargoSegments", 0.66f, 0.955f, 0.52f, 0.595f, DroneTelemetrySegments, DroneCargoSegments);
            CreateText(parent, FieldTabletLayout.DroneStatusLabel, "STATUS", ScreenDim);
            _droneStatusValue = CreateText(parent, FieldTabletLayout.DroneStatusValue, "OK", ScreenGreen);
        }

        private static void BuildCommandUi(Transform parent)
        {
            // The numeric telemetry line holds the longest mode name ("HP 100%  CGO 10  RETURNING TO
            // SHIP", 34 chars) only at full width, so it takes the status line; the target prompt
            // drops to the footer beside the page indicator.
            _commandTelemetryLine = CreateText(parent, FieldTabletLayout.CommandTelemetry, string.Empty, ScreenDim);
            _commandStatusLine = CreateText(parent, FieldTabletLayout.CommandHint, string.Empty, ScreenDim);

            for (int i = 0; i < FieldTabletLayout.VisibleRows; i++)
            {
                float y = FieldTabletLayout.RowY(i);
                Image selector = CreateImage("CommandSelector" + i, parent, new Vector2(FieldTabletLayout.BandMinX, y - FieldTabletLayout.RowHalfBand), new Vector2(FieldTabletLayout.BandMaxX, y + FieldTabletLayout.RowHalfBand), Vector2.zero, Vector2.zero, Color.clear);
                TextMeshProUGUI marker = CreateText(parent, FieldTabletLayout.CommandMarkers[i], FieldTabletLayout.RowMarkerText, ScreenAmber);
                TextMeshProUGUI label = CreateText(parent, FieldTabletLayout.CommandLabels[i], "COMMAND", ScreenGreen);
                TextMeshProUGUI reason = CreateText(parent, FieldTabletLayout.CommandReasons[i], string.Empty, ScreenRed);
                marker.gameObject.SetActive(false);
                selector.gameObject.SetActive(false);
                label.gameObject.SetActive(false);
                reason.gameObject.SetActive(false);
                CommandMarkers.Add(marker);
                CommandSelectors.Add(selector);
                CommandLabels.Add(label);
                CommandReasons.Add(reason);
            }

            _commandPageText = CreateText(parent, FieldTabletLayout.CommandPage, string.Empty, ScreenDim);
        }

        private static void BuildSegmentRow(Transform parent, string name, float anchorMinX, float anchorMaxX, float anchorMinY, float anchorMaxY, int count, List<Image> output)
        {
            output.Clear();
            GameObject rowGo = new GameObject(name);
            rowGo.transform.SetParent(parent, false);
            RectTransform row = rowGo.AddComponent<RectTransform>();
            row.anchorMin = new Vector2(anchorMinX, anchorMinY);
            row.anchorMax = new Vector2(anchorMaxX, anchorMaxY);
            row.offsetMin = Vector2.zero;
            row.offsetMax = Vector2.zero;
            for (int i = 0; i < count; i++)
            {
                Image segment = CreateImage("Segment" + i, row,
                    new Vector2(i / (float)count, 0f),
                    new Vector2((i + 1) / (float)count, 1f),
                    new Vector2(8f, 0f), new Vector2(-8f, 0f),
                    ScreenGreen);
                output.Add(segment);
            }
        }

        private static GameObject BuildLinkGlyph(Transform parent, Vector2 anchor)
        {
            GameObject glyphGo = new GameObject("LinkGlyph");
            glyphGo.transform.SetParent(parent, false);
            RectTransform glyph = glyphGo.AddComponent<RectTransform>();
            glyph.anchorMin = anchor;
            glyph.anchorMax = anchor;
            glyph.pivot = new Vector2(0.5f, 0.5f);
            glyph.sizeDelta = new Vector2(140f, 52f);
            // Two chain loops with a connecting bar.
            BuildBoxOutline(glyph, "LoopL", new Vector2(0f, 0f), new Vector2(0.42f, 1f), 10f, ScreenAmber);
            BuildBoxOutline(glyph, "LoopR", new Vector2(0.58f, 0f), new Vector2(1f, 1f), 10f, ScreenAmber);
            CreateImage("LinkBar", glyph, new Vector2(0.34f, 0.4f), new Vector2(0.66f, 0.6f), Vector2.zero, Vector2.zero, ScreenAmber);
            return glyphGo;
        }

        private static void BuildBoxOutline(RectTransform parent, string name, Vector2 anchorMin, Vector2 anchorMax, float thickness, Color color)
        {
            GameObject boxGo = new GameObject(name);
            boxGo.transform.SetParent(parent, false);
            RectTransform box = boxGo.AddComponent<RectTransform>();
            box.anchorMin = anchorMin;
            box.anchorMax = anchorMax;
            box.offsetMin = Vector2.zero;
            box.offsetMax = Vector2.zero;
            CreateImage("Top", box, new Vector2(0f, 1f), Vector2.one, new Vector2(0f, -thickness), Vector2.zero, color);
            CreateImage("Bottom", box, Vector2.zero, new Vector2(1f, 0f), Vector2.zero, new Vector2(0f, thickness), color);
            CreateImage("Left", box, Vector2.zero, new Vector2(0f, 1f), Vector2.zero, new Vector2(thickness, 0f), color);
            CreateImage("Right", box, new Vector2(1f, 0f), Vector2.one, new Vector2(-thickness, 0f), Vector2.zero, color);
        }

        private static void BuildHackUi(Transform parent)
        {
            _hackStatusText = CreateText(parent, FieldTabletLayout.HackStatus, "SPLICING", ScreenAmber);

            _hackTargetText = CreateText(parent, FieldTabletLayout.HackTarget, "FIELD TARGET", ScreenGreen);
            _hackTargetText.characterSpacing = 3f;

            Image progressBack = CreateImage("HackBarBack", parent, new Vector2(0.16f, 0.42f), new Vector2(0.84f, 0.47f), Vector2.zero, Vector2.zero, new Color(ScreenGreen.r, ScreenGreen.g, ScreenGreen.b, 0.22f));
            BuildBoxOutline(progressBack.rectTransform, "HackBarFrame", new Vector2(-0.01f, -0.35f), new Vector2(1.01f, 1.35f), 16f, new Color(ScreenGreen.r, ScreenGreen.g, ScreenGreen.b, 0.55f));
            Image progressFill = CreateImage("HackBarFill", parent, new Vector2(0.16f, 0.42f), new Vector2(0.16f, 0.47f), Vector2.zero, Vector2.zero, ScreenAmber);
            _hackProgressFill = progressFill.rectTransform;
            progressBack.raycastTarget = false;

            _hackPercentText = CreateText(parent, FieldTabletLayout.HackPercent, "00%", ScreenAmber);
            _hackHintText = CreateText(parent, FieldTabletLayout.HackHint, "[" + Gui.UpgradeInput.DisplayLabel(Gui.Plugin.Keybinds?.TabletBack, "BKSP") + "] ABORT", ScreenDim);
        }

        private static void BuildMainframeUi(Transform parent)
        {
            _mainframeStatusText = CreateText(parent, FieldTabletLayout.MainframeStatus, "MAINFRAME", ScreenAmber);

            for (int i = 0; i < MainframeVisibleRows; i++)
            {
                float y = FieldTabletLayout.RowY(i);
                Image selector = CreateImage("MainframeSelector" + i, parent, new Vector2(FieldTabletLayout.BandMinX, y - FieldTabletLayout.RowHalfBand), new Vector2(FieldTabletLayout.BandMaxX, y + FieldTabletLayout.RowHalfBand), Vector2.zero, Vector2.zero, Color.clear);
                TextMeshProUGUI marker = CreateText(parent, FieldTabletLayout.MainframeMarkers[i], FieldTabletLayout.RowMarkerText, ScreenAmber);
                TextMeshProUGUI label = CreateText(parent, FieldTabletLayout.MainframeLabels[i], string.Empty, ScreenGreen);
                TextMeshProUGUI detail = CreateText(parent, FieldTabletLayout.MainframeDetails[i], string.Empty, ScreenDim);
                marker.gameObject.SetActive(false);
                selector.gameObject.SetActive(false);
                label.gameObject.SetActive(false);
                detail.gameObject.SetActive(false);
                MainframeMarkers.Add(marker);
                MainframeSelectors.Add(selector);
                MainframeLabels.Add(label);
                MainframeDetails.Add(detail);
            }

            _uplinkProgressRoot = new GameObject("UplinkProgress", typeof(RectTransform));
            _uplinkProgressRoot.transform.SetParent(parent, false);
            RectTransform progressRect = _uplinkProgressRoot.GetComponent<RectTransform>();
            progressRect.anchorMin = Vector2.zero;
            progressRect.anchorMax = Vector2.one;
            progressRect.offsetMin = Vector2.zero;
            progressRect.offsetMax = Vector2.zero;
            Image uplinkBack = CreateImage("UplinkBarBack", progressRect, new Vector2(0.16f, 0.035f), new Vector2(0.72f, 0.075f), Vector2.zero, Vector2.zero, new Color(ScreenGreen.r, ScreenGreen.g, ScreenGreen.b, 0.22f));
            BuildBoxOutline(uplinkBack.rectTransform, "UplinkBarFrame", new Vector2(-0.01f, -0.35f), new Vector2(1.01f, 1.35f), 16f, new Color(ScreenGreen.r, ScreenGreen.g, ScreenGreen.b, 0.55f));
            Image uplinkFill = CreateImage("UplinkBarFill", progressRect, new Vector2(0.16f, 0.035f), new Vector2(0.16f, 0.075f), Vector2.zero, Vector2.zero, ScreenAmber);
            _uplinkProgressFill = uplinkFill.rectTransform;
            _uplinkProgressRoot.SetActive(false);

            _mainframePageText = CreateText(parent, FieldTabletLayout.MainframePage, string.Empty, ScreenDim);
        }

        private static void BuildTabBar(Transform parent)
        {
            TabLabels.Clear();
            TabUnderlines.Clear();
            CreateImage("TabBarDivider", parent, new Vector2(0.03f, FieldTabletLayout.HeaderDividerY), new Vector2(0.97f, FieldTabletLayout.HeaderDividerY + FieldTabletLayout.DividerThickness), Vector2.zero, Vector2.zero, new Color(ScreenGreen.r, ScreenGreen.g, ScreenGreen.b, 0.55f));
            for (int i = 0; i < FieldTabletLayout.Tabs.Length; i++)
            {
                TabLabels.Add(CreateText(parent, FieldTabletLayout.Tabs[i], FieldTabletLayout.TabNames[i], ScreenDim));
                Image underline = CreateImage("TabUnderline_" + FieldTabletLayout.TabNames[i], parent,
                    new Vector2(FieldTabletLayout.TabUnderlineMinX(i), FieldTabletLayout.TabUnderlineMinY),
                    new Vector2(FieldTabletLayout.TabUnderlineMaxX(i), FieldTabletLayout.TabUnderlineMaxY),
                    Vector2.zero, Vector2.zero, ScreenAmber);
                underline.gameObject.SetActive(false);
                TabUnderlines.Add(underline);
            }
        }

        private static void BuildCrtOverlay(RectTransform root)
        {
            // Twenty-nine scanlines at 1.8 canvas units is 0.23 real pixels each: haze, not an
            // effect. Eight lines at 8 units survive the minification and still read as a CRT.
            for (int i = 1; i < 9; i++)
            {
                float y = i / 9f;
                CreateImage("Scanline" + i, root, new Vector2(0f, y), new Vector2(1f, y), new Vector2(0f, -4f), new Vector2(0f, 4f), new Color(0f, 0f, 0f, 0.22f));
            }
            _rollingScanline = CreateImage("RollingScanline", root, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(0f, -8f), new Vector2(0f, 8f), new Color(ScreenGreen.r, ScreenGreen.g, ScreenGreen.b, 0.20f));
        }

        /// <summary>
        /// Dev calibration pattern for the screen aperture (workstream A). Draws the render
        /// texture's own outer edge, so "flush" is a thing you can see: the amber band has to meet
        /// the bezel the whole way round with no baked albedo screen graphic showing beside it.
        /// The corner brackets make a clipped edge obvious even where the band blends into the
        /// bezel, and the readout shows the live rect so it can be nudged without alt-tabbing.
        /// </summary>
        private static void BuildCalibrationOverlay(RectTransform root)
        {
            _calibrationRoot = new GameObject("Calibration", typeof(RectTransform));
            _calibrationRoot.transform.SetParent(root, false);
            RectTransform rect = _calibrationRoot.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            const float border = 16f;
            CreateImage("CalEdgeTop", rect, new Vector2(0f, 1f), Vector2.one, new Vector2(0f, -border), Vector2.zero, ScreenAmber);
            CreateImage("CalEdgeBottom", rect, Vector2.zero, new Vector2(1f, 0f), Vector2.zero, new Vector2(0f, border), ScreenAmber);
            CreateImage("CalEdgeLeft", rect, Vector2.zero, new Vector2(0f, 1f), Vector2.zero, new Vector2(border, 0f), ScreenAmber);
            CreateImage("CalEdgeRight", rect, new Vector2(1f, 0f), Vector2.one, new Vector2(-border, 0f), Vector2.zero, ScreenAmber);

            BuildCalibrationCorner(rect, "CalCornerBL", new Vector2(0f, 0f), 1f, 1f);
            BuildCalibrationCorner(rect, "CalCornerBR", new Vector2(1f, 0f), -1f, 1f);
            BuildCalibrationCorner(rect, "CalCornerTL", new Vector2(0f, 1f), 1f, -1f);
            BuildCalibrationCorner(rect, "CalCornerTR", new Vector2(1f, 1f), -1f, -1f);

            const float crossArm = 220f;
            const float crossThick = 8f;
            CreateImage("CalCrossH", rect, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(-crossArm, -crossThick), new Vector2(crossArm, crossThick), ScreenCyan);
            CreateImage("CalCrossV", rect, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(-crossThick, -crossArm), new Vector2(crossThick, crossArm), ScreenCyan);

            // Kept clear of the centre crosshair's lower arm, which reaches y 0.33.
            CreateImage("CalReadoutPanel", rect, new Vector2(0.16f, 0.06f), new Vector2(0.84f, 0.31f),
                Vector2.zero, Vector2.zero, new Color(ScreenPanel.r, ScreenPanel.g, ScreenPanel.b, 0.92f));
            CreateText(rect, FieldTabletLayout.CalTitle, "APERTURE CAL", ScreenAmber);
            _calibrationOffsetReadout = CreateText(rect, FieldTabletLayout.CalOffset, string.Empty, ScreenGreen);
            _calibrationSizeReadout = CreateText(rect, FieldTabletLayout.CalSize, string.Empty, ScreenGreen);
            RefreshCalibrationReadout(ResolveScreenLocalOffset(), ResolveScreenLocalSize());

            _calibrationRoot.SetActive(false);
        }

        private static void BuildCalibrationCorner(RectTransform parent, string name, Vector2 corner, float signX, float signY)
        {
            const float arm = 260f;
            const float thick = 28f;
            const float inset = 34f;
            float nearX = inset * signX;
            float farX = (inset + arm) * signX;
            float nearY = inset * signY;
            float farY = (inset + arm) * signY;
            float thickX = (inset + thick) * signX;
            float thickY = (inset + thick) * signY;

            CreateImage(name + "H", parent, corner, corner,
                new Vector2(Mathf.Min(nearX, farX), Mathf.Min(nearY, thickY)),
                new Vector2(Mathf.Max(nearX, farX), Mathf.Max(nearY, thickY)), ScreenGreen);
            CreateImage(name + "V", parent, corner, corner,
                new Vector2(Mathf.Min(nearX, thickX), Mathf.Min(nearY, farY)),
                new Vector2(Mathf.Max(nearX, thickX), Mathf.Max(nearY, farY)), ScreenGreen);
        }

        private static CanvasGroup CreateGroup(string name, Transform parent)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            CanvasGroup group = go.AddComponent<CanvasGroup>();
            group.blocksRaycasts = false;
            group.interactable = false;
            return group;
        }

        /// <summary>
        /// A tablet-screen text element placed by its <see cref="FieldTabletLayout"/> box. Wrapping
        /// stays off. An Overflow box holds fixed strings that already fit and is never cut; every
        /// other box ellipsizes (or truncates, when the font has no U+2026), so a string that
        /// outgrows its column is cut inside it instead of running into its neighbour. Those boxes
        /// are at least <see cref="FieldTabletLayout.MinCutBoxHeightEm"/> tall, so the vertical half
        /// of the cut never blanks the line.
        /// </summary>
        private static TextMeshProUGUI CreateText(Transform parent, FieldTabletTextBox box, string text, Color color)
        {
            TextAlignmentOptions alignment = box.Align == FieldTabletTextAlign.Right ? TextAlignmentOptions.Right
                : box.Align == FieldTabletTextAlign.Center ? TextAlignmentOptions.Center
                : TextAlignmentOptions.Left;
            // The inner CreateText resolves the font, which sets _ellipsisGlyphSupported.
            TextMeshProUGUI tmp = CreateText(box.Name, parent, text, box.FontSize, box.Bold ? FontStyles.Bold : FontStyles.Normal,
                alignment, new Vector2(box.AnchorX, box.AnchorY), new Vector2(box.Width, box.Height), color);
            tmp.overflowMode = box.Overflow ? TextOverflowModes.Overflow
                : _ellipsisGlyphSupported ? TextOverflowModes.Ellipsis
                : TextOverflowModes.Truncate;
            return tmp;
        }

        private static TextMeshProUGUI CreateText(string name, Transform parent, string text, float size, FontStyles style, TextAlignmentOptions alignment, Vector2 anchor, Vector2 boxSize, Color color)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = new Vector2(alignment == TextAlignmentOptions.Right ? 1f : alignment == TextAlignmentOptions.Center ? 0.5f : 0f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = boxSize;
            TextMeshProUGUI tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = size;
            tmp.fontStyle = style;
            tmp.alignment = alignment;
            tmp.color = color;
            tmp.raycastTarget = false;
            tmp.enableWordWrapping = false;
            TMP_FontAsset font = ResolveFont();
            if (font != null)
                tmp.font = font;
            return tmp;
        }

        /// <summary>
        /// Text on the player's world-space crosshair canvas, NOT on the tablet screen. That canvas
        /// is a fixed 58-pixel square floating 0.8 m in front of the camera, so it is sized in that
        /// canvas's own units and is deliberately exempt from the tablet screen's typography budget.
        /// </summary>
        private static TextMeshProUGUI CreateWorldReticleText(string name, Transform parent, float size, Vector2 boxSize)
            => CreateText(name, parent, string.Empty, size, FontStyles.Bold, TextAlignmentOptions.Center, new Vector2(0.5f, 0f), boxSize, ScreenAmber);

        private static Image CreateImage(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax, Color color)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
            Image img = go.AddComponent<Image>();
            img.sprite = GetPixelSprite();
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        private static void BuildSquareReticle(RectTransform root, List<Image> output, float thickness, float length, Color color)
        {
            output.Clear();
            output.Add(CreateReticleLine(root, "TopLeftH", new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, -thickness), new Vector2(length, 0f), color));
            output.Add(CreateReticleLine(root, "TopLeftV", new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, -length), new Vector2(thickness, 0f), color));
            output.Add(CreateReticleLine(root, "TopRightH", new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-length, -thickness), new Vector2(0f, 0f), color));
            output.Add(CreateReticleLine(root, "TopRightV", new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-thickness, -length), new Vector2(0f, 0f), color));
            output.Add(CreateReticleLine(root, "BottomLeftH", new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(length, thickness), color));
            output.Add(CreateReticleLine(root, "BottomLeftV", new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(thickness, length), color));
            output.Add(CreateReticleLine(root, "BottomRightH", new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-length, 0f), new Vector2(0f, thickness), color));
            output.Add(CreateReticleLine(root, "BottomRightV", new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-thickness, 0f), new Vector2(0f, length), color));
        }

        private static Image CreateReticleLine(RectTransform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax, Color color)
        {
            return CreateImage(name, parent, anchorMin, anchorMax, offsetMin, offsetMax, color);
        }

        private static void EnsurePlayerCrosshair(Camera camera)
        {
            if (_playerReticleRoot != null)
                return;
            if (camera == null)
                return;

            _playerReticleRoot = new GameObject("Y4NGZ_FieldTablet_PlayerWorldReticle");
            UnityEngine.Object.DontDestroyOnLoad(_playerReticleRoot);
            RectTransform rt = _playerReticleRoot.AddComponent<RectTransform>();
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(WorldReticleBasePixels, WorldReticleBasePixels);
            Canvas canvas = _playerReticleRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = camera;
            canvas.sortingOrder = 0;
            CanvasGroup group = _playerReticleRoot.AddComponent<CanvasGroup>();
            group.alpha = 0f;
            group.blocksRaycasts = false;
            group.interactable = false;
            _playerReticleCanvas = canvas;
            _playerReticleGroup = group;
            _playerReticleRect = rt;
            _playerReticleCamera = camera;
            _playerReticleAppliedAlpha = 0f;
            _playerReticleAppliedSize = WorldReticleBasePixels;
            _playerReticleAppliedScale = float.NaN;
            SetLayerRecursively(_playerReticleRoot, 0);
            BuildSquareReticle(rt, PlayerReticleLines, 2.2f, 13f, ScreenAmber);
            _worldReticleLabel = CreateWorldReticleText("WorldReticleLabel", rt, WorldReticleLabelFontSize, new Vector2(150f, 18f));
            _worldReticleLabel.rectTransform.anchoredPosition = new Vector2(0f, -18f);
            SetActiveIfChanged(_worldReticleLabel.gameObject, false);
        }

        private static void SetLayerRecursively(GameObject root, int layer)
        {
            if (root == null)
                return;

            root.layer = layer;
            for (int i = 0; i < root.transform.childCount; i++)
                SetLayerRecursively(root.transform.GetChild(i).gameObject, layer);
        }
    }
}
