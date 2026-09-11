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

        public static async Task<GlobalConfig> ReadAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try { return ReadUnlocked(); }
            finally { _gate.Release(); }
        }

        public static async Task UpdateAsync(Action<GlobalConfig> mutate)
        {
            ArgumentNullException.ThrowIfNull(mutate);

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var config = ReadUnlocked();
                mutate(config);
                WriteUnlocked(config);
            }
            catch (Exception ex) { App.LogError("ConfigStore.UpdateAsync", ex); }
            finally { _gate.Release(); }
        }

        public static void Update(Action<GlobalConfig> mutate)
        {
            ArgumentNullException.ThrowIfNull(mutate);

            _gate.Wait();
            try
            {
                var config = ReadUnlocked();
                mutate(config);
                WriteUnlocked(config);
            }
            catch (Exception ex) { App.LogError("ConfigStore.Update", ex); }
            finally { _gate.Release(); }
        }

        private static GlobalConfig ReadUnlocked()
        {
            if (!File.Exists(Path)) return new GlobalConfig();

            try
            {
                using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                string json = reader.ReadToEnd();
                return JsonSerializer.Deserialize<GlobalConfig>(json, ReadOptions) ?? new GlobalConfig();
            }
            catch (Exception ex)
            {
                App.LogError("ConfigStore.ReadUnlocked", ex);
                try
                {
                    string backup = $"{Path}.corrupt_{DateTime.UtcNow:yyyyMMddHHmmss}.bak";
                    File.Copy(Path, backup, overwrite: true);
                }
                catch { }

                return new GlobalConfig();
            }
        }

        private static void WriteUnlocked(GlobalConfig config)
        {
            string dir = System.IO.Path.GetDirectoryName(Path)!;
            Directory.CreateDirectory(dir);
            string temp = System.IO.Path.Combine(dir, $"{System.IO.Path.GetFileName(Path)}.{Guid.NewGuid():N}.tmp");

            try
            {
                string json = JsonSerializer.Serialize(config, WriteOptions);
                File.WriteAllText(temp, json);
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
