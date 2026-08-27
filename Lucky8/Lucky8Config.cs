using BepInEx.Configuration;
using UnityEngine;
using Y4NGZUpgrades.Config;

namespace Y4NGZUpgrades.Lucky8
{
    internal sealed class Lucky8Config
    {
        internal readonly ConfigEntry<bool> Enabled;
        internal readonly ConfigEntry<float> ChancePerConstellation;
        internal readonly ConfigEntry<int> TokensPerSpin;
        internal readonly ConfigEntry<int> SpinsPerMachine;
        internal readonly ConfigEntry<float> WheelDurationSeconds;
        internal readonly ConfigEntry<float> ButtonHoldSeconds;
        internal readonly ConfigEntry<float> ButtonPressSeconds;
        internal readonly ConfigEntry<float> MinEntranceDistance;
        internal readonly ConfigEntry<string> WeaponPool;
        internal readonly ConfigEntry<string> AmmoPool;
        internal readonly ConfigEntry<string> SuitPool;
        internal readonly ConfigEntry<string> CosmeticPool;
        internal readonly ConfigEntry<string> EmotePool;
        internal readonly ConfigEntry<int> CommonWeight;
        internal readonly ConfigEntry<int> UncommonWeight;
        internal readonly ConfigEntry<int> RareWeight;
        internal readonly ConfigEntry<int> LegendaryWeight;
        internal readonly ConfigEntry<bool> DebugLogging;

        internal Lucky8Config(Y4NGZConfigScope config)
        {
            Enabled = Bind(config, "General", "Enabled", true,
                "Spawn the LUCKY-8 facility token prize dispenser.", "Enabled");
            ChancePerConstellation = Bind(config, "General", "Chance Per Constellation", 0.35f,
                "Daily chance that each constellation assigns LUCKY-8 to one eligible moon (0-1).", "Chance Per Constellation");
            MinEntranceDistance = Bind(config, "General", "Minimum Entrance Distance", 24f,
                "Minimum facility path distance from an entrance.", "Minimum Entrance Distance");

            TokensPerSpin = Bind(config, "Spin Rules", "Token Cost Per Spin", 3,
                "Upgrade tokens reserved and spent for an accepted spin.", "Tokens Per Spin");
            SpinsPerMachine = Bind(config, "Spin Rules", "Spins Per Machine", 3,
                "Maximum accepted spins during one facility visit.", "Spins Per Machine");
            WheelDurationSeconds = Bind(config, "Spin Rules", "Wheel Duration Seconds", 7.5f,
                "Time the clockwise selector runs before stopping.", "Wheel Duration Seconds");
            ButtonHoldSeconds = Bind(config, "Spin Rules", "Button Hold Seconds", 0.75f,
                "Interaction hold time on the centre button.", "Button Hold Seconds");
            ButtonPressSeconds = Bind(config, "Spin Rules", "Button Press Seconds", 1.0f,
                "First-person button press and input-lock duration.", "Button Press Seconds");

            WeaponPool = Bind(config, "Reward Pools", "Weapon Pool", "*|Common;revolver|Uncommon;flame_thrower|Rare",
                "Semicolon-separated stableId|rarity entries. '*' includes all registered Y4NGZ weapons.", "Weapon Pool");
            AmmoPool = Bind(config, "Reward Pools", "Ammo Pool", "*|Common",
                "Semicolon-separated stableId|rarity entries. '*' includes all registered Y4NGZ ammo pickups.", "Ammo Pool");
            SuitPool = Bind(config, "Reward Pools", "Suit Pool", "*|Uncommon",
                "Semicolon-separated stableId|rarity entries. '*' includes all non-default suits.", "Suit Pool");
            CosmeticPool = Bind(config, "Reward Pools", "Cosmetic Pool", "*|Uncommon",
                "Semicolon-separated stableId|rarity entries. '*' includes all MoreCompany cosmetics.", "Cosmetic Pool");
            EmotePool = Bind(config, "Reward Pools", "Emote Pool", "*",
                "Semicolon-separated stableId|rarity entries. '*' includes all eligible unowned TooManyEmotes emotes.", "Emote Pool");

            CommonWeight = Bind(config, "Rarity Weights", "Common", 100,
                "Relative win weight for Common rewards.", "Common Win Weight");
            UncommonWeight = Bind(config, "Rarity Weights", "Uncommon", 50,
                "Relative win weight for Uncommon rewards.", "Uncommon Win Weight");
            RareWeight = Bind(config, "Rarity Weights", "Rare", 20,
                "Relative win weight for Rare rewards.", "Rare Win Weight");
            LegendaryWeight = Bind(config, "Rarity Weights", "Legendary", 5,
                "Relative win weight for Legendary rewards.", "Legendary Win Weight");
            DebugLogging = Bind(config, "Troubleshooting", "Debug Logging", false,
                "Log assignment, lineup, transaction, and payout diagnostics.", "Debug Logging");
        }

        private static ConfigEntry<T> Bind<T>(
            Y4NGZConfigScope config,
            string section,
            string key,
            T defaultValue,
            string description,
            string legacyKey)
        {
            return Y4NGZConfigFiles.BindMigrated(
                config,
                section,
                key,
                defaultValue,
                description,
                new LegacyConfigKey("LUCKY-8", legacyKey));
        }

        // TEMP: the LUCKY-8 vending machine is switched off for the 2026-08-06 release-prep pass
        // (Lawson). Nothing is deleted — assets, prefabs, reward catalog, netcode and the "Enabled"
        // config entry all stay exactly as they were. This one constant forces the effective gate
        // false so no machine is assigned, spawned, or announced to clients.
        // TO RE-ENABLE: set TemporarilyDisabled to false. That is the whole change.
        internal const bool TemporarilyDisabled = true;

        /// <summary>The effective on/off state. Every runtime gate reads THIS, never
        /// <see cref="Enabled"/> directly, so the TEMP switch above cannot be half-applied.</summary>
        internal bool FeatureEnabled => !TemporarilyDisabled && Enabled.Value;

        internal float AssignmentChance => Mathf.Clamp01(ChancePerConstellation.Value);
        internal int SpinCost => Mathf.Clamp(TokensPerSpin.Value, 1, 99);
        internal int SpinLimit => Mathf.Clamp(SpinsPerMachine.Value, 1, 8);
        internal float WheelDuration => Mathf.Clamp(WheelDurationSeconds.Value, 2f, 20f);
        internal float HoldDuration => Mathf.Clamp(ButtonHoldSeconds.Value, 0.1f, 3f);
        internal float PressDuration => Mathf.Clamp(ButtonPressSeconds.Value, 0.2f, 3f);

        internal int WeightFor(Lucky8Rarity rarity)
        {
            switch (rarity)
            {
                case Lucky8Rarity.Legendary: return Mathf.Max(1, LegendaryWeight.Value);
                case Lucky8Rarity.Rare: return Mathf.Max(1, RareWeight.Value);
                case Lucky8Rarity.Uncommon: return Mathf.Max(1, UncommonWeight.Value);
                default: return Mathf.Max(1, CommonWeight.Value);
            }
        }
    }
}
