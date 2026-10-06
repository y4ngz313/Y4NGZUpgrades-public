using System.Runtime.CompilerServices;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Patches
{
    /// <summary>
    /// Field Optics light half: scales brightness and cone width on whichever flashlight the
    /// local player is currently operating (held bulb, its glow fill, or the helmet lamp the
    /// game switches to while the flashlight is pocketed).
    /// </summary>
    [HarmonyPatch]
    internal static class FieldOpticsFlashlightPatch
    {
        private sealed class LightBaseline
        {
            internal float Intensity;
            internal float SpotAngle;
        }

        private static readonly ConditionalWeakTable<Light, LightBaseline> Baselines =
            new ConditionalWeakTable<Light, LightBaseline>();

        // F-TECH-13: the helmet light is the one light that is only written while an operator
        // resolves. Vanilla PocketItem/DiscardItem clear usingPlayerHelmetLight, so dropping or
        // handing over a pocketed flashlight made operator_ null and stranded the boosted
        // intensity on the helmet lamp. Remembering who we last boosted lets the reset run once.
        private static PlayerControllerB _boostedHelmetOperator;

        [HarmonyPatch(typeof(FlashlightItem), "Update")]
        [HarmonyPostfix]
        private static void BoostOperatedFlashlight(FlashlightItem __instance)
        {
            if (__instance == null) return;

            // A flashlight that stops being ours (dropped, handed over, upgrade reset) falls back to
            // 1.0 so the cached baseline is written back instead of leaving the boost stranded on it.
            bool boosted = IsOperatedByLocalPlayer(__instance, out PlayerControllerB operator_);
            float intensityMultiplier = boosted ? FieldOpticsUpgrade.GetLightIntensityMultiplier() : 1f;
            float coneMultiplier = boosted ? FieldOpticsUpgrade.GetLightConeMultiplier() : 1f;

            // Vanilla Update rewrites flashlightBulb.intensity from initialIntensity every frame
            // (and randomises it under interference), so scaling in place never compounds and the
            // interference flicker survives.
            Light bulb = __instance.flashlightBulb;
            if (bulb != null)
            {
                bulb.intensity *= intensityMultiplier;
                ApplyCone(bulb, coneMultiplier);
            }

            // Nothing rewrites these each frame, so they are driven from a cached baseline.
            ApplyFromBaseline(__instance.flashlightBulbGlow, intensityMultiplier, coneMultiplier);

            if (operator_ != null)
            {
                ApplyFromBaseline(operator_.helmetLight, intensityMultiplier, coneMultiplier);
                if (boosted)
                    _boostedHelmetOperator = operator_;
                else if (_boostedHelmetOperator == operator_)
                    _boostedHelmetOperator = null;
            }
            else if (_boostedHelmetOperator != null)
            {
                // F-TECH-13: one final write of the cached baseline, which is what the design
                // doc promises ("multipliers fall back to 1.0").
                ApplyFromBaseline(_boostedHelmetOperator.helmetLight, 1f, 1f);
                _boostedHelmetOperator = null;
            }
        }

        private static bool IsOperatedByLocalPlayer(FlashlightItem flashlight, out PlayerControllerB operator_)
        {
            operator_ = flashlight.playerHeldBy;
            if (operator_ == null && flashlight.usingPlayerHelmetLight)
                operator_ = flashlight.previousPlayerHeldBy;

            if (operator_ == null) return false;
            return operator_ == GameNetworkManager.Instance?.localPlayerController;
        }

        private static void ApplyFromBaseline(Light light, float intensityMultiplier, float coneMultiplier)
        {
            if (light == null) return;

            LightBaseline baseline = Baselines.GetValue(light, CaptureBaseline);
            light.intensity = baseline.Intensity * intensityMultiplier;
            SetSpotAngle(light, baseline.SpotAngle * coneMultiplier);
        }

        private static void ApplyCone(Light light, float coneMultiplier)
        {
            LightBaseline baseline = Baselines.GetValue(light, CaptureBaseline);
            SetSpotAngle(light, baseline.SpotAngle * coneMultiplier);
        }

        private static LightBaseline CaptureBaseline(Light light)
        {
            return new LightBaseline
            {
                Intensity = light.intensity,
                SpotAngle = light.spotAngle,
            };
        }

        private static void SetSpotAngle(Light light, float angle)
        {
            if (light.type != LightType.Spot) return;

            float clamped = Mathf.Clamp(angle, 1f, 179f);
            if (Mathf.Abs(light.spotAngle - clamped) <= 0.01f) return;

            // HDRP owns the authoring value; going through HDAdditionalLightData keeps the two in sync.
            HDAdditionalLightData hdLight = light.GetComponent<HDAdditionalLightData>();
            if (hdLight != null)
                hdLight.SetSpotAngle(clamped);
            else
                light.spotAngle = clamped;
        }
    }
}
