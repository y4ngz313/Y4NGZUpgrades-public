using System;
using System.Reflection;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Patches
{
    /// <summary>
    /// Pumping Iron (id protein_powder, Enforcer tier 2) melee wiring (#364).
    ///
    /// Two sub-tiers:
    ///   Level 1 - +1 force on a held melee weapon hit.
    ///   Level 2 - keeps the +1 and adds a 25% chance per landed hit to stagger the
    ///             struck enemy for one second.
    ///
    /// In LGU-preferred mode (#435) the row sells only the stagger: Late Game Upgrades' own
    /// Protein Powder is the melee-strength training, so a stagger-eligible hit must reach the
    /// postfix without the native force addition.
    ///
    /// The stagger reuses <see cref="EnemyStunRelay.StunEnemy"/> rather than
    /// duplicating the named-message relay: EnemyAI behaviour runs on the host, so a
    /// client-only SetEnemyStunned would change nothing about the enemy's movement.
    /// </summary>
    [HarmonyPatch]
    internal static class ProteinPowderFix
    {
        // One-shot fail-open audit: if Better Armory is loaded but nothing in the process
        // carries the weapon namespace ArmoryItemProbe matches on, the exclusion below is
        // silently inert and every armoury gun is being buffed like a shovel.
        private static bool _armoryProbeAudited;

        [HarmonyPatch(typeof(EnemyAI), "HitEnemyOnLocalClient")]
        [HarmonyPrefix]
        private static void PreHitEnemyOnLocalClient(
            ref int force, PlayerControllerB playerWhoHit, out bool __state)
        {
            // __state carries "this hit is eligible for the tier-2 stagger roll" to the
            // postfix, so the gating below (including the allocating armoury probe) runs
            // exactly once per hit.
            __state = false;

            if (force <= 0)
                return;

            if (playerWhoHit == null || playerWhoHit != GameNetworkManager.Instance?.localPlayerController)
                return;

            GrabbableObject held = playerWhoHit.currentlyHeldObjectServer;
            if (held == null)
                return;

            // Before the classifier below, deliberately: this runs on every local melee hit, and
            // the capability reads are dictionary lookups while IsArmoryWeapon allocates a
            // GetComponents array and scans type names. An upgrade with nothing to add to this
            // hit has nothing to classify either.
            bool addsForce = ProteinPowderUpgrade.HasForceBonus();
            bool rollsStagger = ProteinPowderUpgrade.HasStagger();
            if (!addsForce && !rollsStagger)
                return;

            // MELEE ONLY (#286). Pumping Iron adds a FLAT +1 to whatever force the hit carried,
            // which is a sane 100% on a shovel (vanilla force 1, and the swing is the whole risk).
            // On gunfire it is not: since enemy damage became a directly authored force, an armoury
            // weapon lands 1 or 2 per round, so the same flat bonus is a 2x multiplier applied at
            // range, per pellet, from behind cover - for an upgrade that is about swinging harder.
            // The stagger carries the same exclusion, so this gate precedes both halves.
            //
            // The classifier, not a type reference: Y4NGZUpgrades holds no compile-time link to
            // Better Armory (#266/#267), so this asks ArmoryItemProbe the same way the stow service
            // and the burning patch do. It matches on the weapons' preserved
            // Y4NGZUpgrades.Weapons namespace, which covers every armoury gun including the flame
            // thrower, and leaves every vanilla and third-party melee weapon buffed exactly as before.
            AuditArmoryProbeOnce();
            if (ArmoryItemProbe.IsArmoryWeapon(held))
                return;

            __state = rollsStagger;
            if (!addsForce)
                return;

            int original = force;
            force += ProteinPowderUpgrade.MELEE_FORCE_BONUS;

            Plugin.Log?.LogDebug($"ProteinPowderFix: buffed {held.GetType().Name} hit from {original} to {force}.");
        }

        /// <summary>
        /// Tier 2 stagger. Rolled in the postfix so the hit has already resolved, and only reached
        /// for a hit the prefix actually buffed.
        ///
        /// F-ENF-15: the isEnemyDead guard below does NOT mean "an enemy the swing killed is never
        /// staggered". In v81 HitEnemyOnLocalClient calls HitEnemy locally and then
        /// HitEnemyServerRpc; death is resolved by KillEnemyOnOwnerClient, so on a non-owning client
        /// isEnemyDead is usually still false here. The promise is actually kept host-side, where
        /// EnemyStunRelay's host handler re-checks isEnemyDead and discards the relayed stun.
        /// </summary>
        [HarmonyPatch(typeof(EnemyAI), "HitEnemyOnLocalClient")]
        [HarmonyPostfix]
        private static void PostHitEnemyOnLocalClient(
            EnemyAI __instance, PlayerControllerB playerWhoHit, bool __state)
        {
            if (!__state || __instance == null || __instance.isEnemyDead)
                return;

            if (UnityEngine.Random.value > ProteinPowderUpgrade.STUN_CHANCE)
                return;

            EnemyStunRelay.StunEnemy(__instance, ProteinPowderUpgrade.STUN_SECONDS, playerWhoHit);
        }

        // Fail-open guard (#364 P3). ArmoryItemProbe answers per item, so "it never matches"
        // is indistinguishable from "this item is a shovel" at a call site. Checking the
        // namespace exists at all, once, is the only place the difference is visible.
        private static void AuditArmoryProbeOnce()
        {
            if (_armoryProbeAudited)
                return;

            _armoryProbeAudited = true;

            if (!OptionalPluginCapabilities.BetterArmory)
                return;

            if (AnyArmoryWeaponTypeLoaded())
                return;

            Plugin.Log?.LogWarning(
                "ProteinPowderFix: Better Armory is loaded but no Y4NGZUpgrades.Weapons type is present; "
                + "the armoury-weapon exclusion cannot fire and armoury weapons will receive the melee bonus.");
        }

        private static bool AnyArmoryWeaponTypeLoaded()
        {
            const string weaponNamespace = "Y4NGZUpgrades.Weapons";

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type[] types;
                try
                {
                    types = assemblies[i].GetTypes();
                }
                catch (ReflectionTypeLoadException partial)
                {
                    types = partial.Types;
                }
                catch (Exception)
                {
                    continue;
                }

                if (types == null)
                    continue;

                for (int t = 0; t < types.Length; t++)
                {
                    string ns = types[t]?.Namespace;
                    if (ns != null && ns.StartsWith(weaponNamespace, StringComparison.Ordinal))
                        return true;
                }
            }

            return false;
        }
    }
}
