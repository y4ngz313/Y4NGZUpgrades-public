using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.InputSystem;
using Y4NGZInteractions.InteractionAnimationApi;

namespace Y4NGZUpgrades.Interactive
{
    /// <summary>
    /// Release token for one <see cref="HeldItemStowService.Begin"/> call. Disposing is
    /// idempotent and only the last live token restores the item.
    /// </summary>
    internal readonly struct HeldItemStowHandle : IDisposable
    {
        private readonly PlayerControllerB _player;
        private readonly long _token;

        internal HeldItemStowHandle(PlayerControllerB player, long token)
        {
            _player = player;
            _token = token;
        }

        /// <summary>True while this token still holds the stow open — false for a declined
        /// handle, a disposed one, and one the janitor already abandoned.</summary>
        internal bool IsActive => HeldItemStowService.IsTokenLive(_player, _token);

        /// <summary>Raw token, for <see cref="HeldItemStowApi"/> — a reflecting caller in another
        /// assembly cannot hold this struct, so it holds the number instead.</summary>
        internal long Token => _token;

        public void Dispose()
        {
            if (_token != 0L)
                HeldItemStowService.Release(_player, _token);
        }
    }

    /// <summary>
    /// Puts the held item away exactly like a hotbar switch for the duration of an animated
    /// ability or a special interaction, then re-equips it. Works for the local player and for
    /// remote observers, because every caller rides its own already-replicated code path — the
    /// service adds no network traffic of its own.
    ///
    /// Ordering contract for ability call sites: stow FIRST, then start the ability animation.
    /// The stow itself hands the body back to vanilla — see <see cref="ReleaseInteractionBody"/> —
    /// so a caller's own <c>LCInteractionAnimationAPI</c> request finds the body and local-arms
    /// leases free. The weapon's presentation restarts on its own once the item is restored.
    /// </summary>
    internal static class HeldItemStowService
    {
        private const int ItemOnlySlotIndex = 50;

        /// <summary>How far the janitor pushes a stow's expiry out each tick the player spends in a
        /// special interact (#288) — long enough that one frame of flag flicker cannot expire a
        /// stow, short enough that the backstop resumes counting as soon as the climb ends.</summary>
        private const float SpecialInteractGraceSeconds = 3f;

        private static readonly Dictionary<PlayerControllerB, StowEntry> Entries =
            new Dictionary<PlayerControllerB, StowEntry>();
        private static readonly List<PlayerControllerB> ScratchPlayers = new List<PlayerControllerB>();
        private static readonly InteractionAnimationPresentationKind[] BodyPresentationKinds =
        {
            InteractionAnimationPresentationKind.BodyWorld,
            InteractionAnimationPresentationKind.DedicatedLocalViewmodel,
        };
        private static readonly string[] SuppressedActionNames =
        {
            "ActivateItem",
            "ItemSecondaryUse",
            "ItemTertiaryUse",
            "SwitchItem",
            "UseUtilitySlot",
            "DiscardHeldObject",
        };

        private static long _nextToken = 1L;

        /// <summary>
        /// Stows the player's held item and returns a release token. Never returns a token that
        /// must be checked for null: a decline (nothing held, predicate says no) yields an inert
        /// handle whose Dispose is a no-op. Refcounted per player — the snapshot is taken on the
        /// 0-to-1 edge and the item is restored on the 1-to-0 edge.
        /// </summary>
        /// <param name="reason">Short tag used by the janitor's timeout warning and by
        /// <see cref="End(PlayerControllerB, string)"/>.</param>
        /// <param name="safetyTimeoutSeconds">Backstop: a caller that never releases loses the
        /// stow after this long and the item comes back.</param>
        /// <param name="disableItemActions">Suppress item input for the local player only.</param>
        /// <param name="force">Stow whatever is held instead of consulting
        /// <see cref="ShouldStow"/>.</param>
        internal static HeldItemStowHandle Begin(
            PlayerControllerB player,
            string reason,
            float safetyTimeoutSeconds = 15f,
            bool disableItemActions = false,
            bool force = false)
        {
            if (player == null)
                return default;

            // The expiry janitor lives on the persistent runner (#211). Opening a stow without a
            // janitor to reap it is the wedge this service exists to prevent.
            Y4NGZPersistentRunner.Ensure();

            string tag = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason;
            float expiresAt = Time.time + Mathf.Max(0.25f, safetyTimeoutSeconds);

            if (Entries.TryGetValue(player, out StowEntry entry))
            {
                long nested = NextToken();
                entry.Tokens[nested] = tag;
                entry.ExpiresAt = Mathf.Max(entry.ExpiresAt, expiresAt);
                if (disableItemActions)
                    ApplyInputSuppression(player, entry);
                return new HeldItemStowHandle(player, nested);
            }

            GrabbableObject item = player.currentlyHeldObjectServer;
            if (item == null)
                return default;
            if (!force && !ShouldStow(item))
                return default;

            entry = new StowEntry
            {
                Item = item,
                Slot = player.currentItemSlot,
                EquippedUsableItemQE = player.equippedUsableItemQE,
                ExpiresAt = expiresAt,
                Reason = tag,
            };

            if (!TryStow(player, entry))
                return default;

            long token = NextToken();
            entry.Tokens[token] = tag;
            Entries[player] = entry;
            if (disableItemActions)
                ApplyInputSuppression(player, entry);
            return new HeldItemStowHandle(player, token);
        }

        /// <summary>Releases every token opened under <paramref name="reason"/>. Idempotent.</summary>
        internal static void End(PlayerControllerB player, string reason)
        {
            if (player == null || !Entries.TryGetValue(player, out StowEntry entry))
                return;

            string tag = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason;
            var doomed = new List<long>(entry.Tokens.Count);
            foreach (KeyValuePair<long, string> pair in entry.Tokens)
            {
                if (string.Equals(pair.Value, tag, StringComparison.Ordinal))
                    doomed.Add(pair.Key);
            }

            for (int i = 0; i < doomed.Count; i++)
                entry.Tokens.Remove(doomed[i]);

            if (entry.Tokens.Count == 0)
                Finish(player, entry, restore: true);
        }

        internal static bool IsStowed(PlayerControllerB player)
        {
            return player != null && Entries.ContainsKey(player);
        }

        /// <summary>
        /// Round teardown / disconnect. Restores where the player and item are still valid and
        /// drops the entry otherwise; suppressed input is always re-enabled.
        /// </summary>
        internal static void AbortAll(string reason)
        {
            if (Entries.Count == 0)
                return;

            CollectPlayers();
            for (int i = 0; i < ScratchPlayers.Count; i++)
            {
                PlayerControllerB player = ScratchPlayers[i];
                if (Entries.TryGetValue(player, out StowEntry entry))
                    Finish(player, entry, restore: true);
            }

            Entries.Clear();
            ScratchPlayers.Clear();
            if (!string.IsNullOrWhiteSpace(reason))
                Plugin.Log?.LogInfo("[HeldItemStow] aborted all stows: " + reason);
        }

        /// <summary>Drops a player's stow without restoring — the item stays in its slot so the
        /// vanilla death/drop paths can move it.</summary>
        internal static void Clear(PlayerControllerB player)
        {
            if (player == null || !Entries.TryGetValue(player, out StowEntry entry))
                return;

            Finish(player, entry, restore: false);
        }

        /// <summary>Janitor: expires timeouts, drops dead players, abandons stows the world moved on from.</summary>
        internal static void Tick()
        {
            if (Entries.Count == 0)
                return;

            CollectPlayers();
            for (int i = 0; i < ScratchPlayers.Count; i++)
            {
                PlayerControllerB player = ScratchPlayers[i];
                if (!Entries.TryGetValue(player, out StowEntry entry))
                    continue;

                if (player == null ||
                    player.isPlayerDead ||
                    !player.isPlayerControlled ||
                    player.gameObject == null)
                {
                    Finish(player, entry, restore: false);
                    continue;
                }

                // The world moved on: the player equipped something else, switched slots, or the
                // item is gone. Restoring now would fight whatever owns the hands.
                if (entry.Item == null ||
                    player.currentlyHeldObjectServer != null ||
                    player.currentItemSlot != entry.Slot ||
                    !ItemStillOccupiesSlot(player, entry))
                {
                    Finish(player, entry, restore: false);
                    continue;
                }

                // A ladder climb, a long CCTV session or any other special interact legitimately
                // outlasts the safety timeout (#288). Firing it there re-equips the weapon in the
                // player's hands mid-climb — the exact wedge the stow exists to prevent — so the
                // expiry SLIDES forward while the player is in one and the timeout only ever counts
                // against a normal state. The other reap conditions above still run every tick.
                if (player.isClimbingLadder || player.inSpecialInteractAnimation)
                {
                    entry.ExpiresAt = Mathf.Max(
                        entry.ExpiresAt, Time.time + SpecialInteractGraceSeconds);
                    continue;
                }

                if (Time.time >= entry.ExpiresAt)
                {
                    Plugin.Log?.LogWarning(
                        $"[HeldItemStow] safety timeout expired for reason '{entry.Reason}'; " +
                        $"restoring held item. Live tokens: {DescribeTokens(entry)}.");
                    Finish(player, entry, restore: true);
                }
            }

            ScratchPlayers.Clear();
        }

        /// <summary>
        /// Default predicate: this mod's weapons carry custom hold presentation that survives a
        /// vanilla body animation badly, and vanilla two-handed items already read as "both hands
        /// busy". Everything else keeps vanilla behavior (a flashlight on a ladder stays lit) —
        /// ability call sites pass force to stow regardless.
        /// </summary>
        internal static bool ShouldStow(GrabbableObject item)
        {
            if (item == null)
                return false;
            // The weapon types live in Better Armory, so "is this one of ours" is a full-type-name
            // probe rather than a type check (#267). It matters beyond the two-handed test below
            // because a one-handed Y4NGZ weapon would still carry custom hold presentation.
            if (ArmoryItemProbe.IsArmoryWeapon(item))
                return true;
            return item.itemProperties != null && item.itemProperties.twoHandedAnimation;
        }

        internal static bool IsTokenLive(PlayerControllerB player, long token)
        {
            return player != null &&
                token != 0L &&
                Entries.TryGetValue(player, out StowEntry entry) &&
                entry.Tokens.ContainsKey(token);
        }

        internal static void Release(PlayerControllerB player, long token)
        {
            if (player == null || token == 0L || !Entries.TryGetValue(player, out StowEntry entry))
                return;

            if (!entry.Tokens.Remove(token))
                return;
            if (entry.Tokens.Count == 0)
                Finish(player, entry, restore: true);
        }

        /// <summary>Token owner labels for the janitor's timeout warning — a wedged stow is only
        /// diagnosable if the log names which callers still hold it open.</summary>
        private static string DescribeTokens(StowEntry entry)
        {
            if (entry.Tokens.Count == 0)
                return "none";

            var owners = new List<string>(entry.Tokens.Count);
            foreach (KeyValuePair<long, string> pair in entry.Tokens)
                owners.Add($"{pair.Value}#{pair.Key}");
            return string.Join(", ", owners.ToArray());
        }

        private static long NextToken()
        {
            long token = _nextToken++;
            if (_nextToken == 0L)
                _nextToken = 1L;
            return token;
        }

        private static void CollectPlayers()
        {
            ScratchPlayers.Clear();
            foreach (KeyValuePair<PlayerControllerB, StowEntry> pair in Entries)
                ScratchPlayers.Add(pair.Key);
        }

        private static void Finish(PlayerControllerB player, StowEntry entry, bool restore)
        {
            Entries.Remove(player);
            EnableActions(entry.DisabledActions);
            entry.DisabledActions = null;
            if (restore)
                TryRestore(player, entry);
        }

        // Mirrors vanilla PlayerControllerB.SwitchToItemSlot's put-away half: the grab bool has to
        // go down and the pocket trigger has to fire, or the body keeps the previous hold pose
        // under the ability animation.
        private static bool TryStow(PlayerControllerB player, StowEntry entry)
        {
            GrabbableObject item = entry.Item;
            try
            {
                item.playerHeldBy = player;
                item.PocketItem();
                player.currentlyHeldObjectServer = null;
                player.currentlyHeldObject = null;
                player.isHoldingObject = false;
                player.twoHanded = false;
                player.twoHandedAnimation = false;
                player.equippedUsableItemQE = false;

                ReleaseInteractionBody(player);

                Animator animator = player.playerBodyAnimator;
                if (animator != null)
                {
                    // SetSpecialGrabAnimationBool is private; these are the same assignments it makes.
                    animator.SetBool("Grab", false);
                    if (item.itemProperties != null && !string.IsNullOrEmpty(item.itemProperties.grabAnim))
                        animator.SetBool(item.itemProperties.grabAnim, false);
                    if (item.itemProperties != null && !string.IsNullOrEmpty(item.itemProperties.pocketAnim))
                        animator.SetTrigger(item.itemProperties.pocketAnim);
                    animator.SetBool("GrabValidated", false);
                    animator.SetBool("cancelHolding", true);
                }

                if (player.heldObjectServerCopy != null)
                    player.heldObjectServerCopy.SetActive(false);
                if (player.IsOwner)
                    HUDManager.Instance?.ClearControlTips();
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"[HeldItemStow] stow failed for '{entry.Reason}': " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// Hands the player's body back to vanilla before the pocket parameters below are written.
        ///
        /// <see cref="GrabbableObject.PocketItem"/> only starts the weapon presentation's GRACEFUL
        /// exit, so its session stays active for the blend and keeps the API's body-animator and
        /// local-arms leases. Both symptoms of the custom-weapon stow followed from that: an
        /// ability starting its own interaction in the same frame was rejected as
        /// <c>interaction_resource_busy</c> and fell back to vanilla triggers on the weapon's shell
        /// controller — where <c>doingUpperBodyEmote</c> still ramps the UpperBodyEmotes layer to
        /// full weight over a state that was never played — and the pocket parameters below landed
        /// on that shell only to be thrown away when the session restored its equip-time animator
        /// snapshot.
        /// </summary>
        private static void ReleaseInteractionBody(PlayerControllerB player)
        {
            try
            {
                // The two weapon-animation stops moved to Better Armory with the animation code, so
                // they are one bridged call now (#267). It is a no-op without that plugin, which is
                // correct: there are no weapon presentations to end.
                ArmoryBridge.StopPresentationForHeldItemStow(player);

                for (int i = 0; i < BodyPresentationKinds.Length; i++)
                {
                    if (LCInteractionAnimationAPI.TryGetActiveInteraction(
                            player,
                            BodyPresentationKinds[i],
                            out InteractionAnimationHandle handle))
                    {
                        LCInteractionAnimationAPI.TryStopInteraction(
                            handle,
                            InteractionAnimationStopReason.Interrupted);
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning(
                    "[HeldItemStow] interaction body release failed safely: " + e.Message);
            }
        }

        private static void TryRestore(PlayerControllerB player, StowEntry entry)
        {
            if (player == null ||
                entry.Item == null ||
                player.isPlayerDead ||
                player.currentlyHeldObjectServer != null ||
                !ItemStillOccupiesSlot(player, entry))
            {
                return;
            }

            try
            {
                GrabbableObject item = entry.Item;
                item.playerHeldBy = player;
                item.EquipItem();
                player.currentlyHeldObjectServer = item;
                player.currentlyHeldObject = item;
                player.isHoldingObject = true;
                player.twoHanded = item.itemProperties != null && item.itemProperties.twoHanded;
                player.twoHandedAnimation =
                    item.itemProperties != null && item.itemProperties.twoHandedAnimation;
                player.equippedUsableItemQE = entry.EquippedUsableItemQE;
                player.timeSinceSwitchingSlots = 0f;

                if (player.heldObjectServerCopy != null)
                    player.heldObjectServerCopy.SetActive(true);

                Animator animator = player.playerBodyAnimator;
                if (animator != null)
                {
                    animator.SetBool("GrabValidated", true);
                    animator.SetBool("cancelHolding", false);
                    if (item.itemProperties != null && !string.IsNullOrEmpty(item.itemProperties.grabAnim))
                        animator.SetBool(item.itemProperties.grabAnim, true);
                    string trigger = player.twoHandedAnimation
                        ? "SwitchHoldAnimationTwoHanded"
                        : "SwitchHoldAnimation";
                    animator.ResetTrigger(trigger);
                    animator.SetTrigger(trigger);
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning(
                    $"[HeldItemStow] restore failed safely for '{entry.Reason}': " + e.Message);
            }
        }

        private static bool ItemStillOccupiesSlot(PlayerControllerB player, StowEntry entry)
        {
            if (entry.Slot == ItemOnlySlotIndex)
                return player.ItemOnlySlot == entry.Item;
            return player.ItemSlots != null &&
                entry.Slot >= 0 &&
                entry.Slot < player.ItemSlots.Length &&
                player.ItemSlots[entry.Slot] == entry.Item;
        }

        private static void ApplyInputSuppression(PlayerControllerB player, StowEntry entry)
        {
            if (entry.DisabledActions != null)
                return;
            if (player != GameNetworkManager.Instance?.localPlayerController)
                return;

            var disabled = new List<InputAction>(SuppressedActionNames.Length);
            InputActionAsset actions = IngamePlayerSettings.Instance?.playerInput?.actions;
            if (actions != null)
            {
                for (int i = 0; i < SuppressedActionNames.Length; i++)
                {
                    InputAction action = actions.FindAction(SuppressedActionNames[i], false);
                    if (action == null || !action.enabled)
                        continue;
                    action.Disable();
                    disabled.Add(action);
                }
            }

            entry.DisabledActions = disabled;
        }

        private static void EnableActions(List<InputAction> actions)
        {
            if (actions == null)
                return;
            for (int i = 0; i < actions.Count; i++)
                actions[i]?.Enable();
        }

        private sealed class StowEntry
        {
            internal GrabbableObject Item;
            internal int Slot;
            internal bool EquippedUsableItemQE;
            internal float ExpiresAt;
            internal string Reason;
            internal List<InputAction> DisabledActions;
            internal readonly Dictionary<long, string> Tokens = new Dictionary<long, string>();
        }
    }
}
