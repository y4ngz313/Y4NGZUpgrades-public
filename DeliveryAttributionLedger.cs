using System.Collections.Generic;

namespace Y4NGZUpgrades
{
    /// <summary>
    /// Unity-free record of who last carried an object across the ship boundary, and the
    /// decision of whether the local player earned delivery credit for it.
    ///
    /// Why this exists (#215): delivery attribution used to key off
    /// <c>GrabbableObject.GrabItem</c>, which only ever runs on the grabbing player's own
    /// machine (GrabItemOnClient is owner-only and vanilla scrap does not set
    /// syncGrabFunction). Every peer therefore recorded itself as the "last holder" of
    /// everything it ever touched, so the finalize-time holder check was a tautology and
    /// every player who lifted an object outside was paid the full delivery rate
    /// independently — hand-offs, drop-and-recollect and multi-carrier hauls all multiplied
    /// the payout across the crew, and a dead carrier stayed paid.
    ///
    /// The truth this ledger stores is the ITEM's own flag transition
    /// (<c>isInShipRoom || isInElevator</c>), sampled before and after each vanilla write to
    /// those flags, with the responsible player taken from the write site. It is deliberately
    /// NOT "whatever the call site claimed": no single vanilla method sees every crossing.
    /// <c>SetItemInElevator</c> covers ship-room transitions on every peer, but a remote
    /// player's held item also has <c>isInElevator</c> written directly by
    /// <c>SetHeldObjectInShip</c>, which only forwards to <c>SetItemInElevator</c> when
    /// <c>isInShipRoom</c> changes — so an elevator-only (ship deck) crossing is invisible
    /// there and the ledgers on different machines would disagree about who last brought the
    /// object in. Every write site that can move an object across the boundary must report,
    /// which is why the plugin patches both, plus the enemy-carrier path.
    ///
    /// Where the responsible player cannot be established the same way on every machine, the
    /// crossing is recorded against <see cref="UnattributedHandler"/> and pays NOBODY. A
    /// deterministic zero is strictly better than a non-deterministic double payout.
    ///
    /// Exact cross-machine agreement on WHICH FRAME a crossing happened is not required, and
    /// is not achievable: the replicated position/flag streams settle at different times on
    /// different peers, so a grab at the ship threshold can be observed in a different order
    /// on each machine. What must agree is the FINAL (handler, inside) pair at round end, and
    /// it does: each transition overwrites the previous record outright, so once the flags
    /// stop moving every machine holds the last transition, regardless of the path it took to
    /// get there.
    ///
    /// The ledger is deliberately object-kind agnostic: it stores network keys, not scrap.
    /// A ragdoll crossing the boundary records a handler the same way scrap does (#219).
    /// </summary>
    internal sealed class DeliveryAttributionLedger
    {
        /// <summary>
        /// Handler recorded for a crossing whose responsible player is not crew-consistent:
        /// an enemy carrying scrap, or a vanilla call site that passes
        /// <c>localPlayerController</c> rather than the actual carrier (belt bag contents,
        /// kicked soccer balls, the vehicle magnet), which names a different player on every
        /// machine. No real client id can equal it, so such an object pays nobody.
        /// </summary>
        internal const ulong UnattributedHandler = ulong.MaxValue;

        private readonly Dictionary<ulong, ulong> _lastHandlerByKey = new Dictionary<ulong, ulong>();
        private readonly HashSet<ulong> _preexistingInsideKeys = new HashSet<ulong>();

        /// <summary>Objects already banked in the ship when the round began.</summary>
        internal int PreexistingInsideCount => _preexistingInsideKeys.Count;

        internal void Reset()
        {
            _lastHandlerByKey.Clear();
            _preexistingInsideKeys.Clear();
        }

        /// <summary>
        /// Records an object that was already inside the ship at round start. Such objects can
        /// never pay delivery XP again, which is what stops re-touching banked scrap (or
        /// dumping it outside and re-collecting it) from paying the delivery rate every round.
        /// </summary>
        internal void MarkPreexistingInside(ulong key)
        {
            _preexistingInsideKeys.Add(key);
        }

        /// <summary>
        /// Records one boundary transition. <paramref name="nowInside"/> true means the object
        /// just entered the ship/elevator, and <paramref name="handlerClientId"/> becomes the
        /// player who is credited for it. False means it just left, which revokes credit
        /// entirely: carrying scrap back out undoes the delivery, and a later re-entry
        /// re-credits whoever brought it back.
        ///
        /// Callers must only invoke this on an actual transition; a repeated call with the
        /// object's existing side would otherwise re-credit a player who merely handled the
        /// object without moving it across.
        /// </summary>
        internal void RecordBoundaryCrossing(ulong key, ulong handlerClientId, bool nowInside)
        {
            if (nowInside)
                _lastHandlerByKey[key] = handlerClientId;
            else
                _lastHandlerByKey.Remove(key);
        }

        /// <summary>
        /// True when an observed outward transition must be discarded instead of revoking
        /// credit, because the item did not physically leave with the player named for it.
        ///
        /// Vanilla's death and disconnect drops call DropAllHeldItems with setInShip:false
        /// unconditionally, whatever the player's actual position, so a player who dies
        /// standing in the ship emits an attributed inside-to-outside crossing that would
        /// delete the delivery credit they had already earned. A genuine carry-out is always
        /// performed by a living player who is themselves outside the ship at the time, and
        /// both of those are replicated facts, so every machine discards the same transitions.
        /// </summary>
        internal static bool SuppressOutwardTransition(
            bool nowInside,
            bool handlerKnown,
            bool handlerIsDead,
            bool handlerIsInsideShip)
        {
            return !nowInside && handlerKnown && (handlerIsDead || handlerIsInsideShip);
        }

        /// <summary>
        /// The body-retrieval decision layered on top of the delivery decision (#219). The
        /// ledger stays kind-agnostic — this is the policy, not the storage — and every input
        /// is a replicated fact, so each machine reaches the same verdict for its own player.
        ///
        /// <paramref name="lastHandlerIsPayee"/> is <see cref="ShouldPayDelivery"/> for the
        /// body's key, which already covers "inside at round end", "not aboard when the round
        /// began" and "the payee is the player who carried it in".
        ///
        /// The two extra conditions:
        /// <list type="bullet">
        /// <item><paramref name="bodyIsInShip"/> / <paramref name="bodyDeactivated"/> read the
        /// ragdoll's own state rather than the grabbable's boundary flags. A deactivated body
        /// (teleporter-recovered, or otherwise removed from play) is not a corpse in the ship
        /// any more and pays nothing.</item>
        /// <item><paramref name="payeeOwnsBody"/> is the self-retrieval case: hauling your own
        /// corpse aboard through a revive or teleporter edge case is not a rescue of a
        /// teammate, so it pays nothing. It FAILS CLOSED through
        /// <paramref name="ownerResolved"/>: when the corpse's owner cannot be established at
        /// all, the award is suppressed rather than granted, exactly like an unattributable
        /// crossing paying nobody. Paying on an unknown owner is the strictly worse error —
        /// it is the self-retrieval exploit with extra steps.</item>
        /// </list>
        ///
        /// A body destroyed before round end (Inspire revive destroys the DeadBodyInfo, and the
        /// grabbable is its child) never reaches this call at all: the finalize pass enumerates
        /// live objects, so a stale handler record for it simply pays nobody. The same holds for
        /// a player's FIRST corpse after a revive-and-die-again: records are keyed by corpse
        /// instance, so the old key belongs to a destroyed object and the new corpse starts with
        /// no record of its own.
        /// </summary>
        internal static bool ShouldPayBodyRetrieval(
            bool lastHandlerIsPayee,
            bool bodyIsInShip,
            bool bodyDeactivated,
            bool ownerResolved,
            bool payeeOwnsBody)
        {
            return lastHandlerIsPayee
                   && bodyIsInShip
                   && !bodyDeactivated
                   && ownerResolved
                   && !payeeOwnsBody;
        }

        internal bool TryGetDeliveryHandler(ulong key, out ulong handlerClientId)
        {
            return _lastHandlerByKey.TryGetValue(key, out handlerClientId);
        }

        /// <summary>
        /// The full delivery decision for one object at round end.
        ///
        /// An object pays the local player exactly when it is inside the ship at round end, it
        /// was not already banked there when the round began, and the local player is the last
        /// player who carried it in. The presence of a handler record is itself the
        /// outside-origin proof: an entry only exists because the object crossed INTO the ship
        /// during the round, which means it came from outside.
        /// </summary>
        internal bool ShouldPayDelivery(ulong key, bool insideAtRoundEnd, ulong localClientId)
        {
            return insideAtRoundEnd
                   && !_preexistingInsideKeys.Contains(key)
                   && _lastHandlerByKey.TryGetValue(key, out ulong handlerClientId)
                   && handlerClientId != UnattributedHandler
                   && handlerClientId == localClientId;
        }
    }
}
