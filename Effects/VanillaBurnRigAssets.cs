using System;
using UnityEngine;

namespace Y4NGZUpgrades.Effects
{
    /// <summary>
    /// Runtime acquisition of the vanilla Forest Giant burning rig (<c>ForestGiantAI.burningParticlesContainer</c>)
    /// so any burning enemy can wear a scaled clone of it. Nothing here is bundled: the flame subtree
    /// and its loop clip are read off whichever Forest Giant prefab the session already has loaded.
    /// The search is throttled and warns once, and every hop degrades to null so
    /// <see cref="BurningEnemyEffect"/> falls back to its procedural embers instead of throwing.
    /// </summary>
    internal static class VanillaBurnRigAssets
    {
        private const float SearchRetrySeconds = 2f;

        private static readonly Func<ForestGiantAI, bool> HasBurnRig =
            giant => giant.burningParticlesContainer != null;

        private static GameObject _rigTemplate;
        private static AudioClip _burningClip;
        private static float _nextSearchTime;
        private static bool _missingRigLogged;

        /// <summary>
        /// Clones the flame subtree under <paramref name="parent"/>, inactive and netcode-free.
        /// The caller positions, scales and activates it.
        /// </summary>
        internal static GameObject CloneRig(Transform parent)
        {
            ResolveAssets();
            return VanillaCloneStaging.CloneInactive(_rigTemplate, parent, "Y4NGZ_ForestGiantBurnRig");
        }

        /// <summary>The giant's <c>BurningSFX</c> loop, taken off the same prefab as the rig.</summary>
        internal static AudioClip ResolveBurningClip()
        {
            ResolveAssets();
            return _burningClip;
        }

        private static void ResolveAssets()
        {
            if (_rigTemplate != null)
                return;
            if (Time.unscaledTime < _nextSearchTime)
                return;
            _nextSearchTime = Time.unscaledTime + SearchRetrySeconds;

            ForestGiantAI giant = VanillaEnemyPrefabs.Find(HasBurnRig);
            if (giant == null)
            {
                WarnMissingOnce("no Forest Giant prefab is loaded yet");
                return;
            }

            if (giant.burningParticlesContainer == null)
            {
                WarnMissingOnce("the Forest Giant prefab carries no burningParticlesContainer");
                return;
            }

            _rigTemplate = giant.burningParticlesContainer;
            if (giant.giantBurningAudio != null)
                _burningClip = giant.giantBurningAudio.clip;

            Plugin.Log?.LogInfo(
                "[BurnRig] resolved the vanilla Forest Giant burning rig (particles=" +
                _rigTemplate.GetComponentsInChildren<ParticleSystem>(true).Length +
                ", lights=" + _rigTemplate.GetComponentsInChildren<Light>(true).Length +
                ", clip=" + (_burningClip != null ? _burningClip.name : "none") + ").");
        }

        private static void WarnMissingOnce(string reason)
        {
            if (_missingRigLogged)
                return;
            _missingRigLogged = true;
            Plugin.Log?.LogWarning(
                "[BurnRig] " + reason + "; burning enemies use the procedural effect and the search retries.");
        }
    }
}
