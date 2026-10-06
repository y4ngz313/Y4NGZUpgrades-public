using System;
using GameNetcodeStuff;
using UnityEngine;
using Y4NGZUpgrades.Patches;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Gui;

/// <summary>
/// Read-only live details for the existing upgrade inspector. This class never initializes,
/// advances, resets, or persists ability state: it only describes the state gameplay owns now.
/// </summary>
internal static class AbilityInspectorStatus
{
    internal static string Describe(string upgradeId)
    {
        if (string.IsNullOrWhiteSpace(upgradeId))
            return null;

        string id = upgradeId.Trim();
        string live;
        switch (id.ToLowerInvariant())
        {
            case "adrenaline_rush":
                live = DescribeNineLives();
                break;
            case "lone_wolf":
                live = DescribeLoneWolf();
                break;
            case "buddy_system":
                live = DescribeBuddySystem();
                break;
            case "shadow_step":
                live = DescribeShadowStep();
                break;
            default:
                live = null;
                break;
        }

        string dormant = DescribeDormantOwnership(id);
        if (string.IsNullOrEmpty(dormant))
            return live;

        return string.IsNullOrEmpty(live) ? dormant : live + "\n" + dormant;
    }

    /// <summary>
    /// The rank this save owns in the other progression record, when it owns one (#435). Full
    /// native ranks and unique-only ranks are kept separately and forever: changing
    /// <c>LGU Upgrade Mode</c>, or losing Late Game Upgrades, changes which record is live but
    /// never converts, promotes or refunds the other. Saying so here is the difference between a
    /// player believing their Field Optics ranks were deleted and knowing they are parked.
    /// </summary>
    private static string DescribeDormantOwnership(string upgradeId)
    {
        if (!NativeUpgradeFamilies.TryGet(upgradeId, out NativeUpgradeFamily family))
            return null;

        NativeUpgradeVariant variant = family.UniqueVariant;
        if (variant == null)
            return null;

        switch (Y4NGZUpgradeManager.ModeOf(upgradeId))
        {
            case NativeFamilyMode.UniqueOnly:
            {
                int full = Y4NGZUpgradeManager.GetFullNativeLevel(upgradeId);
                if (full <= 0)
                    return null;
                return "Dormant: full " + FullNativeTitle(upgradeId) + " rank " + full
                    + " kept for native mode; never converted or refunded.";
            }
            case NativeFamilyMode.Full:
            {
                int unique = Y4NGZUpgradeManager.GetUniqueOnlyLevel(upgradeId);
                if (unique <= 0)
                    return null;
                return "Dormant: " + variant.DisplayName + " rank " + unique
                    + " kept for Late Game Upgrades mode; never converted or refunded.";
            }
            default:
                return null;
        }
    }

    private static string FullNativeTitle(string upgradeId)
    {
        return UpgradeCatalogTable.TryGet(upgradeId, out UpgradeCatalogTable.Entry entry)
            ? entry.DisplayName
            : upgradeId;
    }

    private static string DescribeNineLives()
    {
        int tier = NineLivesUpgrade.GetTier();
        if (tier <= 0)
            return null;

        NineLivesState state = NineLivesPatch.State;
        // The shield is sized from the ceiling the damage path actually enforces, which an
        // external health provider can raise, so the readout has to use the same figure or it
        // reports a full shield as partial.
        int capacity = MergedUpgradePatches.GetEffectiveMaxHealth();
        string shield = "Shield " + state.Shield + "/" + capacity;
        int rechargeSeconds = state.Shield < capacity
            ? Mathf.CeilToInt(Mathf.Max(0f, state.RechargeAt - Time.time))
            : 0;
        if (rechargeSeconds > 0)
            shield += " | recharge " + rechargeSeconds + "s";
        if (tier < 2)
            return shield;

        if (state.IsProtected(Time.time))
            return shield + " | death save active";
        return shield + (state.SaveUsed ? " | death save spent" : " | death save ready");
    }

    private static string DescribeLoneWolf()
    {
        if (Y4NGZUpgradeManager.GetExactLevel("lone_wolf") <= 0)
            return null;
        if (LoneWolfUpgrade.LoneWolfActive)
            return "Lone Wolf active";

        PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
        if (!IsLoneWolfEligible(local))
            return "Lone Wolf inactive";

        return HasLoneWolfCrewNearby(local) ? "Crew nearby" : "Isolation arming";
    }

    private static bool IsLoneWolfEligible(PlayerControllerB player)
    {
        return player != null && player.isPlayerControlled && !player.isPlayerDead &&
               !player.isInHangarShipRoom && StartOfRound.Instance != null &&
               !StartOfRound.Instance.inShipPhase;
    }

    private static bool HasLoneWolfCrewNearby(PlayerControllerB local)
    {
        PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
        if (players == null)
            return false;

        const float activationDistance = IsolationState.ActivationDistance;
        for (int i = 0; i < players.Length; i++)
        {
            PlayerControllerB teammate = players[i];
            if (teammate == null || teammate == local || teammate.isPlayerDead ||
                !teammate.isPlayerControlled || teammate.isInsideFactory != local.isInsideFactory)
            {
                continue;
            }
            if (Vector3.Distance(local.transform.position, teammate.transform.position) < activationDistance)
                return true;
        }

        return false;
    }

    private static string DescribeBuddySystem()
    {
        if (!BuddySystemUpgrade.IsUnlocked())
            return null;

        PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
        int activeAuras = ForemanSupportPatch.GetActiveBuddyAuraProviderCount(local);
        return activeAuras == 1
            ? "1 teammate aura active"
            : activeAuras + " teammate auras active";
    }

    private static string DescribeShadowStep()
    {
        if (!ShadowStepUpgrade.HasActiveAbility())
            return null;

        int remainingSeconds = Mathf.CeilToInt(Mathf.Max(
            0f,
            ShadowStepUpgrade.CooldownEndTime - Time.time));
        if (remainingSeconds <= 0)
            return "Cloak ready";
        return ShadowStepUpgrade.IsInvisible
            ? "Cloak active | ready in " + remainingSeconds + "s"
            : "Cloak ready in " + remainingSeconds + "s";
    }
}
