using System;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;

namespace Y4NGZUpgrades
{
    internal static class CompanyQuotaTokenIntegration
    {
        private const string CompanyApiTypeName = "Y4NGZCompany.Core.QuotaProgressionApi";
        private const string GrantMessageName = "Y4NGZUpgrades.CompanyQuotaTokenGrant.v1";
        private const string CompanyProtocolVersionMember = "TokenGrantProtocolVersion";
        private const int MaxActionIdCharacters = 120;

        /// <summary>
        /// Compatibility rule: a Company build that does not expose
        /// <see cref="CompanyProtocolVersionMember"/> predates the handshake and is treated as
        /// protocol 1. Registration still proceeds against it as long as the request struct carries
        /// the fields this integration reads, which is validated separately.
        /// </summary>
        private const int LegacyCompanyProtocolVersion = 1;

        private static NetworkManager _registeredNetworkManager;
        private static object _companyHandler;
        private static MethodInfo _companyUnregister;
        private static bool _loggedRequestShapeError;

        internal static bool IsCompanyProviderRegistered => _companyHandler != null;

        internal static void EnsureRegistered()
        {
            RegisterNetworkHandler();
            RegisterCompanyHandler();
        }

        internal static void Shutdown()
        {
            UnregisterCompanyHandler();
            UnregisterNetworkHandler();
        }

        private static void RegisterCompanyHandler()
        {
            if (_companyHandler != null)
                return;

            Type apiType = FindType(CompanyApiTypeName);
            if (apiType == null)
                return;

            if (!IsCompanyProtocolCompatible(apiType))
                return;

            MethodInfo register = apiType.GetMethod(
                "RegisterTokenGrantHandler",
                BindingFlags.Public | BindingFlags.Static);
            _companyUnregister = apiType.GetMethod(
                "UnregisterTokenGrantHandler",
                BindingFlags.Public | BindingFlags.Static);
            ParameterInfo[] parameters = register?.GetParameters();
            if (parameters == null || parameters.Length != 1)
                return;

            Type delegateType = parameters[0].ParameterType;
            MethodInfo invoke = delegateType.GetMethod("Invoke");
            ParameterInfo[] invokeParameters = invoke?.GetParameters();
            if (invoke == null || invoke.ReturnType != typeof(bool) || invokeParameters == null || invokeParameters.Length != 1)
                return;

            if (!HasRequiredRequestFields(invokeParameters[0].ParameterType))
                return;

            try
            {
                ParameterExpression request = Expression.Parameter(invokeParameters[0].ParameterType, "request");
                MethodInfo callback = typeof(CompanyQuotaTokenIntegration).GetMethod(
                    nameof(HandleCompanyRequest),
                    BindingFlags.NonPublic | BindingFlags.Static);
                LambdaExpression lambda = Expression.Lambda(
                    delegateType,
                    Expression.Call(callback, Expression.Convert(request, typeof(object))),
                    request);

                _companyHandler = lambda.Compile();
                register.Invoke(null, new[] { _companyHandler });
                Plugin.Log?.LogInfo("[QuotaTokens] Registered Company quota token provider.");
            }
            catch (Exception ex)
            {
                _companyHandler = null;
                _companyUnregister = null;
                Plugin.Log?.LogWarning($"[QuotaTokens] Company provider registration failed: {ex.Message}");
            }
        }

        private static void UnregisterCompanyHandler()
        {
            if (_companyHandler == null)
                return;

            try
            {
                _companyUnregister?.Invoke(null, new[] { _companyHandler });
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[QuotaTokens] Company provider unregister failed: {ex.Message}");
            }

            _companyHandler = null;
            _companyUnregister = null;
        }

        /// <summary>
        /// Reads Company's protocol constant. Missing constant means a pre-handshake Company build,
        /// which is accepted as <see cref="LegacyCompanyProtocolVersion"/>. A present but different
        /// (or non-int) value means the contract moved underneath us: refuse to register rather than
        /// silently reading fields that may no longer mean what we think.
        /// </summary>
        private static bool IsCompanyProtocolCompatible(Type apiType)
        {
            FieldInfo versionField = apiType.GetField(
                CompanyProtocolVersionMember,
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

            int companyVersion;
            if (versionField == null)
            {
                companyVersion = LegacyCompanyProtocolVersion;
            }
            else if (versionField.GetValue(null) is int value)
            {
                companyVersion = value;
            }
            else
            {
                Plugin.Log?.LogWarning(
                    $"[QuotaTokens] Not registering: Company {CompanyProtocolVersionMember} is not an int " +
                    $"(Upgrades protocol {ExternalTokenGrantApi.ProtocolVersion}). Update Y4NGZUpgrades and Y4NGZCompany together.");
                return false;
            }

            if (companyVersion == ExternalTokenGrantApi.ProtocolVersion)
                return true;

            Plugin.Log?.LogWarning(
                $"[QuotaTokens] Not registering: token-grant protocol mismatch " +
                $"(Y4NGZUpgrades {ExternalTokenGrantApi.ProtocolVersion}, Y4NGZCompany {companyVersion}). " +
                "Quota token rewards are disabled until both mods are updated together.");
            return false;
        }

        /// <summary>
        /// Verifies the request struct still carries every field the runtime read path depends on.
        /// Without this, a rename degrades into <c>0</c>/<c>null</c> reads and silently swallowed grants.
        /// </summary>
        private static bool HasRequiredRequestFields(Type requestType)
        {
            string missing = string.Empty;
            missing = AppendMissing(missing, requestType, "CompletedQuota", typeof(int));
            missing = AppendMissing(missing, requestType, "TokensPerPlayer", typeof(int));
            missing = AppendMissing(missing, requestType, "ActionId", typeof(string));
            if (missing.Length == 0)
                return true;

            Plugin.Log?.LogWarning(
                $"[QuotaTokens] Not registering: Company request type '{requestType.FullName}' is missing expected field(s): {missing}. " +
                "Quota token rewards are disabled until both mods are updated together.");
            return false;
        }

        private static string AppendMissing(string missing, Type requestType, string name, Type expected)
        {
            FieldInfo field = requestType.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field != null && field.FieldType == expected)
                return missing;

            string entry = field == null ? name : $"{name} (expected {expected.Name}, found {field.FieldType.Name})";
            return missing.Length == 0 ? entry : missing + ", " + entry;
        }

        private static bool HandleCompanyRequest(object request)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (request == null || network == null || !network.IsServer)
                return false;

            try
            {
                Type requestType = request.GetType();
                if (!TryReadIntField(requestType, request, "CompletedQuota", out int completedQuota) ||
                    !TryReadIntField(requestType, request, "TokensPerPlayer", out int tokensPerPlayer) ||
                    !TryReadStringField(requestType, request, "ActionId", out string actionId))
                    return false;

                // Additive since protocol 1; an older Company simply has no such field.
                bool isReplay = ReadOptionalBoolField(requestType, request, "IsReplay");
                if (completedQuota < 1 || tokensPerPlayer < 1 || string.IsNullOrWhiteSpace(actionId))
                    return false;

                return TryGrantConnectedPlayers(
                    new ExternalTokenGrantRequest(
                        tokensPerPlayer,
                        "Y4NGZCompany",
                        $"quota:{completedQuota}",
                        actionId),
                    isReplay);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[QuotaTokens] Company grant failed: {ex.Message}");
                return false;
            }
        }

        internal static bool TryGrantConnectedPlayers(ExternalTokenGrantRequest request)
        {
            return TryGrantConnectedPlayers(request, false);
        }

        /// <summary>
        /// Host entry point. The fan-out is deliberately unconditional: the host's own ledger may
        /// already hold this action (a replay for a late joiner), but every connected client still
        /// needs the message because each client de-duplicates against its own save ledger in
        /// <see cref="ProgressionManager.TryApplyExternalTokenGrant(string, int, out bool)"/>.
        /// </summary>
        internal static bool TryGrantConnectedPlayers(ExternalTokenGrantRequest request, bool isReplay)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsServer || request.AmountPerPlayer < 1)
                return false;

            string source = string.IsNullOrWhiteSpace(request.Source) ? "external" : request.Source.Trim();
            string actionId = string.IsNullOrWhiteSpace(request.ActionId) ? string.Empty : request.ActionId.Trim();
            string canonicalActionId = source + ":" + actionId;
            int completedQuota = ParseQuota(request.MilestoneId);
            if (actionId.Length == 0)
                return false;

            try
            {
                if (canonicalActionId.Length > MaxActionIdCharacters)
                {
                    Plugin.Log?.LogWarning($"[QuotaTokens] Rejected {source} grant with oversized action ID.");
                    return false;
                }

                bool localApplied = ApplyLocalGrant(completedQuota, request.AmountPerPlayer, canonicalActionId, isReplay);
                CustomMessagingManager messages = network.CustomMessagingManager;
                if (messages == null)
                    return localApplied;

                using (var writer = new FastBufferWriter(256, Allocator.Temp))
                {
                    writer.WriteValueSafe(completedQuota);
                    writer.WriteValueSafe(request.AmountPerPlayer);
                    writer.WriteValueSafe(new FixedString128Bytes(canonicalActionId));

                    foreach (ulong clientId in network.ConnectedClientsIds)
                    {
                        if (clientId == network.LocalClientId)
                            continue;
                        messages.SendNamedMessage(
                            GrantMessageName,
                            clientId,
                            writer,
                            NetworkDelivery.ReliableFragmentedSequenced);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[QuotaTokens] {source} grant failed: {ex.Message}");
                return false;
            }
        }

        private static bool ApplyLocalGrant(int completedQuota, int tokensPerPlayer, string actionId, bool isReplay = false)
        {
            if (!ProgressionManager.TryApplyExternalTokenGrant(actionId, tokensPerPlayer, out bool alreadyApplied))
                return false;

            if (alreadyApplied)
            {
                Plugin.Log?.LogDebug(
                    $"[QuotaTokens] Quota {completedQuota} grant already in this save's ledger ({actionId}); ignoring{(isReplay ? " replay" : string.Empty)}.");
                return true;
            }

            Plugin.Log?.LogInfo(
                $"[QuotaTokens] Quota {completedQuota} grant accepted{(isReplay ? " (replay)" : string.Empty)}: +{tokensPerPlayer} tokens ({actionId}).");
            return true;
        }

        private static void RegisterNetworkHandler()
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network?.CustomMessagingManager == null)
                return;

            if (_registeredNetworkManager == network)
                return;

            UnregisterNetworkHandler();
            try
            {
                network.CustomMessagingManager.RegisterNamedMessageHandler(GrantMessageName, OnGrantMessage);
                _registeredNetworkManager = network;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[QuotaTokens] Network handler registration failed: {ex.Message}");
            }
        }

        private static void UnregisterNetworkHandler()
        {
            if (_registeredNetworkManager?.CustomMessagingManager != null)
            {
                try
                {
                    _registeredNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(GrantMessageName);
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"[QuotaTokens] Network handler unregister failed: {ex.Message}");
                }
            }

            _registeredNetworkManager = null;
        }

        private static void OnGrantMessage(ulong senderClientId, FastBufferReader reader)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || senderClientId != NetworkManager.ServerClientId)
                return;

            try
            {
                reader.ReadValueSafe(out int completedQuota);
                reader.ReadValueSafe(out int tokensPerPlayer);
                reader.ReadValueSafe(out FixedString128Bytes actionId);
                if (completedQuota < 1 || tokensPerPlayer < 1 || actionId.IsEmpty)
                    return;

                ApplyLocalGrant(completedQuota, tokensPerPlayer, actionId.ToString());
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[QuotaTokens] Malformed grant from server: {ex.Message}");
            }
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type type = assembly.GetType(fullName, false);
                    if (type != null)
                        return type;
                }
                catch
                {
                }
            }

            return null;
        }

        private static bool TryReadIntField(Type type, object instance, string name, out int value)
        {
            value = 0;
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field?.GetValue(instance) is int found)
            {
                value = found;
                return true;
            }

            LogRequestShapeError(type, name, "int");
            return false;
        }

        private static bool TryReadStringField(Type type, object instance, string name, out string value)
        {
            value = string.Empty;
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field != null && field.FieldType == typeof(string))
            {
                value = field.GetValue(instance) as string ?? string.Empty;
                return true;
            }

            LogRequestShapeError(type, name, "string");
            return false;
        }

        private static bool ReadOptionalBoolField(Type type, object instance, string name)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return field?.GetValue(instance) is bool value && value;
        }

        /// <summary>
        /// A required field vanished after registration validated the contract. Say so loudly once
        /// instead of degrading to a zero-valued read that looks like "no reward configured".
        /// </summary>
        private static void LogRequestShapeError(Type type, string name, string expected)
        {
            if (_loggedRequestShapeError)
                return;

            _loggedRequestShapeError = true;
            Plugin.Log?.LogError(
                $"[QuotaTokens] Company request type '{type.FullName}' has no readable {expected} field '{name}'. " +
                $"Quota token grants are being dropped; Y4NGZUpgrades expects token-grant protocol {ExternalTokenGrantApi.ProtocolVersion}. " +
                "Update Y4NGZUpgrades and Y4NGZCompany together.");
        }

        private static int ParseQuota(string milestoneId)
        {
            if (string.IsNullOrWhiteSpace(milestoneId))
                return 0;

            int separator = milestoneId.LastIndexOf(':');
            string value = separator >= 0 ? milestoneId.Substring(separator + 1) : milestoneId;
            return int.TryParse(value, out int quota) ? Math.Max(0, quota) : 0;
        }

        [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.Awake))]
        private static class StartOfRoundAwakePatch
        {
            private static void Postfix() => EnsureRegistered();
        }

        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        private static class DisconnectPatch
        {
            private static void Prefix() => Shutdown();
        }
    }
}
