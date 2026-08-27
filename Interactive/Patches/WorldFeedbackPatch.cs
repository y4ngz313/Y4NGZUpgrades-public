using System.Collections.Generic;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Y4NGZUpgrades.Interactive.Patches
{
    [HarmonyPatch]
    internal static class WorldFeedbackPatch
    {
        private const int HighValueScrapThreshold = 80;
        private const float NearMissSampleInterval = 0.1f;
        private static float _nextNearMissSampleTime;

        [HarmonyPatch(typeof(PlayerControllerB), "LateUpdate")]
        [HarmonyPostfix]
        private static void PostPlayerLateUpdate(PlayerControllerB __instance)
        {
            if (__instance == null || __instance != GameNetworkManager.Instance?.localPlayerController) return;
            if (Time.unscaledTime < _nextNearMissSampleTime) return;
            _nextNearMissSampleTime = Time.unscaledTime + NearMissSampleInterval;
            NearMissFeedback.Update(__instance);
        }

        [HarmonyPatch(typeof(PlayerControllerB), "DamagePlayer")]
        [HarmonyPostfix]
        private static void PostDamagePlayer(PlayerControllerB __instance, int damageNumber)
        {
            if (__instance == null || __instance != GameNetworkManager.Instance?.localPlayerController) return;
            if (damageNumber > 0) NearMissFeedback.NoteDamage();
        }

        [HarmonyPatch(typeof(GrabbableObject), "Start")]
        [HarmonyPostfix]
        private static void PostGrabbableStart(GrabbableObject __instance)
        {
            HighValueScrapGlint.Ensure(__instance, HighValueScrapThreshold);
        }

        [HarmonyPatch(typeof(GrabbableObject), nameof(GrabbableObject.SetScrapValue))]
        [HarmonyPostfix]
        private static void PostSetScrapValue(GrabbableObject __instance)
        {
            HighValueScrapGlint.Ensure(__instance, HighValueScrapThreshold);
        }

        [HarmonyPatch(typeof(HUDManager), "UpdateWeightCounter")]
        [HarmonyPostfix]
        private static void PostUpdateWeightCounter(HUDManager __instance)
        {
            ScrapPickupFeedback.PulseWeightCounter(__instance);
        }

        [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.DisplayNewScrapFound))]
        [HarmonyPrefix]
        private static void PreDisplayNewScrapFound(HUDManager __instance, ref GrabbableObject __state)
        {
            __state = __instance != null && __instance.itemsToBeDisplayed != null && __instance.itemsToBeDisplayed.Count > 0
                ? __instance.itemsToBeDisplayed[0]
                : null;
        }

        [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.DisplayNewScrapFound))]
        [HarmonyPostfix]
        private static void PostDisplayNewScrapFound(HUDManager __instance, GrabbableObject __state, int ___bottomBoxIndex)
        {
            ScrapPickupFeedback.PulseScrapDisplay(__instance, __state, ___bottomBoxIndex);
        }

        [HarmonyPatch(typeof(HUDManager), "UpdateScanNodes")]
        [HarmonyPostfix]
        private static void PostUpdateScanNodes(HUDManager __instance)
        {
            ScanFeedback.EnsureFadeDrivers(__instance);
        }

        [HarmonyPatch(typeof(HUDManager), "PingScan_performed")]
        [HarmonyPostfix]
        private static void PostPingScan(HUDManager __instance, float ___playerPingingScan)
        {
            if (___playerPingingScan < 0.2f) return;
            ScanFeedback.PlayScanAccent(__instance);
        }

        // Per-round reset, dispatched by RoundLifecycle.RoundStarted. It used to postfix
        // StartOfRound.StartGame, which never runs on a client (#214).
        internal static void OnRoundStarted()
        {
            HighValueScrapGlint.Reset();
            ScanFeedback.Reset();
            NearMissFeedback.Reset();
            _nextNearMissSampleTime = 0f;
        }

        [HarmonyPatch(typeof(StartOfRound), "EndOfGame")]
        [HarmonyPostfix]
        private static void PostEndOfGame()
        {
            HighValueScrapGlint.Reset();
            ScanFeedback.Reset();
            NearMissFeedback.Reset();
            _nextNearMissSampleTime = 0f;
        }
    }

    internal static class HighValueScrapGlint
    {
        private static readonly List<GrabbableObject> Candidates = new List<GrabbableObject>();
        private static HighValueScrapGlintDriver _driver;

        internal static void Ensure(GrabbableObject item, int valueThreshold)
        {
            if (Plugin.WorldFeedbackEnabled?.Value == false || Plugin.WorldFeedbackHighValueGlintEnabled?.Value == false) return;
            if (item == null || item.itemProperties == null || !item.itemProperties.isScrap) return;
            if (item.scrapValue < valueThreshold) return;

            if (!Candidates.Contains(item))
                Candidates.Add(item);

            EnsureDriver();
        }

        internal static void Reset()
        {
            Candidates.Clear();
            if (_driver != null) _driver.ClearTarget();
        }

        internal static GrabbableObject FindNearestCandidate(PlayerControllerB player, float maxDistance)
        {
            if (player == null) return null;

            Vector3 playerPosition = player.transform.position;
            float maxSqrDistance = maxDistance * maxDistance;
            float bestSqrDistance = maxSqrDistance;
            GrabbableObject best = null;

            for (int i = Candidates.Count - 1; i >= 0; i--)
            {
                GrabbableObject item = Candidates[i];
                if (!IsValidCandidate(item))
                {
                    Candidates.RemoveAt(i);
                    continue;
                }

                float sqrDistance = (item.transform.position - playerPosition).sqrMagnitude;
                if (sqrDistance > bestSqrDistance) continue;

                best = item;
                bestSqrDistance = sqrDistance;
            }

            return best;
        }

        private static void EnsureDriver()
        {
            if (_driver != null) return;

            GameObject host = new GameObject("Y4NGZ_HighValueScrapGlintDriver");
            Object.DontDestroyOnLoad(host);
            _driver = host.AddComponent<HighValueScrapGlintDriver>();
        }

        private static bool IsValidCandidate(GrabbableObject item)
        {
            return item != null
                   && item.itemProperties != null
                   && item.itemProperties.isScrap
                   && item.scrapValue >= 80
                   && !item.isHeld
                   && !item.isPocketed
                   && !item.isHeldByEnemy;
        }
    }

    internal sealed class HighValueScrapGlintDriver : MonoBehaviour
    {
        private const float CheckInterval = 0.35f;
        private const float MaxDistance = 18f;

        private Light _light;
        private GrabbableObject _target;
        private Renderer _targetRenderer;
        private float _nextCheckTime;
        private float _nextPulseTime;
        private float _pulseStartTime = -999f;
        private float _pulseDuration = 0.38f;

        private void Awake()
        {
            GameObject lightGo = new GameObject("Y4NGZ_HighValueScrapGlintLight");
            lightGo.transform.SetParent(transform, worldPositionStays: false);
            _light = lightGo.AddComponent<Light>();
            _light.type = LightType.Point;
            _light.color = new Color(1f, 0.78f, 0.48f);
            _light.range = 0.85f;
            _light.intensity = 0f;
            _light.enabled = false;
            _light.shadows = LightShadows.None;
            ScheduleNextPulse();
        }

        private void Update()
        {
            if (Plugin.WorldFeedbackEnabled?.Value == false || Plugin.WorldFeedbackHighValueGlintEnabled?.Value == false)
            {
                ClearTarget();
                return;
            }

            if (Time.time >= _nextCheckTime)
            {
                _nextCheckTime = Time.time + CheckInterval;
                PlayerControllerB player = GameNetworkManager.Instance?.localPlayerController;
                if (player == null || player.isPlayerDead)
                {
                    ClearTarget();
                    return;
                }

                GrabbableObject nextTarget = HighValueScrapGlint.FindNearestCandidate(player, MaxDistance);
                if (nextTarget != _target)
                {
                    _target = nextTarget;
                    _targetRenderer = _target != null
                        ? _target.GetComponentInChildren<Renderer>(includeInactive: false)
                        : null;
                }
            }

            UpdateLightPulse();
        }

        internal void ClearTarget()
        {
            _target = null;
            _targetRenderer = null;
            if (_light != null)
            {
                _light.intensity = 0f;
                _light.enabled = false;
            }
        }

        private void UpdateLightPulse()
        {
            if (_target == null || _light == null)
            {
                if (_light != null) _light.enabled = false;
                return;
            }

            if (_targetRenderer != null)
            {
                Bounds bounds = _targetRenderer.bounds;
                _light.transform.position = bounds.center + Vector3.up * Mathf.Min(0.18f, bounds.extents.y * 0.35f);
            }
            else
            {
                _light.transform.position = _target.transform.position + Vector3.up * 0.15f;
            }

            if (Time.time >= _nextPulseTime)
            {
                _pulseStartTime = Time.time;
                _pulseDuration = Random.Range(0.28f, 0.42f);
                ScheduleNextPulse();
            }

            float t = Mathf.Clamp01((Time.time - _pulseStartTime) / _pulseDuration);
            float pulse = t < 1f ? Mathf.Sin(t * Mathf.PI) : 0f;
            float valueScale = Mathf.Clamp(_target.scrapValue / 145f, 0.45f, 1f);
            float intensity = pulse * Mathf.Lerp(0.12f, 0.3f, valueScale);
            _light.intensity = intensity;
            _light.enabled = intensity > 0.01f;
        }

        private void ScheduleNextPulse()
        {
            _nextPulseTime = Time.time + Random.Range(3.8f, 7.8f);
        }
    }

    internal static class ScrapPickupFeedback
    {
        private static float _lastWeightPulseTime = -999f;

        internal static void PulseWeightCounter(HUDManager hud)
        {
            if (Plugin.WorldFeedbackEnabled?.Value == false || Plugin.WorldFeedbackScrapPickupEnabled?.Value == false) return;
            if (hud == null || hud.weightCounter == null) return;
            if (Time.unscaledTime - _lastWeightPulseTime < 0.18f) return;

            _lastWeightPulseTime = Time.unscaledTime;
            HudPulseDriver.Play(hud.weightCounter.gameObject, 0.08f, 0.22f, new Color(1f, 0.78f, 0.35f, 1f));
        }

        internal static void PulseScrapDisplay(HUDManager hud, GrabbableObject item, int displayedBoxIndex)
        {
            if (Plugin.WorldFeedbackEnabled?.Value == false || Plugin.WorldFeedbackScrapPickupEnabled?.Value == false) return;
            if (hud == null || item == null || hud.ScrapItemBoxes == null) return;
            if (displayedBoxIndex < 0 || displayedBoxIndex >= hud.ScrapItemBoxes.Length) return;

            ScrapItemHUDDisplay box = hud.ScrapItemBoxes[displayedBoxIndex];
            if (box == null) return;

            float bump = item.scrapValue >= 80 ? 0.1f : 0.06f;
            Color flash = item.scrapValue >= 80
                ? new Color(1f, 0.78f, 0.35f, 1f)
                : new Color(0.78f, 0.96f, 1f, 1f);

            if (box.valueText != null)
                HudPulseDriver.Play(box.valueText.gameObject, bump, 0.26f, flash);
            if (box.UIContainer != null)
                HudPulseDriver.Play(box.UIContainer.gameObject, bump * 0.45f, 0.24f, flash);
        }
    }

    internal sealed class HudPulseDriver : MonoBehaviour
    {
        private RectTransform _rect;
        private Graphic _graphic;
        private Vector3 _baseScale;
        private Color _baseColor;
        private Color _flashColor;
        private float _scaleBump;
        private float _duration;
        private float _startTime;

        internal static void Play(GameObject target, float scaleBump, float duration, Color flashColor)
        {
            if (target == null) return;
            HudPulseDriver driver = target.GetComponent<HudPulseDriver>();
            if (driver == null) driver = target.AddComponent<HudPulseDriver>();
            driver.Restart(scaleBump, duration, flashColor);
        }

        private void Awake()
        {
            _rect = GetComponent<RectTransform>();
            _graphic = GetComponent<Graphic>();
            _baseScale = _rect != null ? _rect.localScale : transform.localScale;
            _baseColor = _graphic != null ? _graphic.color : Color.white;
        }

        private void Restart(float scaleBump, float duration, Color flashColor)
        {
            if (_rect == null) _rect = GetComponent<RectTransform>();
            if (_graphic == null) _graphic = GetComponent<Graphic>();
            _baseScale = _rect != null ? _rect.localScale : transform.localScale;
            _baseColor = _graphic != null ? _graphic.color : Color.white;
            _scaleBump = scaleBump;
            _duration = Mathf.Max(0.05f, duration);
            _flashColor = flashColor;
            _startTime = Time.unscaledTime;
            enabled = true;
        }

        private void Update()
        {
            float t = Mathf.Clamp01((Time.unscaledTime - _startTime) / _duration);
            float wave = Mathf.Sin(t * Mathf.PI);
            Vector3 scale = _baseScale * (1f + wave * _scaleBump);

            if (_rect != null) _rect.localScale = scale;
            else transform.localScale = scale;

            if (_graphic != null)
                _graphic.color = Color.Lerp(_baseColor, _flashColor, wave * 0.35f);

            if (t < 1f) return;

            if (_rect != null) _rect.localScale = _baseScale;
            else transform.localScale = _baseScale;
            if (_graphic != null) _graphic.color = _baseColor;
            enabled = false;
        }
    }

    internal sealed class ScanNodeFade : MonoBehaviour
    {
        private CanvasGroup _group;
        private float _startTime;

        private void Awake()
        {
            _group = GetComponent<CanvasGroup>();
            if (_group == null) _group = gameObject.AddComponent<CanvasGroup>();
        }

        private void OnEnable()
        {
            if (_group == null) _group = GetComponent<CanvasGroup>();
            _startTime = Time.unscaledTime;
            if (_group != null) _group.alpha = 0f;
        }

        private void Update()
        {
            if (Plugin.WorldFeedbackEnabled?.Value == false || Plugin.WorldFeedbackScanEnhancementsEnabled?.Value == false)
            {
                if (_group != null) _group.alpha = 1f;
                return;
            }

            if (_group == null) return;
            float t = Mathf.Clamp01((Time.unscaledTime - _startTime) / 0.18f);
            _group.alpha = Mathf.SmoothStep(0f, 1f, t);
        }
    }

    internal static class ScanFeedback
    {
        private static readonly RaycastHit[] ScanHits = new RaycastHit[32];
        private static AudioClip _scrapScanClip;
        private static AudioClip _creatureScanClip;
        private static AudioClip _genericScanClip;
        private static int _fadeDriversHudId = -1;

        internal static void Reset()
        {
            _fadeDriversHudId = -1;
        }

        internal static void EnsureFadeDrivers(HUDManager hud)
        {
            if (Plugin.WorldFeedbackEnabled?.Value == false || Plugin.WorldFeedbackScanEnhancementsEnabled?.Value == false) return;
            if (hud == null || hud.scanElements == null) return;

            int hudId = hud.GetInstanceID();
            if (_fadeDriversHudId == hudId) return;

            for (int i = 0; i < hud.scanElements.Length; i++)
            {
                RectTransform element = hud.scanElements[i];
                if (element == null) continue;
                if (element.GetComponent<ScanNodeFade>() == null)
                    element.gameObject.AddComponent<ScanNodeFade>();
            }

            _fadeDriversHudId = hudId;
        }

        internal static void PlayScanAccent(HUDManager hud)
        {
            if (Plugin.WorldFeedbackEnabled?.Value == false || Plugin.WorldFeedbackScanEnhancementsEnabled?.Value == false) return;
            if (hud == null || hud.UIAudio == null) return;

            ScanCategory category = ResolveDominantCategory();
            switch (category)
            {
                case ScanCategory.Creature:
                    hud.UIAudio.PlayOneShot(GetCreatureScanClip(), 0.11f);
                    break;
                case ScanCategory.Scrap:
                    hud.UIAudio.PlayOneShot(GetScrapScanClip(), 0.12f);
                    break;
                default:
                    hud.UIAudio.PlayOneShot(GetGenericScanClip(), 0.08f);
                    break;
            }
        }

        private static ScanCategory ResolveDominantCategory()
        {
            PlayerControllerB player = GameNetworkManager.Instance?.localPlayerController;
            if (player == null || player.gameplayCamera == null) return ScanCategory.Generic;

            Transform camera = player.gameplayCamera.transform;
            int count = Physics.SphereCastNonAlloc(
                new Ray(camera.position + camera.forward * 20f, camera.forward),
                20f,
                ScanHits,
                80f,
                4194304);

            bool sawScrap = false;
            for (int i = 0; i < count && i < ScanHits.Length; i++)
            {
                ScanNodeProperties node = ScanHits[i].transform != null
                    ? ScanHits[i].transform.GetComponent<ScanNodeProperties>()
                    : null;
                if (node == null) continue;

                if (node.creatureScanID != -1 || node.nodeType == 1)
                    return ScanCategory.Creature;
                if (node.nodeType == 2)
                    sawScrap = true;
            }

            return sawScrap ? ScanCategory.Scrap : ScanCategory.Generic;
        }

        private static AudioClip GetScrapScanClip()
        {
            if (_scrapScanClip != null) return _scrapScanClip;
            _scrapScanClip = MakeToneClip("Y4NGZ_ScanScrapAccent", 1320f, 1760f, 0.055f, 0.32f);
            return _scrapScanClip;
        }

        private static AudioClip GetCreatureScanClip()
        {
            if (_creatureScanClip != null) return _creatureScanClip;
            _creatureScanClip = MakeToneClip("Y4NGZ_ScanCreatureAccent", 520f, 390f, 0.075f, 0.4f);
            return _creatureScanClip;
        }

        private static AudioClip GetGenericScanClip()
        {
            if (_genericScanClip != null) return _genericScanClip;
            _genericScanClip = MakeToneClip("Y4NGZ_ScanGenericAccent", 900f, 980f, 0.045f, 0.24f);
            return _genericScanClip;
        }

        private static AudioClip MakeToneClip(string name, float startHz, float endHz, float duration, float gain)
        {
            const int sampleRate = 44100;
            int sampleCount = Mathf.CeilToInt(sampleRate * duration);
            float[] data = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                float t = (float)i / sampleRate;
                float p = Mathf.Clamp01(t / duration);
                float hz = Mathf.Lerp(startHz, endHz, p);
                float envelope = Mathf.Sin(p * Mathf.PI);
                data[i] = Mathf.Sin(2f * Mathf.PI * hz * t) * envelope * gain;
            }

            AudioClip clip = AudioClip.Create(name, sampleCount, 1, sampleRate, false);
            clip.SetData(data, 0);
            clip.hideFlags = HideFlags.HideAndDontSave;
            return clip;
        }

        private enum ScanCategory
        {
            Generic,
            Scrap,
            Creature
        }
    }

    internal static class NearMissFeedback
    {
        private const float MinDistance = 0.85f;
        private const float MaxDistance = 2.55f;
        private const float MinApproachSpeed = 1.15f;
        private const float GlobalCooldown = 1.4f;
        private const float EvaluationInterval = 0.12f;
        private const float EnemyCacheRefreshInterval = 0.85f;
        private const float MinDistanceSqr = MinDistance * MinDistance;
        private const float MaxDistanceSqr = MaxDistance * MaxDistance;

        private static readonly List<EnemyAI> CachedEnemies = new List<EnemyAI>();
        private static readonly Dictionary<EnemyAI, Vector3> LastPositions = new Dictionary<EnemyAI, Vector3>();
        private static float _lastWhooshTime = -999f;
        private static float _lastDamageTime = -999f;
        private static float _nextEvaluationTime;
        private static float _nextEnemyCacheRefreshTime;
        private static AudioClip _whooshClip;

        internal static void Update(PlayerControllerB player)
        {
            if (Plugin.WorldFeedbackEnabled?.Value == false || Plugin.WorldFeedbackNearMissWhooshEnabled?.Value != true) return;
            if (player == null || player.isPlayerDead || player.gameplayCamera == null) return;
            if (Time.time - _lastWhooshTime < GlobalCooldown) return;
            if (Time.time - _lastDamageTime < 0.55f) return;
            if (Time.time < _nextEvaluationTime) return;
            _nextEvaluationTime = Time.time + EvaluationInterval;

            RefreshEnemyCache();
            if (CachedEnemies.Count == 0) return;

            Vector3 playerPosition = player.transform.position;
            for (int i = CachedEnemies.Count - 1; i >= 0; i--)
            {
                EnemyAI enemy = CachedEnemies[i];
                if (enemy == null || enemy.isEnemyDead)
                {
                    CachedEnemies.RemoveAt(i);
                    continue;
                }

                if (!LooksThreatening(enemy, player)) continue;

                Vector3 enemyPosition = enemy.transform.position;
                float sqrDistance = (playerPosition - enemyPosition).sqrMagnitude;
                if (sqrDistance < MinDistanceSqr || sqrDistance > MaxDistanceSqr)
                {
                    LastPositions[enemy] = enemyPosition;
                    continue;
                }

                Vector3 velocity = ResolveVelocity(enemy, enemyPosition);
                LastPositions[enemy] = enemyPosition;

                Vector3 toPlayer = (player.transform.position - enemyPosition).normalized;
                float approachSpeed = Vector3.Dot(velocity, toPlayer);
                if (approachSpeed < MinApproachSpeed) continue;
                if (Physics.Linecast(player.gameplayCamera.transform.position, enemyPosition + Vector3.up * 0.6f, StartOfRound.Instance.collidersAndRoomMaskAndDefault, QueryTriggerInteraction.Ignore))
                    continue;

                PlayWhoosh(player, enemyPosition, Mathf.Sqrt(sqrDistance));
                _lastWhooshTime = Time.time;
                return;
            }
        }

        internal static void NoteDamage()
        {
            _lastDamageTime = Time.time;
        }

        internal static void Reset()
        {
            CachedEnemies.Clear();
            LastPositions.Clear();
            _lastWhooshTime = -999f;
            _lastDamageTime = -999f;
            _nextEvaluationTime = 0f;
            _nextEnemyCacheRefreshTime = 0f;
        }

        private static void RefreshEnemyCache()
        {
            if (Time.time < _nextEnemyCacheRefreshTime) return;
            _nextEnemyCacheRefreshTime = Time.time + EnemyCacheRefreshInterval;

            CachedEnemies.Clear();
            if (RoundManager.Instance != null && RoundManager.Instance.SpawnedEnemies != null)
            {
                List<EnemyAI> spawned = RoundManager.Instance.SpawnedEnemies;
                for (int i = 0; i < spawned.Count; i++)
                {
                    EnemyAI enemy = spawned[i];
                    if (enemy == null || enemy.isEnemyDead) continue;
                    CachedEnemies.Add(enemy);
                }
                return;
            }

            EnemyAI[] enemies = Object.FindObjectsOfType<EnemyAI>();
            for (int i = 0; i < enemies.Length; i++)
            {
                EnemyAI enemy = enemies[i];
                if (enemy == null || enemy.isEnemyDead) continue;
                CachedEnemies.Add(enemy);
            }
        }

        private static bool LooksThreatening(EnemyAI enemy, PlayerControllerB player)
        {
            if (enemy.targetPlayer == player) return true;
            if (enemy.movingTowardsTargetPlayer && enemy.targetPlayer == null) return true;
            return enemy.currentBehaviourStateIndex > 0
                   && (enemy.transform.position - player.transform.position).sqrMagnitude < MaxDistanceSqr;
        }

        private static Vector3 ResolveVelocity(EnemyAI enemy, Vector3 currentPosition)
        {
            if (enemy.agent != null && enemy.agent.enabled)
            {
                Vector3 agentVelocity = enemy.agent.velocity;
                if (agentVelocity.sqrMagnitude > 0.01f) return agentVelocity;
            }

            if (!LastPositions.TryGetValue(enemy, out Vector3 lastPosition))
                return Vector3.zero;

            return (currentPosition - lastPosition) / Mathf.Max(0.001f, Time.deltaTime);
        }

        private static void PlayWhoosh(PlayerControllerB player, Vector3 enemyPosition, float distance)
        {
            AudioSource source = player.gameplayCamera.GetComponent<AudioSource>();
            float volume = Mathf.Lerp(0.18f, 0.08f, Mathf.InverseLerp(MinDistance, MaxDistance, distance));
            if (source != null)
            {
                source.PlayOneShot(GetWhooshClip(), volume);
                return;
            }

            AudioSource.PlayClipAtPoint(GetWhooshClip(), enemyPosition, volume);
        }

        private static AudioClip GetWhooshClip()
        {
            if (_whooshClip != null) return _whooshClip;

            const int sampleRate = 44100;
            const float duration = 0.18f;
            int sampleCount = Mathf.CeilToInt(sampleRate * duration);
            float[] data = new float[sampleCount];
            System.Random rng = new System.Random(9142);
            float previous = 0f;

            for (int i = 0; i < sampleCount; i++)
            {
                float t = (float)i / sampleRate;
                float p = Mathf.Clamp01(t / duration);
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                previous = Mathf.Lerp(previous, noise, 0.18f);
                float sweep = Mathf.Sin(2f * Mathf.PI * Mathf.Lerp(260f, 90f, p) * t) * 0.18f;
                float envelope = Mathf.Sin(p * Mathf.PI);
                data[i] = (previous * 0.45f + sweep) * envelope;
            }

            _whooshClip = AudioClip.Create("Y4NGZ_NearMissWhoosh", sampleCount, 1, sampleRate, false);
            _whooshClip.SetData(data, 0);
            _whooshClip.hideFlags = HideFlags.HideAndDontSave;
            return _whooshClip;
        }
    }
}
