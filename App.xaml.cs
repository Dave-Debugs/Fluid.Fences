#pragma warning disable CA1001

using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using Application = System.Windows.Application;

namespace DesktopFences
{
    public enum SnapshotResult
    {
        Succeeded,
        NotFound,
        Failed
    }

    public partial class App : Application
    {
        private const int HOTKEY_ID = 9000;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_SHIFT = 0x0004;
        private const uint VK_Z = 0x5A;
        private const long MaxLogFileBytes = 2 * 1024 * 1024;

        private NotifyIcon? _trayIcon;
        private bool _isZenModeActive;
        private CancellationTokenSource? _displayChangeCts;

        public static bool PauseMediaOnRollup { get; set; } = true;

        private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

        public static string ConfigFolder { get; } =
            Environment.GetEnvironmentVariable("FLUIDFENCES_CONFIG_DIR") is { Length: > 0 } customDir
                ? Path.GetFullPath(customDir)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopFences");

        private static readonly string InstanceSuffix =
            Environment.GetEnvironmentVariable("FLUIDFENCES_INSTANCE_ID") is { Length: > 0 } id
                ? $".{id}"
                : string.Empty;

        private static string SingleInstanceMutexName => @"Local\FluidFences.SingleInstance" + InstanceSuffix;
        private static string SurfaceSignalName => @"Local\FluidFences.Surface" + InstanceSuffix;
        private static string SettingsSignalName => @"Local\FluidFences.OpenSettings" + InstanceSuffix;
        private static string ExitSignalName => @"Local\FluidFences.Exit" + InstanceSuffix;

        private static Mutex? _singleInstanceMutex;
        private static bool _ownsMutex;
        private static EventWaitHandle? _surfaceSignal;
        private static EventWaitHandle? _settingsSignal;
        private static EventWaitHandle? _exitSignal;
        private static ManualResetEventSlim? _listenerStopEvent;
        private static readonly SemaphoreSlim _snapshotGate = new(1, 1);

        private const string SettingsArgument = "--settings";
        private const string ExitArgument = "--exit";

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            this.DispatcherUnhandledException += (_, args) =>
            {
                WriteLogLine("debug_exceptions.log", $"[Unhandled] {args.Exception.Message}\n{args.Exception.StackTrace}");
            };

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception ex)
                    WriteLogLine("debug_exceptions.log", $"[AppDomainUnhandled] {ex.Message}\n{ex.StackTrace}");
            };

            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                WriteLogLine("debug_exceptions.log", $"[UnobservedTask] {args.Exception.Message}\n{args.Exception.StackTrace}");
                args.SetObserved();
            };

            bool wantsSettings = e.Args.Any(a =>
                string.Equals(a, SettingsArgument, StringComparison.OrdinalIgnoreCase));
            bool wantsExit = e.Args.Any(a =>
                string.Equals(a, ExitArgument, StringComparison.OrdinalIgnoreCase));

            if (!ClaimSingleInstance())
            {
                if (wantsExit)
                {
                    SignalExitRunningInstance();
                }
                else
                {
                    SignalRunningInstance(wantsSettings);
                }

                Environment.Exit(0);
                return;
            }

            if (wantsExit)
            {
                Environment.Exit(0);
                return;
            }

            string oldFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopFences");
            if (Directory.Exists(oldFolder))
            {
                try
                {
                    void MoveDirectory(string source, string target)
                    {
                        if (!Directory.Exists(target)) Directory.CreateDirectory(target);
                        foreach (string file in Directory.GetFiles(source))
                        {
                            string destFile = Path.Combine(target, Path.GetFileName(file));
                            try { if (!File.Exists(destFile)) File.Move(file, destFile); } catch (Exception ex) { LogError("MoveDirectory File", ex); }
                        }
                        foreach (string dir in Directory.GetDirectories(source))
                        {
                            try { MoveDirectory(dir, Path.Combine(target, Path.GetFileName(dir))); } catch (Exception ex) { LogError("MoveDirectory Dir", ex); }
                        }
                    }
                    MoveDirectory(oldFolder, ConfigFolder);
                    try { Directory.Delete(oldFolder, true); } catch { }
                }
                catch { }
            }

            Directory.CreateDirectory(ConfigFolder);
            Directory.CreateDirectory(Path.Combine(ConfigFolder, "Fences"));

#if DEBUG
            try
            {
                System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = System.Diagnostics.SourceLevels.Warning;
                System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(
                    new System.Diagnostics.TextWriterTraceListener(Path.Combine(ConfigFolder, "wpf_trace.log")));
            }
            catch { }
#endif

            InitialiseTheming();

            CreateTrayIcon();

            StartSurfaceListener();

            ComponentDispatcher.ThreadPreprocessMessage += ComponentDispatcher_ThreadPreprocessMessage;

            bool hotkeyRegistered = NativeMethods.RegisterHotKey(IntPtr.Zero, HOTKEY_ID, MOD_CONTROL | MOD_ALT, VK_Z);
            if (!hotkeyRegistered)
            {
                bool fallbackRegistered = NativeMethods.RegisterHotKey(IntPtr.Zero, HOTKEY_ID, MOD_CONTROL | MOD_SHIFT, VK_Z);
                if (fallbackRegistered) _trayIcon?.ShowBalloonTip(3000, "Hotkey Changed", "Ctrl+Alt+Z is in use by another app. Zen Mode is using Ctrl+Shift+Z instead.", ToolTipIcon.Info);
            }

            SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;

            _ = RestoreSavedFencesAsync();

            if (wantsSettings) OpenSettings();

            Updater.CleanupOldUpdates();
            _ = Updater.CheckAndApplyUpdateAsync(silentCheck: true);
        }

        private static bool ClaimSingleInstance()
        {
            try
            {
                _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);

                if (!createdNew)
                {
                    _ownsMutex = false;
                    _singleInstanceMutex.Dispose();
                    _singleInstanceMutex = null;
                    return false;
                }

                _ownsMutex = true;
                return true;
            }
            catch (AbandonedMutexException)
            {
                _ownsMutex = true;
                return true;
            }
            catch (Exception ex)
            {
                LogError("ClaimSingleInstance", ex);
                _ownsMutex = false;
                return false;
            }
        }

        private static void SignalRunningInstance(bool openSettings = false)
        {
            try
            {
                string name = openSettings ? SettingsSignalName : SurfaceSignalName;
                using EventWaitHandle handle = EventWaitHandle.OpenExisting(name);
                handle.Set();
            }
            catch (Exception ex)
            {
                LogError("SignalRunningInstance", ex);
            }
        }

        private static void SignalExitRunningInstance()
        {
            try
            {
                using EventWaitHandle handle = EventWaitHandle.OpenExisting(ExitSignalName);
                handle.Set();
            }
            catch (Exception ex)
            {
                LogError("SignalExitRunningInstance", ex);
            }
        }

        private void StartSurfaceListener()
        {
            try
            {
                _surfaceSignal = new EventWaitHandle(false, EventResetMode.AutoReset, SurfaceSignalName);
                _settingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, SettingsSignalName);
                _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ExitSignalName);
                _listenerStopEvent = new ManualResetEventSlim(false);

                var handles = new WaitHandle[]
                {
                    _surfaceSignal,
                    _settingsSignal,
                    _exitSignal,
                    _listenerStopEvent.WaitHandle
                };

                var listener = new Thread(() =>
                {
                    while (true)
                    {
                        try
                        {
                            int index = WaitHandle.WaitAny(handles);
                            if (index == 0)
                            {
                                Dispatcher.BeginInvoke(new Action(SurfaceFromSecondLaunch));
                            }
                            else if (index == 1)
                            {
                                Dispatcher.BeginInvoke(new Action(OpenSettings));
                            }
                            else if (index == 2)
                            {
                                Dispatcher.BeginInvoke(new Action(() => _ = ExitAppAsync()));
                                return;
                            }
                            else
                            {
                                return;
                            }
                        }
                        catch (ObjectDisposedException) { return; }
                        catch (Exception ex) { LogError("SurfaceListener", ex); return; }
                    }
                })
                {
                    IsBackground = true,
                    Name = "FluidFences.SignalListener"
                };
                listener.Start();
            }
            catch (Exception ex) { LogError("StartSurfaceListener", ex); }
        }

        private void SurfaceFromSecondLaunch()
        {
            try
            {
                if (_isZenModeActive) ToggleZenMode();

                _trayIcon?.ShowBalloonTip(
                    3000,
                    "Fluid Fences is already running",
                    "Look for the icon here in your system tray. Double-click it to open Settings.",
                    ToolTipIcon.Info);
            }
            catch (Exception ex) { LogError("SurfaceFromSecondLaunch", ex); }
        }

        public static string ActiveThemeId { get; set; } = "obsidian-prism";

        private static void InitialiseTheming()
        {
            try
            {
                Core.Theming.ThemeCatalog.Reload();

                string configPath = Path.Combine(ConfigFolder, "global_config.json");

                GlobalConfig? config = Core.Theming.ThemeMigration.TryReadConfig(configPath);

                bool needsMigration = config is not null && string.IsNullOrWhiteSpace(config.ThemeId);
                if (needsMigration) Core.Theming.ThemeMigration.BackupConfigOnce(configPath);

                string themeId = Core.Theming.ThemeMigration.ResolveThemeId(config);

                ActiveThemeId = themeId;

                Core.Theming.ThemeEngine.Apply(Core.Theming.ThemeCatalog.Get(themeId), animate: false);

                Core.Theming.ThemeLog.Info("Startup",
                    $"catalog={Core.Theming.ThemeCatalog.Themes.Count} themes, " +
                    $"configRead={(config is null ? "NULL" : "ok")}, " +
                    $"legacyName='{config?.Theme?.ThemeName}', " +
                    $"needsMigration={needsMigration}, resolved='{themeId}'");

                if (config is not null)
                {
                    PauseMediaOnRollup = config.PauseMediaOnRollup;

                    if (needsMigration)
                    {
                        ConfigStore.Update(c => c.ThemeId = themeId);
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("InitialiseTheming", ex);
                try
                {
                    Core.Theming.ThemeEngine.Apply(Core.Theming.ThemeCatalog.Default, animate: false);
                }
                catch { }
            }
        }

        public static void SetTheme(string themeId, bool animate = true,
                                    string? mediaPath = null, double? mediaOpacity = null)
        {
            var theme = Core.Theming.ThemeCatalog.Get(themeId);
            ActiveThemeId = theme.Id;
            Core.Theming.ThemeEngine.Apply(theme, animate);
            _ = PersistThemeIdAsync(theme.Id, mediaPath, mediaOpacity);
        }

        private static Task<bool> PersistThemeIdAsync(
            string themeId, string? mediaPath = null, double? mediaOpacity = null)
            => ConfigStore.UpdateAsync(config =>
            {
                config.ThemeId = themeId;

                var resolved = Core.Theming.ThemeCatalog.Get(themeId);
                config.Theme.ThemeName = resolved.Name;
                config.Theme.BackgroundColor = resolved.Colors.Background ?? config.Theme.BackgroundColor;
                config.Theme.HeaderColor = resolved.Colors.Header ?? config.Theme.HeaderColor;
                config.Theme.FontColor = resolved.Colors.TextPrimary ?? config.Theme.FontColor;
                config.Theme.SecondaryFontColor = resolved.Colors.TextSecondary ?? config.Theme.SecondaryFontColor;
                config.Theme.AccentColor = resolved.Colors.Accent ?? config.Theme.AccentColor;

                if (mediaPath is not null) config.Theme.BackgroundMediaPath = mediaPath;
                if (mediaOpacity is double mo) config.Theme.MediaOpacity = mo;
            });

        private static readonly object _logLock = new();

        internal static void WriteLogLine(string fileName, string line)
        {
            try
            {
                lock (_logLock)
                {
                    Directory.CreateDirectory(ConfigFolder);
                    string logPath = Path.Combine(ConfigFolder, fileName);
                    if (File.Exists(logPath) && new FileInfo(logPath).Length >= MaxLogFileBytes)
                    {
                        string backup = logPath + ".old";
                        File.Move(logPath, backup, overwrite: true);
                    }
                    File.AppendAllText(logPath, line + Environment.NewLine);
                }
            }
            catch { }
        }

        public static void LogError(string context, Exception ex)
        {
            WriteLogLine("app_error.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}: {ex.Message}\n{ex.StackTrace}");
        }

        private async void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e)
        {
            try
            {
                DesktopFences.MainWindow.InvalidateScreenCache();
                ThemeUtility.InvalidateWallpaperCache();

                _displayChangeCts?.Cancel();
                _displayChangeCts?.Dispose();
                var cts = new CancellationTokenSource();
                _displayChangeCts = cts;

                await Task.Delay(1000, cts.Token);
                if (cts.IsCancellationRequested) return;

                await Dispatcher.InvokeAsync(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    var fences = Current.Windows.OfType<MainWindow>().ToArray();
                    foreach (var fence in fences)
                    {
                        if (fence.IsLoaded) fence.ClampToScreen();
                    }
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogError("DisplaySettingsChanged", ex);
            }
        }

        private void ComponentDispatcher_ThreadPreprocessMessage(ref MSG msg, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg.message == WM_HOTKEY && (nint)msg.wParam == HOTKEY_ID)
            {
                ToggleZenMode();
                handled = true;
            }
        }

        public bool IsZenModeActive => _isZenModeActive;

        private void ToggleZenMode()
        {
            _isZenModeActive = !_isZenModeActive;
            foreach (Window window in Current.Windows)
            {
                if (window is MainWindow fence && !fence.IsPendingDeletion)
                {
                    fence.Visibility = _isZenModeActive ? Visibility.Hidden : Visibility.Visible;
                }
            }
        }

        public static async Task<SnapshotResult> SaveCurrentLayoutAsync()
        {
            await _snapshotGate.WaitAsync();
            string? tempPath = null;
            try
            {
                string snapshotPath = Path.Combine(ConfigFolder, "snapshot.json");
                List<SnapshotData> snapshots = [];

                var fences = Current.Windows.OfType<MainWindow>().ToArray();
                foreach (var fence in fences)
                {
                    if (!fence.IsPendingDeletion && (fence.Visibility == Visibility.Visible || (Current is App app && app.IsZenModeActive)))
                    {
                        snapshots.Add(new SnapshotData
                        {
                            FenceId = fence.GetFenceId(),
                            Left = fence.Left,
                            Top = fence.Top,
                            Width = fence.Width,
                            Height = fence.Height,
                            IsRolledUp = fence.GetIsRolledUp(),
                            ExpandedLeft = fence.ExpandedLeft,
                            ExpandedTop = fence.ExpandedTop,
                            ExpandedWidth = fence.ExpandedWidth,
                            ExpandedHeight = fence.ExpandedHeight
                        });
                    }
                }

                Directory.CreateDirectory(ConfigFolder);
                tempPath = $"{snapshotPath}.{Guid.NewGuid():N}.tmp";
                await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(snapshots, _jsonOptions));
                File.Move(tempPath, snapshotPath, overwrite: true);
                tempPath = null;
                return SnapshotResult.Succeeded;
            }
            catch (Exception ex)
            {
                if (tempPath is not null)
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                }
                LogError("SaveCurrentLayout", ex);
                return SnapshotResult.Failed;
            }
            finally { _snapshotGate.Release(); }
        }

        public static async Task<SnapshotResult> RestoreLayoutSnapshotAsync()
        {
            await _snapshotGate.WaitAsync();
            try
            {
                string snapshotPath = Path.Combine(ConfigFolder, "snapshot.json");
                if (!File.Exists(snapshotPath)) return SnapshotResult.NotFound;

                string json = await File.ReadAllTextAsync(snapshotPath);
                var snapshots = JsonSerializer.Deserialize<List<SnapshotData>>(json);

                if (snapshots is null || snapshots.Count == 0) return SnapshotResult.NotFound;

                var fences = Current.Windows.OfType<MainWindow>().ToArray();
                foreach (var snap in snapshots)
                {
                    foreach (var fence in fences)
                    {
                        if (fence.GetFenceId() == snap.FenceId)
                        {
                            fence.RestoreSnapshot(snap);
                            break;
                        }
                    }
                }
                return SnapshotResult.Succeeded;
            }
            catch (Exception ex)
            {
                LogError("RestoreLayoutSnapshot", ex);
                return SnapshotResult.Failed;
            }
            finally { _snapshotGate.Release(); }
        }

        private static async Task RestoreSavedFencesAsync()
        {
            try
            {
                string saveDirectory = Path.Combine(ConfigFolder, "Fences");

                if (Directory.Exists(saveDirectory))
                {
                    string[] savedFences = Directory.GetFiles(saveDirectory, "*.json");
                    if (savedFences.Length > 0)
                    {
                        foreach (string file in savedFences)
                        {
                            string fenceId = Path.GetFileNameWithoutExtension(file);
                            if (fenceId.Equals("designer", StringComparison.OrdinalIgnoreCase)) continue;

                            MainWindow restoredFence = new(fenceId);
                            restoredFence.Show();

                            await Task.Delay(50);
                        }
                        return;
                    }
                }
            }
            catch (Exception ex) { LogError("RestoreSavedFences", ex); }

            MainWindow defaultFence = new(Guid.NewGuid().ToString())
            {
                Left = SystemParameters.PrimaryScreenWidth - 350,
                Top = 100,
                Width = 250,
                Height = 300
            };
            defaultFence.FenceTitle = "My First Fence";
            defaultFence.Show();
        }

        private void CreateTrayIcon()
        {
            var iconUri = new Uri("pack://application:,,,/Assets/FF 256.ico");
            using var iconStream = Application.GetResourceStream(iconUri)?.Stream ?? throw new FileNotFoundException("Could not load the embedded icon resource.");

            _trayIcon = new NotifyIcon { Icon = new Icon(iconStream), Visible = true, Text = "Fluid Fences" };

            var trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Toggle Zen Mode", null, (_, _) => ToggleZenMode());
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Create New Fence", null, (_, _) => CreateNewFence());
            trayMenu.Items.Add("Create Folder Portal...", null, (_, _) => CreateNewPortal());
            trayMenu.Items.Add("Auto-Organize Desktop", null, (_, _) => DesktopFences.MainWindow.AutoOrganizeDesktop());
            trayMenu.Items.Add("Check for Updates", null, (_, _) => _ = Updater.CheckAndApplyUpdateAsync(silentCheck: false));
            trayMenu.Items.Add("Open Settings", null, (_, _) => OpenSettings());
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Exit Fluid Fences", null, (_, _) => _ = ExitAppAsync());

            _trayIcon.ContextMenuStrip = trayMenu;
            _trayIcon.DoubleClick += (_, _) => OpenSettings();
        }

        private void DisposeTrayIcon()
        {
            if (_trayIcon is null) return;
            try
            {
                _trayIcon.Visible = false;
                var menu = _trayIcon.ContextMenuStrip;
                var icon = _trayIcon.Icon;
                _trayIcon.ContextMenuStrip = null;
                _trayIcon.Icon = null;
                menu?.Dispose();
                icon?.Dispose();
                _trayIcon.Dispose();
            }
            catch { }
            finally
            {
                _trayIcon = null;
            }
        }

        private static void CreateNewFence() { MainWindow newFence = new(Guid.NewGuid().ToString()); newFence.Show(); }

        private static void CreateNewPortal()
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select a folder to mirror in the Portal Fence" };
            if (dialog.ShowDialog() == true)
            {
                MainWindow portalFence = new(Guid.NewGuid().ToString())
                {
                    Left = SystemParameters.PrimaryScreenWidth / 2 - 125,
                    Top = SystemParameters.PrimaryScreenHeight / 2 - 150
                };
                portalFence.MakePortal(dialog.FolderName);
                portalFence.Show();
            }
        }

        private void OpenSettings()
        {
            var existing = Current.Windows.OfType<SettingsWindow>().FirstOrDefault();
            if (existing is not null)
            {
                existing.Show();
                if (existing.WindowState == WindowState.Minimized)
                    existing.WindowState = WindowState.Normal;

                existing.SwitchToTab("ThemeEditorGrid");
                existing.Activate();
                existing.Topmost = true;
                existing.Topmost = false;
                existing.Focus();
                return;
            }

            SettingsWindow settings = new(null);
            settings.SwitchToTab("ThemeEditorGrid");
            settings.Show();
            settings.Activate();
        }

        private async Task ExitAppAsync()
        {
            DisposeTrayIcon();

            try
            {
                foreach (SettingsWindow settings in Current.Windows.OfType<SettingsWindow>().ToList())
                {
                    try { settings.CommitPendingDeletions(); }
                    catch (Exception ex) { LogError("ExitCommitDeletions", ex); }
                }

                foreach (MainWindow fence in Current.Windows.OfType<MainWindow>().ToList())
                {
                    try { await fence.FlushPendingSaveAsync(); }
                    catch (Exception ex) { LogError("ExitFlush", ex); }
                }
            }
            catch (Exception ex) { LogError("ExitApp", ex); }

            Current.Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            ComponentDispatcher.ThreadPreprocessMessage -= ComponentDispatcher_ThreadPreprocessMessage;
            SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
            _displayChangeCts?.Cancel();
            _displayChangeCts?.Dispose();
            _displayChangeCts = null;

            NativeMethods.UnregisterHotKey(IntPtr.Zero, HOTKEY_ID);
            DisposeTrayIcon();

            try
            {
                _listenerStopEvent?.Set();
                _surfaceSignal?.Dispose();
                _settingsSignal?.Dispose();
                _exitSignal?.Dispose();
                _listenerStopEvent?.Dispose();
                if (_ownsMutex)
                {
                    _singleInstanceMutex?.ReleaseMutex();
                    _ownsMutex = false;
                }
                _singleInstanceMutex?.Dispose();
            }
            catch (Exception ex) { LogError("ReleaseSingleInstance", ex); }
            finally
            {
                _surfaceSignal = null;
                _settingsSignal = null;
                _exitSignal = null;
                _listenerStopEvent = null;
                _singleInstanceMutex = null;
            }

            base.OnExit(e);
        }
    }
}
