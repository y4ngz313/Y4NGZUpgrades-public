using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Y4NGZUpgrades.Config
{
    /// <summary>
    /// Read-only view of a BepInEx TOML config. It intentionally understands only the simple
    /// section/key/value shape BepInEx itself writes; that is enough to migrate live values out of
    /// the former monolithic config without binding (and therefore regenerating) obsolete keys.
    /// </summary>
    internal sealed class LegacyConfigDocument
    {
        private readonly Dictionary<string, string> _values =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private LegacyConfigDocument(string path)
        {
            Path = path ?? string.Empty;
        }

        internal string Path { get; }
        internal int Count => _values.Count;

        internal static LegacyConfigDocument Load(string path)
        {
            var document = new LegacyConfigDocument(path);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return document;

            document.Parse(File.ReadAllLines(path));
            return document;
        }

        internal static LegacyConfigDocument ParseLines(IEnumerable<string> lines)
        {
            var document = new LegacyConfigDocument(string.Empty);
            document.Parse(lines ?? Array.Empty<string>());
            return document;
        }

        internal bool Contains(string section, string key)
        {
            return _values.ContainsKey(Compose(section, key));
        }

        internal bool TryGetRaw(string section, string key, out string value)
        {
            return _values.TryGetValue(Compose(section, key), out value);
        }

        internal bool TryGet<T>(string section, string key, out T value)
        {
            value = default;
            if (!TryGetRaw(section, key, out string raw))
                return false;

            object parsed;
            Type type = typeof(T);
            if (type == typeof(string))
            {
                parsed = raw;
            }
            else if (type == typeof(bool) && bool.TryParse(raw, out bool boolean))
            {
                parsed = boolean;
            }
            else if (type == typeof(int)
                     && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int integer))
            {
                parsed = integer;
            }
            else if (type == typeof(float)
                     && float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float single))
            {
                parsed = single;
            }
            // Match BepInEx's Enum.Parse semantics, including numeric values already valid in config.
            else if (type.IsEnum && Enum.TryParse(type, raw, true, out object member))
            {
                parsed = member;
            }
            else
            {
                return false;
            }

            value = (T)parsed;
            return true;
        }

        private void Parse(IEnumerable<string> lines)
        {
            string section = string.Empty;
            foreach (string sourceLine in lines)
            {
                string line = (sourceLine ?? string.Empty).Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;

                if (line.StartsWith("[", StringComparison.Ordinal)
                    && line.EndsWith("]", StringComparison.Ordinal)
                    && line.Length > 2)
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }

                int equals = line.IndexOf('=');
                if (equals <= 0)
                    continue;

                string key = line.Substring(0, equals).Trim();
                string raw = line.Substring(equals + 1).Trim();
                if (key.Length > 0)
                    _values[Compose(section, key)] = raw;
            }
        }

        private static string Compose(string section, string key)
        {
            return (section ?? string.Empty).Trim() + "\n" + (key ?? string.Empty).Trim();
        }
    }

    internal readonly struct LegacyConfigKey
    {
        internal LegacyConfigKey(string section, string key)
        {
            Section = section ?? string.Empty;
            Key = key ?? string.Empty;
        }

        internal string Section { get; }
        internal string Key { get; }
    }
}
