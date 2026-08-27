using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Config
{
    /// <summary>
    /// Human-readable progression and XP settings grouped into the Progression and XP Sources
    /// sections of Y4NGZUpgrades.cfg. Every award has an explicit enable switch; zero remains a
    /// supported amount, but is no longer the only way to disable a source.
    /// </summary>
    internal sealed class ProgressionSettings
    {
        private readonly Dictionary<string, ConfigEntry<float>> _contractCompletedMultipliers =
            new Dictionary<string, ConfigEntry<float>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ConfigEntry<bool>> _contractSourceEnabled =
            new Dictionary<string, ConfigEntry<bool>>(StringComparer.OrdinalIgnoreCase);

        internal ConfigEntry<bool> Enabled;
        internal ConfigEntry<bool> DebugLogging;
        internal ConfigEntry<string>[] RankNames;
        internal ConfigEntry<int>[] XpRequiredForNextRank;
        internal ConfigEntry<int> TokensRanks1To5;
        internal ConfigEntry<int> TokensRanks6To10;
        internal ConfigEntry<int> TokensRanks11To19;
        internal ConfigEntry<int> TokensRanks20To30;

        internal ConfigEntry<bool> ScrapPickupEnabled;
        internal ConfigEntry<float> ScrapPickupXpPerValue;
        internal ConfigEntry<bool> ScrapDeliveredEnabled;
        internal ConfigEntry<float> ScrapDeliveredXpPerValue;
        internal ConfigEntry<bool> MonsterKillEnabled;
        internal ConfigEntry<int> MonsterKillXpPerPower;
        internal ConfigEntry<int> MonsterKillMinXp;
        internal ConfigEntry<int> MonsterKillMaxXp;
        internal ConfigEntry<float> KillAttributionWindowSeconds;
        internal ConfigEntry<bool> SurvivalTimeEnabled;
        internal ConfigEntry<int> SurvivalXpPerMinute;
        internal ConfigEntry<int> SurvivalMaxMinutesPerRound;
        internal ConfigEntry<bool> SurvivalExtractBonusEnabled;
        internal ConfigEntry<int> SurvivalExtractBonus;
        internal ConfigEntry<bool> BodyRetrievalEnabled;
        internal ConfigEntry<int> BodyRetrievalXp;
        internal ConfigEntry<bool> MainframeHackEnabled;
        internal ConfigEntry<int> MainframeHackXp;
        internal ConfigEntry<bool> ClutchExtractEnabled;
        internal ConfigEntry<int> ClutchExtractXp;

        internal ConfigEntry<bool> ContractXpEnabled;
        internal ConfigEntry<int> WhistleblowerDiscoveryXp;
        internal ConfigEntry<int> WhistleblowerKillXp;
        internal ConfigEntry<int> BlacksiteDrillPlacedXp;
        internal ConfigEntry<int> SurveyDronePlacedXp;
        internal ConfigEntry<int> BlackoutBreakerRestoredXp;
        internal ConfigEntry<int> ShadowRaidDrillCompletedXp;
        internal ConfigEntry<int> PestControlCompletedXp;
        internal ConfigEntry<int> WasteDisposalCompletedXp;
        internal ConfigEntry<int> ContainmentWaveSurvivedXp;
        internal ConfigEntry<int> ShadowRaidHardDriveXp;
        internal ConfigEntry<int> WasteDisposalRepairedXp;
        internal ConfigEntry<int> WasteDisposalRepairMaxPerRound;
        internal ConfigEntry<int> PestControlCaptureXp;
        internal ConfigEntry<int> PestControlCaptureMaxPerRound;
        internal ConfigEntry<float> ContractCompletionRankWidthFraction;
        internal ConfigEntry<float> ContractCompletedDefaultMultiplier;
        internal ConfigEntry<int> ContractFailedXp;
        internal ConfigEntry<int> DefuseCorrectWireXp;
        internal ConfigEntry<int> DefuseCorrectCodeXp;
        internal ConfigEntry<float> PayloadPilotXpPerSecond;
        internal ConfigEntry<int> PayloadPilotMaxXp;
        internal ConfigEntry<float> RiskMultiplierD;
        internal ConfigEntry<float> RiskMultiplierC;
        internal ConfigEntry<float> RiskMultiplierB;
        internal ConfigEntry<float> RiskMultiplierA;
        internal ConfigEntry<float> RiskMultiplierS;
        internal PerUpgradeConfig UpgradePrices;

        internal ProgressionSettings(Y4NGZConfigScope progression, Y4NGZConfigScope xpSources)
        {
            Enabled = Bind(
                progression, "General", "Progression Enabled", true,
                "Enable Y4NGZ ranks, XP, promotion tokens, and upgrade purchases.",
                "Progression", "Enabled");
            DebugLogging = Bind(
                progression, "General", "Debug Logging", false,
                "Log detailed XP accounting and config migration information.",
                "Progression", "DebugLogging");

            BindRanks(progression);
            TokensRanks1To5 = BindTokenBand(progression, "Ranks 1-5", 5, "TokensRanks1To5");
            TokensRanks6To10 = BindTokenBand(progression, "Ranks 6-10", 8, "TokensRanks6To10");
            TokensRanks11To19 = BindTokenBand(progression, "Ranks 11-19", 12, "TokensRanks11To19");
            TokensRanks20To30 = BindTokenBand(progression, "Ranks 20-30", 15, "TokensRanks20To30");

            ScrapPickupEnabled = BindEnabled(xpSources, "Scrap Discovery", "Scrap discovery XP");
            ScrapPickupXpPerValue = Bind(
                xpSources, "Scrap Discovery", "XP Per Scrap Value", 0.05f,
                Range("XP awarded per item value the first time the local player picks up scrap.", 0f, 10f),
                "Progression.XP", "ScrapPickupXpPerValue");

            ScrapDeliveredEnabled = BindEnabled(xpSources, "Scrap Delivery", "Delivered scrap XP");
            ScrapDeliveredXpPerValue = Bind(
                xpSources, "Scrap Delivery", "XP Per Delivered Value", 0.25f,
                Range("XP awarded per delivered scrap value to the last player who carried it aboard.", 0f, 10f),
                "Progression.XP", "ScrapDeliveredXpPerValue");

            MonsterKillEnabled = BindEnabled(xpSources, "Monster Kills", "Monster kill XP");
            MonsterKillXpPerPower = Bind(
                xpSources, "Monster Kills", "XP Per Power Level", 12,
                Range("XP per EnemyType power level for an attributed kill.", 0, 1000),
                "Progression.XP", "MonsterKillXpPerPower");
            MonsterKillMinXp = Bind(
                xpSources, "Monster Kills", "Minimum XP", 8,
                Range("Minimum XP paid for an attributed monster kill.", 0, 10000),
                "Progression.XP", "MonsterKillMinXp");
            MonsterKillMaxXp = Bind(
                xpSources, "Monster Kills", "Maximum XP", 80,
                Range("Maximum XP paid for an attributed monster kill.", 0, 10000),
                "Progression.XP", "MonsterKillMaxXp");
            KillAttributionWindowSeconds = Bind(
                xpSources, "Monster Kills", "Attribution Window Seconds", 30f,
                Range("Seconds after your last hit that an enemy death remains attributable to you.", 1f, 300f),
                "22 - XP - Combat", "KillAttributionWindowSeconds");

            SurvivalTimeEnabled = BindEnabled(xpSources, "Survival", "Time-survived XP", "Time XP Enabled");
            SurvivalXpPerMinute = Bind(
                xpSources, "Survival", "XP Per Minute", 4,
                Range("XP paid per complete minute survived during a landed moon run.", 0, 1000),
                "Progression.XP", "SurvivalXpPerMinute");
            SurvivalMaxMinutesPerRound = Bind(
                xpSources, "Survival", "Maximum Paid Minutes", 15,
                Range("Maximum survival minutes that can pay XP in one round.", 1, 240),
                "Progression.XP", "SurvivalMaxMinutesPerRound");
            SurvivalExtractBonusEnabled = BindEnabled(
                xpSources, "Survival", "Alive-at-extraction bonus XP", "Extraction Bonus Enabled");
            SurvivalExtractBonus = Bind(
                xpSources, "Survival", "Extraction Bonus XP", 15,
                Range("Bonus XP paid when the local player is alive as the ship leaves.", 0, 10000),
                "Progression.XP", "SurvivalExtractBonus");

            BodyRetrievalEnabled = BindEnabled(xpSources, "Body Retrieval", "Body retrieval XP");
            BodyRetrievalXp = Bind(
                xpSources, "Body Retrieval", "XP Per Body", 30,
                Range("Base XP paid for each eligible teammate body you carried aboard, before moon risk.", 0, 500),
                "Progression.XP", "BodyRetrievalXp");

            MainframeHackEnabled = BindEnabled(xpSources, "Mainframe Hacking", "Mainframe hacking XP");
            MainframeHackXp = Bind(
                xpSources, "Mainframe Hacking", "XP Per Mainframe", 25,
                Range("XP paid once per distinct LethalCCTV mainframe you successfully hack.", 0, 500),
                "Progression.XP", "MainframeHackXp");

            ClutchExtractEnabled = BindEnabled(xpSources, "Clutch Extraction", "Clutch extraction XP");
            ClutchExtractXp = Bind(
                xpSources, "Clutch Extraction", "Bonus XP", 30,
                Range("XP paid for extracting as the sole survivor of a multi-player facility entry.", 0, 500),
                "Progression.XP", "ClutchExtractXp");

            ContractXpEnabled = BindEnabled(xpSources, "Contract Work", "All Y4NGZCompany contract XP");
            BindContractSources(xpSources);
            BindRiskMultipliers(xpSources);

            RankCatalog.Configure(this);
            UpgradePrices = new PerUpgradeConfig();
        }

        internal string[] GetRankNames()
        {
            var names = new string[RankNames.Length];
            for (int i = 0; i < names.Length; i++)
            {
                string configured = RankNames[i]?.Value?.Trim();
                names[i] = string.IsNullOrWhiteSpace(configured)
                    ? RankCatalog.GetDefaultRankName(i)
                    : configured;
            }
            return names;
        }

        internal int[] GetRankWidths()
        {
            var widths = new int[XpRequiredForNextRank.Length];
            for (int i = 0; i < widths.Length; i++)
                widths[i] = Math.Max(1, XpRequiredForNextRank[i]?.Value ?? RankCatalog.GetDefaultRankWidth(i));
            return widths;
        }

        internal bool IsContractSourceEnabled(string eventKind)
        {
            if (ContractXpEnabled?.Value != true)
                return false;
            if (string.IsNullOrWhiteSpace(eventKind))
                return true;
            return !_contractSourceEnabled.TryGetValue(eventKind, out ConfigEntry<bool> entry)
                   || entry.Value;
        }

        internal float GetContractCompletedMultiplier(string contractType)
        {
            if (!string.IsNullOrWhiteSpace(contractType)
                && _contractCompletedMultipliers.TryGetValue(contractType.Trim(), out ConfigEntry<float> entry))
            {
                return entry.Value;
            }
            return ContractCompletedDefaultMultiplier.Value;
        }

        private void BindRanks(Y4NGZConfigScope progression)
        {
            int rankCount = RankCatalog.DefaultRankCount;
            RankNames = new ConfigEntry<string>[rankCount];
            XpRequiredForNextRank = new ConfigEntry<int>[rankCount - 1];

            int[] legacyWidths = null;
            if (Y4NGZConfigFiles.TryGetLegacy(
                    "10 - Progression Curve", "XpRequiredPerLevel", out string legacyCurve))
            {
                ProgressionEconomyMath.TryParsePositiveIntCsv(
                    legacyCurve,
                    rankCount - 1,
                    out legacyWidths);
            }

            for (int index = 0; index < rankCount; index++)
            {
                int number = index + 1;
                string rankKey = $"Rank {number:00}";
                RankNames[index] = Y4NGZConfigFiles.BindMigrated(
                    progression,
                    "Rank Names",
                    rankKey,
                    RankCatalog.GetDefaultRankName(index),
                    $"Display name for progression rank {number}.");

                if (index >= rankCount - 1)
                    continue;

                string widthKey = $"Rank {number:00} to Rank {number + 1:00}";
                bool targetDefined = Y4NGZConfigFiles.TargetAlreadyDefines(
                    progression, "XP Required Per Promotion", widthKey);
                ConfigEntry<int> width = Y4NGZConfigFiles.BindMigrated(
                    progression,
                    "XP Required Per Promotion",
                    widthKey,
                    RankCatalog.GetDefaultRankWidth(index),
                    new ConfigDescription(
                        $"XP required to advance from rank {number} to rank {number + 1}.",
                        new AcceptableValueRange<int>(1, 1000000)));
                if (!targetDefined && legacyWidths != null)
                    width.Value = legacyWidths[index];
                XpRequiredForNextRank[index] = width;
            }
        }

        private void BindContractSources(Y4NGZConfigScope config)
        {
            WhistleblowerDiscoveryXp = BindContractAward(
                config, "Whistleblower Sightings", "WhistleblowerSighted", 10,
                "XP Per Sighting", "Progression.Contracts", "WhistleblowerDiscoveryXp");
            WhistleblowerKillXp = BindContractAward(
                config, "Whistleblower Neutralized", "WhistleblowerNeutralized", 35,
                "XP", "Progression.Contracts", "WhistleblowerKillXp");
            BlacksiteDrillPlacedXp = BindContractAward(
                config, "Shadow Raid Drill Mounted", "ShadowRaidDrillAttached", 25,
                "XP Per Drill", "Progression.Contracts", "BlacksiteDrillPlacedXp");
            ShadowRaidDrillCompletedXp = BindContractAward(
                config, "Shadow Raid Container Opened", "ShadowRaidDrillCompleted", 15,
                "XP Per Container", "Progression.Contracts", "ShadowRaidDrillCompletedXp");
            ShadowRaidHardDriveXp = BindContractAward(
                config, "Shadow Raid Hard Drive", "ShadowRaidHardDriveRecovered", 15,
                "XP", "Progression.Contracts", "ShadowRaidHardDriveXp");
            SurveyDronePlacedXp = BindContractAward(
                config, "Survey Drone Placed", "SurveyDronePlaced", 12,
                "XP Per Drone", "Progression.Contracts", "SurveyDronePlacedXp");
            BlackoutBreakerRestoredXp = BindContractAward(
                config, "Blackout Power Restored", "BlackoutAuditRestored", 35,
                "XP", "Progression.Contracts", "BlackoutBreakerRestoredXp");
            ContainmentWaveSurvivedXp = BindContractAward(
                config, "Containment Wave Survived", "ContainmentBreachWaveSurvived", 10,
                "XP Per Wave", "Progression.Contracts", "ContainmentWaveSurvivedXp");
            PestControlCompletedXp = BindContractAward(
                config, "Pest Control Completed", "PestControlCompleted", 25,
                "XP", "Progression.Contracts", "PestControlCompletedXp");
            PestControlCaptureXp = BindContractAward(
                config, "Extra Pest Captures", "PestControlCapture", 10,
                "XP Per Capture", "Progression.Contracts", "PestControlCaptureXp");
            PestControlCaptureMaxPerRound = Bind(
                config, "Extra Pest Captures", "Maximum Paid Captures Per Round", 3,
                Range("Maximum repeat captures that pay one player in a round.", 0, 50),
                "Progression.Contracts", "PestControlCaptureMaxPerRound");
            WasteDisposalCompletedXp = BindContractAward(
                config, "Waste Disposal Completed", "WasteDisposalCompleted", 25,
                "XP", "Progression.Contracts", "WasteDisposalCompletedXp");
            WasteDisposalRepairedXp = BindContractAward(
                config, "Waste Disposal Repairs", "WasteDisposalRepaired", 8,
                "XP Per Repair", "Progression.Contracts", "WasteDisposalRepairedXp");
            WasteDisposalRepairMaxPerRound = Bind(
                config, "Waste Disposal Repairs", "Maximum Paid Repairs Per Round", 5,
                Range("Maximum incinerator repairs that pay one player in a round.", 0, 100),
                "Progression.Contracts", "WasteDisposalRepairMaxPerRound");

            DefuseCorrectWireXp = BindContractAward(
                config, "Defuse Correct Wire", "DefuseCorrectWire", 4,
                "XP Per Wire", "23 - XP - Contracts", "DefuseCorrectWireXp");
            DefuseCorrectCodeXp = BindContractAward(
                config, "Defuse Correct Code", "DefuseCorrectCode", 10,
                "XP", "23 - XP - Contracts", "DefuseCorrectCodeXp");
            PayloadPilotXpPerSecond = BindContractAwardFloat(
                config, "Payload Piloting", "PayloadPilotSeconds", 0.1f,
                "XP Per Second", "23 - XP - Contracts", "PayloadPilotXpPerSecond");
            PayloadPilotMaxXp = Bind(
                config, "Payload Piloting", "Maximum XP Per Round", 20,
                Range("Maximum base Payload pilot XP paid to one player before moon risk.", 0, 500),
                "23 - XP - Contracts", "PayloadPilotMaxXp");

            _contractSourceEnabled["ContractCompleted"] = BindEnabled(
                config, "Contract Completion", "Generic contract completion XP");
            ContractCompletionRankWidthFraction = Bind(
                config, "Contract Completion", "Rank Width Fraction", 0.20f,
                Range("Fraction of the completing player's current rank width paid before multipliers; 0.20 means 20%.", 0f, 1f),
                "Progression.Contracts.Completion", "ContractCompletionRankWidthFraction");
            ContractCompletedDefaultMultiplier = Bind(
                config, "Contract Completion", "Unknown Contract Multiplier", 1f,
                Range("Completion multiplier for contract types without an explicit entry.", 0f, 10f),
                "Progression.Contracts.Completion", "ContractCompletedMultiplier.Default");
            BindContractCompleted(config, "Whistleblower", 1f);
            BindContractCompleted(config, "BlackoutAudit", 1f);
            BindContractCompleted(config, "ShadowRaid", 1f);
            BindContractCompleted(config, "Survey", 1f);
            BindContractCompleted(config, "Defuse", 0f);
            BindContractCompleted(config, "ContainmentBreach", 0f);
            BindContractCompleted(config, "Payload", 0f);
            BindContractCompleted(config, "PestControl", 0f);
            BindContractCompleted(config, "WasteDisposal", 0f);

            _contractSourceEnabled["ContractFailed"] = BindEnabled(
                config, "Failed Contracts", "Failed-contract XP");
            ContractFailedXp = Bind(
                config, "Failed Contracts", "XP", 0,
                Range("XP paid for a failed contract. Zero by default.", 0, 500),
                "Progression.Contracts.Completion", "ContractFailedXp");
        }

        private ConfigEntry<int> BindContractAward(
            Y4NGZConfigScope config,
            string section,
            string eventKind,
            int defaultValue,
            string valueKey,
            string legacySection,
            string legacyKey)
        {
            _contractSourceEnabled[eventKind] = BindEnabled(config, section, section + " XP");
            return Bind(
                config, section, valueKey, defaultValue,
                Range("Base XP before the moon-risk multiplier.", 0, 10000),
                legacySection, legacyKey);
        }

        private ConfigEntry<float> BindContractAwardFloat(
            Y4NGZConfigScope config,
            string section,
            string eventKind,
            float defaultValue,
            string valueKey,
            string legacySection,
            string legacyKey)
        {
            _contractSourceEnabled[eventKind] = BindEnabled(config, section, section + " XP");
            return Bind(
                config, section, valueKey, defaultValue,
                Range("Base XP before the moon-risk multiplier.", 0f, 100f),
                legacySection, legacyKey);
        }

        private void BindContractCompleted(Y4NGZConfigScope config, string contractType, float defaultValue)
        {
            _contractCompletedMultipliers[contractType] = Bind(
                config,
                "Contract Completion Multipliers",
                contractType,
                defaultValue,
                Range($"Multiplier for {contractType} contract completion XP.", 0f, 10f),
                "Progression.Contracts.Completion",
                "ContractCompletedMultiplier." + contractType);
        }

        private void BindRiskMultipliers(Y4NGZConfigScope config)
        {
            RiskMultiplierD = BindRisk(config, "Risk D", 0.5f, "RiskMultiplierD");
            RiskMultiplierC = BindRisk(config, "Risk C", 1f, "RiskMultiplierC");
            RiskMultiplierB = BindRisk(config, "Risk B", 1.25f, "RiskMultiplierB");
            RiskMultiplierA = BindRisk(config, "Risk A", 1.5f, "RiskMultiplierA");
            RiskMultiplierS = BindRisk(config, "Risk S", 2f, "RiskMultiplierS");
        }

        private static ConfigEntry<float> BindRisk(
            Y4NGZConfigScope config,
            string key,
            float defaultValue,
            string legacyKey)
        {
            return Bind(
                config,
                "Contract and Body Risk Multipliers",
                key,
                defaultValue,
                Range("Multiplier applied to contract and body-retrieval XP at this moon risk.", 0f, 10f),
                "23 - XP - Contracts",
                legacyKey);
        }

        private static ConfigEntry<int> BindTokenBand(
            Y4NGZConfigScope progression,
            string key,
            int defaultValue,
            string legacyKey)
        {
            return Bind(
                progression,
                "Promotion Token Rewards",
                key,
                defaultValue,
                Range("Upgrade tokens granted for each promotion in this rank band.", 0, 100),
                "11 - Token Curve",
                legacyKey);
        }

        private static ConfigEntry<bool> BindEnabled(
            Y4NGZConfigScope config,
            string section,
            string sourceName,
            string key = "Enabled")
        {
            return Y4NGZConfigFiles.BindMigrated(
                config,
                section,
                key,
                true,
                $"Enable {sourceName}.");
        }

        private static ConfigEntry<T> Bind<T>(
            Y4NGZConfigScope config,
            string section,
            string key,
            T defaultValue,
            string description,
            string legacySection,
            string legacyKey)
        {
            return Y4NGZConfigFiles.BindMigrated(
                config, section, key, defaultValue, description,
                new LegacyConfigKey(legacySection, legacyKey));
        }

        private static ConfigEntry<T> Bind<T>(
            Y4NGZConfigScope config,
            string section,
            string key,
            T defaultValue,
            ConfigDescription description,
            string legacySection,
            string legacyKey)
        {
            return Y4NGZConfigFiles.BindMigrated(
                config, section, key, defaultValue, description,
                new LegacyConfigKey(legacySection, legacyKey));
        }

        private static ConfigDescription Range(string description, int min, int max)
        {
            return new ConfigDescription(description, new AcceptableValueRange<int>(min, max));
        }

        private static ConfigDescription Range(string description, float min, float max)
        {
            return new ConfigDescription(description, new AcceptableValueRange<float>(min, max));
        }
    }
}
