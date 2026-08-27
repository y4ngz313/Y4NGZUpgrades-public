using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;

namespace Y4NGZUpgrades.Gui;

[Serializable]
internal class PlayerLevelData
{
    public List<string> suits     = new();
    public List<string> cosmetics = new();
    // Emotes are NOT tracked here - TooManyEmotes ships its own shop, save
    // path (TooManyEmotes.UnlockedEmotes), and sync RPCs. The PLAYER LEVEL
    // emote section calls SessionManager.UnlockEmoteLocal directly.
}

// Per-save, on-disk unlock store for the PLAYER LEVEL tab.
// File: BepInEx/config/Y4NGZPlayerLevel/<saveFileName>.json
//
// Local to each player and not synced across the net, but scoped by save:
// hosting or solo uses your own save identity, while joining a lobby uses the
// HOST's identity (see HostSaveIdentity), so a fresh host save starts fresh.
internal static class PlayerLevelStore
{
    private static PlayerLevelData _data;
    private static string          _key;

    private static string SaveDir => Path.Combine(Paths.ConfigPath, "Y4NGZPlayerLevel");

    private static bool TryGetCurrentKey(out string key)
    {
        return SaveKey.TryGetCurrent(out key);
    }

    private static string GetPath(string key)
        => Path.Combine(SaveDir, SanitizeFileName(key) + ".json");

    private static string SanitizeFileName(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = raw.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
        return new string(chars);
    }

    public static PlayerLevelData Get()
    {
        if (!TryGetCurrentKey(out string key))
        {
            _key = null;
            _data = new PlayerLevelData();
            return _data;
        }

        if (_data != null && _key == key) return _data;

        _key = key;
        var path = GetPath(key);
        AdoptLegacyBareSlotFile(key, path);
        if (File.Exists(path))
        {
            try
            {
                _data = JsonUtility.FromJson<PlayerLevelData>(File.ReadAllText(path))
                        ?? new PlayerLevelData();
            }
            catch
            {
                _data = new PlayerLevelData();
            }
        }
        else
        {
            _data = new PlayerLevelData();
        }
        // Defensive: JsonUtility leaves null lists when fields are missing.
        _data.suits     ??= new List<string>();
        _data.cosmetics ??= new List<string>();
        return _data;
    }

    // Files written before save keys grew their "#<guid>" suffix are named after the bare slot
    // ("LCSaveFile1.json"). Rename such a file into the identity-keyed name exactly once, so the
    // unlocks it holds survive and the file becomes prunable like every other managed store.
    //
    // Only ever runs for a local identity-keyed save. While joined to a remote host the key is
    // "HOST#<hostGuid>" and this client's legacy unlocks must not be pulled into the host scope.
    private static void AdoptLegacyBareSlotFile(string key, string identityPath)
    {
        try
        {
            if (File.Exists(identityPath)
                || key.IndexOf('#') < 0
                || !SaveIdentityCleanupPolicy.TryGetManagedSlotName(key, out string slot))
            {
                return;
            }

            string legacyPath = GetPath(slot);
            if (!File.Exists(legacyPath))
                return;

            if (!Directory.Exists(SaveDir)) Directory.CreateDirectory(SaveDir);
            File.Move(legacyPath, identityPath);
            Plugin.CustomLogger?.LogInfo(
                $"[PlayerLevelStore] adopted legacy '{slot}.json' into '{Path.GetFileName(identityPath)}'.");
        }
        catch (Exception e)
        {
            Plugin.CustomLogger?.LogWarning($"[PlayerLevelStore] legacy adoption failed: {e.Message}");
        }
    }

    public static void Save()
    {
        if (_data == null || string.IsNullOrWhiteSpace(_key)) return;
        try
        {
            if (!Directory.Exists(SaveDir)) Directory.CreateDirectory(SaveDir);
            File.WriteAllText(GetPath(_key), JsonUtility.ToJson(_data, true));
        }
        catch (Exception e)
        {
            Plugin.CustomLogger?.LogWarning($"[PlayerLevelStore] save failed: {e.Message}");
        }
    }

    public static void ResetCurrentSave()
    {
        if (!SaveKey.TryGetCurrentExistingOrSlot(out string key))
        {
            _key = null;
            _data = new PlayerLevelData();
            return;
        }

        DeleteForKey(key);
    }

    internal static bool DeleteForKey(string key)
    {
        bool deleted = false;
        try
        {
            string path = GetPath(key);
            if (File.Exists(path))
            {
                File.Delete(path);
                deleted = true;
            }
        }
        catch (Exception e)
        {
            Plugin.CustomLogger?.LogWarning($"[PlayerLevelStore] reset failed: {e.Message}");
        }

        if (string.Equals(_key, key, StringComparison.OrdinalIgnoreCase))
        {
            _key = null;
            _data = new PlayerLevelData();
        }
        return deleted;
    }

    // Re-evaluate the active save file (e.g. after StartOfRound.Start fires
    // with a different save loaded).
    public static void Reload() { _data = null; _key = null; }
}
