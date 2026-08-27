using System;
using System.Collections.Generic;

namespace Y4NGZUpgrades.Patches
{
    internal enum NativeFistPhase : byte
    {
        Inactive = 0,
        Equipping = 1,
        Idle = 2,
        Punching = 3,
        Unequipping = 4,
    }

    internal enum NativeFistHand : byte
    {
        None = 0,
        Left = 1,
        Right = 2,
    }

    internal enum NativeFistHitKind : byte
    {
        Miss = 0,
        Surface = 1,
        Enemy = 2,
        ClingingEnemy = 3,
        Player = 4,
    }

    /// <summary>
    /// Unity-free rules shared by the live transport and the model checks. Presentation timing is
    /// deliberately not hidden in coroutines: the server advances this model, then broadcasts the
    /// resulting phase and authoritative hand.
    /// </summary>
    internal sealed class NativeFistSessionModel
    {
        internal const double EquipPlaybackSpeed = 2d;
        internal const double PunchPlaybackSpeed = 1.8d;
        internal const double UnequipPlaybackSpeed = 2d;
        internal const double EquipSeconds = (22d / 30d) / EquipPlaybackSpeed;
        internal const double InitialPunchLeadSeconds = (6d / 30d) / EquipPlaybackSpeed;
        internal const double PunchContactSeconds = (11d / 30d) / PunchPlaybackSpeed;
        internal const double InitialPunchContactSeconds = InitialPunchLeadSeconds + PunchContactSeconds;
        internal const double PunchCadenceSeconds = 0.75d;
        internal const double InactivitySeconds = 3d;
        internal const double UnequipSeconds = (25d / 30d) / UnequipPlaybackSpeed;

        private bool hasStateSequence;
        private bool hasPunchSequence;
        private uint lastStateSequence;
        private uint lastPunchSequence;

        internal NativeFistPhase Phase { get; private set; } = NativeFistPhase.Inactive;
        internal NativeFistHand NextHand { get; private set; } = NativeFistHand.Left;
        internal NativeFistHand ActiveHand { get; private set; } = NativeFistHand.None;
        internal uint LastStateSequence => lastStateSequence;
        internal uint LastPunchSequence => lastPunchSequence;
        internal uint QueuedPunchSequence { get; private set; }
        internal bool HasQueuedPunch { get; private set; }
        internal double LastAcceptedPunchAt { get; private set; } = double.NegativeInfinity;
        internal double NextPunchAllowedAt { get; private set; } = double.NegativeInfinity;
        internal int Generation { get; private set; }

        internal bool TrySetActive(uint sequence, bool desiredActive)
        {
            if (!AcceptSequence(sequence, ref hasStateSequence, ref lastStateSequence))
                return false;

            if (!desiredActive)
            {
                Interrupt();
                return true;
            }

            if (Phase != NativeFistPhase.Inactive)
                return false;

            Phase = NativeFistPhase.Equipping;
            ActiveHand = NativeFistHand.None;
            HasQueuedPunch = false;
            QueuedPunchSequence = 0;
            Generation++;
            return true;
        }

        internal bool TryQueueOrStartPunch(uint sequence, double now, out NativeFistHand hand)
        {
            hand = NativeFistHand.None;
            if (!AcceptSequence(sequence, ref hasPunchSequence, ref lastPunchSequence))
                return false;

            if (Phase == NativeFistPhase.Equipping)
            {
                if (HasQueuedPunch)
                    return false;

                HasQueuedPunch = true;
                QueuedPunchSequence = sequence;
                LastAcceptedPunchAt = now;
                return true;
            }

            if (Phase != NativeFistPhase.Idle || now + 1e-6d < NextPunchAllowedAt)
                return false;

            hand = BeginPunch(now, updateAcceptedInputTime: true);
            return true;
        }

        internal bool FinishEquip(double now, out uint punchSequence, out NativeFistHand hand)
        {
            punchSequence = 0;
            hand = NativeFistHand.None;
            if (Phase != NativeFistPhase.Equipping)
                return false;

            Phase = NativeFistPhase.Idle;
            if (!HasQueuedPunch)
                return true;

            HasQueuedPunch = false;
            punchSequence = QueuedPunchSequence;
            QueuedPunchSequence = 0;
            hand = BeginPunch(now, updateAcceptedInputTime: false);
            return true;
        }

        internal bool FinishPunch()
        {
            if (Phase != NativeFistPhase.Punching)
                return false;

            Phase = NativeFistPhase.Idle;
            ActiveHand = NativeFistHand.None;
            return true;
        }

        internal bool ShouldBeginUnequip(double now)
        {
            return Phase == NativeFistPhase.Idle &&
                   now - LastAcceptedPunchAt + 1e-6d >= InactivitySeconds;
        }

        internal bool BeginUnequip()
        {
            if (Phase == NativeFistPhase.Inactive || Phase == NativeFistPhase.Unequipping)
                return false;

            Phase = NativeFistPhase.Unequipping;
            ActiveHand = NativeFistHand.None;
            HasQueuedPunch = false;
            QueuedPunchSequence = 0;
            Generation++;
            return true;
        }

        internal bool TryBeginUnequip(uint sequence)
        {
            if (!AcceptSequence(sequence, ref hasStateSequence, ref lastStateSequence))
                return false;

            return BeginUnequip();
        }

        internal void FinishUnequip()
        {
            if (Phase == NativeFistPhase.Unequipping)
                ResetState();
        }

        internal void Interrupt()
        {
            Generation++;
            ResetState();
        }

        private NativeFistHand BeginPunch(double now, bool updateAcceptedInputTime)
        {
            NativeFistHand hand = NextHand;
            NextHand = hand == NativeFistHand.Left ? NativeFistHand.Right : NativeFistHand.Left;
            ActiveHand = hand;
            Phase = NativeFistPhase.Punching;
            if (updateAcceptedInputTime)
                LastAcceptedPunchAt = now;
            NextPunchAllowedAt = now + PunchCadenceSeconds;
            Generation++;
            return hand;
        }

        private void ResetState()
        {
            Phase = NativeFistPhase.Inactive;
            NextHand = NativeFistHand.Left;
            ActiveHand = NativeFistHand.None;
            HasQueuedPunch = false;
            QueuedPunchSequence = 0;
            LastAcceptedPunchAt = double.NegativeInfinity;
            NextPunchAllowedAt = double.NegativeInfinity;
        }

        private static bool AcceptSequence(uint candidate, ref bool hasValue, ref uint last)
        {
            if (hasValue && unchecked((int)(candidate - last)) <= 0)
                return false;

            hasValue = true;
            last = candidate;
            return true;
        }
    }

    internal sealed class NativeFistFractionalDamage<TKey>
    {
        private readonly Dictionary<TKey, float> remainderByTarget = new Dictionary<TKey, float>();

        internal int Add(TKey target, float shovelUnits)
        {
            if (ReferenceEquals(target, null) || shovelUnits <= 0f || float.IsNaN(shovelUnits) || float.IsInfinity(shovelUnits))
                return 0;

            remainderByTarget.TryGetValue(target, out float remainder);
            float total = remainder + shovelUnits;
            int whole = (int)Math.Floor(total + 0.00001f);
            float next = total - whole;
            if (next <= 0.00001f)
                remainderByTarget.Remove(target);
            else
                remainderByTarget[target] = next;
            return whole;
        }

        internal void Remove(TKey target)
        {
            if (!ReferenceEquals(target, null))
                remainderByTarget.Remove(target);
        }

        internal void Clear()
        {
            remainderByTarget.Clear();
        }
    }
}
