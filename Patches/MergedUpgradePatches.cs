using System.Collections.Generic;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Patches
{
    [HarmonyPatch]
    internal static class MergedUpgradePatches
    {
        private const string PhysicalConditioning = "Sprinter";
        private const string QuickHands = "Quick Hands";
        private const string ThickSkin = "Resilience";

        private const float MovementSpeedBonus = 1.4f;
        private const float SprintTimeBonus = 6f;
        private const float StaminaRegenMultiplier = 1.05f;
        private const float JumpForceBonus = 3f;
        private const float JumpForceFinalBonus = 0.75f;
        private const float FootstepNoiseReduction = 10f;

        private const float SinkingPenaltyReduction = 0.2f;
        private const float TractionIncrease = 0.25f;
        private const float SlopePenaltyReduction = 0.25f;
        private const float CrouchPenaltyReduction = 0.2f;
        private const float FallDamageMultiplier = 0.75f;

        private const float GrabDistancePerTier = 1f;
        private const int GrabDistanceMaxTiers = 3;
        private const float InteractionSpeedPerTier = 0.1f;
        private const float ClimbSpeedPerTierMultiplier = 0.2f;
        private const int ClimbSpeedMaxTiers = 3;

        private const int HealthPerTier = 20;
        private const int BonusRegenAmount = 1;
        private const float RegenCapIncrease = 0.5f;
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
            internal float ExtraRegenTimer;
            internal int MaxHealth = VanillaMaxHealth;
        }

        private sealed class DamageState
        {
            internal readonly bool Applies;
            internal readonly int HealthBefore;
            internal readonly int Damage;
            internal readonly bool WasCriticallyInjured;

            internal DamageState(bool applies, int healthBefore, int damage, bool wasCriticallyInjured)
            {
                Applies = applies;
                HealthBefore = healthBefore;
                Damage = damage;
                WasCriticallyInjured = wasCriticallyInjured;
            }
        }

        private static readonly Dictionary<int, AppliedBonuses> BonusesByPlayer =
            new Dictionary<int, AppliedBonuses>();

        [HarmonyPatch(typeof(PlayerControllerB), "LateUpdate")]
        [HarmonyPostfix]
        private static void PlayerLateUpdatePostfix(PlayerControllerB __instance)
        {
            if (!IsLocalPlayer(__instance))
                return;

            AppliedBonuses bonuses = GetBonuses(__instance);
            ApplyStatBonuses(__instance, bonuses);
            ApplyHealthCap(__instance, bonuses);
            TickBonusStaminaRegen(__instance);
            TickBonusHealthRegen(__instance, bonuses);
        }

        private static void ApplyStatBonuses(PlayerControllerB player, AppliedBonuses bonuses)
        {
            int physicalTier = Tier(PhysicalConditioning);
            int bootsTier = physicalTier;
            int quickTier = Tier(QuickHands);

            float baseMovementSpeed = player.movementSpeed - bonuses.MovementSpeed;
            float nextMovementSpeedBonus = physicalTier >= 1 ? MovementSpeedBonus : 0f;
            if (bootsTier >= 4 && player.isCrouching)
                nextMovementSpeedBonus += Mathf.Max(0f, baseMovementSpeed) * GetCrouchSpeedBonusMultiplier();
            player.movementSpeed = Mathf.Max(0.1f, baseMovementSpeed + nextMovementSpeedBonus);
            bonuses.MovementSpeed = nextMovementSpeedBonus;

            float baseSprintTime = player.sprintTime - bonuses.SprintTime;
            float nextSprintTimeBonus = physicalTier >= 2 ? SprintTimeBonus : 0f;
            player.sprintTime = Mathf.Max(0.1f, baseSprintTime + nextSprintTimeBonus);
            bonuses.SprintTime = nextSprintTimeBonus;

            float baseJumpForce = player.jumpForce - bonuses.JumpForce;
            float nextJumpForceBonus = 0f;
            if (physicalTier >= 3)
                nextJumpForceBonus += JumpForceBonus;
            if (physicalTier >= 4)
                nextJumpForceBonus += JumpForceFinalBonus;
            player.jumpForce = Mathf.Max(0.1f, baseJumpForce + nextJumpForceBonus);
            bonuses.JumpForce = nextJumpForceBonus;

            float baseGrabDistance = player.grabDistance - bonuses.GrabDistance;
            float nextGrabDistanceBonus =
                Mathf.Min(quickTier, GrabDistanceMaxTiers) * GrabDistancePerTier;
            player.grabDistance = Mathf.Max(1f, baseGrabDistance + nextGrabDistanceBonus);
            bonuses.GrabDistance = nextGrabDistanceBonus;

            float baseClimbSpeed = player.climbSpeed - bonuses.ClimbSpeed;
            float nextClimbSpeedBonus = physicalTier > 0
                ? Mathf.Max(0f, baseClimbSpeed) * Mathf.Min(physicalTier, ClimbSpeedMaxTiers) * ClimbSpeedPerTierMultiplier
                : 0f;
            player.climbSpeed = Mathf.Max(0.1f, baseClimbSpeed + nextClimbSpeedBonus);
            bonuses.ClimbSpeed = nextClimbSpeedBonus;

            float baseSlopeModifierSpeed = player.slopeModifierSpeed - bonuses.SlopeModifierSpeed;
            float nextSlopeModifierSpeedBonus = bootsTier >= 2
                ? Mathf.Max(0f, baseSlopeModifierSpeed) * TractionIncrease
                : 0f;
            player.slopeModifierSpeed = Mathf.Max(0.01f, baseSlopeModifierSpeed + nextSlopeModifierSpeedBonus);
            bonuses.SlopeModifierSpeed = nextSlopeModifierSpeedBonus;

            float baseSlopeIntensity = player.slopeIntensity - bonuses.SlopeIntensity;
            float nextSlopeIntensityBonus = bootsTier >= 3
                ? -Mathf.Max(0f, baseSlopeIntensity) * SlopePenaltyReduction
                : 0f;
            player.slopeIntensity = Mathf.Max(0f, baseSlopeIntensity + nextSlopeIntensityBonus);
            bonuses.SlopeIntensity = nextSlopeIntensityBonus;

            float baseSinkingSpeed = player.sinkingSpeedMultiplier - bonuses.SinkingSpeed;
            float nextSinkingSpeedBonus = bootsTier >= 1 && player.isSinking
                ? -Mathf.Max(0f, baseSinkingSpeed) * SinkingPenaltyReduction
                : 0f;
            player.sinkingSpeedMultiplier = Mathf.Max(0f, baseSinkingSpeed + nextSinkingSpeedBonus);
            bonuses.SinkingSpeed = nextSinkingSpeedBonus;
        }

        private static float GetCrouchSpeedBonusMultiplier()
        {
            const float vanillaCrouchDivisor = 1.5f;
            float vanillaMultiplier = 1f / vanillaCrouchDivisor;
            float improvedMultiplier = vanillaMultiplier + (1f - vanillaMultiplier) * CrouchPenaltyReduction;
            return improvedMultiplier / vanillaMultiplier - 1f;
        }

        private static void TickBonusStaminaRegen(PlayerControllerB player)
        {
            if (Tier(PhysicalConditioning) < 2)
                return;

            if (player.isSprinting || player.isMovementHindered > 0 || player.sprintMeter >= 1f)
                return;

            float baseRegen = Time.deltaTime / Mathf.Max(0.1f, player.sprintTime + (player.isWalking ? 9f : 4f));
            player.sprintMeter = Mathf.Clamp01(player.sprintMeter + baseRegen * (StaminaRegenMultiplier - 1f));
            if (player.sprintMeterUI != null)
                player.sprintMeterUI.fillAmount = player.sprintMeter;
        }

        private static void ApplyHealthCap(PlayerControllerB player, AppliedBonuses bonuses)
        {
            int nextMaxHealth = GetMaxHealth();
            if (nextMaxHealth <= VanillaMaxHealth)
            {
                bonuses.MaxHealth = VanillaMaxHealth;
                return;
            }

            if (bonuses.MaxHealth != nextMaxHealth)
            {
                int previousMax = Mathf.Max(VanillaMaxHealth, bonuses.MaxHealth);
                if (player.health >= Mathf.Min(VanillaMaxHealth, previousMax))
                {
                    player.health = nextMaxHealth;
                    UpdateHealthUi(player);
                }

                bonuses.MaxHealth = nextMaxHealth;
            }

            if (player.health > nextMaxHealth)
            {
                player.health = nextMaxHealth;
                UpdateHealthUi(player);
            }
        }

        private static void TickBonusHealthRegen(PlayerControllerB player, AppliedBonuses bonuses)
        {
            int thickTier = Tier(ThickSkin);
            if (thickTier <= 0 || player.isPlayerDead)
                return;

            int regenCap = thickTier >= 2
                ? Mathf.RoundToInt(VanillaRegenCap * (1f + RegenCapIncrease))
                : VanillaRegenCap;
            regenCap = Mathf.Min(regenCap, GetMaxHealth());
            if (player.health >= regenCap)
                return;

            bonuses.ExtraRegenTimer -= Time.deltaTime;
            if (bonuses.ExtraRegenTimer > 0f)
                return;

            bonuses.ExtraRegenTimer = 1f;
            int amount = player.health < VanillaRegenCap ? BonusRegenAmount : 1;
            player.health = Mathf.Min(player.health + amount, regenCap);
            if (player.health >= VanillaRegenCap && player.criticallyInjured)
                player.MakeCriticallyInjured(enable: false);
            UpdateHealthUi(player);
        }

        private static int GetMaxHealth()
        {
            int thickTier = Tier(ThickSkin);
            return thickTier <= 0 ? VanillaMaxHealth : VanillaMaxHealth + thickTier * HealthPerTier;
        }

        private static AppliedBonuses GetBonuses(PlayerControllerB player)
        {
            int key = player.GetInstanceID();
            if (!BonusesByPlayer.TryGetValue(key, out AppliedBonuses bonuses))
            {
                bonuses = new AppliedBonuses();
                BonusesByPlayer[key] = bonuses;
            }

            return bonuses;
        }

        private static int Tier(string upgradeName)
        {
            return Mathf.Max(0, Y4NGZUpgradeManager.GetLevel(upgradeName));
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
            int quickTier = Tier(QuickHands);
            if (quickTier <= 0)
                return;

            speedMultiplier *= 1f + quickTier * InteractionSpeedPerTier;
        }

        [HarmonyPatch(typeof(PlayerControllerB), "DamagePlayer")]
        [HarmonyPrefix]
        private static void DamagePlayerPrefix(
            PlayerControllerB __instance,
            ref int damageNumber,
            CauseOfDeath causeOfDeath,
            bool fallDamage,
            out DamageState __state)
        {
            __state = null;
            if (!IsLocalPlayer(__instance))
                return;

            if (Tier(PhysicalConditioning) >= 5 && (fallDamage || causeOfDeath == CauseOfDeath.Gravity))
                damageNumber = Mathf.Max(0, Mathf.RoundToInt(damageNumber * FallDamageMultiplier));

            __state = new DamageState(
                Tier(ThickSkin) > 0,
                __instance.health,
                damageNumber,
                __instance.criticallyInjured);
        }

        [HarmonyPatch(typeof(PlayerControllerB), "DamagePlayer")]
        [HarmonyPostfix]
        private static void DamagePlayerPostfix(PlayerControllerB __instance, DamageState __state)
        {
            if (__state == null || !__state.Applies || !IsLocalPlayer(__instance) || __instance.isPlayerDead)
                return;

            int maxHealth = GetMaxHealth();
            int expectedHealth;
            if (__state.HealthBefore - __state.Damage <= 0 && !__state.WasCriticallyInjured && __state.Damage < 50)
                expectedHealth = 5;
            else
                expectedHealth = Mathf.Clamp(__state.HealthBefore - __state.Damage, 0, maxHealth);

            if (expectedHealth > __instance.health)
            {
                __instance.health = expectedHealth;
                UpdateHealthUi(__instance);
            }
        }

        [HarmonyPatch(typeof(RoundManager), "PlayAudibleNoise")]
        [HarmonyPrefix]
        private static void PlayAudibleNoisePrefix(Vector3 noisePosition, ref float noiseRange, int noiseID = 0)
        {
            if (noiseID != 6 || Tier(PhysicalConditioning) < 4)
                return;

            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
            if (local == null || Vector3.Distance(local.transform.position, noisePosition) > 2.5f)
                return;

            noiseRange = Mathf.Max(0f, noiseRange - FootstepNoiseReduction);
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
            bonuses.ExtraRegenTimer = 0f;
        }
    }
}
