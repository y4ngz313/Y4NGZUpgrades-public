using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;

namespace Y4NGZUpgrades
{
    internal static class EffectiveConfigInventory
    {
        internal static string ReportPath => Path.Combine(Paths.ConfigPath, "Y4NGZUpgrades", "effective-config.tsv");

        internal static void Write(IEnumerable<ConfigFile> configs)
        {
            if (configs == null)
                return;

            try
            {
                var rows = new List<InventoryRow>();
                foreach (ConfigFile config in configs)
                {
                    if (config == null)
                        continue;

                    string fileName = Path.GetFileName(config.ConfigFilePath);
                    foreach (KeyValuePair<ConfigDefinition, ConfigEntryBase> pair in config)
                        rows.Add(new InventoryRow(fileName, pair.Value));
                }

                rows.Sort((left, right) =>
                {
                    int file = string.Compare(left.FileName, right.FileName, StringComparison.OrdinalIgnoreCase);
                    if (file != 0)
                        return file;
                    int section = string.Compare(
                        left.Entry.Definition.Section,
                        right.Entry.Definition.Section,
                        StringComparison.OrdinalIgnoreCase);
                    return section != 0
                        ? section
                        : string.Compare(
                            left.Entry.Definition.Key,
                            right.Entry.Definition.Key,
                            StringComparison.OrdinalIgnoreCase);
                });

                var output = new StringBuilder();
                output.AppendLine("file\tsection\tkey\ttype\tdefault\teffective\tdescription");
                for (int i = 0; i < rows.Count; i++)
                {
                    InventoryRow row = rows[i];
                    ConfigEntryBase entry = row.Entry;
                    output.Append(Escape(row.FileName)).Append('\t')
                        .Append(Escape(entry.Definition.Section)).Append('\t')
                        .Append(Escape(entry.Definition.Key)).Append('\t')
                        .Append(Escape(entry.SettingType?.FullName)).Append('\t')
                        .Append(Escape(Format(entry.DefaultValue))).Append('\t')
                        .Append(Escape(Format(entry.BoxedValue))).Append('\t')
                        .Append(Escape(entry.Description?.Description))
                        .AppendLine();
                }

                Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
                File.WriteAllText(ReportPath, output.ToString());
                Plugin.Log?.LogInfo($"Effective config inventory written to '{ReportPath}' ({rows.Count} definitions).");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Failed writing effective config inventory: {ex.Message}");
            }
        }

        private static string Format(object value)
        {
            if (value == null)
                return string.Empty;
            return value is IFormattable formattable
                ? formattable.ToString(null, CultureInfo.InvariantCulture)
                : value.ToString();
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private readonly struct InventoryRow
        {
            internal InventoryRow(string fileName, ConfigEntryBase entry)
            {
                FileName = fileName;
                Entry = entry;
            }

            internal string FileName { get; }
            internal ConfigEntryBase Entry { get; }
        }
    }
}
