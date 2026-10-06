using GameNetcodeStuff;
using HarmonyLib;
using Y4NGZUpgrades.Upgrades;
using UnityEngine;
using UnityEngine.UI;

namespace Y4NGZUpgrades.Patches
{
    /// <summary>
    /// Shared classification of CauseOfDeath values, so every consumer layers on one
    /// hand-maintained list instead of carrying its own.
    /// </summary>
    internal static class DamageCauseFilter
    {
        /// <summary>
        /// The narrow "a creature just hit me in melee" set: anything wider risks treating a
        /// hit no enemy dealt as a creature's.
        /// </summary>
        internal static bool IsEnemyMeleeCause(CauseOfDeath cause)
        {
            switch (cause)
            {
                case CauseOfDeath.Mauling:
                case CauseOfDeath.Bludgeoning:
                case CauseOfDeath.Strangulation:
                case CauseOfDeath.Stabbing:
                case CauseOfDeath.Crushing:
                case CauseOfDeath.Suffocation:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The wider "a creature or a hostile trap killed me" set: the melee set plus trap
        /// and special instant-kill causes. Nine Lives' once-per-round death save
        /// uses this. Environmental/abandonment causes (Gravity, Drowning, Abandoned,
        /// Inertia) are deliberately excluded - they aren't "lethal hits".
        /// </summary>
        internal static bool IsEnemyOrTrapCause(CauseOfDeath cause, int deathAnimation)
        {
            if (IsEnemyMeleeCause(cause))
                return true;

            switch (cause)
            {
                case CauseOfDeath.Gunshots:
                case CauseOfDeath.Electrocution:
                case CauseOfDeath.Kicking:
                case CauseOfDeath.Burning:
                case CauseOfDeath.Blast:
                case CauseOfDeath.Fan:        // industrial fan trap
                case CauseOfDeath.Snipping:   // shears / barber
                case CauseOfDeath.Unknown:    // Ghost Girl and most modded instant kills
                    return true;
                default:
                    // Non-zero death animations are the scripted grab/attach kills
                    // (haunted mask, creepy doll, etc.), which are enemy-driven too.
                    return deathAnimation == 1;
            }
        }
    }

}
