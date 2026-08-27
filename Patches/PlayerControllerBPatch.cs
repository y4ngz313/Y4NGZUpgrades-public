using System.Collections;
using System.Collections.Generic;
using GameNetcodeStuff;
using HarmonyLib;
using Y4NGZUpgrades.Upgrades;
using UnityEngine;

namespace Y4NGZUpgrades.Patches
{
    [HarmonyPatch(typeof(PlayerControllerB))]
    internal static class PlayerControllerBPatch
    {
        // -
        //  SALVAGER - Death item glow
        // -

        /// <summary>
        /// Flag to track when items are being dropped due to a player death.
        /// Set in KillPlayer prefix (owner) and KillPlayerClientRpc prefix (non-owner).
        /// </summary>
        private static bool isDeathDrop = false;
        private static readonly List<GrabbableObject> capturedDeathItems = new List<GrabbableObject>();

        [HarmonyPrefix]
        [HarmonyPatch("KillPlayer")]
        private static void KillPlayerPrefix()
        {
            isDeathDrop = true;
        }

        [HarmonyPostfix]
        [HarmonyPatch("KillPlayer")]
        private static void KillPlayerPostfix()
        {
            isDeathDrop = false;
        }

        [HarmonyPrefix]
        [HarmonyPatch("KillPlayerClientRpc")]
        private static void KillPlayerClientRpcPrefix()
        {
            isDeathDrop = true;
        }

        /// <summary>
        /// After a player death RPC is processed:
        /// 1) Lone Wolf - check if local player is the last survivor
        /// 2) Salvager - glow items near the death position
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch("KillPlayerClientRpc")]
        private static void KillPlayerClientRpcPostfix(PlayerControllerB __instance, int playerId)
        {
            isDeathDrop = false;

            // --- Lone Wolf check ---
            LoneWolfUpgrade.CheckAndActivate();

            // --- Salvager glow ---
            if (Y4NGZUpgradeState.GetActiveUpgrade(SalvagerUpgrade.UPGRADE_NAME))
            {
                PlayerControllerB deadPlayer = StartOfRound.Instance.allPlayerScripts[playerId];
                Vector3 deathPos = deadPlayer.transform.position;
                GameNetworkManager.Instance.StartCoroutine(AddDeathItemGlow(deathPos));
            }
        }

        /// <summary>
        /// Captures items about to be dropped during a death sequence.
        /// We record items held by the dying player so we can add glow to exactly those items.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch("DropAllHeldItems")]
        private static void DropAllHeldItemsPrefix(PlayerControllerB __instance)
        {
            capturedDeathItems.Clear();
            if (!isDeathDrop) return;
            if (!Y4NGZUpgradeState.GetActiveUpgrade(SalvagerUpgrade.UPGRADE_NAME)) return;

            for (int i = 0; i < __instance.ItemSlots.Length; i++)
            {
                if (__instance.ItemSlots[i] != null)
                    capturedDeathItems.Add(__instance.ItemSlots[i]);
            }
            // ItemSlots already covers all held items in this game version
        }

        /// <summary>
        /// After items are dropped during death, attach a soft glow light to each one.
        /// Since this runs locally on each client, the lights are only visible to players
        /// who have the Salvager upgrade on THEIR client.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch("DropAllHeldItems")]
        private static void DropAllHeldItemsPostfix()
        {
            if (capturedDeathItems.Count == 0) return;

            foreach (GrabbableObject item in capturedDeathItems)
            {
                if (item != null && item.gameObject.GetComponent<SalvagerGlow>() == null)
                {
                    SalvagerGlow glow = item.gameObject.AddComponent<SalvagerGlow>();
                    glow.Initialize();
                }
            }

            Plugin.Log.LogDebug($"Salvager: Added glow to {capturedDeathItems.Count} death-dropped items.");
            capturedDeathItems.Clear();
        }

        /// <summary>
        /// Fallback: add glow to items near death position after a short delay.
        /// This catches items that were synced from the server rather than dropped locally.
        /// </summary>
        private static IEnumerator AddDeathItemGlow(Vector3 deathPosition)
        {
            yield return new WaitForSeconds(0.8f);

            if (!Y4NGZUpgradeState.GetActiveUpgrade(SalvagerUpgrade.UPGRADE_NAME)) yield break;

            GrabbableObject[] allItems = Object.FindObjectsOfType<GrabbableObject>();
            int count = 0;
            foreach (GrabbableObject item in allItems)
            {
                if (item == null || item.isHeld || item.isInShipRoom) continue;
                if (Vector3.Distance(item.transform.position, deathPosition) > 12f) continue;
                if (item.gameObject.GetComponent<SalvagerGlow>() != null) continue;

                SalvagerGlow glow = item.gameObject.AddComponent<SalvagerGlow>();
                glow.Initialize();
                count++;
            }

            if (count > 0)
                Plugin.Log.LogDebug($"Salvager fallback: Added glow to {count} items near death position.");
        }

        // -
        //  LONE WOLF - Stamina bonus + ship deactivation
        // -

        /// <summary>
        /// Every frame: apply Lone Wolf stamina bonus (level 2+) and
        /// deactivate Lone Wolf when the player enters the ship.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch("Update")]
        private static void UpdatePostfix(PlayerControllerB __instance)
        {
            if (!__instance.IsOwner || !__instance.isPlayerControlled) return;
            if (__instance != GameNetworkManager.Instance.localPlayerController) return;

            if (!LoneWolfUpgrade.LoneWolfActive) return;

            // Deactivate when entering the ship
            if (__instance.isInHangarShipRoom)
            {
                LoneWolfUpgrade.Deactivate();
                return;
            }

            // Apply stamina bonus for level 2+
            LoneWolfUpgrade.ApplyStaminaBonus(__instance);
        }
    }

    // -
    //  LONE WOLF - Round end cleanup
    // -

    [HarmonyPatch(typeof(StartOfRound))]
    internal static class StartOfRoundPatch
    {
        /// <summary>
        /// When the ship leaves the moon, deactivate Lone Wolf.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch("ShipLeave")]
        private static void ShipLeavePostfix()
        {
            LoneWolfUpgrade.Deactivate();
        }

        /// <summary>
        /// When players are revived (new round), deactivate Lone Wolf.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch("ReviveDeadPlayers")]
        private static void ReviveDeadPlayersPostfix()
        {
            LoneWolfUpgrade.Deactivate();
        }
    }

    // -
    //  SALVAGER - Glow component
    // -

    /// <summary>
    /// MonoBehaviour attached to death-dropped items to give them a soft glow.
    /// The light is created locally so it's only visible to the client with Salvager.
    /// Automatically removes itself if the item is picked up.
    /// </summary>
    internal class SalvagerGlow : MonoBehaviour
    {
        private Light glowLight;
        private GrabbableObject item;
        private float startTime;

        public void Initialize()
        {
            item = GetComponent<GrabbableObject>();
            startTime = Time.time;

            glowLight = gameObject.AddComponent<Light>();
            glowLight.type = LightType.Point;
            glowLight.color = new Color(0.3f, 0.9f, 0.5f); // soft green
            glowLight.intensity = 3.5f;
            glowLight.range = 7f;
            glowLight.shadows = LightShadows.None;
        }

        private void Update()
        {
            // Remove glow when item is picked up or enters the ship
            if (item != null && (item.isHeld || item.isInShipRoom))
            {
                if (glowLight != null) Destroy(glowLight);
                Destroy(this);
            }

            if (glowLight != null)
            {
                float pulse = 0.55f + 0.45f * Mathf.Sin((Time.time - startTime) * 3.2f);
                glowLight.intensity = Mathf.Lerp(2.0f, 5.0f, pulse);
            }
        }

        private void OnDestroy()
        {
            if (glowLight != null) Destroy(glowLight);
        }
    }
}
