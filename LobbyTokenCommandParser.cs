using System;

namespace Y4NGZUpgrades
{
    internal enum LobbyTokenCommandKind
    {
        NotCommand,
        Usage,
        InvalidAmount,
        Set
    }

    internal readonly struct LobbyTokenCommand
    {
        internal LobbyTokenCommandKind Kind { get; }
        internal int Amount { get; }

        internal LobbyTokenCommand(LobbyTokenCommandKind kind, int amount = 0)
        {
            Kind = kind;
            Amount = amount;
        }
    }

    internal static class LobbyTokenCommandParser
    {
        internal const int MaximumTokenBalance = 999999;

        internal static LobbyTokenCommand Parse(string input)
        {
            string normalized = (input ?? string.Empty).Trim();
            if (normalized.Length == 0)
                return new LobbyTokenCommand(LobbyTokenCommandKind.NotCommand);

            string[] parts = normalized.Split(
                new[] { ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0
                || !string.Equals(parts[0], "settokens", StringComparison.OrdinalIgnoreCase))
            {
                return new LobbyTokenCommand(LobbyTokenCommandKind.NotCommand);
            }

            if (parts.Length != 2)
                return new LobbyTokenCommand(LobbyTokenCommandKind.Usage);

            if (!int.TryParse(parts[1], out int amount)
                || amount < 0
                || amount > MaximumTokenBalance)
            {
                return new LobbyTokenCommand(LobbyTokenCommandKind.InvalidAmount);
            }

            return new LobbyTokenCommand(LobbyTokenCommandKind.Set, amount);
        }
    }
}
