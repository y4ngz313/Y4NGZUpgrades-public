using HarmonyLib;

namespace Y4NGZUpgrades
{
    [HarmonyPatch(typeof(DeleteFileButton), nameof(DeleteFileButton.DeleteFile))]
    internal static class SaveDeletionPatches
    {
        private sealed class DeleteState
        {
            internal string SaveFileName { get; }
            internal string SaveKey { get; }

            internal DeleteState(string saveFileName, string saveKey)
            {
                SaveFileName = saveFileName;
                SaveKey = saveKey;
            }
        }

        [HarmonyPrefix]
        private static void Prefix(DeleteFileButton __instance, out DeleteState __state)
        {
            __state = null;
            string saveFileName = GetSaveFileName(__instance?.fileToDelete ?? -1);
            if (saveFileName == null
                || !SaveKey.TryGetExistingForSaveFile(saveFileName, out string key))
            {
                return;
            }

            __state = new DeleteState(saveFileName, key);
        }

        [HarmonyPostfix]
        private static void Postfix(DeleteState __state)
        {
            string saveFileName = __state?.SaveFileName;
            string saveKey = __state?.SaveKey;
            if (string.IsNullOrWhiteSpace(saveFileName)
                || string.IsNullOrWhiteSpace(saveKey)
                || !SaveKey.TrySaveFileExists(saveFileName, out bool exists)
                || exists)
            {
                return;
            }

            SaveDataLifecycle.DeleteSave(saveKey, "manual vanilla save deletion");
        }

        private static string GetSaveFileName(int fileNumber)
        {
            switch (fileNumber)
            {
                case 0: return "LCSaveFile1";
                case 1: return "LCSaveFile2";
                case 2: return "LCSaveFile3";
                default: return null;
            }
        }
    }
}
