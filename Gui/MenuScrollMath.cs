using System;

namespace Y4NGZUpgrades.Gui;

// Input units are supplied by the active input module, never inferred from the
// magnitude of one event (which would turn a fine trackpad gesture into a tick).
internal static class MenuScrollMath
{
    // Win32 WHEEL_DELTA: one mouse-wheel notch as the native Windows backend reports it.
    internal const float WindowsWheelDelta = 120f;

    // Units one wheel notch occupies in the delta the UI module delivers. The UI module
    // multiplies the device delta by its own per-tick scale. Only a runtime that normalizes
    // the platform range (Input System built with platform scroll delta) has already
    // divided by WHEEL_DELTA; LC's Input System 1.14 does not, whatever
    // InputSettings.scrollDeltaBehavior says.
    internal static float UnitsPerTick(float moduleTickScale, bool runtimeNormalizes, bool windowsRange)
    {
        if (float.IsNaN(moduleTickScale) || float.IsInfinity(moduleTickScale) || moduleTickScale <= 0f)
            moduleTickScale = 1f;
        return runtimeNormalizes || !windowsRange ? moduleTickScale : moduleTickScale * WindowsWheelDelta;
    }

    internal static float Ticks(float delta, float unitsPerTick)
    {
        if (float.IsNaN(delta) || float.IsInfinity(delta)) return 0f;
        if (float.IsNaN(unitsPerTick) || float.IsInfinity(unitsPerTick) || unitsPerTick <= 0f)
            unitsPerTick = 1f;
        return Math.Max(-8f, Math.Min(8f, delta / unitsPerTick));
    }
}
