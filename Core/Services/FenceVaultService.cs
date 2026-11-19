using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DesktopFences.Core.Services
{
    public static class FenceVaultService
    {
        private static readonly FrozenSet<string> ReservedDeviceNames = new[]
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public static string SanitizeFenceId(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return Guid.NewGuid().ToString();
            var invalidChars = Path.GetInvalidFileNameChars();
            string cleaned = new string(id.Trim().Where(c => Array.IndexOf(invalidChars, c) < 0 && c != '/' && c != '\\').ToArray())
                .TrimEnd('.', ' ');

            if (string.IsNullOrWhiteSpace(cleaned) || cleaned is "." or "..")
            {
                return Guid.NewGuid().ToString();
            }

            string baseName = cleaned.Split('.')[0];
            if (ReservedDeviceNames.Contains(baseName))
            {
                return Guid.NewGuid().ToString();
            }

            return cleaned;
        }

        public static string GetStorageDirectory(string configFolder, string fenceId)
        {
            string sanitized = SanitizeFenceId(fenceId);
            return Path.Combine(configFolder, "Storage", sanitized);
        }

        public static bool IsInsideDirectory(string path, string directory)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory)) return false;
            try
            {
                string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string fullDir = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return fullPath.StartsWith(fullDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static bool IsDirectChildOfDirectory(string path, string directory)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory)) return false;
            try
            {
                string? parent = Path.GetDirectoryName(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(parent)) return false;
                string fullParent = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string fullDir = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(fullParent, fullDir, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static string ResolveVaultCollision(string targetDirectory, string fileName, bool isDirectory = false)
        {
            string newPath = Path.Combine(targetDirectory, fileName);
            int counter = 1;

            while (File.Exists(newPath) || Directory.Exists(newPath))
            {
                if (isDirectory)
                {
                    newPath = Path.Combine(targetDirectory, $"{fileName} ({counter})");
                }
                else
                {
                    string nameOnly = Path.GetFileNameWithoutExtension(fileName);
                    string ext = Path.GetExtension(fileName);
                    newPath = Path.Combine(targetDirectory, $"{nameOnly} ({counter}){ext}");
                }
                counter++;
            }

            return newPath;
        }

        public static List<string> RestoreDesktopFiles(string storageDir, string desktopPath, out int failureCount)
        {
            failureCount = 0;
            List<string> restored = [];

            if (!Directory.Exists(storageDir)) return restored;

            var allEntries = Directory.GetFileSystemEntries(storageDir);
            foreach (string entry in allEntries)
            {
                bool isDir = Directory.Exists(entry);
                string fileName = Path.GetFileName(entry);
                string dest = ResolveVaultCollision(desktopPath, fileName, isDir);

                try
                {
                    if (isDir) Directory.Move(entry, dest);
                    else if (File.Exists(entry)) File.Move(entry, dest);
                    restored.Add(dest);
                }
                catch
                {
                    failureCount++;
                }
            }

            return restored;
        }
    }
}
