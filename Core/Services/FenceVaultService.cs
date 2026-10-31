using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DesktopFences.Core.Services
{
    public static class FenceVaultService
    {
        public static string SanitizeFenceId(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return Guid.NewGuid().ToString();
            var invalidChars = Path.GetInvalidFileNameChars();
            string cleaned = new string(id.Trim().Where(c => Array.IndexOf(invalidChars, c) < 0 && c != '/' && c != '\\').ToArray());
            if (string.IsNullOrWhiteSpace(cleaned) || cleaned is "." or "..")
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

        public static string ResolveVaultCollision(string targetDirectory, string fileName)
        {
            string newPath = Path.Combine(targetDirectory, fileName);
            int counter = 1;

            while (File.Exists(newPath) || Directory.Exists(newPath))
            {
                string nameOnly = Path.GetFileNameWithoutExtension(fileName);
                string ext = Path.GetExtension(fileName);
                newPath = Path.Combine(targetDirectory, $"{nameOnly} ({counter}){ext}");
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
                string fileName = Path.GetFileName(entry);
                string dest = ResolveVaultCollision(desktopPath, fileName);

                try
                {
                    if (File.Exists(entry)) File.Move(entry, dest);
                    else if (Directory.Exists(entry)) Directory.Move(entry, dest);
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
