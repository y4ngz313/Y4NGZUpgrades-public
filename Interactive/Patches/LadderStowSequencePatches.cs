using System;
using System.Collections;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;

namespace Y4NGZUpgrades.Interactive.Patches
{
    /// <summary>
    /// Ladders accept any weapon, and the put-away plays BEFORE the climb starts (#287).
    ///
    /// VANILLA FLOW, as decompiled from Assembly-CSharp (v72), because the patch points are not
    /// where the symptom suggests:
    ///
    ///   * <see cref="InteractTrigger.Interact"/> contains NO two-handed test at all. A ladder
    ///     rejects a two-handed carry one level up, in the CALLER:
    ///       - <c>PlayerControllerB.Interact_performed</c>:
    ///           <c>(!twoHanded || (hoveringOverTrigger.twoHandedItemAllowed
    ///             &amp;&amp; !hoveringOverTrigger.specialCharacterAnimation))</c>
    ///       - <c>PlayerControllerB.ClickHoldInteraction</c>:
    ///           <c>(twoHanded &amp;&amp; !hoveringOverTrigger.twoHandedItemAllowed)</c>
    ///     Ladder triggers ship with <c>twoHandedItemAllowed = false</c>, so a two-handed carry
    ///     never reaches <c>Interact</c> and the climb is impossible rather than merely ugly.
    ///     <c>InteractTriggerUseConditionsMet</c> — the other gate on that line — does not look at
    ///     hands at all, so relaxing <c>twoHanded</c> for the duration of the caller is the whole
    ///     fix for the "blocked entirely" half.
    ///   * The hands-full PROMPT is a third, separate site:
    ///     <c>SetHoverTipAndCurrentInteractTrigger</c> writes <c>"[Hands full]"</c> into
    ///     <c>cursorTip</c>, but only while <c>isHoldingInteract</c>. It is cosmetic and is
    ///     repaired by postfix rather than by another relax scope.
    ///   * The climb itself is <c>InteractTrigger.ladderClimbAnimation</c>, started as
    ///     <c>useLadderCoroutine = StartCoroutine(ladderClimbAnimation(component))</c> at the tail
    ///     of <c>Interact</c>. Everything up to its first <c>yield return null</c> runs
    ///     SYNCHRONOUSLY inside that call — including <c>inSpecialInteractAnimation = true</c>,
    ///     the <c>EnterLadder</c> trigger and <c>thisController.enabled = false</c>. That is what
    ///     makes the "did the climb actually take?" check below a plain field read on the line
    ///     after <c>Interact</c> returns, with no frame of slack. <c>isClimbingLadder</c> only goes
    ///     true later, after the <c>animationWaitTime</c> snap-to-ladder lerp.
    ///   * Re-interacting while <c>usingLadder</c> is the LET-GO path
    ///     (<c>Interact</c> -> <c>CancelLadderAnimation</c>), and climbing down from the top uses
    ///     the SAME trigger and the same <c>Interact</c> entry — vanilla picks the end by clamping
    ///     <c>ladderPlayerPositionNode</c> between the bottom and top nodes. So one prefix covers
    ///     up, down and dismount, provided it declines to interfere with the let-go case.
    ///
    /// WHAT THIS ADDS: on the local owner's first ladder interact while holding something that
    /// wants putting away, the stow is opened, the vanilla climb start is suppressed for that
    /// frame, and the real <c>Interact</c> is replayed <see cref="StowSeconds"/> later from the
    /// persistent runner. Remote clients need nothing: the stow rides
    /// <see cref="GrabbableObject.PocketItem"/>, which is already replicated.
    ///
    /// REFCOUNT PAIRING (revised #288). Two rules, and they are not the same rule:
    ///
    ///   * <b>Happy path — the reason TAG closes everything.</b> This class opens its stow under
    ///     the SAME string <see cref="HeldItemStowPatches"/> uses. That patch's edge detector
    ///     <c>Begin</c>s a second, nested token on the rising edge of
    ///     <c>inSpecialInteractAnimation</c> one Update later, and
    ///     <see cref="HeldItemStowService.End(PlayerControllerB, string)"/> releases EVERY token
    ///     carrying the tag — so the dismount's falling edge closes this class's token AND the edge
    ///     detector's, and the item comes back exactly once. Opening under a private reason would
    ///     instead strand a refcount of 1 after every climb.
    ///   * <b>Abort paths — this class releases only its OWN token.</b> Never <c>End(tag)</c>: the
    ///     deferred start aborts precisely when the player was pulled into ANOTHER special
    ///     interaction (a teleport, an enemy grab, someone else's animation), which is exactly the
    ///     state where the edge detector is already holding its own token under the shared tag. A
    ///     tag-wide release there drops that holder's token too and re-equips the weapon in the
    ///     middle of the animation that stole the player. So every abort goes through
    ///     <see cref="HeldItemStowService.Release(PlayerControllerB, long)"/> with the token this
    ///     class obtained, and the edge detector still restores on ITS own falling edge.
    ///
    /// The two rules compose: the token this class holds carries the shared tag, so a happy-path
    /// <c>End</c> from the edge detector still releases it, while an abort cannot reach anyone
    /// else's. The only double-release risk would be aborting a sequence whose climb DID start,
    /// which cannot happen — the coroutine releases on abort or hands off, never both, and
    /// <c>Release</c> is idempotent for an already-freed token besides.
    /// </summary>
    [HarmonyPatch]
    internal static class LadderStowSequencePatches
    {
        /// <summary>Must match <see cref="HeldItemStowPatches"/> — see the refcount note above.</summary>
        private const string SpecialInteractReason = "special-interact";

        /// <summary>Put-away lead-in, matching the grenade throw's measured value.</summary>
        private const float StowSeconds = 0.28f;

        /// <summary>Backstop only. The falling edge of the climb is what normally ends the stow;
        /// this covers the window between Begin and the climb actually starting.</summary>
        private const float StowTimeoutSeconds = 6f;

        private const float MaxLadderHorizontalDistance = 8f;
        private const string HandsFullTip = "[Hands full]";

        /// <summary>Slack on top of <see cref="StowSeconds"/> before a pending sequence is presumed
        /// dead (#288). The lead-in is a single WaitForSeconds on the persistent runner, so anything
        /// this far past its deadline means the routine was destroyed or StopAllCoroutine'd and its
        /// exits never ran — without this, <c>_pendingRoutine</c> stays non-null forever and every
        /// later ladder interact is silently swallowed.</summary>
        private const float PendingStaleSlackSeconds = 2f;

        /// <summary>True while a caller's <c>twoHanded</c> is temporarily forced false.</summary>
        private static bool _relaxingTwoHandedGate;

        /// <summary>The player whose <c>twoHanded</c> the open relax scope forced false, and what
        /// the flag read BEFORE it did. The stow decision needs the pre-relax value: by the time
        /// <see cref="StowBeforeLadderPrefix"/> runs, <c>player.twoHanded</c> is false on every path
        /// that reaches it, so testing it there is dead code (#288).</summary>
        private static PlayerControllerB _relaxedPlayer;
        private static bool _relaxedOriginalTwoHanded;

        /// <summary>True only for this class's own replayed <c>Interact</c> call, so the prefix
        /// below lets it through instead of scheduling a second delay.</summary>
        private static bool _replayingLadderInteract;

        private static Coroutine _pendingRoutine;
        private static PlayerControllerB _pendingPlayer;
        private static InteractTrigger _pendingTrigger;

        /// <summary>This class's OWN stow token for the pending sequence — see the abort rule in the
        /// class comment. Zero when nothing is pending.</summary>
        private static long _pendingToken;

        /// <summary>Realtime stamp of the pending sequence, for the stranded-routine fallback.</summary>
        private static float _pendingStartedAtRealtime;

        // -----------------------------------------------------------------------------------
        // 1. Gate relaxation: let a two-handed carry reach InteractTrigger.Interact at all.
        // -----------------------------------------------------------------------------------

        [HarmonyPatch(typeof(PlayerControllerB), "Interact_performed")]
        [HarmonyPrefix]
        private static void RelaxLadderGateForTapPrefix(
            PlayerControllerB __instance,
            out TwoHandedRelaxScope __state)
        {
            __state = BeginRelaxScope(__instance);
        }

        [HarmonyPatch(typeof(PlayerControllerB), "Interact_performed")]
        [HarmonyFinalizer]
        private static Exception RelaxLadderGateForTapFinalizer(
            Exception __exception,
            TwoHandedRelaxScope __state)
        {
            __state?.Dispose();
            return __exception;
        }

        [HarmonyPatch(typeof(PlayerControllerB), "ClickHoldInteraction")]
        [HarmonyPrefix]
        private static void RelaxLadderGateForHoldPrefix(
            PlayerControllerB __instance,
            out TwoHandedRelaxScope __state)
        {
            __state = BeginRelaxScope(__instance);
        }

        [HarmonyPatch(typeof(PlayerControllerB), "ClickHoldInteraction")]
        [HarmonyFinalizer]
        private static Exception RelaxLadderGateForHoldFinalizer(
            Exception __exception,
            TwoHandedRelaxScope __state)
        {
            __state?.Dispose();
            return __exception;
        }

        /// <summary>
        /// <c>Interact_performed</c> calls <c>BeginGrabObject</c> BEFORE the trigger gate, and that
        /// method has its own <c>|| twoHanded ||</c> early-return. Relaxing the flag for the whole
        /// caller would therefore also let a two-handed carry scoop a prop off the floor while
        /// looking at a ladder. Putting the real flag back for the duration of the grab keeps that
        /// refusal — and keeps
        /// <see cref="Y4NGZUpgrades.Patches.ScannerTransporterPatch"/>'s own two-handed relax, which
        /// reads <c>player.twoHanded</c>, seeing the value it saw before this class existed.
        /// Priority.First so this restore lands before that patch's prefix reads the flag.
        /// </summary>
        [HarmonyPatch(typeof(PlayerControllerB), "BeginGrabObject")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void RestoreTwoHandedForGrabPrefix(PlayerControllerB __instance, out bool __state)
        {
            __state = false;
            if (!_relaxingTwoHandedGate || __instance == null || __instance.twoHanded)
                return;

            GrabbableObject held = __instance.currentlyHeldObjectServer;
            if (held == null || held.itemProperties == null || !held.itemProperties.twoHanded)
                return;

            __instance.twoHanded = true;
            __state = true;
        }

        [HarmonyPatch(typeof(PlayerControllerB), "BeginGrabObject")]
        [HarmonyFinalizer]
        [HarmonyPriority(Priority.Last)]
        private static Exception RestoreTwoHandedForGrabFinalizer(
            Exception __exception,
            PlayerControllerB __instance,
            bool __state)
        {
            if (__state && __instance != null)
                __instance.twoHanded = false;
            return __exception;
        }

        /// <summary>
        /// Cosmetic half of the gate: vanilla writes "[Hands full]" over the ladder's own prompt
        /// while the interact key is held. The climb is allowed now, so the prompt has to say so.
        /// </summary>
        [HarmonyPatch(typeof(PlayerControllerB), "SetHoverTipAndCurrentInteractTrigger")]
        [HarmonyPostfix]
        private static void RepairLadderHoverTipPostfix(PlayerControllerB __instance)
        {
            if (!CanRelaxLadderGate(__instance))
                return;
            if (__instance.cursorTip == null)
                return;
            if (!string.Equals(__instance.cursorTip.text, HandsFullTip, StringComparison.Ordinal))
                return;

            InteractTrigger trigger = __instance.hoveringOverTrigger;
            __instance.cursorTip.text = string.IsNullOrEmpty(trigger.holdTip)
                ? trigger.hoverTip
                : trigger.holdTip;
        }

        // -----------------------------------------------------------------------------------
        // 2. Sequencing: stow first, climb 0.28 s later.
        // -----------------------------------------------------------------------------------

        [HarmonyPatch(typeof(InteractTrigger), "Interact")]
        [HarmonyPrefix]
        private static bool StowBeforeLadderPrefix(InteractTrigger __instance, Transform playerTransform)
        {
            // Our own replayed call: this is the climb we scheduled, so run vanilla.
            if (_replayingLadderInteract)
                return true;
            if (__instance == null || !__instance.isLadder || !__instance.interactable)
                return true;
            // usingLadder is the LET-GO path and isPlayingSpecialAnimation is somebody else's
            // animation; both must reach vanilla untouched.
            if (__instance.usingLadder || __instance.isPlayingSpecialAnimation)
                return true;
            if (playerTransform == null)
                return true;

            PlayerControllerB player = playerTransform.GetComponent<PlayerControllerB>();
            if (player == null || player != GameNetworkManager.Instance?.localPlayerController)
                return true;
            if (player.isPlayerDead || player.inSpecialInteractAnimation || player.isClimbingLadder)
                return true;

            // Interact spam inside the 0.28 s window: swallow it. Letting it through would start a
            // second lead-in, and letting it reach vanilla would start the climb early — which is
            // the ordering this whole class exists to prevent.
            if (_pendingRoutine != null && !TryReapStrandedPending())
                return false;

            // Already put away by another owner (an ability's stow, a previous special interact).
            // Nothing to sequence; the edge detector will hold it down for the climb.
            if (HeldItemStowService.IsStowed(player))
                return true;

            GrabbableObject item = player.currentlyHeldObjectServer;
            if (item == null)
                return true;
            // Key on the RUNTIME flags, not on weapon identity: #285 lets config make any weapon
            // two-handed, and a plain vanilla two-handed prop deserves the same courtesy. The
            // two-handed half reads the PRE-RELAX value: this prefix runs inside the relax scope,
            // which has already forced player.twoHanded false on every path that reaches here, so
            // reading the live flag would never be true (#288).
            if (!HeldItemStowService.ShouldStow(item) && !WasTwoHandedBeforeRelax(player))
                return true;

            HeldItemStowHandle handle = HeldItemStowService.Begin(
                player,
                SpecialInteractReason,
                StowTimeoutSeconds,
                disableItemActions: true,
                force: true);
            if (!handle.IsActive)
            {
                // Service declined (no runner, stow threw). Vanilla behavior, unchanged.
                return true;
            }

            _pendingPlayer = player;
            _pendingTrigger = __instance;
            _pendingToken = handle.Token;
            _pendingStartedAtRealtime = Time.realtimeSinceStartup;
            _pendingRoutine = Y4NGZPersistentRunner.Run(
                StartLadderAfterStow(player, __instance, handle.Token));
            if (_pendingRoutine == null)
            {
                // No persistent host means no delay and no janitor coverage, so nothing may stay
                // open: give OUR token straight back and let this frame's interact run vanilla.
                ClearPending();
                HeldItemStowService.Release(player, handle.Token);
                return true;
            }

            Plugin.Log?.LogInfo(
                "[Ladder] stow-first sequence armed on '" + __instance.gameObject.name +
                "'; climb starts in " + StowSeconds.ToString("0.00") + "s.");
            return false;
        }

        private static IEnumerator StartLadderAfterStow(
            PlayerControllerB player, InteractTrigger trigger, long stowToken)
        {
            yield return new WaitForSeconds(StowSeconds);

            // Past this point there are no more yields, so every exit below is reached even if the
            // routine is the last thing the runner does this frame.
            ClearPending();

            if (!IsLadderStartStillValid(player, trigger))
            {
                // OUR token only. The commonest reason to land here is the player having been pulled
                // into another special interaction, where the edge detector already holds its own
                // token under the same tag — End(tag) would release that one too (#288).
                Plugin.Log?.LogInfo("[Ladder] stow-first sequence aborted before the climb; restoring item.");
                HeldItemStowService.Release(player, stowToken);
                yield break;
            }

            bool started;
            _replayingLadderInteract = true;
            try
            {
                trigger.Interact(player.thisPlayerBody);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Ladder] deferred climb start threw safely: " + e.Message);
            }
            finally
            {
                _replayingLadderInteract = false;
            }

            // ladderClimbAnimation sets inSpecialInteractAnimation synchronously, so this is a
            // truthful "did it take?" the instant Interact returns. It does NOT take when the
            // ladder position is obstructed or the player is in the hangar ship room.
            started = player.inSpecialInteractAnimation || player.isClimbingLadder;
            if (!started)
            {
                // Same rule as the abort above: release only what this class opened. The climb did
                // not start, so the edge detector never fired and this is the last live token —
                // which is what actually brings the item back.
                Plugin.Log?.LogInfo("[Ladder] deferred climb declined by vanilla; restoring item.");
                HeldItemStowService.Release(player, stowToken);
            }

            // Started: hand off. The dismount's falling edge calls End(tag), which releases this
            // token along with the edge detector's, and the item comes back exactly once.
        }

        /// <summary>
        /// The routine's host can die (scene teardown, a StopAllCoroutines from elsewhere) inside
        /// the 0.28 s lead-in, and none of the coroutine's exits then run — leaving
        /// <c>_pendingRoutine</c> non-null forever and every later ladder interact swallowed by the
        /// spam guard, silently, until death or round-end (#288). A pending sequence older than the
        /// lead-in plus slack is therefore presumed dead: it is cleared, this class's token is
        /// released if it is somehow still live, and the caller proceeds with the new interact.
        /// </summary>
        /// <returns>True when a stranded sequence was reaped and the caller may continue.</returns>
        private static bool TryReapStrandedPending()
        {
            if (Time.realtimeSinceStartup - _pendingStartedAtRealtime
                <= StowSeconds + PendingStaleSlackSeconds)
            {
                return false;
            }

            PlayerControllerB player = _pendingPlayer;
            long token = _pendingToken;
            string ladder = _pendingTrigger != null ? _pendingTrigger.gameObject.name : "unknown";
            ClearPending();

            if (token != 0L && HeldItemStowService.IsTokenLive(player, token))
                HeldItemStowService.Release(player, token);

            // One line per stranded sequence, not per interact: the clear above means the next
            // interact takes the normal path.
            Plugin.Log?.LogWarning(
                "[Ladder] pending stow-first sequence on '" + ladder + "' was stranded (its host " +
                "went away mid lead-in); reaped it and letting this interact through.");
            return true;
        }

        /// <summary>
        /// Re-validation for the deferred start. Covers dying, being pulled into another special
        /// animation, the ladder being destroyed / disabled / taken by someone else, losing the
        /// held item, and walking away during the lead-in.
        /// </summary>
        private static bool IsLadderStartStillValid(PlayerControllerB player, InteractTrigger trigger)
        {
            if (player == null || trigger == null)
                return false;
            if (player.isPlayerDead || !player.isPlayerControlled || player.thisPlayerBody == null)
                return false;
            if (player.inSpecialInteractAnimation || player.isClimbingLadder || player.isInHangarShipRoom)
                return false;
            if (player.inAnimationWithEnemy != null)
                return false;
            if (trigger.gameObject == null || !trigger.gameObject.activeInHierarchy)
                return false;
            if (!trigger.interactable || trigger.usingLadder || trigger.isPlayingSpecialAnimation)
                return false;
            if (trigger.ladderPlayerPositionNode == null ||
                trigger.ladderHorizontalPosition == null ||
                trigger.bottomOfLadderPosition == null ||
                trigger.topOfLadderPosition == null)
            {
                return false;
            }

            // Horizontal only: the lead-in is short enough that vertical drift is a fall, which the
            // ladder's own linecast handles, while walking off is what this guards.
            Vector3 a = player.thisPlayerBody.position;
            Vector3 b = trigger.ladderHorizontalPosition.position;
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return (dx * dx) + (dz * dz) <=
                MaxLadderHorizontalDistance * MaxLadderHorizontalDistance;
        }

        // -----------------------------------------------------------------------------------
        // 3. Teardown.
        // -----------------------------------------------------------------------------------

        [HarmonyPatch(typeof(PlayerControllerB), "KillPlayer")]
        [HarmonyPrefix]
        private static void CancelPendingOnDeath(PlayerControllerB __instance, bool __runOriginal)
        {
            if (!__runOriginal) return;
            if (__instance != null && __instance == _pendingPlayer)
                CancelPendingSequence("death");
        }

        [HarmonyPatch(typeof(StartOfRound), "EndOfGame")]
        [HarmonyPostfix]
        private static void CancelPendingOnRoundEnd() => CancelPendingSequence("round-end");

        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        [HarmonyPostfix]
        private static void CancelPendingOnDisconnect() => CancelPendingSequence("disconnect");

        private static void CancelPendingSequence(string reason)
        {
            if (_pendingRoutine == null)
                return;

            Y4NGZPersistentRunner.Stop(_pendingRoutine);
            _relaxingTwoHandedGate = false;
            _replayingLadderInteract = false;
            string ladder = _pendingTrigger != null ? _pendingTrigger.gameObject.name : "unknown";
            ClearPending();
            Plugin.Log?.LogInfo(
                "[Ladder] pending stow-first sequence on '" + ladder + "' cancelled: " + reason);
            // HeldItemStowService's own KillPlayer / AbortAll hooks own the stow itself; ending it
            // here as well would be a second release of the same tag.
        }

        private static void ClearPending()
        {
            _pendingRoutine = null;
            _pendingPlayer = null;
            _pendingTrigger = null;
            _pendingToken = 0L;
            _pendingStartedAtRealtime = 0f;
        }

        /// <summary>
        /// Was this player two-handed before the relax scope forced the flag false? Inside the scope
        /// the live flag is always false, so the scope's remembered value is the only truthful
        /// answer; outside it the live flag is the answer.
        /// </summary>
        private static bool WasTwoHandedBeforeRelax(PlayerControllerB player)
        {
            if (player == null)
                return false;
            if (player.twoHanded)
                return true;
            return _relaxingTwoHandedGate && _relaxedPlayer == player && _relaxedOriginalTwoHanded;
        }

        // -----------------------------------------------------------------------------------
        // Shared predicate + scope.
        // -----------------------------------------------------------------------------------

        private static TwoHandedRelaxScope BeginRelaxScope(PlayerControllerB player)
        {
            return CanRelaxLadderGate(player) ? new TwoHandedRelaxScope(player) : null;
        }

        /// <summary>
        /// "Would vanilla refuse this ladder purely because the hands are full?" Runtime flags
        /// only — no weapon identity — so a config-promoted two-handed weapon (#285) and a plain
        /// vanilla two-handed prop take the same path.
        /// </summary>
        private static bool CanRelaxLadderGate(PlayerControllerB player)
        {
            if (player == null || !player.twoHanded)
                return false;
            if (player != GameNetworkManager.Instance?.localPlayerController)
                return false;
            if (player.isPlayerDead || player.isClimbingLadder)
                return false;

            InteractTrigger trigger = player.hoveringOverTrigger;
            return trigger != null &&
                trigger.isLadder &&
                trigger.interactable &&
                !trigger.twoHandedItemAllowed;
        }

        /// <summary>Scope-sets <c>twoHanded</c> false and restores from the item actually held on
        /// exit — never from a remembered "true", because the stow legitimately clears the flag
        /// inside the scope and forcing it back would leave the player two-handed with empty
        /// hands.</summary>
        internal sealed class TwoHandedRelaxScope : IDisposable
        {
            private readonly PlayerControllerB _player;
            private bool _disposed;

            internal TwoHandedRelaxScope(PlayerControllerB player)
            {
                _player = player;
                // Remembered for the stow decision only — the restore below still recomputes from
                // the item actually held, never from this value.
                _relaxedPlayer = player;
                _relaxedOriginalTwoHanded = player.twoHanded;
                _player.twoHanded = false;
                _relaxingTwoHandedGate = true;
            }

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                _relaxingTwoHandedGate = false;
                _relaxedPlayer = null;
                _relaxedOriginalTwoHanded = false;
                if (_player == null)
                    return;

                GrabbableObject held = _player.currentlyHeldObjectServer;
                _player.twoHanded = held != null &&
                    held.itemProperties != null &&
                    held.itemProperties.twoHanded;
            }
        }
    }
}
