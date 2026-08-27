using System;
using System.Reflection;
using Unity.Netcode;

namespace Y4NGZUpgrades
{
    internal static class SaveKey
    {
        private const string SaveIdentityKey = "Y4NGZUpgrades_SaveIdentity";
        private static readonly string[] ManagedSaveFiles =
        {
            "LCSaveFile1",
            "LCSaveFile2",
            "LCSaveFile3"
        };

        private static readonly PendingSaveIdentityStore PendingIdentities = new PendingSaveIdentityStore();

        private static Type _es3Type;
        private static MethodInfo _es3FileExists;
        private static MethodInfo _es3KeyExists;
        private static MethodInfo _es3LoadWithDefault;
        private static MethodInfo _es3SaveObject;

        private enum IdentityReadStatus
        {
            Unknown,
            MissingFile,
            PresentWithoutIdentity,
            PresentWithIdentity
        }

        internal static bool TryGetCurrent(out string key)
        {
            return TryGetCurrentInternal(out key, allowSlotFallback: false, createIdentity: true);
        }

        internal static bool TryGetCurrentOrSlot(out string key)
        {
            return TryGetCurrentInternal(out key, allowSlotFallback: true, createIdentity: true);
        }

        internal static bool TryGetCurrentExistingOrSlot(out string key)
        {
            return TryGetCurrentInternal(out key, allowSlotFallback: true, createIdentity: false);
        }

        internal static void ClearPendingIdentities()
        {
            PendingIdentities.Clear();
        }

        internal static bool TryGetExistingForSaveFile(string saveFileName, out string key)
        {
            key = null;
            if (ReadSaveIdentity(saveFileName, out string identity) != IdentityReadStatus.PresentWithIdentity)
                return false;

            key = BuildKey(saveFileName, identity);
            return true;
        }

        internal static bool TrySaveFileExists(string saveFileName, out bool exists)
        {
            exists = false;
            if (string.IsNullOrWhiteSpace(saveFileName) || !ResolveEs3())
                return false;

            try
            {
                exists = (bool)_es3FileExists.Invoke(null, new object[] { saveFileName.Trim() });
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning(
                    $"[Y4NGZUpgrades] Could not test save file '{saveFileName}': {UnwrapMessage(ex)}");
                return false;
            }
        }

        internal static SaveSlotSnapshot[] ReadManagedSlotSnapshots()
        {
            var snapshots = new SaveSlotSnapshot[ManagedSaveFiles.Length];
            for (int i = 0; i < ManagedSaveFiles.Length; i++)
            {
                string saveFileName = ManagedSaveFiles[i];
                IdentityReadStatus status = ReadSaveIdentity(saveFileName, out string identity);
                switch (status)
                {
                    case IdentityReadStatus.MissingFile:
                        snapshots[i] = new SaveSlotSnapshot(
                            saveFileName,
                            statusKnown: true,
                            fileExists: false,
                            liveKey: null);
                        break;
                    case IdentityReadStatus.PresentWithoutIdentity:
                        snapshots[i] = new SaveSlotSnapshot(
                            saveFileName,
                            statusKnown: true,
                            fileExists: true,
                            liveKey: null);
                        break;
                    case IdentityReadStatus.PresentWithIdentity:
                        snapshots[i] = new SaveSlotSnapshot(
                            saveFileName,
                            statusKnown: true,
                            fileExists: true,
                            liveKey: BuildKey(saveFileName, identity));
                        break;
                    default:
                        snapshots[i] = new SaveSlotSnapshot(
                            saveFileName,
                            statusKnown: false,
                            fileExists: false,
                            liveKey: null);
                        break;
                }
            }
            return snapshots;
        }

        private static bool TryGetCurrentInternal(
            out string key,
            bool allowSlotFallback,
            bool createIdentity)
        {
            key = null;

            // Joined to a remote host: persist under the HOST's identity, never the local one.
            // Pending means the host identity has not arrived yet - report "no save" so nothing is
            // read from or written to this client's own identity-keyed state in the meantime.
            switch (HostSaveIdentity.Resolve(out string hostKey))
            {
                case HostSaveScope.UseHostKey:
                    key = hostKey;
                    return true;
                case HostSaveScope.Pending:
                    return false;
            }

            if (!TryGetBaseSaveFileName(out string baseName, allowSlotFallback))
                return false;

            IdentityReadStatus status = ReadSaveIdentity(baseName, out string identity);
            if (status == IdentityReadStatus.PresentWithIdentity)
            {
                PendingIdentities.MarkPersisted(baseName);
                key = BuildKey(baseName, identity);
                return true;
            }

            if (!createIdentity)
                return false;

            // A newly hosted slot has an authoritative runtime session before vanilla writes its
            // ES3 file. Reserve a stable identity for that narrow window without forcing the broad
            // vanilla SaveGame path or allowing stale menu state to resurrect a deleted slot.
            if (status == IdentityReadStatus.MissingFile && IsActiveAuthoritativeSession())
            {
                if (!PendingIdentities.TryGet(baseName, out identity))
                {
                    if (!PendingIdentities.TryGetOrCreate(baseName, out identity))
                        return false;

                    Plugin.Log?.LogInfo(
                        $"[Y4NGZUpgrades] Reserved pending save identity for active fresh save '{baseName}': {identity}");
                }

                key = BuildKey(baseName, identity);
                return true;
            }

            // Never create a vanilla save merely because stale runtime state still names its slot.
            if (status != IdentityReadStatus.PresentWithoutIdentity)
                return false;

            // Vanilla has now materialized a file that was pending earlier in this host session.
            // Persist the exact reserved identity so all progression already written under it stays
            // attached to the save after disconnect/reload.
            if (PendingIdentities.TryGet(baseName, out string pendingIdentity))
            {
                if (!TryPersistSaveIdentity(baseName, pendingIdentity, out identity))
                    return false;

                PendingIdentities.MarkPersisted(baseName);
                Plugin.Log?.LogInfo(
                    $"[Y4NGZUpgrades] Persisted pending save identity for '{baseName}': {identity}");
            }
            else if (!TryCreateSaveIdentity(baseName, out identity))
            {
                return false;
            }

            key = BuildKey(baseName, identity);
            return true;
        }

        private static bool TryGetBaseSaveFileName(out string saveName, bool allowSlotFallback)
        {
            saveName = GameNetworkManager.Instance?.currentSaveFileName;
            if (!string.IsNullOrWhiteSpace(saveName))
            {
                saveName = saveName.Trim();
                return saveName.Length > 0;
            }

            if (!allowSlotFallback)
            {
                saveName = null;
                return false;
            }

            GameNetworkManager manager = GameNetworkManager.Instance;
            if (manager == null || manager.saveFileNum < 0 || manager.saveFileNum >= ManagedSaveFiles.Length)
            {
                saveName = null;
                return false;
            }

            saveName = ManagedSaveFiles[manager.saveFileNum];
            return true;
        }

        private static bool IsActiveAuthoritativeSession()
        {
            NetworkManager network = NetworkManager.Singleton;
            return network != null
                   && network.IsListening
                   && network.IsServer;
        }

        private static IdentityReadStatus ReadSaveIdentity(string saveFileName, out string identity)
        {
            identity = null;
            if (string.IsNullOrWhiteSpace(saveFileName) || !ResolveEs3())
                return IdentityReadStatus.Unknown;

            saveFileName = saveFileName.Trim();
            try
            {
                bool fileExists = (bool)_es3FileExists.Invoke(null, new object[] { saveFileName });
                if (!fileExists)
                    return IdentityReadStatus.MissingFile;

                bool keyExists = (bool)_es3KeyExists.Invoke(
                    null,
                    new object[] { SaveIdentityKey, saveFileName });
                if (!keyExists)
                    return IdentityReadStatus.PresentWithoutIdentity;

                object loaded = _es3LoadWithDefault.Invoke(
                    null,
                    new object[] { SaveIdentityKey, saveFileName, "" });
                identity = loaded as string ?? loaded?.ToString();
                if (string.IsNullOrWhiteSpace(identity))
                {
                    identity = null;
                    return IdentityReadStatus.PresentWithoutIdentity;
                }

                identity = identity.Trim();
                if (!Guid.TryParseExact(identity, "N", out _))
                {
                    identity = null;
                    return IdentityReadStatus.PresentWithoutIdentity;
                }
                return IdentityReadStatus.PresentWithIdentity;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning(
                    $"[Y4NGZUpgrades] Save identity unavailable for '{saveFileName}': {UnwrapMessage(ex)}");
                identity = null;
                return IdentityReadStatus.Unknown;
            }
        }

        private static bool TryCreateSaveIdentity(string saveFileName, out string identity)
        {
            string candidate = Guid.NewGuid().ToString("N");
            if (!TryPersistSaveIdentity(saveFileName, candidate, out identity))
                return false;

            Plugin.Log?.LogInfo(
                $"[Y4NGZUpgrades] Created save identity for '{saveFileName}': {identity}");
            return true;
        }

        private static bool TryPersistSaveIdentity(
            string saveFileName,
            string requestedIdentity,
            out string identity)
        {
            identity = requestedIdentity?.Trim();
            if (string.IsNullOrWhiteSpace(saveFileName)
                || !Guid.TryParseExact(identity, "N", out _))
            {
                identity = null;
                return false;
            }

            try
            {
                if (!ResolveEs3())
                {
                    identity = null;
                    return false;
                }

                // The caller only reaches this after confirming the file exists. Check once more
                // immediately before writing so a concurrent menu deletion cannot resurrect it.
                bool fileExists = (bool)_es3FileExists.Invoke(null, new object[] { saveFileName });
                if (!fileExists)
                {
                    identity = null;
                    return false;
                }

                _es3SaveObject.Invoke(
                    null,
                    new object[] { SaveIdentityKey, identity, saveFileName });
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning(
                    $"[Y4NGZUpgrades] Could not persist save identity for '{saveFileName}': {UnwrapMessage(ex)}");
                identity = null;
                return false;
            }
        }

        private static string BuildKey(string saveFileName, string identity)
        {
            return saveFileName.Trim() + "#" + identity.Trim();
        }

        private static bool ResolveEs3()
        {
            if (_es3Type != null)
            {
                return _es3FileExists != null
                       && _es3KeyExists != null
                       && _es3LoadWithDefault != null
                       && _es3SaveObject != null;
            }

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    _es3Type = assembly.GetType("ES3");
                    if (_es3Type != null)
                        break;
                }
                catch
                {
                }
            }

            if (_es3Type == null)
                return false;

            _es3FileExists = _es3Type.GetMethod(
                "FileExists",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string) },
                null);
            _es3KeyExists = _es3Type.GetMethod(
                "KeyExists",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string), typeof(string) },
                null);
            _es3LoadWithDefault = _es3Type.GetMethod(
                "Load",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string), typeof(string), typeof(object) },
                null);
            _es3SaveObject = _es3Type.GetMethod(
                "Save",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string), typeof(object), typeof(string) },
                null);

            return _es3FileExists != null
                   && _es3KeyExists != null
                   && _es3LoadWithDefault != null
                   && _es3SaveObject != null;
        }

        private static string UnwrapMessage(Exception exception)
        {
            return exception is TargetInvocationException invocation && invocation.InnerException != null
                ? invocation.InnerException.Message
                : exception.Message;
        }
    }
}
