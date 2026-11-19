using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopFences
{
    public static class ConfigStore
    {
        private static readonly SemaphoreSlim _gate = new(1, 1);

        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
        private static readonly JsonSerializerOptions ReadOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true
        };

        public static string Path => System.IO.Path.Combine(App.ConfigFolder, "global_config.json");

        public static GlobalConfig Read()
        {
            _gate.Wait();
            try
            {
                return ReadUnlocked(allowFallbackOnIoError: true);
            }
            finally { _gate.Release(); }
        }

        public static async Task<GlobalConfig> ReadAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await ReadUnlockedAsync(allowFallbackOnIoError: true).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }

        public static async Task<bool> UpdateAsync(Action<GlobalConfig> mutate)
        {
            ArgumentNullException.ThrowIfNull(mutate);

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var config = await ReadUnlockedAsync(allowFallbackOnIoError: false).ConfigureAwait(false);
                mutate(config);
                await WriteUnlockedAsync(config).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                App.LogError("ConfigStore.UpdateAsync", ex);
                return false;
            }
            finally { _gate.Release(); }
        }

        public static bool Update(Action<GlobalConfig> mutate)
        {
            ArgumentNullException.ThrowIfNull(mutate);

            _gate.Wait();
            try
            {
                var config = ReadUnlocked(allowFallbackOnIoError: false);
                mutate(config);
                WriteUnlocked(config);
                return true;
            }
            catch (Exception ex)
            {
                App.LogError("ConfigStore.Update", ex);
                return false;
            }
            finally { _gate.Release(); }
        }

        private static async Task<GlobalConfig> ReadUnlockedAsync(bool allowFallbackOnIoError)
        {
            if (!File.Exists(Path)) return new GlobalConfig();

            const int maxAttempts = 3;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    await using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);
                    return await JsonSerializer.DeserializeAsync<GlobalConfig>(fs, ReadOptions).ConfigureAwait(false) ?? new GlobalConfig();
                }
                catch (JsonException ex)
                {
                    App.LogError("ConfigStore.ReadUnlockedAsync", ex);
                    BackupCorruptFile();
                    return new GlobalConfig();
                }
                catch (IOException ex) when (attempt < maxAttempts)
                {
                    App.LogError($"ConfigStore.ReadUnlockedAsync (retry {attempt})", ex);
                    await Task.Delay(50 * attempt).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    App.LogError("ConfigStore.ReadUnlockedAsync", ex);
                    if (allowFallbackOnIoError) return new GlobalConfig();
                    throw;
                }
            }

            return new GlobalConfig();
        }

        private static GlobalConfig ReadUnlocked(bool allowFallbackOnIoError)
        {
            if (!File.Exists(Path)) return new GlobalConfig();

            const int maxAttempts = 3;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    return JsonSerializer.Deserialize<GlobalConfig>(fs, ReadOptions) ?? new GlobalConfig();
                }
                catch (JsonException ex)
                {
                    App.LogError("ConfigStore.ReadUnlocked", ex);
                    BackupCorruptFile();
                    return new GlobalConfig();
                }
                catch (IOException ex) when (attempt < maxAttempts)
                {
                    App.LogError($"ConfigStore.ReadUnlocked (retry {attempt})", ex);
                    Thread.Sleep(50 * attempt);
                }
                catch (Exception ex)
                {
                    App.LogError("ConfigStore.ReadUnlocked", ex);
                    if (allowFallbackOnIoError) return new GlobalConfig();
                    throw;
                }
            }

            return new GlobalConfig();
        }

        private static void BackupCorruptFile()
        {
            try
            {
                string backup = $"{Path}.corrupt_{DateTime.UtcNow:yyyyMMddHHmmss}.bak";
                File.Copy(Path, backup, overwrite: true);
            }
            catch { }
        }

        private static async Task WriteUnlockedAsync(GlobalConfig config)
        {
            string dir = System.IO.Path.GetDirectoryName(Path)!;
            Directory.CreateDirectory(dir);
            string temp = System.IO.Path.Combine(dir, $"{System.IO.Path.GetFileName(Path)}.{Guid.NewGuid():N}.tmp");

            try
            {
                await using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(fs, config, WriteOptions).ConfigureAwait(false);
                }
                File.Move(temp, Path, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                throw;
            }
        }

        private static void WriteUnlocked(GlobalConfig config)
        {
            string dir = System.IO.Path.GetDirectoryName(Path)!;
            Directory.CreateDirectory(dir);
            string temp = System.IO.Path.Combine(dir, $"{System.IO.Path.GetFileName(Path)}.{Guid.NewGuid():N}.tmp");

            try
            {
                using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(fs, config, WriteOptions);
                }
                File.Move(temp, Path, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                throw;
            }
        }
    }
}
