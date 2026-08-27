using System.Collections.Generic;
using GameNetcodeStuff;
using HarmonyLib;
using Y4NGZUpgrades.Upgrades;
using UnityEngine;
using UnityEngine.UI;

namespace Y4NGZUpgrades.Patches
{
    /// <summary>
    /// Each frame the local player's PlayerControllerB ticks Update, this patch
    /// scans for nearby enemies and tags every in-range enemy with a red point
    /// light child object. Lights persist while the enemy stays in range and are
    /// destroyed the moment the enemy leaves it (or dies). Tier 3 also drives a
    /// heartbeat AudioSource whose interval scales linearly with the closest
    /// in-range enemy's distance to the player.
    ///
    /// Lights are spawned with shadows disabled and no shadow casters so they
    /// don't drag perf in dark interior cells. Network sync isn't required -
    /// each client renders its own lights based on its own upgrade state.
    /// </summary>
    [HarmonyPatch]
    internal static class PredatorInstinctPatch
    {
        private const string MARKER_NAME = "Y4NGZ_PredatorInstinct_Marker";

        private const float HEARTBEAT_INTERVAL_AT_FAR = 1.6f;
        private const float HEARTBEAT_INTERVAL_AT_CLOSE = 0.45f;

        private static AudioClip _heartbeatClip;
        private static AudioSource _heartbeatSource;
        private static float _nextHeartbeatTime;

        // Enemy list cache - refresh at most once per ENEMY_SCAN_INTERVAL seconds.
        private static EnemyAI[] _cachedEnemies = System.Array.Empty<EnemyAI>();
        private static float _nextEnemyScanTime;
        private const float ENEMY_SCAN_INTERVAL = 0.5f;

        // Marker cache - maps each enemy to its Light so we never call Transform.Find per frame.
        private static readonly Dictionary<EnemyAI, Light> _markerCache = new Dictionary<EnemyAI, Light>();
        private static readonly Dictionary<EnemyAI, RectTransform> _indicatorCache = new Dictionary<EnemyAI, RectTransform>();
        private static Canvas _overlayCanvas;
        private static Sprite _enemyMarkerSprite;

        // -----------------------------------------------------------------------
        // 1) PER-FRAME SCAN
        // -----------------------------------------------------------------------

        [HarmonyPatch(typeof(PlayerControllerB), "Update")]
        [HarmonyPostfix]
        private static void PostPlayerUpdate(PlayerControllerB __instance)
        {
            if (__instance == null) return;
            if (!__instance.IsOwner || !__instance.isPlayerControlled) return;
            if (__instance != GameNetworkManager.Instance?.localPlayerController) return;
            if (__instance.isPlayerDead) { ClearAllMarkers(); StopHeartbeat(); return; }

            if (!PredatorInstinctUpgrade.IsUnlocked())
            {
                ClearAllMarkers();
                StopHeartbeat();
                return;
            }

            float range = PredatorInstinctUpgrade.GetRange();
            if (range <= 0f) return;

            EnsureCanvas();
            Camera cam = __instance.gameplayCamera != null ? __instance.gameplayCamera : Camera.main;

            if (Time.time >= _nextEnemyScanTime)
            {
                _cachedEnemies = Object.FindObjectsOfType<EnemyAI>();
                _nextEnemyScanTime = Time.time + ENEMY_SCAN_INTERVAL;
            }

            float rangeSqr = range * range;
            float closestSqr = float.MaxValue;
            EnemyAI[] enemies = _cachedEnemies;

            for (int i = 0; i < enemies.Length; i++)
            {
                EnemyAI e = enemies[i];
                if (e == null) continue;

                if (e.isEnemyDead)
                {
                    if (_markerCache.TryGetValue(e, out Light deadLight))
                    {
                        if (deadLight != null) Object.Destroy(deadLight.gameObject);
                        _markerCache.Remove(e);
                    }
                    RemoveIndicator(e);
                    continue;
                }

                float sqrDist = (e.transform.position - __instance.transform.position).sqrMagnitude;
                bool hasMarker = _markerCache.TryGetValue(e, out Light existingLight) && existingLight != null;

                if (sqrDist <= rangeSqr)
                {
                    if (!hasMarker) AddMarker(e);
                    UpdateIndicator(e, true, Mathf.Sqrt(sqrDist), range, cam);
                    if (sqrDist < closestSqr) closestSqr = sqrDist;
                }
                else
                {
                    if (hasMarker)
                    {
                        Object.Destroy(existingLight.gameObject);
                        _markerCache.Remove(e);
                    }
                    UpdateIndicator(e, false, 0f, range, cam);
                }
            }

            // Heartbeat is Tier-3-gated and only ticks while at least one enemy is in range.
            if (PredatorInstinctUpgrade.ShouldHeartbeat() && closestSqr < float.MaxValue)
            {
                EnsureHeartbeatSource(__instance);
                float dist = Mathf.Sqrt(closestSqr);
                float t = Mathf.Clamp01(dist / range);
                float interval = Mathf.Lerp(HEARTBEAT_INTERVAL_AT_CLOSE, HEARTBEAT_INTERVAL_AT_FAR, t);
                if (Time.time >= _nextHeartbeatTime)
                {
                    _heartbeatSource.PlayOneShot(_heartbeatClip);
                    _nextHeartbeatTime = Time.time + interval;
                }
            }
            else
            {
                StopHeartbeat();
            }
        }

        // -----------------------------------------------------------------------
        // 2) MARKER LIFECYCLE
        // -----------------------------------------------------------------------

        private static void AddMarker(EnemyAI enemy)
        {
            GameObject marker = new GameObject(MARKER_NAME);
            marker.transform.SetParent(enemy.transform, worldPositionStays: false);
            marker.transform.localPosition = Vector3.up * 0.6f;

            Light l = marker.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = new Color(1f, 0.15f, 0.15f);
            l.intensity = 4f;
            l.range = 6f;
            l.shadows = LightShadows.None;

            _markerCache[enemy] = l;
        }

        private static void UpdateIndicator(EnemyAI enemy, bool visible, float distance, float range, Camera cam)
        {
            RectTransform indicator;
            if (!_indicatorCache.TryGetValue(enemy, out indicator) || indicator == null)
            {
                if (!visible || _overlayCanvas == null) return;

                GameObject go = new GameObject("PredatorInstinct_Indicator_" + enemy.gameObject.name);
                go.transform.SetParent(_overlayCanvas.transform, worldPositionStays: false);
                indicator = go.AddComponent<RectTransform>();
                indicator.sizeDelta = new Vector2(22f, 22f);
                indicator.localRotation = Quaternion.Euler(0f, 0f, 45f);

                Image img = go.AddComponent<Image>();
                img.sprite = GetEnemyMarkerSprite();
                img.color = new Color(1f, 0.12f, 0.08f, 0f);
                img.raycastTarget = false;
                _indicatorCache[enemy] = indicator;
            }

            if (!visible || cam == null)
            {
                indicator.gameObject.SetActive(false);
                return;
            }

            Vector3 sp = cam.WorldToScreenPoint(ResolveEnemyCenter(enemy));
            if (sp.z <= 0f)
            {
                sp.x = Screen.width - sp.x;
                sp.y = Screen.height - sp.y;
            }

            const float margin = 34f;
            sp.x = Mathf.Clamp(sp.x, margin, Screen.width - margin);
            sp.y = Mathf.Clamp(sp.y, margin, Screen.height - margin);

            indicator.gameObject.SetActive(true);
            indicator.SetAsLastSibling();
            indicator.position = new Vector3(sp.x, sp.y, 0f);

            float proximity = 1f - Mathf.Clamp01(distance / Mathf.Max(range, 0.1f));
            float pulse = 0.72f + Mathf.Sin(Time.time * Mathf.Lerp(3.5f, 8f, proximity)) * 0.18f;
            float alpha = Mathf.Lerp(0.26f, 0.82f, proximity) * pulse;
            indicator.localScale = Vector3.one * Mathf.Lerp(0.85f, 1.28f, proximity);

            Image markerImage = indicator.GetComponent<Image>();
            if (markerImage != null)
                markerImage.color = new Color(1f, 0.12f, 0.06f, Mathf.Clamp01(alpha));
        }

        private static void RemoveIndicator(EnemyAI enemy)
        {
            RectTransform indicator;
            if (!_indicatorCache.TryGetValue(enemy, out indicator)) return;
            if (indicator != null) Object.Destroy(indicator.gameObject);
            _indicatorCache.Remove(enemy);
        }

        private static void ClearAllMarkers()
        {
            foreach (var kvp in _markerCache)
            {
                if (kvp.Value != null)
                    Object.Destroy(kvp.Value.gameObject);
            }
            _markerCache.Clear();

            foreach (var kvp in _indicatorCache)
            {
                if (kvp.Value != null)
                    Object.Destroy(kvp.Value.gameObject);
            }
            _indicatorCache.Clear();

            _cachedEnemies = System.Array.Empty<EnemyAI>();
            _nextEnemyScanTime = 0f;
        }

        private static void EnsureCanvas()
        {
            if (_overlayCanvas != null) return;

            GameObject go = new GameObject("PredatorInstinctCanvas");
            _overlayCanvas = go.AddComponent<Canvas>();
            _overlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _overlayCanvas.sortingOrder = 185;
            CanvasScaler scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            go.AddComponent<GraphicRaycaster>();
            Object.DontDestroyOnLoad(go);
        }

        private static Vector3 ResolveEnemyCenter(EnemyAI enemy)
        {
            if (enemy == null) return Vector3.zero;
            if (enemy.eye != null) return enemy.eye.position;

            Renderer renderer = null;
            if (enemy.skinnedMeshRenderers != null)
            {
                for (int i = 0; i < enemy.skinnedMeshRenderers.Length; i++)
                {
                    if (enemy.skinnedMeshRenderers[i] == null) continue;
                    renderer = enemy.skinnedMeshRenderers[i];
                    break;
                }
            }

            if (renderer == null && enemy.meshRenderers != null)
            {
                for (int i = 0; i < enemy.meshRenderers.Length; i++)
                {
                    if (enemy.meshRenderers[i] == null) continue;
                    renderer = enemy.meshRenderers[i];
                    break;
                }
            }

            return renderer != null ? renderer.bounds.center : enemy.transform.position + Vector3.up;
        }

        private static Sprite GetEnemyMarkerSprite()
        {
            if (_enemyMarkerSprite != null) return _enemyMarkerSprite;

            const int size = 64;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave
            };

            Vector2 center = new Vector2((size - 1) * 0.5f, (size - 1) * 0.5f);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = Mathf.Abs((x - center.x) / center.x);
                    float dy = Mathf.Abs((y - center.y) / center.y);
                    float dist = dx + dy;
                    float ring = Mathf.Clamp01(1f - Mathf.Abs(dist - 0.72f) / 0.12f);
                    float core = Mathf.Clamp01(1f - dist / 0.32f) * 0.55f;
                    float alpha = Mathf.Clamp01(Mathf.Max(ring, core));
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            tex.Apply(false, true);
            _enemyMarkerSprite = Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f));
            _enemyMarkerSprite.hideFlags = HideFlags.HideAndDontSave;
            return _enemyMarkerSprite;
        }

        // -----------------------------------------------------------------------
        // 3) HEARTBEAT AUDIO (TIER 3)
        // -----------------------------------------------------------------------

        private static void EnsureHeartbeatSource(PlayerControllerB player)
        {
            if (_heartbeatClip == null) _heartbeatClip = BuildHeartbeatClip();
            if (_heartbeatSource == null || _heartbeatSource.gameObject == null)
            {
                Camera cam = player.gameplayCamera != null ? player.gameplayCamera : Camera.main;
                Transform host = cam != null ? cam.transform : player.transform;
                GameObject go = new GameObject("Y4NGZ_PredatorInstinct_HeartbeatSrc");
                go.transform.SetParent(host, worldPositionStays: false);
                _heartbeatSource = go.AddComponent<AudioSource>();
                _heartbeatSource.spatialBlend = 0f;
                _heartbeatSource.volume = 0.55f;
                _heartbeatSource.playOnAwake = false;
                _nextHeartbeatTime = 0f;
            }
        }

        private static void StopHeartbeat()
        {
            if (_heartbeatSource == null) return;
            _heartbeatSource.Stop();
            Object.Destroy(_heartbeatSource.gameObject);
            _heartbeatSource = null;
            _nextHeartbeatTime = 0f;
        }

        private static AudioClip BuildHeartbeatClip()
        {
            const int sampleRate = 22050;
            const float duration = 0.35f;
            int sampleCount = Mathf.CeilToInt(sampleRate * duration);
            float[] data = new float[sampleCount];

            float lubAt = 0f;
            float dubAt = 0.13f;
            float lubFreq = 55f;
            float dubFreq = 70f;
            float decay = 18f;

            for (int i = 0; i < sampleCount; i++)
            {
                float t = (float)i / sampleRate;
                float lub = (t >= lubAt)
                    ? Mathf.Sin(2f * Mathf.PI * lubFreq * (t - lubAt)) * Mathf.Exp(-decay * (t - lubAt))
                    : 0f;
                float dub = (t >= dubAt)
                    ? Mathf.Sin(2f * Mathf.PI * dubFreq * (t - dubAt)) * Mathf.Exp(-decay * (t - dubAt)) * 0.85f
                    : 0f;
                data[i] = Mathf.Clamp(lub + dub, -1f, 1f);
            }

            AudioClip clip = AudioClip.Create("Y4NGZ_PredatorInstinct_Heartbeat", sampleCount, 1, sampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
