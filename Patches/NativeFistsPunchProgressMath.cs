using System;

namespace Y4NGZUpgrades.Patches;

/// <summary>Pure elapsed-time normalization for a continuing authored fist punch.</summary>
internal static class NativeFistsPunchProgressMath
{
    internal static float Evaluate(float elapsedSeconds, float cadenceSeconds)
    {
        if (cadenceSeconds <= 0f || float.IsNaN(cadenceSeconds) || float.IsInfinity(cadenceSeconds))
            return 1f;
        if (float.IsNaN(elapsedSeconds) || elapsedSeconds <= 0f)
            return 0f;
        if (float.IsInfinity(elapsedSeconds) || elapsedSeconds >= cadenceSeconds)
            return 1f;
        return elapsedSeconds / cadenceSeconds;
    }
}
