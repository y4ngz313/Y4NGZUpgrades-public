using System.Collections.Generic;
using UnityEngine;

namespace Y4NGZUpgrades.Effects
{
    /// <summary>
    /// One shared cap on realtime fire lights. HDRP point lights are the expensive part of every
    /// fire effect, so burning enemies (and any later ground-fire patches) register here instead of
    /// each enabling its own light unconditionally. Over the cap, only the lights nearest the active
    /// camera stay on.
    /// </summary>
    internal static class BurningFireLightBudget
    {
        private const float EvaluateInterval = 0.25f;
        private const int DefaultCap = 6;

        private static readonly List<Light> Lights = new List<Light>();
        private static float _nextEvaluateTime;

        internal static void Register(Light light)
        {
            if (light == null || Lights.Contains(light))
                return;
            Lights.Add(light);
            // Evaluate on the next tick rather than now: a fresh light should not out-rank the ones
            // already lit until the whole set is compared.
            _nextEvaluateTime = 0f;
        }

        internal static void Unregister(Light light)
        {
            if (light == null)
                return;
            Lights.Remove(light);
            _nextEvaluateTime = 0f;
        }

        /// <summary>Safe to call from every registered effect's Update; the work is globally throttled.</summary>
        internal static void Tick()
        {
            if (Time.unscaledTime < _nextEvaluateTime)
                return;
            _nextEvaluateTime = Time.unscaledTime + EvaluateInterval;
            Evaluate();
        }

        private static void Evaluate()
        {
            for (int i = Lights.Count - 1; i >= 0; i--)
            {
                if (Lights[i] == null)
                    Lights.RemoveAt(i);
            }

            int cap = Plugin.BurningMaxSimultaneousLights != null
                ? Mathf.Max(0, Plugin.BurningMaxSimultaneousLights.Value)
                : DefaultCap;

            if (Lights.Count <= cap)
            {
                for (int i = 0; i < Lights.Count; i++)
                    Lights[i].enabled = true;
                return;
            }

            for (int i = 0; i < Lights.Count; i++)
                Lights[i].enabled = false;

            Vector3 viewer = ResolveViewerPosition();

            // Repeated min-scan rather than a sort: the cap is single digits, and List.Sort with a
            // comparison delegate allocates on every pass.
            for (int slot = 0; slot < cap; slot++)
            {
                int nearest = -1;
                float nearestDistance = float.MaxValue;
                for (int i = 0; i < Lights.Count; i++)
                {
                    Light light = Lights[i];
                    if (light.enabled)
                        continue;
                    float distance = (light.transform.position - viewer).sqrMagnitude;
                    if (distance >= nearestDistance)
                        continue;
                    nearestDistance = distance;
                    nearest = i;
                }

                if (nearest < 0)
                    break;
                Lights[nearest].enabled = true;
            }
        }

        private static Vector3 ResolveViewerPosition()
        {
            Camera camera = StartOfRound.Instance != null ? StartOfRound.Instance.activeCamera : null;
            if (camera == null)
                camera = Camera.main;
            return camera != null ? camera.transform.position : Vector3.zero;
        }
    }
}
