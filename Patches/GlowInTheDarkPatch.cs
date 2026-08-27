using GameNetcodeStuff;
using HarmonyLib;
using Y4NGZUpgrades.Upgrades;
using UnityEngine;

namespace Y4NGZUpgrades.Patches
{
    [HarmonyPatch]
    internal static class GlowInTheDarkPatch
    {
        private static GameObject _glowLight;

        [HarmonyPatch(typeof(PlayerControllerB), "Update")]
        [HarmonyPostfix]
        private static void PostPlayerUpdate(PlayerControllerB __instance)
        {
            if (__instance == null) return;
            if (!__instance.IsOwner || !__instance.isPlayerControlled) return;
            if (__instance != GameNetworkManager.Instance?.localPlayerController) return;

            if (!GlowInTheDarkUpgrade.IsUnlocked())
            {
                DestroyGlow();
                return;
            }

            if (__instance.isPlayerDead)
            {
                DestroyGlow();
                return;
            }

            // Interior-only: don't waste light outside on the moon surface
            // or up in the ship in orbit. isInsideFactory is the standard LC
            // flag for "underground / inside the dungeon".
            if (!__instance.isInsideFactory)
            {
                DestroyGlow();
                return;
            }

            EnsureGlow(__instance);
        }

        private static void EnsureGlow(PlayerControllerB player)
        {
            if (_glowLight != null)
            {
                Light existing = _glowLight.GetComponent<Light>();
                if (existing != null)
                {
                    existing.range = GlowInTheDarkUpgrade.GetRange();
                    existing.intensity = GlowInTheDarkUpgrade.GetIntensity();
                }
                return;
            }

            try
            {
                _glowLight = new GameObject("Y4NGZ_GlowInTheDark");
                _glowLight.transform.SetParent(player.transform, worldPositionStays: false);
                // Chest height so crouching doesn't put the light at eye level.
                _glowLight.transform.localPosition = new Vector3(0f, 1.5f, 0f);

                Light light = _glowLight.AddComponent<Light>();
                light.type = LightType.Point;
                light.range = GlowInTheDarkUpgrade.GetRange();
                light.intensity = GlowInTheDarkUpgrade.GetIntensity();
                light.color = new Color(0.8f, 0.9f, 1.0f);
                light.shadows = LightShadows.None;
                light.bounceIntensity = 0f;

                Plugin.Log.LogInfo("[Y4NGZUpgrades] Glow in the Dark light attached to local player.");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError($"[Y4NGZUpgrades] Failed to create Glow in the Dark light: {e}");
                if (_glowLight != null)
                {
                    Object.Destroy(_glowLight);
                    _glowLight = null;
                }
            }
        }

        private static void DestroyGlow()
        {
            if (_glowLight == null) return;
            Object.Destroy(_glowLight);
            _glowLight = null;
        }

        // Per-round reset, dispatched by RoundLifecycle.RoundStarted. It used to postfix
        // StartOfRound.StartGame, which never runs on a client (#214).
        internal static void OnRoundStarted()
        {
            DestroyGlow();
        }

        [HarmonyPatch(typeof(StartOfRound), "EndOfGame")]
        [HarmonyPostfix]
        private static void PostEndOfGame()
        {
            DestroyGlow();
        }
    }
}
