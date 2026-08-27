using System;

namespace Y4NGZUpgrades
{
    /// <summary>
    /// Decides whether a contract progression event credited ONE named player or fanned the same
    /// occurrence out to the whole crew. Unity-free on purpose: the rule is the gate on every
    /// personal lifetime statistic, so it is exercised by the checks project off-engine.
    ///
    /// XP does not need this distinction — a crew-wide award is a legitimate award. A personal
    /// counter does: "you planted 40 beacons" has to mean this player planted them.
    /// </summary>
    internal static class ContractEventAttribution
    {
        /// <summary>
        /// The explicit crew-wide marker Y4NGZCompany writes into a fan-out event id.
        ///
        /// It exists because nothing else in the payload separates the two paths. The player
        /// suffix does not: the crew-wide fallback publishes one SUFFIXED id per participant, so
        /// "ends with my client id" is true on both paths and proves only that this event was
        /// meant for this machine. The marker is the discriminator; the suffix stays part of the
        /// test so a malformed or foreign id cannot pass by accident.
        /// </summary>
        internal const string CrewFanOutMarker = ".crew.";

        /// <summary>
        /// True when <paramref name="creditedClientId"/> was credited INDIVIDUALLY by this event.
        ///
        /// COVERAGE, and it is deliberately narrow: the marker is currently emitted by the survey
        /// publisher alone. Newer personal counters use their own provider-specific attribution
        /// seams (for example, the Blackout actor latch) or intentionally follow the provider's
        /// crew-fallback semantics; they do not call this helper.
        ///
        /// So this is NOT a general "was this attributed" oracle, and must not be used as one. An
        /// unmarked fan-out id from some other kind reads as individually attributed here. Any
        /// future kind that starts feeding a personal statistic has to adopt the marker on its
        /// fan-out path first; without that, this returns a confident wrong answer for it.
        /// </summary>
        internal static bool IsIndividuallyAttributed(string eventId, ulong creditedClientId)
        {
            if (string.IsNullOrEmpty(eventId))
                return false;

            // Concatenated rather than formatted: the BepInEx Harmony analyzer reads a ToString
            // call on a parameter inside a patch class as a discarded assignment, and this rule is
            // called from one. A ulong renders as invariant ASCII digits either way.
            if (!eventId.EndsWith("." + creditedClientId, StringComparison.Ordinal))
                return false;

            return eventId.IndexOf(CrewFanOutMarker, StringComparison.Ordinal) < 0;
        }
    }
}
