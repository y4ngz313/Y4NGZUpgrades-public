using UnityEngine;
using Y4NGZUpgrades.Config;

namespace Y4NGZUpgrades
{
    public readonly struct RankDefinition
    {
        public readonly string Name;
        public readonly int StartXp;
        public readonly int EndXp;

        public RankDefinition(string name, int startXp, int endXp)
        {
            Name = name;
            StartXp = startXp;
            EndXp = endXp;
        }
    }

    public static class RankCatalog
    {
        public const string DefaultXpRequiredPerLevel = "150,150,150,150,150,180,200,230,260,290,330,370,420,470,540,600,680,770,870,980,1110,1250,1410,1590,1800,2000,2000,2000,2000,2000";
        public static int MaxRankXp { get; private set; } = 25100;

        private static readonly string[] DefaultRankNames =
        {
            "Applicant", "Probationary Intern", "Dock Intern", "Scrap Trainee", "Field Trainee",
            "Junior Runner", "Scrap Runner", "Route Runner", "Quota Runner", "Salvage Hand",
            "Certified Hand", "Facility Scout", "Hazard Scout", "Quota Scout", "Recovery Tech",
            "Senior Recovery Tech", "Ship Operator", "Route Operator", "Field Operator", "Quota Operator",
            "Crew Lead", "Salvage Lead", "Hazard Lead", "Route Supervisor", "Quota Supervisor",
            "Site Manager", "Operations Manager", "Sector Manager", "Regional Asset", "Executive Asset",
            "Company Asset"
        };
        /// <summary>
        /// The configured curve, and the single source every rank fact is derived from:
        /// <see cref="Ranks"/>, <see cref="MaxRankXp"/>, the rank-index walk and the band widths
        /// are all produced from this one array by <see cref="ApplyCurve"/>. Nothing else assigns
        /// it, so no two of those can describe different curves.
        /// </summary>
        private static int[] _curve;
        private static string[] _rankNames;
        private static RankDefinition[] Ranks;
        private static int _tokensRanks1To5 = 5;
        private static int _tokensRanks6To10 = 8;
        private static int _tokensRanks11To19 = 12;
        private static int _tokensRanks20To30 = 15;

        static RankCatalog()
        {
            ProgressionEconomyMath.TryParsePositiveIntCsv(
                DefaultXpRequiredPerLevel,
                DefaultRankNames.Length - 1,
                out int[] widths);
            _rankNames = (string[])DefaultRankNames.Clone();
            ApplyCurve(widths);
        }

        internal static int DefaultRankCount => DefaultRankNames.Length;
        public static int Count => Ranks.Length;
        public static int EarnableRankCount => Ranks.Length - 1;

        internal static void Configure(ProgressionSettings config)
        {
            string[] names = config?.GetRankNames();
            int[] widths = config?.GetRankWidths();
            if (names == null || names.Length != DefaultRankNames.Length)
                names = (string[])DefaultRankNames.Clone();
            if (widths == null || widths.Length != DefaultRankNames.Length - 1)
                ProgressionEconomyMath.TryParsePositiveIntCsv(
                    DefaultXpRequiredPerLevel, DefaultRankNames.Length - 1, out widths);

            _rankNames = names;
            ApplyCurve(widths);
            _tokensRanks1To5 = Mathf.Max(0, config?.TokensRanks1To5?.Value ?? 5);
            _tokensRanks6To10 = Mathf.Max(0, config?.TokensRanks6To10?.Value ?? 8);
            _tokensRanks11To19 = Mathf.Max(0, config?.TokensRanks11To19?.Value ?? 12);
            _tokensRanks20To30 = Mathf.Max(0, config?.TokensRanks20To30?.Value ?? 15);
        }

        internal static string GetDefaultRankName(int index)
        {
            return DefaultRankNames[Mathf.Clamp(index, 0, DefaultRankNames.Length - 1)];
        }

        internal static int GetDefaultRankWidth(int index)
        {
            ProgressionEconomyMath.TryParsePositiveIntCsv(
                DefaultXpRequiredPerLevel, DefaultRankNames.Length - 1, out int[] widths);
            return widths[Mathf.Clamp(index, 0, widths.Length - 1)];
        }

        public static int ClampXp(int xp)
        {
            return Mathf.Clamp(xp, 0, MaxRankXp);
        }

        public static RankDefinition GetRank(int xp)
        {
            return Ranks[GetRankIndex(xp)];
        }

        public static RankDefinition GetRankByIndex(int index)
        {
            return Ranks[Mathf.Clamp(index, 0, Ranks.Length - 1)];
        }

        public static int GetRankIndex(int xp)
        {
            return ProgressionEconomyMath.GetRankIndex(_curve, ClampXp(xp));
        }

        public static float GetProgress(int xp)
        {
            xp = ClampXp(xp);
            int rankIndex = GetRankIndex(xp);
            if (rankIndex >= Ranks.Length - 1)
                return 1f;

            RankDefinition rank = Ranks[rankIndex];
            return Mathf.Clamp01((xp - rank.StartXp) / (float)Mathf.Max(1, rank.EndXp - rank.StartXp));
        }

        /// <summary>
        /// XP width of the band at <paramref name="rankIndex"/>, from the same curve
        /// <see cref="Ranks"/> is built from. Index 0 is rank 1. The final catalog rank is a
        /// one-XP terminal marker rather than an earnable band, so it - and anything past it -
        /// reads the last earnable rank's width instead.
        /// </summary>
        public static int GetRankWidth(int rankIndex)
        {
            return ProgressionEconomyMath.GetRankWidth(_curve, rankIndex);
        }

        public static int GetTokenGrantForRankIndex(int rankIndex)
        {
            if (rankIndex <= 0)
                return 0;
            if (rankIndex <= 5)
                return _tokensRanks1To5;
            return ProgressionEconomyMath.GetTokenGrantForRankIndex(
                rankIndex,
                EarnableRankCount,
                _tokensRanks1To5,
                _tokensRanks6To10,
                _tokensRanks11To19,
                _tokensRanks20To30);
        }

        public static int GetTokenGrantForRankRange(int firstRankIndex, int finalRankIndex)
        {
            return ProgressionEconomyMath.GetTokenGrantForRankRange(
                firstRankIndex,
                finalRankIndex,
                EarnableRankCount,
                _tokensRanks1To5,
                _tokensRanks6To10,
                _tokensRanks11To19,
                _tokensRanks20To30);
        }

        public static int GetVanillaRankIndex(int xp)
        {
            xp = ClampXp(xp);
            if (xp >= GetRankByIndex(25).StartXp) return 4;
            if (xp >= GetRankByIndex(19).StartXp) return 3;
            if (xp >= GetRankByIndex(10).StartXp) return 2;
            if (xp >= GetRankByIndex(5).StartXp) return 1;
            return 0;
        }

        /// <summary>
        /// The one place the curve is installed. Everything derived from it is rebuilt here in the
        /// same call, so a re-Configure cannot leave the bands and the width/index lookups on
        /// different curves.
        /// </summary>
        private static void ApplyCurve(int[] widths)
        {
            _curve = widths;
            Ranks = BuildRanks(widths);
            MaxRankXp = Ranks[Ranks.Length - 1].StartXp;
        }

        private static RankDefinition[] BuildRanks(int[] widths)
        {
            var result = new RankDefinition[_rankNames.Length];
            int start = 0;
            for (int i = 0; i < widths.Length; i++)
            {
                int end = start + Mathf.Max(1, widths[i]);
                result[i] = new RankDefinition(_rankNames[i], start, end);
                start = end;
            }
            result[result.Length - 1] = new RankDefinition(_rankNames[_rankNames.Length - 1], start, start + 1);
            return result;
        }
    }
}
