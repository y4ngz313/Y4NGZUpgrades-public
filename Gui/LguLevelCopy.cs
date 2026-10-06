using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Y4NGZUpgrades.Gui;

/// <summary>
/// Numbered copy for the imported Late Game Upgrades rows (#442). The catalog row IS the copy
/// (#366), but an imported row's per-level magnitudes are Late Game Upgrades settings, so the
/// row's <c>"Description: ...\nLevel N: ..."</c> block is generated here from the values its
/// own effect code reads, and the ordinary <see cref="UpgradeLevelCopy"/> parser draws it.
///
/// Unity-free and reflection-free: the caller supplies a reader over the upgrade's live
/// configuration object (members named as LGU declares them, list slots as <c>Name[i]</c>), so
/// the menu checks exercise this exact text. Any missing member, non-number or non-finite value
/// makes <see cref="TryDescribe"/> return false and the caller keeps the static row copy.
///
/// Late Game Upgrades 3.14.1 has formula bugs (PLAN.md, formula-bug verification). A row whose
/// levels all work but differ from LGU's own text shows the true numbers, and only on the exact
/// version they were verified against (<c>quirkAccurate</c>); any other version gets LGU's
/// nominal formula. A row with a dead level or clause lists what LGU advertises for the whole
/// row, so its levels stay monotonic. No line ever claims a level does nothing.
/// </summary>
internal static class LguLevelCopy
{
    /// <summary>Reads one configuration member's live value; false when it does not exist.</summary>
    internal delegate bool ValueReader(string member, out object value);

    private const double VanillaMovementSpeed = 4.6;
    private const double VanillaJumpForce = 13;
    private const double VanillaSprintSeconds = 11;
    private const double VanillaClimbSpeed = 4;
    private const double VanillaAirSeconds = 12;
    private const double VanillaCrouchDivisor = 1.5;
    private const int VanillaStingDamage = 10;
    private const int VanillaRegenCap = 20;
    private const double HollowPointReferenceDamage = 5;

    /// <param name="incomingFirearmResistance">
    /// Bullet Resistance also reduces the firearm hits Better Armory resolves for this player, so
    /// its copy speaks of firearms rather than only turrets and shotguns.
    /// </param>
    internal static bool TryDescribe(
        string upgradeId,
        int levels,
        bool quirkAccurate,
        bool incomingFirearmResistance,
        ValueReader read,
        out string text)
    {
        text = null;
        if (string.IsNullOrEmpty(upgradeId) || levels < 1 || read == null)
            return false;

        var values = new Values(read);
        var lines = new string[levels];
        string description = Compose(upgradeId, levels, quirkAccurate, incomingFirearmResistance, values, lines);
        if (description == null || !values.Valid)
            return false;

        var builder = new StringBuilder("Description: ").Append(description);
        for (int level = 1; level <= levels; level++)
        {
            string line = lines[level - 1];
            if (string.IsNullOrWhiteSpace(line)
                || line.IndexOf("no effect", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            builder.Append("\nLevel ").Append(level.ToString(CultureInfo.InvariantCulture))
                .Append(": ").Append(line);
        }

        string composed = builder.ToString();
        if (!RoundTrips(composed, description, lines))
            return false;

        text = composed;
        return true;
    }

    /// <summary>
    /// The generated block must survive the parser unchanged: the description may not read as a
    /// level line, and no effect may lose a leading token to the price stripper.
    /// </summary>
    private static bool RoundTrips(string composed, string description, string[] lines)
    {
        Dictionary<int, string> parsed = UpgradeLevelCopy.ExtractLevelEffects(
            composed, string.Empty, lines.Length, out string lore);
        if (parsed.Count != lines.Length
            || !string.Equals(
                UpgradeLevelCopy.CleanDescriptionText(lore),
                UpgradeLevelCopy.CleanDescriptionText(description),
                StringComparison.Ordinal))
        {
            return false;
        }

        for (int level = 1; level <= lines.Length; level++)
        {
            if (!parsed.TryGetValue(level, out string effect)
                || !string.Equals(effect, UpgradeLevelCopy.CleanEffectText(lines[level - 1]), StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Fills one line per level and returns the description, or null for an unknown row.</summary>
    private static string Compose(
        string id, int levels, bool quirkAccurate, bool incomingFirearmResistance, Values v, string[] lines)
    {
        switch (id)
        {
            // - Enforcer -
            case "lgu_back_muscles":
                return BackMuscles(levels, v, lines);
            case "lgu_stimpack":
            {
                double start = v.Num("InitialEffect"), step = v.Num("IncrementalEffect");
                Fill(lines, i => $"Maximum health {N(100 + start + i * step)}.");
                return "Raises your maximum health (base 100).";
            }
            case "lgu_protein_powder":
            {
                // Advertised row: LGU 3.14.1 never rolls the crit (S2), but LGU sells it for the
                // top level, so the whole row lists what LGU advertises.
                double start = v.Num("InitialEffects[0]"), step = v.Num("IncrementalEffects[0]");
                double crit = v.Num("InitialSecondEffects[0]") * 100;
                Fill(lines, i =>
                {
                    string force = $"Melee hits +{N(start + i * step)} force";
                    return i == levels - 1 && crit > 0
                        ? $"{force}, with a {P(crit)} chance to kill outright."
                        : force + ".";
                });
                return "Shovel, sign and kitchen knife hits land harder.";
            }
            case "lgu_deeper_pockets":
            {
                double start = v.Num("InitialEffect"), step = v.Num("IncrementalEffect");
                Fill(lines, i => $"Carry {N(1 + start + i * step)} two-handed items.");
                return "Carry more than one two-handed item at a time.";
            }
            case "lgu_explosion_resistance":
            {
                double start = v.Num("InitialEffect"), step = v.Num("IncrementalEffect");
                Fill(lines, i => $"Explosions deal {P(Clamp(start + i * step, 0, 100))} less damage to you.");
                return "Explosions hurt you less.";
            }
            case "lgu_bullet_resistance":
            {
                double start = v.Num("InitialEffect"), step = v.Num("IncrementalEffect");
                if (incomingFirearmResistance)
                {
                    Fill(lines, i => $"Turret, shotgun and other firearm bullets deal {P(Clamp(start + i * step, 0, 100))} less damage to you.");
                    return "Bullets from turrets and firearms hurt you less.";
                }

                Fill(lines, i => $"Turret and shotgun bullets deal {P(Clamp(start + i * step, 0, 100))} less damage to you.");
                return "Turret and shotgun bullets hurt you less.";
            }
            case "lgu_hollow_point":
            {
                // The percentage Better Armory applies: GetHollowPointDamageBoost(5) / 5.
                double start = v.Num("InitialEffect"), step = v.Num("IncrementalEffect");
                Fill(lines, i => $"Firearm damage +{P((start + i * step) / HollowPointReferenceDamage * 100)}.");
                return "Your firearms deal more damage.";
            }
            case "lgu_long_barrel":
            {
                double start = v.Num("InitialEffect"), step = v.Num("IncrementalEffect");
                Fill(lines, i => $"Firearm range +{P(start + i * step)}.");
                return "Longer firearm range and damage falloff.";
            }
            case "lgu_sleight_of_hand":
            {
                // S11: 3.14.1 scales each reload wait by (1 - X/200), which is what Better Armory
                // applies too, so the time cut is true for every firearm.
                double start = v.Num("InitialEffect"), step = v.Num("IncrementalEffect");
                Fill(lines, i => quirkAccurate
                    ? $"Firearm reloads take {P(Clamp((start + i * step) / 2, 0, 100))} less time."
                    : $"Firearm reload speed +{P(start + i * step)}.");
                return "Faster firearm reloads.";
            }
            case "lgu_silver_bullets":
                return OneLevel(levels, lines, "Firearm hits can kill the Ghost Girl.",
                    "Your firearms can kill the Ghost Girl.");

            // - Ghost -
            case "lgu_running_shoes":
            {
                // Advertised row: the top-level footstep reduction never fires in 3.14.1 (S1).
                double start = v.Num("InitialEffects[0]"), step = v.Num("IncrementalEffects[0]");
                double noise = v.Num("InitialEffects[1]");
                Fill(lines, i =>
                {
                    double add = start + i * step;
                    string speed = $"Movement speed +{N(add)} (+{W(add / VanillaMovementSpeed * 100)}%)";
                    return i == levels - 1 && noise > 0
                        ? $"{speed}; footstep noise range {N(noise)}m shorter."
                        : speed + ".";
                });
                return noise > 0 ? "Run faster. At the top level your footsteps carry less far." : "Run faster.";
            }
            case "lgu_strong_legs":
            {
                double start = v.Num("InitialEffect"), step = v.Num("IncrementalEffect");
                Fill(lines, i =>
                {
                    double add = start + i * step;
                    return $"Jump force +{N(add)} (+{W(add / VanillaJumpForce * 100)}%).";
                });
                return "Jump higher.";
            }
            case "lgu_bigger_lungs":
                return BiggerLungs(levels, quirkAccurate, v, lines);
            case "lgu_carbon_kneejoints":
                return CarbonKneejoints(quirkAccurate, v, lines);
            case "lgu_hiking_boots":
            {
                double start = v.Num("InitialEffect"), step = v.Num("IncrementalEffect");
                Fill(lines, i =>
                {
                    double less = Clamp(start + i * step, 0, 100);
                    return less >= 100 ? "Uphill slopes no longer slow you." : $"Uphill slopes slow you {P(less)} less.";
                });
                return "Uphill slopes slow you less.";
            }
            case "lgu_reinforced_boots":
            {
                double start = v.Num("InitialEffect"), step = v.Num("IncrementalEffect");
                Fill(lines, i => $"Falls and jetpack crashes hurt {P(Clamp(start + i * step, 0, 100))} less.");
                return "Falls and jetpack crashes hurt you less.";
            }
            case "lgu_rubber_boots":
            {
                // Advertised row until #440 reads the real water and quicksand values (S7).
                double start = v.Num("InitialEffect"), step = v.Num("IncrementalEffect");
                Fill(lines, i => $"Water and quicksand hinder you {P(Clamp(start + i * step, 0, 100))} less.");
                return "Water and quicksand slow you less. Sinking is unchanged.";
            }
            case "lgu_traction_boots":
            {
                double start = v.Num("InitialEffect"), step = v.Num("IncrementalEffect");
                Fill(lines, i => $"Traction +{P(start + i * step)}.");
                return "Better grip for turning and stopping.";
            }
            case "lgu_climbing_gloves":
            {
                double start = v.Num("InitialEffect"), step = v.Num("IncrementalEffect");
                Fill(lines, i =>
                {
                    double add = start + i * step;
                    return $"Ladder speed +{N(add)} (+{W(add / VanillaClimbSpeed * 100)}%).";
                });
                return "Climb ladders faster.";
            }
            case "lgu_oxygen_canisters":
            {
                double start = v.Num("InitialEffect"), step = v.Num("IncrementalEffect");
                Fill(lines, i =>
                {
                    double less = Clamp(start + i * step, 0, 100);
                    return less >= 100
                        ? "Oxygen never runs out underwater."
                        : $"Oxygen lasts {N1(VanillaAirSeconds / (1 - less / 100))}s underwater (base 12s).";
                });
                return "Hold your breath longer underwater.";
            }
            case "lgu_clay_glasses":
            {
                double start = v.Num("InitialEffect"), step = v.Num("IncrementalEffect");
                Fill(lines, i => $"Spot the Clay Surgeon from {N(start + i * step)}m farther away.");
                return "See the Clay Surgeon from farther away.";
            }

            // - Technician -
            case "lgu_better_scanner":
                return BetterScanner(levels, v, lines);
            case "lgu_quick_hands":
            {
                // Advertised row: 3.14.1 truncates the multiplier, so lower levels do nothing (S4).
                double start = v.Num("InitialEffect"), step = v.Num("IncrementalEffect");
                Fill(lines, i => $"Hold interactions {P(start + i * step)} faster.");
                return "Faster hold interactions.";
            }
            case "lgu_mechanical_arms":
            {
                double start = v.Num("InitialEffect"), step = v.Num("IncrementalEffect");
                Fill(lines, i => $"Reach +{N(start + i * step)}m.");
                return "Grab and interact from farther away.";
            }
            case "lgu_lithium_batteries":
            {
                double start = v.Num("InitialEffect"), step = v.Num("IncrementalEffect");
                Fill(lines, i => $"Battery items drain {P(Clamp(start + i * step, 0, 100))} slower.");
                return "Battery items you use drain slower.";
            }
            case "lgu_nv_headset_batteries":
                return NightVision(v, lines);
            case "lgu_locksmith":
                return OneLevel(levels, lines,
                    v.Flag("DisableDoorCollision")
                        ? "Interact with a locked door to pick it with a pin minigame."
                        : "Walk into a locked door to pick it with a pin minigame.",
                    "Pick locked doors by hand.");
            case "lgu_aluminium_coils":
            {
                double difficulty = v.Num("InitialEffects[0]"), difficultyStep = v.Num("IncrementalEffects[0]");
                double cooldown = v.Num("InitialEffects[1]"), cooldownStep = v.Num("IncrementalEffects[1]");
                double stun = v.Num("InitialSecondEffects[0]"), stunStep = v.Num("IncrementalSecondEffects[0]");
                double range = v.Num("InitialSecondEffects[1]"), rangeStep = v.Num("IncrementalSecondEffects[1]");
                Fill(lines, i =>
                    $"Zap gun range +{N(range + i * rangeStep)}m, stun +{N(stun + i * stunStep)}s, "
                    + $"minigame {P(Clamp(difficulty + i * difficultyStep, 0, 100))} easier, "
                    + $"cooldown {P(Clamp(cooldown + i * cooldownStep, 0, 100))} shorter.");
                return "Your zap gun reaches farther, stuns longer, is easier to hold and recovers sooner.";
            }
            case "lgu_jet_fuel":
            {
                double start = v.Num("InitialEffect"), step = v.Num("IncrementalEffect");
                Fill(lines, i => $"Jetpack acceleration +{P(start + i * step)}.");
                return "Your jetpack accelerates faster.";
            }
            case "lgu_jetpack_thrusters":
            {
                double start = v.Num("InitialEffect"), step = v.Num("IncrementalEffect");
                Fill(lines, i => $"Jetpack top speed +{P(start + i * step)}.");
                return "Your jetpack reaches a higher top speed.";
            }

            // - Foreman -
            case "lgu_beekeeper":
            {
                // LGU works in float and truncates to int: (int)(10 * (0.64 - 0.15i)).
                float start = (float)v.Num("InitialEffects[0]"), step = (float)v.Num("IncrementalEffects[0]");
                double hive = v.Num("InitialEffects[1]");
                Fill(lines, i =>
                {
                    int sting = Math.Max(0, Math.Min(VanillaStingDamage,
                        (int)(VanillaStingDamage * (start - i * step))));
                    string damage = $"Stings deal {sting} damage (base {VanillaStingDamage})";
                    double bonus = (hive - 1) * 100;
                    return i == levels - 1 && bonus > 0
                        ? $"{damage}; hives spawn worth {P(bonus)} more."
                        : damage + ".";
                });
                return "Bee and Butler hornet stings hurt you less.";
            }
            case "lgu_medical_nanobots":
            {
                double start = v.Num("InitialEffect"), step = v.Num("IncrementalEffect");
                Fill(lines, i =>
                    $"Natural healing continues up to {VanillaRegenCap + (int)(VanillaRegenCap * (start + i * step) / 100)} HP (base {VanillaRegenCap}).");
                return "Natural healing continues to a higher health. How much each tick heals does not change.";
            }
            case "lgu_effective_bandaids":
            {
                double start = v.Num("InitialEffect"), step = v.Num("IncrementalEffect");
                Fill(lines, i => $"Natural healing restores {N(1 + start + i * step)} HP per tick (base 1).");
                return "Natural healing restores more health per tick. Your maximum health and how far natural healing goes do not change.";
            }
            case "lgu_walkie_gps":
                return OneLevel(levels, lines, "Holding a walkie-talkie shows your position and the time.",
                    "A held walkie-talkie shows where you are.");
            case "lgu_sick_beats":
                return SickBeats(levels, v, lines);
            case "lgu_tzp_buffer":
            {
                double buff = v.Num("InitialEffects[0]"), buffStep = v.Num("IncrementalEffects[0]");
                double relief = v.Num("InitialEffects[1]"), reliefStep = v.Num("IncrementalEffects[1]");
                Fill(lines, i =>
                    $"TZP works {P(buff + i * buffStep)} better with {P(Clamp(relief + i * reliefStep, 0, 100))} weaker side effects.");
                return "TZP helps more and blurs less.";
            }
            case "lgu_weed_genetic_manipulation":
            {
                // Advertised row: 3.14.1's first level is clamped to no change (S5).
                double start = v.Num("InitialEffect"), step = v.Num("IncrementalEffect");
                Fill(lines, i => $"Weed Killer is {P(start + i * step)} more effective on plants.");
                return "Your Weed Killer clears plants faster.";
            }
            case "lgu_fedora_suit":
                return OneLevel(levels, lines, "Butlers do not attack you for being alone.",
                    "Butlers never treat you as alone. They still fight back if hit.");
            default:
                return null;
        }
    }

    private static string BackMuscles(int levels, Values v, string[] lines)
    {
        double start = v.Num("InitialEffect"), step = v.Num("IncrementalEffect");
        string mode = v.Text("AlternativeMode");
        Func<double, string> line;
        string description;
        switch (mode)
        {
            case "ReduceWeight":
                line = less => $"Carried items weigh {P(less)} less.";
                description = "Scrap you carry weighs less.";
                break;
            case "ReduceCarryInfluence":
                line = less => $"Carried weight slows you {P(less)} less.";
                description = "Carried weight slows your sprint less.";
                break;
            case "ReduceCarryStrain":
                line = less => $"Carried weight drains {P(less)} less stamina.";
                description = "Carried weight drains less stamina.";
                break;
            default:
                return null;
        }

        Fill(lines, i => line(Clamp((1 - (start - i * step)) * 100, 0, 100)));
        return description;
    }

    private static string BiggerLungs(int levels, bool quirkAccurate, Values v, string[] lines)
    {
        double sprint = v.Num("InitialEffects[0]"), sprintStep = v.Num("IncrementalEffects[0]");
        double regen = v.Num("InitialEffects[1]"), regenStep = v.Num("IncrementalEffects[1]");
        double jump = v.Num("InitialEffects[2]"), jumpStep = v.Num("IncrementalEffects[2]");
        double regenLevel = v.Num("StaminaRegenerationLevel"), jumpLevel = v.Num("JumpReductionLevel");
        bool anyRegen = false, anyJump = false;
        Fill(lines, i =>
        {
            int level = i + 1;
            var line = new StringBuilder($"Sprint time +{N(sprint + i * sprintStep)}s (base {N(VanillaSprintSeconds)}s)");
            if (level >= regenLevel)
            {
                // S3: 3.14.1 multiplies by |i - level - 1| where it means |i - (level - 1)|.
                double factor = quirkAccurate
                    ? regen + regenStep * Math.Abs(i - regenLevel - 1)
                    : regen + regenStep * (level - regenLevel);
                double percent = (Clamp(factor, 0, 10) - 1) * 100;
                if (Math.Abs(percent) >= 0.05)
                {
                    line.Append("; stamina regen ").Append(Signed(percent));
                    anyRegen = true;
                }
            }

            if (level >= jumpLevel)
            {
                double factor = quirkAccurate
                    ? jump - jumpStep * Math.Abs(i - jumpLevel - 1)
                    : jump - jumpStep * (level - jumpLevel);
                double less = (1 - Clamp(factor, 0, 10)) * 100;
                if (less >= 0.05)
                {
                    line.Append("; jumps cost ").Append(P(less)).Append(" less stamina");
                    anyJump = true;
                }
                else if (less <= -0.05)
                {
                    line.Append("; jumps cost ").Append(P(-less)).Append(" more stamina");
                    anyJump = true;
                }
            }

            return line.Append('.').ToString();
        });

        var parts = new List<string> { "More sprint time" };
        if (anyRegen) parts.Add("faster stamina recovery");
        if (anyJump) parts.Add("cheaper jumps");
        return JoinList(parts) + ".";
    }

    private static string CarbonKneejoints(bool quirkAccurate, Values v, string[] lines)
    {
        double start = v.Num("InitialEffect"), step = v.Num("IncrementalEffect");
        if (!quirkAccurate)
        {
            Fill(lines, i => $"Crouching slows you {P(Clamp(start + i * step, 0, 100))} less.");
            return "Move faster while crouching.";
        }

        // S6: 3.14.1 rewrites vanilla's crouch divisor 1.5 to 1.5 + 0.5 * (1 - m), so everyone
        // with Late Game Upgrades crouches at 50% and the upgrade recovers only part of it.
        double top = 0;
        Fill(lines, i =>
        {
            double reduction = Clamp((start + i * step) / 100, 0, 1);
            double speed = 100 / (VanillaCrouchDivisor + (1 - reduction) * (VanillaCrouchDivisor - 1));
            top = Math.Max(top, speed);
            return $"Crouch at {W(speed)}% of walking speed.";
        });
        return top < 66.5
            ? "Move faster while crouching. Late Game Upgrades slows every crouch to 50% of walking speed, and even the top level stays below vanilla's 67%."
            : "Move faster while crouching. Late Game Upgrades slows every crouch to 50% of walking speed.";
    }

    private static string BetterScanner(int levels, Values v, string[] lines)
    {
        if (levels > 3)
            return null;

        double node = v.Num("NodeRangeIncrease"), outside = v.Num("OutsideNodesRangeIncrease");
        bool enemies = v.Flag("SeeEnemiesThroughWalls");
        string[] authored =
        {
            $"Scan range +{N(node)}m; ship and main entrance +{N(outside)}m.",
            "Adds terminal scan commands for players, enemies, scrap, hives and doors.",
            enemies ? "Also scans scrap and enemies through walls." : "Also scans scrap through walls."
        };
        Array.Copy(authored, lines, levels);

        var parts = new List<string> { "Longer scans" };
        if (levels >= 2) parts.Add("terminal scan commands");
        if (levels >= 3) parts.Add(enemies ? "scrap and enemies through walls" : "scrap through walls");
        return JoinList(parts) + ".";
    }

    private static string NightVision(Values v, string[] lines)
    {
        double charge = v.Num("InitialEffects[0]"), chargeStep = v.Num("IncrementalEffects[0]");
        double regen = v.Num("InitialEffects[1]"), regenStep = v.Num("IncrementalEffects[1]");
        double drain = v.Num("InitialEffects[2]"), drainStep = v.Num("IncrementalEffects[2]");
        double rangeStep = v.Num("IncrementalEffects[3]"), brightnessStep = v.Num("IncrementalEffects[4]");
        Fill(lines, i =>
        {
            // The battery terms scale with the owned level (i + 1); range and brightness with i.
            int level = i + 1;
            double capacity = charge + level * chargeStep;
            double use = drain - level * drainStep;
            double refill = regen + level * regenStep;
            var line = new StringBuilder(use > 0
                ? $"Goggles run {N1(capacity / use)}s per charge"
                : "Goggles never run out of charge");
            if (refill > 0)
                line.Append(" and refill in ").Append(N1(capacity / refill)).Append('s');
            if (Math.Abs(i * rangeStep) > 0)
                line.Append("; light range ").Append(SignedNumber(i * rangeStep));
            if (Math.Abs(i * brightnessStep) > 0)
                line.Append("; brightness ").Append(SignedNumber(i * brightnessStep));
            return line.Append('.').ToString();
        });

        return drain > 0 && regen > 0
            ? $"Night vision goggles run longer and refill faster (base {N1(charge / drain)}s run, {N1(charge / regen)}s refill). The goggles are bought separately."
            : "Night vision goggles run longer and refill faster. The goggles are bought separately.";
    }

    private static string SickBeats(int levels, Values v, string[] lines)
    {
        double radius = v.Num("Radius");
        var clauses = new List<string>();
        if (v.Flag("EnableSpeed"))
            clauses.Add($"speed +{N(v.Num("SpeedBoost"))}");
        if (v.Flag("EnableDamage"))
            clauses.Add($"melee hits +{N(v.Num("DamageBoost"))} force");
        if (v.Flag("EnableDefense"))
            clauses.Add($"damage taken {Signed((v.Num("DefenseBoost") - 1) * 100)}");
        if (v.Flag("EnableStaminaRegen"))
            clauses.Add($"stamina regen {Signed((v.Num("StaminaRegenBoost") - 1) * 100)}");
        if (clauses.Count == 0)
            return null;

        return OneLevel(levels, lines, $"Within {N(radius)}m of music: {JoinList(clauses)}.",
            "Music from a boombox or Cruiser radio buffs you.");
    }

    private static string OneLevel(int levels, string[] lines, string line, string description)
    {
        if (levels != 1)
            return null;
        lines[0] = line;
        return description;
    }

    private static void Fill(string[] lines, Func<int, string> line)
    {
        for (int i = 0; i < lines.Length; i++)
            lines[i] = line(i);
    }

    private static string JoinList(List<string> parts)
    {
        if (parts.Count == 1)
            return parts[0];
        return string.Join(", ", parts.GetRange(0, parts.Count - 1)) + " and " + parts[parts.Count - 1];
    }

    private static double Clamp(double value, double min, double max) =>
        value < min ? min : value > max ? max : value;

    private static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private static string N1(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
    private static string W(double value) => Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
    private static string P(double percent) => N1(percent) + "%";
    private static string Signed(double percent) => (percent >= 0 ? "+" : "-") + P(Math.Abs(percent));
    private static string SignedNumber(double value) => (value >= 0 ? "+" : "-") + N(Math.Abs(value));

    /// <summary>Typed access over the reader; any miss or non-finite value poisons the row.</summary>
    private sealed class Values
    {
        private readonly ValueReader _read;

        internal Values(ValueReader read) { _read = read; }

        internal bool Valid { get; private set; } = true;

        internal double Num(string member)
        {
            if (_read(member, out object value) && value is IConvertible convertible
                && !(value is bool) && !(value is string) && !(value is Enum))
            {
                double number = convertible.ToDouble(CultureInfo.InvariantCulture);
                if (!double.IsNaN(number) && !double.IsInfinity(number))
                    return number;
            }

            Valid = false;
            return 0;
        }

        internal bool Flag(string member)
        {
            if (_read(member, out object value) && value is bool flag)
                return flag;
            Valid = false;
            return false;
        }

        internal string Text(string member)
        {
            if (_read(member, out object value) && value != null)
                return value.ToString();
            Valid = false;
            return string.Empty;
        }
    }
}
