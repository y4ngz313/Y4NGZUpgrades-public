using GameNetcodeStuff;
using HarmonyLib;

namespace Y4NGZUpgrades.Gui;

// Blocks the vanilla pause-menu input path while our P-menu is visible.
// The shipped game routes ESC through PlayerControllerB.OpenMenu_performed,
// which calls either OpenQuickMenu or CloseQuickMenu depending on isMenuOpen.
// Our menu sets that vanilla flag, so guard both quick-menu outcomes as well
// as the input callback; neither method owns a direct audio call, making the
// exact close-path UI sound source ambiguous in the decompile. Bug #7/#103.
public class QuickMenuPatch
{
    [HarmonyPatch(typeof(PlayerControllerB), "OpenMenu_performed")]
    class Patch_OpenMenuPerformed
    {
        static bool Prefix() => !MenuController.IsOpen;
    }

    [HarmonyPatch(typeof(QuickMenuManager), nameof(QuickMenuManager.OpenQuickMenu))]
    class Patch_OpenQuickMenu
    {
        static bool Prefix() => !MenuController.IsOpen;
    }

    [HarmonyPatch(typeof(QuickMenuManager), nameof(QuickMenuManager.CloseQuickMenu))]
    class Patch_CloseQuickMenu
    {
        static bool Prefix() => !MenuController.IsOpen;
    }
}
