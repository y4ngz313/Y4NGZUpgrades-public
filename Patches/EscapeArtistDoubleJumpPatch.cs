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

        // F-ESCAPE-1: the owner whose air jump is still waiting for a landing. Vanilla only ever
        // clears isFallingFromJump from the tail of PlayerControllerB.PlayerJump(), which this
        // patch deliberately never starts, so the landing tail has to be mirrored here.
        private static PlayerControllerB _airJumpLandingPlayer;

        // Per-round reset, dispatched by RoundLifecycle.RoundStarted. It used to postfix
        // StartOfRound.StartGame, which never runs on a client (#214).
        internal static void OnRoundStarted()
        {
            AirJumpConsumedByPlayer.Clear();
            _airJumpLandingPlayer = null;
        }

        [HarmonyPatch(typeof(StartOfRound), "EndOfGame")]
        [HarmonyPostfix]
        private static void PostEndOfGame()
        {
            AirJumpConsumedByPlayer.Clear();
            _airJumpLandingPlayer = null;
        }

        /// <summary>
        /// F-ESCAPE-14: the sibling movement patches all drop their per-lobby state on
        /// GameNetworkManager.Disconnect; without this the consumed-jump set (keyed by
        /// NetworkObjectId, which is reused) survived into the next lobby.
        /// </summary>
        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        [HarmonyPostfix]
        private static void PostDisconnect()
        {
            AirJumpConsumedByPlayer.Clear();
            _airJumpLandingPlayer = null;
        }

        [HarmonyPatch(typeof(PlayerControllerB), "Update")]
        [HarmonyPostfix]
        private static void PostPlayerUpdate(PlayerControllerB __instance)
        {
            if (!IsLocallyControlled(__instance))
                return;

            bool grounded = __instance.thisController != null && __instance.thisController.isGrounded;
            if (__instance.isPlayerDead || grounded)
            {
                AirJumpConsumedByPlayer.Remove(__instance.NetworkObjectId);
                if (__instance.playerBodyAnimator != null)
                    __instance.playerBodyAnimator.SetBool("Jumping", false);
                FinishAirJumpLanding(__instance, grounded && !__instance.isPlayerDead);
            }
        }

        /// <summary>
        /// F-ESCAPE-1: mirror of the tail of vanilla <c>PlayerControllerB.PlayerJump()</c>
        /// (<c>isFallingFromJump = false; PlayerHitGroundEffects();</c> after the ground WaitUntil).
        /// Without it the flag stayed set after the player's first air jump: no fall damage (only
        /// PlayerHitGroundEffects calls DamagePlayer with CauseOfDeath.Gravity), no landing audio
        /// or audible noise, the FallNoJump state never played, fallValue was never reset on
        /// landing, and walk acceleration stayed pinned to the airborne 1.33/s ramp instead of
        /// ~10/carryWeight — until the next ordinary grounded jump landed.
        /// </summary>
        private static void FinishAirJumpLanding(PlayerControllerB player, bool runGroundEffects)
        {
            if (!ReferenceEquals(_airJumpLandingPlayer, player))
                return;

            _airJumpLandingPlayer = null;

            // Vanilla's own PlayerJump tail is still in flight (the player ground-jumped moments
            // before air jumping). Its WaitUntil resumes after this Update on the same landing
            // frame and runs the exact same tail, so stepping in here would double-apply fall
            // damage and the landing noise.
            if (player.jumpCoroutine != null)
                return;

            // This postfix runs inside PlayerControllerB.Update, and Update's grounded branch
            // leaves fallValue alone while isFallingFromJump is set, so the accumulated fall
            // speed the damage tiers read is still intact here.
            if (runGroundEffects && player.isFallingFromJump)
                player.PlayerHitGroundEffects();

            player.isFallingFromJump = false;
            player.ResetFallGravity();
        }

        [HarmonyPatch(typeof(PlayerControllerB), "Jump_performed")]
        [HarmonyPrefix]
        private static bool PreJumpPerformed(PlayerControllerB __instance)
        {
            // F-ESCAPE-11: a jump cancels an Escape Artist slide (the canonical slide-into-jump
            // chain) and falls through to vanilla, which makes ShouldExitSlide's "jump" exit
            // reason reachable for the first time.
            if (PanicSlidePatch.TryCancelSlideForJump(__instance))
                return true;

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
            // F-ESCAPE-1: arm the landing tail that vanilla's PlayerJump() coroutine would
            // otherwise own (see FinishAirJumpLanding).
            _airJumpLandingPlayer = player;
            player.sprintMeter = Mathf.Clamp01(player.sprintMeter - PanicSlideUpgrade.AIR_JUMP_STAMINA_COST);
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
