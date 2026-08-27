using System;
using System.Collections;
using System.Collections.Generic;
using GameNetcodeStuff;
using HarmonyLib;
using Y4NGZUpgrades.HUD;
using Y4NGZUpgrades.Upgrades;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace Y4NGZUpgrades.Patches
{
    [HarmonyPatch]
    internal static class ShadowStepPatch
    {
        private const string MSG_INVIS_SYNC = "ShadowStep_InvisSync";

        private const float CLOSEST_PLAYER_BASELINE_RANGE = 40f;

        private static bool _externalInvisibilityActive = false;
        private static float _externalInvisibilityEndTime = 0f;

        private static bool _handlersRegistered = false;

        private const string HUD_SPRITE = "shadow_hud";
        private const int HUD_STACK_POSITION = 2;

        private const float REMOTE_VISIBILITY_FLICKER_DURATION = 0.45f;
        private const int REMOTE_VISIBILITY_FLICKER_TOGGLES = 6;

        private sealed class RemoteRendererState
        {
            internal readonly Renderer[] Renderers;
            internal readonly bool[] Enabled;
            internal readonly ShadowCastingMode[] ShadowCastingModes;

            internal RemoteRendererState(
                Renderer[] renderers,
                bool[] enabled,
                ShadowCastingMode[] shadowCastingModes)
            {
                Renderers = renderers;
                Enabled = enabled;
                ShadowCastingModes = shadowCastingModes;
            }
        }

        private static readonly Dictionary<PlayerControllerB, RemoteRendererState> _remoteRendererStates =
            new Dictionary<PlayerControllerB, RemoteRendererState>();
        private static readonly Dictionary<PlayerControllerB, int> _remoteVisibilitySequences =
            new Dictionary<PlayerControllerB, int>();
        private static readonly Dictionary<PlayerControllerB, Coroutine> _remoteVisibilityRoutines =
            new Dictionary<PlayerControllerB, Coroutine>();

        // --- Procedural audio clips (cached, generated once) ---
        private static AudioClip _activationClip;
        private static AudioClip _deactivationClip;

        // --- Cloak effects state ---
        private static float _originalAudioVolume = 1f;
        private static bool _audioVolumeCaptured = false;
        private static AudioSource _hissSource;
        private static AudioClip _hissClip;
        private static bool _cloakEffectsActive = false;
        private static AudioLowPassFilter _cloakLowPassFilter;
        private static bool _cloakLowPassWasAdded = false;
        private static float _originalLowPassCutoff = 22000f;
        private static float _originalLowPassResonance = 1f;

        private const float CLOAK_AUDIO_VOLUME_SCALE = 0.72f;
        private const float CLOAK_LOW_PASS_CUTOFF = 850f;
        private const float CLOAK_LOW_PASS_RESONANCE = 1.15f;

        // --- Cloak vignette ---
        private static Canvas _cloakOverlayCanvas;
        private static GameObject _cloakVignetteObj;
        private static RawImage _cloakVignette;
        private static Texture2D _vignetteTexture;


        // -----------------------------------------------------------------------
        // PROCEDURAL AUDIO GENERATION
        // -----------------------------------------------------------------------

        private static AudioClip GetActivationClip()
        {
            if (_activationClip != null) return _activationClip;

            int sampleRate = 44100;
            float duration = 0.25f;
            int sampleCount = (int)(sampleRate * duration);
            float[] samples = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                float t = (float)i / sampleRate;
                float progress = t / duration;

                float freq = Mathf.Lerp(1200f, 200f, progress);
                float envelope = Mathf.Exp(-t * 6f);
                float sample = Mathf.Sin(2f * Mathf.PI * freq * t) * envelope * 1.5f;
                float noise = (UnityEngine.Random.Range(-1f, 1f)) * envelope * 0.3f;

                samples[i] = Mathf.Clamp(sample + noise, -1f, 1f);
            }

            _activationClip = AudioClip.Create("Y4NGZ_ShadowStep_PhaseOut", sampleCount, 1, sampleRate, false);
            bool setDataOk = _activationClip.SetData(samples, 0);
            Plugin.Log.LogInfo($"[Shadow Step] Generated activation clip: samples={sampleCount}, SetData={setDataOk}");
            return _activationClip;
        }

        private static AudioClip GetDeactivationClip()
        {
            if (_deactivationClip != null) return _deactivationClip;

            int sampleRate = 44100;
            float duration = 0.12f;
            int sampleCount = (int)(sampleRate * duration);
            float[] samples = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                float t = (float)i / sampleRate;
                float progress = t / duration;

                float freq = Mathf.Lerp(200f, 800f, progress);
                float envelope = Mathf.Exp(-t * 10f);

                samples[i] = Mathf.Clamp(Mathf.Sin(2f * Mathf.PI * freq * t) * envelope * 1.5f, -1f, 1f);
            }

            _deactivationClip = AudioClip.Create("Y4NGZ_ShadowStep_SnapOff", sampleCount, 1, sampleRate, false);
            bool setDataOk = _deactivationClip.SetData(samples, 0);
            Plugin.Log.LogInfo($"[Shadow Step] Generated deactivation clip: samples={sampleCount}, SetData={setDataOk}");
            return _deactivationClip;
        }

        private static AudioClip GetHissClip()
        {
            if (_hissClip != null) return _hissClip;

            int sampleRate = 44100;
            float duration = 2f;
            int sampleCount = (int)(sampleRate * duration);
            float[] samples = new float[sampleCount];

            System.Random rng = new System.Random(42);
            for (int i = 0; i < sampleCount; i++)
            {
                samples[i] = (float)(rng.NextDouble() * 2.0 - 1.0) * 0.12f;
            }

            _hissClip = AudioClip.Create("Y4NGZ_ShadowStep_Hiss", sampleCount, 1, sampleRate, false);
            _hissClip.SetData(samples, 0);
            return _hissClip;
        }

        // -----------------------------------------------------------------------
        // 1) DETECTION OVERRIDES (POSTFIX)
        // -----------------------------------------------------------------------

        [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.CheckLineOfSightForPlayer))]
        [HarmonyPostfix]
        private static void PostCheckLineOfSightForPlayer(
            EnemyAI __instance, ref PlayerControllerB __result,
            float width, int range, int proximityAwareness)
        {
            try
            {
                if (__result == null) return;
                if (GameNetworkManager.Instance == null) return;
                if (__result != GameNetworkManager.Instance.localPlayerController) return;

                float multiplier = GetDetectionMultiplier();
                if (multiplier >= 1f) return;
                if (multiplier <= 0f) { __result = null; return; }

                float reducedRange = range * multiplier;
                float distance = Vector3.Distance(__instance.transform.position, __result.transform.position);
                if (distance > reducedRange) __result = null;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Shadow Step] CheckLineOfSight patch error: {e}");
            }
        }

        [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.GetClosestPlayer))]
        [HarmonyPostfix]
        private static void PostGetClosestPlayer(
            EnemyAI __instance, ref PlayerControllerB __result, bool requireLineOfSight)
        {
            try
            {
                if (__result == null) return;
                if (GameNetworkManager.Instance == null) return;
                if (__result != GameNetworkManager.Instance.localPlayerController) return;

                float multiplier = GetDetectionMultiplier();
                if (multiplier >= 1f) return;
                if (multiplier <= 0f) { __result = null; return; }

                float reducedRange = CLOSEST_PLAYER_BASELINE_RANGE * multiplier;
                float distance = Vector3.Distance(__instance.transform.position, __result.transform.position);
                if (distance > reducedRange) __result = null;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Shadow Step] GetClosestPlayer patch error: {e}");
            }
        }

        [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.PlayerIsTargetable))]
        [HarmonyPostfix]
        private static void PostPlayerIsTargetable(EnemyAI __instance, ref bool __result,
            PlayerControllerB playerScript)
        {
            try
            {
                if (!__result) return;
                if (playerScript != GameNetworkManager.Instance.localPlayerController) return;
                if (!IsAnyInvisibilityActive()) return;

                __result = false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Shadow Step] PlayerIsTargetable patch error: {e}");
            }
        }

        private static float GetDetectionMultiplier()
        {
            if (IsAnyInvisibilityActive()) return 0f;
            if (!ShadowStepUpgrade.IsUnlocked()) return 1f;

            int tier = ShadowStepUpgrade.GetTier();
            PlayerControllerB player = GameNetworkManager.Instance?.localPlayerController;
            if (player == null || player.isPlayerDead) return 1f;

            bool isSprinting = player.isSprinting;
            bool isCrouching = player.isCrouching;

            switch (tier)
            {
                case 1:
                    return isCrouching ? ShadowStepUpgrade.REDUCED_DETECTION_MULTIPLIER : 1f;
                case 2:
                case 3:
                    return isSprinting ? 1f : ShadowStepUpgrade.REDUCED_DETECTION_MULTIPLIER;
                default:
                    return 1f;
            }
        }

        // -----------------------------------------------------------------------
        // 2) LOCAL-PLAYER UPDATE TICK (KEYBIND POLL + HUD/OVERLAY DRIVERS)
        // -----------------------------------------------------------------------

        [HarmonyPatch(typeof(PlayerControllerB), "Update")]
        [HarmonyPostfix]
        private static void PostPlayerUpdate(PlayerControllerB __instance)
        {
            if (__instance == null) return;
            if (!__instance.IsOwner || !__instance.isPlayerControlled) return;
            if (__instance != GameNetworkManager.Instance?.localPlayerController) return;

            PollKeybind(__instance);
            UpdateInvisibilityTimer(__instance);
            UpdateExternalInvisibilityTimer(__instance);
            UpdateHUD();
            UpdateCloakVignette();
        }

        private static void PollKeybind(PlayerControllerB player)
        {
            if (!ShadowStepUpgrade.HasActiveAbility()) return;
            if (player.isPlayerDead) return;
            if (player.quickMenuManager != null && player.quickMenuManager.isMenuOpen) return;
            if (player.isTypingChat) return;
            if (player.inSpecialInteractAnimation) return;
            if (player.inTerminalMenu) return;

            if (Gui.UpgradeInput.WasPressed(Gui.Plugin.Keybinds?.ShadowStep))
                ActivateInvisibility(player);
        }

        // -----------------------------------------------------------------------
        // 3) INVISIBILITY ACTIVATION + TIMER
        // -----------------------------------------------------------------------

        private static void ActivateInvisibility(PlayerControllerB player)
        {
            if (!ShadowStepUpgrade.HasActiveAbility()) return;
            if (ShadowStepUpgrade.IsInvisible) return;
            if (Time.time < ShadowStepUpgrade.CooldownEndTime)
            {
                HUDManager.Instance?.DisplayTip(
                    "SHADOW STEP", "Still on cooldown.", isWarning: true);
                return;
            }

            ShadowStepUpgrade.IsInvisible         = true;
            ShadowStepUpgrade.InvisibilityEndTime = Time.time + ShadowStepUpgrade.INVISIBILITY_DURATION;
            ShadowStepUpgrade.CooldownEndTime     =
                ShadowStepUpgrade.InvisibilityEndTime + ShadowStepUpgrade.COOLDOWN_DURATION;

            ForceDeaggroAllEnemies();

            BroadcastInvisibilityState(true);

            var clip = GetActivationClip();
            PlayLocalShadowStepClip(clip, player.transform.position, 0.75f);

            StartCloakEffects();

            Plugin.Log.LogInfo($"[Shadow Step] Invisibility ACTIVATED - duration={ShadowStepUpgrade.INVISIBILITY_DURATION}s, IsInvisible={ShadowStepUpgrade.IsInvisible}");
        }

        internal static void ActivateExternalInvisibility(PlayerControllerB player, float seconds, string sourceLabel)
        {
            if (player == null || seconds <= 0f) return;
            if (GameNetworkManager.Instance == null) return;
            if (player != GameNetworkManager.Instance.localPlayerController) return;
            if (player.isPlayerDead) return;

            bool wasInvisible = IsAnyInvisibilityActive();
            _externalInvisibilityActive = true;
            _externalInvisibilityEndTime = Mathf.Max(_externalInvisibilityEndTime, Time.time + seconds);

            ForceDeaggroAllEnemies();

            if (!wasInvisible)
            {
                BroadcastInvisibilityState(true);
                PlayLocalShadowStepClip(GetActivationClip(), player.transform.position, 0.75f);
            }

            StartCloakEffects();
            Plugin.Log.LogInfo($"[Shadow Step] External invisibility from {sourceLabel ?? "upgrade"} active for {seconds:F1}s.");
        }

        private static void UpdateInvisibilityTimer(PlayerControllerB player)
        {
            if (!ShadowStepUpgrade.IsInvisible) return;
            if (Time.time < ShadowStepUpgrade.InvisibilityEndTime) return;

            ShadowStepUpgrade.IsInvisible = false;

            if (!IsAnyInvisibilityActive())
            {
                BroadcastInvisibilityState(false);
                PlayLocalShadowStepClip(GetDeactivationClip(), player.transform.position, 0.7f);
                StopCloakEffects();
            }


            Plugin.Log.LogInfo("[Shadow Step] Invisibility ENDED - cooldown active.");
        }

        private static void UpdateExternalInvisibilityTimer(PlayerControllerB player)
        {
            if (!_externalInvisibilityActive) return;
            if (Time.time < _externalInvisibilityEndTime) return;

            _externalInvisibilityActive = false;
            _externalInvisibilityEndTime = 0f;

            if (ShadowStepUpgrade.IsInvisible) return;

            BroadcastInvisibilityState(false);
            if (player != null)
                PlayLocalShadowStepClip(GetDeactivationClip(), player.transform.position, 0.7f);
            StopCloakEffects();
        }

        // -----------------------------------------------------------------------
        // FORCE DE-AGGRO ON ACTIVATION
        // -----------------------------------------------------------------------

        private static void ForceDeaggroAllEnemies()
        {
            try
            {
                var localPlayer = GameNetworkManager.Instance.localPlayerController;
                if (localPlayer == null) return;

                var enemies = UnityEngine.Object.FindObjectsOfType<EnemyAI>();
                int deaggroCount = 0;

                foreach (var enemy in enemies)
                {
                    if (enemy == null || enemy.isEnemyDead) continue;
                    if (enemy.targetPlayer != localPlayer) continue;

                    enemy.targetPlayer = null;
                    enemy.movingTowardsTargetPlayer = false;

                    if (enemy.currentBehaviourStateIndex != 0)
                    {
                        enemy.SwitchToBehaviourStateOnLocalClient(0);
                    }

                    deaggroCount++;
                }

                if (deaggroCount > 0)
                {
                    Plugin.Log.LogInfo($"[Shadow Step] Force de-aggroed {deaggroCount} enemies on activation.");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Shadow Step] ForceDeaggro error: {e}");
            }
        }

        // -----------------------------------------------------------------------
        // 4) HUD COOLDOWN DISPLAY (TIER 3 ONLY)
        // -----------------------------------------------------------------------

        private static void EnsureSpriteHUD()
        {
            if (UpgradeHUDManager.GetHUDElement(HUD_SPRITE) != null) return;
            if (HUDManager.Instance == null) return;
            Canvas canvas = HUDManager.Instance.playerScreenTexture != null
                ? HUDManager.Instance.playerScreenTexture.canvas
                : null;
            if (canvas == null) return;
            UpgradeHUDManager.CreateHUDElement(HUD_SPRITE, HUD_STACK_POSITION, canvas.transform);
        }

        private static void UpdateHUD()
        {
            if (!ShadowStepUpgrade.IsUnlocked() || !ShadowStepUpgrade.HasActiveAbility())
            {
                GameObject go = UpgradeHUDManager.GetHUDElement(HUD_SPRITE);
                if (go != null) go.SetActive(false);
                return;
            }

            EnsureSpriteHUD();
            GameObject element = UpgradeHUDManager.GetHUDElement(HUD_SPRITE);
            if (element == null) return;
            element.SetActive(true);

            if (ShadowStepUpgrade.IsInvisible)
            {
                UpgradeHUDManager.SetCooldownActive(HUD_SPRITE, false);
            }
            else if (Time.time < ShadowStepUpgrade.CooldownEndTime)
            {
                UpgradeHUDManager.SetCooldownActive(HUD_SPRITE, true);
                float remaining = ShadowStepUpgrade.CooldownEndTime - Time.time;
                UpgradeHUDManager.UpdateFillBar(HUD_SPRITE, remaining / ShadowStepUpgrade.COOLDOWN_DURATION);
            }
            else
            {
                UpgradeHUDManager.SetCooldownActive(HUD_SPRITE, false);
            }
        }

        // -----------------------------------------------------------------------
        // 5) CLOAK EFFECTS - AUDIO DAMPENING + HISS + VIGNETTE
        // -----------------------------------------------------------------------

        private static void StartCloakEffects()
        {
            if (_cloakEffectsActive) return;

            _cloakEffectsActive = true;

            var player = GameNetworkManager.Instance.localPlayerController;
            if (player != null)
            {
                StartAudioDampening(player);

                Transform audioHost = player.gameplayCamera != null ? player.gameplayCamera.transform : player.transform;
                _hissSource = audioHost.gameObject.AddComponent<AudioSource>();
                _hissSource.clip = GetHissClip();
                _hissSource.loop = true;
                _hissSource.volume = 0.09f;
                _hissSource.spatialBlend = 0f;
                _hissSource.playOnAwake = false;
                _hissSource.Play();
            }

            CreateCloakVignette();
        }

        private static void StopCloakEffects()
        {
            _cloakEffectsActive = false;

            if (_audioVolumeCaptured)
            {
                AudioListener.volume = _originalAudioVolume;
                _audioVolumeCaptured = false;
            }

            StopAudioDampening();

            if (_hissSource != null)
            {
                _hissSource.Stop();
                UnityEngine.Object.Destroy(_hissSource);
                _hissSource = null;
            }

            DestroyCloakVignette();
        }

        private static void StartAudioDampening(PlayerControllerB player)
        {
            if (!_audioVolumeCaptured)
            {
                _originalAudioVolume = AudioListener.volume;
                _audioVolumeCaptured = true;
            }

            AudioListener.volume = Mathf.Clamp01(_originalAudioVolume * CLOAK_AUDIO_VOLUME_SCALE);

            GameObject filterHost = null;
            AudioListener listener = UnityEngine.Object.FindObjectOfType<AudioListener>();
            if (listener != null) filterHost = listener.gameObject;
            if (filterHost == null && player != null && player.gameplayCamera != null)
                filterHost = player.gameplayCamera.gameObject;
            if (filterHost == null) return;

            _cloakLowPassFilter = filterHost.GetComponent<AudioLowPassFilter>();
            _cloakLowPassWasAdded = _cloakLowPassFilter == null;

            if (_cloakLowPassFilter == null)
                _cloakLowPassFilter = filterHost.AddComponent<AudioLowPassFilter>();
            else
            {
                _originalLowPassCutoff = _cloakLowPassFilter.cutoffFrequency;
                _originalLowPassResonance = _cloakLowPassFilter.lowpassResonanceQ;
            }

            _cloakLowPassFilter.cutoffFrequency = CLOAK_LOW_PASS_CUTOFF;
            _cloakLowPassFilter.lowpassResonanceQ = CLOAK_LOW_PASS_RESONANCE;
        }

        private static void StopAudioDampening()
        {
            if (_cloakLowPassFilter != null)
            {
                if (_cloakLowPassWasAdded)
                {
                    UnityEngine.Object.Destroy(_cloakLowPassFilter);
                }
                else
                {
                    _cloakLowPassFilter.cutoffFrequency = _originalLowPassCutoff;
                    _cloakLowPassFilter.lowpassResonanceQ = _originalLowPassResonance;
                }
            }

            _cloakLowPassFilter = null;
            _cloakLowPassWasAdded = false;
            _originalLowPassCutoff = 22000f;
            _originalLowPassResonance = 1f;
        }

        private static void PlayLocalShadowStepClip(AudioClip clip, Vector3 position, float volume)
        {
            if (clip == null) return;

            AudioSource uiAudio = HUDManager.Instance?.UIAudio;
            if (uiAudio != null)
                uiAudio.PlayOneShot(clip, volume);

            PlayerControllerB player = GameNetworkManager.Instance?.localPlayerController;
            if (player != null && player.gameplayCamera != null)
            {
                AudioSource source = player.gameplayCamera.GetComponent<AudioSource>();
                if (source != null)
                    source.PlayOneShot(clip, volume * 0.65f);
            }
            else
            {
                AudioSource.PlayClipAtPoint(clip, position, volume * 0.5f);
            }
        }

        // --- Vignette texture generation (cached once) ---

        private static Texture2D GetVignetteTexture()
        {
            if (_vignetteTexture != null) return _vignetteTexture;

            int size = 256;
            _vignetteTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            Color[] pixels = new Color[size * size];
            float center = size / 2f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - center) / center;
                    float dy = (y - center) / center;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);

                    float alpha = Mathf.Clamp01((dist - 0.38f) / 0.62f);
                    alpha = alpha * alpha * (3f - 2f * alpha);

                    pixels[y * size + x] = new Color(0f, 0f, 0f, alpha);
                }
            }

            _vignetteTexture.SetPixels(pixels);
            _vignetteTexture.Apply();
            return _vignetteTexture;
        }

        private static void CreateCloakVignette()
        {
            if (_cloakVignetteObj != null) return;

            Canvas targetCanvas = null;
            if (HUDManager.Instance != null && HUDManager.Instance.playerScreenTexture != null)
                targetCanvas = HUDManager.Instance.playerScreenTexture.canvas;

            if (targetCanvas == null)
            {
                GameObject canvasGo = new GameObject("Y4NGZ_ShadowStep_OverlayCanvas");
                _cloakOverlayCanvas = canvasGo.AddComponent<Canvas>();
                _cloakOverlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
                _cloakOverlayCanvas.sortingOrder = 320;
                canvasGo.AddComponent<CanvasScaler>();
                canvasGo.AddComponent<GraphicRaycaster>();
                UnityEngine.Object.DontDestroyOnLoad(canvasGo);
                targetCanvas = _cloakOverlayCanvas;
            }

            _cloakVignetteObj = new GameObject("ShadowStepCloakVignette");
            _cloakVignetteObj.transform.SetParent(targetCanvas.transform, false);

            RectTransform rect = _cloakVignetteObj.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            _cloakVignette = _cloakVignetteObj.AddComponent<RawImage>();
            _cloakVignette.texture = GetVignetteTexture();
            _cloakVignette.color = new Color(0f, 0.02f, 0.015f, 0.55f);
            _cloakVignette.raycastTarget = false;
            _cloakVignetteObj.transform.SetAsLastSibling();

            Plugin.Log.LogInfo("[Shadow Step] Cloak vignette created.");
        }

        private static void DestroyCloakVignette()
        {
            if (_cloakVignetteObj != null)
            {
                UnityEngine.Object.Destroy(_cloakVignetteObj);
                _cloakVignetteObj = null;
                _cloakVignette = null;
            }

            if (_cloakOverlayCanvas != null)
            {
                UnityEngine.Object.Destroy(_cloakOverlayCanvas.gameObject);
                _cloakOverlayCanvas = null;
            }
        }

        private static void UpdateCloakVignette()
        {
            if (!_cloakEffectsActive || _cloakVignette == null) return;

            _cloakVignetteObj?.transform.SetAsLastSibling();
            float remaining = GetCurrentInvisibilityEndTime() - Time.time;

            float pulseSpeed = 2f;
            float pulseRange = 0.05f;
            float baseAlpha = 0.55f;

            if (remaining < 2f)
            {
                pulseSpeed = 6f;
                pulseRange = 0.09f;
                baseAlpha = 0.68f;
            }

            float pulse = baseAlpha + Mathf.Sin(Time.time * pulseSpeed) * pulseRange;
            _cloakVignette.color = new Color(0f, 0.02f, 0.015f, Mathf.Clamp01(pulse));
        }

        private static bool IsAnyInvisibilityActive()
        {
            bool shadowActive = ShadowStepUpgrade.IsInvisible;
            bool externalActive = _externalInvisibilityActive && Time.time < _externalInvisibilityEndTime;
            return shadowActive || externalActive;
        }

        private static float GetCurrentInvisibilityEndTime()
        {
            float endTime = 0f;
            if (ShadowStepUpgrade.IsInvisible)
                endTime = Mathf.Max(endTime, ShadowStepUpgrade.InvisibilityEndTime);
            if (_externalInvisibilityActive)
                endTime = Mathf.Max(endTime, _externalInvisibilityEndTime);
            return endTime;
        }

        // -----------------------------------------------------------------------
        // 6) NETWORKING (NAMED MESSAGE BROADCAST)
        // -----------------------------------------------------------------------

#pragma warning disable Harmony003 // Custom message serializers mutate FastBufferReader/FastBufferWriter by design.
        /// <summary>
        /// Netcode has no client-to-all primitive - SendNamedMessageToAll reads ConnectedClientsIds,
        /// whose getter throws off-host. The host broadcasts; a client sends to the host, which
        /// relays in <see cref="OnReceiveInvisibilityState"/>.
        /// </summary>
        private static void BroadcastInvisibilityState(bool invisible)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsClient) return;
            CustomMessagingManager messaging = network.CustomMessagingManager;
            if (messaging == null) return;

            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
            if (local == null) return;

            FastBufferWriter writer = new FastBufferWriter(sizeof(int) + sizeof(bool), Allocator.Temp);
            try
            {
                writer.WriteValueSafe((int)local.playerClientId);
                writer.WriteValueSafe(invisible);
                if (network.IsServer)
                    messaging.SendNamedMessageToAll(MSG_INVIS_SYNC, writer);
                else
                    messaging.SendNamedMessage(MSG_INVIS_SYNC, NetworkManager.ServerClientId, writer);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"ShadowStepPatch: invisibility send failed: {ex.Message}");
            }
            finally
            {
                writer.Dispose();
            }
        }

        /// <summary>Host-only re-broadcast of a client's cloak state to every other client.</summary>
        private static void RelayInvisibilityState(ulong excludeClientId, int playerId, bool invisible)
        {
            NetworkManager network = NetworkManager.Singleton;
            CustomMessagingManager messaging = network?.CustomMessagingManager;
            if (network == null || messaging == null || !network.IsServer) return;

            foreach (ulong clientId in network.ConnectedClientsIds)
            {
                if (clientId == excludeClientId || clientId == network.LocalClientId)
                    continue;

                FastBufferWriter writer = new FastBufferWriter(sizeof(int) + sizeof(bool), Allocator.Temp);
                try
                {
                    writer.WriteValueSafe(playerId);
                    writer.WriteValueSafe(invisible);
                    messaging.SendNamedMessage(MSG_INVIS_SYNC, clientId, writer);
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"ShadowStepPatch: invisibility relay to {clientId} failed: {ex.Message}");
                }
                finally
                {
                    writer.Dispose();
                }
            }
        }

        private static PlayerControllerB ResolvePlayerByActualClientId(ulong clientId)
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return null;

            for (int i = 0; i < players.Length; i++)
            {
                if (players[i] != null && players[i].actualClientId == clientId)
                    return players[i];
            }

            return null;
        }

        private static void RegisterNetworkHandlers()
        {
            if (_handlersRegistered) return;
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.CustomMessagingManager == null) return;
            try
            {
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler(
                    MSG_INVIS_SYNC, OnReceiveInvisibilityState);
                _handlersRegistered = true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"ShadowStepPatch: RegisterNamedMessageHandler failed: {ex.Message}");
            }
        }

        [HarmonyPatch(typeof(PlayerControllerB), "ConnectClientToPlayerObject")]
        [HarmonyPostfix]
        private static void PostConnectClientToPlayerObject()
        {
            RegisterNetworkHandlers();
        }

        private static void OnReceiveInvisibilityState(ulong senderClientId, FastBufferReader reader)
        {
            int  playerId;
            bool invisible;
            try
            {
                reader.ReadValueSafe(out playerId);
                reader.ReadValueSafe(out invisible);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"ShadowStepPatch: malformed invis sync: {ex.Message}");
                return;
            }

            NetworkManager network = NetworkManager.Singleton;
            if (network != null && network.IsServer && senderClientId != network.LocalClientId)
            {
                // The payload's player id is attacker-controlled; the host trusts only the
                // connection the message arrived on, so a client can only cloak itself.
                PlayerControllerB sender = ResolvePlayerByActualClientId(senderClientId);
                if (sender == null)
                    return;

                playerId = (int)sender.playerClientId;
                RelayInvisibilityState(senderClientId, playerId, invisible);
            }

            int localId = GameNetworkManager.Instance != null && GameNetworkManager.Instance.localPlayerController != null
                ? (int)GameNetworkManager.Instance.localPlayerController.playerClientId
                : -1;
            if (playerId == localId) return;

            if (StartOfRound.Instance == null) return;
            if (playerId < 0 || playerId >= StartOfRound.Instance.allPlayerScripts.Length) return;
            PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerId];
            if (player == null) return;

            ApplyRemoteVisibility(player, invisible);
            SpawnSmokePoof(player.transform.position);
        }
#pragma warning restore Harmony003

        // -----------------------------------------------------------------------
        // 7) REMOTE PLAYER FULL INVISIBILITY
        // -----------------------------------------------------------------------

        private static void ApplyRemoteVisibility(PlayerControllerB player, bool invisible)
        {
            if (ReferenceEquals(player, null)) return;

            PlayerControllerB localPlayer = GameNetworkManager.Instance?.localPlayerController;
            if (localPlayer != null &&
                (player == localPlayer || player.playerClientId == localPlayer.playerClientId))
            {
                return;
            }

            int sequence = AdvanceRemoteVisibilitySequence(player);
            CancelRemoteVisibilityRoutine(player);

            if (!_remoteRendererStates.TryGetValue(player, out RemoteRendererState state))
            {
                if (!invisible) return;

                state = CaptureRemoteRendererState(player);
                if (state == null) return;
                _remoteRendererStates[player] = state;
            }

            GameNetworkManager host = GameNetworkManager.Instance;
            if (host == null || !host.isActiveAndEnabled)
            {
                if (invisible)
                    ApplyRemoteRendererState(state, shown: false);
                else
                    RestoreRemoteRendererState(player, state);
                return;
            }

            Coroutine routine = host.StartCoroutine(
                RemoteVisibilityFlickerRoutine(player, state, invisible, sequence));
            _remoteVisibilityRoutines[player] = routine;
        }

        private static RemoteRendererState CaptureRemoteRendererState(PlayerControllerB player)
        {
            var renderers = new List<Renderer>();
            var uniqueRenderers = new HashSet<Renderer>();

            AddRemoteRenderer(player.thisPlayerModel, renderers, uniqueRenderers);
            AddRemoteRenderer(player.thisPlayerModelLOD1, renderers, uniqueRenderers);
            AddRemoteRenderer(player.thisPlayerModelLOD2, renderers, uniqueRenderers);

            if (player.playerBodyAnimator != null)
            {
                Renderer[] bodyRenderers =
                    player.playerBodyAnimator.GetComponentsInChildren<Renderer>(includeInactive: true);
                for (int i = 0; i < bodyRenderers.Length; i++)
                    AddRemoteRenderer(bodyRenderers[i], renderers, uniqueRenderers);
            }

            if (renderers.Count == 0) return null;

            Renderer[] rendererArray = renderers.ToArray();
            bool[] enabled = new bool[rendererArray.Length];
            ShadowCastingMode[] shadowCastingModes = new ShadowCastingMode[rendererArray.Length];
            for (int i = 0; i < rendererArray.Length; i++)
            {
                Renderer renderer = rendererArray[i];
                enabled[i] = renderer.enabled;
                shadowCastingModes[i] = renderer.shadowCastingMode;
            }

            return new RemoteRendererState(rendererArray, enabled, shadowCastingModes);
        }

        private static void AddRemoteRenderer(
            Renderer renderer,
            List<Renderer> renderers,
            HashSet<Renderer> uniqueRenderers)
        {
            if (renderer != null && uniqueRenderers.Add(renderer))
                renderers.Add(renderer);
        }

        private static IEnumerator RemoteVisibilityFlickerRoutine(
            PlayerControllerB player,
            RemoteRendererState state,
            bool invisible,
            int sequence)
        {
            bool shown = invisible;
            ApplyRemoteRendererState(state, shown);

            float interval = REMOTE_VISIBILITY_FLICKER_DURATION / REMOTE_VISIBILITY_FLICKER_TOGGLES;
            var wait = new WaitForSeconds(interval);
            for (int i = 0; i < REMOTE_VISIBILITY_FLICKER_TOGGLES; i++)
            {
                yield return wait;
                if (!IsRemoteVisibilitySequenceCurrent(player, sequence))
                    yield break;

                shown = !shown;
                ApplyRemoteRendererState(state, shown);
            }

            if (!IsRemoteVisibilitySequenceCurrent(player, sequence))
                yield break;

            if (invisible)
                ApplyRemoteRendererState(state, shown: false);
            else
                RestoreRemoteRendererState(player, state);

            _remoteVisibilityRoutines.Remove(player);
        }

        private static void ApplyRemoteRendererState(RemoteRendererState state, bool shown)
        {
            for (int i = 0; i < state.Renderers.Length; i++)
            {
                Renderer renderer = state.Renderers[i];
                if (renderer == null) continue;

                renderer.enabled = shown && state.Enabled[i];
                renderer.shadowCastingMode = shown
                    ? state.ShadowCastingModes[i]
                    : ShadowCastingMode.Off;
            }
        }

        private static void RestoreRemoteRendererState(PlayerControllerB player)
        {
            if (ReferenceEquals(player, null)) return;

            AdvanceRemoteVisibilitySequence(player);
            CancelRemoteVisibilityRoutine(player);
            if (_remoteRendererStates.TryGetValue(player, out RemoteRendererState state))
                RestoreRemoteRendererState(player, state);
        }

        private static void RestoreRemoteRendererState(
            PlayerControllerB player,
            RemoteRendererState state)
        {
            for (int i = 0; i < state.Renderers.Length; i++)
            {
                Renderer renderer = state.Renderers[i];
                if (renderer == null) continue;

                renderer.enabled = state.Enabled[i];
                renderer.shadowCastingMode = state.ShadowCastingModes[i];
            }

            _remoteRendererStates.Remove(player);
        }

        private static void RestoreAllRemoteRendererStates()
        {
            List<PlayerControllerB> players = new List<PlayerControllerB>(_remoteRendererStates.Keys);
            for (int i = 0; i < players.Count; i++)
                RestoreRemoteRendererState(players[i]);

            _remoteRendererStates.Clear();
            _remoteVisibilitySequences.Clear();
            _remoteVisibilityRoutines.Clear();
        }

        private static int AdvanceRemoteVisibilitySequence(PlayerControllerB player)
        {
            _remoteVisibilitySequences.TryGetValue(player, out int sequence);
            sequence = sequence == int.MaxValue ? 1 : sequence + 1;
            _remoteVisibilitySequences[player] = sequence;
            return sequence;
        }

        private static bool IsRemoteVisibilitySequenceCurrent(
            PlayerControllerB player,
            int sequence)
        {
            return _remoteVisibilitySequences.TryGetValue(player, out int currentSequence) &&
                currentSequence == sequence;
        }

        private static void CancelRemoteVisibilityRoutine(PlayerControllerB player)
        {
            if (!_remoteVisibilityRoutines.TryGetValue(player, out Coroutine routine))
                return;

            GameNetworkManager host = GameNetworkManager.Instance;
            if (host != null && routine != null)
                host.StopCoroutine(routine);

            _remoteVisibilityRoutines.Remove(player);
        }

        // -----------------------------------------------------------------------
        // 8) SMOKE POOF (BORROWED PARTICLE + PROCEDURAL AUDIO)
        // -----------------------------------------------------------------------

        private static void SpawnSmokePoof(Vector3 position)
        {
            GameObject smokePrefab = FindSmokeParticleSystem();
            if (smokePrefab != null)
            {
                GameObject poof = UnityEngine.Object.Instantiate(smokePrefab, position + Vector3.up, Quaternion.identity);
                poof.transform.localScale = Vector3.one * 0.4f;
                ParticleSystem ps = poof.GetComponent<ParticleSystem>();
                if (ps != null)
                {
                    ParticleSystem.MainModule main = ps.main;
                    main.startLifetime = 0.6f;
                    main.startSize     = 1.5f;
                    main.startColor    = new Color(0.3f, 0.3f, 0.3f, 0.5f);
                    main.duration      = 0.3f;
                    main.loop          = false;
                    ps.Play();
                }
                UnityEngine.Object.Destroy(poof, 2f);
            }
            else
            {
                GameObject poof = new GameObject("Y4NGZ_ShadowStep_ProceduralPoof");
                poof.transform.position = position + Vector3.up;
                ParticleSystem ps = poof.AddComponent<ParticleSystem>();
                ParticleSystem.MainModule main = ps.main;
                main.duration = 0.28f;
                main.loop = false;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.38f, 0.72f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.25f, 0.85f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.45f, 1.2f);
                main.startColor = new ParticleSystem.MinMaxGradient(
                    new Color(0.08f, 0.14f, 0.13f, 0.42f),
                    new Color(0.28f, 0.42f, 0.36f, 0.34f));
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.gravityModifier = -0.03f;

                ParticleSystem.EmissionModule emission = ps.emission;
                emission.enabled = true;
                emission.rateOverTime = 0f;
                emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 18) });

                ParticleSystem.ShapeModule shape = ps.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = 0.35f;

                ps.Play(withChildren: true);
                UnityEngine.Object.Destroy(poof, 1.4f);

                GameObject lightHost = new GameObject("Y4NGZ_ShadowStep_PoofLight");
                lightHost.transform.position = position + Vector3.up;
                Light light = lightHost.AddComponent<Light>();
                light.type      = LightType.Point;
                light.color     = new Color(0.35f, 0.95f, 0.72f);
                light.intensity = 7f;
                light.range     = 5f;
                light.shadows   = LightShadows.None;
                UnityEngine.Object.Destroy(lightHost, 0.25f);
            }

            PlayLocalShadowStepClip(GetActivationClip(), position, 0.5f);
        }

        private static GameObject FindSmokeParticleSystem()
        {
            ParticleSystem[] systems = Resources.FindObjectsOfTypeAll<ParticleSystem>();
            for (int i = 0; i < systems.Length; i++)
            {
                ParticleSystem ps = systems[i];
                if (ps == null || ps.gameObject == null) continue;
                string n = ps.gameObject.name.ToLowerInvariant();
                if (n.Contains("smoke") || n.Contains("poof") || n.Contains("stun"))
                    return ps.gameObject;
            }
            return null;
        }

        // -----------------------------------------------------------------------
        // 9) ROUND RESET + END-OF-ROUND + DEATH CLEANUP
        // -----------------------------------------------------------------------

        // Per-round reset, dispatched by RoundLifecycle.RoundStarted. It used to postfix
        // StartOfRound.StartGame, which never runs on a client (#214).
        internal static void OnRoundStarted()
        {
            ShadowStepUpgrade.IsInvisible         = false;
            ShadowStepUpgrade.CooldownEndTime     = 0f;
            ShadowStepUpgrade.InvisibilityEndTime = 0f;
            _externalInvisibilityActive = false;
            _externalInvisibilityEndTime = 0f;

            StopCloakEffects();
            DestroyCloakVignette();
        }

        [HarmonyPatch(typeof(StartOfRound), "EndOfGame")]
        [HarmonyPostfix]
        private static void PostEndOfGame()
        {
            Cleanup();
        }

        [HarmonyPatch(typeof(PlayerControllerB), "KillPlayer")]
        [HarmonyPostfix]
        private static void PostKillPlayer(PlayerControllerB __instance)
        {
            try
            {
                RestoreRemoteRendererState(__instance);
                if (__instance != GameNetworkManager.Instance?.localPlayerController) return;

                ShadowStepUpgrade.IsInvisible = false;
                _externalInvisibilityActive = false;
                _externalInvisibilityEndTime = 0f;
                StopCloakEffects();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Shadow Step] KillPlayer cleanup error: {e}");
            }
        }

        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        [HarmonyPostfix]
        private static void PostDisconnect()
        {
            Cleanup();
            _handlersRegistered = false;
        }

        private static void Cleanup()
        {
            UpgradeHUDManager.DestroyHUDElement(HUD_SPRITE);

            StopCloakEffects();
            DestroyCloakVignette();

            ShadowStepUpgrade.IsInvisible         = false;
            ShadowStepUpgrade.CooldownEndTime     = 0f;
            ShadowStepUpgrade.InvisibilityEndTime = 0f;
            _externalInvisibilityActive = false;
            _externalInvisibilityEndTime = 0f;

            RestoreAllRemoteRendererStates();
        }
    }
}
