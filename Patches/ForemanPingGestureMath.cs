using System;

namespace Y4NGZUpgrades.Patches
{
    /// <summary>
    /// Pure timing math for the pointing-layer envelope.  Keeping the curve outside the patch
    /// makes a repeated network point an extension of its current pose rather than a one-frame
    /// jump to a new pose.
    /// </summary>
    internal static class ForemanPingGestureMath
    {
        internal static float Evaluate(float from, float target, float elapsedSeconds, float durationSeconds)
        {
            from = Clamp01(from);
            target = Clamp01(target);
            if (durationSeconds <= 0f || float.IsNaN(durationSeconds) || float.IsInfinity(durationSeconds))
                return target;

            float t = Clamp01(elapsedSeconds / durationSeconds);
            // SmoothStep gives both ends a zero velocity, so this gesture eases into and out of
            // the vanilla emote without changing its activation time or world marker timing.
            t = t * t * (3f - 2f * t);
            return from + (target - from) * t;
        }

        private static float Clamp01(float value)
        {
            if (float.IsNaN(value) || value <= 0f)
                return 0f;
            return value >= 1f || float.IsInfinity(value) ? 1f : value;
        }
    }
}
