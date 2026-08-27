using UnityEngine;

namespace Y4NGZUpgrades
{
    /// <summary>
    /// Name-only test for "is this held object one of Better Armory's weapons?" (#266).
    ///
    /// Y4NGZUpgrades holds no compile-time reference to the weapon types any more, but one place
    /// still genuinely needs the answer rather than a degraded default:
    /// <see cref="Patches.EnemyStatusEffectPatch"/>. That patch applies burning when the local
    /// player damages an enemy while holding anything whose NAME looks like a flamethrower, and it
    /// has always excluded Y4NGZ's own flamethrower because that weapon applies burning itself.
    /// Without the exclusion both paths fire: the enemy takes two independent burn effects, ticking
    /// twice and wearing two fire rigs.
    ///
    /// So the check survives as a full-type-name scan. The moved files deliberately kept their
    /// original <c>Y4NGZUpgrades.Weapons</c> namespace, which is what makes this reliable.
    ///
    /// It is the declared answer to that question on this side of the split (#267), and it now
    /// also classifies two-handed weapon stows (<see cref="Interactive.HeldItemStowService"/>) and
    /// suppresses weapon-specific prompts (<see cref="Interactive.Hud.Y4ngzPromptOverlay"/>).
    /// </summary>
    internal static class ArmoryItemProbe
    {
        private const string WeaponNamespacePrefix = "Y4NGZUpgrades.Weapons.";

        internal static bool IsArmoryWeapon(GrabbableObject item)
        {
            if (item == null)
                return false;

            Component[] components = item.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null)
                    continue;

                string fullName = component.GetType().FullName;
                if (fullName != null && fullName.StartsWith(WeaponNamespacePrefix, System.StringComparison.Ordinal))
                    return true;
            }

            return false;
        }
    }
}
