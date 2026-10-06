using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Y4NGZUpgrades.Upgrades;

namespace Y4NGZUpgrades.Gui;

// Uses the game's existing temporary tip, never a persistent upgrade HUD object.
internal static class AbilityControlTips
{
    private static readonly Queue<(string Scope, string Id)> Pending = new Queue<(string, string)>();
    private static readonly HashSet<string> Queued = new HashSet<string>();
    private static Coroutine _routine;

    internal static string Instruction(string id)
    {
        if (id == "ping") return "Ping: [" + UpgradeInput.DisplayLabel(Plugin.Keybinds?.ForemanPing, "Q") + "]";
        if (id == "shadow_step") return "Cloak: [" + UpgradeInput.DisplayLabel(Plugin.Keybinds?.ShadowStep, "X") + "]";
        return null;
    }

    internal static void Queue(string id)
    {
        if (Instruction(id) == null || !SaveKey.TryGetCurrent(out string scope)) return;
        if (!Queued.Add(scope + "|" + id)) return;
        Pending.Enqueue((scope, id));
        if (_routine == null) _routine = Y4NGZPersistentRunner.Run(ShowQueued());
    }

    private static IEnumerator ShowQueued()
    {
        // Let the caller store its coroutine handle before the queue can finish.
        yield return null;
        while (Pending.Count > 0)
        {
            var tip = Pending.Peek();
            if (!SaveKey.TryGetCurrent(out string scope) || scope != tip.Scope)
            {
                Pending.Dequeue();
                continue;
            }
            var player = StartOfRound.Instance?.localPlayerController;
            if (player == null || player.isPlayerDead || MenuController.IsOpen || player.inTerminalMenu ||
                (player.quickMenuManager != null && player.quickMenuManager.isMenuOpen) || HUDManager.Instance == null)
            {
                yield return null;
                continue;
            }
            Pending.Dequeue();
            if (Y4NGZUpgradeManager.GetLevel(tip.Id) > 0)
                HUDManager.Instance.DisplayTip("UPGRADE", Instruction(tip.Id), isWarning: false);
            yield return new WaitForSecondsRealtime(5f);
        }
        _routine = null;
    }
}
