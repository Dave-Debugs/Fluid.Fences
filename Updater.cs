using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace DesktopFences
{
    public static partial class Updater
    {
        public static string CurrentVersion { get; } = ResolveCurrentVersion();

        private static string ResolveCurrentVersion()
        {
            try
            {
                var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
            }
            catch { return "0.0.0"; }
        }

        private static readonly string[] GitHubApiUrls =
        [
            "https://api.github.com/repos/Dave-Debugs/Fluid.Fences/releases/latest",
            "https://api.github.com/repos/DaveDebugs/Fluid.Fences/releases/latest"
        ];

        [GeneratedRegex("^[A-Fa-f0-9]{64}$", RegexOptions.Compiled)]
        private static partial Regex Sha256HexRegex();

        private static readonly HttpClient _httpClient = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                ConnectTimeout = TimeSpan.FromSeconds(15)
            };
            var client = new HttpClient(handler);
            client.DefaultRequestHeaders.Add("User-Agent", "FluidFences-Updater");
            return client;
        }

        public class GitHubRelease
        {
            [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
            [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = [];
        }

        public class GitHubAsset
        {
            [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; } = "";
            [JsonPropertyName("name")] public string Name { get; set; } = "";
        }

        private static async Task<GitHubRelease?> FetchLatestReleaseAsync()
        {
            Exception? lastError = null;
            foreach (string url in GitHubApiUrls)
            {
                try
                {
                    var release = await _httpClient.GetFromJsonAsync<GitHubRelease>(url);
                    if (release is not null && !string.IsNullOrEmpty(release.TagName))
                        return release;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }
            if (lastError is not null) throw lastError;
            return null;
        }

        public static async Task CheckAndApplyUpdateAsync(bool silentCheck = true)
        {
            try
            {
                var release = await FetchLatestReleaseAsync();
                if (release == null || string.IsNullOrEmpty(release.TagName)) return;

                string latestTag = release.TagName.TrimStart('v', 'V');

                if (Version.TryParse(latestTag, out Version? latestVersion) && Version.TryParse(CurrentVersion, out Version? currentVersion))
                {
                    if (latestVersion > currentVersion)
                    {
                        GitHubAsset? exeAsset =
                            release.Assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                                            && a.Name.Contains("setup", StringComparison.OrdinalIgnoreCase))
                            ?? release.Assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

                        GitHubAsset? checksumAsset = release.Assets.FirstOrDefault(a => a.Name.Equals("checksum.txt", StringComparison.OrdinalIgnoreCase));

                        if (exeAsset != null && checksumAsset != null)
                        {
                            if (MessageBox.Show($"A secure update to Fluid Fences (v{latestTag}) is available!\n\nWould you like to download and install it now?", "Update Available", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                            {
                                await ApplySecureUpdateAsync(exeAsset.DownloadUrl, checksumAsset.DownloadUrl, exeAsset.Name);
                            }
                        }
                        else if (!silentCheck)
                        {
                            MessageBox.Show("An update was found, but the security checksum is missing from the server. Update aborted to ensure your safety.", "Security Alert", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    }
                    else if (!silentCheck)
                    {
                        MessageBox.Show("You are running the latest version of Fluid Fences!", "Up to Date", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!silentCheck) MessageBox.Show($"Could not check for updates.\n{ex.Message}", "Update Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static bool IsTrustedDownloadUri(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttps) return false;
            return uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                   uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase) ||
                   uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task ApplySecureUpdateAsync(string installerUrl, string checksumUrl, string assetName)
        {
            if (!IsTrustedDownloadUri(installerUrl) || !IsTrustedDownloadUri(checksumUrl))
            {
                MessageBox.Show("Security Alert: Untrusted update URL detected. Aborting update.",
                                "Security Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string updateDir = Path.Combine(App.ConfigFolder, "Updates");
            Directory.CreateDirectory(updateDir);
            string installerPath = Path.Combine(updateDir, $"FluidFences_Update_{Guid.NewGuid():N}.exe");

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

                string checksumContent = await _httpClient.GetStringAsync(checksumUrl, cts.Token);
                string expectedHash = "";
                string targetFile = Path.GetFileName(assetName);
                var nonEmptyLines = checksumContent.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

                foreach (var line in nonEmptyLines)
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("SHA256", StringComparison.OrdinalIgnoreCase) && trimmed.Contains(targetFile, StringComparison.OrdinalIgnoreCase))
                    {
                        int eqIdx = trimmed.LastIndexOf('=');
                        if (eqIdx >= 0)
                        {
                            string candidate = trimmed.Substring(eqIdx + 1).Trim();
                            if (Sha256HexRegex().IsMatch(candidate))
                            {
                                expectedHash = candidate.ToUpperInvariant();
                                break;
                            }
                        }
                    }

                    var parts = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && Sha256HexRegex().IsMatch(parts[0]))
                    {
                        string filePart = string.Join(" ", parts.Skip(1)).TrimStart('*', ' ');
                        if (string.Equals(filePart, targetFile, StringComparison.OrdinalIgnoreCase) ||
                            filePart.EndsWith(targetFile, StringComparison.OrdinalIgnoreCase))
                        {
                            expectedHash = parts[0].ToUpperInvariant();
                            break;
                        }
                    }
                    else if (nonEmptyLines.Length == 1 && parts.Length == 1 && Sha256HexRegex().IsMatch(parts[0]) && string.IsNullOrEmpty(expectedHash))
                    {
                        expectedHash = parts[0].ToUpperInvariant();
                    }
                }

                if (string.IsNullOrEmpty(expectedHash))
                {
                    MessageBox.Show("Security Alert: The downloaded checksum format was invalid or did not contain a hash for this file. The update has been aborted.",
                                    "Verification Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                using (var response = await _httpClient.GetAsync(installerUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token))
                {
                    response.EnsureSuccessStatusCode();

                    await using (var netStream = await response.Content.ReadAsStreamAsync(cts.Token))
                    await using (var fileStream = new FileStream(installerPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                    using (var sha256 = SHA256.Create())
                    {
                        byte[] buffer = new byte[81920];
                        int bytesRead;
                        while ((bytesRead = await netStream.ReadAsync(buffer, cts.Token)) > 0)
                        {
                            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cts.Token);
                            sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
                        }
                        sha256.TransformFinalBlock([], 0, 0);

                        string actualHash = Convert.ToHexString(sha256.Hash!).ToUpperInvariant();

                        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                        {
                            fileStream.Close();
                            TryDelete(installerPath);
                            MessageBox.Show(
                                "Security Alert: The downloaded update was corrupted or tampered with. The update has been aborted.",
                                "Verification Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                            return;
                        }
                    }
                }

                ProcessStartInfo psi = new(installerPath)
                {
                    Arguments = "/SILENT /NORESTART",
                    UseShellExecute = true,
                    WorkingDirectory = updateDir
                };

                try
                {
                    var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        Application.Current.Shutdown();
                    }
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    TryDelete(installerPath);
                    MessageBox.Show(
                        "The update was cancelled because Administrator permission was not granted.",
                        "Update Cancelled", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                TryDelete(installerPath);
                MessageBox.Show($"Failed to download or start the update:\n{ex.Message}",
                                "Update Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        public static void CleanupOldUpdates()
        {
            try
            {
                string updateDir = Path.Combine(App.ConfigFolder, "Updates");
                if (Directory.Exists(updateDir))
                {
                    foreach (string leftover in Directory.GetFiles(updateDir, "FluidFences_Update_*.exe"))
                        TryDelete(leftover);
                }
            }
            catch { }

            try
            {
                foreach (string leftover in Directory.GetFiles(Path.GetTempPath(), "FluidFences_Update_*.exe"))
                    TryDelete(leftover);
            }
            catch { }
        }
    }
}
