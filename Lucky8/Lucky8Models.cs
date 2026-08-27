using System;
using System.Collections.Generic;

namespace Y4NGZUpgrades.Lucky8
{
    internal enum Lucky8RewardCategory : byte
    {
        Weapon = 0,
        Suit = 1,
        Cosmetic = 2,
        Ammo = 3,
        Emote = 4,
    }

    internal enum Lucky8Rarity : byte
    {
        Common,
        Uncommon,
        Rare,
        Legendary,
    }

    internal sealed class Lucky8RewardDefinition
    {
        internal string StableId;
        internal string DisplayName;
        internal Lucky8RewardCategory Category;
        internal Lucky8Rarity Rarity;
        internal int WinWeight;
        internal UnityEngine.Texture IconTexture;
        internal object IconSource;
        internal uint PresentationColorRgba;
        internal bool HasPresentationColor;
    }

    internal sealed class Lucky8RouteInfo
    {
        internal int RouteIndex;
        internal int LevelId;
        internal string MoonName;
        internal string ConstellationKey;
        internal bool IsCompanyMoon;
        internal bool HasFacility;
    }

    internal sealed class Lucky8MachineState
    {
        internal ulong NetworkObjectId;
        internal readonly Lucky8RewardDefinition[] Rewards = new Lucky8RewardDefinition[8];
        internal byte SoldMask;
        internal int SpinsRemaining;
        internal bool IsSpinning;
        internal int WinnerIndex = -1;
        internal double SpinStartedAt;
        internal float SpinDuration;

        internal bool IsSold(int index) => index >= 0 && index < 8 && (SoldMask & (1 << index)) != 0;
    }

    internal static class Lucky8LineupGenerator
    {
        internal static bool TryCreate(
            IReadOnlyList<Lucky8RewardDefinition> weapons,
            IReadOnlyList<Lucky8RewardDefinition> suits,
            IReadOnlyList<Lucky8RewardDefinition> cosmetics,
            int seed,
            out Lucky8RewardDefinition[] lineup,
            out string reason)
        {
            return TryCreate(weapons, suits, cosmetics, null, seed, out lineup, out reason);
        }

        internal static bool TryCreate(
            IReadOnlyList<Lucky8RewardDefinition> weapons,
            IReadOnlyList<Lucky8RewardDefinition> suits,
            IReadOnlyList<Lucky8RewardDefinition> cosmetics,
            IReadOnlyList<Lucky8RewardDefinition> ammo,
            int seed,
            out Lucky8RewardDefinition[] lineup,
            out string reason)
        {
            return TryCreate(weapons, suits, cosmetics, ammo, null, seed, out lineup, out reason);
        }

        internal static bool TryCreate(
            IReadOnlyList<Lucky8RewardDefinition> weapons,
            IReadOnlyList<Lucky8RewardDefinition> suits,
            IReadOnlyList<Lucky8RewardDefinition> cosmetics,
            IReadOnlyList<Lucky8RewardDefinition> ammo,
            IReadOnlyList<Lucky8RewardDefinition> emotes,
            int seed,
            out Lucky8RewardDefinition[] lineup,
            out string reason)
        {
            lineup = null;
            reason = string.Empty;
            if (weapons == null || weapons.Count < 3 || suits == null || suits.Count < 3 || cosmetics == null || cosmetics.Count < 3)
            {
                reason = $"minimum-pool-not-met weapons={weapons?.Count ?? 0} suits={suits?.Count ?? 0} cosmetics={cosmetics?.Count ?? 0}";
                return false;
            }

            var random = new StableRandom(seed);
            var selected = new List<Lucky8RewardDefinition>(8);
            bool hasAmmo = ammo != null && ammo.Count > 0;
            bool hasEmotes = emotes != null && emotes.Count > 0;
            int optionalCategories = (hasAmmo ? 1 : 0) + (hasEmotes ? 1 : 0);
            int ammoSlots = hasAmmo ? (optionalCategories == 1 ? Math.Min(2, ammo.Count) : 1) : 0;
            int emoteSlots = hasEmotes ? (optionalCategories == 1 ? Math.Min(2, emotes.Count) : 1) : 0;
            int requiredSlots = 8 - ammoSlots - emoteSlots;
            int requiredBase = requiredSlots / 3;
            int requiredRemainder = requiredSlots % 3;
            var requiredCounts = new[] { requiredBase, requiredBase, requiredBase };
            var requiredOrder = new List<int> { 0, 1, 2 };
            Shuffle(requiredOrder, random);
            for (int i = 0; i < requiredRemainder; i++)
                requiredCounts[requiredOrder[i]]++;

            AddUniqueRandom(selected, weapons, requiredCounts[0], random);
            AddUniqueRandom(selected, suits, requiredCounts[1], random);
            AddUniqueRandom(selected, cosmetics, requiredCounts[2], random);
            if (ammoSlots > 0) AddUniqueRandom(selected, ammo, ammoSlots, random);
            if (emoteSlots > 0) AddUniqueRandom(selected, emotes, emoteSlots, random);

            for (int i = selected.Count - 1; i > 0; i--)
            {
                int swap = random.Next(i + 1);
                (selected[i], selected[swap]) = (selected[swap], selected[i]);
            }

            lineup = selected.ToArray();
            return lineup.Length == 8;
        }

        internal static int PickWinner(Lucky8MachineState state, int seed)
        {
            if (state == null)
                return -1;

            int total = 0;
            for (int i = 0; i < state.Rewards.Length; i++)
            {
                if (!state.IsSold(i) && state.Rewards[i] != null)
                    total += Math.Max(1, state.Rewards[i].WinWeight);
            }
            if (total <= 0)
                return -1;

            int roll = new StableRandom(seed).Next(total);
            for (int i = 0; i < state.Rewards.Length; i++)
            {
                if (state.IsSold(i) || state.Rewards[i] == null)
                    continue;
                roll -= Math.Max(1, state.Rewards[i].WinWeight);
                if (roll < 0)
                    return i;
            }
            return -1;
        }

        private static void AddUniqueRandom(
            List<Lucky8RewardDefinition> destination,
            IReadOnlyList<Lucky8RewardDefinition> source,
            int count,
            StableRandom random)
        {
            var indices = new List<int>(source.Count);
            for (int i = 0; i < source.Count; i++) indices.Add(i);
            for (int i = 0; i < count; i++)
            {
                int pick = random.Next(indices.Count);
                destination.Add(source[indices[pick]]);
                indices.RemoveAt(pick);
            }
        }

        private static void Shuffle(List<int> values, StableRandom random)
        {
            for (int i = values.Count - 1; i > 0; i--)
            {
                int swap = random.Next(i + 1);
                (values[i], values[swap]) = (values[swap], values[i]);
            }
        }
    }

    internal static class Lucky8RewardResolver
    {
        internal static Lucky8RewardDefinition Resolve(
            IReadOnlyList<Lucky8RewardDefinition> pool,
            Lucky8RewardCategory category,
            string stableId,
            string displayName,
            Lucky8Rarity rarity,
            int winWeight)
        {
            if (pool != null)
            {
                for (int i = 0; i < pool.Count; i++)
                {
                    Lucky8RewardDefinition candidate = pool[i];
                    if (candidate == null
                        || !string.Equals(candidate.StableId, stableId, StringComparison.OrdinalIgnoreCase))
                        continue;
                    candidate.Rarity = rarity;
                    candidate.WinWeight = Math.Max(1, winWeight);
                    return candidate;
                }
            }

            return new Lucky8RewardDefinition
            {
                StableId = stableId ?? string.Empty,
                DisplayName = string.IsNullOrWhiteSpace(displayName)
                    ? stableId ?? "Unknown reward"
                    : displayName,
                Category = category,
                Rarity = rarity,
                WinWeight = Math.Max(1, winWeight),
            };
        }
    }

    internal sealed class StableRandom
    {
        private uint state;

        internal StableRandom(int seed)
        {
            state = unchecked((uint)seed);
            if (state == 0) state = 0x6D2B79F5u;
        }

        internal int Next(int maximum)
        {
            if (maximum <= 1) return 0;
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return (int)(state % (uint)maximum);
        }

        internal double NextDouble()
        {
            return Next(int.MaxValue) / (double)int.MaxValue;
        }
    }
}
