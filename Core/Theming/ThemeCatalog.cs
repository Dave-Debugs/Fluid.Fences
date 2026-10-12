using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DesktopFences.Core.Theming
{

    public static class ThemeCatalog
    {
        private const string BuiltinResourcePrefix = "DesktopFences.Themes.Builtin.";
        public const string ThemePackageExtension = ".fftheme";

        public static string? LastImportProblem { get; private set; }

        private static readonly JsonSerializerOptions ReadOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        private static readonly JsonSerializerOptions WriteOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        private static readonly ObservableCollection<ThemeDefinition> _themes = new();
        private static readonly object _catalogLock = new();

        public static ReadOnlyObservableCollection<ThemeDefinition> Themes { get; } = new(_themes);

        public static string UserThemeFolder
        {
            get
            {
                string path = Path.Combine(App.ConfigFolder, "Themes");
                try { Directory.CreateDirectory(path); } catch { }
                return path;
            }
        }

        public static void Reload()
        {
            var loaded = new List<ThemeDefinition>();

            foreach (var theme in LoadBuiltins())
                loaded.Add(theme);

            foreach (var theme in LoadUserThemes())
            {
                int existing = loaded.FindIndex(t =>
                    string.Equals(t.Id, theme.Id, StringComparison.OrdinalIgnoreCase));

                if (existing >= 0) loaded[existing] = theme;
                else loaded.Add(theme);
            }

            var sorted = loaded
                .OrderByDescending(t => t.IsBuiltIn)
                .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            if (sorted.Count == 0)
            {
                var fallback = ThemeDefinition.Fallback.Clone();
                fallback.IsBuiltIn = true;
                sorted.Add(fallback);
            }

            void ApplyLoaded()
            {
                lock (_catalogLock)
                {
                    _themes.Clear();
                    foreach (var theme in sorted)
                    {
                        _themes.Add(theme);
                    }
                }
            }

            if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            {
                dispatcher.Invoke(ApplyLoaded);
            }
            else
            {
                ApplyLoaded();
            }
        }

        private static IEnumerable<ThemeDefinition> LoadBuiltins()
        {
            var asm = Assembly.GetExecutingAssembly();

            foreach (string name in asm.GetManifestResourceNames())
            {
                if (!name.StartsWith(BuiltinResourcePrefix, StringComparison.Ordinal)) continue;
                if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

                ThemeDefinition? theme = null;
                try
                {
                    using Stream? stream = asm.GetManifestResourceStream(name);
                    if (stream is null) continue;

                    theme = JsonSerializer.Deserialize<ThemeDefinition>(stream, ReadOptions);
                    if (theme is null) continue;

                    theme.IsBuiltIn = true;
                    theme.SourcePath = null;

                    theme.Id = Slugify(string.IsNullOrWhiteSpace(theme.Id) ? theme.Name : theme.Id);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ThemeCatalog] Built-in '{name}' failed to load: {ex.Message}");
                    continue;
                }

                yield return theme;
            }
        }

        private static IEnumerable<ThemeDefinition> LoadUserThemes()
        {
            string folder = UserThemeFolder;
            if (!Directory.Exists(folder)) yield break;

            string[] files;
            try { files = Directory.GetFiles(folder, "*.json", SearchOption.TopDirectoryOnly); }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ThemeCatalog] Cannot enumerate user themes: {ex.Message}");
                yield break;
            }

            foreach (string file in files)
            {
                ThemeDefinition? theme = TryLoadFile(file);
                if (theme is not null) yield return theme;
            }
        }

        public static ThemeDefinition? TryLoadFile(string path)
        {
            try
            {
                string json = File.ReadAllText(path);
                var theme = JsonSerializer.Deserialize<ThemeDefinition>(json, ReadOptions);
                if (theme is null) return null;

                theme.IsBuiltIn = false;
                theme.SourcePath = path;

                if (string.IsNullOrWhiteSpace(theme.Name))
                    theme.Name = Path.GetFileNameWithoutExtension(path);

                theme.Id = Slugify(string.IsNullOrWhiteSpace(theme.Id)
                    ? Path.GetFileNameWithoutExtension(path)
                    : theme.Id);

                return theme;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ThemeCatalog] User theme '{path}' failed to load: {ex.Message}");
                return null;
            }
        }

        public static ThemeDefinition Get(string? idOrName)
        {
            bool needsReload;
            lock (_catalogLock)
            {
                needsReload = _themes.Count == 0;
            }
            if (needsReload) Reload();

            if (string.Equals(idOrName, "fluid-glass", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(idOrName, "Fluid Glass (Default)", StringComparison.OrdinalIgnoreCase))
            {
                return Default;
            }

            if (!string.IsNullOrWhiteSpace(idOrName))
            {
                lock (_catalogLock)
                {
                    var byId = _themes.FirstOrDefault(t =>
                        string.Equals(t.Id, idOrName, StringComparison.OrdinalIgnoreCase));
                    if (byId is not null) return byId;

                    var byName = _themes.FirstOrDefault(t =>
                        string.Equals(t.Name, idOrName, StringComparison.OrdinalIgnoreCase));
                    if (byName is not null) return byName;
                }
            }

            return Default;
        }

        public static ThemeDefinition Default
        {
            get
            {
                lock (_catalogLock)
                {
                    return _themes.FirstOrDefault(t => t.Id == "obsidian-prism")
                        ?? _themes.FirstOrDefault(t => t.Id == "fluid-glass")
                        ?? _themes.FirstOrDefault()
                        ?? ThemeDefinition.Fallback;
                }
            }
        }

        public static bool Exists(string id)
        {
            lock (_catalogLock)
            {
                return _themes.Any(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
            }
        }

        public static async Task<ThemeDefinition> SaveAsync(ThemeDefinition theme, string? newName = null,
                                                            string? overwriteId = null)
        {
            var (copy, path, json) = PrepareForSave(theme, newName, overwriteId);

            string temp = $"{path}.{Guid.NewGuid():N}.tmp";

            try
            {
                await File.WriteAllTextAsync(temp, json).ConfigureAwait(false);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }

            if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            {
                await dispatcher.InvokeAsync(Reload);
            }
            else
            {
                Reload();
            }

            return Get(copy.Id);
        }

        public static ThemeDefinition Save(ThemeDefinition theme, string? newName = null,
                                           string? overwriteId = null)
        {
            var (copy, path, json) = PrepareForSave(theme, newName, overwriteId);

            string temp = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temp, json);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }

            if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            {
                dispatcher.Invoke(Reload);
            }
            else
            {
                Reload();
            }

            return Get(copy.Id);
        }

        private static (ThemeDefinition Copy, string Path, string Json) PrepareForSave(
            ThemeDefinition theme, string? newName, string? overwriteId = null)
        {
            ArgumentNullException.ThrowIfNull(theme);

            var copy = Clone(theme);

            if (!string.IsNullOrWhiteSpace(newName)) copy.Name = newName!;

            if (!string.IsNullOrWhiteSpace(overwriteId))
            {
                copy.Id = Slugify(overwriteId!);
            }
            else if (theme.IsBuiltIn)
            {
                string baseId = Slugify(copy.Name);
                copy.Id = UniqueId(baseId);
                if (string.Equals(copy.Name, theme.Name, StringComparison.Ordinal))
                    copy.Name = $"{theme.Name} (Custom)";
            }
            else
            {
                string baseId = Slugify(string.IsNullOrWhiteSpace(copy.Id) ? copy.Name : copy.Id);
                copy.Id = string.IsNullOrWhiteSpace(copy.Id) ? UniqueId(baseId) : baseId;
            }

            copy.IsBuiltIn = false;

            string path = Path.Combine(UserThemeFolder, copy.Id + ".json");
            string fullPath = Path.GetFullPath(path);
            string userFolder = Path.GetFullPath(UserThemeFolder);
            if (!fullPath.StartsWith(userFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing to write theme outside user theme folder.");

            copy.SourcePath = fullPath;

            return (copy, fullPath, JsonSerializer.Serialize(copy, WriteOptions));
        }

        public static bool Delete(ThemeDefinition theme)
        {
            if (theme is null || theme.IsBuiltIn) return false;
            if (string.IsNullOrWhiteSpace(theme.SourcePath)) return false;

            try
            {
                if (File.Exists(theme.SourcePath)) File.Delete(theme.SourcePath);
                DeleteThemeMediaFiles(theme.Id);
                Reload();
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ThemeCatalog] Delete failed: {ex.Message}");
                return false;
            }
        }

        public static bool Delete(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            var theme = Get(id);
            if (theme is not null && !theme.IsBuiltIn && string.Equals(theme.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return Delete(theme);
            }
            return false;
        }

        private static void DeleteThemeMediaFiles(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            try
            {
                string mediaDir = Path.Combine(UserThemeFolder, "media");
                if (!Directory.Exists(mediaDir)) return;
                string prefix = $"{Slugify(id)}_";
                foreach (string mediaFile in Directory.GetFiles(mediaDir, $"{prefix}*"))
                {
                    try { File.Delete(mediaFile); } catch { }
                }
            }
            catch { }
        }

        public static async Task ExportAsync(ThemeDefinition theme, string destinationPath, string? mediaPath = null)
        {
            ArgumentNullException.ThrowIfNull(theme);

            string staging = Path.Combine(Path.GetTempPath(), "ff_theme_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);

            try
            {
                var payload = Clone(theme);
                payload.SourcePath = null;

                if (!string.IsNullOrWhiteSpace(mediaPath) && File.Exists(mediaPath))
                {
                    string mediaDir = Path.Combine(staging, "media");
                    Directory.CreateDirectory(mediaDir);
                    string mediaFileName = Path.GetFileName(mediaPath);
                    File.Copy(mediaPath, Path.Combine(mediaDir, mediaFileName), overwrite: true);
                    payload.LoopFile = mediaFileName;
                }

                await File.WriteAllTextAsync(
                    Path.Combine(staging, "theme.json"),
                    JsonSerializer.Serialize(payload, WriteOptions));

                if (File.Exists(destinationPath)) File.Delete(destinationPath);
                ZipFile.CreateFromDirectory(staging, destinationPath, CompressionLevel.Optimal, false);
            }
            finally
            {
                try { Directory.Delete(staging, true); } catch { }
            }
        }

        public static async Task<ThemeDefinition?> ImportAsync(string sourcePath)
        {
            if (!File.Exists(sourcePath)) return null;

            if (Path.GetExtension(sourcePath).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                var direct = TryLoadFile(sourcePath);
                if (direct is null) return null;
                string baseId = Slugify(string.IsNullOrWhiteSpace(direct.Id) ? direct.Name : direct.Id);
                direct.Id = UniqueId(baseId);
                return await SaveAsync(direct);
            }

            string staging = Path.Combine(Path.GetTempPath(), "ff_import_" + Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(staging);
                ExtractSafely(sourcePath, staging);

                string manifest = Path.Combine(staging, "theme.json");
                if (!File.Exists(manifest))
                {
                    manifest = Directory.GetFiles(staging, "*.json", SearchOption.AllDirectories).FirstOrDefault() ?? "";
                    if (!File.Exists(manifest)) return null;
                }

                var imported = TryLoadFile(manifest);
                if (imported is null) return null;

                string baseId = Slugify(string.IsNullOrWhiteSpace(imported.Id) ? imported.Name : imported.Id);
                imported.Id = UniqueId(baseId);

                string mediaDir = Path.Combine(staging, "media");
                if (Directory.Exists(mediaDir))
                {
                    string target = Path.Combine(UserThemeFolder, "media");
                    Directory.CreateDirectory(target);
                    var allowedMedia = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ".mp4", ".webm", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp"
                    };
                    foreach (string file in Directory.GetFiles(mediaDir))
                    {
                        string ext = Path.GetExtension(file);
                        if (!allowedMedia.Contains(ext)) continue;
                        string destFileName = $"{imported.Id}_{Path.GetFileName(file)}";
                        try
                        {
                            File.Copy(file, Path.Combine(target, destFileName), overwrite: true);
                            imported.LoopFile = destFileName;
                        }
                        catch { }
                    }
                }

                return await SaveAsync(imported);
            }
            catch (InvalidDataException ex)
            {

                ThemeLog.Warn("Import", $"Refused '{Path.GetFileName(sourcePath)}': {ex.Message}");
                LastImportProblem = ex.Message;
                return null;
            }
            catch (Exception ex)
            {
                ThemeLog.Error("Import", ex);
                LastImportProblem = ex.Message;
                return null;
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
            }
        }

        private const int    MaxArchiveEntries      = 64;
        private const long   MaxTotalUncompressed   = 64L * 1024 * 1024;
        private const long   MaxSingleEntryBytes    = 48L * 1024 * 1024;
        private const double MaxCompressionRatio    = 120.0;

        private static void ExtractSafely(string archivePath, string destination)
        {
            string root = Path.GetFullPath(destination);
            if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;

            using ZipArchive archive = ZipFile.OpenRead(archivePath);

            if (archive.Entries.Count > MaxArchiveEntries)
                throw new InvalidDataException(
                    $"Theme package has {archive.Entries.Count} entries; the limit is {MaxArchiveEntries}.");

            long declaredTotal = 0;
            foreach (ZipArchiveEntry e in archive.Entries) declaredTotal += e.Length;

            if (declaredTotal > MaxTotalUncompressed)
                throw new InvalidDataException(
                    $"Theme package expands to {declaredTotal / (1024 * 1024)} MB; the limit is {MaxTotalUncompressed / (1024 * 1024)} MB.");

            long written = 0;
            byte[] buffer = new byte[81920];

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;

                string target = Path.GetFullPath(Path.Combine(root, entry.FullName));

                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"Theme package tried to write outside its folder ('{entry.FullName}').");

                if (entry.Length > MaxSingleEntryBytes)
                    throw new InvalidDataException(
                        $"'{entry.FullName}' is {entry.Length / (1024 * 1024)} MB; the limit is {MaxSingleEntryBytes / (1024 * 1024)} MB.");

                if (entry.CompressedLength > 0 &&
                    (double)entry.Length / entry.CompressedLength > MaxCompressionRatio)
                    throw new InvalidDataException(
                        $"'{entry.FullName}' is compressed {entry.Length / entry.CompressedLength}x, which is not plausible for theme content.");

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                using var input = entry.Open();
                using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                long entryWritten = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    entryWritten += read;
                    if (entryWritten > MaxSingleEntryBytes)
                        throw new InvalidDataException(
                            $"'{entry.FullName}' exceeded the {MaxSingleEntryBytes / (1024 * 1024)} MB limit while extracting.");

                    written += read;
                    if (written > MaxTotalUncompressed)
                        throw new InvalidDataException("Theme package exceeded its size budget while extracting.");

                    output.Write(buffer, 0, read);
                }
            }
        }

        public static ThemeDefinition Clone(ThemeDefinition source)
        {
            string json = JsonSerializer.Serialize(source, WriteOptions);
            var clone = JsonSerializer.Deserialize<ThemeDefinition>(json, ReadOptions)!;
            clone.IsBuiltIn = source.IsBuiltIn;
            clone.SourcePath = source.SourcePath;
            clone.Pattern = source.Pattern;
            clone.LoopFile = source.LoopFile;
            return clone;
        }

        public static string Slugify(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "theme";
            string slug = Regex.Replace(input.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
            return string.IsNullOrEmpty(slug) ? "theme" : slug;
        }

        private static string UniqueId(string baseId)
        {
            baseId = Slugify(baseId);
            if (!Exists(baseId) && !File.Exists(Path.Combine(UserThemeFolder, baseId + ".json")))
                return baseId;

            for (int i = 2; i < 1000; i++)
            {
                string candidate = $"{baseId}-{i}";
                if (!Exists(candidate) && !File.Exists(Path.Combine(UserThemeFolder, candidate + ".json")))
                    return candidate;
            }
            return $"{baseId}-{Guid.NewGuid():N}";
        }
    }
}
