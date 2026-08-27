using System;
using System.Collections.Generic;
using System.IO;

namespace Y4NGZUpgrades
{
    internal static class SaveDataFile
    {
        internal static void WriteAllLinesAtomic(string path, IEnumerable<string> lines)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string temporaryPath = path + ".tmp." + Guid.NewGuid().ToString("N");
            string backupPath = path + ".bak";
            try
            {
                File.WriteAllLines(temporaryPath, lines);
                if (File.Exists(path))
                {
                    TryDelete(backupPath);
                    try
                    {
                        File.Replace(temporaryPath, path, backupPath, ignoreMetadataErrors: true);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Copy(temporaryPath, path, overwrite: true);
                        TryDelete(temporaryPath);
                    }
                    TryDelete(backupPath);
                }
                else
                {
                    File.Move(temporaryPath, path);
                }
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
