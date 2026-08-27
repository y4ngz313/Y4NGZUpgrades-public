using System;
using System.Reflection;

namespace Y4NGZUpgrades.Gui;

// #237. Y4NGZCompany's LocalInputStateWatchdog watches for the vanilla menu latch being
// stranded (quickMenuManager.isMenuOpen left true with no menu on screen) and recovers it.
// Our purchase menu sets that flag deliberately, so from the watchdog's side it is
// indistinguishable from the bug it exists to fix. The watchdog exposes
// PushExternalMenuLatch()/PopExternalMenuLatch() for exactly this: an external owner
// declaring that the latch is legitimately held for now.
//
// Resolved by reflection rather than a direct call even though the build references
// Y4NGZCompany: Upgrades has to load and run with Company absent, and the API is newer than
// the referenced assembly on some profiles. The lookup happens once and is cached, so the
// call sites cost a delegate invoke.
internal static class CompanyInputWatchdogLatch
{
    private const string WatchdogTypeName =
        "Y4NGZCompany.Experience.Diagnostics.LocalInputStateWatchdog, Y4NGZCompany";

    private static bool _resolved;
    private static MethodInfo _push;
    private static MethodInfo _pop;

    // Single-menu ownership guard. Only one purchase menu exists at a time, and the close
    // path can run twice (the AnimateClose clear and the OnDisable safety net), so the pop
    // is gated on actually holding the latch. The watchdog's own counter never goes
    // negative either, but not leaning on that keeps the pairing honest here.
    private static bool _held;

    internal static void Push()
    {
        if (_held) return;
        if (!TryResolve()) return;

        try
        {
            _push.Invoke(null, null);
            _held = true;
        }
        catch { }
    }

    internal static void Pop()
    {
        if (!_held) return;
        _held = false;
        if (!TryResolve()) return;

        try { _pop.Invoke(null, null); }
        catch { }
    }

    private static bool TryResolve()
    {
        if (_resolved) return _push != null && _pop != null;
        _resolved = true;

        try
        {
            Type watchdog = Type.GetType(WatchdogTypeName, throwOnError: false);
            if (watchdog == null) return false;

            _push = watchdog.GetMethod(
                "PushExternalMenuLatch", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            _pop = watchdog.GetMethod(
                "PopExternalMenuLatch", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
        }
        catch
        {
            _push = null;
            _pop = null;
        }

        return _push != null && _pop != null;
    }
}
