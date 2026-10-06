using System;
using System.Collections.Generic;

namespace Y4NGZUpgrades
{
    /// <summary>
    /// Which catalog supplies the effects the native tree and Late Game Upgrades both offer
    /// (#435). Local and restart-only. Without Late Game Upgrades every mode is the full native
    /// tree.
    /// </summary>
    internal enum LguUpgradeMode
    {
        /// <summary>Every native row, plus the imported rows that add something new.</summary>
        NativePreferred,

        /// <summary>Every imported row; native rows keep only what is theirs alone.</summary>
        LguPreferred,

        /// <summary>Imported rows only: every native catalog row is hidden and inert.</summary>
        LguOnly
    }

    /// <summary>What a native family sells while a policy is in force (#435).</summary>
    internal enum NativeFamilyMode
    {
        /// <summary>The authored native row, every rank, every effect.</summary>
        Full,

        /// <summary>Only the effects Late Game Upgrades does not supply, on fewer ranks.</summary>
        UniqueOnly,

        /// <summary>Fully redundant: not registered at all.</summary>
        Hidden
    }

    /// <summary>Which per-save rank dictionary a read or a write targets (#435).</summary>
    internal enum UpgradeRankRecord
    {
        Full,
        UniqueOnly
    }

    /// <summary>
    /// The reduced definition a native family sells in LGU-preferred mode. A unique rank k
    /// (1-based) corresponds to the original full-native rank <c>Milestones[k - 1]</c>, which is
    /// the only relationship between the two ownership records: credit flows from full ranks to
    /// unique ranks and never back (#435).
    /// </summary>
    internal sealed class NativeUpgradeVariant
    {
        internal NativeUpgradeVariant(string displayName, string description, int[] milestones)
        {
            if (milestones == null || milestones.Length == 0)
                throw new ArgumentException("A variant needs at least one milestone.", nameof(milestones));

            for (int i = 1; i < milestones.Length; i++)
            {
                if (milestones[i] <= milestones[i - 1])
                    throw new ArgumentException("Variant milestones must ascend.", nameof(milestones));
            }

            DisplayName = displayName;
            Description = description ?? string.Empty;
            Milestones = milestones;
        }

        internal string DisplayName { get; }
        internal string Description { get; }
        internal int[] Milestones { get; }
        internal int RankCount => Milestones.Length;

        /// <summary>
        /// The unique rank an existing full-native rank has already earned: the number of
        /// milestones at or below it. Idempotent, and deliberately one-way.
        /// </summary>
        internal int ProjectFullRank(int fullRank)
        {
            int earned = 0;
            for (int i = 0; i < Milestones.Length; i++)
            {
                if (Milestones[i] <= fullRank)
                    earned++;
                else
                    break;
            }

            return earned;
        }

        /// <summary>Zero-based unique rank index of an original full rank, or -1.</summary>
        internal int IndexOfMilestone(int originalRank)
        {
            for (int i = 0; i < Milestones.Length; i++)
            {
                if (Milestones[i] == originalRank)
                    return i;
            }

            return -1;
        }

        /// <summary>Whether <paramref name="uniqueRank"/> owns the original rank's milestone.</summary>
        internal bool HasMilestone(int uniqueRank, int originalRank)
        {
            int index = IndexOfMilestone(originalRank);
            return index >= 0 && uniqueRank >= index + 1;
        }
    }

    /// <summary>
    /// The levels a row really sells when some of its stored ranks have no effect in this process
    /// (#441). The record keeps its stored ranks, so nothing is migrated: the displayed level is
    /// the number of live ranks at or below the stored rank, and a purchase writes the next live
    /// rank's stored value. Effects keep reading the stored rank itself.
    /// </summary>
    internal sealed class LiveRankLadder
    {
        internal LiveRankLadder(int[] storedRanks, int storedCap)
        {
            if (storedRanks == null || storedRanks.Length == 0)
                throw new ArgumentException("A live ladder needs at least one rank.", nameof(storedRanks));

            for (int i = 0; i < storedRanks.Length; i++)
            {
                if (storedRanks[i] <= (i == 0 ? 0 : storedRanks[i - 1]))
                    throw new ArgumentException("Live ranks must be positive and ascend.", nameof(storedRanks));
            }

            if (storedRanks[storedRanks.Length - 1] > storedCap)
                throw new ArgumentException("A live rank cannot exceed the stored cap.", nameof(storedCap));

            StoredRanks = storedRanks;
            StoredCap = storedCap;
        }

        /// <summary>The stored rank behind each live level, ascending.</summary>
        internal int[] StoredRanks { get; }

        /// <summary>The authored rank count of the record this ladder reads.</summary>
        internal int StoredCap { get; }

        internal int LiveCount => StoredRanks.Length;

        /// <summary>The live level a stored rank shows: live ranks at or below it.</summary>
        internal int Project(int storedRank)
        {
            int live = 0;
            while (live < StoredRanks.Length && StoredRanks[live] <= storedRank)
                live++;

            return live;
        }

        /// <summary>The stored rank that live level <paramref name="liveLevel"/> writes; 0 for none.</summary>
        internal int StoredRankFor(int liveLevel)
        {
            if (liveLevel <= 0)
                return 0;

            return StoredRanks[Math.Min(liveLevel, StoredRanks.Length) - 1];
        }

        /// <summary>
        /// The stored level whose own next step prices the live step bought from
        /// <paramref name="liveLevel"/>. Only the step into the live rank is charged, so a stored
        /// rank the ladder skips is dropped rather than folded into the price. Past the last live
        /// level this is the stored cap, which every record prices as not purchasable.
        /// </summary>
        internal int PricedStoredLevel(int liveLevel)
        {
            if (liveLevel < 0)
                liveLevel = 0;

            return liveLevel >= StoredRanks.Length ? StoredCap : StoredRanks[liveLevel] - 1;
        }

        /// <summary>Every live level's price, read from the record's own stored-level prices.</summary>
        internal int[] LivePrices(Func<int, int> storedLevelPrice)
        {
            var prices = new int[StoredRanks.Length];
            for (int i = 0; i < prices.Length; i++)
                prices[i] = storedLevelPrice(PricedStoredLevel(i));

            return prices;
        }
    }

    /// <summary>
    /// One native upgrade whose effects overlap Late Game Upgrades, and the imported rows that
    /// replace them.
    /// </summary>
    internal sealed class NativeUpgradeFamily
    {
        internal NativeUpgradeFamily(
            string nativeId,
            string[] lguMemberIds,
            NativeUpgradeVariant uniqueVariant,
            string prerequisiteReplacementId = null)
        {
            NativeId = nativeId;
            LguMemberIds = lguMemberIds ?? Array.Empty<string>();
            UniqueVariant = uniqueVariant;
            PrerequisiteReplacementId = prerequisiteReplacementId;
        }

        internal string NativeId { get; }

        /// <summary>Every one of these must be available before the family leaves Full mode.</summary>
        internal string[] LguMemberIds { get; }

        /// <summary>Null when the family is fully redundant and hides instead.</summary>
        internal NativeUpgradeVariant UniqueVariant { get; }

        /// <summary>
        /// The row that inherits this family's role as somebody else's purchase prerequisite when
        /// it is not in Full mode. Only Pumping Iron has one: Lethal Hands is gated on melee
        /// strength training, which stun-only Stagger is not.
        /// </summary>
        internal string PrerequisiteReplacementId { get; }
    }

    /// <summary>
    /// One resolved snapshot of which native families sell what. Catalog, effects, purchases,
    /// prices, gates and UI all read this same object so they cannot disagree, and
    /// <see cref="Generation"/> lets a pending purchase notice that the answer moved under it.
    /// </summary>
    internal sealed class NativeUpgradePolicy
    {
        private readonly Dictionary<string, NativeFamilyMode> _modes;
        private readonly bool _pending;

        internal NativeUpgradePolicy(
            int generation,
            LguUpgradeMode mode,
            Dictionary<string, NativeFamilyMode> modes,
            bool pending,
            bool nativeCatalogHidden)
        {
            Generation = generation;
            Mode = mode;
            _modes = modes ?? new Dictionary<string, NativeFamilyMode>(StringComparer.OrdinalIgnoreCase);
            _pending = pending;
            NativeCatalogHidden = nativeCatalogHidden;
        }

        /// <summary>
        /// Bumped only when a row's mode actually changed. A re-resolution that lands on the
        /// same answer must not invalidate quotes that are already in flight.
        /// </summary>
        internal int Generation { get; }

        /// <summary>The configured mode, whether or not Late Game Upgrades is installed.</summary>
        internal LguUpgradeMode Mode { get; }

        /// <summary>
        /// LGU-only mode with Late Game Upgrades integrated, ready or not: every native catalog
        /// row is hidden and inert, including rows that are no family. Decided from integration
        /// alone so a provider still loading can never briefly reinstate a native effect.
        /// </summary>
        internal bool NativeCatalogHidden { get; }

        /// <summary>
        /// The mode a row sells in. Imported rows are always Full; while the native catalog is
        /// hidden every other id is Hidden, family or not.
        /// </summary>
        internal NativeFamilyMode ModeOf(string upgradeId)
        {
            if (upgradeId == null)
                return NativeFamilyMode.Full;
            if (NativeCatalogHidden)
            {
                return NativeUpgradeFamilies.IsImportedId(upgradeId)
                    ? NativeFamilyMode.Full
                    : NativeFamilyMode.Hidden;
            }

            return _modes.TryGetValue(upgradeId, out NativeFamilyMode mode)
                ? mode
                : NativeFamilyMode.Full;
        }

        /// <summary>
        /// True while Late Game Upgrades is installed but has not finished loading in
        /// LGU-preferred mode, so this family's identity is not settled yet. Purchases are
        /// refused without charging.
        /// </summary>
        internal bool IsPending(string nativeId)
        {
            return _pending && nativeId != null && _modes.ContainsKey(nativeId);
        }

        internal UpgradeRankRecord RecordOf(string upgradeId)
        {
            return ModeOf(upgradeId) == NativeFamilyMode.UniqueOnly
                ? UpgradeRankRecord.UniqueOnly
                : UpgradeRankRecord.Full;
        }

        /// <summary>The state before anything is known: every native row sells its full self.</summary>
        internal static NativeUpgradePolicy AllFull { get; } = BuildAllFull();

        private static NativeUpgradePolicy BuildAllFull()
        {
            var modes = new Dictionary<string, NativeFamilyMode>(StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<NativeUpgradeFamily> families = NativeUpgradeFamilies.All;
            for (int i = 0; i < families.Count; i++)
                modes[families[i].NativeId] = NativeFamilyMode.Full;

            return new NativeUpgradePolicy(0, LguUpgradeMode.NativePreferred, modes, false, false);
        }
    }

    /// <summary>
    /// The authored overlap map between the native catalog and Late Game Upgrades (#435), plus the
    /// pure rules that turn provider availability into a policy and milestones into prices.
    /// Unity-free so the deterministic checks exercise the shipped rules rather than a copy.
    /// </summary>
    internal static class NativeUpgradeFamilies
    {
        private static readonly NativeUpgradeFamily[] Families =
        {
            // Late Game Upgrades' own Back Muscles covers the whole carry-weight effect.
            new NativeUpgradeFamily("back_muscles", new[] { "lgu_back_muscles" }, null),
            new NativeUpgradeFamily(
                "protein_powder",
                new[] { "lgu_protein_powder" },
                new NativeUpgradeVariant(
                    "Stagger",
                    "Description: Melee weapon hits have a 25% chance to stun the struck enemy for 1 second.\nLevel 1: Stagger chance active.",
                    new[] { 2 }),
                "lgu_protein_powder"),
            // Stimpack and Medical Nanobots cover the health cap and the healing ceiling, and
            // Resilience has nothing left of its own, so it hides.
            new NativeUpgradeFamily("thick_skin", new[] { "lgu_stimpack", "lgu_medical_nanobots" }, null),
            // Sprinter's speed, stamina and quiet-crouch ladder is covered whole.
            new NativeUpgradeFamily(
                "physical_conditioning",
                new[] { "lgu_running_shoes", "lgu_bigger_lungs", "lgu_carbon_kneejoints" },
                null),
            // Only the sinking reduction of Surefooted's first rank is its own.
            new NativeUpgradeFamily(
                SurefootedId,
                new[]
                {
                    "lgu_strong_legs",
                    "lgu_hiking_boots",
                    "lgu_reinforced_boots",
                    "lgu_traction_boots",
                    "lgu_climbing_gloves"
                },
                new NativeUpgradeVariant(
                    "Firm Footing",
                    "Description: You sink 20% slower in water and quicksand.\nLevel 1: Sinking slowed by 20%.",
                    new[] { 1 })),
            // Interaction speed and reach are both sold by Late Game Upgrades.
            new NativeUpgradeFamily("quick_hands", new[] { "lgu_quick_hands", "lgu_mechanical_arms" }, null),
            new NativeUpgradeFamily(
                "better_scanner",
                new[] { "lgu_better_scanner" },
                new NativeUpgradeVariant(
                    "Field Lighting",
                    "Description: Brighter, wider flashlight beam.\nLevel 1: Flashlight brightness +10%, cone width +10%.\nLevel 2: Brightness +15%, cone width +20%.\nLevel 3: Brightness +40%, cone width +30%.",
                    new[] { 1, 2, 3 })),
            new NativeUpgradeFamily(
                "extra_inventory_slot",
                new[] { "lgu_deeper_pockets" },
                new NativeUpgradeVariant(
                    "Expanded Hotbar",
                    "Description: Adds hotbar slots to the standard four.\nLevel 1: Five hotbar slots.\nLevel 2: Six hotbar slots.\nLevel 3: Seven hotbar slots.",
                    new[] { 1, 2, 3 })),
            new NativeUpgradeFamily(
                "turret_hacker",
                new[] { "lgu_locksmith" },
                new NativeUpgradeVariant(
                    "Field Mechanic",
                    "Description: On-site sabotage of facility hardware. Every hold interaction takes 1.2 seconds.\nLevel 1: Fuel pumps are half as likely to stall, and Company CCTV cameras can be disabled for the rest of the round.\nLevel 2: Turrets can be disabled for 90 seconds from the Field Operations tablet or a nearby hold, with a 20 second cooldown.",
                    new[] { 1, 3 }))
        };

        private static readonly Dictionary<string, NativeUpgradeFamily> ByNativeId = BuildIndex();
        private static readonly HashSet<string> Overlap = BuildOverlapSet();

        internal static IReadOnlyList<NativeUpgradeFamily> All => Families;

        /// <summary>The imported rows that replace family effects: 17 of the 38.</summary>
        internal static IReadOnlyCollection<string> OverlapMemberIds => Overlap;

        internal static bool TryGet(string nativeId, out NativeUpgradeFamily family)
        {
            family = null;
            return !string.IsNullOrWhiteSpace(nativeId) && ByNativeId.TryGetValue(nativeId, out family);
        }

        internal static bool IsFamily(string nativeId)
        {
            return !string.IsNullOrWhiteSpace(nativeId) && ByNativeId.ContainsKey(nativeId);
        }

        internal static bool IsOverlapMember(string lguId)
        {
            return !string.IsNullOrWhiteSpace(lguId) && Overlap.Contains(lguId);
        }

        /// <summary>Every imported Late Game Upgrades catalog id starts with this.</summary>
        internal const string ImportedIdPrefix = "lgu_";

        internal static bool IsImportedId(string upgradeId)
        {
            return upgradeId != null && upgradeId.StartsWith(ImportedIdPrefix, StringComparison.OrdinalIgnoreCase);
        }

        internal const string FieldMechanicId = "turret_hacker";
        internal const string SurefootedId = "surefooted";
        internal const string CommandNetId = "command_net";
        internal const string WalkieGpsId = "lgu_walkie_gps";

        /// <summary>
        /// Whether an imported row is adopted but not sold because a native row already supplies
        /// it (#435). The row stays out of Late Game Upgrades' own store and its effect stays
        /// unwound; owned ranks stay dormant. Native-preferred mode suppresses the family overlap
        /// members, and Walkie GPS yields to Command Net wherever Command Net is enabled. LGU-only
        /// mode sells every imported row, since no native row is live there.
        /// </summary>
        /// <param name="commandNetEnabled">Command Net's own Enabled switch.</param>
        internal static bool IsSuppressedImport(LguUpgradeMode mode, string importedId, bool commandNetEnabled)
        {
            if (string.IsNullOrWhiteSpace(importedId) || mode == LguUpgradeMode.LguOnly)
                return false;
            if (mode == LguUpgradeMode.NativePreferred && IsOverlapMember(importedId))
                return true;

            return commandNetEnabled && string.Equals(importedId, WalkieGpsId, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Field Mechanic's full rank 1 (fuel pumps and Company CCTV cameras) acts only through Y4NGZ
        /// Ship Systems or LethalCCTV. Without either it is not sold (#441).
        /// </summary>
        private const int FieldMechanicProviderRank = 1;

        /// <summary>
        /// The live ladder a row sells in this process, or null when it sells every stored rank.
        /// Only Field Mechanic without its hacking providers has one: Full mode sells doors and
        /// turrets (stored ranks 2 and 3), the unique variant sells turrets (unique rank 2). The
        /// provider set is fixed for the process, so this never changes a policy generation.
        /// </summary>
        /// <param name="authoredRanks">The row's authored full-native rank count.</param>
        internal static LiveRankLadder LiveLadderFor(
            string nativeId,
            NativeFamilyMode mode,
            int authoredRanks,
            bool fieldMechanicProvidersInstalled)
        {
            if (fieldMechanicProvidersInstalled
                || !string.Equals(nativeId, FieldMechanicId, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var live = new List<int>();
            switch (mode)
            {
                case NativeFamilyMode.Full:
                    for (int rank = 1; rank <= authoredRanks; rank++)
                    {
                        if (rank != FieldMechanicProviderRank)
                            live.Add(rank);
                    }

                    return live.Count == 0 ? null : new LiveRankLadder(live.ToArray(), authoredRanks);
                case NativeFamilyMode.UniqueOnly:
                    if (!TryGet(nativeId, out NativeUpgradeFamily family) || family.UniqueVariant == null)
                        return null;

                    int[] milestones = family.UniqueVariant.Milestones;
                    for (int uniqueRank = 1; uniqueRank <= milestones.Length; uniqueRank++)
                    {
                        if (milestones[uniqueRank - 1] != FieldMechanicProviderRank)
                            live.Add(uniqueRank);
                    }

                    return live.Count == 0 ? null : new LiveRankLadder(live.ToArray(), milestones.Length);
                default:
                    return null;
            }
        }

        /// <summary>
        /// Price of the NEXT unique rank. The unique rank at original milestone T aggregates the
        /// configured full-native steps from wherever this save already stands up to T, so a
        /// partly progressed save never pays for a step it already bought, and a fresh save pays
        /// exactly what the full ladder to that milestone costs. This is pricing credit, never a
        /// rank conversion.
        /// </summary>
        /// <param name="uniqueRank">Unique ranks already owned on this save.</param>
        /// <param name="fullRank">Full-native ranks already owned on this save.</param>
        internal static int UniqueRankPrice(
            NativeUpgradeVariant variant,
            int uniqueRank,
            int fullRank,
            int unlockPrice,
            int[] tierPrices)
        {
            if (variant == null || uniqueRank < 0 || uniqueRank >= variant.RankCount)
                return int.MaxValue;

            int target = variant.Milestones[uniqueRank];
            int start = uniqueRank == 0 ? 0 : variant.Milestones[uniqueRank - 1];
            if (fullRank > start)
                start = fullRank;
            if (start < 0)
                start = 0;

            int total = 0;
            for (int rank = start; rank < target; rank++)
            {
                if (rank == 0)
                {
                    total += Math.Max(0, unlockPrice);
                    continue;
                }

                int index = rank - 1;
                if (tierPrices != null && index < tierPrices.Length)
                    total += Math.Max(0, tierPrices[index]);
            }

            return total;
        }

        /// <summary>
        /// Whether Late Game Upgrades takes part in the player menu at all (#493): the plugin is
        /// installed and the local Integrate LGU Into Player Menu switch is on. It is the only LGU
        /// presence the policy, the catalog and the bridge act on. False is exactly the path an
        /// install without Late Game Upgrades takes, so a switched-off integration can never hide
        /// native rows without an imported replacement.
        /// </summary>
        internal static bool IntegrationActive(bool lguInstalled, bool integrateIntoPlayerMenu)
        {
            return lguInstalled && integrateIntoPlayerMenu;
        }

        /// <summary>
        /// The one place a mode and a provider state become a decision. Without Late Game
        /// Upgrades integrated every mode is the full native tree. LGU-only mode hides the whole
        /// native catalog from the moment the provider is integrated, before it is ready.
        /// LGU-preferred mode moves a family off Full only once every row that replaces it is
        /// really there, and a transient not-ready state keeps the settled identity instead of
        /// briefly reinstating full native effects.
        /// </summary>
        /// <param name="lguIntegrated">
        /// <see cref="IntegrationActive"/>: installed and integrated into the player menu.
        /// </param>
        /// <param name="memberAvailable">
        /// True when the bridge owns that row, it is not suppressed, and it sells at least one
        /// level.
        /// </param>
        internal static NativeUpgradePolicy Resolve(
            LguUpgradeMode upgradeMode,
            bool lguIntegrated,
            bool lguReady,
            Func<string, bool> memberAvailable,
            NativeUpgradePolicy previous)
        {
            NativeUpgradePolicy before = previous ?? NativeUpgradePolicy.AllFull;
            bool nativeHidden = lguIntegrated && upgradeMode == LguUpgradeMode.LguOnly;
            bool preferLgu = lguIntegrated && upgradeMode == LguUpgradeMode.LguPreferred;
            bool pending = preferLgu && !lguReady;

            var modes = new Dictionary<string, NativeFamilyMode>(
                Families.Length, StringComparer.OrdinalIgnoreCase);
            bool changed = nativeHidden != before.NativeCatalogHidden;
            for (int i = 0; i < Families.Length; i++)
            {
                NativeUpgradeFamily family = Families[i];
                NativeFamilyMode mode;
                if (nativeHidden)
                    mode = NativeFamilyMode.Hidden;
                else if (!preferLgu)
                    mode = NativeFamilyMode.Full;
                else if (!lguReady)
                    mode = before.ModeOf(family.NativeId);
                else if (!EveryMemberAvailable(family, memberAvailable))
                    mode = NativeFamilyMode.Full;
                else
                    mode = family.UniqueVariant == null ? NativeFamilyMode.Hidden : NativeFamilyMode.UniqueOnly;

                modes[family.NativeId] = mode;
                if (mode != before.ModeOf(family.NativeId))
                    changed = true;
            }

            return new NativeUpgradePolicy(
                changed ? before.Generation + 1 : before.Generation,
                upgradeMode,
                modes,
                pending,
                nativeHidden);
        }

        private static bool EveryMemberAvailable(NativeUpgradeFamily family, Func<string, bool> memberAvailable)
        {
            if (memberAvailable == null)
                return false;

            for (int i = 0; i < family.LguMemberIds.Length; i++)
            {
                if (!memberAvailable(family.LguMemberIds[i]))
                    return false;
            }

            return true;
        }

        private static Dictionary<string, NativeUpgradeFamily> BuildIndex()
        {
            var index = new Dictionary<string, NativeUpgradeFamily>(
                Families.Length, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Families.Length; i++)
                index[Families[i].NativeId] = Families[i];

            return index;
        }

        private static HashSet<string> BuildOverlapSet()
        {
            var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Families.Length; i++)
            {
                string[] ids = Families[i].LguMemberIds;
                for (int m = 0; m < ids.Length; m++)
                    members.Add(ids[m]);
            }

            return members;
        }
    }
}
