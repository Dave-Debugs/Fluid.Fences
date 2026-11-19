using System;
using System.Buffers;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopFences.Core.Theming
{
    public static partial class ThemeCatalog
    {
        private const string BuiltinResourcePrefix = "DesktopFences.Themes.Builtin.";
        public const string ThemePackageExtension = ".fftheme";
        private const long MaxJsonThemeBytes = 1024 * 1024;

        [GeneratedRegex(@"[^a-z0-9]+")]
        private static partial Regex SlugifyRegex();

        private static readonly FrozenSet<string> AllowedMediaExtensions =
            FrozenSet.ToFrozenSet(
                [".mp4", ".webm", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp"],
                StringComparer.OrdinalIgnoreCase);

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

        private sealed record CatalogSnapshot(
            FrozenDictionary<string, ThemeDefinition> ById,
            FrozenDictionary<string, ThemeDefinition> ByName,
            ThemeDefinition DefaultTheme);

        private static readonly ObservableCollection<ThemeDefinition> _themes = [];
        private static readonly object _catalogLock = new();
        private static volatile CatalogSnapshot? _snapshot;

        public static ReadOnlyObservableCollection<ThemeDefinition> Themes { get; } = new(_themes);

        private static string _userThemeFolderCache = "";
        private static bool _userThemeFolderCreated;

        public static string UserThemeFolder
        {
            get
            {
                string path = Path.Combine(App.ConfigFolder, "Themes");
                if (!_userThemeFolderCreated || !string.Equals(_userThemeFolderCache, path, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        Directory.CreateDirectory(path);
                        _userThemeFolderCache = path;
                        _userThemeFolderCreated = true;
                    }
                    catch { }
                }
                return path;
            }
        }

        public static void Reload()
        {
            var builtins = LoadBuiltins().ToList();
            var builtinIds = builtins.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var loaded = new List<ThemeDefinition>(builtins);

            foreach (var theme in LoadUserThemes())
            {
                if (builtinIds.Contains(theme.Id))
                {
                    ThemeLog.Warn("Catalog", $"Ignored user theme '{theme.SourcePath}' attempting to shadow built-in ID '{theme.Id}'.");
                    continue;
                }

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
                var fallback = ThemeDefinition.Fallback;
                fallback.IsBuiltIn = true;
                sorted.Add(fallback);
            }

            var byIdDict = new Dictionary<string, ThemeDefinition>(sorted.Count, StringComparer.OrdinalIgnoreCase);
            var byNameDict = new Dictionary<string, ThemeDefinition>(sorted.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var theme in sorted)
            {
                byIdDict[theme.Id] = theme;
                byNameDict.TryAdd(theme.Name, theme);
            }

            var defaultTheme = byIdDict.GetValueOrDefault("obsidian-prism")
                ?? byIdDict.GetValueOrDefault("fluid-glass")
                ?? sorted[0];

            var newSnapshot = new CatalogSnapshot(
                byIdDict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
                byNameDict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
                defaultTheme);

            Interlocked.Exchange(ref _snapshot, newSnapshot);

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
                dispatcher.BeginInvoke(ApplyLoaded);
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
                var fileInfo = new FileInfo(path);
                if (!fileInfo.Exists) return null;
                if (fileInfo.Length > MaxJsonThemeBytes)
                {
                    LastImportProblem = $"Theme file exceeds the {MaxJsonThemeBytes / 1024} KB limit.";
                    return null;
                }

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var theme = JsonSerializer.Deserialize<ThemeDefinition>(fs, ReadOptions);
                if (theme is null) return null;

                theme.IsBuiltIn = false;
                theme.SourcePath = path;

                if (string.IsNullOrWhiteSpace(theme.Name))
                    theme.Name = Path.GetFileNameWithoutExtension(path);

                theme.Id = Slugify(string.IsNullOrWhiteSpace(theme.Id)
                    ? Path.GetFileNameWithoutExtension(path)
                    : theme.Id);

                if (!string.IsNullOrWhiteSpace(theme.LoopFile))
                {
                    string safeLoop = Path.GetFileName(theme.LoopFile);
                    string ext = Path.GetExtension(safeLoop);
                    theme.LoopFile = AllowedMediaExtensions.Contains(ext) ? safeLoop : null;
                }

                return theme;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ThemeCatalog] User theme '{path}' failed to load: {ex.Message}");
                return null;
            }
        }

        private static CatalogSnapshot EnsureLoaded()
        {
            var snap = _snapshot;
            if (snap is null)
            {
                Reload();
                snap = _snapshot!;
            }
            return snap;
        }

        public static ThemeDefinition Get(string? idOrName)
        {
            var snap = EnsureLoaded();

            if (string.Equals(idOrName, "fluid-glass", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(idOrName, "Fluid Glass (Default)", StringComparison.OrdinalIgnoreCase))
            {
                return snap.DefaultTheme;
            }

            if (!string.IsNullOrWhiteSpace(idOrName))
            {
                if (snap.ById.TryGetValue(idOrName, out var byId)) return byId;
                if (snap.ByName.TryGetValue(idOrName, out var byName)) return byName;
            }

            return snap.DefaultTheme;
        }

        public static ThemeDefinition Default => EnsureLoaded().DefaultTheme;

        public static bool Exists(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            return EnsureLoaded().ById.ContainsKey(id);
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

            Reload();
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

            Reload();
            return Get(copy.Id);
        }

        private static (ThemeDefinition Copy, string Path, string Json) PrepareForSave(
            ThemeDefinition theme, string? newName, string? overwriteId = null)
        {
            ArgumentNullException.ThrowIfNull(theme);
            var snap = EnsureLoaded();

            var copy = Clone(theme);

            if (!string.IsNullOrWhiteSpace(newName)) copy.Name = newName!;

            if (!string.IsNullOrWhiteSpace(overwriteId))
            {
                string slug = Slugify(overwriteId!);
                if (snap.ById.TryGetValue(slug, out var existing) && existing.IsBuiltIn)
                    slug = UniqueId(slug);
                copy.Id = slug;
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
                if (string.IsNullOrWhiteSpace(copy.Id) || (snap.ById.TryGetValue(baseId, out var existing) && existing.IsBuiltIn))
                    copy.Id = UniqueId(baseId);
                else
                    copy.Id = baseId;
            }

            copy.IsBuiltIn = false;

            string userFolder = Path.GetFullPath(UserThemeFolder);
            string path = Path.Combine(userFolder, copy.Id + ".json");
            string fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(userFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing to write theme outside user theme folder.");

            copy.SourcePath = fullPath;

            return (copy, fullPath, JsonSerializer.Serialize(copy, WriteOptions));
        }

        public static bool Delete(ThemeDefinition theme, bool deleteMedia = true)
        {
            if (theme is null || theme.IsBuiltIn) return false;
            if (string.IsNullOrWhiteSpace(theme.SourcePath)) return false;

            try
            {
                string userFolder = Path.GetFullPath(UserThemeFolder);
                string fullSource = Path.GetFullPath(theme.SourcePath);
                if (!fullSource.StartsWith(userFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return false;

                if (File.Exists(fullSource)) File.Delete(fullSource);
                if (deleteMedia) DeleteThemeMediaFiles(theme.Id);
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

        internal static void DeleteThemeMediaFiles(string? id)
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

        public static Task ExportAsync(ThemeDefinition theme, string destinationPath, string? mediaPath = null)
        {
            ArgumentNullException.ThrowIfNull(theme);
            return Task.Run(() => ExportCore(theme, destinationPath, mediaPath));
        }

        private static void ExportCore(ThemeDefinition theme, string destinationPath, string? mediaPath)
        {
            string staging = Path.Combine(Path.GetTempPath(), "ff_theme_" + Guid.NewGuid().ToString("N"));
            string tempZip = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
            Directory.CreateDirectory(staging);

            try
            {
                var payload = Clone(theme);
                payload.SourcePath = null;

                if (!string.IsNullOrWhiteSpace(mediaPath) && File.Exists(mediaPath))
                {
                    var fi = new FileInfo(mediaPath);
                    if (fi.Length > MaxSingleEntryBytes)
                        throw new InvalidDataException($"Attached media exceeds the {MaxSingleEntryBytes / (1024 * 1024)} MB export limit.");

                    string mediaDir = Path.Combine(staging, "media");
                    Directory.CreateDirectory(mediaDir);
                    string mediaFileName = Path.GetFileName(mediaPath);
                    File.Copy(mediaPath, Path.Combine(mediaDir, mediaFileName), overwrite: true);
                    payload.LoopFile = mediaFileName;
                }

                File.WriteAllText(
                    Path.Combine(staging, "theme.json"),
                    JsonSerializer.Serialize(payload, WriteOptions));

                ZipFile.CreateFromDirectory(staging, tempZip, CompressionLevel.Optimal, false);
                File.Move(tempZip, destinationPath, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
            }
        }

        public static async Task<ThemeDefinition?> ImportAsync(string sourcePath)
        {
            LastImportProblem = null;
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return null;

            return await Task.Run(() => ImportCore(sourcePath)).ConfigureAwait(false);
        }

        private static ThemeDefinition? ImportCore(string sourcePath)
        {
            if (Path.GetExtension(sourcePath).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                var direct = TryLoadFile(sourcePath);
                if (direct is null) return null;
                string baseId = Slugify(string.IsNullOrWhiteSpace(direct.Id) ? direct.Name : direct.Id);
                direct.Id = UniqueId(baseId);
                return Save(direct);
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
                    if (!File.Exists(manifest))
                    {
                        LastImportProblem = "Theme package did not contain a theme.json manifest.";
                        return null;
                    }
                }

                var imported = TryLoadFile(manifest);
                if (imported is null) return null;

                string baseId = Slugify(string.IsNullOrWhiteSpace(imported.Id) ? imported.Name : imported.Id);
                imported.Id = UniqueId(baseId);

                string mediaDir = Path.Combine(staging, "media");
                if (Directory.Exists(mediaDir))
                {
                    var allowedFiles = Directory.GetFiles(mediaDir)
                        .Where(f => AllowedMediaExtensions.Contains(Path.GetExtension(f)))
                        .ToList();

                    string? chosen = null;
                    if (!string.IsNullOrWhiteSpace(imported.LoopFile))
                    {
                        chosen = allowedFiles.FirstOrDefault(f =>
                            string.Equals(Path.GetFileName(f), imported.LoopFile, StringComparison.OrdinalIgnoreCase));
                    }
                    chosen ??= allowedFiles.FirstOrDefault(f =>
                        Path.GetExtension(f).Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                        Path.GetExtension(f).Equals(".webm", StringComparison.OrdinalIgnoreCase))
                        ?? allowedFiles.FirstOrDefault();

                    if (chosen is not null)
                    {
                        string target = Path.Combine(UserThemeFolder, "media");
                        Directory.CreateDirectory(target);
                        string destFileName = $"{imported.Id}_{Path.GetFileName(chosen)}";
                        File.Copy(chosen, Path.Combine(target, destFileName), overwrite: true);
                        imported.LoopFile = destFileName;
                    }
                    else
                    {
                        imported.LoopFile = null;
                    }
                }
                else
                {
                    imported.LoopFile = null;
                }

                return Save(imported);
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

        private const int MaxArchiveEntries = 64;
        private const long MaxTotalUncompressed = 64L * 1024 * 1024;
        private const long MaxSingleEntryBytes = 48L * 1024 * 1024;
        private const double MaxCompressionRatio = 120.0;

        private static void ExtractSafely(string archivePath, string destination)
        {
            string root = Path.GetFullPath(destination);
            if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;

            using ZipArchive archive = ZipFile.OpenRead(archivePath);

            if (archive.Entries.Count > MaxArchiveEntries)
                throw new InvalidDataException(
                    $"Theme package has {archive.Entries.Count} entries; the limit is {MaxArchiveEntries}.");

            long declaredTotal = 0;
            foreach (ZipArchiveEntry e in archive.Entries)
            {
                if (e.Length < 0 || e.Length > MaxSingleEntryBytes)
                    throw new InvalidDataException($"'{e.FullName}' has an invalid or oversized declared length.");
                try { declaredTotal = checked(declaredTotal + e.Length); }
                catch (OverflowException)
                {
                    throw new InvalidDataException("Theme package declared total size overflowed.");
                }
            }

            if (declaredTotal > MaxTotalUncompressed)
                throw new InvalidDataException(
                    $"Theme package expands to {declaredTotal / (1024 * 1024)} MB; the limit is {MaxTotalUncompressed / (1024 * 1024)} MB.");

            long written = 0;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;

                    string target = Path.GetFullPath(Path.Combine(root, entry.FullName));

                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(
                            $"Theme package tried to write outside its folder ('{entry.FullName}').");

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
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public static ThemeDefinition Clone(ThemeDefinition source)
        {
            ArgumentNullException.ThrowIfNull(source);
            var sc = source.Colors ?? new ThemeColors();
            var ss = source.Shape ?? new ThemeShape();
            var st = source.Typography ?? new ThemeTypography();
            var sm = source.Motion ?? new ThemeMotion();

            return new ThemeDefinition
            {
                Schema = source.Schema,
                Id = source.Id,
                Name = source.Name,
                Author = source.Author,
                Description = source.Description,
                Category = source.Category,
                BaseTheme = source.BaseTheme,
                Pattern = source.Pattern,
                LoopFile = source.LoopFile,
                IsBuiltIn = source.IsBuiltIn,
                SourcePath = source.SourcePath,
                Colors = new ThemeColors
                {
                    Primary = sc.Primary,
                    PrimaryHover = sc.PrimaryHover,
                    PrimaryPressed = sc.PrimaryPressed,
                    Secondary = sc.Secondary,
                    Accent = sc.Accent,
                    Background = sc.Background,
                    Surface = sc.Surface,
                    SurfaceSubtle = sc.SurfaceSubtle,
                    SurfaceHover = sc.SurfaceHover,
                    SurfaceSelected = sc.SurfaceSelected,
                    Header = sc.Header,
                    Border = sc.Border,
                    BorderSubtle = sc.BorderSubtle,
                    FocusRing = sc.FocusRing,
                    TextPrimary = sc.TextPrimary,
                    TextSecondary = sc.TextSecondary,
                    TextDisabled = sc.TextDisabled,
                    TextOnAccent = sc.TextOnAccent,
                    Glyph = sc.Glyph,
                    GlyphHover = sc.GlyphHover,
                    Success = sc.Success,
                    Error = sc.Error,
                    Warning = sc.Warning,
                    Info = sc.Info,
                    SelectionFill = sc.SelectionFill,
                    SelectionStroke = sc.SelectionStroke,
                    DropTarget = sc.DropTarget,
                    ScrollThumb = sc.ScrollThumb,
                    ScrollThumbHover = sc.ScrollThumbHover,
                    ScrollThumbActive = sc.ScrollThumbActive
                },
                Shape = new ThemeShape
                {
                    CornerRadius = ss.CornerRadius,
                    CornerRadiusSmall = ss.CornerRadiusSmall,
                    BorderThickness = ss.BorderThickness,
                    ShadowOpacity = ss.ShadowOpacity,
                    ShadowBlurRadius = ss.ShadowBlurRadius,
                    ShadowDepth = ss.ShadowDepth
                },
                Typography = new ThemeTypography
                {
                    FontFamily = st.FontFamily,
                    MonospaceFontFamily = st.MonospaceFontFamily,
                    Scale = st.Scale,
                    TitleWeight = st.TitleWeight
                },
                Motion = new ThemeMotion
                {
                    SpeedMultiplier = sm.SpeedMultiplier,
                    RollUpStyle = sm.RollUpStyle,
                    ThemeTransitionMs = sm.ThemeTransitionMs
                },
                CustomTokens = new Dictionary<string, string>(
                    source.CustomTokens ?? [], StringComparer.OrdinalIgnoreCase)
            };
        }

        public static string Slugify(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "theme";
            string slug = SlugifyRegex().Replace(input.ToLowerInvariant(), "-").Trim('-');
            return string.IsNullOrEmpty(slug) ? "theme" : slug;
        }

        private static string UniqueId(string baseId)
        {
            baseId = Slugify(baseId);
            string folder = UserThemeFolder;
            if (!Exists(baseId) && !File.Exists(Path.Combine(folder, baseId + ".json")))
                return baseId;

            for (int i = 2; i < 1000; i++)
            {
                string candidate = $"{baseId}-{i}";
                if (!Exists(candidate) && !File.Exists(Path.Combine(folder, candidate + ".json")))
                    return candidate;
            }
            return $"{baseId}-{Guid.NewGuid():N}";
        }
    }
}
