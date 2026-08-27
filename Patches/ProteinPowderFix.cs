using GameNetcodeStuff;
using HarmonyLib;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Patches
{
    [HarmonyPatch]
    internal static class ProteinPowderFix
    {
        private const string ProteinPowderName = "Pumping Iron";

        [HarmonyPatch(typeof(EnemyAI), "HitEnemyOnLocalClient")]
        [HarmonyPrefix]
        private static void PreHitEnemyOnLocalClient(ref int force, PlayerControllerB playerWhoHit)
        {
            if (force <= 0)
                return;

            if (playerWhoHit == null || playerWhoHit != GameNetworkManager.Instance?.localPlayerController)
                return;

            if (!Y4NGZUpgradeState.GetActiveUpgrade(ProteinPowderName))
                return;

            GrabbableObject held = playerWhoHit.currentlyHeldObjectServer;
            if (held == null)
                return;

            // Before the classifier below, deliberately: this runs on every local melee hit, and the
            // tier read is a dictionary lookup while IsArmoryWeapon allocates a GetComponents array
            // and scans type names. An untiered upgrade has nothing to add either way.
            int tier = Y4NGZUpgradeState.GetTier(ProteinPowderName);
            if (tier <= 0)
                return;

            // MELEE ONLY (#286). Pumping Iron adds a FLAT +tier to whatever force the hit carried,
            // which is a sane 20-60% on a shovel (vanilla force 1, and the swing is the whole risk).
            // On gunfire it is not: since enemy damage became a directly authored force, an armoury
            // weapon lands 1 or 2 per round, so the same flat bonus is a 2x-3x multiplier applied at
            // range, per pellet, from behind cover - it would make a tier-3 Assault Rifle hit four
            // times as hard as an untiered one for a upgrade that is about swinging harder.
            //
            // The classifier, not a type reference: Y4NGZUpgrades holds no compile-time link to
            // Better Armory (#266/#267), so this asks ArmoryItemProbe the same way the stow service
            // and the burning patch do. It matches on the weapons' preserved
            // Y4NGZUpgrades.Weapons namespace, which covers every armoury gun including the flame
            // thrower, and leaves every vanilla and third-party melee weapon buffed exactly as before.
            if (ArmoryItemProbe.IsArmoryWeapon(held))
                return;

            int original = force;
            force += tier;

            Plugin.Log?.LogDebug($"ProteinPowderFix: buffed {held.GetType().Name} hit from {original} to {force}.");
        }
    }
}
