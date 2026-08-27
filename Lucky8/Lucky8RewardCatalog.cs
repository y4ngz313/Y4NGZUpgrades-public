using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Y4NGZUpgrades.Gui;

namespace Y4NGZUpgrades.Lucky8
{
    internal static class Lucky8RewardCatalog
    {
        /// <summary>
        /// Weapon prizes, read from Better Armory across the <see cref="ArmoryBridge"/> (#267).
        /// Empty without that plugin, which is the same shape as a weapon pool configured away.
        /// </summary>
        internal static IReadOnlyList<Lucky8RewardDefinition> BuildWeapons()
        {
            var discovered = new List<Lucky8RewardDefinition>();
            foreach (string id in ArmoryBridge.GetWeaponPrizeIds())
            {
                discovered.Add(new Lucky8RewardDefinition
                {
                    StableId = id,
                    DisplayName = ArmoryBridge.GetWeaponDisplayName(id),
                    Category = Lucky8RewardCategory.Weapon,
                });
            }
            return ApplyPool(discovered, Lucky8Manager.Config.WeaponPool.Value);
        }

        /// <summary>Same as <see cref="BuildWeapons"/>, for ammo pickups.</summary>
        internal static IReadOnlyList<Lucky8RewardDefinition> BuildAmmo()
        {
            var discovered = new List<Lucky8RewardDefinition>();
            foreach (string id in ArmoryBridge.GetAmmoPrizeIds())
            {
                discovered.Add(new Lucky8RewardDefinition
                {
                    StableId = id,
                    DisplayName = ArmoryBridge.GetAmmoDisplayName(id),
                    Category = Lucky8RewardCategory.Ammo,
                });
            }
            return ApplyPool(discovered, Lucky8Manager.Config.AmmoPool.Value);
        }

        internal static IReadOnlyList<Lucky8RewardDefinition> BuildSuits()
        {
            var discovered = new List<Lucky8RewardDefinition>();
            List<UnlockableItem> unlockables = StartOfRound.Instance?.unlockablesList?.unlockables;
            if (unlockables != null)
            {
                for (int i = 0; i < unlockables.Count; i++)
                {
                    UnlockableItem suit = unlockables[i];
                    if (suit == null || suit.unlockableType != 0 || suit.suitMaterial == null || suit.alreadyUnlocked)
                        continue;
                    string label = string.IsNullOrWhiteSpace(suit.unlockableName) ? "Suit " + i : suit.unlockableName.Trim();
                    discovered.Add(new Lucky8RewardDefinition
                    {
                        StableId = Slug(label),
                        DisplayName = label,
                        Category = Lucky8RewardCategory.Suit,
                    });
                }
            }
            return ApplyPool(discovered, Lucky8Manager.Config.SuitPool.Value);
        }

        internal static IReadOnlyList<Lucky8RewardDefinition> BuildCosmetics()
        {
            var discovered = Lucky8IconResolver.DiscoverMoreCompanyCosmetics()
                .Select(entry => new Lucky8RewardDefinition
                {
                    StableId = entry.id,
                    DisplayName = entry.label,
                    Category = Lucky8RewardCategory.Cosmetic,
                })
                .ToList();
            return ApplyPool(discovered, Lucky8Manager.Config.CosmeticPool.Value);
        }

        internal static IReadOnlyList<Lucky8RewardDefinition> BuildEmotes()
        {
            return BuildEmotes(excludeOwned: true);
        }

        private static IReadOnlyList<Lucky8RewardDefinition> BuildEmotes(bool excludeOwned)
        {
            List<TooManyEmotesEntry> emotes = TooManyEmotesBridge.GetAllPurchasableEmotes();
            if (emotes == null) return Array.Empty<Lucky8RewardDefinition>();

            var discovered = new List<Lucky8RewardDefinition>();
            for (int i = 0; i < emotes.Count; i++)
            {
                TooManyEmotesEntry emote = emotes[i];
                if (excludeOwned)
                {
                    bool owned = TooManyEmotesBridge.IsEmoteUnlocked(emote.obj);
                    if (!TooManyEmotesBridge.IsAvailable)
                        return Array.Empty<Lucky8RewardDefinition>();
                    if (owned) continue;
                }

                discovered.Add(CreateEmoteReward(emote));
            }
            return ApplyPool(discovered, Lucky8Manager.Config.EmotePool.Value);
        }

        internal static Lucky8RewardDefinition Resolve(
            Lucky8RewardCategory category,
            string stableId,
            string displayName,
            Lucky8Rarity rarity)
        {
            if (category == Lucky8RewardCategory.Emote)
            {
                TooManyEmotesEntry emote = TooManyEmotesBridge.FindPurchasableEmote(stableId);
                if (emote != null)
                {
                    Lucky8RewardDefinition resolvedEmote = CreateEmoteReward(emote);
                    resolvedEmote.Rarity = rarity;
                    resolvedEmote.WinWeight = Lucky8Manager.Config.WeightFor(rarity);
                    return resolvedEmote;
                }
            }

            IReadOnlyList<Lucky8RewardDefinition> pool;
            switch (category)
            {
                case Lucky8RewardCategory.Weapon:
                    pool = BuildWeapons();
                    break;
                case Lucky8RewardCategory.Suit:
                    pool = BuildSuits();
                    break;
                case Lucky8RewardCategory.Ammo:
                    pool = BuildAmmo();
                    break;
                case Lucky8RewardCategory.Emote:
                    pool = Array.Empty<Lucky8RewardDefinition>();
                    break;
                default:
                    pool = BuildCosmetics();
                    break;
            }
            return Lucky8RewardResolver.Resolve(
                pool,
                category,
                stableId,
                displayName,
                rarity,
                Lucky8Manager.Config.WeightFor(rarity));
        }

        internal static bool HasMinimumPools(out string reason)
        {
            // Weapons only gate the machine when Better Armory is installed. Without it there ARE
            // no weapons, so requiring three of them would take LUCKY-8 offline entirely rather
            // than running it on the categories that do exist (#267).
            int suits = BuildSuits().Count;
            int cosmetics = BuildCosmetics().Count;
            bool valid = suits >= 3 && cosmetics >= 3;
            if (!ArmoryBridge.IsArmoryPresent)
            {
                reason = valid ? string.Empty : $"suits={suits}, cosmetics={cosmetics}; each category requires at least 3";
                return valid;
            }

            int weapons = BuildWeapons().Count;
            valid = valid && weapons >= 3;
            reason = valid ? string.Empty : $"weapons={weapons}, suits={suits}, cosmetics={cosmetics}; each category requires at least 3";
            return valid;
        }

        internal static int MinimumPoolSourceFingerprint()
        {
            unchecked
            {
                uint hash = 2166136261u;
                // Weapons contribute nothing without Better Armory, so an Upgrades-only install
                // simply has a shorter fingerprint rather than a different one every rebuild.
                foreach (string token in ArmoryBridge.GetWeaponFingerprintTokens())
                    AddFingerprint(ref hash, token);

                List<UnlockableItem> unlockables = StartOfRound.Instance?.unlockablesList?.unlockables;
                if (unlockables == null)
                {
                    AddFingerprint(ref hash, "suits-unavailable");
                }
                else
                {
                    for (int i = 0; i < unlockables.Count; i++)
                    {
                        UnlockableItem suit = unlockables[i];
                        if (suit == null || suit.unlockableType != 0 || suit.suitMaterial == null)
                            continue;
                        AddFingerprint(ref hash, i.ToString());
                        AddFingerprint(ref hash, suit.unlockableName);
                        AddFingerprint(ref hash, suit.alreadyUnlocked ? "owned" : "eligible");
                    }
                }

                foreach ((string id, string _) in Lucky8IconResolver.DiscoverMoreCompanyCosmetics())
                    AddFingerprint(ref hash, id);
                return (int)hash;
            }
        }

        private static void AddFingerprint(ref uint hash, string value)
        {
            unchecked
            {
                foreach (char character in value ?? string.Empty)
                {
                    hash ^= character;
                    hash *= 16777619u;
                }
                hash ^= '|';
                hash *= 16777619u;
            }
        }

        private static IReadOnlyList<Lucky8RewardDefinition> ApplyPool(
            List<Lucky8RewardDefinition> discovered,
            string configuration)
        {
            var explicitRarity = new Dictionary<string, Lucky8Rarity?>(StringComparer.OrdinalIgnoreCase);
            Lucky8Rarity? wildcardRarity = null;
            bool includeWildcard = false;
            foreach (string raw in (configuration ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] pieces = raw.Split('|');
                string id = pieces[0].Trim();
                if (id.Length == 0) continue;
                Lucky8Rarity? configuredRarity = pieces.Length > 1 && !string.IsNullOrWhiteSpace(pieces[1])
                    ? ParseRarity(pieces[1])
                    : null;
                if (id == "*")
                {
                    includeWildcard = true;
                    wildcardRarity = configuredRarity;
                }
                else
                {
                    explicitRarity[id] = configuredRarity;
                }
            }

            var result = new List<Lucky8RewardDefinition>();
            foreach (Lucky8RewardDefinition reward in discovered)
            {
                if (!explicitRarity.TryGetValue(reward.StableId, out Lucky8Rarity? rarityOverride))
                {
                    if (!includeWildcard) continue;
                    rarityOverride = wildcardRarity;
                }
                if (rarityOverride.HasValue)
                    reward.Rarity = rarityOverride.Value;
                reward.WinWeight = Lucky8Manager.Config.WeightFor(reward.Rarity);
                result.Add(reward);
            }
            return result
                .GroupBy(reward => reward.StableId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(reward => reward.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static Lucky8Rarity ParseRarity(string value)
        {
            return Enum.TryParse((value ?? string.Empty).Trim(), true, out Lucky8Rarity rarity)
                ? rarity
                : Lucky8Rarity.Common;
        }

        internal static Lucky8Rarity MapEmoteRarity(int tier, string tierName)
        {
            string normalized = (tierName ?? string.Empty).Trim();
            if (normalized.IndexOf("legend", StringComparison.OrdinalIgnoreCase) >= 0)
                return Lucky8Rarity.Legendary;
            if (normalized.IndexOf("epic", StringComparison.OrdinalIgnoreCase) >= 0)
                return Lucky8Rarity.Rare;
            if (normalized.IndexOf("uncommon", StringComparison.OrdinalIgnoreCase) >= 0)
                return Lucky8Rarity.Uncommon;
            return tier <= 0
                ? Lucky8Rarity.Common
                : tier == 1
                    ? Lucky8Rarity.Uncommon
                    : tier == 2
                        ? Lucky8Rarity.Rare
                        : Lucky8Rarity.Legendary;
        }

        private static Lucky8RewardDefinition CreateEmoteReward(TooManyEmotesEntry emote)
        {
            return new Lucky8RewardDefinition
            {
                StableId = emote.emoteName,
                DisplayName = emote.displayName,
                Category = Lucky8RewardCategory.Emote,
                Rarity = MapEmoteRarity(emote.rarity, emote.rarityName),
                IconSource = emote.icon,
                PresentationColorRgba = Lucky8RewardPresentation.PackColor(emote.rarityColor),
                HasPresentationColor = true,
            };
        }

        private static string Slug(string value)
        {
            char[] chars = (value ?? string.Empty).ToLowerInvariant()
                .Select(character => char.IsLetterOrDigit(character) ? character : '_')
                .ToArray();
            return new string(chars).Trim('_');
        }
    }
}
