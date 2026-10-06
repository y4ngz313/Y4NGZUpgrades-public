using System;
using System.Collections.Generic;

namespace Y4NGZUpgrades
{
    /// <summary>
    /// #392: maps the contract event kinds this plugin pays to the performance-report line
    /// Y4NGZCompany writes for the same act, and divides the round's risk-scaled contract
    /// bucket back across those lines. Company decorates its own line with the result
    /// ("> Restored the breaker box (15 XP)"); whatever maps to no Company line stays on this
    /// plugin's contract note. Unity-free so <c>verification/ProgressionChecks</c> can pin
    /// the table and the division.
    ///
    /// Keys mirror <c>Y4NGZCompany.Contracts._Shared.PerformanceReportXpAmounts.LineKeys</c>:
    /// a sub-objective line is <c>subobjective.</c> plus the lower-cased kind name, and the
    /// Payload pilot line has its own key. The two crew-wide outcomes reach no line - the
    /// report's DIRECTIVE cell already states them - and neither does the unnamed bucket the
    /// public <c>ProgressionApi.AwardContractXp</c> surface pays into.
    /// </summary>
    public static class ProgressionReportLineKeys
    {
        public const string SubObjectivePrefix = "subobjective.";
        public const string PayloadPilotLineKey = "payload.pilot";

        /// <summary>
        /// #1201 (Company #1201, Lawson 2026-09-22): prefix of a crew award - XP every player in
        /// the round received for the same occurrence. It marks a contract bucket
        /// (<c>crew.SurveyDronePlaced</c>), the amount key it maps to
        /// (<c>crew.subobjective.surveydroneplaced</c>) and an outcome source key
        /// (<c>crew.contract.ContractCompleted</c>). Mirrors Company's
        /// <c>PerformanceReportXpAmounts.LineKeys.CrewWidePrefix</c>: Company hides these from
        /// its lines and remainder, and so does this plugin's standalone composer. Enum kind
        /// names never start with it, so a bucket reads back unambiguously.
        /// </summary>
        public const string CrewWidePrefix = "crew.";

        /// <summary>
        /// The per-round contract bucket one award lands in: the kind, or the kind under
        /// <see cref="CrewWidePrefix"/> when the occurrence was a crew award. The unnamed API
        /// bucket is never crew-wide.
        /// </summary>
        public static string ContractBucket(string eventKind, bool crewWide)
        {
            string kind = eventKind == null ? string.Empty : eventKind.Trim();
            return crewWide && kind.Length > 0 ? CrewWidePrefix + kind : kind;
        }

        public static bool IsCrewWideKey(string key)
        {
            return key != null && key.StartsWith(CrewWidePrefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// Maps a kind, or a contract bucket from <see cref="ContractBucket"/>, to the Company
        /// line key. A crew bucket keeps its prefix, so its share is recorded under a key that
        /// decorates no line.
        /// </summary>
        public static string MapEventKindToLineKey(string eventKind)
        {
            if (string.IsNullOrWhiteSpace(eventKind))
                return null;

            string kind = eventKind.Trim();
            if (IsCrewWideKey(kind))
            {
                string line = MapEventKindToLineKey(kind.Substring(CrewWidePrefix.Length));
                return line == null ? null : CrewWidePrefix + line;
            }

            switch (kind)
            {
                case "ContractCompleted":
                case "ContractFailed":
                    return null;
                case "PayloadPilotSeconds":
                    return PayloadPilotLineKey;
                default:
                    return SubObjectivePrefix + kind.ToLowerInvariant();
            }
        }

        /// <summary>
        /// The report source key for a bucket no Company line carries: <c>contract.&lt;kind&gt;</c>,
        /// or <c>crew.contract.&lt;kind&gt;</c> for a crew award.
        /// </summary>
        public static string ContractSourceKey(string bucket)
        {
            string kind = bucket ?? string.Empty;
            return IsCrewWideKey(kind)
                ? CrewWidePrefix + "contract." + kind.Substring(CrewWidePrefix.Length)
                : "contract." + kind;
        }

        /// <summary>
        /// Divides <paramref name="scaledTotal"/> across EVERY kind that fed the bucket
        /// (<see cref="ProgressionEconomyMath.DistributeScaledXp"/>, largest remainder, in
        /// first-award order so the leftover unit is deterministic), then merges the mapped
        /// kinds by line key. Unmapped kinds take part in the division but are not returned:
        /// their share is the remainder the caller reports on its own line.
        /// </summary>
        public static IReadOnlyList<KeyValuePair<string, int>> BuildContractLineAmounts(
            IReadOnlyList<string> kindOrder,
            IReadOnlyDictionary<string, int> rawByKind,
            int scaledTotal)
        {
            if (kindOrder == null || kindOrder.Count == 0 || rawByKind == null || scaledTotal <= 0)
                return Array.Empty<KeyValuePair<string, int>>();

            var raw = new int[kindOrder.Count];
            for (int i = 0; i < kindOrder.Count; i++)
                rawByKind.TryGetValue(kindOrder[i] ?? string.Empty, out raw[i]);

            int[] scaled = ProgressionEconomyMath.DistributeScaledXp(raw, scaledTotal);

            var order = new List<string>();
            var byLine = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < kindOrder.Count; i++)
            {
                if (scaled[i] <= 0)
                    continue;

                string lineKey = MapEventKindToLineKey(kindOrder[i]);
                if (string.IsNullOrEmpty(lineKey))
                    continue;

                if (!byLine.TryGetValue(lineKey, out int existing))
                    order.Add(lineKey);
                byLine[lineKey] = existing + scaled[i];
            }

            var lines = new KeyValuePair<string, int>[order.Count];
            for (int i = 0; i < order.Count; i++)
                lines[i] = new KeyValuePair<string, int>(order[i], byLine[order[i]]);
            return lines;
        }

        /// <summary>
        /// #456: the report's title bar already names the contract, so the outcome line states
        /// only what it is. Anything that is not a crew-wide outcome is the unreported tail.
        /// </summary>
        public static string OutcomeLabel(string kind)
        {
            if (IsCrewWideKey(kind))
                kind = kind.Substring(CrewWidePrefix.Length);
            return kind == "ContractCompleted" || kind == "ContractFailed"
                ? "Contract bonus"
                : "Other contract work";
        }

        public static int Sum(IReadOnlyList<KeyValuePair<string, int>> lines)
        {
            if (lines == null)
                return 0;

            long total = 0;
            for (int i = 0; i < lines.Count; i++)
                total += Math.Max(0, lines[i].Value);
            return total > int.MaxValue ? int.MaxValue : (int)total;
        }
    }
}
