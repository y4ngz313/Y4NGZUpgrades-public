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
        private static void RenderTabletBootView()
        {
            SetActiveGroup(_bootGroup);

            float elapsed = Mathf.Max(0f, Time.unscaledTime - _openedAt);
            float progress = Mathf.Clamp01(elapsed / BootSeconds);
            float transition = Mathf.Clamp01((elapsed - BootSeconds) / BootTransitionSeconds);
            if (_bootTitle != null)
            {
                SetTextIfChanged(_bootTitle, "Y4NGZ INDUSTRIES");
                _bootTitle.alpha = Mathf.Lerp(0.78f, 1f, Mathf.PingPong(Time.unscaledTime * 2.4f, 1f));
            }
            if (_bootFill != null)
                _bootFill.anchorMax = new Vector2(Mathf.Lerp(0.30f, 0.70f, progress), 0.45f);
            if (_bootGroup != null && transition > 0f)
                _bootGroup.alpha = Mathf.Lerp(1f, 0.15f, transition);
            if (transition > 0f)
            {
                SetGroup(_scanGroup, true, Mathf.Clamp01(transition * 1.25f));
                RefreshTabletScanContent();
            }
        }

        private static void RenderTabletScanView()
        {
            SetActiveGroup(_scanGroup);
            RefreshTabletScanContent();
        }

        private static void RefreshTabletScanContent()
        {
            bool hasTarget = _currentTarget.IsValid;
            if (_scanStatus != null)
            {
                SetTextIfChanged(_scanStatus, _statusLine);
                SetColorIfChanged(_scanStatus, hasTarget ? ScreenAmber : ScreenGreen);
            }
            if (_scanTarget != null)
            {
                RefreshScanTargetText(hasTarget);
                SetColorIfChanged(_scanTarget, ScreenGreen);
            }
            if (_scanLinkReady != null)
                SetActiveIfChanged(_scanLinkReady.gameObject, hasTarget);
            if (_scanLinkGlyph != null)
                SetActiveIfChanged(_scanLinkGlyph, hasTarget);
            if (_scanCenterDot != null)
            {
                SetActiveIfChanged(_scanCenterDot.gameObject, !hasTarget);
                float blink = 0.6f + 0.4f * Mathf.Sin(Time.unscaledTime * 3.4f);
                _scanCenterDot.color = new Color(ScreenGreen.r, ScreenGreen.g, ScreenGreen.b, blink);
            }

            UpdateTabletScanReticle(hasTarget);
        }

        private static void RenderTabletRadarView(PlayerControllerB player)
        {
            SetActiveGroup(_radarGroup);

            RefreshRadarStatusText();
            RefreshRadarZoomHintText();

            // The focus raycast runs inside the throttle, only when a radar render is due.
            _radarRendered = TryRenderRadarTextureThrottled(player, null, player.isInsideFactory, player.isInHangarShipRoom, updateMarkers: true);

            if (_radarImage != null)
            {
                SetEnabledIfChanged(_radarImage, _radarRendered);
                SetColorIfChanged(_radarImage, _radarRendered ? new Color(0.62f, 1f, 0.66f, 0.9f) : new Color(0f, 0f, 0f, 0f));
            }
        }

        // MAP footer names the bound zoom keys. The binding paths are compared each frame
        // (stored strings, no allocation) and the hint rebuilt only when one changes.
        private static void RefreshRadarZoomHintText()
        {
            if (_radarZoomHint == null)
                return;

            Gui.IngameKeybinds keybinds = Gui.Plugin.Keybinds;
            string zoomInPath = Gui.UpgradeInput.EffectivePath(keybinds?.TabletZoomIn);
            string zoomOutPath = Gui.UpgradeInput.EffectivePath(keybinds?.TabletZoomOut);
            if (_zoomInBindingPath != null
                && string.Equals(zoomInPath, _zoomInBindingPath, StringComparison.Ordinal)
                && string.Equals(zoomOutPath, _zoomOutBindingPath, StringComparison.Ordinal))
                return;

            _zoomInBindingPath = zoomInPath;
            _zoomOutBindingPath = zoomOutPath;
            SetTextIfChanged(_radarZoomHint,
                "[" + Gui.UpgradeInput.DisplayPair(keybinds?.TabletZoomIn, "=", keybinds?.TabletZoomOut, "-") + "] ZOOM");
        }

        private static void RenderTabletDroneView(PlayerControllerB player)
        {
            SetActiveGroup(_droneGroup);

            bool hasDrone = CourierDronePatch.TryGetTabletTelemetry(
                out Vector3 dronePosition,
                out float healthNormalized,
                out float cargoNormalized,
                out int cargoCount,
                out string status,
                out bool droneInside);

            SetSegmentBar(DroneHpSegments, hasDrone ? healthNormalized : 0f, healthNormalized <= 0.3f ? ScreenRed : ScreenGreen);
            SetSegmentBar(DroneCargoSegments, hasDrone ? cargoNormalized : 0f, ScreenGreen);
            if (_droneStatusValue != null)
            {
                bool pilotActive = CourierDronePatch.IsLocalPilotModeActive;
                SetTextIfChanged(_droneStatusValue, pilotActive ? "REMOTE LINK ACTIVE" : hasDrone ? status : "NO LINK");
                SetColorIfChanged(_droneStatusValue, !hasDrone || status == "DESTROYED" ? ScreenRed : ScreenGreen);
            }

            bool feedRendered = false;
            if (hasDrone)
                _radarRendered = TryRenderRadarTextureThrottled(player, dronePosition + Vector3.up * 0.06f, droneInside, inShipRoom: false, updateMarkers: false);
            feedRendered = hasDrone && _radarRendered;

            if (_droneFeedImage != null)
            {
                if (_radarTexture != null && _droneFeedImage.texture != _radarTexture)
                    _droneFeedImage.texture = _radarTexture;
                SetEnabledIfChanged(_droneFeedImage, feedRendered);
                SetColorIfChanged(_droneFeedImage, feedRendered ? new Color(0.55f, 1f, 0.75f, 0.85f) : new Color(0f, 0f, 0f, 0f));
            }
            if (_droneMarker != null)
                SetActiveIfChanged(_droneMarker.gameObject, feedRendered);
            if (_droneFeedFallback != null)
                SetActiveIfChanged(_droneFeedFallback.gameObject, !feedRendered);
        }

        private static void RenderTabletCommandView(PlayerControllerB player)
        {
            SetActiveGroup(_commandGroup);

            // #500: the entries (and the hint and telemetry lines, which also call into
            // CourierDronePatch) refresh at the text interval; selection and input still run every
            // frame on the current entries, and a press re-reads them before acting.
            float time = Time.unscaledTime;
            bool refresh = time >= _nextCommandEntriesRefreshAt;
            if (refresh)
            {
                _nextCommandEntriesRefreshAt = time + TabletDynamicTextRefreshInterval;
                _commandEntryCount = CourierDronePatch.GetTabletCommandEntries(player, CommandEntries);
            }

            int count = _commandEntryCount;
            if (count <= 0)
            {
                _commandSelection = 0;
                _commandWindowStart = 0;
                if (refresh && _commandStatusLine != null)
                    SetTextIfChanged(_commandStatusLine, BuildCommandStatusHint("NO COMMANDS"));
            }
            else
            {
                _commandSelection = Mathf.Clamp(_commandSelection, 0, count - 1);
                // The bindings themselves live on the HUD prompt stack now.
                if (refresh && _commandStatusLine != null)
                    SetTextIfChanged(_commandStatusLine, BuildCommandStatusHint(string.Empty));
            }

            _commandWindowStart = ResolveRowWindowStart(_commandSelection, count);
            RenderPageIndicator(_commandPageText, _commandWindowStart, count);

            for (int slot = 0; slot < CommandLabels.Count; slot++)
            {
                int index = _commandWindowStart + slot;
                bool visible = index < count;
                if (CommandLabels[slot] != null)
                    SetActiveIfChanged(CommandLabels[slot].gameObject, visible);
                if (CommandReasons[slot] != null)
                    SetActiveIfChanged(CommandReasons[slot].gameObject, visible);
                if (CommandSelectors[slot] != null)
                    SetActiveIfChanged(CommandSelectors[slot].gameObject, visible);
                // Selection is colour, band and marker only: the label keeps one size and weight,
                // so its width never changes as the selection moves.
                if (CommandMarkers[slot] != null)
                    SetActiveIfChanged(CommandMarkers[slot].gameObject, visible && index == _commandSelection);
                if (!visible)
                    continue;

                CourierDronePatch.TabletCommandEntry entry = CommandEntries[index];
                bool selected = index == _commandSelection;
                if (CommandLabels[slot] != null)
                {
                    SetTextIfChanged(CommandLabels[slot], entry.Label);
                    SetColorIfChanged(CommandLabels[slot], ResolveRowLabelColor(selected, entry.Available));
                }
                if (CommandReasons[slot] != null)
                {
                    string reason = entry.Available ? string.Empty : entry.Reason ?? string.Empty;
                    if (!string.Equals(LastCommandEntryReasons[slot], reason, StringComparison.Ordinal) || LastCommandEntryAvailable[slot] != entry.Available)
                    {
                        LastCommandEntryReasons[slot] = reason;
                        LastCommandEntryAvailable[slot] = entry.Available;
                        SetTextIfChanged(CommandReasons[slot], reason);
                    }
                    SetColorIfChanged(CommandReasons[slot], ResolveRowDetailColor(selected, entry.Available));
                }
                if (CommandSelectors[slot] != null)
                    SetColorIfChanged(CommandSelectors[slot], ResolveRowSelectorColor(selected));
            }

            if (refresh)
                RenderCommandTelemetry();
        }

        /// <summary>
        /// Resolves which five-row page holds the selection. Discrete pages rather than a sliding
        /// window, so the "PAGE n/m" footer means exactly what it says. Shared by the command and
        /// mainframe lists.
        /// </summary>
        private static int ResolveRowWindowStart(int selection, int count)
        {
            if (count <= FieldTabletLayout.VisibleRows)
                return 0;
            return Mathf.Clamp(selection, 0, count - 1) / FieldTabletLayout.VisibleRows * FieldTabletLayout.VisibleRows;
        }

        private static void RenderPageIndicator(TMP_Text target, int windowStart, int count)
        {
            if (target == null)
                return;

            bool paged = count > FieldTabletLayout.VisibleRows;
            SetActiveIfChanged(target.gameObject, paged);
            if (!paged)
                return;

            int pages = Mathf.Max(1, Mathf.CeilToInt(count / (float)FieldTabletLayout.VisibleRows));
            int page = Mathf.Clamp(windowStart / FieldTabletLayout.VisibleRows + 1, 1, pages);
            int key = (page << 16) | (pages & 0xFFFF);
            if (!PageTexts.TryGetValue(key, out string text))
            {
                text = "PAGE " + page + "/" + pages;
                PageTexts[key] = text;
            }
            SetTextIfChanged(target, text);
        }

        // A selected row is drawn on a green selection band, so its text has to clear 4.5:1 against
        // that band rather than against the panel: dim-on-band measures 2.82:1 and fails, amber
        // measures 5.47:1. Availability is carried by the detail string on a selected row.
        private static Color ResolveRowLabelColor(bool selected, bool enabled)
            => selected ? ScreenAmber : enabled ? ScreenGreen : ScreenDim;

        private static Color ResolveRowDetailColor(bool selected, bool enabled)
            => selected ? ScreenAmber : enabled ? ScreenDim : ScreenRed;

        private static Color ResolveRowSelectorColor(bool selected)
            => selected
                ? new Color(ScreenGreen.r, ScreenGreen.g, ScreenGreen.b, ScreenSelectionAlpha)
                : Color.clear;

        private static string BuildCommandStatusHint(string baseText)
        {
            string text = baseText ?? string.Empty;
            bool hasDrone = CourierDronePatch.TryGetTabletTelemetry(
                out _,
                out _,
                out _,
                out _,
                out _,
                out _);
            InputAction targetAction = Gui.Plugin.Keybinds?.CourierDroneCommand;
            // EffectivePath is a stored string, so the unchanged case allocates nothing.
            string bindingPath = hasDrone ? Gui.UpgradeInput.EffectivePath(targetAction) : null;
            if (_commandHintBase != null
                && string.Equals(_commandHintBase, text, StringComparison.Ordinal)
                && _commandHintHasDrone == hasDrone
                && string.Equals(_commandHintBindingPath, bindingPath, StringComparison.Ordinal))
                return _lastCommandStatusHint;

            _commandHintBase = text;
            _commandHintHasDrone = hasDrone;
            _commandHintBindingPath = bindingPath;
            if (hasDrone)
                text += (text.Length > 0 ? "  " : string.Empty) + "[" + Gui.UpgradeInput.DisplayLabel(targetAction, "MMB") + "] TARGET";

            _lastCommandStatusHint = text;
            return text;
        }

        private static void RenderCommandTelemetry()
        {
            bool hasDrone = CourierDronePatch.TryGetTabletTelemetry(
                out _,
                out float healthNormalized,
                out _,
                out int cargoCount,
                out _,
                out _);
            string status = CourierDronePatch.GetTabletActiveModeName();

            if (_commandTelemetryLine == null)
                return;

            int health = hasDrone ? Mathf.RoundToInt(Mathf.Clamp01(healthNormalized) * 100f) : 0;
            int cargo = hasDrone ? cargoCount : 0;
            status = status ?? string.Empty;
            if (_commandTelemetryStatus != null
                && _commandTelemetryHasDrone == hasDrone
                && _commandTelemetryHealth == health
                && _commandTelemetryCargo == cargo
                && string.Equals(_commandTelemetryStatus, status, StringComparison.Ordinal))
                return;

            _commandTelemetryHasDrone = hasDrone;
            _commandTelemetryHealth = health;
            _commandTelemetryCargo = cargo;
            _commandTelemetryStatus = status;

            // One numeric line instead of two ten-segment bars: at the real footprint a segment is
            // ~18 real pixels with a sub-pixel gap, so the bars read as solid blocks anyway.
            string telemetry = hasDrone
                ? "HP " + health + "%  CGO " + cargo + "  " + status
                : "NO LINK";
            if (string.Equals(_lastCommandTelemetry, telemetry, StringComparison.Ordinal))
                return;

            _lastCommandTelemetry = telemetry;
            SetTextIfChanged(_commandTelemetryLine, telemetry);
            SetColorIfChanged(_commandTelemetryLine, !hasDrone || status == "DESTROYED" ? ScreenRed : ScreenDim);
        }

        private static void SetSegmentBar(List<Image> segments, float normalized, Color litColor)
        {
            int lit = Mathf.RoundToInt(Mathf.Clamp01(normalized) * segments.Count);
            for (int i = 0; i < segments.Count; i++)
            {
                if (segments[i] == null)
                    continue;
                SetColorIfChanged(segments[i], i < lit
                    ? litColor
                    : new Color(litColor.r, litColor.g, litColor.b, 0.14f));
            }
        }

        private static void SetActiveGroup(CanvasGroup active)
        {
            SetGroup(_bootGroup, active == _bootGroup, 1f);
            SetGroup(_scanGroup, active == _scanGroup, 1f);
            SetGroup(_radarGroup, active == _radarGroup, 1f);
            SetGroup(_droneGroup, active == _droneGroup, 1f);
            SetGroup(_commandGroup, active == _commandGroup, 1f);
            SetGroup(_hackGroup, active == _hackGroup, 1f);
            SetGroup(_mainframeGroup, active == _mainframeGroup, 1f);
        }

        private static void RenderTabBar()
        {
            if (_tabBarGroup == null)
                return;

            bool visible = _mode != TabletScreenMode.Boot;
            SetGroup(_tabBarGroup, visible, 1f);
            if (!visible)
                return;

            // The names are fixed at build time; selection is colour, weight and the underline.
            int activeTab = TabIndexForMode(_mode);
            for (int i = 0; i < TabLabels.Count; i++)
            {
                bool selected = i == activeTab;
                TextMeshProUGUI label = TabLabels[i];
                if (label != null)
                {
                    SetColorIfChanged(label, selected ? ScreenAmber : ScreenDim);
                    SetFontStyleIfChanged(label, selected ? FontStyles.Bold : FontStyles.Normal);
                }
                if (i < TabUnderlines.Count && TabUnderlines[i] != null)
                    SetActiveIfChanged(TabUnderlines[i].gameObject, selected);
            }
        }

        private static void RenderTabletMainframeView(PlayerControllerB player)
        {
            SetActiveGroup(_mainframeGroup);

            BuildMainframeRows(player);
            int count = MainframeRows.Count;
            _mainframeSelection = count > 0 ? Mathf.Clamp(_mainframeSelection, 0, count - 1) : 0;

            _mainframeWindowStart = ResolveRowWindowStart(_mainframeSelection, count);
            // The uplink bar owns the footer band while a channel is running, so the two never
            // share it.
            if (_mainframePageText != null && _uplinkChannelActive)
                SetActiveIfChanged(_mainframePageText.gameObject, false);
            else
                RenderPageIndicator(_mainframePageText, _mainframeWindowStart, count);

            if (_mainframeStatusText != null)
            {
                SetTextIfChanged(_mainframeStatusText, _mainframeStatus);
                SetColorIfChanged(_mainframeStatusText, _uplinkEstablished ? ScreenGreen : ScreenAmber);
            }

            for (int slot = 0; slot < MainframeLabels.Count; slot++)
            {
                int index = _mainframeWindowStart + slot;
                bool rowVisible = index < count;
                if (MainframeLabels[slot] != null)
                    SetActiveIfChanged(MainframeLabels[slot].gameObject, rowVisible);
                if (MainframeDetails[slot] != null)
                    SetActiveIfChanged(MainframeDetails[slot].gameObject, rowVisible);
                if (MainframeSelectors[slot] != null)
                    SetActiveIfChanged(MainframeSelectors[slot].gameObject, rowVisible);
                if (MainframeMarkers[slot] != null)
                    SetActiveIfChanged(MainframeMarkers[slot].gameObject, rowVisible && index == _mainframeSelection);
                if (!rowVisible)
                    continue;

                MainframeRow row = MainframeRows[index];
                bool selected = index == _mainframeSelection;

                if (MainframeLabels[slot] != null)
                {
                    SetTextIfChanged(MainframeLabels[slot], row.Label);
                    SetColorIfChanged(MainframeLabels[slot], ResolveRowLabelColor(selected, row.Enabled));
                }
                if (MainframeDetails[slot] != null)
                {
                    SetTextIfChanged(MainframeDetails[slot], row.Detail ?? string.Empty);
                    SetColorIfChanged(MainframeDetails[slot], ResolveRowDetailColor(selected, row.Enabled));
                }
                if (MainframeSelectors[slot] != null)
                    SetColorIfChanged(MainframeSelectors[slot], ResolveRowSelectorColor(selected));
            }

            if (_uplinkProgressRoot != null)
                SetActiveIfChanged(_uplinkProgressRoot, _uplinkChannelActive);
            if (_uplinkChannelActive && _uplinkProgressFill != null)
                _uplinkProgressFill.anchorMax = new Vector2(Mathf.Lerp(0.16f, 0.72f, Mathf.Clamp01(_uplinkProgress)), 0.075f);
        }
    }
}
