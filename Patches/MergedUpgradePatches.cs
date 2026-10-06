using System.Collections.Generic;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;
using Y4NGZUpgrades.HUD;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Patches
{
    [HarmonyPatch]
    internal static class MergedUpgradePatches
    {
        // Stable ids only (#435). The imported Late Game Upgrades catalog carries its own row
        // titled "Quick Hands", and a native family can be conditionally retitled by the resolved
        // policy, so a display-name lookup here can resolve the wrong row outright. Sprinter,
        // Surefooted and Resilience are read through their accessors, which also know which
        // effects survive in each mode; their rank ladders live in LguEffectComposition.
        private const string QuickHandsId = "quick_hands";

        private const float StaminaRegenMultiplier = 1.05f;
        private const float FootstepNoiseReduction = 10f;

        // v81 emits the owner's footstep as noiseID 6 (PlayFootstepLocal) and every other client's
        // copy of it as noiseID 7 (PlayFootstepServer). Both need the reduction - see F-GHOST-4.
        private const int FootstepNoiseIdLocal = 6;
        private const int FootstepNoiseIdServer = 7;
        private const float FootstepNoiseRadius = 2.5f;

        private const float SinkingPenaltyReduction = 0.2f;

        private const float GrabDistancePerTier = 1f;
        private const int GrabDistanceMaxTiers = 3;
        private const float InteractionSpeedPerTier = 0.1f;

        private const int BonusRegenAmount = 1;
        private const int VanillaMaxHealth = 100;
        private const int VanillaRegenCap = 20;

        private sealed class AppliedBonuses
        {
            internal float MovementSpeed;
            internal float SprintTime;
            internal float JumpForce;
            internal float GrabDistance;
            internal float ClimbSpeed;
            internal float SlopeModifierSpeed;
            internal float SlopeIntensity;
            internal float SinkingSpeed;

            // F-GHOST-6 / F-GHOST-16: the true, un-bonused value of each stat, snapshotted rather
            // than reconstructed as `current - applied`. The reconstruction was poisoned by two
            // things: the Mathf.Max floors below (a movementSpeed driven under the applied bonus
            // latched the player at 0.1 permanently) and any absolute external write. The snapshot
            // re-syncs on every frame in which we are applying nothing, which is precisely when the
            // live value IS the base, so an external writer still wins as soon as we step aside.
            internal float BaseMovementSpeed;
            internal float BaseSprintTime;
            internal float BaseJumpForce;
            internal float BaseGrabDistance;
            internal float BaseClimbSpeed;
            internal float BaseSlopeModifierSpeed;
            internal float BaseSlopeIntensity;
            internal float BaseSinkingSpeed;
            internal int SinkingSources = -1;

            // F-GHOST-6 follow-up: the exact value we last assigned to movementSpeed. Anything
            // else the live field holds was written by someone outside this class - LGU's Sick
            // Beats adds and removes a flat boost from an Update postfix - and has to be carried
            // rather than overwritten. See LguEffectComposition.ReconcileBase.
            internal float LastMovementSpeed;

            internal float ExtraRegenTimer = 1f;
            internal int MaxHealth = VanillaMaxHealth;
        }

        // Keyed by playerClientId (stable across respawns and lobby joins) rather than
        // GetInstanceID, which changes whenever the player object is rebuilt and used to
        // leak an entry per lobby. Cleared wholesale on Disconnect.
        private static readonly Dictionary<ulong, AppliedBonuses> BonusesByPlayer =
            new Dictionary<ulong, AppliedBonuses>();

        // Named external movement bonuses compose without baking a temporary buff into base speed.
        private static readonly Dictionary<string, float> SpeedMultipliersBySource =
            new Dictionary<string, float>();

        /// <summary>
        /// Registers (or refreshes) a multiplicative movement-speed bonus for the local
        /// player under <paramref name="sourceId"/>. A multiplier of 1 or less clears it.
        /// </summary>
        internal static void SetSpeedMultiplier(string sourceId, float multiplier)
        {
            if (string.IsNullOrEmpty(sourceId))
                return;

            if (multiplier <= 1f)
            {
                SpeedMultipliersBySource.Remove(sourceId);
                return;
            }

            SpeedMultipliersBySource[sourceId] = multiplier;
        }

        /// <summary>Removes a previously registered movement-speed multiplier.</summary>
        internal static void ClearSpeedMultiplier(string sourceId)
        {
            if (string.IsNullOrEmpty(sourceId))
                return;

            SpeedMultipliersBySource.Remove(sourceId);
        }

        private static float GetExternalSpeedMultiplier()
        {
            if (SpeedMultipliersBySource.Count == 0)
                return 1f;

            float product = 1f;
            foreach (float multiplier in SpeedMultipliersBySource.Values)
                product *= multiplier;

            return product;
        }

        // F-GHOST-2 / F-GHOST-11: snapshot the stamina bar so the bonus regen can be computed from
        // the recovery vanilla actually applied this frame instead of re-deriving its formula.
        // Harmony gives each patch class its own __state slot, so this does not collide with
        // LoneWolfUpgrade's identical prefix on the same method.
        [HarmonyPatch(typeof(PlayerControllerB), "LateUpdate")]
        [HarmonyPrefix]
        private static void PlayerLateUpdatePrefix(PlayerControllerB __instance, out float __state)
        {
            __state = __instance != null ? __instance.sprintMeter : 0f;
        }

        [HarmonyPatch(typeof(PlayerControllerB), "LateUpdate")]
        [HarmonyPostfix]
        private static void PlayerLateUpdatePostfix(PlayerControllerB __instance, float __state)
        {
            if (!IsLocalPlayer(__instance))
                return;

            AppliedBonuses bonuses = GetBonuses(__instance);
            ApplyStatBonuses(__instance, bonuses);
            ApplyHealthCap(__instance, bonuses);
            TickBonusStaminaRegen(__instance, bonuses, __state);
            TickBonusHealthRegen(__instance, bonuses);
            ResilienceHealthPresentation.Tick(__instance, ResolvePresentationMaxHealth());
        }

        /// <summary>
        /// F-GHOST-6 / F-GHOST-16: resolves the base value of a stat we own. The base is a stored
        /// snapshot, refreshed only while we are applying nothing, so neither our own clamps nor an
        /// absolute write by another mod can be folded into the stored bonus and stick there.
        /// </summary>
        private static float ResolveBase(ref float storedBase, float currentValue, float appliedBonus)
        {
            storedBase = LguEffectComposition.ReconcileBase(
                storedBase, 0f, currentValue, appliedBonus, reconcileExternalWrites: false);
            return storedBase;
        }

        /// <summary>
        /// The same resolution for a stat an external provider writes additively while we hold a
        /// bonus. Comparing against our own last write is what tells the two apart; see
        /// <see cref="LguEffectComposition.ReconcileBase"/> for why the snapshot rule alone both
        /// erases the foreign boost and latches it permanently.
        /// </summary>
        private static float ResolveBase(
            ref float storedBase, float lastWritten, float currentValue, float appliedBonus)
        {
            storedBase = LguEffectComposition.ReconcileBase(
                storedBase, lastWritten, currentValue, appliedBonus,
                reconcileExternalWrites: LguEffectCompatibility.Present);
            return storedBase;
        }

        private static void ApplyStatBonuses(PlayerControllerB player, AppliedBonuses bonuses)
        {
            // Each row reads 0 unless it is selling its full self: in LGU-preferred mode Late
            // Game Upgrades sells every one of these effects, and in LGU-only mode every native
            // row is hidden. Only Firm Footing's sinking reduction at the bottom of this method
            // survives in unique-only mode (#435).
            int sprinterTier = SprinterUpgrade.GetAthleticsTier();
            int traversalTier = SurefootedUpgrade.GetTraversalTier();
            int quickTier = Y4NGZUpgradeState.GetTier(QuickHandsId);

            float baseMovementSpeed = ResolveBase(
                ref bonuses.BaseMovementSpeed, bonuses.LastMovementSpeed, player.movementSpeed, bonuses.MovementSpeed);
            float nextMovementSpeedBonus = LguEffectComposition.SprinterMovementSpeedBonus(sprinterTier);
            // F-GHOST-12: the crouch SPEED GAIN is stated directly as the +10% the catalog promises.
            if (player.isCrouching)
                nextMovementSpeedBonus += Mathf.Max(0f, baseMovementSpeed) * LguEffectComposition.SprinterCrouchSpeedGain(sprinterTier);

            // External multipliers stack on top of the tree's own bonuses so a surge feels
            // the same whether or not Sprinter is owned.
            float externalMultiplier = GetExternalSpeedMultiplier();
            if (externalMultiplier > 1f)
            {
                float boostedBase = Mathf.Max(0f, baseMovementSpeed + nextMovementSpeedBonus);
                nextMovementSpeedBonus += boostedBase * (externalMultiplier - 1f);
            }

            float movementSpeed = Mathf.Max(0.1f, baseMovementSpeed + nextMovementSpeedBonus);
            player.movementSpeed = movementSpeed;
            bonuses.LastMovementSpeed = movementSpeed;
            bonuses.MovementSpeed = nextMovementSpeedBonus;

            float baseSprintTime = ResolveBase(ref bonuses.BaseSprintTime, player.sprintTime, bonuses.SprintTime);
            float nextSprintTimeBonus = LguEffectComposition.SprinterSprintTimeBonus(sprinterTier);
            player.sprintTime = Mathf.Max(0.1f, baseSprintTime + nextSprintTimeBonus);
            bonuses.SprintTime = nextSprintTimeBonus;

            float baseJumpForce = ResolveBase(ref bonuses.BaseJumpForce, player.jumpForce, bonuses.JumpForce);
            float nextJumpForceBonus = LguEffectComposition.SurefootedJumpForceBonus(traversalTier);
            player.jumpForce = Mathf.Max(0.1f, baseJumpForce + nextJumpForceBonus);
            bonuses.JumpForce = nextJumpForceBonus;

            float baseGrabDistance = ResolveBase(ref bonuses.BaseGrabDistance, player.grabDistance, bonuses.GrabDistance);
            float nextGrabDistanceBonus =
                Mathf.Min(quickTier, GrabDistanceMaxTiers) * GrabDistancePerTier;
            player.grabDistance = Mathf.Max(1f, baseGrabDistance + nextGrabDistanceBonus);
            bonuses.GrabDistance = nextGrabDistanceBonus;

            float baseClimbSpeed = ResolveBase(ref bonuses.BaseClimbSpeed, player.climbSpeed, bonuses.ClimbSpeed);
            float nextClimbSpeedBonus =
                Mathf.Max(0f, baseClimbSpeed) * LguEffectComposition.SurefootedClimbSpeedGain(traversalTier);
            player.climbSpeed = Mathf.Max(0.1f, baseClimbSpeed + nextClimbSpeedBonus);
            bonuses.ClimbSpeed = nextClimbSpeedBonus;

            float baseSlopeModifierSpeed = ResolveBase(ref bonuses.BaseSlopeModifierSpeed, player.slopeModifierSpeed, bonuses.SlopeModifierSpeed);
            float nextSlopeModifierSpeedBonus =
                Mathf.Max(0f, baseSlopeModifierSpeed) * LguEffectComposition.SurefootedTractionGain(traversalTier);
            player.slopeModifierSpeed = Mathf.Max(0.01f, baseSlopeModifierSpeed + nextSlopeModifierSpeedBonus);
            bonuses.SlopeModifierSpeed = nextSlopeModifierSpeedBonus;

            float baseSlopeIntensity = ResolveBase(ref bonuses.BaseSlopeIntensity, player.slopeIntensity, bonuses.SlopeIntensity);
            float nextSlopeIntensityBonus =
                -Mathf.Max(0f, baseSlopeIntensity) * LguEffectComposition.SurefootedUphillReduction(traversalTier);
            player.slopeIntensity = Mathf.Max(0f, baseSlopeIntensity + nextSlopeIntensityBonus);
            bonuses.SlopeIntensity = nextSlopeIntensityBonus;

            // F-GHOST-6: sinkingSpeedMultiplier is the one stat here that vanilla writes, and it
            // writes it ABSOLUTELY and exactly once per engagement - QuicksandTrigger.OnTriggerStay
            // returns early while isSinking is already true, and the water variant writes 0f
            // meaning "do not sink at all". Deriving the base as `current - applied` therefore
            // folded our own reduction into the next frame's base and, in water, resurrected a
            // sinking speed vanilla had just cancelled. Re-snapshot only when we are applying
            // nothing or when the sinking-source count moved, which are the only moments vanilla
            // can have touched the field.
            int sinkingSources = player.sourcesCausingSinking;
            if (bonuses.SinkingSpeed == 0f || bonuses.SinkingSources != sinkingSources)
                bonuses.BaseSinkingSpeed = player.sinkingSpeedMultiplier;
            bonuses.SinkingSources = sinkingSources;

            // #435: the sinking reduction is the ONLY Surefooted effect the Firm Footing variant
            // keeps, so it asks for its milestone rather than for a rank.
            float baseSinkingSpeed = Mathf.Max(0f, bonuses.BaseSinkingSpeed);
            float nextSinkingSpeedBonus = player.isSinking && SurefootedUpgrade.HasSinkingReduction()
                ? -baseSinkingSpeed * SinkingPenaltyReduction
                : 0f;
            player.sinkingSpeedMultiplier = Mathf.Max(0f, baseSinkingSpeed + nextSinkingSpeedBonus);
            bonuses.SinkingSpeed = nextSinkingSpeedBonus;
        }

        private static void TickBonusStaminaRegen(PlayerControllerB player, AppliedBonuses bonuses, float sprintMeterBeforeLateUpdate)
        {
            if (!LguEffectComposition.SprinterRegeneratesStamina(SprinterUpgrade.GetAthleticsTier()))
                return;

            if (player.isSprinting || player.isMovementHindered > 0 || player.sprintMeter >= 1f)
                return;

            // F-GHOST-11: measure the recovery vanilla actually applied rather than re-deriving its
            // formula. That picks up the drunkness factor (Mathf.Abs(drunknessSpeedEffect - 1.25f))
            // for free and drops the duplicated 4/9 magic numbers.
            float recovered = player.sprintMeter - sprintMeterBeforeLateUpdate;
            if (recovered <= 0f)
                return;

            // F-GHOST-2: this same level grants +6s sprintTime, and vanilla's regen denominator is
            // (sprintTime + 9) walking / (sprintTime + 4) still - so the sprint-time buff made the
            // bar refill ~27-37% SLOWER while the shop promised "stamina regen +5%". Scale the
            // measured delta back up by the denominator ratio to undo that, then apply the
            // advertised 5% on top, so the net result really is 5% faster than before the purchase.
            // LGU's Bigger Lungs rewrites every sprintTime read inside this very LateUpdate, so the
            // denominator vanilla used is not the field value; composing it keeps the net result at
            // the advertised 5% instead of over-correcting.
            float denominatorOffset = player.isWalking ? 9f : 4f;
            float effectiveSprintTime = LguEffectCompatibility.ComposeEffectiveSprintTime(player.sprintTime);
            float bonus = LguEffectComposition.StaminaRegenBonus(
                recovered, effectiveSprintTime, bonuses.SprintTime, denominatorOffset, StaminaRegenMultiplier);
            if (bonus <= 0f)
                return;

            player.sprintMeter = Mathf.Clamp01(player.sprintMeter + bonus);
            if (player.sprintMeterUI != null)
                player.sprintMeterUI.fillAmount = player.sprintMeter;
        }

        private static void ApplyHealthCap(PlayerControllerB player, AppliedBonuses bonuses)
        {
            int nextMaxHealth = GetMaxHealth();

            // F-ENF-11: the old `nextMaxHealth <= VanillaMaxHealth` early return ran BEFORE the
            // clamp below, so selling Resilience never pulled a raised-cap player back to 100 -
            // only their next DamagePlayer (whose ceiling is now 100 again) did. Fall through.
            bool ownCapChanged = bonuses.MaxHealth != nextMaxHealth;
            if (ownCapChanged)
            {
                // Top up only when the cap actually rose; a respec downwards is the clamp's job.
                // The top-up deliberately reads our OWN maximum: an external provider's health is
                // its own to grant, and treating its ceiling as ours would hand out a free heal
                // every time one of its levels was bought.
                int toppedUp = LguEffectComposition.ResolveHealthTopUp(
                    player.health, bonuses.MaxHealth, nextMaxHealth, VanillaMaxHealth);
                if (toppedUp != player.health)
                {
                    player.health = toppedUp;
                    UpdateHealthUi(player);
                }

                bonuses.MaxHealth = nextMaxHealth;
            }

            // The clamp, by contrast, must use the ceiling the game itself enforces. LGU's Stimpack
            // adds flat health on purchase and restores it through its own ReviveDeadPlayers
            // transpiler, so clamping to our own maximum would delete that health one frame later.
            // A ceiling we cannot establish means no clamp at all rather than a destructive guess.
            if (!LguEffectCompatibility.TryComposeMaxHealth(nextMaxHealth, out int ceiling))
                return;

            if (player.health > ceiling)
            {
                player.health = ceiling;
                UpdateHealthUi(player);
            }
        }

        private static void TickBonusHealthRegen(PlayerControllerB player, AppliedBonuses bonuses)
        {
            int thickTier = ResilienceUpgrade.GetHealthTier();
            if (thickTier <= 0 || player.isPlayerDead)
                return;

            // F-ENF-6: vanilla PlayerControllerB.Update already regenerates 1 HP/s on its own timer
            // while health is below its cap. Ticking here as well healed 2 HP/s in that band -
            // twice the advertised rate - so leave it entirely to vanilla. LGU's Medical Nanobots
            // raises that cap, which can swallow our band whole; when it does we contribute
            // nothing, which is correct rather than a loss. LGU's Effective Bandaids scales only
            // the HP of those natural ticks; our band stays a fixed BonusRegenAmount per second.
            int regenFloor = LguEffectCompatibility.ComposeHealthRegenFloor(VanillaRegenCap);
            int regenCap = LguEffectComposition.ResilienceRegenCap(thickTier, VanillaRegenCap);
            if (!LguEffectComposition.ShouldTickBonusHealthRegen(player.health, regenFloor, regenCap))
                return;

            bonuses.ExtraRegenTimer -= Time.deltaTime;
            if (bonuses.ExtraRegenTimer > 0f)
                return;

            // F-ENF-12: a full second before the first tick, not a free HP on the first eligible
            // frame. F-ENF-6: the old `health < VanillaRegenCap ? BonusRegenAmount : 1` ternary had
            // two identical branches and reached for a band that is now skipped outright above.
            bonuses.ExtraRegenTimer = 1f;
            player.health = Mathf.Min(player.health + BonusRegenAmount, regenCap);
            if (player.criticallyInjured)
                player.MakeCriticallyInjured(enable: false);
            UpdateHealthUi(player);
        }

        internal static int GetMaxHealth()
        {
            // The raised cap only exists because NineLivesPatch's DamagePlayer transpiler replaced
            // vanilla's Mathf.Clamp(health - damage, 0, 100) ceiling with a call to this method. If
            // that transpiler did not apply, vanilla's literal 100 is still the live ceiling, and
            // reporting anything higher would hand out a Nine Lives shield capacity and a health
            // top-up that the very next hit silently undoes. Degrade to the vanilla cap instead.
            if (!NineLivesPatch.HealthCapTranspilerApplied)
                return VanillaMaxHealth;

            return LguEffectComposition.ResilienceMaxHealth(ResilienceUpgrade.GetHealthTier(), VanillaMaxHealth);
        }

        /// <summary>
        /// The health ceiling the game actually enforces: ours, raised by any external provider
        /// that rewrote the same damage clamp. Shield capacity and the per-frame clamp both need
        /// this rather than <see cref="GetMaxHealth"/>, which describes only this mod's share.
        /// </summary>
        internal static int GetEffectiveMaxHealth()
        {
            int nativeMax = GetMaxHealth();
            return LguEffectCompatibility.TryComposeMaxHealth(nativeMax, out int composed)
                ? composed
                : nativeMax;
        }

        /// <summary>
        /// Ceiling for the Resilience readout. Without Resilience the readout is not ours to draw,
        /// so an external provider's raised ceiling must not switch it on.
        /// </summary>
        private static int ResolvePresentationMaxHealth()
        {
            int nativeMax = GetMaxHealth();
            return nativeMax <= VanillaMaxHealth ? nativeMax : GetEffectiveMaxHealth();
        }

        private static AppliedBonuses GetBonuses(PlayerControllerB player)
        {
            ulong key = player.playerClientId;
            if (!BonusesByPlayer.TryGetValue(key, out AppliedBonuses bonuses))
            {
                bonuses = new AppliedBonuses();
                BonusesByPlayer[key] = bonuses;
            }

            return bonuses;
        }

        private static bool IsLocalPlayer(PlayerControllerB player)
        {
            if (player == null)
                return false;

            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
            return player == local || (local == null && player.IsOwner && player.isPlayerControlled);
        }

        private static void UpdateHealthUi(PlayerControllerB player)
        {
            HUDManager hud = HUDManager.Instance;
            if (hud == null)
                return;

            hud.SetCracksOnVisor(Mathf.Min(player.health, VanillaMaxHealth));
            hud.UpdateHealthUI(player.health, hurtPlayer: false);
        }

        [HarmonyPatch(typeof(HUDManager), "HoldInteractionFill")]
        [HarmonyPrefix]
        private static void HoldInteractionFillPrefix(ref float speedMultiplier)
        {
            int quickTier = Y4NGZUpgradeState.GetTier(QuickHandsId);
            if (quickTier <= 0)
                return;

            speedMultiplier *= 1f + quickTier * InteractionSpeedPerTier;
        }

        // Surefooted's fall-damage reduction is the only change to the incoming damage number
        // here. Resilience deliberately does NOT snapshot health and rewrite it in a postfix:
        // that reconciliation clobbered every other patch's (and every other mod's)
        // in-DamagePlayer health change, and it smuggled in an undocumented "survive at 5 HP" save
        // that duplicated Adrenaline Rush tier 2. Adrenaline Rush is the one sanctioned death save.
        // The max-health cap and the bonus regen still run from LateUpdate.
        [HarmonyPatch(typeof(PlayerControllerB), "DamagePlayer")]
        [HarmonyPrefix]
        private static void DamagePlayerPrefix(
            PlayerControllerB __instance,
            ref int damageNumber,
            CauseOfDeath causeOfDeath,
            bool fallDamage)
        {
            if (!IsLocalPlayer(__instance))
                return;

            if (fallDamage || causeOfDeath == CauseOfDeath.Gravity)
            {
                float scale = LguEffectComposition.SurefootedFallDamageScale(SurefootedUpgrade.GetTraversalTier());
                if (scale < 1f)
                    damageNumber = Mathf.Max(0, Mathf.RoundToInt(damageNumber * scale));
            }
        }

        [HarmonyPatch(typeof(RoundManager), "PlayAudibleNoise")]
        [HarmonyPrefix]
        private static void PlayAudibleNoisePrefix(Vector3 noisePosition, ref float noiseRange, int noiseID = 0)
        {
            if (noiseID == FootstepNoiseIdLocal)
            {
                if (!SprinterUpgrade.HasQuietFootsteps())
                    return;

                PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
                if (local == null
                    || (local.transform.position - noisePosition).sqrMagnitude > FootstepNoiseRadius * FootstepNoiseRadius)
                {
                    return;
                }

                noiseRange = Mathf.Max(0f, noiseRange - FootstepNoiseReduction);
                return;
            }

            // F-GHOST-4: PlayFootstepLocal emits noiseID 6 on the walker's own client, but
            // PlayFootstepServer emits noiseID 7 at the same range on every OTHER client, the host
            // included - and host-owned enemy AI (BaboonBirdAI.DetectNoise and friends open with
            // `if (!IsOwner) return;`) only ever hears that copy. Reducing noiseID 6 alone therefore
            // did nothing for a client. Attribute the server-side footstep to the player standing on
            // it and read THEIR synced quiet-footsteps capability (#435: Sprinter rank 3).
            if (noiseID != FootstepNoiseIdServer)
                return;

            PlayerControllerB walker = FindPlayerNearestNoise(noisePosition);
            if (walker == null)
                return;

            if (UpgradeTierSync.GetTier(walker.actualClientId, SprinterUpgrade.QUIET_FOOTSTEPS_CAPABILITY_ID) <= 0)
                return;

            noiseRange = Mathf.Max(0f, noiseRange - FootstepNoiseReduction);
        }

        /// <summary>
        /// Nearest live player within <see cref="FootstepNoiseRadius"/> of a noise, or null. Walks
        /// the fixed allPlayerScripts array: this runs on every footstep of every player, so no
        /// LINQ, no closures and no allocation.
        /// </summary>
        private static PlayerControllerB FindPlayerNearestNoise(Vector3 noisePosition)
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return null;

            PlayerControllerB nearest = null;
            float nearestSqr = FootstepNoiseRadius * FootstepNoiseRadius;
            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB candidate = players[i];
                if (candidate == null || !candidate.isPlayerControlled || candidate.isPlayerDead)
                    continue;

                float sqr = (candidate.transform.position - noisePosition).sqrMagnitude;
                if (sqr > nearestSqr)
                    continue;

                nearestSqr = sqr;
                nearest = candidate;
            }

            return nearest;
        }


        [HarmonyPatch(typeof(StartOfRound), "ReviveDeadPlayers")]
        [HarmonyPostfix]
        private static void ReviveDeadPlayersPostfix()
        {
            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
            if (local == null)
                return;

            AppliedBonuses bonuses = GetBonuses(local);
            bonuses.MaxHealth = VanillaMaxHealth;
            ApplyHealthCap(local, bonuses);
        }

        [HarmonyPatch(typeof(StartOfRound), "ShipLeave")]
        [HarmonyPostfix]
        private static void ShipLeavePostfix()
        {
            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
            if (local == null)
                return;

            AppliedBonuses bonuses = GetBonuses(local);
            bonuses.ExtraRegenTimer = 1f;
        }

        [HarmonyPatch(typeof(StartOfRound), "EndOfGame")]
        [HarmonyPostfix]
        private static void EndOfGamePostfix()
        {
            ResilienceHealthPresentation.Restore();
        }

        /// <summary>
        /// Per-player bonus snapshots describe one lobby's player objects only, and any
        /// external speed multiplier belongs to a run that just ended. Drop both so a
        /// re-join starts from vanilla stats.
        /// </summary>
        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        [HarmonyPostfix]
        private static void DisconnectPostfix()
        {
            BonusesByPlayer.Clear();
            SpeedMultipliersBySource.Clear();
            ResilienceHealthPresentation.Restore();
        }
    }
}
