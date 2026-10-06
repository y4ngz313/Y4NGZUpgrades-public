using System;
using System.Collections.Generic;

namespace Y4NGZUpgrades
{
    /// <summary>
    /// Which contract acts are priced against the actor's rank band (#457), and the flat XP each
    /// of them ships with.
    ///
    /// Two classes, because two shapes of contract exist. A HEADLINE act is the one thing that
    /// contract is about - the kill, the restore, the defusal - and is paid a full headline share
    /// of the rank band. A STEP act repeats several times in one contract (survey beacons,
    /// containment waves), so it is paid a much smaller share per occurrence and the contract's
    /// total still lands in the same neighbourhood.
    ///
    /// The flat numbers live here rather than beside their <c>Bind</c> calls so the deterministic
    /// checks can price every shipped act without a Unity-bound config: <c>ProgressionSettings</c>
    /// binds its defaults from this table, and the LGU rows keep taking theirs from
    /// <see cref="LguContractXpCatalog"/>, so each number still has exactly one home.
    ///
    /// Unity-free and config-free on purpose. The price itself is
    /// <see cref="ProgressionEconomyMath.ComputeContractActXp"/>; this table only says which
    /// fraction key an act reads and what its floor is.
    /// </summary>
    internal static class ContractActCatalog
    {
        internal enum ActScaling
        {
            /// <summary>The contract's defining act; paid the headline fraction of the band.</summary>
            Headline,

            /// <summary>One of several repeated steps; paid the step fraction, per step.</summary>
            Step
        }

        internal readonly struct Row
        {
            internal readonly string Kind;
            internal readonly ActScaling Scaling;
            internal readonly int FlatDefaultXp;

            internal Row(string kind, ActScaling scaling, int flatDefaultXp)
            {
                Kind = kind;
                Scaling = scaling;
                FlatDefaultXp = flatDefaultXp;
            }
        }

        private static readonly Row[] Rows =
        {
            new Row("BlackoutAuditRestored", ActScaling.Headline, 35),
            new Row("WhistleblowerNeutralized", ActScaling.Headline, 35),
            new Row("ShadowRaidDrillCompleted", ActScaling.Headline, 15),
            new Row("PestControlCompleted", ActScaling.Headline, 25),
            new Row("WasteDisposalCompleted", ActScaling.Headline, 25),
            new Row("DefuseCorrectCode", ActScaling.Headline, 10),
            new Row("LguDataRecovered", ActScaling.Headline, LguFlatDefault("LguDataRecovered")),
            new Row("LguNestDestroyed", ActScaling.Headline, LguFlatDefault("LguNestDestroyed")),
            new Row("LguScavengerRecovered", ActScaling.Headline, LguFlatDefault("LguScavengerRecovered")),
            new Row("LguDemonIdentified", ActScaling.Headline, LguFlatDefault("LguDemonIdentified")),
            new Row("LguDeviceDefused", ActScaling.Headline, LguFlatDefault("LguDeviceDefused")),
            new Row("SurveyDronePlaced", ActScaling.Step, 12),
            new Row("ContainmentBreachWaveSurvived", ActScaling.Step, 10)
        };

        internal static IReadOnlyList<Row> All => Rows;

        internal static bool TryGet(string kind, out Row row)
        {
            if (!string.IsNullOrEmpty(kind))
            {
                foreach (Row candidate in Rows)
                {
                    if (string.Equals(kind, candidate.Kind, StringComparison.Ordinal))
                    {
                        row = candidate;
                        return true;
                    }
                }
            }

            row = default;
            return false;
        }

        /// <summary>
        /// The shipped flat award for a scaled kind, which is also the default its config key
        /// binds. An unlisted kind has no scaled default and returns zero.
        /// </summary>
        internal static int FlatDefaultXp(string kind)
        {
            return TryGet(kind, out Row row) ? row.FlatDefaultXp : 0;
        }

        /// <summary>
        /// Whether an award under this event kind proves the local player TOOK PART in the round's
        /// contract work (#457), which is half of the completion award's gate.
        ///
        /// A named act kind does - every one of them is published against a player who did
        /// something, scaled or not. The two outcome kinds do not: they are the very award being
        /// gated. Neither does the unnamed bucket behind the public
        /// <c>ProgressionApi.AwardContractXp</c> surface, which names no event and could be
        /// granted by any mod for any reason, including to a player who never left the ship; it
        /// keeps its XP and its place in the report reconciliation, it just proves nothing about
        /// participation.
        /// </summary>
        internal static bool CountsAsParticipation(string eventKind)
        {
            return !string.IsNullOrWhiteSpace(eventKind)
                   && !string.Equals(eventKind, "ContractCompleted", StringComparison.Ordinal)
                   && !string.Equals(eventKind, "ContractFailed", StringComparison.Ordinal);
        }

        private static int LguFlatDefault(string kind)
        {
            return LguContractXpCatalog.TryGet(kind, out LguContractXpCatalog.Award award) ? award.BaseXp : 0;
        }
    }
}
