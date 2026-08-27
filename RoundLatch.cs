namespace Y4NGZUpgrades
{
    /// <summary>
    /// Unity-free round-state latch shared by every per-round ledger (progression XP,
    /// employee statistics). It owns the Active/Finalized pair and the per-round sequence
    /// number, and it is deliberately free of engine types so the checks project can prove
    /// the recovery semantics without a Unity runtime.
    ///
    /// The invariant that matters: a round that never received its start event, or that
    /// never finalized, must never wedge the NEXT round. <see cref="Begin"/> is therefore a
    /// full reset, not a conditional one.
    /// </summary>
    internal sealed class RoundLatch
    {
        /// <summary>
        /// Monotonic round counter. Zero means "no round has ever begun on this peer".
        /// Consumers key per-round deduplication on this instead of object identity, so a
        /// single missed round can never swallow the presentation of later rounds.
        /// </summary>
        internal int Sequence { get; private set; }

        internal bool Active { get; private set; }

        internal bool Finalized { get; private set; }

        /// <summary>
        /// Set when a finalize had to synthesize its own round start because the round-start
        /// event never reached this peer. This is a bug signal, never a normal state — it is
        /// what the host-only StartGame hook produced on every client before #214.
        /// </summary>
        internal bool FinalizedWithoutStart { get; private set; }

        internal void Begin()
        {
            Sequence++;
            Active = true;
            Finalized = false;
            FinalizedWithoutStart = false;
        }

        /// <summary>
        /// Opens a finalize pass.
        /// </summary>
        /// <param name="recoveredMissedStart">
        /// True when the round had to be synthesized because no start event arrived.
        /// </param>
        /// <returns>
        /// True when the caller must build a fresh payout; false when the round already
        /// finalized and the caller must return its cached result unchanged.
        /// </returns>
        internal bool TryBeginFinalize(out bool recoveredMissedStart)
        {
            recoveredMissedStart = false;
            if (Finalized)
                return false;

            if (!Active)
            {
                recoveredMissedStart = true;
                FinalizedWithoutStart = true;
                if (Sequence <= 0)
                    Sequence = 1;
                Active = true;
            }

            return true;
        }

        internal void CompleteFinalize()
        {
            Finalized = true;
            Active = false;
        }

        /// <summary>
        /// The round ended in a way that pays nothing (disconnect, game over, ship reset).
        /// Clears both halves so neither a stale Active nor a stale Finalized survives into
        /// the next round.
        /// </summary>
        internal void Abandon()
        {
            Active = false;
            Finalized = false;
            FinalizedWithoutStart = false;
        }
    }
}
