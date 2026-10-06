using System;
using System.Collections.Generic;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades
{
    [Flags]
    internal enum OptionalUpgradeProvider
    {
        None = 0,
        Contracted = 1 << 0,
        LethalCctv = 1 << 1,
        BetterArmory = 1 << 2,
        NativeInventory = 1 << 3,
        ShipSystems = 1 << 4,

        /// <summary>
        /// Late Game Upgrades (#435). Unlike the others this flag also requires
        /// <c>LguUpgradeBridge</c> to have installed cleanly, so a drifted signature hides
        /// the rows instead of selling a rank that cannot apply.
        /// </summary>
        LateGameUpgrades = 1 << 5
    }

    /// <summary>
    /// The single authored description of every upgrade: identity, skill-tree placement, sub-tier
    /// count, and prose. Token prices are not authored here - they are derived from the tree tier
    /// (#217), so an upgrade in tier T costs T to unlock and one more token for each sub-tier after
    /// that. One row builds both the skill-tree node and the upgrade definition, so a node's tier
    /// and its price can never drift apart.
    ///
    /// Unity-free on purpose: the deterministic checks sweep this exact table rather than a
    /// transcription of it.
    /// </summary>
    internal static class UpgradeCatalogTable
    {
        /// <summary>
        /// Tree-investment required to unlock each tier, indexed by (tier - 1). Retuned in #217
        /// from 0/2/5/8 to 0/3/7/11 to match the tier-derived price curve.
        /// </summary>
        internal static readonly int[] DefaultTierGates = { 0, 3, 7, 11 };

        /// <summary>The full gate ladder a tree registers, indexed by (tier - 1).</summary>
        internal static int[] GatesFor(Y4NGZSkillTreeClass treeClass)
        {
            if (DerivedTierGates.TryGetValue(treeClass, out int[] gates))
                return gates;

            throw new InvalidOperationException($"No gate ladder for skill tree '{treeClass}'.");
        }

        /// <summary>
        /// The gate ladder for a tree when only the rows that pass <paramref name="suppliesLevels"/>
        /// sell levels. This is how a per-upgrade `Enabled = false` (#417) keeps the tree
        /// reachable: a disabled row supplies nothing, so every gate above it clamps down to what
        /// is still purchasable. The shipped ladder is this call with the always-available rows.
        /// </summary>
        internal static int[] GatesFor(Y4NGZSkillTreeClass treeClass, Func<Entry, bool> suppliesLevels)
        {
            if (suppliesLevels == null)
                throw new ArgumentNullException(nameof(suppliesLevels));

            return GatesFor(treeClass, entry => suppliesLevels(entry) ? entry.SubTierCount : 0);
        }

        /// <summary>
        /// The gate ladder for a tree where <paramref name="levelsSupplied"/> returns how many
        /// levels an entry contributes (0 = supplies nothing). This variant supports entries whose
        /// effective rank count differs from their authored <see cref="Entry.SubTierCount"/>.
        /// </summary>
        internal static int[] GatesFor(Y4NGZSkillTreeClass treeClass, Func<Entry, int> levelsSupplied)
        {
            if (levelsSupplied == null)
                throw new ArgumentNullException(nameof(levelsSupplied));

            var gates = new int[DefaultTierGates.Length];
            int availableBelow = 0;
            for (int tier = 1; tier <= gates.Length; tier++)
            {
                gates[tier - 1] = Math.Min(DefaultTierGates[tier - 1], availableBelow);
                for (int i = 0; i < Ordered.Length; i++)
                {
                    Entry entry = Ordered[i];
                    if (entry.TreeClass == treeClass && entry.Tier == tier)
                        availableBelow += levelsSupplied(entry);
                }
            }

            return gates;
        }

        /// <summary>
        /// Rows that are always available: no optional-provider requirement. Optional rows are
        /// excluded from the gate supply so the ladder is identical in every supported mod
        /// combination.
        /// </summary>
        internal static bool AlwaysAvailable(Entry entry)
        {
            return entry != null && !entry.HasOptionalProviderRequirement;
        }

        /// <summary>
        /// Levels a row contributes to its tree's registered gate ladder. Optional-provider rows
        /// supply nothing so the ladder is identical in every mod combination, a hidden row
        /// supplies nothing either, and a unique-only variant or a live ladder supplies only the
        /// levels it really sells - a gate can never ask for more investment than exists below it
        /// (#435, #441). Player-disabled rows are the caller's to exclude.
        /// </summary>
        internal static int GateSupply(
            Entry entry,
            NativeFamilyMode mode,
            bool fieldMechanicProvidersInstalled)
        {
            if (!AlwaysAvailable(entry) || mode == NativeFamilyMode.Hidden)
                return 0;

            LiveRankLadder ladder = NativeUpgradeFamilies.LiveLadderFor(
                entry.Id, mode, entry.MaxTier, fieldMechanicProvidersInstalled);
            if (ladder != null)
                return ladder.LiveCount;

            return mode == NativeFamilyMode.UniqueOnly
                   && NativeUpgradeFamilies.TryGet(entry.Id, out NativeUpgradeFamily family)
                   && family.UniqueVariant != null
                ? family.UniqueVariant.RankCount
                : entry.SubTierCount;
        }

        /// <summary>
        /// Closes a set of player-disabled upgrade ids over functional dependencies: a row whose
        /// <see cref="Entry.DependsOnUpgradeIds"/> names a disabled row is disabled too, because it
        /// cannot work without it (Courier Drone is flown from the Field Operations tablet;
        /// Inspire is a Ping variant). Each cascade is reported through
        /// <paramref name="cascaded"/> as (dependent id, disabled dependency id) so the log can
        /// say why a row the player did not touch went missing. Unknown ids are ignored.
        /// </summary>
        internal static HashSet<string> ExpandDisabled(
            IEnumerable<string> disabledIds,
            List<KeyValuePair<string, string>> cascaded = null)
        {
            var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (disabledIds != null)
            {
                foreach (string id in disabledIds)
                {
                    if (!string.IsNullOrWhiteSpace(id) && ById.ContainsKey(id.Trim()))
                        disabled.Add(id.Trim());
                }
            }

            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 0; i < Ordered.Length; i++)
                {
                    Entry entry = Ordered[i];
                    if (disabled.Contains(entry.Id))
                        continue;

                    for (int d = 0; d < entry.DependsOnUpgradeIds.Length; d++)
                    {
                        string dependency = entry.DependsOnUpgradeIds[d];
                        if (!disabled.Contains(dependency))
                            continue;

                        disabled.Add(entry.Id);
                        cascaded?.Add(new KeyValuePair<string, string>(entry.Id, dependency));
                        changed = true;
                        break;
                    }
                }
            }

            return disabled;
        }

        /// <summary>
        /// Levels that must already be bought in a tree before <paramref name="tier"/> opens.
        /// Throws for a tier the ladder does not cover: that is an authoring mistake, not a tier
        /// that happens to be free.
        /// </summary>
        internal static int GateForTier(Y4NGZSkillTreeClass treeClass, int tier)
        {
            if (tier <= 1)
                return 0;

            int[] gates = GatesFor(treeClass);
            int index = tier - 1;
            if (index >= gates.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(tier), $"Skill tree '{treeClass}' has no gate authored for tier {tier}.");
            }

            return gates[index];
        }

        internal sealed class Entry
        {
            internal Entry(
                string id,
                string displayName,
                Y4NGZSkillTreeClass treeClass,
                int tier,
                int column,
                int row,
                int subTierCount,
                string iconKey,
                string description,
                string[] prerequisiteUpgradeIds = null,
                string[] connectionUpgradeIds = null,
                OptionalUpgradeProvider requiredProviders = OptionalUpgradeProvider.None,
                OptionalUpgradeProvider anyProviders = OptionalUpgradeProvider.None,
                string[] dependsOnUpgradeIds = null,
                string configName = null)
            {
                if (subTierCount < 1)
                    throw new ArgumentOutOfRangeException(nameof(subTierCount), $"Upgrade '{id}' needs at least one sub-tier.");
                if (tier < 1)
                    throw new ArgumentOutOfRangeException(nameof(tier), $"Upgrade '{id}' needs a tree tier of 1 or more.");

                Id = id;
                DisplayName = displayName;
                ConfigName = configName ?? displayName;
                TreeClass = treeClass;
                Tier = tier;
                Column = column;
                Row = row;
                SubTierCount = subTierCount;
                IconKey = iconKey;
                Description = description ?? string.Empty;
                PrerequisiteUpgradeIds = prerequisiteUpgradeIds ?? Array.Empty<string>();
                ConnectionUpgradeIds = connectionUpgradeIds ?? Array.Empty<string>();
                RequiredProviders = requiredProviders;
                AnyProviders = anyProviders;
                DependsOnUpgradeIds = dependsOnUpgradeIds ?? Array.Empty<string>();

                // The whole pricing rule: unlock costs the tier, each later sub-tier costs one more.
                var tierPrices = new int[subTierCount - 1];
                for (int i = 0; i < tierPrices.Length; i++)
                    tierPrices[i] = tier + 1 + i;
                TierPrices = tierPrices;
            }

            internal string Id { get; }
            internal string DisplayName { get; }
            internal string ConfigName { get; }
            internal Y4NGZSkillTreeClass TreeClass { get; }
            internal int Tier { get; }
            internal int Column { get; }
            internal int Row { get; }
            internal int SubTierCount { get; }
            internal string IconKey { get; }
            internal string Description { get; }
            internal string[] PrerequisiteUpgradeIds { get; }
            internal string[] ConnectionUpgradeIds { get; }
            internal OptionalUpgradeProvider RequiredProviders { get; }
            internal OptionalUpgradeProvider AnyProviders { get; }

            /// <summary>
            /// Rows this upgrade cannot function without. Unlike a prerequisite, which only gates
            /// the purchase, a functional dependency drags this row along when the player disables
            /// the dependency (#417). See <see cref="ExpandDisabled"/>.
            /// </summary>
            internal string[] DependsOnUpgradeIds { get; }
            internal bool HasOptionalProviderRequirement =>
                RequiredProviders != OptionalUpgradeProvider.None
                || AnyProviders != OptionalUpgradeProvider.None;

            /// <summary>A Late Game Upgrades row the bridge registers, as opposed to a native one.</summary>
            internal bool IsImported =>
                (RequiredProviders & OptionalUpgradeProvider.LateGameUpgrades) != OptionalUpgradeProvider.None;

            internal bool IsAvailable(OptionalUpgradeProvider installedProviders)
            {
                bool hasEveryRequired =
                    (RequiredProviders & installedProviders) == RequiredProviders;
                bool hasAnAlternative = AnyProviders == OptionalUpgradeProvider.None
                    || (AnyProviders & installedProviders) != OptionalUpgradeProvider.None;
                return hasEveryRequired && hasAnAlternative;
            }

            /// <summary>Derived: unlocking an upgrade costs its tree tier.</summary>
            internal int UnlockPrice => Tier;

            /// <summary>Derived: price for each sub-tier after the unlock.</summary>
            internal int[] TierPrices { get; }

            internal int MaxTier => UpgradePriceMath.GetMaxTier(TierPrices);

            internal int GateRequirement => GateForTier(TreeClass, Tier);

            /// <summary>Tokens to take this upgrade from unowned to maxed.</summary>
            internal int TotalPrice
            {
                get
                {
                    int total = UnlockPrice;
                    for (int i = 0; i < TierPrices.Length; i++)
                        total += TierPrices[i];
                    return total;
                }
            }
        }

        private static readonly Entry[] Ordered =
        {
            // - Enforcer -
            new Entry("back_muscles", "Transporter", Y4NGZSkillTreeClass.Enforcer, 1, 0, 0, 3, "transporter",
                "Description: Cuts the movement and stamina penalty of the scrap you are carrying. It does not change what the scrap is worth.\nLevel 1: Carried weight counts for 20% less.\nLevel 2: Carried weight counts for 40% less.\nLevel 3: Carried weight counts for 60% less."),
            // Two tiers since #364: the old flat +3 was four times a shovel's damage. The
            // second level now buys a stagger chance instead of more force.
            // Y4NGZUpgradeManager refunds saves that already bought the removed third level.
            new Entry("protein_powder", "Pumping Iron", Y4NGZSkillTreeClass.Enforcer, 2, 1, 0, 2, "pumping_iron",
                "Description: Adds force to melee weapon hits.\nLevel 1: Melee hits gain +1 force. A vanilla shovel hits for 1, so this doubles it.\nLevel 2: Melee hits also have a 25% chance to stun the struck enemy for 1 second.",
                connectionUpgradeIds: new[] { "back_muscles" }),
            new Entry("thick_skin", "Resilience", Y4NGZSkillTreeClass.Enforcer, 2, 1, 1, 3, "resilience",
                "Description: Raises the health cap and puts a health readout on your HUD. Below 20 HP the suit already recovers 1 HP per second on its own; from level 2 that recovery continues past 20 HP up to 30.\nLevel 1: Max health 120.\nLevel 2: Max health 140. Injuries now recover past 20 HP, up to 30.\nLevel 3: Max health 160. Recovery cap stays 30 HP.",
                connectionUpgradeIds: new[] { "back_muscles" }),
            new Entry("lethal_hands_training", "Lethal Hands", Y4NGZSkillTreeClass.Enforcer, 3, 2, 0, 3, "lethal_hands",
                "Description: Unlocks empty-handed punching. One punch lands every 0.75 seconds; a vanilla shovel hit is 1 damage for scale. Punches also deal 10 damage to other employees at every level.\nLevel 1: Punches deal 0.5 shovel hits of damage.\nLevel 2: Punches deal 1.0 shovel hit of damage.\nLevel 3: Punches deal 1.5 shovel hits of damage and stun the target for 1 second.",
                prerequisiteUpgradeIds: new[] { "protein_powder" },
                connectionUpgradeIds: new[] { "protein_powder" }),
            new Entry("extra_inventory_slot", "Deeper Pockets", Y4NGZSkillTreeClass.Enforcer, 3, 2, 1, 3, "deeper_pockets",
                "Description: Adds hotbar slots to the standard four.\nLevel 1: Five hotbar slots.\nLevel 2: Six hotbar slots.\nLevel 3: Seven hotbar slots, and you can carry two two-handed items at once.",
                connectionUpgradeIds: new[] { "thick_skin" },
                requiredProviders: OptionalUpgradeProvider.NativeInventory),
            new Entry("adrenaline_rush", "Nine Lives", Y4NGZSkillTreeClass.Enforcer, 4, 3, 0, 2, "adrenaline_rush",
                "Description: A rechargeable shield and a last chance to survive.\nLevel 1: After 15 seconds without health or shield damage, restore a separate shield equal to your maximum health. Excess damage spills into health.\nLevel 2: Once per round, a fatal hit leaves you at 1 HP with 4 seconds of invulnerability. Numeric fall damage is covered; bottomless pits, drowning and abandonment are not.",
                connectionUpgradeIds: new[] { "lethal_hands_training", "extra_inventory_slot" }),

            // - Ghost -
            new Entry("light_feet", "Light Feet", Y4NGZSkillTreeClass.Ghost, 1, 0, 0, 3, "light_feet",
                "Description: Automated hazards stop reacting to you.\nLevel 1: You no longer set off landmines - not by stepping on them, and not by dropping scrap or a body onto one.\nLevel 2: Turrets hold fire for up to 2.5 extra seconds after spotting you.\nLevel 3: Lightning strikes no longer damage or kill you."),
            new Entry("sixth_sense", "Sixth Sense", Y4NGZSkillTreeClass.Ghost, 1, 0, 1, 3, "sixth_sense",
                "Description: Outlines what you cannot see. Anything in direct line of sight is skipped; only occluded targets are drawn.\nLevel 1: Main entrance, fire exits, mainframes, stashes and objectives outline within 15m. Turrets and landmines only within 5m.\nLevel 2: Also outlines grabbable scrap and items within 10m.\nLevel 3: Also outlines living enemies within 15m."),
            new Entry("chameleon", "Chameleon", Y4NGZSkillTreeClass.Ghost, 1, 0, 2, 3, "chameleon",
                "Description: Camouflage plating that only fools Company security cameras. It does nothing to enemies.\nLevel 1: Cameras take 15% longer to detect you.\nLevel 2: Cameras take 30% longer to detect you.\nLevel 3: Cameras take 50% longer to detect you.",
                connectionUpgradeIds: new[] { "panic_slide" },
                requiredProviders: OptionalUpgradeProvider.LethalCctv),
            new Entry("panic_slide", "Escape Artist", Y4NGZSkillTreeClass.Ghost, 2, 1, 0, 3, "panic_slide",
                "Description: A chained movement kit. A heavy load weakens the slide.\nLevel 1: Sprint-crouch to power slide. Costs 14% stamina, 2.25 second cooldown.\nLevel 2: Mantle ledges from 0.95m to 3m high. Costs 8% stamina.\nLevel 3: One extra jump while airborne for 8% stamina, restored when you land.",
                connectionUpgradeIds: new[] { "sixth_sense" }),
            new Entry("lone_wolf", "Lone Wolf", Y4NGZSkillTreeClass.Ghost, 3, 2, 0, 3, "lone_wolf",
                "Description: Outside the ship, activates after 8 seconds with no living teammate within 35m in the same interior or exterior. Ends after a teammate stays within 25m for 2 seconds.\nLevel 1: Non-sprinting stamina recovery +25%.\nLevel 2: Also take 15% less damage while active.\nLevel 3: Recovery rises to +50%. Your last 60 seconds of travelled route become a personal return trail while active.",
                connectionUpgradeIds: new[] { "panic_slide" }),
            // #435 fresh design: the old five-rank Sprinter split into two three-rank rows. The id
            // "physical_conditioning" is load-bearing (save keys, tier sync index 7).
            new Entry("physical_conditioning", "Sprinter", Y4NGZSkillTreeClass.Ghost, 3, 2, 1, 3, "sprinter",
                "Description: Running training: speed, stamina and quiet movement.\nLevel 1: Movement speed +1.4.\nLevel 2: Sprint time +6 seconds and stamina regen +5%.\nLevel 3: Crouching speed +10% and footstep noise range 10m shorter.",
                connectionUpgradeIds: new[] { "panic_slide" }),
            // Deliberately reuses the imported Hiking Boots art: no Surefooted icon was drawn.
            new Entry(NativeUpgradeFamilies.SurefootedId, "Surefooted", Y4NGZSkillTreeClass.Ghost, 3, 2, 2, 3, "lgu_hiking_boots",
                "Description: Footing training: ladders, rough ground, jumps and landings.\nLevel 1: Ladder speed +20%, traction +25%, and you sink 20% slower in water and quicksand.\nLevel 2: Ladder speed +40%, jump force +3, uphill slowdown -25%.\nLevel 3: Ladder speed +60%, jump force +3.75, fall damage -25%.",
                connectionUpgradeIds: new[] { "panic_slide" }),
            new Entry("shadow_step", "Shadow Step", Y4NGZSkillTreeClass.Ghost, 4, 3, 0, 3, "shadow_step",
                "Description: Reduces visual detection range and grants an active cloak. Attacking or sprinting breaks cloak; sound and physical contact remain dangerous. 90 seconds between activations.\nLevel 1: Enemy sight range -25% while crouching; cloak lasts 3 seconds.\nLevel 2: Walking also reduces sight range by 25%; cloak lasts 4.5 seconds.\nLevel 3: Cloak lasts 6 seconds, with an expiry cue in its final second.",
                connectionUpgradeIds: new[] { "lone_wolf" }),

            // - Technician -
            // Display rename only: id "better_scanner" is load-bearing (save keys, node id, icon key).
            new Entry("better_scanner", "Field Optics", Y4NGZSkillTreeClass.Technician, 1, 0, 0, 3, "better_scanner",
                "Description: Upgrades issued optics - scanner glass and lamp alike - so you spot scrap sooner and light more of the room.\nLevel 1: Increases scanner node detection range by 15%, flashlight brightness by 10%, and flashlight cone width by 10%.\nLevel 2: Increases scanner node detection range by 30%, flashlight brightness by 15%, and flashlight cone width by 20%.\nLevel 3: Increases scanner node detection range by 45%, flashlight brightness by 40%, and flashlight cone width by 30%."),
            new Entry("quick_hands", "Quick Hands", Y4NGZSkillTreeClass.Technician, 1, 0, 1, 3, "quick_hands",
                "Description: Faster hold interactions and longer reach. The reach bonus applies to grabbing and to every interact trigger.\nLevel 1: Vanilla hold interactions 10% faster, reach +1m.\nLevel 2: Vanilla hold interactions 20% faster, reach +2m.\nLevel 3: Vanilla hold interactions 30% faster, reach +3m."),
            new Entry("turret_hacker", "Field Mechanic", Y4NGZSkillTreeClass.Technician, 2, 1, 0, 3, "field_mechanic",
                "Description: On-site sabotage of facility hardware. Every hold interaction takes 1.2 seconds.\nLevel 1: Fuel pumps are half as likely to stall, and Company CCTV cameras can be disabled for the rest of the round.\nLevel 2: Standard locked doors can be hacked open, no key required.\nLevel 3: Turrets can be disabled for 90 seconds from the Field Operations tablet or a nearby hold, with a 20 second cooldown.",
                connectionUpgradeIds: new[] { "better_scanner" }),
            new Entry("scavenger", "Scavenger", Y4NGZSkillTreeClass.Technician, 2, 1, 1, 3, "scavenger",
                "Description: Squeezes extra value out of everything scavenged in the field.\nLevel 1: Ammo pickups grant 10% more rounds from larger pickups and fuel chute deposits are worth 10% more.\nLevel 2: Ammo pickups grant 20% more rounds from larger pickups and fuel deposits are worth 20% more.\nLevel 3: Ammo pickups grant 30% more rounds from larger pickups and fuel deposits are worth 30% more.",
                connectionUpgradeIds: new[] { "quick_hands" },
                anyProviders: OptionalUpgradeProvider.ShipSystems | OptionalUpgradeProvider.BetterArmory),
            new Entry("field_operations", "Field Operations", Y4NGZSkillTreeClass.Technician, 3, 2, 0, 1, "field_operations",
                "Description: Grants the Field Operations tablet.\nLevel 1: Unlocks the tablet: scan, interior map, drone link, and a mainframe uplink that takes 10 seconds to establish and then offers a scrap sweep and a trap shutdown. The tablet also carries stash codes, the camera list and camera shutdown, alarm control and lockdown.",
                prerequisiteUpgradeIds: new[] { "turret_hacker" },
                connectionUpgradeIds: new[] { "turret_hacker" }),
            new Entry("deathbound", "Deathbound", Y4NGZSkillTreeClass.Technician, 4, 3, 1, 1, "deathbound",
                "Description: Binds the far-left hotbar slot through death.\nLevel 1: The item in the far-left hotbar slot is not dropped on death and returns with you on respawn. The protected slot is tinted red.",
                connectionUpgradeIds: new[] { "field_operations" }),
            new Entry("courier_drone", "Courier Drone", Y4NGZSkillTreeClass.Technician, 4, 3, 0, 3, "courier_drone",
                "Description: A steampunk courier drone commanded from the Field Operations tablet. The drone has 30 health; if destroyed it is gone until the next round. Below 10 health it breaks off and returns to you for six seconds. Aim at a monster or scrap and press the Courier Drone Command key to order an attack or a fetch, with or without the tablet.\nLevel 1: Hauls up to 150 lb of scrap and defends itself with a flamethrower out to 3.5m.\nLevel 2: Adds a grenade launcher that engages monsters between 8m and 18m on an 8 second cooldown. It checks for nearby crew before firing on its own, but the grenade still detonates if someone walks in, and pilot-fired grenades skip the check.\nLevel 3: Unlocks pilot mode to fly the drone yourself from the tablet.",
                connectionUpgradeIds: new[] { "field_operations" },
                dependsOnUpgradeIds: new[] { "field_operations" }),

            // - Foreman -
            new Entry("buddy_system", "Buddy System", Y4NGZSkillTreeClass.Foreman, 1, 0, 0, 2, "buddy_system",
                "Description: A support aura for the crew. The buffs go to other employees only, never to you.\nLevel 1: Teammates within 10m gain 3% movement speed and 3% damage reduction. You see teammates in that radius outlined when walls or fog hide them.\nLevel 2: Radius rises to 15m and both buffs rise to 6%. Damage reductions do not stack; the largest applies."),
            new Entry("ping", "Ping", Y4NGZSkillTreeClass.Foreman, 2, 1, 0, 3, "ping",
                "Description: A team marking tool on its own key. Reaches 80m, marks last 5 seconds, 3 second cooldown. Requires line of sight.\nLevel 1: Mark a location, or outline an enemy, teammate, item, entrance, interactable or trap.\nLevel 2: Marked enemies deal 20% less contact damage.\nLevel 3: Marked teammates take 20% less damage for the mark duration. Damage reductions do not stack; the largest applies.",
                connectionUpgradeIds: new[] { "buddy_system" }),
            new Entry("worklight_beacon", "Worklight Beacon", Y4NGZSkillTreeClass.Foreman, 2, 1, 1, 3, "worklight_beacon",
                "Description: Throws sticky worklights. Each burns for 180 seconds, charges refill at the start of every round, and picking a beacon back up refunds its charge.\nLevel 1: Three charges, 10m lit radius.\nLevel 2: Five charges, 13m lit radius.\nLevel 3: Nine charges, 17m lit radius.",
                connectionUpgradeIds: new[] { "buddy_system" }),
            new Entry("command_net", "Command Net", Y4NGZSkillTreeClass.Foreman, 2, 1, 2, 3, "command_net",
                "Description: Passive comms and a tactical display that cost no inventory slot.\nLevel 1: A built-in walkie channel. Hold the Command Net key to transmit with no walkie-talkie in hand.\nLevel 2: Teammates are outlined for 5 seconds whenever they take damage.\nLevel 3: A corner minimap once you step out of the ship room, tracking crew, entrances and enemies, plus security cameras and objectives.",
                connectionUpgradeIds: new[] { "buddy_system" }),
            // Display rename only: id "veteran" is load-bearing (force_field save migration).
            new Entry("veteran", "Overachiever", Y4NGZSkillTreeClass.Foreman, 3, 2, 0, 3, "veteran",
                "Description: Extra upgrade tokens on every promotion. Buying a level also pays the bonus retroactively for promotions you already earned.\nLevel 1: Each promotion grants 1 extra token.\nLevel 2: Each promotion grants 2 extra tokens.\nLevel 3: Each promotion grants 4 extra tokens.",
                connectionUpgradeIds: new[] { "ping", "worklight_beacon", "command_net" }),
            new Entry("inspire", "Inspire", Y4NGZSkillTreeClass.Foreman, 4, 3, 0, 1, "inspire",
                "Description: Turns Ping into a revive attempt.\nLevel 1: Ping a dead body within 7m for a 50% chance to revive it. Once per round, consumed only on success, then a 75 second cooldown.",
                connectionUpgradeIds: new[] { "veteran" },
                dependsOnUpgradeIds: new[] { "ping" }),
            // Two tiers only: QuotaGuardUpgrade reads levels 1-2, so the former third level was a
            // no-op. Y4NGZUpgradeManager refunds saves that already bought it.
            new Entry("quota_guard", "Veteran", Y4NGZSkillTreeClass.Foreman, 4, 3, 1, 2, "quota_guard",
                "Description: The Company goes easy on a crew that keeps showing up. The whole crew uses the highest level anyone has bought.\nLevel 1: The end-of-round credit penalty for dead crew is halved.\nLevel 2: The next quota sets a 4 day deadline instead of 3.",
                connectionUpgradeIds: new[] { "veteran" }),

            // - Late Game Upgrades (#435) -
            //
            // Every row below needs com.malco.lethalcompany.moreshipupgrades and is registered by
            // LguUpgradeBridge, which buys the matching Late Game Upgrades rank with Y4NGZ tokens
            // and applies it to the buying player alone. They carry an optional-provider
            // requirement, so they supply nothing to any class-tree gate ladder. LGU-only
            // always registers them in the ungated flat Augments catalog instead.
            //
            // Display names drop the "LGU " prefix; ConfigName keeps the original title so
            // config sections and keys are unchanged.
            //
            // Sub-tier counts here are Late Game Upgrades' stock rank counts. The live count comes
            // from the Late Game Upgrades configuration at registration time, so a shortened or
            // lengthened price list is honoured and a switched-off upgrade never reaches the tree.
            // The per-level copy is generated then too, from the values the upgrade's own effect
            // code reads (LguUpgradeBridge.TryDescribe, #442). The descriptions below are only the
            // fallback when a value cannot be read, so they state no magnitudes of their own.
            //
            // Charging Booster is not imported (#442): it fits a station to every radar booster
            // and syncs its cooldown to the crew, so it stays in Late Game Upgrades' credit store.

            // - Enforcer -
            new Entry("lgu_back_muscles", "Back Muscles", Y4NGZSkillTreeClass.Enforcer, 1, 0, 1, 4, "lgu_back_muscles",
                "Description: Reduces the movement and stamina penalty from the scrap you are carrying.\nLevel 1: Carried weight penalty reduced.",
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU Back Muscles"),
            new Entry("lgu_stimpack", "Stimpack", Y4NGZSkillTreeClass.Enforcer, 1, 0, 2, 4, "lgu_stimpack",
                "Description: Raises your own maximum health.\nLevel 1: Maximum health raised.",
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU Stimpack"),
            new Entry("lgu_beekeeper", "Beekeeper", Y4NGZSkillTreeClass.Enforcer, 1, 0, 3, 4, "lgu_beekeeper",
                "Description: Circuit bees and Butler Hornets do significantly less damage to you.\nLevel 4: The commission on a nest you sell rises.",
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU Beekeeper"),
            new Entry("lgu_protein_powder", "Protein Powder", Y4NGZSkillTreeClass.Enforcer, 2, 1, 2, 2, "lgu_protein_powder",
                "Description: Increases damage dealt with shovels and signs.\nLevel 1: Melee damage increased.",
                connectionUpgradeIds: new[] { "lgu_back_muscles" },
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU Protein Powder"),
            new Entry("lgu_deeper_pockets", "Deeper Pockets", Y4NGZSkillTreeClass.Enforcer, 2, 1, 3, 2, "lgu_deeper_pockets",
                "Description: Increases the amount of two-handed items you can carry in your inventory.\nLevel 1: One extra two-handed item.",
                connectionUpgradeIds: new[] { "lgu_back_muscles" },
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU Deeper Pockets"),
            new Entry("lgu_explosion_resistance", "Explosion Resistance", Y4NGZSkillTreeClass.Enforcer, 2, 1, 4, 3, "lgu_explosion_resistance",
                "Description: Increases your damage resistance to explosions.\nLevel 1: Explosion damage reduced.",
                connectionUpgradeIds: new[] { "lgu_stimpack" },
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU Explosion Resistance"),
            // #435 fresh design: moved from Foreman tier 2 with the same ranks and prices.
            new Entry("lgu_effective_bandaids", "Effective Bandaids", Y4NGZSkillTreeClass.Enforcer, 2, 1, 5, 4, "lgu_effective_bandaids",
                "Description: Natural healing restores more health per tick. Your maximum health and the healing limit do not change.\nLevel 1: Health recovered per tick increased.",
                connectionUpgradeIds: new[] { "lgu_stimpack" },
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU Effective Bandaids"),
            new Entry("lgu_bullet_resistance", "Bullet Resistance", Y4NGZSkillTreeClass.Enforcer, 3, 2, 2, 3, "lgu_bullet_resistance",
                "Description: Reduces damage from turret bullets and shotguns.\nLevel 1: Bullet damage reduced.",
                connectionUpgradeIds: new[] { "lgu_explosion_resistance" },
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU Bullet Resistance"),
            new Entry("lgu_hollow_point", "Hollow Point", Y4NGZSkillTreeClass.Enforcer, 3, 2, 3, 2, "lgu_hollow_point",
                "Description: Your firearms deal more damage.\nLevel 1: Firearm damage increased.",
                connectionUpgradeIds: new[] { "lgu_protein_powder" },
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU Hollow Point"),
            new Entry("lgu_long_barrel", "Long Barrel", Y4NGZSkillTreeClass.Enforcer, 3, 2, 4, 3, "lgu_long_barrel",
                "Description: Longer firearm range and damage falloff.\nLevel 1: Firearm range increased.",
                connectionUpgradeIds: new[] { "lgu_hollow_point" },
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU Long Barrel"),
            new Entry("lgu_sleight_of_hand", "Sleight of Hand", Y4NGZSkillTreeClass.Enforcer, 4, 3, 1, 4, "lgu_sleight_of_hand",
                "Description: Faster firearm reloads.\nLevel 1: Firearm reloads take less time.",
                connectionUpgradeIds: new[] { "lgu_long_barrel" },
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU Sleight of Hand"),
            new Entry("lgu_silver_bullets", "Silver Bullets", Y4NGZSkillTreeClass.Enforcer, 4, 3, 2, 1, "lgu_silver_bullets",
                "Description: Your firearms can kill the Ghost Girl.\nLevel 1: Firearm hits can kill the Ghost Girl.",
                connectionUpgradeIds: new[] { "lgu_hollow_point" },
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU Silver Bullets"),

            // - Ghost -
            new Entry("lgu_running_shoes", "Running Shoes", Y4NGZSkillTreeClass.Ghost, 1, 0, 3, 4, "lgu_running_shoes",
                "Description: Increases your movement speed.\nLevel 4: Footstep noise reduced.",
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU Running Shoes"),
            new Entry("lgu_strong_legs", "Strong Legs", Y4NGZSkillTreeClass.Ghost, 1, 0, 4, 4, "lgu_strong_legs",
                "Description: Increases your jump force.\nLevel 1: Jump height increased.",
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU Strong Legs"),
            new Entry("lgu_bigger_lungs", "Bigger Lungs", Y4NGZSkillTreeClass.Ghost, 2, 1, 2, 4, "lgu_bigger_lungs",
                "Description: Increases your sprint duration.\nLevel 3: Stamina recovers faster and jumping costs less stamina.",
                connectionUpgradeIds: new[] { "lgu_running_shoes" },
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU Bigger Lungs"),
            new Entry("lgu_carbon_kneejoints", "Carbon Kneejoints", Y4NGZSkillTreeClass.Ghost, 2, 1, 3, 4, "lgu_carbon_kneejoints",
                "Description: Decreases the movement speed loss when crouching.\nLevel 1: Crouch penalty reduced.",
                connectionUpgradeIds: new[] { "lgu_running_shoes" },
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU Carbon Kneejoints"),
            new Entry("lgu_hiking_boots", "Hiking Boots", Y4NGZSkillTreeClass.Ghost, 2, 1, 4, 4, "lgu_hiking_boots",
                "Description: Reduces the movement speed penalty on slopes.\nLevel 1: Uphill penalty reduced.",
                connectionUpgradeIds: new[] { "lgu_strong_legs" },
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU Hiking Boots"),
            new Entry("lgu_reinforced_boots", "Reinforced Boots", Y4NGZSkillTreeClass.Ghost, 3, 2, 3, 5, "lgu_reinforced_boots",
                "Description: Reduces incoming fall damage.\nLevel 1: Fall damage reduced.",
                connectionUpgradeIds: new[] { "lgu_hiking_boots" },
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU Reinforced Boots"),
            new Entry("lgu_rubber_boots", "Rubber Boots", Y4NGZSkillTreeClass.Ghost, 2, 1, 5, 4, "lgu_rubber_boots",
                "Description: Reduces movement hindrance when walking on water and quicksand.\nLevel 1: Water and quicksand hinder you less.",
                connectionUpgradeIds: new[] { "lgu_strong_legs" },
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU Rubber Boots"),
            new Entry("lgu_traction_boots", "Traction Boots", Y4NGZSkillTreeClass.Ghost, 3, 2, 4, 4, "lgu_traction_boots",
                "Description: Increases your traction for easier direction changes and stopping.\nLevel 1: Traction increased.",
                connectionUpgradeIds: new[] { "lgu_carbon_kneejoints" },
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU Traction Boots"),
            new Entry("lgu_climbing_gloves", "Climbing Gloves", Y4NGZSkillTreeClass.Ghost, 3, 2, 5, 4, "lgu_climbing_gloves",
                "Description: Increases your ladder climbing speed.\nLevel 1: Ladder speed increased.",
                connectionUpgradeIds: new[] { "lgu_bigger_lungs" },
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU Climbing Gloves"),
            new Entry("lgu_oxygen_canisters", "Oxygen Canisters", Y4NGZSkillTreeClass.Ghost, 2, 1, 6, 4, "lgu_oxygen_canisters",
                "Description: Decreases your oxygen consumption rate.\nLevel 1: Oxygen lasts longer.",
                connectionUpgradeIds: new[] { "lgu_running_shoes" },
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU Oxygen Canisters"),
            new Entry("lgu_clay_glasses", "Clay Glasses", Y4NGZSkillTreeClass.Ghost, 2, 1, 7, 5, "lgu_clay_glasses",
                "Description: Increases the maximum distance from which you can spot a Clay Surgeon entity.\nLevel 1: Spotting distance increased.",
                connectionUpgradeIds: new[] { "lgu_strong_legs" },
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU Clay Glasses"),
            new Entry("lgu_fedora_suit", "Fedora Suit", Y4NGZSkillTreeClass.Ghost, 2, 1, 8, 1, "lgu_fedora_suit",
                "Description: Butlers no longer attack you for being alone. They still retaliate if attacked.\nLevel 1: Butlers treat you as staff and leave you alone.",
                connectionUpgradeIds: new[] { "lgu_running_shoes" },
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU Fedora Suit"),

            // - Technician -
            new Entry("lgu_better_scanner", "Better Scanner", Y4NGZSkillTreeClass.Technician, 1, 0, 2, 3, "lgu_better_scanner",
                "Description: Extends your scanner range and adds scan commands. At max level you can scan scrap through walls.\nLevel 1: Ship and entrance pings reach further. All other pings also reach further.\nLevel 2: Unlocks five new scan commands: scan player, scan enemies, scan scrap, scan hives, scan doors.\nLevel 3: Scan scrap through walls.",
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU Better Scanner"),
            new Entry("lgu_quick_hands", "Quick Hands", Y4NGZSkillTreeClass.Technician, 1, 0, 3, 5, "lgu_quick_hands",
                "Description: Increases your interaction speed on objects.\nLevel 1: Interaction speed increased.",
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU Quick Hands"),
            new Entry("lgu_mechanical_arms", "Mechanical Arms", Y4NGZSkillTreeClass.Technician, 2, 1, 2, 4, "lgu_mechanical_arms",
                "Description: Increases your reach when grabbing items and using interactables.\nLevel 1: Reach increased.",
                connectionUpgradeIds: new[] { "lgu_quick_hands" },
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU Mechanical Arms"),
            new Entry("lgu_lithium_batteries", "Lithium Batteries", Y4NGZSkillTreeClass.Technician, 2, 1, 3, 5, "lgu_lithium_batteries",
                "Description: Battery-powered items you carry drain more slowly.\nLevel 1: Battery drain reduced.",
                connectionUpgradeIds: new[] { "lgu_better_scanner" },
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU Lithium Batteries"),
            new Entry("lgu_nv_headset_batteries", "NV Headset Batteries", Y4NGZSkillTreeClass.Technician, 3, 2, 2, 3, "lgu_nv_headset_batteries",
                "Description: Increases regen speed and decreases depletion speed of the night vision headset.\nLevel 1: Headset battery life improved.",
                connectionUpgradeIds: new[] { "lgu_lithium_batteries" },
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU NV Headset Batteries"),
            new Entry("lgu_locksmith", "Locksmith", Y4NGZSkillTreeClass.Technician, 3, 2, 3, 1, "lgu_locksmith",
                "Description: Allows you to pick locked doors by completing a minigame.\nLevel 1: You can pick a locked door by hand, with no lockpicker.",
                connectionUpgradeIds: new[] { "lgu_mechanical_arms" },
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU Locksmith"),
            new Entry("lgu_aluminium_coils", "Aluminium Coils", Y4NGZSkillTreeClass.Technician, 3, 2, 4, 4, "lgu_aluminium_coils",
                "Description: Provides buffs to the zap gun: increased stun range, longer stun timer, and reduced cooldown.\nLevel 1: Zap gun handling improved.",
                connectionUpgradeIds: new[] { "lgu_lithium_batteries" },
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU Aluminium Coils"),
            // #435 fresh design: Jet Fuel and Jetpack Thrusters open at tier 3 (3/4/5/6).
            new Entry("lgu_jet_fuel", "Jet Fuel", Y4NGZSkillTreeClass.Technician, 3, 2, 5, 4, "lgu_jet_fuel",
                "Description: Increases the acceleration rate of the jetpack while in flight.\nLevel 1: Jetpack acceleration increased.",
                connectionUpgradeIds: new[] { "lgu_lithium_batteries" },
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU Jet Fuel"),
            new Entry("lgu_jetpack_thrusters", "Jetpack Thrusters", Y4NGZSkillTreeClass.Technician, 3, 2, 6, 4, "lgu_jetpack_thrusters",
                "Description: Increases the maximum speed of the jetpack while in flight.\nLevel 1: Jetpack top speed increased.",
                connectionUpgradeIds: new[] { "lgu_jet_fuel" },
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU Jetpack Thrusters"),

            // - Foreman -
            new Entry("lgu_medical_nanobots", "Medical Nanobots", Y4NGZSkillTreeClass.Foreman, 2, 1, 3, 4, "lgu_medical_nanobots",
                "Description: Natural healing continues up to a higher health limit. It does not change how much each tick heals.\nLevel 1: Healing limit raised.",
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU Medical Nanobots"),
            new Entry("lgu_walkie_gps", "Walkie GPS", Y4NGZSkillTreeClass.Foreman, 3, 2, 1, 1, "lgu_walkie_gps",
                "Description: Displays your position and time when holding a walkie talkie.\nLevel 1: Walkie talkie shows position and time.",
                connectionUpgradeIds: new[] { "lgu_medical_nanobots" },
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU Walkie GPS"),
            new Entry("lgu_sick_beats", "Sick Beats", Y4NGZSkillTreeClass.Foreman, 3, 2, 2, 1, "lgu_sick_beats",
                "Description: Boomboxes playing music near you grant movement speed, damage boost and damage reduction.\nLevel 1: Music buffs active near you.",
                connectionUpgradeIds: new[] { "lgu_medical_nanobots" },
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU Sick Beats"),
            new Entry("lgu_tzp_buffer", "TZP Buffer", Y4NGZSkillTreeClass.Foreman, 3, 2, 3, 3, "lgu_tzp_buffer",
                "Description: Increases the effectiveness of the TZP-Inhalant positive effects while decreasing negative effects.\nLevel 1: TZP inhaler works better for you.",
                connectionUpgradeIds: new[] { "lgu_medical_nanobots" },
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU TZP Buffer"),
            new Entry("lgu_weed_genetic_manipulation", "Weed Genetic Manipulation", Y4NGZSkillTreeClass.Foreman, 3, 2, 4, 4, "lgu_weed_genetic_manipulation",
                "Description: Increases the effectiveness of the Weed Killer item in eradicating plants.\nLevel 1: Weed killer works faster.",
                connectionUpgradeIds: new[] { "lgu_walkie_gps" },
                requiredProviders: OptionalUpgradeProvider.LateGameUpgrades,
                configName: "LGU Weed Genetic Manipulation")
        };

        /// <summary>
        /// The gate ladder each tree actually registers: the shipped ladder, clamped per tier to
        /// the number of levels that tree sells below that tier. A gate can never ask for more
        /// levels than exist beneath it, so no tree can soft-lock.
        ///
        /// This is what gives Foreman its 0/2/7/11 ladder today: its tier 1 holds only Buddy
        /// System, whose two sub-tiers cap tree investment at 2. Lawson's ruling (2026-08-13) was
        /// to keep the catalog exactly as verified - 242/236, no new Buddy System sub-tier, no
        /// layout change - and let Foreman alone carry the lower tier-2 gate. Deriving it rather
        /// than hand-encoding it means the exception disappears by itself if Foreman tier 1 ever
        /// gains levels, and Buddy System's sub-tier count is not restated anywhere.
        ///
        /// Optional-provider rows are excluded from the supply, so the ladder is identical in
        /// every supported mod combination.
        /// </summary>
        private static readonly Dictionary<Y4NGZSkillTreeClass, int[]> DerivedTierGates = BuildTierGates();

        private static Dictionary<Y4NGZSkillTreeClass, int[]> BuildTierGates()
        {
            var ladders = new Dictionary<Y4NGZSkillTreeClass, int[]>();
            foreach (Y4NGZSkillTreeClass treeClass in (Y4NGZSkillTreeClass[])Enum.GetValues(typeof(Y4NGZSkillTreeClass)))
                ladders[treeClass] = GatesFor(treeClass, AlwaysAvailable);

            return ladders;
        }

        private static readonly Dictionary<string, Entry> ById = BuildIndex();

        internal static IReadOnlyList<Entry> All => Ordered;

        internal static Entry Get(string id)
        {
            if (TryGet(id, out Entry entry))
                return entry;

            throw new InvalidOperationException($"No catalog row for upgrade '{id}'.");
        }

        internal static bool TryGet(string id, out Entry entry)
        {
            entry = null;
            return !string.IsNullOrWhiteSpace(id)
                && ById.TryGetValue(id.Trim(), out entry);
        }

        private static Dictionary<string, Entry> BuildIndex()
        {
            var index = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Ordered.Length; i++)
                index[Ordered[i].Id] = Ordered[i];
            return index;
        }
    }
}
