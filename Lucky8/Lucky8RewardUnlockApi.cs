using System;
using System.Collections.Generic;
using Y4NGZUpgrades.Gui;

namespace Y4NGZUpgrades.Lucky8
{
    internal static class Lucky8RewardUnlockApi
    {
        internal static bool IsOwned(Lucky8RewardDefinition reward)
        {
            if (reward == null) return true;
            PlayerLevelData data = PlayerLevelStore.Get();
            switch (reward.Category)
            {
                case Lucky8RewardCategory.Suit:
                    return data.suits.Exists(value => string.Equals(value, reward.DisplayName, StringComparison.OrdinalIgnoreCase));
                case Lucky8RewardCategory.Cosmetic:
                    return data.cosmetics.Exists(value => string.Equals(value, reward.StableId, StringComparison.OrdinalIgnoreCase));
                case Lucky8RewardCategory.Emote:
                    return TooManyEmotesBridge.IsEmoteUnlocked(reward.StableId);
                default:
                    return false;
            }
        }

        internal static bool TryClaim(Lucky8RewardDefinition reward, out string reason)
        {
            reason = string.Empty;
            if (reward == null
                || reward.Category == Lucky8RewardCategory.Weapon
                || reward.Category == Lucky8RewardCategory.Ammo)
            {
                reason = "invalid-claim-reward";
                return false;
            }
            if (IsOwned(reward))
            {
                reason = "already-owned";
                return false;
            }

            if (reward.Category == Lucky8RewardCategory.Emote)
            {
                if (!TooManyEmotesBridge.IsAvailable)
                {
                    reason = "emote-unavailable";
                    return false;
                }
                if (!TooManyEmotesBridge.TryUnlockEmoteLocal(reward.StableId))
                {
                    reason = "unlock-failed";
                    return false;
                }
                reason = "claimed";
                return true;
            }

            PlayerLevelData data = PlayerLevelStore.Get();
            if (reward.Category == Lucky8RewardCategory.Suit)
                data.suits.Add(reward.DisplayName);
            else
                data.cosmetics.Add(reward.StableId);
            PlayerLevelStore.Save();
            reason = "claimed";
            return true;
        }

    }
}
