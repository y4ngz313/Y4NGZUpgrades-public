using HarmonyLib;
using UnityEngine;

namespace Y4NGZUpgrades
{
    [HarmonyPatch(typeof(Terminal), "ParsePlayerSentence")]
    internal static class ProgressionTokenTerminalPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Terminal __instance, ref TerminalNode __result)
        {
            LobbyTokenCommand command = LobbyTokenCommandParser.Parse(GetInput(__instance));
            if (command.Kind == LobbyTokenCommandKind.NotCommand)
                return true;

            if (command.Kind == LobbyTokenCommandKind.Usage)
            {
                __result = CreateNode("Usage: settokens <amount>\nExample: settokens 1000");
                return false;
            }
            if (command.Kind == LobbyTokenCommandKind.InvalidAmount)
            {
                __result = CreateNode(
                    $"Token amount must be a whole number from 0 to {LobbyTokenCommandParser.MaximumTokenBalance}.");
                return false;
            }

            if (LobbyTokenCommandNetwork.TrySetLobbyTokens(
                command.Amount,
                out int connectedEmployees,
                out string error))
            {
                string employeeWord = connectedEmployees == 1 ? "employee" : "employees";
                __result = CreateNode(
                    $"Tokens set to {command.Amount} for {connectedEmployees} connected {employeeWord}.");
            }
            else
            {
                __result = CreateNode(error);
            }

            return false;
        }

        private static string GetInput(Terminal terminal)
        {
            string text = terminal?.screenText?.text ?? string.Empty;
            int lastNewline = text.LastIndexOf('\n');
            return lastNewline >= 0
                ? text.Substring(lastNewline + 1).Trim()
                : text.Trim();
        }

        private static TerminalNode CreateNode(string text)
        {
            TerminalNode node = ScriptableObject.CreateInstance<TerminalNode>();
            node.displayText = (text ?? string.Empty) + "\n\n";
            node.clearPreviousText = true;
            return node;
        }
    }
}
