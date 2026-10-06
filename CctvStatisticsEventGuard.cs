using System;
using System.Collections.Generic;

namespace Y4NGZUpgrades
{
    /// <summary>
    /// Round-scoped acceptance rules for the CCTV-specific employee counters. This is kept free
    /// of Unity and Netcode so the identity and delivery rules are covered by deterministic checks.
    /// </summary>
    internal sealed class CctvStatisticsEventGuard
    {
        private readonly HashSet<string> _acceptedDeviceKeys =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _acceptedAlarmKeys =
            new HashSet<string>(StringComparer.Ordinal);

        internal bool TryAcceptDevice(string logicalDeviceKey)
        {
            return !string.IsNullOrWhiteSpace(logicalDeviceKey)
                   && _acceptedDeviceKeys.Add(logicalDeviceKey);
        }

        internal bool TryAcceptAlarm(string episodeKey)
        {
            return !string.IsNullOrWhiteSpace(episodeKey)
                   && _acceptedAlarmKeys.Add(episodeKey);
        }

        /// <summary>
        /// CctvSupportApi.TryDisableCamera reports a valid request even when the target was
        /// already disabled. A statistic belongs only to the actor whose request changed the
        /// authoritative disabled state.
        /// </summary>
        internal static bool IsNewCameraDisable(
            bool wasDisabled,
            bool providerAccepted,
            bool isDisabled)
        {
            return !wasDisabled && providerAccepted && isDisabled;
        }

        internal static bool IsTrustedDelivery(
            ulong senderClientId,
            ulong serverClientId,
            ulong recipientClientId,
            ulong localClientId,
            ulong eventId)
        {
            return senderClientId == serverClientId
                   && recipientClientId == localClientId
                   && eventId != 0;
        }

        internal void Reset()
        {
            _acceptedDeviceKeys.Clear();
            _acceptedAlarmKeys.Clear();
        }
    }

    /// <summary>
    /// Pure late-join admission rule. A peer that joined after the normal round-start callback
    /// may open its own empty Employee File ledger only after its local player and landed-map
    /// metadata exist; an active ledger is never reset by this recovery path.
    /// </summary>
    internal static class CctvLateJoinLifecycle
    {
        internal static bool ShouldInitializePersonalRound(
            bool latchActive,
            bool isSaveKeyResolved,
            bool isLocalPlayerReady,
            bool isMapReady,
            bool shipHasLanded,
            bool inShipPhase,
            bool shipIsLeaving)
        {
            return !latchActive
                   && isSaveKeyResolved
                   && isLocalPlayerReady
                   && isMapReady
                   && shipHasLanded
                   && !inShipPhase
                   && !shipIsLeaving;
        }
    }
    /// <summary>
    /// Holds the host-issued round epoch accepted by one peer. The deterministic map token stops
    /// a delayed message from a prior level becoming current; the monotonic epoch also separates
    /// repeated visits to the same level in one network session.
    /// </summary>
    internal sealed class CctvStatisticsEpochGuard
    {
        private ulong _activeEpoch;
        private ulong _minimumEpoch;
        private ulong _levelToken;
        private bool _ready;

        internal ulong ActiveEpoch => _activeEpoch;
        internal ulong LevelToken => _levelToken;
        internal bool IsReady => _ready;

        internal void BeginRound(ulong levelToken)
        {
            if (_activeEpoch != 0 && _activeEpoch < ulong.MaxValue)
                _minimumEpoch = _activeEpoch + 1;

            _activeEpoch = 0;
            _levelToken = levelToken;
            _ready = false;
        }

        internal bool TryAcceptHostEpoch(ulong epoch, ulong levelToken)
        {
            if (epoch == 0
                || levelToken == 0
                || levelToken != _levelToken
                || epoch < _minimumEpoch
                || (_ready && epoch != _activeEpoch))
            {
                return false;
            }

            _activeEpoch = epoch;
            _ready = true;
            return true;
        }

        internal bool IsCurrent(ulong epoch, ulong levelToken)
        {
            return _ready
                   && epoch != 0
                   && epoch == _activeEpoch
                   && levelToken != 0
                   && levelToken == _levelToken;
        }

        internal void ResetSession()
        {
            _activeEpoch = 0;
            _minimumEpoch = 0;
            _levelToken = 0;
            _ready = false;
        }
    }
}
