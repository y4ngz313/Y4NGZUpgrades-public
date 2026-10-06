using System.Collections;
using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;

namespace Y4NGZUpgrades.Upgrades
{
    internal static class Y4NGZUpgradeState
    {
        /// <summary>
        /// The stored rank a native effect reads, or 0 while the resolved policy hides the row
        /// (#435). Every accessor below passes a catalog STABLE ID, never a display name: only an
        /// id can be asked for its mode, and an id passed verbatim never resolves to an imported
        /// Late Game Upgrades row through a colliding or normalized title. The mode is asked
        /// first so a hidden row stays inert even while its dormant ranks are still registered -
        /// in LGU-only mode that is every native row, whether or not it belongs to a family.
        ///
        /// Two dictionary lookups and no allocation; this is read from per-frame effect paths.
        /// </summary>
        internal static int GetTier(string id)
        {
            return Y4NGZUpgradeManager.ModeOf(id) == NativeFamilyMode.Hidden
                ? 0
                : Y4NGZUpgradeManager.GetExactLevel(id);
        }

        internal static bool GetActiveUpgrade(string id)
        {
            return GetTier(id) > 0;
        }

        /// <summary>
        /// The rank of a row whose every effect is also sold by Late Game Upgrades, or 0 whenever
        /// the row is not selling its full self. Rows with no unique variant are hidden rather
        /// than reduced, so this is the read that keeps LGU-preferred mode from stacking a native
        /// contribution on top of the imported one.
        /// </summary>
        internal static int GetFullNativeTier(string id)
        {
            return Y4NGZUpgradeManager.ModeOf(id) == NativeFamilyMode.Full
                ? Y4NGZUpgradeManager.GetExactLevel(id)
                : 0;
        }

        /// <summary>
        /// True while this native family sells only its unique variant, so Late Game Upgrades
        /// supplies the overlapping effects and the rank <see cref="GetTier"/> reports is a
        /// unique rank rather than a full-native one (#435).
        /// </summary>
        internal static bool IsUniqueOnly(string id)
        {
            return Y4NGZUpgradeManager.ModeOf(id) == NativeFamilyMode.UniqueOnly;
        }

        /// <summary>
        /// Whether the rank this save has earned owns the effect that full-native rank
        /// <paramref name="originalRank"/> unlocked, in whichever record the row currently sells.
        /// An old rank is never faked: in unique-only mode the variant's milestone table answers,
        /// and a hidden family - or a rank the variant does not carry at all - owns nothing.
        ///
        /// Two dictionary lookups and no allocation; this is read from per-frame effect paths.
        /// </summary>
        internal static bool HasMilestone(string id, int originalRank)
        {
            switch (Y4NGZUpgradeManager.ModeOf(id))
            {
                case NativeFamilyMode.Full:
                    return Y4NGZUpgradeManager.GetExactLevel(id) >= originalRank;
                case NativeFamilyMode.UniqueOnly:
                    return NativeUpgradeFamilies.TryGet(id, out NativeUpgradeFamily family)
                        && family.UniqueVariant != null
                        && family.UniqueVariant.HasMilestone(Y4NGZUpgradeManager.GetExactLevel(id), originalRank);
                default:
                    return false;
            }
        }
    }

    internal static class NativeFistsUpgrade
    {
        public const string UPGRADE_ID = "lethal_hands_training";
        private const float TIER_1_DAMAGE = 0.5f;
        private const float TIER_2_DAMAGE = 1.0f;
        private const float TIER_3_DAMAGE = 1.5f;
        public const float TIER_3_STUN_SECONDS = 1.0f;
        // F-FIST-11: the tier at which a punch also stuns. NativeFistsPatch reads this and
        // TIER_3_STUN_SECONDS instead of carrying its own copies of the same two numbers.
        public const int STUN_TIER = 3;

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_ID);
        public static int GetTier() => Y4NGZUpgradeState.GetTier(UPGRADE_ID);

        // F-INFRA/WP12 dead-code sweep: GetCurrentDamageMultiplier() was unreferenced - every
        // damage site resolves an explicit tier through GetDamageForTier so the server can
        // score a punch for an arbitrary client id.
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

        /// <summary>
        /// F-FIST-11: single source for the stun rule. The tier overload is what the authoritative
        /// hit path uses, because the server resolves a punch for an arbitrary client id and cannot
        /// read the local player's upgrade level.
        /// </summary>
        public static bool ShouldStun(int tier) => tier >= STUN_TIER;

        public static bool ShouldStun()
        {
            return IsUnlocked() && ShouldStun(GetTier());
        }
    }

    internal static class NineLivesUpgrade
    {
        // Preserve the purchased ID, config section, prices and existing icon.
        public const string UPGRADE_ID = "adrenaline_rush";
        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_ID);
        public static int GetTier() => Y4NGZUpgradeState.GetTier(UPGRADE_ID);
        public static bool HasInsurance() => GetTier() >= 2;
    }

    // WP12 dead-code sweep (F-INFRA-13): ShoulderCheckUpgrade and SpatialAwarenessUpgrade are
    // gone along with ShoulderCheckPatch. Neither "Shoulder Check" nor "Spatial Awareness" is a
    // catalog id or display name, so GetExactLevel returned 0 forever and every gate downstream
    // was unreachable; "Spatial Awareness" was superseded by LightFeetUpgrade below.

    // F-SHADOW-10: EscapeReflexUpgrade lived on after "Escape Reflex" left the catalog, so
    // GetExactLevel("Escape Reflex") returned 0 forever while EscapeReflexPatch still installed
    // four Harmony patches and wrote movementSpeed every LateUpdate from dead code. The accessor
    // and its patch are gone; only the escape_reflex -> sixth_sense migration rule remains.

    internal static class LightFeetUpgrade
    {
        // Needed by UpgradeTierSync: the turret hold-fire is resolved host-side (F-INFRA-4).
        public const string UPGRADE_ID = "light_feet";
        public const int TIER_LIGHT_FEET = 1;
        public const int TIER_TURRET_DELAY = 2;
        public const int TIER_DONT_SHOOT = TIER_TURRET_DELAY;
        public const int TIER_GROUNDED = 3;
        public const float TURRET_FIRE_DELAY_SECONDS = 2.5f;

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_ID);
        public static int GetTier() => Y4NGZUpgradeState.GetTier(UPGRADE_ID);
        public static bool HasTier(int requiredTier) => GetTier() >= requiredTier;
    }

    /// <summary>
    /// Sprinter (#435): three ranks of speed, stamina and quiet movement. Late Game Upgrades'
    /// Running Shoes, Bigger Lungs and Carbon Kneejoints sell every one of these effects, so the
    /// row has no unique variant and reads nothing unless it is selling its full self.
    /// </summary>
    internal static class SprinterUpgrade
    {
        public const string UPGRADE_ID = "physical_conditioning";

        /// <summary>
        /// Wire id of the per-player quiet-footsteps capability in <c>UpgradeTierSync</c> (0 or 1).
        /// The host attributes every remote footstep and needs only this answer. The synced
        /// physical_conditioning rank cannot carry it: an older peer publishes that slot on the
        /// old five-rank ladder, where rank 3 bought no footstep reduction. This is not a
        /// catalog row and has no save state.
        /// </summary>
        public const string QUIET_FOOTSTEPS_CAPABILITY_ID = "sprinter_quiet_footsteps";

        /// <summary>
        /// Rank driving movement speed, sprint time, stamina regen, crouching speed and footstep
        /// noise, and nothing else. Zero whenever the row is hidden.
        /// </summary>
        public static int GetAthleticsTier() => Y4NGZUpgradeState.GetFullNativeTier(UPGRADE_ID);

        public static bool HasQuietFootsteps() =>
            LguEffectComposition.SprinterQuietensFootsteps(GetAthleticsTier());
    }

    /// <summary>
    /// Surefooted (#435): three ranks of ladder speed, sinking, traction, jumping, slopes and
    /// landings. In LGU-preferred mode Late Game Upgrades sells every traversal effect and the
    /// row keeps only Firm Footing, the slower sinking. Stable-id lookups throughout.
    /// </summary>
    internal static class SurefootedUpgrade
    {
        public const string UPGRADE_ID = "surefooted";

        /// <summary>Full-native rank at which the sinking reduction unlocks; the unique milestone.</summary>
        public const int SINKING_TIER = 1;

        /// <summary>
        /// Rank driving ladder speed, traction, jump force, slopes and fall damage. Zero in
        /// unique-only mode, where those effects are LGU's, and while the row is hidden.
        /// </summary>
        public static int GetTraversalTier() => Y4NGZUpgradeState.GetFullNativeTier(UPGRADE_ID);

        /// <summary>The one effect that survives in unique-only mode.</summary>
        public static bool HasSinkingReduction() => Y4NGZUpgradeState.HasMilestone(UPGRADE_ID, SINKING_TIER);
    }

    // WP12 dead-code sweep (F-INFRA-13): SalvagerUpgrade and PredatorInstinctUpgrade are gone
    // along with LastWillPatch, PredatorInstinctPatch and the PlayerControllerBPatch death-item
    // glow. "Salvager" and "Predator Instinct" are neither catalog ids nor display names, so
    // every gate they fed was permanently false.

    internal static class SixthSenseUpgrade
    {
        public const string UPGRADE_ID = "sixth_sense";
        public const float BASE_RANGE = 15f;
        public const float HAZARD_RANGE = 5f;
        public const float ITEM_RANGE = 10f;
        public const float ENEMY_RANGE = 15f;
        public const float REFRESH_INTERVAL = 0.25f;
        public const float OUTLINE_SECONDS = 0.60f;

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_ID);
        public static int GetTier() => Y4NGZUpgradeState.GetTier(UPGRADE_ID);
        public static bool ShowsItems() => GetTier() >= 2;
        public static bool ShowsEnemies() => GetTier() >= 3;
    }

    internal static class FieldOpticsUpgrade
    {
        // Display rename only: id "better_scanner" is load-bearing (save keys, skill-tree node, icon).
        public const string UPGRADE_ID = "better_scanner";
        public const float RANGE_BONUS_PER_TIER = 0.15f;

        private static readonly float[] LIGHT_INTENSITY_BONUS_BY_TIER = { 0.10f, 0.15f, 0.40f };
        private static readonly float[] LIGHT_CONE_BONUS_BY_TIER = { 0.10f, 0.20f, 0.30f };

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_ID);
        public static int GetTier() => Y4NGZUpgradeState.GetTier(UPGRADE_ID);

        /// <summary>
        /// Scanner node range. Neutral in unique-only mode: LGU's Better Scanner owns the scanner
        /// there and the native row keeps only the lamp (#435). The light tables below still read
        /// the rank directly - unique ranks 1-3 map onto the same three entries.
        /// </summary>
        public static float GetRangeMultiplier()
        {
            return Y4NGZUpgradeState.IsUniqueOnly(UPGRADE_ID)
                ? 1f
                : 1f + Mathf.Max(0, GetTier()) * RANGE_BONUS_PER_TIER;
        }

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
        // Fully redundant with LGU's own Back Muscles, so in LGU-preferred mode this row is hidden
        // and every read below is 0. Stable id, never the display name: "back_muscles" normalizes
        // to the imported row's own title, and only the id can be asked for its mode.
        public const string UPGRADE_ID = "back_muscles";
        public const float WEIGHT_REDUCTION_PER_TIER = 0.20f;

        public static int GetTier() => Y4NGZUpgradeState.GetTier(UPGRADE_ID);
        public static bool IsUnlocked() => GetTier() > 0;
        public static float GetCarryWeightMultiplier()
        {
            return Mathf.Clamp01(1f - Mathf.Max(0, GetTier()) * WEIGHT_REDUCTION_PER_TIER);
        }

        // WP12 dead-code sweep: the `=> false` CanCarryTwoTwoHandedItems() stub is gone.
        // ExtraSlotUpgrade owns that promise (Deeper Pockets tier 3) and is what both call
        // sites read; a second always-false copy on Transporter only invited a wrong call.
    }

    internal static class ShadowStepUpgrade
    {
        public const string UPGRADE_ID = "shadow_step";
        public static float INVISIBILITY_DURATION => SurvivalAbilityRules.CloakDuration(GetTier());
        public const float COOLDOWN_DURATION = SurvivalAbilityRules.CloakCooldown;
        public const float REDUCED_DETECTION_MULTIPLIER = 0.75f;

        public static bool IsInvisible = false;
        public static float InvisibilityEndTime = 0f;
        public static float CooldownEndTime = 0f;

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_ID);

        // F-SHADOW-17: this sits on the enemy-sight hot path; one level read per answer.
        public static int GetTier() => Y4NGZUpgradeState.GetTier(UPGRADE_ID);
        public static bool HasActiveAbility() => GetTier() >= 1;
    }

    /// <summary>
    /// Resilience (#435): three ranks of raised health cap, the extended recovery band from
    /// rank 2 and the HUD reserve readout. LGU's Stimpack and Medical Nanobots replace all of it,
    /// so the row has no unique variant and contributes exactly vanilla 100 unless it is
    /// selling its full self.
    /// </summary>
    internal static class ResilienceUpgrade
    {
        public const string UPGRADE_ID = "thick_skin";

        public static int GetHealthTier() => Y4NGZUpgradeState.GetFullNativeTier(UPGRADE_ID);
    }

    internal static class DeathboundUpgrade
    {
        // F-TECH-1: the host has to confirm a dying client actually owns Deathbound before it
        // spares their far-left slot, which means the tier has to travel through UpgradeTierSync.
        public const string UPGRADE_ID = "deathbound";
        public const int PROTECTED_SLOT_INDEX = 0;

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_ID);
        public static int GetTier() => Y4NGZUpgradeState.GetTier(UPGRADE_ID);
    }

    // WP12 dead-code sweep (F-INFRA-13): GlowInTheDarkUpgrade is gone along with
    // GlowInTheDarkPatch. "Night Vision" is neither a catalog id nor a display name, so the
    // round-start hook created no light and the whole feature was unreachable.

    internal static class ExtraSlotUpgrade
    {
        // Stable id, never the display name: LGU's Deeper Pockets is an imported row with the
        // same title, and it sells the two-handed allowance rather than hotbar slots (#435).
        public const string UPGRADE_ID = "extra_inventory_slot";

        /// <summary>Full-native rank that unlocked the second two-handed item.</summary>
        public const int TWO_HANDED_TIER = 3;

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_ID);

        /// <summary>
        /// Hotbar slot rank. Unchanged in both modes: the unique variant's three ranks are the
        /// same five/six/seven slots.
        /// </summary>
        public static int GetTier() => Y4NGZUpgradeState.GetTier(UPGRADE_ID);

        /// <summary>
        /// The native second two-handed allowance, which the unique variant drops entirely: in
        /// LGU-preferred mode that capacity is LGU Deeper Pockets' to sell.
        /// </summary>
        public static bool CanCarryTwoTwoHandedItems() =>
            !Y4NGZUpgradeState.IsUniqueOnly(UPGRADE_ID) && GetTier() >= TWO_HANDED_TIER;
    }

    // F-FOREMAN-A-12: SquadSightUpgrade is gone. Y4NGZUpgradeCatalog migrates "squad_sight" into
    // "buddy_system" and no catalog entry is named "Squad Sight", so GetTier() was permanently 0
    // and nothing called it - SquadSightPatch reads BuddySystemUpgrade throughout.

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
        // F-ESCAPE-10: carry-weight penalty on the slide impulse, as
        // Clamp(BASE - (carryWeight - 1) * PENALTY_PER_UNIT, MIN, BASE) * SLIDE_FORCE.
        // The old 0.12 coefficient made "a heavy load weakens the slide" a 3-6% effect no
        // player could feel, and its 0.55 floor needed carryWeight 6.0 (unreachable). At 0.6
        // a realistic heavy four-slot loadout (carryWeight ~1.5-1.6) loses ~26-31% of the
        // impulse versus empty, and the floor is reached at carryWeight 1.92.
        public const float SLIDE_WEIGHT_BASE_MULTIPLIER = 1.15f;
        public const float SLIDE_WEIGHT_PENALTY_PER_CARRY_UNIT = 0.6f;
        public const float SLIDE_WEIGHT_MIN_MULTIPLIER = 0.6f;

        // Tier 2 mantle tuning.
        public const float MANTLE_RETRY_SECONDS = 0.05f;
        public const float MANTLE_COOLDOWN_SECONDS = 0.8f;
        public const float MANTLE_FORWARD_REACH = 1.35f;
        public const float MANTLE_MIN_LEDGE_HEIGHT = 0.95f;
        public const float MANTLE_MAX_LEDGE_HEIGHT = 3.0f;
        public const float MANTLE_TALL_THRESHOLD = 1.35f;
        public const float MANTLE_STAMINA_COST = 0.08f;
        // F-ESCAPE-8: the mantle charged 8% stamina without ever requiring it. Mirror the
        // slide's headroom (MIN_SPRINT_METER 0.16 against a 0.14 cost) so the cost is real.
        public const float MANTLE_MIN_SPRINT_METER = 0.10f;
        public const float MANTLE_ONE_METER_SECONDS = 0.667f;
        public const float MANTLE_TWO_METER_SECONDS = 1.3f;
        public const float MANTLE_CAMERA_DIP = 0.25f;
        public const float MANTLE_CAMERA_DIP_IN_SECONDS = 0.12f;
        public const float MANTLE_CAMERA_DIP_OUT_SECONDS = 0.18f;
        public const float MANTLE_BODY_LOWER_METERS = 0.25f;
        public const float MANTLE_BODY_LOWER_RAMP_OUT_FRACTION = 0.25f;

        // Tier 3 air jump tuning. F-ESCAPE-14: the cost used to be a bare literal in
        // EscapeArtistDoubleJumpPatch, so the catalog's "8%" had no single source of truth.
        public const float AIR_JUMP_STAMINA_COST = 0.08f;

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_ID);
        public static int GetTier() => Y4NGZUpgradeState.GetTier(UPGRADE_ID);
        public static bool HasDoubleJump() => GetTier() >= 3;
    }

    internal static class TurretHackerUpgrade
    {
        public const string UPGRADE_ID = "turret_hacker";

        /// <summary>
        /// Wire id of the per-player turret-hack capability in <c>UpgradeTierSync</c> (0 or 1).
        /// The host cannot authorise a turret hack from the synced RANK alone any more: full rank
        /// 2 must be refused and unique rank 2 must be accepted, and neither peer may read the
        /// other's preference. This is not a catalog row and has no save state (#435).
        /// </summary>
        public const string TURRET_HACK_CAPABILITY_ID = "turret_hack_capability";

        /// <summary>Full-native ranks behind each capability; the unique variant keeps 1 and 3.</summary>
        public const int CAMERA_TIER = 1;
        public const int DOOR_TIER = 2;
        public const int TURRET_TIER = 3;

        public const float COOLDOWN_SECONDS = 20f;
        /// <summary>Hold-interact duration for the camera hack and the door hack.</summary>
        public const float HACK_HOLD_SECONDS = 1.2f;

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_ID);
        public static int GetTier() => Y4NGZUpgradeState.GetTier(UPGRADE_ID);
        public static bool CanHackCameras() => Y4NGZUpgradeState.HasMilestone(UPGRADE_ID, CAMERA_TIER);

        // The one grade the unique variant drops: LGU's Locksmith opens locked doors in
        // LGU-preferred mode, so no unique rank may ever hack one.
        public static bool CanHackDoors() =>
            !Y4NGZUpgradeState.IsUniqueOnly(UPGRADE_ID) && GetTier() >= DOOR_TIER;

        // Full rank 3 or unique rank 2; UpgradeTierSync publishes this answer so the host can
        // authorise a remote hack without knowing which mode that client is in.
        public static bool CanHackTurrets() => Y4NGZUpgradeState.HasMilestone(UPGRADE_ID, TURRET_TIER);

        // F-TECH-3: the fuel-pump stall roll only ever runs on the host, so it has to resolve
        // the pump user's synced tier rather than read the local save state. Synced rank >= 1 is
        // the camera/pump milestone in both modes.
        public static float GetBreakChanceMultiplier(int tier) => tier >= CAMERA_TIER ? 0.5f : 1f;

        // F-TECH-10: every caller is gated on CanHackTurrets(), so the old "tier < 2 -> 30f"
        // branch was unreachable and contradicted the catalog's flat 90 s.
        public static float GetDuration() => 90f;
    }

    internal static class FieldOperationsUpgrade
    {
        public const string UPGRADE_ID = "field_operations";

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_ID);
        public static int GetTier() => Y4NGZUpgradeState.GetTier(UPGRADE_ID);
    }

    internal static class CourierDroneUpgrade
    {
        public const string UPGRADE_ID = "courier_drone";
        public const float COMMAND_COOLDOWN_SECONDS = 3f;
        public const float PICKUP_RADIUS = 10f;
        public const int MAX_ITEMS_PER_SWEEP = 12;
        public const float MAX_CARGO_WEIGHT_LB = 150f;

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_ID);
        public static int GetTier() => Y4NGZUpgradeState.GetTier(UPGRADE_ID);
    }

    internal static class BuddySystemUpgrade
    {
        public const string UPGRADE_ID = "buddy_system";
        private static readonly float[] RangeByTier = { 10f, 15f };
        private static readonly float[] BuffByTier = { 0.03f, 0.06f };

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_ID);
        public static int GetTier() => Y4NGZUpgradeState.GetTier(UPGRADE_ID);
        public static float GetRange() => GetRange(GetTier());
        public static float GetRange(int tier) => tier <= 0 ? 0f : RangeByTier[Mathf.Clamp(tier - 1, 0, RangeByTier.Length - 1)];
        public static float GetBuff() => GetBuff(GetTier());
        public static float GetBuff(int tier) => tier <= 0 ? 0f : BuffByTier[Mathf.Clamp(tier - 1, 0, BuffByTier.Length - 1)];
    }

    internal static class PingUpgrade
    {
        public const string UPGRADE_ID = "ping";
        public const float COOLDOWN_SECONDS = 3f;
        public const float MARK_DURATION = 5f;
        public const float TARGET_RANGE = 80f;
        public const float ENEMY_DAMAGE_REDUCTION = 0.20f;
        public const float TEAMMATE_DAMAGE_REDUCTION = 0.20f;

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_ID);
        public static int GetTier() => Y4NGZUpgradeState.GetTier(UPGRADE_ID);
        public static bool ReducesMarkedEnemyDamage() => GetTier() >= 2;
        public static bool CanMarkTeammates() => GetTier() >= 3;
    }

    internal static class CommandNetUpgrade
    {
        public const string UPGRADE_ID = "command_net";
        public const float DAMAGE_OUTLINE_SECONDS = 5f;
        public const float MINIMAP_RANGE_METERS = 80f;

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_ID);
        public static int GetTier() => Y4NGZUpgradeState.GetTier(UPGRADE_ID);
        public static bool HasVirtualWalkie() => GetTier() >= 1;
        public static bool HasDamageOutline() => GetTier() >= 2;
        public static bool HasMinimap() => GetTier() >= 3;
    }

    // F-FOREMAN-A-6: RallyCallUpgrade is gone. "Rally Call" resolved against neither an id nor a
    // display name in UpgradeCatalogTable, so IsUnlocked() was permanently false and every
    // consumer in ForemanSupportPatch was unreachable - while the failing name lookup allocated
    // ~150 strings per frame (F-FOREMAN-A-7).

    internal static class WorklightBeaconUpgrade
    {
        public const string UPGRADE_ID = "worklight_beacon";
        public const float COOLDOWN_SECONDS = 0.35f;
        public const float THROW_FORCE = 28f;
        public const float LIFE_SECONDS = 180f;

        // F-FOREMAN-B-20: the host will not relay more than this many live beacons per thrower.
        public const int MAX_LIVE_BEACONS_PER_THROWER = 9;

        private static readonly int[] ChargesByTier = { 3, 5, 9 };
        private static readonly float[] IntensityByTier = { 12f, 18f, 26f };
        private static readonly float[] RangeByTier = { 10f, 13f, 17f };
        // F-FOREMAN-B-7: HDRP authors point lights in photometric units. The legacy
        // IntensityByTier numbers are raw Light.intensity and are only the fallback when
        // HDAdditionalLightData cannot be attached; these are the lumen equivalents.
        private static readonly float[] LumensByTier = { 1200f, 1800f, 2600f };

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_ID);
        public static int GetTier() => Y4NGZUpgradeState.GetTier(UPGRADE_ID);
        public static int GetMaxCharges() => GetValue(ChargesByTier, GetTier());
        public static float GetIntensity(int tier) => GetValue(IntensityByTier, tier);
        public static float GetLumens(int tier) => GetValue(LumensByTier, tier);
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
        // F-FOREMAN-A-20: the host validates that a revive requester actually owns Inspire, which
        // needs the stable id for the UpgradeTierSync lookup.
        public const string UPGRADE_ID = "inspire";
        public const float REVIVE_RANGE = 7f;
        public const float REVIVE_CHANCE = 0.5f;
        public const float COOLDOWN_SECONDS = 75f;

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_ID);
        public static int GetTier() => Y4NGZUpgradeState.GetTier(UPGRADE_ID);
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
        public static int GetTier() => Y4NGZUpgradeState.GetTier(UPGRADE_ID);

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

    internal static class ProteinPowderUpgrade
    {
        // Stable-id lookup (display name is "Pumping Iron"), matching the modern accessor
        // pattern: the id is what the save file, the node and the icon key are keyed on,
        // and it survives a display rename the way "better_scanner"/"veteran" already had to.
        public const string UPGRADE_ID = "protein_powder";

        /// <summary>Full rank 1 and up: flat force added to a held melee weapon hit (vanilla shovel force is 1).</summary>
        public const int MELEE_FORCE_BONUS = 1;

        /// <summary>Full-native rank at which the force bonus unlocks.</summary>
        public const int FORCE_TIER = 1;

        /// <summary>Full-native rank at which the stagger roll unlocks; the unique variant's milestone.</summary>
        public const int STUN_TIER = 2;

        /// <summary>Tier 2: chance per landed melee hit to stagger the struck enemy.</summary>
        public const float STUN_CHANCE = 0.25f;

        /// <summary>Tier 2: stagger duration in seconds; at or below the shared relay clamp.</summary>
        public const float STUN_SECONDS = 1f;

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_ID);
        public static int GetTier() => Y4NGZUpgradeState.GetTier(UPGRADE_ID);

        /// <summary>
        /// The +1 melee force, which the unique variant drops entirely: LGU's Protein Powder is
        /// the melee-strength training in LGU-preferred mode, and Stagger is stun-only (#435).
        /// </summary>
        public static bool HasForceBonus() =>
            !Y4NGZUpgradeState.IsUniqueOnly(UPGRADE_ID) && GetTier() >= FORCE_TIER;

        /// <summary>The stagger roll, which unique rank 1 owns on its own terms.</summary>
        public static bool HasStagger() => Y4NGZUpgradeState.HasMilestone(UPGRADE_ID, STUN_TIER);
    }

    internal static class QuotaGuardUpgrade
    {
        // Stable-id lookup (display name is "Veteran"); tier effects are
        // host-resolved as the MAX tier across all connected clients via
        // UpgradeTierSync ("any crew member owns it").
        public const string UPGRADE_ID = "quota_guard";

        /// <summary>
        /// Tier 1 (vanilla): multiplier applied to the vanilla end-of-round credit fine for
        /// dead crew, i.e. the fine is halved.
        /// </summary>
        public const float DEATH_CREDIT_PENALTY_MULTIPLIER = 0.5f;

        /// <summary>
        /// Tier 1 (Y4NGZCompany bonus): crew-death quota penalty cap, as a fraction of the
        /// pre-penalty quota per round-end. Opportunistic - it only binds when that mod is present.
        /// </summary>
        public const float DEATH_PENALTY_FRACTION_CAP = 0.10f;

        /// <summary>Tier 2: quota deadline in days (vanilla default is 3).</summary>
        public const int EXTENDED_DEADLINE_DAYS = 4;

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_ID);
        public static int GetTier() => Y4NGZUpgradeState.GetTier(UPGRADE_ID);
        public static bool HasCreditPenaltyRelief() => GetTier() >= 1;
        public static bool HasQuotaCap() => GetTier() >= 1;
        public static bool HasExtendedDeadline() => GetTier() >= 2;
    }

    internal static class ChameleonUpgrade
    {
        public const string UPGRADE_ID = "chameleon";

        private static readonly float[] ExtraDetectionTimeByTier = { 0.15f, 0.30f, 0.50f };

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_ID);
        public static int GetTier() => Y4NGZUpgradeState.GetTier(UPGRADE_ID);

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
        public const string UPGRADE_ID = "scavenger";

        private static readonly float[] BonusByTier = { 0.10f, 0.20f, 0.30f };

        public static bool IsUnlocked() => Y4NGZUpgradeState.GetActiveUpgrade(UPGRADE_ID);
        public static int GetTier() => Y4NGZUpgradeState.GetTier(UPGRADE_ID);

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
