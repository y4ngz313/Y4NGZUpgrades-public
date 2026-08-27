using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Y4NGZUpgrades.Gui
{
    internal sealed class TooManyEmotesEntry
    {
        internal string emoteName;
        internal string displayName;
        internal object obj;
        internal int rarity;
        internal string rarityName;
        internal Color rarityColor;
        internal object icon;
    }

    /// <summary>
    /// Reflected soft-dependency boundary shared by the purchase menu and
    /// optional reward systems. TooManyEmotes remains entirely optional.
    /// </summary>
    internal static class TooManyEmotesBridge
    {
        private static readonly Color[] FallbackTierColors =
        {
            new Color(0.78f, 0.78f, 0.78f, 1f),
            new Color(0.29f, 0.62f, 1.00f, 1f),
            new Color(0.69f, 0.29f, 1.00f, 1f),
            new Color(1.00f, 0.70f, 0.28f, 1f),
        };

        private static readonly Dictionary<int, Color> TierColors = new Dictionary<int, Color>();
        private static readonly string[] IconMemberNames =
        {
            "icon", "Icon", "iconSprite", "IconSprite", "emoteIcon", "EmoteIcon",
            "iconTexture", "IconTexture",
        };

        private static bool resolved;
        private static bool available;
        private static bool unavailableWarningLogged;
        private static FieldInfo allUnlockableEmotes;
        private static FieldInfo emoteName;
        private static FieldInfo displayName;
        private static FieldInfo purchasable;
        private static FieldInfo complementary;
        private static FieldInfo rarity;
        private static PropertyInfo rarityText;
        private static MethodInfo isEmoteUnlocked;
        private static MethodInfo unlockEmoteLocal;
        private static PropertyInfo nameColor;
        private static FieldInfo rarityColorCodes;

        internal static bool IsAvailable
        {
            get
            {
                EnsureResolved();
                if (!available) MarkUnavailable();
                return available;
            }
        }

        internal static void EnsureResolved()
        {
            if (resolved) return;
            resolved = true;
            try
            {
                Assembly assembly = null;
                foreach (Assembly candidate in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (candidate.GetName().Name == "TooManyEmotes")
                    {
                        assembly = candidate;
                        break;
                    }
                }
                if (assembly == null)
                {
                    MarkUnavailable();
                    return;
                }

                Type emotesManager = assembly.GetType("TooManyEmotes.EmotesManager");
                Type sessionManager = assembly.GetType("TooManyEmotes.SessionManager");
                Type unlockable = assembly.GetType("TooManyEmotes.UnlockableEmote");
                if (emotesManager == null || sessionManager == null || unlockable == null)
                {
                    MarkUnavailable();
                    return;
                }

                const BindingFlags StaticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                const BindingFlags InstanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                allUnlockableEmotes = emotesManager.GetField("allUnlockableEmotes", StaticFlags);
                emoteName = unlockable.GetField("emoteName", InstanceFlags);
                displayName = unlockable.GetField("displayName", InstanceFlags);
                purchasable = unlockable.GetField("purchasable", InstanceFlags);
                complementary = unlockable.GetField("complementary", InstanceFlags);
                rarity = unlockable.GetField("rarity", InstanceFlags);
                rarityText = unlockable.GetProperty("rarityText", InstanceFlags);
                nameColor = unlockable.GetProperty("nameColor", InstanceFlags);
                rarityColorCodes = unlockable.GetField("rarityColorCodes", StaticFlags);

                foreach (MethodInfo method in sessionManager.GetMethods(StaticFlags))
                {
                    ParameterInfo[] parameters = method.GetParameters();
                    if (method.Name == "IsEmoteUnlocked"
                        && parameters.Length >= 1
                        && parameters[0].ParameterType == unlockable)
                    {
                        isEmoteUnlocked = method;
                    }
                    else if (method.Name == "UnlockEmoteLocal"
                        && parameters.Length >= 1
                        && parameters[0].ParameterType == unlockable)
                    {
                        unlockEmoteLocal = method;
                    }
                }

                available = allUnlockableEmotes != null
                    && emoteName != null
                    && isEmoteUnlocked != null
                    && unlockEmoteLocal != null;
                if (!available)
                    MarkUnavailable();
            }
            catch
            {
                MarkUnavailable();
            }
        }

        internal static List<TooManyEmotesEntry> GetAllPurchasableEmotes()
        {
            EnsureResolved();
            if (!available) return null;

            var result = new List<TooManyEmotesEntry>();
            try
            {
                if (allUnlockableEmotes.GetValue(null) is IEnumerable list)
                {
                    foreach (object emote in list)
                    {
                        if (emote == null) continue;

                        bool canPurchase = purchasable == null || (bool)purchasable.GetValue(emote);
                        bool isComplementary = complementary != null && (bool)complementary.GetValue(emote);
                        if (!canPurchase || isComplementary) continue;

                        string stableId = emoteName.GetValue(emote) as string ?? string.Empty;
                        string label = displayName?.GetValue(emote) as string;
                        if (string.IsNullOrEmpty(label)) label = stableId;
                        if (string.IsNullOrEmpty(stableId)) continue;

                        int tier = rarity != null ? (int)(rarity.GetValue(emote) ?? 0) : 0;
                        string tierName = rarityText?.GetValue(emote, null) as string;
                        if (string.IsNullOrEmpty(tierName)) tierName = "Tier" + tier;

                        result.Add(new TooManyEmotesEntry
                        {
                            emoteName = stableId,
                            displayName = label,
                            obj = emote,
                            rarity = tier,
                            rarityName = tierName,
                            rarityColor = ResolveTierColor(emote, tier),
                            icon = ReadOptionalIcon(emote),
                        });
                    }
                }
            }
            catch
            {
                MarkUnavailable();
                return null;
            }
            return result;
        }

        internal static TooManyEmotesEntry FindPurchasableEmote(string stableId)
        {
            if (string.IsNullOrWhiteSpace(stableId)) return null;
            List<TooManyEmotesEntry> emotes = GetAllPurchasableEmotes();
            if (emotes == null) return null;
            for (int i = 0; i < emotes.Count; i++)
            {
                if (string.Equals(emotes[i].emoteName, stableId, StringComparison.OrdinalIgnoreCase))
                    return emotes[i];
            }
            return null;
        }

        internal static bool IsEmoteUnlocked(object emote)
        {
            EnsureResolved();
            if (!available || emote == null) return false;
            try
            {
                return (bool)isEmoteUnlocked.Invoke(null, new[] { emote, "" });
            }
            catch
            {
                MarkUnavailable();
                return false;
            }
        }

        internal static bool IsEmoteUnlocked(string stableId)
        {
            return IsEmoteUnlocked(FindPurchasableEmote(stableId)?.obj);
        }

        internal static string GetEmoteName(object emote)
        {
            EnsureResolved();
            if (!available || emote == null) return string.Empty;
            try { return emoteName.GetValue(emote) as string ?? string.Empty; }
            catch { return string.Empty; }
        }

        internal static bool TryUnlockEmoteLocal(object emote)
        {
            EnsureResolved();
            if (!available || emote == null) return false;
            try
            {
                unlockEmoteLocal.Invoke(null, new[] { emote, (object)true, "" });
                return true;
            }
            catch
            {
                MarkUnavailable();
                return false;
            }
        }

        internal static bool TryUnlockEmoteLocal(string stableId)
        {
            return TryUnlockEmoteLocal(FindPurchasableEmote(stableId)?.obj);
        }

        internal static Color GetTierColorForRarity(int tier)
        {
            if (TierColors.TryGetValue(tier, out Color cached)) return cached;
            return tier >= 0 && tier < FallbackTierColors.Length
                ? FallbackTierColors[tier]
                : Color.white;
        }

        private static Color ResolveTierColor(object emote, int tier)
        {
            if (TierColors.TryGetValue(tier, out Color cached)) return cached;
            Color resolvedColor = Color.white;
            bool found = false;
            try
            {
                if (nameColor != null && emote != null)
                {
                    string hex = nameColor.GetValue(emote, null) as string;
                    if (!string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString(hex, out Color parsed))
                    {
                        resolvedColor = parsed;
                        found = true;
                    }
                }
                if (!found && rarityColorCodes?.GetValue(null) is string[] codes
                    && tier >= 0 && tier < codes.Length
                    && ColorUtility.TryParseHtmlString(codes[tier], out Color tierColor))
                {
                    resolvedColor = tierColor;
                    found = true;
                }
            }
            catch
            {
                found = false;
            }
            if (!found && tier >= 0 && tier < FallbackTierColors.Length)
                resolvedColor = FallbackTierColors[tier];
            TierColors[tier] = resolvedColor;
            return resolvedColor;
        }

        private static object ReadOptionalIcon(object emote)
        {
            if (emote == null) return null;
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            Type type = emote.GetType();
            for (int i = 0; i < IconMemberNames.Length; i++)
            {
                try
                {
                    object value = type.GetField(IconMemberNames[i], Flags)?.GetValue(emote)
                        ?? type.GetProperty(IconMemberNames[i], Flags)?.GetValue(emote, null);
                    if (value is Sprite || value is Texture)
                        return value;
                }
                catch { }
            }
            return null;
        }

        private static void MarkUnavailable()
        {
            available = false;
            if (unavailableWarningLogged) return;
            if (Plugin.CustomLogger == null) return;
            unavailableWarningLogged = true;
            Plugin.CustomLogger?.LogWarning(
                "[Y4NGZ] TooManyEmotes optional integration unavailable; emotes are disabled.");
        }
    }
}
