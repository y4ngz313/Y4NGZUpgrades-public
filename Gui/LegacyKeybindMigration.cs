using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using LethalCompanyInputUtils.Config;
using LethalCompanyInputUtils.Data;
using UnityEngine.InputSystem;

namespace Y4NGZUpgrades.Gui
{
    /// <summary>
    /// Moves the retired string key settings into InputUtils once. Existing InputUtils overrides
    /// always win; custom legacy values are written to InputUtils before Gale entries are removed.
    /// </summary>
    internal static class LegacyKeybindMigration
    {
        private const string ActionMapId = Y4NGZUpgrades.Plugin.Guid + ".IngameKeybinds";

        private readonly struct LegacyBinding
        {
            internal LegacyBinding(ConfigEntry<string> entry, InputAction action, string label)
            {
                Entry = entry;
                Action = action;
                Label = label;
            }

            internal ConfigEntry<string> Entry { get; }
            internal InputAction Action { get; }
            internal string Label { get; }
        }

        private readonly struct PendingOverride
        {
            internal PendingOverride(InputAction action, int bindingIndex, string path, BindingOverride record)
            {
                Action = action;
                BindingIndex = bindingIndex;
                Path = path;
                Record = record;
            }

            internal InputAction Action { get; }
            internal int BindingIndex { get; }
            internal string Path { get; }
            internal BindingOverride Record { get; }
        }

        internal static bool TryMigrate(
            IngameKeybinds actions,
            ConfigEntry<string> shadowStep,
            ConfigEntry<string> ping,
            ConfigEntry<string> fieldTablet,
            ConfigEntry<string> commandNet,
            ConfigEntry<string> worklight,
            ConfigEntry<string> dronePointer)
        {
            if (actions == null)
                return false;

            var bindings = new[]
            {
                new LegacyBinding(shadowStep, actions.ShadowStep, "Shadow Step"),
                new LegacyBinding(ping, actions.ForemanPing, "Foreman Ping"),
                new LegacyBinding(fieldTablet, actions.FieldTablet, "Field Tablet"),
                new LegacyBinding(commandNet, actions.CommandNetTransmit, "Command Net"),
                new LegacyBinding(worklight, actions.WorklightBeacon, "Worklight Beacon"),
                new LegacyBinding(dronePointer, actions.CourierDroneCommand, "Courier Drone Command"),
            };

            var pending = new List<PendingOverride>();
            for (int index = 0; index < bindings.Length; index++)
            {
                LegacyBinding legacy = bindings[index];
                if (legacy.Entry == null || legacy.Action == null)
                    continue;

                int bindingIndex = UpgradeInput.FindKeyboardMouseBindingIndex(legacy.Action);
                if (bindingIndex < 0)
                    continue;

                InputBinding binding = legacy.Action.bindings[bindingIndex];
                if (binding.hasOverrides)
                    continue;

                string path = ToControlPath(legacy.Entry.Value);
                if (string.IsNullOrWhiteSpace(path))
                {
                    Y4NGZUpgrades.Plugin.Log?.LogWarning(
                        $"[Config] Could not migrate {legacy.Label} key '{legacy.Entry.Value}'. "
                        + "Its InputUtils default will be used; the original remains in the config backup.");
                    continue;
                }

                if (string.Equals(binding.effectivePath, path, StringComparison.OrdinalIgnoreCase))
                    continue;

                pending.Add(new PendingOverride(
                    legacy.Action,
                    bindingIndex,
                    path,
                    new BindingOverride
                    {
                        action = legacy.Action.name,
                        origPath = binding.path,
                        path = path,
                        groups = binding.groups,
                    }));
            }

            if (pending.Count == 0)
                return true;

            try
            {
                BindingOverrideType storage = ResolveStorageType();
                string destination = storage.GetJsonPath(ActionMapId);
                BindingOverrides overrides = LoadOverrides(destination);
                for (int index = 0; index < pending.Count; index++)
                    overrides.overrides.Add(pending[index].Record);

                WriteAtomically(destination, overrides.AsJson());
                for (int index = 0; index < pending.Count; index++)
                {
                    PendingOverride migrated = pending[index];
                    migrated.Action.ApplyBindingOverride(migrated.BindingIndex, migrated.Path);
                }

                Y4NGZUpgrades.Plugin.Log?.LogInfo(
                    $"[Config] Migrated {pending.Count} legacy control override(s) into InputUtils {storage} bindings.");
                return true;
            }
            catch (Exception exception)
            {
                Y4NGZUpgrades.Plugin.Log?.LogWarning(
                    "[Config] Legacy controls remain in Y4NGZUpgrades.cfg because InputUtils "
                    + "override persistence failed safely: " + exception.Message);
                return false;
            }
        }

        private static BindingOverrideType ResolveStorageType()
        {
            ConfigEntry<BindingOverridePriority> priority = InputUtilsConfig.bindingOverridePriority;
            return priority != null && priority.Value == BindingOverridePriority.GlobalOnly
                ? BindingOverrideType.Global
                : BindingOverrideType.Local;
        }

        private static BindingOverrides LoadOverrides(string path)
        {
            if (!File.Exists(path))
                return new BindingOverrides();

            string json = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(json) ? new BindingOverrides() : BindingOverrides.FromJson(json);
        }

        private static void WriteAtomically(string destination, string json)
        {
            string directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string temporary = destination + ".y4ngz-migration.tmp";
            try
            {
                File.WriteAllText(temporary, json, new System.Text.UTF8Encoding(false));
                if (File.Exists(destination))
                    File.Replace(temporary, destination, null);
                else
                    File.Move(temporary, destination);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }

        private static string ToControlPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            string trimmed = value.Trim();
            if (trimmed.StartsWith("<Keyboard>/", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("<Mouse>/", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }

            switch (trimmed.ToLowerInvariant())
            {
                case "middlebutton":
                case "middlemouse":
                case "mousemiddle":
                case "mmb":
                    return "<Mouse>/middleButton";
                case "rightbutton":
                case "rightmouse":
                case "mouseright":
                case "rmb":
                    return "<Mouse>/rightButton";
                case "forwardbutton":
                case "mouseforward":
                case "mouse4":
                case "m4":
                    return "<Mouse>/forwardButton";
                case "backbutton":
                case "mouseback":
                case "mouse5":
                case "m5":
                    return "<Mouse>/backButton";
            }

            if (!Enum.TryParse(trimmed, ignoreCase: true, out Key key) || key == Key.None)
                return null;

            string control = key.ToString();
            if (control.StartsWith("Digit", StringComparison.OrdinalIgnoreCase))
                control = control.Substring("Digit".Length);
            else if (control.Length > 0)
                control = char.ToLowerInvariant(control[0]) + control.Substring(1);

            return "<Keyboard>/" + control;
        }
    }
}