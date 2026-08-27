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
        ShipSystems = 1 << 4
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
                OptionalUpgradeProvider anyProviders = OptionalUpgradeProvider.None)
            {
                if (subTierCount < 1)
                    throw new ArgumentOutOfRangeException(nameof(subTierCount), $"Upgrade '{id}' needs at least one sub-tier.");
                if (tier < 1)
                    throw new ArgumentOutOfRangeException(nameof(tier), $"Upgrade '{id}' needs a tree tier of 1 or more.");

                Id = id;
                DisplayName = displayName;
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

                // The whole pricing rule: unlock costs the tier, each later sub-tier costs one more.
                var tierPrices = new int[subTierCount - 1];
                for (int i = 0; i < tierPrices.Length; i++)
                    tierPrices[i] = tier + 1 + i;
                TierPrices = tierPrices;
            }

            internal string Id { get; }
            internal string DisplayName { get; }
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
            internal bool HasOptionalProviderRequirement =>
                RequiredProviders != OptionalUpgradeProvider.None
                || AnyProviders != OptionalUpgradeProvider.None;

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
                "Description: Reduces the operational penalty of hauling heavy quota objects.\nLevel 1: Reduces carried item weight penalties by 20%.\nLevel 2: Reduces carried item weight penalties by 40%.\nLevel 3: Reduces carried item weight penalties by 60%."),
            new Entry("protein_powder", "Pumping Iron", Y4NGZSkillTreeClass.Enforcer, 2, 1, 0, 3, "pumping_iron",
                "Description: Increases local melee weapon hit force; native fist damage is always fixed by fist tier.\nLevel 1: Adds +1 force to valid melee hits.\nLevel 2: Adds +2 force to valid melee hits.\nLevel 3: Adds +3 force to valid melee hits.",
                connectionUpgradeIds: new[] { "back_muscles" }),
            new Entry("thick_skin", "Resilience", Y4NGZSkillTreeClass.Enforcer, 2, 1, 1, 4, "resilience",
                "Description: Improves survivability with a higher health cap, slow recovery while injured, and a final enemy-hit countershock.\nLevel 1: Raises max health by 20 and slowly regenerates critical injuries up to normal injured health.\nLevel 2: Raises max health by 40 and increases the regeneration cap.\nLevel 3: Raises max health by 60 with the same improved regeneration cap.\nLevel 4: Raises max health by 80 and reflects enemy melee hits with a brief stun.",
                connectionUpgradeIds: new[] { "back_muscles" }),
            new Entry("lethal_hands_training", "Lethal Hands", Y4NGZSkillTreeClass.Enforcer, 3, 2, 0, 3, "lethal_hands",
                "Description: Unlocks empty-handed native fist combat and improves it by tier.\nLevel 1: Allows left-click punching while empty-handed for 0.5 shovel units of enemy damage.\nLevel 2: Raises punch damage to 1.0 shovel unit.\nLevel 3: Raises punch damage to 1.5 shovel units and stuns punched enemies briefly.",
                prerequisiteUpgradeIds: new[] { "protein_powder" },
                connectionUpgradeIds: new[] { "protein_powder" }),
            new Entry("extra_inventory_slot", "Deeper Pockets", Y4NGZSkillTreeClass.Enforcer, 3, 2, 1, 3, "deeper_pockets",
                "Description: Expands the employee carry harness with additional item slots.\nLevel 1: Adds 1 extra inventory slot.\nLevel 2: Adds 2 extra inventory slots.\nLevel 3: Adds 3 extra inventory slots and allows carrying two two-handed items.",
                connectionUpgradeIds: new[] { "thick_skin" },
                requiredProviders: OptionalUpgradeProvider.NativeInventory),
            new Entry("adrenaline_rush", "Adrenaline Rush", Y4NGZSkillTreeClass.Enforcer, 4, 3, 0, 2, "adrenaline_rush",
                "Description: Gives a panic-speed surge at low health and a one-time death save at the higher tier.\nLevel 1: Dropping to 20 HP or lower grants a 30% movement speed surge until healed above the threshold.\nLevel 2: Once per round, an enemy or trap lethal hit leaves you at 1 HP and grants 2 seconds of invincibility.",
                connectionUpgradeIds: new[] { "lethal_hands_training", "extra_inventory_slot" }),

            // - Ghost -
            new Entry("light_feet", "Light Feet", Y4NGZSkillTreeClass.Ghost, 1, 0, 0, 3, "light_feet",
                "Description: Combines Light Feet, turret hesitation, and Grounded into one utility upgrade focused on traps and automated hazards.\nLevel 1: Your footsteps no longer trigger landmines.\nLevel 2: Turrets take longer to start firing after spotting you.\nLevel 3: Lightning strikes no longer kill or damage you."),
            new Entry("sixth_sense", "Sixth Sense", Y4NGZSkillTreeClass.Ghost, 1, 0, 1, 3, "sixth_sense",
                "Description: Passively outlines nearby critical objects using the Foreman Ping outline effect family.\nLevel 1: Within 15m, outlines Main Entrance, Fire Exits, Mainframes, Company Stashes, objectives, and nearby hazards. Turrets and mines only outline within 5m.\nLevel 2: Also outlines grabbable scrap/items within 10m.\nLevel 3: Also outlines enemies within 15m."),
            new Entry("chameleon", "Chameleon", Y4NGZSkillTreeClass.Ghost, 1, 0, 2, 3, "chameleon",
                "Description: Adaptive camouflage plating confuses Company security camera tracking.\nLevel 1: Security cameras take 15% longer to detect you.\nLevel 2: Security cameras take 30% longer to detect you.\nLevel 3: Security cameras take 50% longer to detect you.",
                connectionUpgradeIds: new[] { "panic_slide" },
                requiredProviders: OptionalUpgradeProvider.LethalCctv),
            new Entry("physical_conditioning", "Sprinter", Y4NGZSkillTreeClass.Ghost, 2, 1, 0, 5, "sprinter",
                "Description: Combines movement training, reinforced boots, climbing grip, and quiet movement into one athletics upgrade.\nLevel 1: Increases movement speed, reduces sinking slowdown, and improves ladder speed.\nLevel 2: Increases stamina, adds bonus stamina regen, improves traction, and further improves ladder speed.\nLevel 3: Increases jump height, reduces uphill slope penalties, and reaches the maximum ladder speed bonus.\nLevel 4: Reduces footstep noise and reduces crouch speed penalties.\nLevel 5: Further improves jump height and reduces fall damage.",
                connectionUpgradeIds: new[] { "light_feet" }),
            new Entry("panic_slide", "Escape Artist", Y4NGZSkillTreeClass.Ghost, 2, 1, 1, 3, "panic_slide",
                "Description: Builds a tiered movement escape chain around slides, mantles, and an aerial recovery jump.\nLevel 1: Sprint-crouch triggers an animated power slide with a stamina cost.\nLevel 2: Mantle ledges to recover from blocked routes.\nLevel 3: Gain one additional jump while airborne.",
                connectionUpgradeIds: new[] { "sixth_sense" }),
            new Entry("lone_wolf", "Lone Wolf", Y4NGZSkillTreeClass.Ghost, 3, 2, 0, 3, "lone_wolf",
                "Description: Activates when you are the last living employee outside the ship.\nLevel 1: Grants a 30% movement speed bonus while you are the last survivor on the moon.\nLevel 2: Adds bonus stamina recovery while Lone Wolf is active.\nLevel 3: Reveals nearby enemies for a short duration when Lone Wolf activates.",
                connectionUpgradeIds: new[] { "physical_conditioning", "panic_slide" }),
            new Entry("shadow_step", "Shadow Step", Y4NGZSkillTreeClass.Ghost, 4, 3, 0, 3, "shadow_step",
                "Description: Reduces enemy detection and eventually unlocks a short active invisibility cloak.\nLevel 1: Crouching reduces enemy detection range.\nLevel 2: Walking also receives the reduced detection benefit; sprinting remains fully detectable.\nLevel 3: Unlocks the configured-key invisibility cloak for 6 seconds with a 120 second cooldown.",
                connectionUpgradeIds: new[] { "lone_wolf" }),

            // - Technician -
            // Display rename only: id "better_scanner" is load-bearing (save keys, node id, icon key).
            new Entry("better_scanner", "Field Optics", Y4NGZSkillTreeClass.Technician, 1, 0, 0, 3, "better_scanner",
                "Description: Upgrades issued optics - scanner glass and lamp alike - so you spot scrap sooner and light more of the room.\nLevel 1: Increases scanner node detection range by 15%, flashlight brightness by 10%, and flashlight cone width by 10%.\nLevel 2: Increases scanner node detection range by 30%, flashlight brightness by 15%, and flashlight cone width by 20%.\nLevel 3: Increases scanner node detection range by 45%, flashlight brightness by 40%, and flashlight cone width by 30%."),
            new Entry("quick_hands", "Quick Hands", Y4NGZSkillTreeClass.Technician, 1, 0, 1, 3, "quick_hands",
                "Description: Improves hold/interact actions and close pickup handling.\nLevel 1: Hold interactions are 10% faster and grab reach increases slightly.\nLevel 2: Hold interactions are 20% faster and grab reach improves further.\nLevel 3: Hold interactions are 30% faster and grab reach reaches its cap."),
            new Entry("turret_hacker", "Field Mechanic", Y4NGZSkillTreeClass.Technician, 2, 1, 0, 3, "field_mechanic",
                "Description: Improves Technician field repairs and adds on-site hacking of facility security hardware.\nLevel 1: Placed drills and pumps are significantly less likely to break, drill/pump repairs complete 50% faster, and Company CCTV cameras can be disabled with a 1.2 second hold interaction.\nLevel 2: Batteries can now be repaired, and standard locked doors can be hacked open with a 1.2 second hold interaction, no key required.\nLevel 3: Turrets can be temporarily disabled from the Field Operations tablet or with a nearby hold interaction.",
                connectionUpgradeIds: new[] { "better_scanner" }),
            new Entry("scavenger", "Scavenger", Y4NGZSkillTreeClass.Technician, 2, 1, 1, 3, "scavenger",
                "Description: Squeezes extra value out of everything scavenged in the field.\nLevel 1: Ammo pickups grant 10% more rounds and your fuel chute deposits are worth 10% more.\nLevel 2: Ammo pickups grant 20% more rounds and fuel deposits are worth 20% more.\nLevel 3: Ammo pickups grant 30% more rounds and fuel deposits are worth 30% more.",
                connectionUpgradeIds: new[] { "quick_hands" },
                anyProviders: OptionalUpgradeProvider.ShipSystems | OptionalUpgradeProvider.BetterArmory),
            new Entry("field_operations", "Field Operations", Y4NGZSkillTreeClass.Technician, 3, 2, 0, 1, "field_operations",
                "Description: Grants the Field Operations tablet.\nLevel 1: Grants access to the Field Operations tablet.",
                prerequisiteUpgradeIds: new[] { "turret_hacker" },
                connectionUpgradeIds: new[] { "turret_hacker" }),
            new Entry("deathbound", "Deathbound", Y4NGZSkillTreeClass.Technician, 3, 2, 1, 1, "deathbound",
                "Description: Binds the far-left hotbar slot through death.\nLevel 1: The item held in the far-left hotbar slot does not drop on death and returns with the player on respawn. The protected slot is tinted red.",
                connectionUpgradeIds: new[] { "scavenger" }),
            new Entry("courier_drone", "Courier Drone", Y4NGZSkillTreeClass.Technician, 4, 3, 0, 3, "courier_drone",
                "Description: Deploys a reworked steampunk courier drone from the Field Operations tablet drone command screen.\nLevel 1: Command the steampunk drone from the tablet drone screen as a scrap courier that carries up to 150 lb and auto-defends with a close-range flamethrower.\nLevel 2: Adds an automatic grenade launcher that engages monsters at range with friendly-fire safety.\nLevel 3: Unlocks pilot mode to remote-control the drone through the Field Operations tablet.",
                connectionUpgradeIds: new[] { "field_operations", "deathbound" }),

            // - Foreman -
            new Entry("buddy_system", "Buddy System", Y4NGZSkillTreeClass.Foreman, 1, 0, 0, 2, "buddy_system",
                "Description: Nearby teammates receive passive Foreman support and wall/fog teammate outlines.\nLevel 1: Teammates within 10m gain 3% speed and 3% damage reduction, and obscured teammates can be outlined in that support range.\nLevel 2: Range increases to 15m and buffs rise to 6%."),
            new Entry("ping", "Ping", Y4NGZSkillTreeClass.Foreman, 2, 1, 0, 3, "ping",
                "Description: Adds a separate Foreman team marking tool with a universal 3 second cooldown.\nLevel 1: Mark locations or outline enemies, items, interactables, and traps for 5 seconds.\nLevel 2: Marked enemies deal reduced damage.\nLevel 3: Teammates can be marked to reduce incoming damage for the mark duration.",
                connectionUpgradeIds: new[] { "buddy_system" }),
            new Entry("worklight_beacon", "Worklight Beacon", Y4NGZSkillTreeClass.Foreman, 2, 1, 1, 3, "worklight_beacon",
                "Description: Throws sticky worklights that illuminate the area where they land and can be recovered to refill charges.\nLevel 1: Carry three sticky worklight beacons with improved brightness.\nLevel 2: Carry five beacons with stronger brightness.\nLevel 3: Carry nine beacons with maximum brightness.",
                connectionUpgradeIds: new[] { "buddy_system" }),
            new Entry("command_net", "Command Net", Y4NGZSkillTreeClass.Foreman, 2, 1, 2, 3, "command_net",
                "Description: Adds passive tactical comms tools for crew coordination.\nLevel 1: Grants a built-in walkie channel; hold the configured key to transmit without carrying a walkie-talkie.\nLevel 2: Teammates who take damage are automatically outlined using the Foreman ping outline system.\nLevel 3: Adds a compact tactical minimap after leaving the ship, with crew and entrances plus blue cameras, red enemies, and yellow objectives.",
                connectionUpgradeIds: new[] { "buddy_system" }),
            // Display rename only: id "veteran" is load-bearing (force_field save migration).
            new Entry("veteran", "Overachiever", Y4NGZSkillTreeClass.Foreman, 3, 2, 0, 3, "veteran",
                "Description: Y4NGZ rewards ambition. Eager careerists earn extra upgrade tokens with every promotion.\nLevel 1: Each level-up grants 1 extra upgrade token.\nLevel 2: Each level-up grants 2 extra upgrade tokens.\nLevel 3: Each level-up grants 4 extra upgrade tokens.",
                connectionUpgradeIds: new[] { "ping", "worklight_beacon", "command_net" }),
            new Entry("inspire", "Inspire", Y4NGZSkillTreeClass.Foreman, 4, 3, 0, 1, "inspire",
                "Description: Reuses Ping to attempt a once-per-round revive on a nearby dead teammate.\nLevel 1: Pinging a dead body within 7m has a 25% chance to revive the player. The use is consumed only on success.",
                connectionUpgradeIds: new[] { "veteran" }),
            // Two tiers only: QuotaGuardUpgrade reads levels 1-2, so the former third level was a
            // no-op. Y4NGZUpgradeManager refunds saves that already bought it.
            new Entry("quota_guard", "Veteran", Y4NGZSkillTreeClass.Foreman, 4, 3, 1, 2, "quota_guard",
                "Description: A veteran Foreman doesn't get rattled - the Company goes easy on survivors.\nLevel 1: Quota increases caused by crew deaths are capped at 10% of the pre-penalty quota per round-end.\nLevel 2: The quota deadline is extended to 4 days instead of 3.",
                connectionUpgradeIds: new[] { "veteran" })
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
            {
                var gates = new int[DefaultTierGates.Length];
                int availableBelow = 0;
                for (int tier = 1; tier <= gates.Length; tier++)
                {
                    gates[tier - 1] = Math.Min(DefaultTierGates[tier - 1], availableBelow);
                    for (int i = 0; i < Ordered.Length; i++)
                    {
                        Entry entry = Ordered[i];
                        if (entry.TreeClass == treeClass && entry.Tier == tier && !entry.HasOptionalProviderRequirement)
                            availableBelow += entry.SubTierCount;
                    }
                }

                ladders[treeClass] = gates;
            }

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
