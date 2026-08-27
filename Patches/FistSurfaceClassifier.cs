// COPIED from what is now BetterArmory/Weapons/WeaponSurfaceClassifier.cs (#266).
//
// The native fists (NativeFistsPatch) are melee, not a weapon, so they stayed in Y4NGZUpgrades -
// but they resolved impact surfaces through the weapon classifier. The classifier is a pure read
// of vanilla's StartOfRound.footstepSurfaces table with no state and no caching, so a second copy
// costs one tag read per punch and duplicates nothing. The weapon-VFX diagnostic dump was dropped
// on the way across; it belongs with the weapons.
using UnityEngine;

namespace Y4NGZUpgrades.Patches
{
    /// <summary>
    /// What material did this shot hit? (#118)
    ///
    /// <b>The premise this replaced was wrong.</b> `WORKFLOW_Y4NGZ_WEAPONS.md` recorded surface
    /// classification as an open question on the stated premise that "LC colliders carry no material
    /// metadata". They do, and the code that disproves it is vanilla melee: <c>Shovel.HitShovel</c>
    /// and <c>KnifeItem.HitKnife</c> both read <c>collider.gameObject.tag</c> and scan it against
    /// <c>StartOfRound.footstepSurfaces[i].surfaceTag</c> on every single swing. The table is not
    /// footstep-only either — <c>FootstepSurface</c> carries a field literally named
    /// <see cref="FootstepSurface.hitSurfaceSFX"/>, "the sound of something striking this material",
    /// authored for melee impacts. Firearms have simply never asked.
    ///
    /// <b>Match on the string, never on an index.</b> The tag strings and the array order are
    /// authored on the <c>StartOfRound</c> prefab and are absent from the decompiled C#, so an index
    /// this code hardcodes is an index nobody has verified. The only indices vanilla itself hardcodes
    /// are 4/5 (terrain variants), 8 (water) and 12 (slippery). <see cref="LogSurfaceTable"/> dumps
    /// the live table once per session so the real strings can be read off a run rather than guessed.
    ///
    /// <b>No cache, deliberately.</b> A lookup is one tag read and at most ~13 string compares.
    /// Vanilla pays it per melee swing; the MX16A4's 12.5 hits/second is the same cost times twelve,
    /// which is nothing. A collider-keyed cache would buy an invalidation bug — instance IDs are
    /// reused after destruction — in exchange for noise-level savings.
    /// </summary>
    internal static class FistSurfaceClassifier
    {
        /// <summary>No tag matched. Every consumer falls back to its own default, which is exactly
        /// the behaviour that shipped before this existed — so classification cannot regress
        /// anything, it can only improve a hit it recognises.</summary>
        internal const int UnknownSurface = -1;

        /// <summary>
        /// The surface index for a collider, or <see cref="UnknownSurface"/>.
        ///
        /// Mirrors <c>Shovel.HitShovel</c> exactly: the collider's own tag, scanned against the
        /// authored table, first match wins.
        /// </summary>
        internal static int ResolveSurfaceIndex(Collider collider)
        {
            if (collider == null)
                return UnknownSurface;

            StartOfRound round = StartOfRound.Instance;
            if (round == null || round.footstepSurfaces == null)
                return UnknownSurface;

            GameObject go = collider.gameObject;
            if (go == null)
                return UnknownSurface;

            string tag = go.tag;
            if (string.IsNullOrEmpty(tag) || tag == "Untagged")
                return UnknownSurface;

            FootstepSurface[] surfaces = round.footstepSurfaces;
            for (int i = 0; i < surfaces.Length; i++)
            {
                if (surfaces[i] != null && surfaces[i].surfaceTag == tag)
                    return i;
            }

            return UnknownSurface;
        }

        /// <summary>The authored tag for a resolved index, or an empty string.</summary>
        internal static string SurfaceTag(int surfaceIndex)
        {
            FootstepSurface surface = SurfaceAt(surfaceIndex);
            return surface != null && surface.surfaceTag != null ? surface.surfaceTag : string.Empty;
        }

        /// <summary>
        /// The clip vanilla authored for "something struck this material", or null.
        ///
        /// Not every surface in the table carries one — <see cref="LogSurfaceTable"/> reports which
        /// do, so a silent impact can be traced to an unauthored clip rather than to this code.
        /// </summary>
        internal static AudioClip HitSurfaceClip(int surfaceIndex)
        {
            FootstepSurface surface = SurfaceAt(surfaceIndex);
            return surface != null ? surface.hitSurfaceSFX : null;
        }

        private static FootstepSurface SurfaceAt(int surfaceIndex)
        {
            if (surfaceIndex < 0)
                return null;

            StartOfRound round = StartOfRound.Instance;
            if (round == null || round.footstepSurfaces == null)
                return null;

            return surfaceIndex < round.footstepSurfaces.Length
                ? round.footstepSurfaces[surfaceIndex]
                : null;
        }

    }
}
