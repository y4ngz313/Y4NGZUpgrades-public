using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Patches
{
    [HarmonyPatch]
    internal static class EscapeReflexPatch
    {
        private static RawImage _vignette;
        private static Texture2D _vignetteTexture;

        [HarmonyPatch(typeof(PlayerControllerB), "DamagePlayer")]
        [HarmonyPostfix]
        private static void PostDamagePlayer(PlayerControllerB __instance, int damageNumber)
        {
            if (!IsLocalPlayer(__instance))
                return;
            if (damageNumber <= 0 || __instance.isPlayerDead)
                return;
            if (!EscapeReflexUpgrade.IsUnlocked() || EscapeReflexUpgrade.UsedThisRound)
                return;
            if (__instance.health <= 0 || __instance.health > EscapeReflexUpgrade.HEALTH_THRESHOLD)
                return;

            TriggerEscapeReflex(__instance);
        }

        [HarmonyPatch(typeof(PlayerControllerB), "LateUpdate")]
        [HarmonyPostfix]
        private static void PostPlayerLateUpdate(PlayerControllerB __instance)
        {
            if (!IsLocalPlayer(__instance))
                return;

            ApplySpeedBoost(__instance);
            UpdateVignette();
        }

        // Per-round reset, dispatched by RoundLifecycle.RoundStarted. It used to postfix
        // StartOfRound.StartGame, which never runs on a client (#214).
        internal static void OnRoundStarted()
        {
            ResetState();
        }

        [HarmonyPatch(typeof(StartOfRound), "EndOfGame")]
        [HarmonyPostfix]
        private static void PostEndOfGame()
        {
            ResetState();
        }

        [HarmonyPatch(typeof(PlayerControllerB), "KillPlayer")]
        [HarmonyPostfix]
        private static void PostKillPlayer(PlayerControllerB __instance)
        {
            if (!IsLocalPlayer(__instance))
                return;

            EscapeReflexUpgrade.SpeedBoostEndTime = 0f;
            ApplySpeedBoost(__instance);
            HideVignette();
        }

        private static void TriggerEscapeReflex(PlayerControllerB player)
        {
            EscapeReflexUpgrade.UsedThisRound = true;
            EscapeReflexUpgrade.VisualEndTime = Time.unscaledTime + EscapeReflexUpgrade.VISUAL_SECONDS;

            player.sprintMeter = 1f;
            if (player.sprintMeterUI != null)
                player.sprintMeterUI.fillAmount = player.sprintMeter;

            int tier = EscapeReflexUpgrade.GetTier();
            if (tier >= 2)
                ShadowStepPatch.ActivateExternalInvisibility(player, EscapeReflexUpgrade.INVISIBILITY_SECONDS, "Escape Reflex");

            if (tier >= 3)
                EscapeReflexUpgrade.SpeedBoostEndTime = Time.time + EscapeReflexUpgrade.SPEED_BOOST_SECONDS;
        }

        private static void ApplySpeedBoost(PlayerControllerB player)
        {
            if (player == null)
                return;

            float baseSpeed = player.movementSpeed - EscapeReflexUpgrade.AppliedSpeedBonus;
            bool active = EscapeReflexUpgrade.HasSpeedBoost()
                          && Time.time < EscapeReflexUpgrade.SpeedBoostEndTime
                          && !player.isPlayerDead;

            float nextBonus = active
                ? Mathf.Max(0f, baseSpeed) * EscapeReflexUpgrade.SPEED_BOOST_MULTIPLIER
                : 0f;

            player.movementSpeed = Mathf.Max(0.1f, baseSpeed + nextBonus);
            EscapeReflexUpgrade.AppliedSpeedBonus = nextBonus;
        }

        private static void ResetState()
        {
            EscapeReflexUpgrade.SpeedBoostEndTime = 0f;

            PlayerControllerB player = GameNetworkManager.Instance?.localPlayerController;
            if (player != null && EscapeReflexUpgrade.AppliedSpeedBonus != 0f)
                ApplySpeedBoost(player);

            EscapeReflexUpgrade.UsedThisRound = false;
            EscapeReflexUpgrade.AppliedSpeedBonus = 0f;
            EscapeReflexUpgrade.VisualEndTime = 0f;
            HideVignette();
        }

        private static void UpdateVignette()
        {
            if (Time.unscaledTime >= EscapeReflexUpgrade.VisualEndTime)
            {
                HideVignette();
                return;
            }

            EnsureVignette();
            if (_vignette == null)
                return;

            float remaining = EscapeReflexUpgrade.VisualEndTime - Time.unscaledTime;
            float alpha = Mathf.Clamp01(remaining / EscapeReflexUpgrade.VISUAL_SECONDS);
            float pulse = Mathf.Abs(Mathf.Sin(Time.unscaledTime * 16f)) * 0.08f;
            _vignette.gameObject.SetActive(true);
            _vignette.color = new Color(0.22f, 0.95f, 0.78f, Mathf.Clamp01(alpha * 0.35f + pulse));
        }

        private static void EnsureVignette()
        {
            if (_vignette != null)
                return;
            if (HUDManager.Instance == null || HUDManager.Instance.playerScreenTexture == null)
                return;

            Canvas canvas = HUDManager.Instance.playerScreenTexture.canvas;
            if (canvas == null)
                return;

            GameObject go = new GameObject("Y4NGZ_EscapeReflexVignette");
            go.transform.SetParent(canvas.transform, worldPositionStays: false);
            RectTransform rect = go.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            _vignette = go.AddComponent<RawImage>();
            _vignette.texture = GetVignetteTexture();
            _vignette.raycastTarget = false;
            _vignette.color = new Color(0.22f, 0.95f, 0.78f, 0f);
        }

        private static Texture2D GetVignetteTexture()
        {
            if (_vignetteTexture != null)
                return _vignetteTexture;

            const int size = 256;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave
            };

            float center = (size - 1) * 0.5f;
            float inner = center * 0.50f;
            float fade = center - inner;
            Color[] pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - center;
                    float dy = y - center;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01((dist - inner) / fade);
                    a = a * a;
                    pixels[y * size + x] = new Color(1f, 1f, 1f, a);
                }
            }

            tex.SetPixels(pixels);
            tex.Apply(false, true);
            _vignetteTexture = tex;
            return _vignetteTexture;
        }

        private static void HideVignette()
        {
            if (_vignette != null)
                _vignette.gameObject.SetActive(false);
        }

        private static bool IsLocalPlayer(PlayerControllerB player)
        {
            if (player == null)
                return false;

            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
            return player == local || (local == null && player.IsOwner && player.isPlayerControlled);
        }
    }
}
