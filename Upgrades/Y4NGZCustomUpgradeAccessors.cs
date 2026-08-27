using System.Collections;
using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;

namespace Y4NGZUpgrades.Upgrades
{
    internal static class Y4NGZUpgradeState
    {
        internal static bool GetActiveUpgrade(string name)
        {
            return Y4NGZUpgradeManager.IsExactUnlocked(name);
        }

        internal static int GetUpgradeLevel(string name)
        {
            return Mathf.Max(0, Y4NGZUpgradeManager.GetExactLevel(name) - 1);
        }

        internal static int GetTier(string name)
        {
            return Y4NGZUpgradeManager.GetExactLevel(name);
        }
    }

    internal static class NativeFistsUpgrade
    {
        public const string UPGRADE_NAME = "Lethal Hands";
        public const string UPGRADE_ID = "lethal_hands_training";
        private const float TIER_1_DAMAGE = 0.5f;
        private const float TIER_2_DAMAGE = 1.0f;
        private const float TIER_3_DAMAGE = 1.5f;
        public const float TIER_3_STUN_SECONDS = 1.0f;

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_NAME);
        public static int GetTier() => IsUnlocked() ? Y4NGZUpgradeState.GetTier(UPGRADE_NAME) : 0;

        public static float GetCurrentDamageMultiplier() => GetDamageForTier(GetTier());

        public static float GetDamageForTier(int tier)
        {
            switch (tier)
            {
                case 1: return TIER_1_DAMAGE;
                case 2: return TIER_2_DAMAGE;
                case 3: return TIER_3_DAMAGE;
                default: return 0f;
            }
        }

        public static bool ShouldStun()
        {
            return IsUnlocked() && Y4NGZUpgradeState.GetUpgradeLevel(UPGRADE_NAME) >= 2;
        }
    }

    internal static class AdrenalineRushUpgrade
    {
        public const string UPGRADE_NAME = "Adrenaline Rush";
        public const int LOW_HP_THRESHOLD = 20;
        public const float SPEED_MULTIPLIER = 1.30f;
        public const float INVINCIBILITY_SECONDS = 2f;

        public static bool IsAdrenalineActive = false;
        public static bool InsuranceUsedThisRound = false;
        public static bool SpecialInsuranceUsedThisRound = false;
        public static bool IsInvincible = false;
        public static float InvincibilityEndTime = 0f;
        public static float OriginalSpeed = 0f;

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_NAME);
        public static int GetTier() => IsUnlocked() ? Y4NGZUpgradeState.GetTier(UPGRADE_NAME) : 0;
        public static bool HasInsurance() => GetTier() >= 2;
        public static bool HasSpecialDeathProtection() => GetTier() >= 2;
    }

    internal static class ShoulderCheckUpgrade
    {
        public const string UPGRADE_NAME = "Shoulder Check";
        public const float BASE_ARM_SECONDS = 2.0f;
        public const float TIER3_ARM_SECONDS = 1.65f;
        public const float READY_MIN_SPRINT_METER = 0.12f;
        public const float IMPACT_STUN_SECONDS = 0.65f;
        public const float DOOR_CHECK_RANGE = 1.35f;
        public const float DOOR_CHECK_RADIUS = 0.45f;

        private static readonly int[] DamageByTier = { 1, 2, 2 };
        private static readonly float[] KnockbackByTier = { 3.4f, 5.1f, 5.7f };

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_NAME);
        public static int GetTier() => IsUnlocked() ? Y4NGZUpgradeState.GetTier(UPGRADE_NAME) : 0;
        public static float GetArmSeconds() => GetTier() >= 3 ? TIER3_ARM_SECONDS : BASE_ARM_SECONDS;
        public static int GetDamage()
        {
            int tier = GetTier();
            return tier <= 0 ? 0 : DamageByTier[Mathf.Clamp(tier - 1, 0, DamageByTier.Length - 1)];
        }

        public static float GetKnockbackMeters()
        {
            int tier = GetTier();
            return tier <= 0 ? 0f : KnockbackByTier[Mathf.Clamp(tier - 1, 0, KnockbackByTier.Length - 1)];
        }
    }

    internal static class EscapeReflexUpgrade
    {
        public const string UPGRADE_NAME = "Escape Reflex";
        public const int HEALTH_THRESHOLD = 35;
        public const float INVISIBILITY_SECONDS = 2f;
        public const float SPEED_BOOST_SECONDS = 5f;
        public const float SPEED_BOOST_MULTIPLIER = 0.15f;
        public const float VISUAL_SECONDS = 1.15f;

        public static bool UsedThisRound;
        public static float SpeedBoostEndTime;
        public static float AppliedSpeedBonus;
        public static float VisualEndTime;

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_NAME);
        public static int GetTier() => IsUnlocked() ? Y4NGZUpgradeState.GetTier(UPGRADE_NAME) : 0;
        public static bool HasCloak() => GetTier() >= 2;
        public static bool HasSpeedBoost() => GetTier() >= 3;
    }

    internal static class LoneWolfUpgrade
    {
        public const string UPGRADE_NAME = "Lone Wolf";
        private const float SPEED_MULTIPLIER = 0.30f;
        private const float STAMINA_RECOVERY_RATE = 0.015f;
        private const float ENEMY_REVEAL_RANGE = 30f;
        private const float ENEMY_REVEAL_DURATION = 5f;

        internal static bool LoneWolfActive = false;
        internal static int ActiveLevel = 0;
        private static float _speedBonus = 0f;

        internal static void CheckAndActivate()
        {
            if (!Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_NAME) || LoneWolfActive)
                return;

            PlayerControllerB localPlayer = GameNetworkManager.Instance?.localPlayerController;
            if (localPlayer == null || localPlayer.isPlayerDead || localPlayer.isInHangarShipRoom)
                return;

            int livingOnMoon = 0;
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return;

            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player != null && player.isPlayerControlled && !player.isPlayerDead && !player.isInHangarShipRoom)
                    livingOnMoon++;
            }

            if (livingOnMoon == 1)
                ActivateLoneWolf(localPlayer, Y4NGZUpgradeState.GetTier(UPGRADE_NAME));
        }

        private static void ActivateLoneWolf(PlayerControllerB player, int level)
        {
            _speedBonus = player.movementSpeed * SPEED_MULTIPLIER;
            player.movementSpeed += _speedBonus;
            LoneWolfActive = true;
            ActiveLevel = level;

            Plugin.Log?.LogInfo($"Lone Wolf activated. Level {level}, speed bonus: {_speedBonus:F2}");

            if (level >= 3)
                GameNetworkManager.Instance.StartCoroutine(RevealNearbyEnemies(player));
        }

        private static string GetActivationMessage(int level)
        {
            switch (level)
            {
                case 1: return "You're the last one standing. Movement speed increased.";
                case 2: return "You're the last one standing. Speed and stamina enhanced.";
                case 3: return "You're the last one standing. Full survival mode activated.";
                default: return "Lone Wolf activated.";
            }
        }

        internal static void Deactivate()
        {
            if (!LoneWolfActive)
                return;

            PlayerControllerB player = GameNetworkManager.Instance?.localPlayerController;
            if (player != null)
            {
                player.movementSpeed -= _speedBonus;
                if (player.movementSpeed < 4.0f)
                    player.movementSpeed = 4.6f;
            }

            _speedBonus = 0f;
            LoneWolfActive = false;
            ActiveLevel = 0;
            Plugin.Log?.LogDebug("Lone Wolf deactivated.");
        }

        private static IEnumerator RevealNearbyEnemies(PlayerControllerB player)
        {
            EnemyAI[] enemies = Object.FindObjectsOfType<EnemyAI>();
            List<Light> revealLights = new List<Light>();

            foreach (EnemyAI enemy in enemies)
            {
                if (enemy == null || enemy.isEnemyDead)
                    continue;

                if (Vector3.Distance(enemy.transform.position, player.transform.position) > ENEMY_REVEAL_RANGE)
                    continue;

                Light light = enemy.gameObject.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = new Color(1f, 0.2f, 0.2f);
                light.intensity = 4f;
                light.range = 10f;
                revealLights.Add(light);
            }

            if (revealLights.Count > 0)
                Plugin.Log?.LogDebug($"Lone Wolf Level 3: Revealed {revealLights.Count} nearby enemies.");

            yield return new WaitForSeconds(ENEMY_REVEAL_DURATION);

            foreach (Light light in revealLights)
            {
                if (light != null)
                    Object.Destroy(light);
            }
        }

        internal static void ApplyStaminaBonus(PlayerControllerB player)
        {
            if (!LoneWolfActive || ActiveLevel < 2 || player == null || player.isPlayerDead)
                return;

            player.sprintMeter = Mathf.Clamp01(player.sprintMeter + Time.deltaTime * STAMINA_RECOVERY_RATE);
        }
    }

    internal static class SpatialAwarenessUpgrade
    {
        public const string UPGRADE_NAME = "Spatial Awareness";
        public const int TIER_LIGHT_FEET = 1;
        public const int TIER_DONT_SHOOT = 2;
        public const int TIER_GROUNDED = 3;
        public const int TIER_NPC = 4;

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_NAME);
        public static int GetTier() => IsUnlocked() ? Y4NGZUpgradeState.GetTier(UPGRADE_NAME) : 0;
        public static bool HasTier(int requiredTier) => GetTier() >= requiredTier;
    }

    internal static class LightFeetUpgrade
    {
        public const string UPGRADE_NAME = "Light Feet";
        public const int TIER_LIGHT_FEET = 1;
        public const int TIER_TURRET_DELAY = 2;
        public const int TIER_DONT_SHOOT = TIER_TURRET_DELAY;
        public const int TIER_GROUNDED = 3;
        public const float TURRET_FIRE_DELAY_SECONDS = 2.5f;

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_NAME);
        public static int GetTier() => IsUnlocked() ? Y4NGZUpgradeState.GetTier(UPGRADE_NAME) : 0;
        public static bool HasTier(int requiredTier) => GetTier() >= requiredTier;
    }

    internal static class SalvagerUpgrade
    {
        public const string UPGRADE_NAME = "Salvager";
        public const int TIER_GLOW = 1;
        public const int TIER_BEACON = 2;
        public const int TIER_TRAIL = 3;

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_NAME);
        public static int GetTier() => IsUnlocked() ? Y4NGZUpgradeState.GetTier(UPGRADE_NAME) : 0;
        public static bool HasTier(int requiredTier) => GetTier() >= requiredTier;
    }

    internal static class PredatorInstinctUpgrade
    {
        public const string UPGRADE_NAME = "Predator Instinct";
        public static readonly float[] RANGE_BY_LEVEL = new float[] { 10f, 20f, 30f };

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_NAME);
        public static int GetTier() => IsUnlocked() ? Y4NGZUpgradeState.GetTier(UPGRADE_NAME) : 0;

        public static float GetRange()
        {
            int tier = GetTier();
            return tier <= 0 ? 0f : RANGE_BY_LEVEL[Mathf.Clamp(tier - 1, 0, RANGE_BY_LEVEL.Length - 1)];
        }

        public static bool ShouldHeartbeat() => GetTier() >= 3;
    }

    internal static class SixthSenseUpgrade
    {
        public const string UPGRADE_NAME = "Sixth Sense";
        public const float BASE_RANGE = 15f;
        public const float HAZARD_RANGE = 5f;
        public const float ITEM_RANGE = 10f;
        public const float ENEMY_RANGE = 15f;
        public const float REFRESH_INTERVAL = 0.25f;
        public const float OUTLINE_SECONDS = 0.60f;

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_NAME);
        public static int GetTier() => IsUnlocked() ? Y4NGZUpgradeState.GetTier(UPGRADE_NAME) : 0;
        public static bool ShowsItems() => GetTier() >= 2;
        public static bool ShowsEnemies() => GetTier() >= 3;
    }

    internal static class FieldOpticsUpgrade
    {
        // Display rename only: id "better_scanner" is load-bearing (save keys, skill-tree node, icon).
        public const string UPGRADE_ID = "better_scanner";
        public const string UPGRADE_NAME = "Field Optics";
        public const float RANGE_BONUS_PER_TIER = 0.15f;

        private static readonly float[] LIGHT_INTENSITY_BONUS_BY_TIER = { 0.10f, 0.15f, 0.40f };
        private static readonly float[] LIGHT_CONE_BONUS_BY_TIER = { 0.10f, 0.20f, 0.30f };

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_ID);
        public static int GetTier() => IsUnlocked() ? Y4NGZUpgradeState.GetTier(UPGRADE_ID) : 0;
        public static float GetRangeMultiplier() => 1f + Mathf.Max(0, GetTier()) * RANGE_BONUS_PER_TIER;

        public static float GetLightIntensityMultiplier() => 1f + BonusForTier(LIGHT_INTENSITY_BONUS_BY_TIER);
        public static float GetLightConeMultiplier() => 1f + BonusForTier(LIGHT_CONE_BONUS_BY_TIER);

        private static float BonusForTier(float[] table)
        {
            int tier = GetTier();
            return tier <= 0 ? 0f : table[Mathf.Clamp(tier - 1, 0, table.Length - 1)];
        }
    }

    internal static class TransporterUpgrade
    {
        public const string UPGRADE_NAME = "Transporter";
        public const float WEIGHT_REDUCTION_PER_TIER = 0.20f;

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_NAME);
        public static int GetTier() => IsUnlocked() ? Y4NGZUpgradeState.GetTier(UPGRADE_NAME) : 0;
        public static float GetCarryWeightMultiplier()
        {
            return Mathf.Clamp01(1f - Mathf.Max(0, GetTier()) * WEIGHT_REDUCTION_PER_TIER);
        }

        public static bool CanCarryTwoTwoHandedItems() => false;
    }

    internal static class ShadowStepUpgrade
    {
        public const string UPGRADE_NAME = "Shadow Step";
        public const float INVISIBILITY_DURATION = 6f;
        public const float COOLDOWN_DURATION = 120f;
        public const float REDUCED_DETECTION_MULTIPLIER = 0.75f;

        public static bool IsInvisible = false;
        public static float InvisibilityEndTime = 0f;
        public static float CooldownEndTime = 0f;

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_NAME);
        public static int GetTier() => IsUnlocked() ? Y4NGZUpgradeState.GetTier(UPGRADE_NAME) : 0;
        public static bool HasActiveAbility() => GetTier() >= 3;
    }

    internal static class RetaliationUpgrade
    {
        public const string UPGRADE_NAME = "Resilience";
        public const float TIER1_REFLECT_CHANCE = 0.5f;
        public const float TIER2_STUN_SECONDS = 1.0f;

        public static bool HasMergedRetaliation() => Y4NGZUpgradeState.GetTier(UPGRADE_NAME) >= 4;
    }

    internal static class DeathboundUpgrade
    {
        public const string UPGRADE_NAME = "Deathbound";
        public const int PROTECTED_SLOT_INDEX = 0;

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_NAME);
    }

    internal static class GlowInTheDarkUpgrade
    {
        public const string UPGRADE_NAME = "Night Vision";
        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_NAME);
        public static int GetTier() => IsUnlocked() ? Y4NGZUpgradeState.GetTier(UPGRADE_NAME) : 0;

        public static float GetRange()
        {
            switch (GetTier())
            {
                case 1: return 24f;
                case 2: return 32f;
                case 3: return 40f;
                default: return 0f;
            }
        }

        public static float GetIntensity()
        {
            switch (GetTier())
            {
                case 1: return 0.12f;
                case 2: return 0.16f;
                case 3: return 0.22f;
                default: return 0f;
            }
        }
    }

    internal static class ExtraSlotUpgrade
    {
        public const string UPGRADE_NAME = "Deeper Pockets";
        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_NAME);
        public static int GetTier() => IsUnlocked() ? Y4NGZUpgradeState.GetTier(UPGRADE_NAME) : 0;
        public static bool CanCarryTwoTwoHandedItems() => GetTier() >= 3;
    }

    internal static class SquadSightUpgrade
    {
        public const string UPGRADE_NAME = "Squad Sight";

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_NAME);
        public static int GetTier() => IsUnlocked() ? Y4NGZUpgradeState.GetTier(UPGRADE_NAME) : 0;

        public static float GetRange()
        {
            switch (GetTier())
            {
                case 1: return 15f;
                case 2: return 30f;
                default: return 0f;
            }
        }

        public static float GetVisibilityMultiplier()
        {
            switch (GetTier())
            {
                case 1: return 0.6f;
                case 2: return 0.85f;
                case 3: return 1.0f;
                default: return 0f;
            }
        }
    }

    internal static class PanicSlideUpgrade
    {
        // Keep this stable ID rather than a display name so the Escape Artist rename
        // preserves all existing purchase state.
        public const string UPGRADE_ID = "panic_slide";

        // Tier 1 slide tuning. Keep these grouped for playtest iteration.
        public const float COOLDOWN_SECONDS = 2.25f;
        public const float STAMINA_COST = 0.14f;
        public const float SLIDE_FORCE = 15.5f;
        public const float MIN_SPRINT_METER = 0.16f;
        public const float SLIDE_DURATION_SECONDS = 1.35f;
        public const float SLIDE_SUSTAIN_FORCE = 4.5f;
        public const float SLIDE_STEERING_DEGREES_PER_SECOND = 60f;
        public const float SLIDE_CAMERA_DIP = 0.5f;
        // legacy head-follow (removed): kept for compatibility with in-flight assets/tasks.
        public const float SLIDE_CAMERA_HEAD_UP_OFFSET = 0.10f;
        public const float SLIDE_CAMERA_HEAD_FORWARD_OFFSET = 0.10f;
        public const float SLIDE_CAMERA_HEAD_SMOOTH_TIME = 0.08f;
        public const float SLIDE_CAMERA_HEAD_MAX_OFFSET_METERS = 1.0f;
        public const float SLIDE_CAMERA_HEAD_BLEND_IN_SECONDS = 0.12f;
        public const float SLIDE_CAMERA_HEAD_BLEND_OUT_SECONDS = 0.15f;
        public const float SLIDE_CAMERA_ROLL_DEGREES = 7f;
        public const float SLIDE_CAMERA_DIP_IN_SECONDS = 0.15f;
        public const float SLIDE_CAMERA_DIP_OUT_SECONDS = 0.20f;
        public const float SLIDE_EXIT_ANIMATION_SECONDS = 0.55f;
        public const float SLIDE_AIRBORNE_CANCEL_SECONDS = 0.35f;
        public const float SLIDE_MIN_SPEED_GRACE_SECONDS = 0.40f;
        public const float SLIDE_MIN_HORIZONTAL_SPEED = 2.5f;

        // Tier 2 mantle tuning.
        public const float MANTLE_RETRY_SECONDS = 0.05f;
        public const float MANTLE_COOLDOWN_SECONDS = 0.8f;
        public const float MANTLE_FORWARD_REACH = 1.35f;
        public const float MANTLE_MIN_LEDGE_HEIGHT = 0.95f;
        public const float MANTLE_MAX_LEDGE_HEIGHT = 3.0f;
        public const float MANTLE_TALL_THRESHOLD = 1.35f;
        public const float MANTLE_STAMINA_COST = 0.08f;
        public const float MANTLE_ONE_METER_SECONDS = 0.667f;
        public const float MANTLE_TWO_METER_SECONDS = 1.3f;
        public const float MANTLE_CAMERA_DIP = 0.25f;
        public const float MANTLE_CAMERA_DIP_IN_SECONDS = 0.12f;
        public const float MANTLE_CAMERA_DIP_OUT_SECONDS = 0.18f;
        public const float MANTLE_BODY_LOWER_METERS = 0.25f;
        public const float MANTLE_BODY_LOWER_RAMP_OUT_FRACTION = 0.25f;

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_ID);
        public static int GetTier() => IsUnlocked() ? Y4NGZUpgradeState.GetTier(UPGRADE_ID) : 0;
        public static bool HasDoubleJump() => GetTier() >= 3;
    }

    internal static class TurretHackerUpgrade
    {
        public const string UPGRADE_NAME = "Field Mechanic";
        public const string UPGRADE_ID = "turret_hacker";
        public const float COOLDOWN_SECONDS = 20f;
        /// <summary>Hold-interact duration for the tier 1 camera hack and tier 2 door hack.</summary>
        public const float HACK_HOLD_SECONDS = 1.2f;

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_NAME);
        public static int GetTier() => IsUnlocked() ? Y4NGZUpgradeState.GetTier(UPGRADE_NAME) : 0;
        public static bool CanHackCameras() => GetTier() >= 1;
        public static bool CanHackDoors() => GetTier() >= 2;
        public static bool CanHackTurrets() => GetTier() >= 3;
        public static bool CanRepairBatteries() => GetTier() >= 2;
        public static float GetDrillPumpRepairSpeedMultiplier() => GetTier() >= 1 ? 1.5f : 1f;
        public static float GetBreakChanceMultiplier() => GetTier() >= 1 ? 0.5f : 1f;
        public static float GetDuration() => GetTier() >= 2 ? 90f : 30f;
    }

    internal static class FieldOperationsUpgrade
    {
        public const string UPGRADE_NAME = "Field Operations";
        public const string UPGRADE_ID = "field_operations";

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_NAME);
        public static int GetTier() => IsUnlocked() ? Y4NGZUpgradeState.GetTier(UPGRADE_NAME) : 0;
    }

    internal static class CourierDroneUpgrade
    {
        public const string UPGRADE_NAME = "Courier Drone";
        public const float COMMAND_COOLDOWN_SECONDS = 3f;
        public const float PICKUP_RADIUS = 10f;
        public const int MAX_ITEMS_PER_SWEEP = 12;
        public const float MAX_CARGO_WEIGHT_LB = 150f;

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_NAME);
        public static int GetTier() => IsUnlocked() ? Y4NGZUpgradeState.GetTier(UPGRADE_NAME) : 0;
    }

    internal static class BuddySystemUpgrade
    {
        public const string UPGRADE_NAME = "Buddy System";
        private static readonly float[] RangeByTier = { 10f, 15f };
        private static readonly float[] BuffByTier = { 0.03f, 0.06f };

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_NAME);
        public static int GetTier() => IsUnlocked() ? Y4NGZUpgradeState.GetTier(UPGRADE_NAME) : 0;
        public static float GetRange() => GetRange(GetTier());
        public static float GetRange(int tier) => tier <= 0 ? 0f : RangeByTier[Mathf.Clamp(tier - 1, 0, RangeByTier.Length - 1)];
        public static float GetBuff() => GetBuff(GetTier());
        public static float GetBuff(int tier) => tier <= 0 ? 0f : BuffByTier[Mathf.Clamp(tier - 1, 0, BuffByTier.Length - 1)];
    }

    internal static class PingUpgrade
    {
        public const string UPGRADE_NAME = "Ping";
        public const float COOLDOWN_SECONDS = 3f;
        public const float MARK_DURATION = 5f;
        public const float TARGET_RANGE = 80f;
        public const float ENEMY_DAMAGE_REDUCTION = 0.20f;
        public const float TEAMMATE_DAMAGE_REDUCTION = 0.20f;

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_NAME);
        public static int GetTier() => IsUnlocked() ? Y4NGZUpgradeState.GetTier(UPGRADE_NAME) : 0;
        public static bool ReducesMarkedEnemyDamage() => GetTier() >= 2;
        public static bool CanMarkTeammates() => GetTier() >= 3;
    }

    internal static class CommandNetUpgrade
    {
        public const string UPGRADE_NAME = "Command Net";
        public const float DAMAGE_OUTLINE_SECONDS = 5f;
        public const float MINIMAP_RANGE_METERS = 80f;

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_NAME);
        public static int GetTier() => IsUnlocked() ? Y4NGZUpgradeState.GetTier(UPGRADE_NAME) : 0;
        public static bool HasVirtualWalkie() => GetTier() >= 1;
        public static bool HasDamageOutline() => GetTier() >= 2;
        public static bool HasMinimap() => GetTier() >= 3;
    }

    internal static class RallyCallUpgrade
    {
        public const string UPGRADE_NAME = "Rally Call";
        public const float RANGE = 20f;
        public const float DURATION_SECONDS = 8f;
        public const float COOLDOWN_SECONDS = 60f;
        private static readonly float[] SpeedByTier = { 0.20f, 0.25f, 0.30f };
        private static readonly float[] DamageReductionByTier = { 0.20f, 0.30f, 0.40f };

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_NAME);
        public static int GetTier() => IsUnlocked() ? Y4NGZUpgradeState.GetTier(UPGRADE_NAME) : 0;
        public static float GetSpeedBonus(int tier) => tier <= 0 ? 0f : SpeedByTier[Mathf.Clamp(tier - 1, 0, SpeedByTier.Length - 1)];
        public static float GetDamageReduction(int tier) => tier <= 0 ? 0f : DamageReductionByTier[Mathf.Clamp(tier - 1, 0, DamageReductionByTier.Length - 1)];
    }

    internal static class WorklightBeaconUpgrade
    {
        public const string UPGRADE_NAME = "Worklight Beacon";
        public const float COOLDOWN_SECONDS = 0.35f;
        public const float THROW_FORCE = 28f;
        public const float LIFE_SECONDS = 180f;

        private static readonly int[] ChargesByTier = { 3, 5, 9 };
        private static readonly float[] IntensityByTier = { 12f, 18f, 26f };
        private static readonly float[] RangeByTier = { 10f, 13f, 17f };

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_NAME);
        public static int GetTier() => IsUnlocked() ? Y4NGZUpgradeState.GetTier(UPGRADE_NAME) : 0;
        public static int GetMaxCharges() => GetValue(ChargesByTier, GetTier());
        public static float GetIntensity(int tier) => GetValue(IntensityByTier, tier);
        public static float GetRange(int tier) => GetValue(RangeByTier, tier);

        private static T GetValue<T>(T[] values, int tier)
        {
            if (values == null || values.Length == 0 || tier <= 0)
                return default;

            return values[Mathf.Clamp(tier - 1, 0, values.Length - 1)];
        }
    }

    internal static class InspireUpgrade
    {
        public const string UPGRADE_NAME = "Inspire";
        public const float REVIVE_RANGE = 7f;
        public const float REVIVE_CHANCE = 0.5f;
        public const float COOLDOWN_SECONDS = 75f;

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_NAME);
    }

    internal static class OverachieverUpgrade
    {
        // Keep this stable ID rather than a display name so the Overachiever rename
        // (and any future rename) preserves all existing purchase state. The id
        // "veteran" is load-bearing: the force_field -> veteran save migration
        // depends on it, and the NEW quota upgrade took the "Veteran" display name.
        public const string UPGRADE_ID = "veteran";

        private static readonly int[] BonusTokensByTier = { 1, 2, 4 };

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_ID);
        public static int GetTier() => IsUnlocked() ? Y4NGZUpgradeState.GetTier(UPGRADE_ID) : 0;

        public static int GetBonusTokensPerRank() => GetBonusTokensForTier(GetTier());

        /// <summary>Per-rank token bonus at an explicit tier. Tier 0 (unowned) pays nothing.</summary>
        public static int GetBonusTokensForTier(int tier)
        {
            if (tier <= 0)
                return 0;

            return BonusTokensByTier[Mathf.Clamp(tier - 1, 0, BonusTokensByTier.Length - 1)];
        }

        /// <summary>Per-rank bonus at the highest tier; used by the economy margin audit.</summary>
        public static int MaxBonusTokensPerRank => BonusTokensByTier[BonusTokensByTier.Length - 1];
    }

    internal static class QuotaGuardUpgrade
    {
        // Stable-id lookup (display name is "Veteran"); tier effects are
        // host-resolved as the MAX tier across all connected clients via
        // UpgradeTierSync ("any crew member owns it").
        public const string UPGRADE_ID = "quota_guard";

        /// <summary>Tier 1: crew-death quota penalty cap, as a fraction of the pre-penalty quota per round-end.</summary>
        public const float DEATH_PENALTY_FRACTION_CAP = 0.10f;

        /// <summary>Tier 2: quota deadline in days (vanilla default is 3).</summary>
        public const int EXTENDED_DEADLINE_DAYS = 4;

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_ID);
        public static int GetTier() => IsUnlocked() ? Y4NGZUpgradeState.GetTier(UPGRADE_ID) : 0;
        public static bool HasQuotaCap() => GetTier() >= 1;
        public static bool HasExtendedDeadline() => GetTier() >= 2;
    }

    internal static class ChameleonUpgrade
    {
        public const string UPGRADE_NAME = "Chameleon";
        public const string UPGRADE_ID = "chameleon";

        private static readonly float[] ExtraDetectionTimeByTier = { 0.15f, 0.30f, 0.50f };

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_NAME);
        public static int GetTier() => IsUnlocked() ? Y4NGZUpgradeState.GetTier(UPGRADE_NAME) : 0;

        /// <summary>Cameras take this factor times as long to detect the player (1.0 = vanilla).</summary>
        public static float GetDetectionTimeMultiplier() => GetDetectionTimeMultiplier(GetTier());

        public static float GetDetectionTimeMultiplier(int tier)
        {
            if (tier <= 0)
                return 1f;

            return 1f + ExtraDetectionTimeByTier[Mathf.Clamp(tier - 1, 0, ExtraDetectionTimeByTier.Length - 1)];
        }
    }

    internal static class ScavengerUpgrade
    {
        public const string UPGRADE_NAME = "Scavenger";
        public const string UPGRADE_ID = "scavenger";

        private static readonly float[] BonusByTier = { 0.10f, 0.20f, 0.30f };

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_NAME);
        public static int GetTier() => IsUnlocked() ? Y4NGZUpgradeState.GetTier(UPGRADE_NAME) : 0;

        public static float GetAmmoMultiplier() => GetMultiplier(GetTier());
        public static float GetFuelMultiplier(int tier) => GetMultiplier(tier);

        private static float GetMultiplier(int tier)
        {
            if (tier <= 0)
                return 1f;

            return 1f + BonusByTier[Mathf.Clamp(tier - 1, 0, BonusByTier.Length - 1)];
        }
    }
}
