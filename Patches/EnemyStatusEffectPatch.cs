using GameNetcodeStuff;
using HarmonyLib;
using Y4NGZUpgrades.Effects;

namespace Y4NGZUpgrades.Patches
{
    /// <summary>
    /// F-ENF-14: despite the generic name, this class is BURNING-ONLY. Its
    /// <c>HitEnemyOnLocalClient</c> postfix reads <c>force</c> solely to early-out on zero and does
    /// nothing but apply flamethrower burning. Pumping Iron's melee force bonus and its tier-2
    /// stagger live in <see cref="ProteinPowderFix"/>, which reaches
    /// <see cref="EnemyStunRelay.StunEnemy"/>; nothing here touches either.
    ///
    /// Worth recording because three separate patch classes now postfix the same
    /// <c>EnemyAI.HitEnemyOnLocalClient</c> - <see cref="ProteinPowderFix"/>, this one, and
    /// BetterArmory's ArmoryCombatReticlePatch - with no shared ordering between them. A rename to
    /// BurningStatusPatch, or one dispatcher with explicit ordering, is the real fix; both are out
    /// of scope here because the type name is referenced from the BetterArmory side.
    /// </summary>
    [HarmonyPatch]
    internal static class EnemyStatusEffectPatch
    {
        [HarmonyPatch(typeof(PlayerControllerB), "ConnectClientToPlayerObject")]
        [HarmonyPostfix]
        private static void PostConnectClientToPlayerObject()
        {
            EnemyStatusEffects.RegisterNetworkHandlers();
            // FlamethrowerStreamDriver registers its own handlers from BetterArmory's
            // WeaponPatches postfix on this same method now (#266).
        }

        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        [HarmonyPostfix]
        private static void PostDisconnect()
        {
            EnemyStatusEffects.ResetNetworkHandlers();
        }

        [HarmonyPatch(typeof(EnemyAI), "HitEnemyOnLocalClient")]
        [HarmonyPostfix]
        private static void PostHitEnemyOnLocalClient(EnemyAI __instance, int force, PlayerControllerB playerWhoHit)
        {
            if (__instance == null || force == 0) return;

            PlayerControllerB localPlayer = GameNetworkManager.Instance?.localPlayerController;
            bool vanillaLocalHit = localPlayer != null && playerWhoHit == localPlayer;
            bool holdingFlamethrower = localPlayer != null && IsHoldingFlamethrower(localPlayer);
            bool nullHitterFlamethrowerHit = playerWhoHit == null && holdingFlamethrower;
            bool isLocalHit = vanillaLocalHit || nullHitterFlamethrowerHit;
            LogHitDecision(__instance, force, playerWhoHit, localPlayer, isLocalHit, holdingFlamethrower);

            if (Plugin.BurningStatusEnabled?.Value == false || Plugin.BurningFromFlamethrowerEnabled?.Value == false) return;
            if (!isLocalHit) return;
            if (!holdingFlamethrower) return;

            LogBurnDebug($"ApplyBurning fired for enemy='{__instance.gameObject.name}' netId={__instance.NetworkObjectId} force={force}.");
            EnemyStatusEffects.ApplyBurning(__instance, localPlayer);
        }

        private static bool IsHoldingFlamethrower(PlayerControllerB player)
        {
            if (player == null) return false;

            GrabbableObject held = player.currentlyHeldObjectServer;
            if (held == null) return false;

            // Better Armory's own flamethrower applies burning on its
            // own path, so this generic name heuristic must keep skipping it or the enemy burns
            // twice. The type check became a full-type-name scan when the weapons moved out.
            if (ArmoryItemProbe.IsArmoryWeapon(held)) return false;

            if (NameSuggestsFlamethrower(held.GetType().Name)) return true;
            if (NameSuggestsFlamethrower(held.name)) return true;

            Item item = held.itemProperties;
            if (item == null) return false;

            return NameSuggestsFlamethrower(item.itemName)
                   || NameSuggestsFlamethrower(item.name);
        }

        private static bool NameSuggestsFlamethrower(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;

            string lower = value.ToLowerInvariant();
            return lower.Contains("flamethrower")
                   || lower.Contains("flame thrower")
                   || lower.Contains("flame_thrower")
                   || lower.Contains("flame-thrower");
        }

        private static void LogHitDecision(
            EnemyAI enemy,
            int force,
            PlayerControllerB playerWhoHit,
            PlayerControllerB localPlayer,
            bool isLocalHit,
            bool holdingFlamethrower)
        {
            if (!ShouldLogBurnDebug()) return;

            GrabbableObject held = localPlayer != null ? localPlayer.currentlyHeldObjectServer : null;
            Item item = held != null ? held.itemProperties : null;
            string heldType = held != null ? held.GetType().FullName : "<null>";
            string heldName = held != null ? held.name : "<null>";
            string itemName = item != null ? item.itemName : "<null>";
            string itemObjectName = item != null ? item.name : "<null>";
            string hitterId = playerWhoHit != null ? playerWhoHit.playerClientId.ToString() : "<null>";
            string localId = localPlayer != null ? localPlayer.playerClientId.ToString() : "<null>";

            LogBurnDebug(
                $"HitEnemyOnLocalClient enemy='{enemy.gameObject.name}' netId={enemy.NetworkObjectId} force={force} " +
                $"hitter={hitterId} local={localId} isLocalHit={isLocalHit} " +
                $"burningEnabled={Plugin.BurningStatusEnabled?.Value ?? true} flamethrowerEnabled={Plugin.BurningFromFlamethrowerEnabled?.Value ?? true} " +
                $"heldType='{heldType}' heldName='{heldName}' itemName='{itemName}' itemObjectName='{itemObjectName}' " +
                $"holdingFlamethrower={holdingFlamethrower}.");
        }

        private static bool ShouldLogBurnDebug()
        {
            return Plugin.BurningDebugLogging != null && Plugin.BurningDebugLogging.Value;
        }

        private static void LogBurnDebug(string message)
        {
            if (!ShouldLogBurnDebug()) return;
            Plugin.Log?.LogInfo($"[BurnDebug] {message}");
        }
    }
}
