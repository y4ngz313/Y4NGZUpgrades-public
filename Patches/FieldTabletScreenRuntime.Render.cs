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
        private static void RenderActiveScreen(PlayerControllerB player)
        {
            if (_screenCanvasRoot == null)
                return;

            float time = Time.unscaledTime;
            UpdateScanlines(time);
            RenderTabBar();

            if (_mode == TabletScreenMode.Boot)
                RenderTabletBootView();
            else if (_mode == TabletScreenMode.Radar)
                RenderTabletRadarView(player);
            else if (_mode == TabletScreenMode.Drone)
                RenderTabletDroneView(player);
            else if (_mode == TabletScreenMode.Command)
                RenderTabletCommandView(player);
            else if (_mode == TabletScreenMode.Hack)
                RenderTabletHackView();
            else if (_mode == TabletScreenMode.Mainframe)
                RenderTabletMainframeView(player);
            else
                RenderTabletScanView();

            RenderScreenTexture();
        }

        private static void RenderScreenTexture()
        {
            if (_screenCamera == null || _screenRenderTexture == null || _screenCanvasRoot == null)
                return;
            float time = Time.unscaledTime;
            if (!ShouldRenderScreenTexture(time))
                return;

            long startedAt = FieldTabletPerfMeter.Timestamp();
            try
            {
                // F-TABLET-11: the global Canvas.ForceUpdateCanvases() used to run here at 30 Hz,
                // rebuilding layout for every canvas in the game - the vanilla HUD included - to
                // flush this one off-screen canvas. Scoped to the tablet's own rect instead, and
                // (#500) only when a write since the last render could have moved layout; the
                // canvas is re-rendered by the manual Render() below either way, since the
                // scanline, pulse and blink animate every frame.
                if (_screenLayoutDirty)
                {
                    _screenLayoutDirty = false;
                    RectTransform canvasRect = _screenCanvasRoot.transform as RectTransform;
                    if (canvasRect != null)
                        LayoutRebuilder.ForceRebuildLayoutImmediate(canvasRect);
                }
                RenderTexture previousActive = RenderTexture.active;
                RenderTexture previousTarget = _screenCamera.targetTexture;
                _screenCamera.targetTexture = _screenRenderTexture;
                _screenCamera.Render();
                RenderTexture.active = previousActive;
                _screenCamera.targetTexture = previousTarget;
                _screenRendersSinceRebind++;
                // #500: the very first render after an open runs before the canvas has ever been
                // built, so probing it reported black on a healthy screen. Probe the third render
                // after an open, RT rebuild or rig build, or the second once 0.2 s have passed
                // since the open; either way the canvas has been built and rendered before.
                if (!_screenRenderDiagnosticsLogged
                    && (_screenRendersSinceRebind >= ScreenProbeRenderIndex
                        || (_screenRendersSinceRebind >= 2 && time - _openedAt >= ScreenProbeMinSecondsAfterOpen)))
                {
                    _screenRenderDiagnosticsLogged = true;
                    float maxLuma = ProbeRenderTextureMaxLuma(_screenRenderTexture);
                    Plugin.Log?.LogInfo($"[Field Tablet] screen runtime v5 rendered rt={_screenRenderTexture.width}x{_screenRenderTexture.height} canvasActive={_screenCanvasRoot.activeInHierarchy} cameraLayer={ScreenRenderLayer} cameraEnabled={_screenCamera.enabled} rtMaxLuma={maxLuma:0.000}");
                    if (maxLuma <= 0.01f)
                        Plugin.Log?.LogWarning("[Field Tablet] screen RT probe came back black: the off-screen canvas is not reaching the render texture.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("[Field Tablet] screen render skipped: " + ex.Message);
            }
            FieldTabletPerfMeter.AddTextureRender(startedAt);
        }

        private static float ProbeRenderTextureMaxLuma(RenderTexture texture)
        {
            if (texture == null)
                return -1f;

            RenderTexture previous = RenderTexture.active;
            Texture2D probe = null;
            try
            {
                const int probeSize = 64;
                RenderTexture.active = texture;
                probe = new Texture2D(probeSize, probeSize, TextureFormat.RGBA32, false);
                probe.ReadPixels(new Rect((texture.width - probeSize) / 2f, (texture.height - probeSize) / 2f, probeSize, probeSize), 0, 0);
                probe.Apply(false);
                Color32[] pixels = probe.GetPixels32();
                byte max = 0;
                for (int i = 0; i < pixels.Length; i++)
                {
                    if (pixels[i].r > max) max = pixels[i].r;
                    if (pixels[i].g > max) max = pixels[i].g;
                    if (pixels[i].b > max) max = pixels[i].b;
                }
                return max / 255f;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("[Field Tablet] RT probe failed: " + ex.Message);
                return -1f;
            }
            finally
            {
                RenderTexture.active = previous;
                if (probe != null)
                    DestroyObject(probe);
            }
        }

        // F-TABLET-19: splicing is only bound on the SCAN tab (and the Hack tab is the SCAN tab
        // mid-channel), so scanning on MAP/DRONE/MFRM bought nothing but an amber world reticle and
        // a "Splice" prompt over a tab where Enter fires a lockdown. Skipping the scan there also
        // removes the RaycastAll from every other tab (part of F-TABLET-11).
        private static bool IsTargetScanRelevant =>
            _mode == TabletScreenMode.Scan || _mode == TabletScreenMode.Hack;

        private static void RefreshHackableTargetScan(PlayerControllerB player)
        {
            if (!IsTargetScanRelevant)
            {
                if (_currentTarget.IsValid)
                {
                    _currentTarget = default;
                    _lastTargetWasValid = false;
                }
                return;
            }

            float time = Time.unscaledTime;
            if (time < _nextTargetScanAt)
                return;

            _nextTargetScanAt = time + TargetScanInterval;
            if (TryFindHackableTarget(player, out TabletHackTarget target))
            {
                _currentTarget = target;
                return;
            }

            _currentTarget = default;
            // F-TABLET-16: BuildTarget sets "OBJECT DETECTED" on the rising edge but nothing ever
            // put the header back, so the SCAN tab read "OBJECT DETECTED" forever after the first
            // lock even with the reticle on empty air.
            if (_lastTargetWasValid && _mode == TabletScreenMode.Scan)
                _statusLine = "SCAN READY";
            _lastTargetWasValid = false;
        }

        private static bool ShouldRenderScreenTexture(float time)
        {
            if (_hasRenderedScreenTexture && time < _nextScreenRenderAt)
                return false;

            _hasRenderedScreenTexture = true;
            _nextScreenRenderAt = time + ScreenRenderInterval;
            return true;
        }

        private static bool ShouldRefreshDynamicText(float time)
        {
            if (time < _nextDynamicTextRefreshAt)
                return false;

            _nextDynamicTextRefreshAt = time + TabletDynamicTextRefreshInterval;
            return true;
        }

        private static void RefreshScanTargetText(bool hasTarget)
        {
            if (_scanTarget == null)
                return;
            if (!hasTarget)
            {
                _lastScanTargetRoot = null;
                _lastScanTargetLabel = string.Empty;
                _lastScanTargetName = string.Empty;
                _lastScanTargetDistanceTenths = int.MinValue;
                SetTextIfChanged(_scanTarget, string.Empty);
                return;
            }

            int distanceTenths = Mathf.RoundToInt(Mathf.Max(0f, _currentTarget.Distance) * 10f);
            string label = string.IsNullOrWhiteSpace(_currentTarget.Label)
                ? FormatTargetKind(_currentTarget.Kind)
                : _currentTarget.Label;
            bool targetChanged = _lastScanTargetRoot != _currentTarget.Root
                || !string.Equals(_lastScanTargetLabel, label, StringComparison.Ordinal);
            bool distanceChanged = _lastScanTargetDistanceTenths != distanceTenths;
            if (!targetChanged && (!distanceChanged || !ShouldRefreshDynamicText(Time.unscaledTime)))
                return;

            if (targetChanged)
            {
                _lastScanTargetRoot = _currentTarget.Root;
                _lastScanTargetLabel = label;
                // Only the name is cut, so the distance suffix always survives.
                _lastScanTargetName = FieldTabletLayout.FitChars(label.ToUpperInvariant(),
                    FieldTabletLayout.MaxChars(FieldTabletLayout.ScanTarget) - FieldTabletLayout.ScanTargetSuffixChars);
            }
            _lastScanTargetDistanceTenths = distanceTenths;
            SetTextIfChanged(_scanTarget, _lastScanTargetName + ScanDistanceSuffix(distanceTenths));
        }

        private static string ScanDistanceSuffix(int distanceTenths)
        {
            if (distanceTenths < 0 || distanceTenths >= ScanDistanceSuffixes.Length)
                return "  " + (distanceTenths / 10f).ToString("00.0") + "M";
            return ScanDistanceSuffixes[distanceTenths]
                ?? (ScanDistanceSuffixes[distanceTenths] = "  " + (distanceTenths / 10f).ToString("00.0") + "M");
        }

        private static void RefreshRadarStatusText()
        {
            if (_radarStatus == null)
                return;

            int zoomTenths = Mathf.RoundToInt(_radarZoom * 10f);
            if (_lastRadarZoomTenths == zoomTenths)
                return;

            _lastRadarZoomTenths = zoomTenths;
            if (zoomTenths < 0 || zoomTenths >= RadarZoomTexts.Length)
                SetTextIfChanged(_radarStatus, "ZOOM " + (zoomTenths / 10f).ToString("0.0") + "X");
            else
                SetTextIfChanged(_radarStatus, RadarZoomTexts[zoomTenths]
                    ?? (RadarZoomTexts[zoomTenths] = "ZOOM " + (zoomTenths / 10f).ToString("0.0") + "X"));
        }

        private static void RefreshHackTargetText()
        {
            if (_hackTargetText == null)
                return;

            string label = string.IsNullOrWhiteSpace(_hackTarget.Label) ? "FIELD TARGET" : _hackTarget.Label;
            if (_lastHackTargetLabel.Equals(label, StringComparison.OrdinalIgnoreCase))
                return;

            _lastHackTargetLabel = label;
            SetTextIfChanged(_hackTargetText, label.ToUpperInvariant());
        }

        private static void RefreshHackPercentText(bool solved)
        {
            if (_hackPercentText == null)
                return;
            if (solved)
            {
                _lastHackPercent = int.MinValue;
                SetTextIfChanged(_hackPercentText, _hackResult);
                return;
            }

            int percent = Mathf.FloorToInt(Mathf.Clamp01(_hackProgress) * 100f);
            if (_lastHackPercent == percent)
                return;

            _lastHackPercent = percent;
            if (percent < 0 || percent >= HackPercentTexts.Length)
                SetTextIfChanged(_hackPercentText, percent.ToString("00") + "%");
            else
                SetTextIfChanged(_hackPercentText, HackPercentTexts[percent]
                    ?? (HackPercentTexts[percent] = percent.ToString("00") + "%"));
        }

        private static void SetTextIfChanged(TMP_Text target, string value)
        {
            if (target == null)
                return;

            value = value ?? string.Empty;
            if (CachedTextValues.TryGetValue(target, out string cached) && string.Equals(cached, value, StringComparison.Ordinal))
                return;
            if (!CachedTextValues.ContainsKey(target) && string.Equals(target.text, value, StringComparison.Ordinal))
            {
                CachedTextValues[target] = value;
                return;
            }

            target.text = value;
            CachedTextValues[target] = value;
            _screenLayoutDirty = true;
        }

        private static void SetColorIfChanged(TMP_Text target, Color value)
        {
            if (target == null)
                return;
            if (CachedTextColors.TryGetValue(target, out Color cached) && cached == value)
                return;
            if (!CachedTextColors.ContainsKey(target) && target.color == value)
            {
                CachedTextColors[target] = value;
                return;
            }

            target.color = value;
            CachedTextColors[target] = value;
        }

        private static void SetColorIfChanged(Graphic target, Color value)
        {
            if (target == null)
                return;
            if (CachedGraphicColors.TryGetValue(target, out Color cached) && cached == value)
                return;
            if (!CachedGraphicColors.ContainsKey(target) && target.color == value)
            {
                CachedGraphicColors[target] = value;
                return;
            }

            target.color = value;
            CachedGraphicColors[target] = value;
        }

        private static void SetActiveIfChanged(GameObject target, bool active)
        {
            if (target == null)
                return;
            if (CachedActiveStates.TryGetValue(target, out bool cached) && cached == active)
                return;
            if (!CachedActiveStates.ContainsKey(target) && target.activeSelf == active)
            {
                CachedActiveStates[target] = active;
                return;
            }

            target.SetActive(active);
            CachedActiveStates[target] = active;
            _screenLayoutDirty = true;
        }

        private static void SetEnabledIfChanged(Behaviour target, bool enabled)
        {
            if (target != null && target.enabled != enabled)
                target.enabled = enabled;
        }

        private static void SetFontStyleIfChanged(TMP_Text target, FontStyles value)
        {
            if (target == null)
                return;
            if (CachedFontStyles.TryGetValue(target, out FontStyles cached) && cached == value)
                return;
            if (!CachedFontStyles.ContainsKey(target) && target.fontStyle == value)
            {
                CachedFontStyles[target] = value;
                return;
            }

            target.fontStyle = value;
            CachedFontStyles[target] = value;
            _screenLayoutDirty = true;
        }

        private static void ClearUiWriteCaches()
        {
            CachedTextValues.Clear();
            CachedTextColors.Clear();
            CachedFontStyles.Clear();
            CachedGraphicColors.Clear();
            CachedActiveStates.Clear();
        }

        private static void ResetDynamicTextState()
        {
            _lastScanTargetRoot = null;
            _lastScanTargetLabel = string.Empty;
            _lastScanTargetName = string.Empty;
            _lastScanTargetDistanceTenths = int.MinValue;
            _lastRadarZoomTenths = int.MinValue;
            _lastHackPercent = int.MinValue;
            _lastHackTargetLabel = string.Empty;
            _lastCommandTelemetry = string.Empty;
            _lastCommandStatusHint = string.Empty;
            _commandHintBase = null;
            _commandHintHasDrone = false;
            _commandHintBindingPath = null;
            _commandTelemetryHasDrone = false;
            _commandTelemetryHealth = int.MinValue;
            _commandTelemetryCargo = int.MinValue;
            _commandTelemetryStatus = null;
            _commandEntryCount = 0;
            _nextCommandEntriesRefreshAt = 0f;
            CameraRowLabels.Clear();
            StashCodeLabels.Clear();
            Array.Clear(LastCommandEntryReasons, 0, LastCommandEntryReasons.Length);
            Array.Clear(LastCommandEntryAvailable, 0, LastCommandEntryAvailable.Length);
        }

        private static void RenderTabletHackView()
        {
            SetActiveGroup(_hackGroup);

            bool solved = _hackSolvedAt > 0f;
            if (_hackStatusText != null)
            {
                bool refused = solved && !string.IsNullOrEmpty(_hackStatusDetail);
                SetTextIfChanged(_hackStatusText, !solved ? "SPLICING" : refused ? _hackStatusDetail : "LINKED");
                SetColorIfChanged(_hackStatusText, !solved ? ScreenAmber : refused ? ScreenRed : ScreenGreen);
            }
            if (_hackTargetText != null)
                RefreshHackTargetText();
            if (_hackPercentText != null)
            {
                RefreshHackPercentText(solved);
                bool awaitingHost = solved && _spliceCameraRequestPending && _hackTarget.Kind == TabletHackTargetKind.CctvCamera;
                SetColorIfChanged(_hackPercentText,
                    !solved || awaitingHost ? ScreenAmber : _hackResultFailed ? ScreenRed : ScreenGreen);
            }
            if (_hackHintText != null)
                SetActiveIfChanged(_hackHintText.gameObject, !solved);
            if (_hackProgressFill != null)
                _hackProgressFill.anchorMax = new Vector2(Mathf.Lerp(0.16f, 0.84f, Mathf.Clamp01(_hackProgress)), 0.47f);

            if (solved && Time.unscaledTime > _hackSolvedAt + HackSolvedLingerSeconds)
                StopHack("solved");
        }

        private static void UpdateTabletScanReticle(bool hasTarget)
        {
            if (_scanReticleRoot == null)
                return;

            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * (hasTarget ? 7.8f : 2.2f));
            float size = hasTarget ? Mathf.Lerp(172f, 192f, pulse) : Mathf.Lerp(68f, 80f, pulse);
            _scanReticleRoot.sizeDelta = new Vector2(size, size);
            Color color = hasTarget ? ScreenAmber : ScreenDim;
            for (int i = 0; i < ScanReticleLines.Count; i++)
            {
                if (ScanReticleLines[i] != null)
                    SetColorIfChanged(ScanReticleLines[i], color);
            }
            if (_scanPulse != null)
            {
                _scanPulse.color = hasTarget
                    ? new Color(ScreenAmber.r, ScreenAmber.g, ScreenAmber.b, Mathf.Lerp(0.06f, 0.18f, pulse))
                    : new Color(ScreenGreen.r, ScreenGreen.g, ScreenGreen.b, 0.035f);
                _scanPulse.rectTransform.sizeDelta = new Vector2(size * 1.2f, size * 1.2f);
            }
        }

        private static void UpdateScanlines(float time)
        {
            if (_rollingScanline == null)
                return;

            float y = Mathf.Lerp(-FieldTabletLayout.ScreenHeight * 0.52f, FieldTabletLayout.ScreenHeight * 0.52f, Mathf.Repeat(time * 0.18f, 1f));
            _rollingScanline.rectTransform.anchoredPosition = new Vector2(0f, y);
            float flicker = 0.17f + Mathf.PerlinNoise(time * 2.7f, 0.33f) * 0.11f;
            _rollingScanline.color = new Color(ScreenGreen.r, ScreenGreen.g, ScreenGreen.b, flicker);
        }

        internal static void SetDronePointerReticle(bool show, Color color, float flashSeconds = 0f, string label = null)
        {
            _dronePointerReticle.Show = show;
            _dronePointerReticle.Color = color;
            _dronePointerReticle.Size = WorldReticleBasePixels;
            _dronePointerReticle.FlashUntil = show && flashSeconds > 0f ? Time.unscaledTime + flashSeconds : (show ? _dronePointerReticle.FlashUntil : 0f);
            _dronePointerReticle.Label = show ? label ?? string.Empty : string.Empty;

            PlayerControllerB player = GameNetworkManager.Instance?.localPlayerController
                ?? StartOfRound.Instance?.localPlayerController;
            UpdateWorldReticle(player);
        }

        private static void UpdatePlayerCrosshair(PlayerControllerB player)
        {
            UpdateWorldReticle(player);
        }

        private static void UpdateWorldReticle(PlayerControllerB player)
        {
            Camera camera = player != null ? player.gameplayCamera : null;
            if (camera == null)
                camera = Camera.main;
            if (camera == null)
                return;

            WorldReticleRequest request = BuildWorldReticleRequest();
            EnsurePlayerCrosshair(camera);
            if (_playerReticleRoot == null)
                return;

            // #500: the components are cached when the reticle is built, and each property is
            // written only when its value moves.
            if (_playerReticleCanvas != null && !ReferenceEquals(_playerReticleCamera, camera))
            {
                _playerReticleCanvas.worldCamera = camera;
                _playerReticleCamera = camera;
            }

            bool show = request.Show;
            _worldReticleAlpha = Mathf.MoveTowards(_worldReticleAlpha, show ? 0.92f : 0f, Time.unscaledDeltaTime * 9f);
            bool visible = show || _worldReticleAlpha > 0.001f;
            SetActiveIfChanged(_playerReticleRoot, visible);

            if (_playerReticleGroup != null && _worldReticleAlpha != _playerReticleAppliedAlpha)
            {
                _playerReticleGroup.alpha = _worldReticleAlpha;
                _playerReticleAppliedAlpha = _worldReticleAlpha;
            }

            // An inactive reticle draws nothing, and the frame that reactivates it runs everything
            // below before it is drawn.
            if (!visible)
                return;

            Transform cameraTransform = camera.transform;
            _playerReticleRoot.transform.SetPositionAndRotation(
                cameraTransform.position + cameraTransform.forward * WorldReticleDistance,
                Quaternion.LookRotation(cameraTransform.forward, cameraTransform.up));

            float flashPulse = request.FlashUntil > Time.unscaledTime
                ? 1f + 0.16f * Mathf.Sin(Time.unscaledTime * 46f)
                : 1f;
            float size = Mathf.Max(1f, request.Size <= 0f ? WorldReticleBasePixels : request.Size) * flashPulse;
            RectTransform root = _playerReticleRect;
            if (root != null)
            {
                if (size != _playerReticleAppliedSize)
                {
                    root.sizeDelta = new Vector2(size, size);
                    _playerReticleAppliedSize = size;
                }
                float pixels = Mathf.Max(1f, camera.pixelHeight);
                float worldHeight = 2f * WorldReticleDistance * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f);
                float worldUnitsPerPixel = worldHeight / pixels;
                if (worldUnitsPerPixel != _playerReticleAppliedScale)
                {
                    root.localScale = Vector3.one * worldUnitsPerPixel;
                    _playerReticleAppliedScale = worldUnitsPerPixel;
                }
            }

            Color color = show ? request.Color : new Color(request.Color.r, request.Color.g, request.Color.b, 0f);
            for (int i = 0; i < PlayerReticleLines.Count; i++)
            {
                if (PlayerReticleLines[i] != null)
                    SetColorIfChanged(PlayerReticleLines[i], color);
            }

            if (_worldReticleLabel != null)
            {
                string label = show ? request.Label ?? string.Empty : string.Empty;
                bool showLabel = show && !string.IsNullOrWhiteSpace(label);
                SetActiveIfChanged(_worldReticleLabel.gameObject, showLabel || _worldReticleAlpha > 0.001f);
                SetTextIfChanged(_worldReticleLabel, label);
                SetColorIfChanged(_worldReticleLabel, showLabel ? color : new Color(color.r, color.g, color.b, 0f));
            }
        }

        private static WorldReticleRequest BuildWorldReticleRequest()
        {
            // F-TABLET-19: the amber world reticle is the splice affordance, so it follows the same
            // tab scope (Hack included, so it stays up through the channel).
            if (FieldOperationsTabletPatch.IsTabletActive && IsTargetScanRelevant && _currentTarget.IsValid)
            {
                float pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 8f);
                return new WorldReticleRequest
                {
                    Show = true,
                    Color = ScreenAmber,
                    Size = Mathf.Lerp(54f, 64f, pulse),
                    FlashUntil = 0f,
                    Label = string.Empty
                };
            }

            if (_dronePointerReticle.Show)
                return _dronePointerReticle;

            return new WorldReticleRequest
            {
                Show = false,
                Color = ScreenAmber,
                Size = 30f,
                FlashUntil = 0f,
                Label = string.Empty
            };
        }
    }
}
