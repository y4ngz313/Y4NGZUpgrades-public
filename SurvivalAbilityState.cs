using System;

namespace Y4NGZUpgrades
{
    // Unity-free rules shared by runtime and behavioral checks.
    internal sealed class NineLivesState
    {
        internal const float RechargeSeconds = 15f;
        internal const float ProtectionSeconds = 4f;
        internal int Shield { get; private set; }
        internal bool SaveUsed { get; private set; }
        internal float RechargeAt { get; private set; }
        internal float ProtectedUntil { get; private set; }
        internal bool IsProtected(float now) => now < ProtectedUntil;

        internal void Reset(float now)
        {
            Shield = 0;
            SaveUsed = false;
            ProtectedUntil = 0f;
            RechargeAt = now + RechargeSeconds;
        }

        internal void ClearLife(float now)
        {
            Shield = 0;
            ProtectedUntil = 0f;
            RechargeAt = now + RechargeSeconds;
        }

        internal bool Tick(float now, int capacity)
        {
            capacity = Math.Max(0, capacity);
            Shield = Math.Min(Shield, capacity);
            if (now < RechargeAt || Shield >= capacity) return false;
            Shield = capacity;
            return capacity > 0;
        }

        internal int Absorb(int damage, float now)
        {
            if (damage <= 0) return damage;
            if (IsProtected(now)) return 0;
            RechargeAt = now + RechargeSeconds;
            int absorbed = Math.Min(Shield, damage);
            Shield -= absorbed;
            return damage - absorbed;
        }

        internal bool PreventDeath(float now, bool hasSave, bool eligible)
        {
            if (!eligible) return false;
            if (IsProtected(now)) return true;
            if (!hasSave || SaveUsed) return false;
            SaveUsed = true;
            Shield = 0;
            ProtectedUntil = now + ProtectionSeconds;
            RechargeAt = now + RechargeSeconds;
            return true;
        }
    }

    internal sealed class IsolationState
    {
        internal const float ActivationDistance = 35f;
        internal const float ReunionDistance = 25f;
        internal const float ArmSeconds = 8f;
        internal const float ReunionGraceSeconds = 2f;
        internal bool Active { get; private set; }
        private float _aloneSince = -1f;
        private float _reunionSince = -1f;

        internal void Reset()
        {
            Active = false;
            _aloneSince = _reunionSince = -1f;
        }

        internal void Tick(float now, bool eligible, float nearestTeammate)
        {
            if (!eligible) { Reset(); return; }
            if (!Active)
            {
                if (nearestTeammate < ActivationDistance) _aloneSince = -1f;
                else if (_aloneSince < 0f) _aloneSince = now;
                else if (now - _aloneSince >= ArmSeconds) Active = true;
                return;
            }
            if (nearestTeammate >= ReunionDistance) _reunionSince = -1f;
            else if (_reunionSince < 0f) _reunionSince = now;
            else if (now - _reunionSince >= ReunionGraceSeconds) Reset();
        }
    }

    internal static class SurvivalAbilityRules
    {
        internal const float CloakCooldown = 90f;
        internal static float CloakDuration(int tier) => tier <= 0 ? 0f : tier == 1 ? 3f : tier == 2 ? 4.5f : 6f;
        internal static float StaminaRecoveryBonus(int tier) => tier <= 0 ? 0f : tier >= 3 ? 0.5f : 0.25f;
        internal static int ReduceIsolatedDamage(int damage, int tier, bool active) =>
            damage <= 0 || !active || tier < 2 ? damage : (int)Math.Round(damage * 0.85, MidpointRounding.AwayFromZero);
    }
}
