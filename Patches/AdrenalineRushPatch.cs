using System.Collections;
using GameNetcodeStuff;
using HarmonyLib;
using Y4NGZUpgrades.Upgrades;
using UnityEngine;
using UnityEngine.UI;

namespace Y4NGZUpgrades.Patches
{
    /// <summary>
    /// Implements all runtime behavior for the Adrenaline Rush upgrade:
    ///   * DamagePlayer postfix: trips Tier 1 effect when HP drops at/below the threshold.
    ///   * DamagePlayer prefix:  blocks all damage during Tier 2's invincibility window.
    ///   * KillPlayer prefix:    intercepts lethal enemy/trap damage at Tier 2, leaves the
    ///                           player at 1 HP and starts the invincibility window.
    ///   * LateUpdate postfix:   auto-deactivates Tier 1 when healed back above threshold,
    ///                           drives the vignette pulse, and ticks the invincibility
    ///                           timer down.
    ///   * StartGame postfix:    resets per-round insurance flag and tears down state.
    ///
    /// All effects are local-only; no networking. Speed boost is applied by overwriting
    /// PlayerControllerB.movementSpeed and restored from a snapshot taken on activation.
    /// </summary>
    [HarmonyPatch]
    internal static class AdrenalineRushPatch
    {
        // - Visual constants -
        private const float VIGNETTE_ALPHA_BASE = 0.10f;
        private const float VIGNETTE_ALPHA_AMP  = 0.06f;
        private const float VIGNETTE_PULSE_HZ   = 3f;

        // Color channels - pulled out so we can tweak danger feel without scrambling math.
        private static readonly Color VIGNETTE_TINT_NORMAL = new Color(0.80f, 0.15f, 0.05f, 0f);
        private static readonly Color VIGNETTE_TINT_INVULN = new Color(1.00f, 0.95f, 0.95f, 0f);

        // Local-only screen overlay shown while adrenaline is active.
        private static RawImage _vignette;
        private static float _pulseTime;

        // -----------------------------------------------------------------------
        // 1) DAMAGE INTERCEPT
        // -----------------------------------------------------------------------

        /// <summary>
        /// Prefix on DamagePlayer - block ALL damage during the Tier 2 invincibility
        /// window. We intentionally block fall damage too: the spec calls out that the
        /// 2s window is meant to be a brief panic-window, and blocking everything is
        /// simpler and more forgiving than partial filtering.
        /// </summary>
        [HarmonyPatch(typeof(PlayerControllerB), "DamagePlayer")]
        [HarmonyPrefix]
        private static bool PreDamagePlayer(PlayerControllerB __instance)
        {
            if (__instance == null) return true;
            if (GameNetworkManager.Instance == null) return true;
            if (__instance != GameNetworkManager.Instance.localPlayerController) return true;
            if (!AdrenalineRushUpgrade.IsInvincible) return true;
            return false; // swallow the damage event
        }

        /// <summary>
        /// Postfix on DamagePlayer - after damage is applied, decide whether to flip the
        /// Tier 1 adrenaline state on. We re-check the active flag each call so that
        /// healing-back-above-threshold (handled in LateUpdate) can re-arm the trigger
        /// for a future drop.
        /// </summary>
        [HarmonyPatch(typeof(PlayerControllerB), "DamagePlayer")]
        [HarmonyPostfix]
        private static void PostDamagePlayer(PlayerControllerB __instance)
        {
            if (__instance == null) return;
            if (GameNetworkManager.Instance == null) return;
            if (__instance != GameNetworkManager.Instance.localPlayerController) return;
            if (!AdrenalineRushUpgrade.IsUnlocked()) return;
            if (__instance.isPlayerDead) return;

            if (__instance.health > 0
                && __instance.health <= AdrenalineRushUpgrade.LOW_HP_THRESHOLD
                && !AdrenalineRushUpgrade.IsAdrenalineActive)
            {
                ActivateAdrenaline(__instance);
            }
        }

        // -----------------------------------------------------------------------
        // 2) LETHAL HIT INTERCEPT (TIER 2 INSURANCE)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Prefix on KillPlayer. If Tier 2 is owned, the death cause is enemy/trap, and
        /// insurance hasn't been used this round, we cancel the kill and start the
        /// invincibility window. Returns false to skip the underlying KillPlayer body.
        /// </summary>
        [HarmonyPatch(typeof(PlayerControllerB), "KillPlayer")]
        [HarmonyPrefix]
        private static bool PreKillPlayer(
            PlayerControllerB __instance,
            Vector3 bodyVelocity,
            bool spawnBody,
            CauseOfDeath causeOfDeath,
            int deathAnimation)
        {
            if (__instance == null) return true;
            if (GameNetworkManager.Instance == null) return true;
            if (__instance != GameNetworkManager.Instance.localPlayerController) return true;

            if (AdrenalineRushUpgrade.HasSpecialDeathProtection()
                && !AdrenalineRushUpgrade.SpecialInsuranceUsedThisRound
                && IsSpecialInstantDeath(causeOfDeath, deathAnimation))
            {
                AdrenalineRushUpgrade.SpecialInsuranceUsedThisRound = true;
                TriggerSurvivalSave(__instance, "Special threat denied - 2s of invincibility.");
                return false;
            }

            if (!AdrenalineRushUpgrade.HasInsurance()) return true;
            if (AdrenalineRushUpgrade.InsuranceUsedThisRound) return true;
            if (!IsEnemyOrTrapDeath(causeOfDeath)) return true;

            AdrenalineRushUpgrade.InsuranceUsedThisRound = true;
            TriggerSurvivalSave(__instance, "Survival insurance triggered - 2s of invincibility.");
            return false;
        }

        private static void TriggerSurvivalSave(PlayerControllerB player, string message)
        {
            player.health = 1;
            player.isPlayerDead = false;

            if (!AdrenalineRushUpgrade.IsAdrenalineActive)
                ActivateAdrenaline(player);

            StartInvincibility(player, AdrenalineRushUpgrade.INVINCIBILITY_SECONDS);

            HUDManager.Instance?.DisplayTip(
                "ADRENALINE SURGE",
                message,
                isWarning: true
            );
        }

        private static bool IsSpecialInstantDeath(CauseOfDeath cause, int deathAnimation)
        {
            switch (cause)
            {
                case CauseOfDeath.Unknown:       // Ghost Girl and several modded instant kills.
                case CauseOfDeath.Suffocation:   // Haunted mask / creepy-doll style attach kills.
                case CauseOfDeath.Strangulation:
                    return true;
                default:
                    return deathAnimation == 1;
            }
        }

        private static bool IsEnemyOrTrapDeath(CauseOfDeath cause)
        {
            // LC's enum (verified against publicized Assembly-CSharp): we accept
            // anything that originates from a creature or hostile trap, and reject
            // environmental/abandonment causes that aren't really "lethal hits".
            switch (cause)
            {
                case CauseOfDeath.Bludgeoning:
                case CauseOfDeath.Strangulation:
                case CauseOfDeath.Mauling:
                case CauseOfDeath.Gunshots:
                case CauseOfDeath.Crushing:
                case CauseOfDeath.Electrocution:
                case CauseOfDeath.Kicking:
                case CauseOfDeath.Burning:
                case CauseOfDeath.Stabbing:
                case CauseOfDeath.Blast:
                case CauseOfDeath.Fan:        // industrial fan trap
                case CauseOfDeath.Snipping:   // shears / barber
                case CauseOfDeath.Unknown:    // catch-all for modded enemies
                    return true;

                case CauseOfDeath.Gravity:    // fall damage
                case CauseOfDeath.Drowning:   // void / water
                case CauseOfDeath.Suffocation:
                case CauseOfDeath.Abandoned:  // left behind
                case CauseOfDeath.Inertia:    // velocity-based death
                default:
                    return false;
            }
        }

        // -----------------------------------------------------------------------
        // 3) LATE UPDATE - AUTO-DEACTIVATE + VIGNETTE TICK + INVULN TIMER
        // -----------------------------------------------------------------------

        [HarmonyPatch(typeof(PlayerControllerB), "LateUpdate")]
        [HarmonyPostfix]
        private static void PostLateUpdate(PlayerControllerB __instance)
        {
            if (__instance == null) return;
            if (GameNetworkManager.Instance == null) return;
            if (__instance != GameNetworkManager.Instance.localPlayerController) return;

            // Tick down invincibility regardless of whether adrenaline is on - it's a
            // separate timer and may outlast the speed-boost window.
            if (AdrenalineRushUpgrade.IsInvincible
                && Time.time >= AdrenalineRushUpgrade.InvincibilityEndTime)
            {
                AdrenalineRushUpgrade.IsInvincible = false;
            }

            if (!AdrenalineRushUpgrade.IsAdrenalineActive) return;

            // Healed past threshold or died - wind everything down.
            if (__instance.isPlayerDead || __instance.health > AdrenalineRushUpgrade.LOW_HP_THRESHOLD)
            {
                DeactivateAdrenaline(__instance);
                return;
            }

            UpdateVignette();
        }

        // -----------------------------------------------------------------------
        // 4) ROUND RESET
        // -----------------------------------------------------------------------

        // Per-round reset, dispatched by RoundLifecycle.RoundStarted. It used to postfix
        // StartOfRound.StartGame, which never runs on a client (#214).
        internal static void OnRoundStarted()
        {
            AdrenalineRushUpgrade.InsuranceUsedThisRound = false;
            AdrenalineRushUpgrade.SpecialInsuranceUsedThisRound = false;
            AdrenalineRushUpgrade.IsInvincible = false;
            AdrenalineRushUpgrade.InvincibilityEndTime = 0f;

            // Force-deactivate so we don't carry a stale speed buff across rounds. We
            // don't have a player ref here; just nuke the local visual state and the
            // flag - speed restoration will happen naturally on next ActivateAdrenaline
            // since OriginalSpeed is overwritten then.
            var player = GameNetworkManager.Instance?.localPlayerController
                ?? StartOfRound.Instance?.localPlayerController;
            DeactivateAdrenaline(player);
            AdrenalineRushUpgrade.OriginalSpeed = 0f;
            HideVignette();
        }

        // -----------------------------------------------------------------------
        // 5) ACTIVATION HELPERS
        // -----------------------------------------------------------------------

        private static void ActivateAdrenaline(PlayerControllerB player)
        {
            if (player == null) return;
            if (AdrenalineRushUpgrade.IsAdrenalineActive) return;

            AdrenalineRushUpgrade.OriginalSpeed = player.movementSpeed;
            player.movementSpeed = AdrenalineRushUpgrade.OriginalSpeed * AdrenalineRushUpgrade.SPEED_MULTIPLIER;
            AdrenalineRushUpgrade.IsAdrenalineActive = true;
        }

        private static void DeactivateAdrenaline(PlayerControllerB player)
        {
            if (!AdrenalineRushUpgrade.IsAdrenalineActive) return;

            if (player != null && AdrenalineRushUpgrade.OriginalSpeed > 0f)
            {
                player.movementSpeed = AdrenalineRushUpgrade.OriginalSpeed;
            }

            AdrenalineRushUpgrade.IsAdrenalineActive = false;
            HideVignette();
        }

        private static void StartInvincibility(PlayerControllerB player, float seconds)
        {
            AdrenalineRushUpgrade.IsInvincible = true;
            AdrenalineRushUpgrade.InvincibilityEndTime = Time.time + seconds;
            // No coroutine necessary - the LateUpdate postfix is what flips IsInvincible
            // off when the timer expires. Keeps cleanup trivial.
        }

        // -----------------------------------------------------------------------
        // 6) VIGNETTE OVERLAY
        // -----------------------------------------------------------------------

        private static void UpdateVignette()
        {
            EnsureVignette();
            if (_vignette == null) return;

            _vignette.gameObject.SetActive(true);
            _pulseTime += Time.deltaTime * VIGNETTE_PULSE_HZ;

            if (AdrenalineRushUpgrade.IsInvincible)
            {
                // Faster, brighter, whiter pulse during the insurance invuln window.
                float a = 0.18f + 0.10f * Mathf.Abs(Mathf.Sin(Time.time * 8f));
                Color c = VIGNETTE_TINT_INVULN; c.a = a;
                _vignette.color = c;
            }
            else
            {
                float a = VIGNETTE_ALPHA_BASE + VIGNETTE_ALPHA_AMP * Mathf.Sin(_pulseTime);
                Color c = VIGNETTE_TINT_NORMAL; c.a = a;
                _vignette.color = c;
            }
        }

        private static void EnsureVignette()
        {
            if (_vignette != null) return;
            if (HUDManager.Instance == null || HUDManager.Instance.playerScreenTexture == null) return;
            Canvas canvas = HUDManager.Instance.playerScreenTexture.canvas;
            if (canvas == null) return;

            GameObject go = new GameObject("AdrenalineVignette");
            go.transform.SetParent(canvas.transform, worldPositionStays: false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            _vignette = go.AddComponent<RawImage>();
            _vignette.raycastTarget = false;
            _vignette.color = new Color(0f, 0f, 0f, 0f);
        }

        private static void HideVignette()
        {
            if (_vignette != null) _vignette.gameObject.SetActive(false);
            _pulseTime = 0f;
        }
    }
}
