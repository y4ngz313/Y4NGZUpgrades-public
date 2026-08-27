using System.Collections.Generic;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Patches
{
    /// <summary>
    /// Tier 3 of Escape Artist grants one owner-authoritative air jump. Vanilla
    /// movement synchronization already broadcasts PlayerJumpedServerRpc, so this
    /// patch only supplies the otherwise-rejected airborne jump input.
    /// </summary>
    [HarmonyPatch]
    internal static class EscapeArtistDoubleJumpPatch
    {
        private static readonly HashSet<ulong> AirJumpConsumedByPlayer = new HashSet<ulong>();

        // Per-round reset, dispatched by RoundLifecycle.RoundStarted. It used to postfix
        // StartOfRound.StartGame, which never runs on a client (#214).
        internal static void OnRoundStarted()
        {
            AirJumpConsumedByPlayer.Clear();
        }

        [HarmonyPatch(typeof(StartOfRound), "EndOfGame")]
        [HarmonyPostfix]
        private static void PostEndOfGame()
        {
            AirJumpConsumedByPlayer.Clear();
        }

        [HarmonyPatch(typeof(PlayerControllerB), "Update")]
        [HarmonyPostfix]
        private static void PostPlayerUpdate(PlayerControllerB __instance)
        {
            if (!IsLocallyControlled(__instance))
                return;

            if (__instance.isPlayerDead
                || (__instance.thisController != null && __instance.thisController.isGrounded))
            {
                AirJumpConsumedByPlayer.Remove(__instance.NetworkObjectId);
                if (__instance.playerBodyAnimator != null)
                    __instance.playerBodyAnimator.SetBool("Jumping", false);
            }
        }

        [HarmonyPatch(typeof(PlayerControllerB), "Jump_performed")]
        [HarmonyPrefix]
        private static bool PreJumpPerformed(PlayerControllerB __instance)
        {
            if (!CanUseAirJump(__instance))
                return true;

            UseAirJump(__instance);
            return false;
        }

        private static bool CanUseAirJump(PlayerControllerB player)
        {
            if (!IsLocallyControlled(player) || !PanicSlideUpgrade.HasDoubleJump())
                return false;
            if (player.thisController == null || player.thisController.isGrounded)
                return false;
            if (AirJumpConsumedByPlayer.Contains(player.NetworkObjectId))
                return false;
            if (player.isPlayerDead || player.isClimbingLadder || player.inSpecialInteractAnimation
                || player.isTypingChat || player.inTerminalMenu || player.isExhausted
                || player.isCrouching || player.isPlayerSliding || player.isUnderwater)
                return false;
            if (player.quickMenuManager != null && player.quickMenuManager.isMenuOpen)
                return false;
            if (player.isMovementHindered > 0 || PanicSlidePatch.IsSlideActive() || LedgeMantlePatch.IsMantleActive)
                return false;

            return true;
        }

        private static void UseAirJump(PlayerControllerB player)
        {
            AirJumpConsumedByPlayer.Add(player.NetworkObjectId);
            player.fallValue = player.jumpForce;
            player.fallValueUncapped = player.jumpForce;
            player.isFallingFromJump = true;
            player.sprintMeter = Mathf.Clamp01(player.sprintMeter - 0.08f);
            if (player.sprintMeterUI != null)
                player.sprintMeterUI.fillAmount = player.sprintMeter;

            if (player.playerBodyAnimator != null)
                player.playerBodyAnimator.SetBool("Jumping", true);

            StartOfRound.Instance?.PlayerJumpEvent?.Invoke(player);
            PlayLocalJumpAudio(player);
            if (StartOfRound.Instance != null && StartOfRound.Instance.connectedPlayersAmount != 0)
                player.PlayerJumpedServerRpc();
        }

        private static void PlayLocalJumpAudio(PlayerControllerB player)
        {
            if (player == null || player.movementAudio == null || StartOfRound.Instance == null)
                return;

            AudioClip clip = StartOfRound.Instance.playerJumpSFX;
            if (StartOfRound.Instance.unlockablesList != null
                && player.currentSuitID >= 0
                && player.currentSuitID < StartOfRound.Instance.unlockablesList.unlockables.Count)
            {
                AudioClip suitClip = StartOfRound.Instance.unlockablesList.unlockables[player.currentSuitID].jumpAudio;
                if (suitClip != null)
                    clip = suitClip;
            }

            if (clip != null)
                player.movementAudio.PlayOneShot(clip);
        }

        private static bool IsLocallyControlled(PlayerControllerB player)
        {
            return player != null
                && player.IsOwner
                && player.isPlayerControlled
                && player == GameNetworkManager.Instance?.localPlayerController;
        }
    }
}
