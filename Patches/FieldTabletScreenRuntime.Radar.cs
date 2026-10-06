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
        // A null focus is the MAP tab's player focus, resolved (with its floor raycast) only when a
        // render is actually due.
        private static bool TryRenderRadarTextureThrottled(PlayerControllerB player, Vector3? focus, bool insideFactory, bool inShipRoom, bool updateMarkers)
        {
            float time = Time.unscaledTime;
            bool modeChanged = _mode != _lastRadarRenderedMode;
            if (!modeChanged && time < _nextRadarRenderAt)
                return _radarRendered;

            long startedAt = FieldTabletPerfMeter.Timestamp();
            _nextRadarRenderAt = time + RadarRenderInterval;
            _lastRadarRenderedMode = _mode;
            _radarRendered = RenderRadarTexture(player, focus ?? ResolveRadarFocus(player), insideFactory, inShipRoom);
            if (_radarRendered && updateMarkers)
                UpdateRadarMarkers(player);
            FieldTabletPerfMeter.AddRadarRender(startedAt);
            return _radarRendered;
        }

        private static bool RenderRadarTexture(PlayerControllerB player, Vector3 focus, bool insideFactory, bool inShipRoom)
        {
            ManualCameraRenderer mapScreen = StartOfRound.Instance != null ? StartOfRound.Instance.mapScreen : null;
            Camera sourceCamera = mapScreen != null ? mapScreen.mapCamera : null;
            if (player == null || sourceCamera == null)
                return false;

            EnsureRadarTexture();
            EnsureRadarCamera(sourceCamera);
            if (_radarCamera == null || _radarTexture == null)
                return false;

            ConfigureRadarCamera(mapScreen, sourceCamera, focus, insideFactory, inShipRoom);
            _vanillaMapUiState = HideVanillaMapUi(mapScreen);
            GameObject contourMap = ResolveContourMap(mapScreen);
            bool restoreContour = false;
            bool contourWasActive = false;
            Vector3 contourPosition = Vector3.zero;
            if (contourMap != null)
            {
                restoreContour = true;
                contourWasActive = contourMap.activeSelf;
                contourPosition = contourMap.transform.position;
                bool showOutsideMap = !insideFactory;
                contourMap.SetActive(showOutsideMap);
                if (showOutsideMap)
                    contourMap.transform.position = new Vector3(contourPosition.x, focus.y - 1.5f, contourPosition.z);
            }

            try
            {
                _radarCamera.Render();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("[Field Tablet] radar render skipped: " + ex.Message);
                return false;
            }
            finally
            {
                if (restoreContour && contourMap != null)
                {
                    contourMap.SetActive(contourWasActive);
                    contourMap.transform.position = contourPosition;
                }
                RestoreVanillaMapUi(mapScreen);
            }

            return true;
        }

        private static void EnsureRadarTexture()
        {
            if (_radarTexture != null && _radarTexture.IsCreated())
            {
                if (_radarImage != null && _radarImage.texture != _radarTexture)
                    _radarImage.texture = _radarTexture;
                return;
            }

            // F-TABLET-13: same leak as the screen RT, and worse here - _radarTexture carries
            // HideFlags.HideAndDontSave, so an orphaned one survives scene loads for the session.
            if (_radarTexture != null)
            {
                _radarTexture.Release();
                DestroyObject(_radarTexture);
                _radarTexture = null;
            }

            _radarTexture = new RenderTexture(RadarTextureSize, RadarTextureSize, 16, RenderTextureFormat.ARGB32)
            {
                name = "Y4NGZ_FieldTablet_RadarTexture",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false
            };
            _radarTexture.Create();
            if (_radarImage != null)
                _radarImage.texture = _radarTexture;
        }

        private static void EnsureRadarCamera(Camera sourceCamera)
        {
            if (sourceCamera == null)
                return;
            if (_radarCamera == null)
            {
                GameObject cameraGo = new GameObject("Y4NGZ_FieldTablet_RadarCamera") { hideFlags = HideFlags.HideAndDontSave };
                _radarCamera = cameraGo.AddComponent<Camera>();
            }
            if (_radarSourceCamera != sourceCamera)
            {
                _radarCamera.CopyFrom(sourceCamera);
                _radarSourceCamera = sourceCamera;
            }
            _radarCamera.enabled = false;
            _radarCamera.targetTexture = _radarTexture;
            _radarCamera.rect = new Rect(0f, 0f, 1f, 1f);
            _radarCamera.aspect = 1f;
        }

        private static void ConfigureRadarCamera(ManualCameraRenderer mapScreen, Camera sourceCamera, Vector3 focus, bool insideFactory, bool inShipRoom)
        {
            _radarCamera.transform.SetPositionAndRotation(
                new Vector3(focus.x, focus.y + RadarCameraHeight, focus.z),
                sourceCamera.transform.rotation);

            _radarCamera.cullingMask = sourceCamera.cullingMask;
            _radarCamera.clearFlags = sourceCamera.clearFlags;
            _radarCamera.backgroundColor = sourceCamera.backgroundColor;
            _radarCamera.orthographic = sourceCamera.orthographic;
            _radarCamera.orthographicSize = Mathf.Max(1f, sourceCamera.orthographicSize / Mathf.Max(0.1f, _radarZoom));
            _radarCamera.fieldOfView = sourceCamera.fieldOfView;

            if (inShipRoom)
            {
                _radarCamera.nearClipPlane = -0.96f;
                _radarCamera.farClipPlane = 7.52f;
            }
            else if (!insideFactory)
            {
                float near = mapScreen != null ? mapScreen.cameraNearPlane : sourceCamera.nearClipPlane;
                float far = mapScreen != null ? mapScreen.cameraFarPlane : sourceCamera.farClipPlane;
                _radarCamera.nearClipPlane = near - 18f;
                _radarCamera.farClipPlane = far + 18f;
            }
            else
            {
                _radarCamera.nearClipPlane = mapScreen != null ? mapScreen.cameraNearPlane : sourceCamera.nearClipPlane;
                _radarCamera.farClipPlane = mapScreen != null ? mapScreen.cameraFarPlane : sourceCamera.farClipPlane;
            }
        }

        private static Vector3 ResolveRadarFocus(PlayerControllerB player)
        {
            Vector3 focus = player.transform.position;
            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMask : ~0;
            if (Physics.Raycast(player.transform.position + Vector3.up * 0.1f, Vector3.down, out RaycastHit hit, 5f, mask, QueryTriggerInteraction.Ignore))
                focus = hit.point + Vector3.up * 0.06f;
            return focus;
        }

        private static GameObject ResolveContourMap(ManualCameraRenderer mapScreen)
        {
            if (mapScreen == null)
                return null;
            if (mapScreen.contourMap != null)
                return mapScreen.contourMap;
            // #500: a scene without the tag used to pay a tag search on every radar render; a
            // miss now waits ContourMapRetrySeconds before the next search.
            float now = Time.unscaledTime;
            if (now < _contourMapRetryAt)
                return null;
            try
            {
                GameObject contour = GameObject.FindGameObjectWithTag("TerrainContourMap");
                if (contour != null)
                    mapScreen.contourMap = contour;
                else
                    _contourMapRetryAt = now + ContourMapRetrySeconds;
                return contour;
            }
            catch
            {
                _contourMapRetryAt = now + ContourMapRetrySeconds;
                return null;
            }
        }

        private static VanillaMapUiState HideVanillaMapUi(ManualCameraRenderer mapScreen)
        {
            VanillaMapUiState state = new VanillaMapUiState();
            StartOfRound round = StartOfRound.Instance;
            if (round != null)
            {
                if (round.mapScreenPlayerName != null)
                {
                    state.MapScreenPlayerNameEnabled = round.mapScreenPlayerName.enabled;
                    round.mapScreenPlayerName.enabled = false;
                }
                if (round.mapScreenPlayerNameBG != null)
                {
                    state.MapScreenPlayerNameBgEnabled = round.mapScreenPlayerNameBG.enabled;
                    round.mapScreenPlayerNameBG.enabled = false;
                }
            }

            if (mapScreen == null)
                return state;

            if (mapScreen.headMountedCamUI != null)
            {
                state.HeadMountedCamUiEnabled = mapScreen.headMountedCamUI.enabled;
                mapScreen.headMountedCamUI.enabled = false;
            }
            if (mapScreen.localPlayerPlaceholder != null)
            {
                state.LocalPlayerPlaceholderEnabled = mapScreen.localPlayerPlaceholder.enabled;
                mapScreen.localPlayerPlaceholder.enabled = false;
            }
            if (mapScreen.compassRose != null)
            {
                state.CompassRoseEnabled = mapScreen.compassRose.enabled;
                mapScreen.compassRose.enabled = false;
            }
            if (mapScreen.shipArrowUI != null)
            {
                state.ShipArrowUiActive = mapScreen.shipArrowUI.activeSelf;
                mapScreen.shipArrowUI.SetActive(false);
            }
            if (mapScreen.shipIcon != null)
            {
                state.ShipIconActive = mapScreen.shipIcon.activeSelf;
                mapScreen.shipIcon.SetActive(false);
            }
            if (mapScreen.LostSignalUI != null)
            {
                state.LostSignalUiActive = mapScreen.LostSignalUI.activeSelf;
                mapScreen.LostSignalUI.SetActive(false);
            }
            if (mapScreen.lineFromRadarTargetToExit != null)
            {
                state.ExitLineEnabled = mapScreen.lineFromRadarTargetToExit.enabled;
                mapScreen.lineFromRadarTargetToExit.enabled = false;
            }
            return state;
        }

        private static void RestoreVanillaMapUi(ManualCameraRenderer mapScreen)
        {
            StartOfRound round = StartOfRound.Instance;
            if (round != null)
            {
                if (round.mapScreenPlayerName != null)
                    round.mapScreenPlayerName.enabled = _vanillaMapUiState.MapScreenPlayerNameEnabled;
                if (round.mapScreenPlayerNameBG != null)
                    round.mapScreenPlayerNameBG.enabled = _vanillaMapUiState.MapScreenPlayerNameBgEnabled;
            }

            if (mapScreen == null)
                return;
            if (mapScreen.headMountedCamUI != null)
                mapScreen.headMountedCamUI.enabled = _vanillaMapUiState.HeadMountedCamUiEnabled;
            if (mapScreen.localPlayerPlaceholder != null)
                mapScreen.localPlayerPlaceholder.enabled = _vanillaMapUiState.LocalPlayerPlaceholderEnabled;
            if (mapScreen.compassRose != null)
                mapScreen.compassRose.enabled = _vanillaMapUiState.CompassRoseEnabled;
            if (mapScreen.shipArrowUI != null)
                mapScreen.shipArrowUI.SetActive(_vanillaMapUiState.ShipArrowUiActive);
            if (mapScreen.shipIcon != null)
                mapScreen.shipIcon.SetActive(_vanillaMapUiState.ShipIconActive);
            if (mapScreen.LostSignalUI != null)
                mapScreen.LostSignalUI.SetActive(_vanillaMapUiState.LostSignalUiActive);
            if (mapScreen.lineFromRadarTargetToExit != null)
                mapScreen.lineFromRadarTargetToExit.enabled = _vanillaMapUiState.ExitLineEnabled;
        }

        private static EntranceTeleport[] ResolveRoundEntrances()
        {
            StartOfRound round = StartOfRound.Instance;
            // An empty result is never cached: the radar can be opened before the dungeon has
            // finished spawning its entrances, and that must not freeze an empty list in.
            if (_cachedEntrances != null && _cachedEntrances.Length > 0 && _cachedEntranceRound == round)
            {
                bool intact = true;
                for (int i = 0; i < _cachedEntrances.Length; i++)
                {
                    if (_cachedEntrances[i] == null)
                    {
                        intact = false;
                        break;
                    }
                }
                if (intact)
                    return _cachedEntrances;
            }

            _cachedEntranceRound = round;
            _cachedEntrances = UnityEngine.Object.FindObjectsOfType<EntranceTeleport>();
            return _cachedEntrances;
        }

        private static void UpdateRadarMarkers(PlayerControllerB player)
        {
            if (_radarMarkerLayer == null || _radarCamera == null || player == null)
                return;

            int used = 0;
            AddRadarMarker(ref used, player.transform.position, ScreenGreen, 16f, player.transform.eulerAngles.y);
            StartOfRound round = StartOfRound.Instance;
            if (round != null && round.allPlayerScripts != null)
            {
                for (int i = 0; i < round.allPlayerScripts.Length; i++)
                {
                    PlayerControllerB teammate = round.allPlayerScripts[i];
                    if (teammate == null || teammate == player || teammate.isPlayerDead || !teammate.isPlayerControlled || teammate.isInsideFactory != player.isInsideFactory)
                        continue;
                    AddRadarMarker(ref used, teammate.transform.position, ScreenAmber, 12f, float.NaN);
                }
            }

            EntranceTeleport[] entrances = ResolveRoundEntrances();
            bool wantEntranceToBuilding = !player.isInsideFactory;
            for (int i = 0; i < entrances.Length; i++)
            {
                EntranceTeleport entrance = entrances[i];
                if (entrance == null || entrance.isEntranceToBuilding != wantEntranceToBuilding)
                    continue;
                Vector3 pos = entrance.entrancePoint != null ? entrance.entrancePoint.position : entrance.transform.position;
                AddRadarMarker(ref used, pos, entrance.entranceId == 0 ? ScreenCyan : ScreenAmber, entrance.entranceId == 0 ? 12f : 9f, float.NaN);
            }

            for (int i = used; i < RadarMarkers.Count; i++)
            {
                if (RadarMarkers[i] != null)
                    SetActiveIfChanged(RadarMarkers[i].gameObject, false);
            }
        }

        private static void AddRadarMarker(ref int used, Vector3 worldPosition, Color color, float size, float headingDegrees)
        {
            if (!TryWorldToRadarPosition(worldPosition + Vector3.up * 0.25f, out Vector2 anchoredPosition))
                return;

            Image marker = GetRadarMarker(used++);
            RectTransform rt = marker.rectTransform;
            rt.anchoredPosition = anchoredPosition;
            rt.sizeDelta = new Vector2(size, size);
            rt.localEulerAngles = float.IsNaN(headingDegrees) ? new Vector3(0f, 0f, 45f) : new Vector3(0f, 0f, -headingDegrees);
            SetColorIfChanged(marker, color);
            SetActiveIfChanged(marker.gameObject, true);
        }

        private static bool TryWorldToRadarPosition(Vector3 worldPosition, out Vector2 anchoredPosition)
        {
            anchoredPosition = Vector2.zero;
            if (_radarCamera == null || _radarMarkerLayer == null)
                return false;

            Vector3 viewport = _radarCamera.WorldToViewportPoint(worldPosition);
            if (viewport.z < 0f || viewport.x < 0f || viewport.x > 1f || viewport.y < 0f || viewport.y > 1f)
                return false;

            Rect rect = _radarMarkerLayer.rect;
            anchoredPosition = new Vector2((viewport.x - 0.5f) * rect.width, (viewport.y - 0.5f) * rect.height);
            return true;
        }

        private static Image GetRadarMarker(int index)
        {
            while (RadarMarkers.Count <= index)
            {
                Image img = CreateImage("RadarMarker", _radarMarkerLayer, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-6f, -6f), new Vector2(6f, 6f), ScreenGreen);
                img.gameObject.SetActive(false);
                _screenLayoutDirty = true;
                RadarMarkers.Add(img);
            }
            return RadarMarkers[index];
        }
    }
}
