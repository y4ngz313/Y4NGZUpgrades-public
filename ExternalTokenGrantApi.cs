using System;

namespace Y4NGZUpgrades
{
    public readonly struct ExternalTokenGrantRequest
    {
        public readonly int AmountPerPlayer;
        public readonly string Source;
        public readonly string MilestoneId;
        public readonly string ActionId;

        public ExternalTokenGrantRequest(int amountPerPlayer, string source, string milestoneId, string actionId)
        {
            AmountPerPlayer = amountPerPlayer;
            Source = source ?? string.Empty;
            MilestoneId = milestoneId ?? string.Empty;
            ActionId = actionId ?? string.Empty;
        }
    }

    public static class ExternalTokenGrantApi
    {
        /// <summary>
        /// Token-grant contract version this build speaks. Checked at registration time against
        /// <c>Y4NGZCompany.Core.QuotaProgressionApi.TokenGrantProtocolVersion</c>; a Company build
        /// that lacks that constant predates the handshake and is accepted as version 1.
        /// Bump only on a breaking change to the grant request shape.
        /// </summary>
        public const int ProtocolVersion = 1;

        public static bool IsCompanyProviderRegistered =>
            CompanyQuotaTokenIntegration.IsCompanyProviderRegistered;

        public static bool TryGrantConnectedPlayers(ExternalTokenGrantRequest request)
        {
            return CompanyQuotaTokenIntegration.TryGrantConnectedPlayers(request);
        }
    }
}
