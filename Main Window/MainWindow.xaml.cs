using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopFences
{
    public partial class MainWindow : Window, IDisposable
    {
        private static readonly HashSet<string> _imageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".webp" };

        private static readonly HashSet<string> _dangerousExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".com", ".msi", ".msc", ".cpl", ".bat", ".cmd", ".ps1", ".psm1", ".psd1",
            ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".ws", ".hta", ".scr", ".pif",
            ".reg", ".jar", ".appx", ".msix", ".scf", ".lnk", ".url", ".appref-ms", ".diagcab",
            ".search-ms", ".library-ms", ".settingcontent-ms", ".chm", ".inf", ".msp", ".py", ".pyw"
        };

        private static System.Drawing.Rectangle[]? _cachedWorkingAreas;
        private static readonly object _workingAreaLock = new();

        public static void InvalidateScreenCache()
        {
            lock (_workingAreaLock)
            {
                _cachedWorkingAreas = null;
            }
        }

        private static System.Drawing.Rectangle[] GetCachedWorkingAreas()
        {
            lock (_workingAreaLock)
            {
                if (_cachedWorkingAreas == null)
                {
                    var screens = System.Windows.Forms.Screen.AllScreens;
                    var areas = new System.Drawing.Rectangle[screens.Length];
                    for (int i = 0; i < screens.Length; i++)
                    {
                        areas[i] = screens[i].WorkingArea;
                    }
                    _cachedWorkingAreas = areas;
                }
                return _cachedWorkingAreas;
            }
        }

        public enum DockState { Floating, Top, Bottom, Left, Right }

        private readonly DispatcherTimer _saveTimer = new();
        private readonly DispatcherTimer _portalDebounceTimer = new();
        private readonly List<DispatcherTimer> _tabDragTimers = [];
        private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
        private readonly List<string> _currentFiles = [];
        private readonly List<StackPanel> _selectedItems = [];

        private static readonly SolidColorBrush _fallbackHighlightBrush = CreateFrozenBrush(System.Windows.Media.Color.FromArgb(85, 0, 120, 215));
        private static readonly SolidColorBrush _hitTestBackgroundBrush = CreateFrozenBrush(System.Windows.Media.Color.FromArgb(1, 0, 0, 0));

        private static SolidColorBrush CreateFrozenBrush(System.Windows.Media.Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private SolidColorBrush _highlightBrush =>
            TryFindResource("CustomSelectionFillBrush") as SolidColorBrush
            ?? _fallbackHighlightBrush;

        private readonly SemaphoreSlim _iconSemaphore = new(4, 4);
        private readonly SemaphoreSlim _saveGate = new(1, 1);
        private CancellationTokenSource? _portalCts;

        public string FenceId { get; } = string.Empty;
        private readonly string _saveFilePath = string.Empty;
        public double CurrentIconSize { get; set; } = 48;
        private List<FenceTab> _tabs = [];
        private int _activeTabIndex;
        private bool _hasInjectedTab;
        private System.Windows.Point? _tabDragStartPoint;

        private MainWindow? _hoveredTargetFence;

        private bool _isRolledUp;
        private double _expandedHeight = 300;
        private double _expandedWidth = 250;
        private double _expandedLeft;
        private double _expandedTop;
        private const double HEADER_SIZE = 35;

        public double ExpandedLeft => _expandedLeft;
        public double ExpandedTop => _expandedTop;
        public double ExpandedWidth => _expandedWidth;
        public double ExpandedHeight => _expandedHeight;

        private DockState _dockState = DockState.Floating;
        private bool _isTemporarilyRevealed;
        private string _saveSortMethod = "None";
        private bool _isContextMenuOpen;
        private bool _isDeleted;
        private bool _loadFailed;
        private bool _isClosing;
        private string? _pendingPortalPath;
        private bool _resourcesReleased;
        private HwndSourceHook? _wndProcHook;
        private HwndSource? _hwndSource;
        private long _portalRefreshGeneration;
        private System.Windows.Threading.DispatcherTimer? _portalRetryTimer;

        private bool _isPortal;
        private string _portalPath = "";
        private FileSystemWatcher? _portalWatcher;

        private Color _currentFenceColor = Colors.Black;
        private double _currentOpacity = 0.7;
        private bool _autoMatchColor;

        private string _colorSource = FenceColorSource.Theme;

        private bool _globalGhostMode;
        private int _ghostModeOverride;
        private Core.ThemeSettings _currentTheme = new();

        private System.Windows.Point _selectionStartPoint;
        private bool _isDraggingSelectionBox;
        private bool _isManualDragging;
        private System.Windows.Point _manualDragStartMouse;
        private double _manualDragStartLeft;
        private double _manualDragStartTop;
        private bool _wasRolledUpBeforeDrag;

        public string FenceTitle
        {
            get => TitleText.Text;
            set
            {
                TitleText.Text = value;
                System.Windows.Automation.AutomationProperties.SetName(this, value ?? string.Empty);
            }
        }
        public string FenceSortMethod { get => _saveSortMethod; set => _saveSortMethod = value; }
        public string AutoSortExtensions { get; set; } = "";
        public bool ShowSearch { get; set; } = true;
        public Color FenceColor { get => _currentFenceColor; }
        public double FenceOpacity { get => _currentOpacity; }
        public bool AutoMatchColor { get => _autoMatchColor; set => _autoMatchColor = value; }

        public string ColorSource
        {
            get => _colorSource;
            set { _colorSource = value; ResolveBodyColor(); SaveFenceState(); }
        }
        public int GhostModeOverride { get => _ghostModeOverride; set => _ghostModeOverride = value; }

        public string GetFenceId() { return FenceId; }
        public bool GetIsRolledUp() { return _isRolledUp; }

        public static readonly DependencyProperty GlassOpacityProperty =
            DependencyProperty.Register("GlassOpacity", typeof(double), typeof(MainWindow),
            new PropertyMetadata(1.0, OnGlassOpacityChanged));

        public double GlassOpacity
        {
            get { return (double)GetValue(GlassOpacityProperty); }
            set { SetValue(GlassOpacityProperty, value); }
        }

        private static void OnGlassOpacityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MainWindow window)
            {
                ApplyAcrylicToHwnd(new WindowInteropHelper(window).Handle, window._currentFenceColor, window._currentOpacity * (double)e.NewValue);
            }
        }

        public void SetFenceColor(Color color, double opacity)
        {
            _currentFenceColor = color; _currentOpacity = opacity;
            _colorSource = FenceColorSource.Custom;
            ApplyAcrylicBlur(color, opacity);
            SaveFenceState();
        }

        public void UseThemeColor()
        {
            _autoMatchColor = false;
            _colorSource = FenceColorSource.Theme;
            ResolveBodyColor();
            SaveFenceState();
        }
        public void DashboardSaveAndRefresh() { ApplySorting(); SaveFenceState(); AnimateGhostMode(false); }
        public void UpdateGlobalGhostMode(bool isEnabled) { _globalGhostMode = isEnabled; AnimateGhostMode(false); }

        public void UpdateSearchVisibility()
        {
            if (SearchIcon is not null) SearchIcon.Visibility = ShowSearch ? Visibility.Visible : Visibility.Collapsed;
            if (SearchBox is not null) { SearchBox.Visibility = Visibility.Collapsed; SearchBox.Text = string.Empty; }
        }

        public void DashboardDelete()
        {
            ProcessVaultOnDeletion();
            _isDeleted = true;
            try { if (File.Exists(_saveFilePath)) File.Delete(_saveFilePath); } catch (Exception ex) { LogError("DashboardDelete", ex); }
            this.Close();
        }

        public void RestoreSnapshot(SnapshotData snap)
        {
            this.BeginAnimation(Window.LeftProperty, null);
            this.BeginAnimation(Window.TopProperty, null);
            this.BeginAnimation(Window.WidthProperty, null);
            this.BeginAnimation(Window.HeightProperty, null);

            _isRolledUp = snap.IsRolledUp;
            _isTemporarilyRevealed = false;

            this.Left = snap.Left;
            this.Top = snap.Top;
            this.Width = snap.Width;
            this.Height = snap.Height;

            _expandedLeft = snap.ExpandedLeft > 0 ? snap.ExpandedLeft : snap.Left;
            _expandedTop = snap.ExpandedTop > 0 ? snap.ExpandedTop : snap.Top;
            _expandedWidth = snap.ExpandedWidth > 0 ? snap.ExpandedWidth : snap.Width;
            _expandedHeight = snap.ExpandedHeight > 0 ? snap.ExpandedHeight : snap.Height;

            DetermineDockState(); UpdateDockOrientation();
            SaveFenceState();
        }

        private Rect GetCurrentScreenWorkingAreaLogical()
        {
            var helper = new WindowInteropHelper(this);
            var screen = System.Windows.Forms.Screen.FromHandle(helper.Handle);
            if (screen == null)
            {
                return new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
            }

            var workArea = screen.WorkingArea;
            try
            {
                if (PresentationSource.FromVisual(this) != null)
                {
                    var relTopLeft = PointFromScreen(new System.Windows.Point(workArea.Left, workArea.Top));
                    var relBottomRight = PointFromScreen(new System.Windows.Point(workArea.Right, workArea.Bottom));
                    double left = this.Left + relTopLeft.X;
                    double top = this.Top + relTopLeft.Y;
                    double width = relBottomRight.X - relTopLeft.X;
                    double height = relBottomRight.Y - relTopLeft.Y;
                    return new Rect(left, top, Math.Max(0, width), Math.Max(0, height));
                }
            }
            catch
            {
            }

            double dpiX = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double dpiY = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            return new Rect(workArea.Left / dpiX, workArea.Top / dpiY, workArea.Width / dpiX, workArea.Height / dpiY);
        }

        public void ClampToScreen()
        {
            Rect workArea = GetCurrentScreenWorkingAreaLogical();
            double logicalWidth = workArea.Width;
            double logicalHeight = workArea.Height;
            double logicalLeft = workArea.Left;
            double logicalTop = workArea.Top;
            double logicalRight = workArea.Right;
            double logicalBottom = workArea.Bottom;

            bool changed = false;

            if (_expandedWidth > logicalWidth) { _expandedWidth = logicalWidth; changed = true; }
            if (_expandedHeight > logicalHeight) { _expandedHeight = logicalHeight; changed = true; }

            if (_expandedLeft > logicalRight - 50) { _expandedLeft = logicalRight - _expandedWidth; changed = true; }
            if (_expandedTop > logicalBottom - 50) { _expandedTop = logicalBottom - _expandedHeight; changed = true; }

            if (_expandedLeft < logicalLeft) { _expandedLeft = logicalLeft; changed = true; }
            if (_expandedTop < logicalTop) { _expandedTop = logicalTop; changed = true; }

            if (changed)
            {
                if (!_isRolledUp && !_isTemporarilyRevealed)
                {
                    this.Left = _expandedLeft;
                    this.Top = _expandedTop;
                    this.Width = _expandedWidth;
                    this.Height = _expandedHeight;
                }
                else
                {
                    if (_dockState == DockState.Bottom) { this.Top = _expandedTop + (_expandedHeight - HEADER_SIZE); this.Left = _expandedLeft; }
                    else if (_dockState == DockState.Right) { this.Left = _expandedLeft + (_expandedWidth - HEADER_SIZE); this.Top = _expandedTop; }
                    else { this.Left = _expandedLeft; this.Top = _expandedTop; }
                }

                DetermineDockState();
                UpdateDockOrientation();
                SaveFenceState();
            }
        }

        private void ProcessVaultOnDeletion()
        {
            if (_isPortal) return;
            try
            {
                bool restoreFiles = true;

                try
                {
                    GlobalConfig config = Task.Run(() => ConfigStore.ReadAsync()).GetAwaiter().GetResult();
                    restoreFiles = config.RestoreFilesOnDelete;
                    App.PauseMediaOnRollup = config.PauseMediaOnRollup;
                }
                catch (Exception ex) { App.LogError("StartupLoadConfig", ex); }

                string storageDir = Path.Combine(App.ConfigFolder, "Storage", FenceId);

                if (Directory.Exists(storageDir))
                {
                    if (restoreFiles)
                    {
                        var allEntries = Directory.GetFileSystemEntries(storageDir);
                        string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                        int failed = 0;

                        foreach (string file in allEntries)
                        {
                            string fileName = Path.GetFileName(file);
                            string dest = Path.Combine(desktopPath, fileName);

                            int counter = 1;
                            while (File.Exists(dest) || Directory.Exists(dest))
                            {
                                string nameOnly = Path.GetFileNameWithoutExtension(fileName);
                                string ext = Path.GetExtension(fileName);
                                dest = Path.Combine(desktopPath, $"{nameOnly} ({counter}){ext}");
                                counter++;
                            }

                            try
                            {
                                if (File.Exists(file)) File.Move(file, dest);
                                else if (Directory.Exists(file)) Directory.Move(file, dest);
                            }
                            catch (Exception ex)
                            {

                                failed++;
                                LogError($"RestoreToDesktop failed for '{fileName}'", ex);
                            }
                        }

                        try
                        {
                            if (Directory.Exists(storageDir))
                            {
                                bool empty = Directory.GetFileSystemEntries(storageDir).Length == 0;
                                if (empty)
                                {
                                    Directory.Delete(storageDir, true);
                                }
                                else
                                {
                                    LogError($"RestoreToDesktop left {failed} item(s) in the vault; recycling rather than deleting",
                                             new IOException("restore incomplete"));
                                    SendToRecycleBin(storageDir);
                                }
                            }
                        }
                        catch (Exception ex) { LogError("DisposeVaultAfterRestore", ex); }
                    }
                    else
                    {
                        SendToRecycleBin(storageDir);
                    }
                }
            }
            catch (Exception ex) { LogError("ProcessVaultOnDeletion", ex); }
        }

        private static string SanitizeFenceId(string? id)
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

        public MainWindow()
        {
            InitializeComponent();
            FenceId = "DESIGNER";
            _saveFilePath = Path.Combine(App.ConfigFolder, "Fences", "designer.json");
            System.Windows.Automation.AutomationProperties.SetAutomationId(this, $"FenceWindow_{FenceId}");
            System.Windows.Automation.AutomationProperties.SetName(this, FenceTitle ?? "Fluid Fence");

            _saveTimer.Interval = TimeSpan.FromSeconds(1.5);
            _saveTimer.Tick += async (s, e) => { _saveTimer.Stop(); await PerformDiskWriteAsync(); };

            _portalDebounceTimer.Interval = TimeSpan.FromMilliseconds(200);
            _portalDebounceTimer.Tick += (s, e) => { _portalDebounceTimer.Stop(); _ = RefreshPortalUIAsync(); };
        }

        public MainWindow(string fenceId) : this()
        {
            FenceId = SanitizeFenceId(fenceId);
            _saveFilePath = Path.Combine(App.ConfigFolder, "Fences", $"{FenceId}.json");
            System.Windows.Automation.AutomationProperties.SetAutomationId(this, $"FenceWindow_{FenceId}");
        }

        private static void LogError(string context, Exception ex)
        {
            App.LogError($"Fence_{context}", ex);
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            Keyboard.ClearFocus();
            FocusManager.SetFocusedElement(FocusManager.GetFocusScope(this), null);
            SearchBox.Text = ""; SearchBox.Visibility = Visibility.Collapsed;
            
            if (_isRolledUp && _isTemporarilyRevealed) { AnimateRollUp(); _isTemporarilyRevealed = false; }
            
            if (!this.IsMouseOver)
            {
                AnimateGhostMode(false);
            }
        }

        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBox textBox && !textBox.IsMouseOver)
            {
                this.Focus();
            }
        }

        private static unsafe void ApplyAcrylicToHwnd(IntPtr hwnd, Color color, double opacity)
        {
            if (hwnd == IntPtr.Zero) return;
            byte a = (byte)(opacity * 255);
            uint gradientColor = (uint)((a << 24) | (color.B << 16) | (color.G << 8) | color.R);
            var accent = new NativeMethods.AccentPolicy { AccentState = NativeMethods.AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND, GradientColor = gradientColor };
            var data = new NativeMethods.WindowCompositionAttributeData
            {
                Attribute = NativeMethods.WindowCompositionAttribute.WCA_ACCENT_POLICY,
                SizeOfData = (nuint)sizeof(NativeMethods.AccentPolicy),
                Data = (nint)(&accent)
            };
            NativeMethods.SetWindowCompositionAttribute(hwnd, ref data);
            int cornerPreference = (int)NativeMethods.DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(hwnd, (int)NativeMethods.DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));
        }

        private void ShowUnreadableState()
        {
            try
            {
                if (TitleText is not null) TitleText.Text = "Fence data unreadable";

                if (IconPanel is not null)
                {
                    IconPanel.Children.Clear();
                    var message = new TextBlock
                    {
                        Text = "This fence's saved data could not be read, so nothing is being " +
                               "loaded or saved. The file has been left untouched:\n\n" +
                               _saveFilePath +
                               "\n\nRestore it from a backup, or delete this fence to start over.",
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(12),
                        MaxWidth = 320
                    };
                    message.SetResourceReference(TextBlock.ForegroundProperty, "CustomTextPrimaryBrush");
                    IconPanel.Children.Add(message);
                }
            }
            catch (Exception ex) { LogError("ShowUnreadableState", ex); }
        }

        private void ResolveBodyColor()
        {
            try
            {
                switch (_colorSource)
                {
                    case FenceColorSource.Custom:
                        break;

                    case FenceColorSource.AutoMatch:
                        _currentFenceColor = ThemeUtility.GetDominantWallpaperColor();
                        break;

                    default:
                        if (Application.Current?.TryFindResource("CustomBackgroundColor") is Color themeBg)
                        {
                            _currentFenceColor = Color.FromRgb(themeBg.R, themeBg.G, themeBg.B);

                            if (themeBg.A > 0) _currentOpacity = themeBg.A / 255.0;
                        }
                        break;
                }

                ApplyAcrylicBlur(_currentFenceColor, _currentOpacity);
            }
            catch (Exception ex) { LogError("ResolveBodyColor", ex); }
        }

        private void ApplyAcrylicBlur(Color color, double opacity)
        {
            _currentFenceColor = color; _currentOpacity = opacity;
            var windowHelper = new WindowInteropHelper(this);
            if (windowHelper.Handle == IntPtr.Zero) return;
            ApplyAcrylicToHwnd(windowHelper.Handle, color, opacity * GlassOpacity);

            MainBorder.Background = _hitTestBackgroundBrush;
        }


        public void SetInitialTab(FenceTab tab)
        {
            _tabs.Clear();
            _tabs.Add(tab);
            _activeTabIndex = 0;
            _hasInjectedTab = true;
        }

        private async Task LoadFenceStateAsync()
        {
            if (_hasInjectedTab)
            {
                RefreshTabsUI();
                SaveFenceState();
                return;
            }

            if (File.Exists(_saveFilePath))
            {
                try
                {
                    string json = await File.ReadAllTextAsync(_saveFilePath);
                    if (JsonSerializer.Deserialize<FenceData>(json) is FenceData data)
                    {
                        bool wasHorizontallyRolledUp = data.IsRolledUp && double.IsFinite(data.Width) && data.Width > 0 && data.Width <= 40;
                        data.Left = double.IsFinite(data.Left) ? data.Left : 100;
                        data.Top = double.IsFinite(data.Top) ? data.Top : 100;
                        data.Width = double.IsFinite(data.Width) && data.Width >= 100 ? data.Width : (wasHorizontallyRolledUp ? HEADER_SIZE : 250);
                        data.Height = double.IsFinite(data.Height) && data.Height >= HEADER_SIZE ? data.Height : 300;
                        data.ExpandedWidth = double.IsFinite(data.ExpandedWidth) && data.ExpandedWidth >= 100 ? data.ExpandedWidth : (data.Width >= 100 ? data.Width : 250);
                        data.ExpandedHeight = double.IsFinite(data.ExpandedHeight) && data.ExpandedHeight >= HEADER_SIZE ? data.ExpandedHeight : Math.Max(data.Height, 300);
                        data.ExpandedLeft = double.IsFinite(data.ExpandedLeft) ? data.ExpandedLeft : data.Left;
                        data.ExpandedTop = double.IsFinite(data.ExpandedTop) ? data.ExpandedTop : data.Top;
                        data.Opacity = Math.Clamp(double.IsFinite(data.Opacity) ? data.Opacity : 0.7, 0.05, 1.0);
                        data.IconSize = Math.Clamp(double.IsFinite(data.IconSize) ? data.IconSize : 48, 16, 256);

                        this.Left = data.Left; this.Top = data.Top;
                        Rect virtualScreen = new(
                            SystemParameters.VirtualScreenLeft,
                            SystemParameters.VirtualScreenTop,
                            SystemParameters.VirtualScreenWidth,
                            SystemParameters.VirtualScreenHeight);
                        Rect fenceBounds = new(data.Left, data.Top, data.Width, data.Height);
                        bool isOnScreen = virtualScreen.IntersectsWith(fenceBounds);
                        if (!isOnScreen) { this.Left = 100; this.Top = 100; }

                        _isRolledUp = data.IsRolledUp;
                        _expandedHeight = data.ExpandedHeight; _expandedWidth = data.ExpandedWidth;
                        _expandedLeft = data.ExpandedLeft; _expandedTop = data.ExpandedTop;
                        if (!isOnScreen) { _expandedLeft = 100; _expandedTop = 100; }
                        TitleText.Text = data.Title;
                        System.Windows.Automation.AutomationProperties.SetName(this, TitleText.Text ?? string.Empty);

                        if (SearchIcon is not null) SearchIcon.Visibility = data.ShowSearch ? Visibility.Visible : Visibility.Collapsed;
                        if (SearchBox is not null) SearchBox.Visibility = Visibility.Collapsed;

                        CurrentIconSize = data.IconSize;
                        _autoMatchColor = data.AutoMatchColor; _ghostModeOverride = data.GhostModeOverride;

                        try { _currentFenceColor = (Color)ColorConverter.ConvertFromString(data.HexColor); } catch { }
                        _currentOpacity = data.Opacity;

                        _colorSource = data.ColorSource ?? (data.AutoMatchColor
                            ? FenceColorSource.AutoMatch
                            : FenceColorSource.Theme);

                        if (_isRolledUp) { if (wasHorizontallyRolledUp) { this.Width = HEADER_SIZE; this.Height = data.Height; } else { this.Height = HEADER_SIZE; this.Width = data.Width; } }
                        else { this.Height = data.Height; this.Width = data.Width; }

                        _tabs = data.Tabs;
                        _activeTabIndex = data.ActiveTabIndex;

                        if (_tabs.Count == 0 && data.Files.Count > 0)
                        {
                            var legacyTab = new FenceTab { Title = data.Title, Files = data.Files, SortMethod = data.SortMethod, AutoSortExtensions = data.AutoSortExtensions, IsPortal = data.IsPortal, PortalPath = data.PortalPath };
                            _tabs.Add(legacyTab);
                        }

                        if (_tabs.Count == 0) _tabs.Add(new FenceTab { Title = "Main" });
                        if (_activeTabIndex >= _tabs.Count || _activeTabIndex < 0) _activeTabIndex = 0;

                        RefreshTabsUI();
                        DetermineDockState(); UpdateDockOrientation();
                    }
                }
                catch (Exception ex)
                {
                    LogError("LoadFenceStateAsync", ex);
                    _loadFailed = true;
                    ShowUnreadableState();
                }
            }
            else if (_tabs.Count == 0)
            {
                _tabs.Add(new FenceTab { Title = "Main" });
                RefreshTabsUI();
            }
        }

        private void SaveFenceState() { _saveTimer.Stop(); _saveTimer.Start(); }

        public async Task FlushPendingSaveAsync()
        {
            if (!_saveTimer.IsEnabled) return;
            _saveTimer.Stop();
            if (_isDeleted || _loadFailed) return;
            await PerformDiskWriteAsync();
        }

        private void PerformDiskWriteSynchronous()
        {
            if (_isDeleted || _loadFailed || TitleText is null) return;
            _saveGate.Wait();
            try
            {
                if (_isDeleted || _loadFailed || TitleText is null) return;
                SaveCurrentTabState();
                FenceData data = new()
                {
                    Left = this.Left,
                    Top = this.Top,
                    Width = this.Width,
                    Height = this.Height,
                    IsRolledUp = _isRolledUp,
                    ExpandedHeight = _expandedHeight,
                    ExpandedWidth = _expandedWidth,
                    ExpandedLeft = _expandedLeft,
                    ExpandedTop = _expandedTop,
                    Tabs = _tabs,
                    ActiveTabIndex = _activeTabIndex,
                    Title = TitleText.Text,
                    HexColor = $"#{_currentFenceColor.R:X2}{_currentFenceColor.G:X2}{_currentFenceColor.B:X2}",
                    Opacity = _currentOpacity,
                    IconSize = CurrentIconSize,
                    ShowSearch = ShowSearch,
                    AutoMatchColor = _autoMatchColor,
                    ColorSource = _colorSource,
                    GhostModeOverride = _ghostModeOverride
                };

                string tempPath = $"{_saveFilePath}.{Guid.NewGuid():N}.tmp";
                string saveDir = Path.GetDirectoryName(_saveFilePath)!;
                Directory.CreateDirectory(saveDir);

                File.WriteAllText(tempPath, JsonSerializer.Serialize(data, _jsonOptions));
                File.Move(tempPath, _saveFilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                LogError("PerformDiskWriteSynchronous", ex);
            }
            finally
            {
                _saveGate.Release();
            }
        }

        private async Task PerformDiskWriteAsync()
        {
            if (_isDeleted || _loadFailed || TitleText is null) return;
            await _saveGate.WaitAsync();
            try
            {
                if (_isDeleted || _loadFailed || TitleText is null) return;
                SaveCurrentTabState();
                FenceData data = new()
                {
                    Left = this.Left,
                    Top = this.Top,
                    Width = this.Width,
                    Height = this.Height,
                    IsRolledUp = _isRolledUp,
                    ExpandedHeight = _expandedHeight,
                    ExpandedWidth = _expandedWidth,
                    ExpandedLeft = _expandedLeft,
                    ExpandedTop = _expandedTop,
                    Tabs = _tabs,
                    ActiveTabIndex = _activeTabIndex,
                    Title = TitleText.Text,
                    HexColor = $"#{_currentFenceColor.R:X2}{_currentFenceColor.G:X2}{_currentFenceColor.B:X2}",
                    Opacity = _currentOpacity,
                    IconSize = CurrentIconSize,
                    ShowSearch = ShowSearch,
                    AutoMatchColor = _autoMatchColor,
                    ColorSource = _colorSource,
                    GhostModeOverride = _ghostModeOverride
                };

                string tempPath = $"{_saveFilePath}.{Guid.NewGuid():N}.tmp";
                string saveDir = Path.GetDirectoryName(_saveFilePath)!;
                Directory.CreateDirectory(saveDir);

                await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(data, _jsonOptions));
                File.Move(tempPath, _saveFilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                LogError("PerformDiskWriteAsync", ex);
            }
            finally
            {
                _saveGate.Release();
            }
        }

        private void DetermineDockState()
        {
            var helper = new WindowInteropHelper(this);
            var workArea = System.Windows.Forms.Screen.FromHandle(helper.Handle).WorkingArea;
            double dpiX = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double dpiY = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

            int physLeft = (int)(this.Left * dpiX); int physTop = (int)(this.Top * dpiY);
            int physRight = (int)((this.Left + this.Width) * dpiX); int physBottom = (int)((this.Top + this.Height) * dpiY);
            int thresh = 35; int dLeft = Math.Abs(physLeft - workArea.Left); int dRight = Math.Abs(physRight - workArea.Right);
            int dTop = Math.Abs(physTop - workArea.Top); int dBottom = Math.Abs(physBottom - workArea.Bottom);
            int minH = Math.Min(dLeft, dRight); int minV = Math.Min(dTop, dBottom);

            if (minH >= thresh && minV >= thresh) _dockState = DockState.Floating;
            else if (minH < thresh && minV < thresh)
            {
                if (_dockState == DockState.Left || _dockState == DockState.Right) _dockState = dRight < dLeft ? DockState.Right : DockState.Left;
                else if (_dockState == DockState.Top || _dockState == DockState.Bottom) _dockState = dBottom < dTop ? DockState.Bottom : DockState.Top;
                else _dockState = this.Width > this.Height ? (dBottom <= dTop ? DockState.Bottom : DockState.Top) : (dRight <= dLeft ? DockState.Right : DockState.Left);
            }
            else if (minV < minH) _dockState = dBottom < dTop ? DockState.Bottom : DockState.Top;
            else if (minH < minV) _dockState = dRight < dLeft ? DockState.Right : DockState.Left;
            else _dockState = this.Width > this.Height ? (dBottom <= dTop ? DockState.Bottom : DockState.Top) : (dRight <= dLeft ? DockState.Right : DockState.Left);
        }

        private void UpdateDockOrientation()
        {
            if (_dockState == DockState.Left)
            {
                HeaderRowTop.Height = new GridLength(0); HeaderRowBottom.Height = new GridLength(0);
                HeaderColLeft.Width = new GridLength(HEADER_SIZE); HeaderColRight.Width = new GridLength(0);
                Grid.SetRow(HeaderBorder, 0); Grid.SetRowSpan(HeaderBorder, 3); Grid.SetColumn(HeaderBorder, 0); Grid.SetColumnSpan(HeaderBorder, 1);
                HeaderGrid.LayoutTransform = new RotateTransform(-90); HeaderBorder.CornerRadius = new CornerRadius(8, 0, 0, 8);
                if (SearchBox is not null) { Grid.SetRow(SearchBox, 0); Grid.SetRowSpan(SearchBox, 3); Grid.SetColumn(SearchBox, 0); Grid.SetColumnSpan(SearchBox, 1); SearchBox.LayoutTransform = new RotateTransform(-90); SearchBox.HorizontalAlignment = HorizontalAlignment.Center; SearchBox.VerticalAlignment = VerticalAlignment.Center; SearchBox.Margin = new Thickness(0); SearchBox.Width = 140; }
            }
            else if (_dockState == DockState.Right)
            {
                HeaderRowTop.Height = new GridLength(0); HeaderRowBottom.Height = new GridLength(0);
                HeaderColLeft.Width = new GridLength(0); HeaderColRight.Width = new GridLength(HEADER_SIZE);
                Grid.SetRow(HeaderBorder, 0); Grid.SetRowSpan(HeaderBorder, 3); Grid.SetColumn(HeaderBorder, 2); Grid.SetColumnSpan(HeaderBorder, 1);
                HeaderGrid.LayoutTransform = new RotateTransform(90); HeaderBorder.CornerRadius = new CornerRadius(0, 8, 8, 0);
                if (SearchBox is not null) { Grid.SetRow(SearchBox, 0); Grid.SetRowSpan(SearchBox, 3); Grid.SetColumn(SearchBox, 2); Grid.SetColumnSpan(SearchBox, 1); SearchBox.LayoutTransform = new RotateTransform(90); SearchBox.HorizontalAlignment = HorizontalAlignment.Center; SearchBox.VerticalAlignment = VerticalAlignment.Center; SearchBox.Margin = new Thickness(0); SearchBox.Width = 140; }
            }
            else if (_dockState == DockState.Bottom)
            {
                HeaderRowTop.Height = new GridLength(0); HeaderRowBottom.Height = new GridLength(HEADER_SIZE);
                HeaderColLeft.Width = new GridLength(0); HeaderColRight.Width = new GridLength(0);
                Grid.SetRow(HeaderBorder, 2); Grid.SetRowSpan(HeaderBorder, 1); Grid.SetColumn(HeaderBorder, 0); Grid.SetColumnSpan(HeaderBorder, 3);
                HeaderGrid.LayoutTransform = Transform.Identity; HeaderBorder.CornerRadius = new CornerRadius(0, 0, 8, 8);
                if (SearchBox is not null) { Grid.SetRow(SearchBox, 2); Grid.SetRowSpan(SearchBox, 1); Grid.SetColumn(SearchBox, 0); Grid.SetColumnSpan(SearchBox, 3); SearchBox.LayoutTransform = Transform.Identity; SearchBox.HorizontalAlignment = HorizontalAlignment.Right; SearchBox.VerticalAlignment = VerticalAlignment.Bottom; SearchBox.Margin = new Thickness(0, 0, 35, 4); SearchBox.Width = 160; }
            }
            else
            {
                HeaderRowTop.Height = new GridLength(HEADER_SIZE); HeaderRowBottom.Height = new GridLength(0);
                HeaderColLeft.Width = new GridLength(0); HeaderColRight.Width = new GridLength(0);
                Grid.SetRow(HeaderBorder, 0); Grid.SetRowSpan(HeaderBorder, 1); Grid.SetColumn(HeaderBorder, 0); Grid.SetColumnSpan(HeaderBorder, 3);
                HeaderGrid.LayoutTransform = Transform.Identity; HeaderBorder.CornerRadius = new CornerRadius(8, 8, 0, 0);
                if (SearchBox is not null) { Grid.SetRow(SearchBox, 0); Grid.SetRowSpan(SearchBox, 1); Grid.SetColumn(SearchBox, 0); Grid.SetColumnSpan(SearchBox, 3); SearchBox.LayoutTransform = Transform.Identity; SearchBox.HorizontalAlignment = HorizontalAlignment.Right; SearchBox.VerticalAlignment = VerticalAlignment.Top; SearchBox.Margin = new Thickness(0, 4, 35, 0); SearchBox.Width = 160; }
            }
        }

        private void AnimateGhostMode(bool isHovering)
        {
            bool useGhost = _globalGhostMode;
            if (_ghostModeOverride == 1) useGhost = true;
            if (_ghostModeOverride == 2) useGhost = false;

            if (!useGhost)
            {
                if (MainGrid.Opacity < 1.0) { MainGrid.BeginAnimation(OpacityProperty, null); this.BeginAnimation(GlassOpacityProperty, null); MainGrid.Opacity = 1.0; GlassOpacity = 1.0; }
                return;
            }

            double targetOpacity = isHovering ? 1.0 : 0.01;
            double targetGlass = isHovering ? 1.0 : 0.0;
            DoubleAnimation animOpacity = new() { To = targetOpacity, Duration = TimeSpan.FromMilliseconds(250) };
            DoubleAnimation animGlass = new() { To = targetGlass, Duration = TimeSpan.FromMilliseconds(250) };
            MainGrid.BeginAnimation(OpacityProperty, animOpacity); this.BeginAnimation(GlassOpacityProperty, animGlass);
        }

        private void AnimateRollUp()
        {
            if (BackgroundMedia.Visibility == Visibility.Visible && App.PauseMediaOnRollup) BackgroundMedia.Pause();
            double d = 250;

            IEasingFunction? ease = _currentTheme.RollUpAnimation switch
            {
                Core.AnimationStyle.Bounce => new BounceEase() { Bounces = 2, Bounciness = 2, EasingMode = EasingMode.EaseOut },
                Core.AnimationStyle.Linear => null,
                _ => new QuarticEase() { EasingMode = EasingMode.EaseInOut }
            };

            if (_dockState == DockState.Left) { this.BeginAnimation(Window.WidthProperty, new DoubleAnimation { To = HEADER_SIZE, Duration = TimeSpan.FromMilliseconds(d), EasingFunction = ease }); }
            else if (_dockState == DockState.Right) { this.BeginAnimation(Window.WidthProperty, new DoubleAnimation { To = HEADER_SIZE, Duration = TimeSpan.FromMilliseconds(d), EasingFunction = ease }); this.BeginAnimation(Window.LeftProperty, new DoubleAnimation { To = _expandedLeft + (_expandedWidth - HEADER_SIZE), Duration = TimeSpan.FromMilliseconds(d), EasingFunction = ease }); }
            else if (_dockState == DockState.Bottom) { this.BeginAnimation(Window.HeightProperty, new DoubleAnimation { To = HEADER_SIZE, Duration = TimeSpan.FromMilliseconds(d), EasingFunction = ease }); this.BeginAnimation(Window.TopProperty, new DoubleAnimation { To = _expandedTop + (_expandedHeight - HEADER_SIZE), Duration = TimeSpan.FromMilliseconds(d), EasingFunction = ease }); }
            else { this.BeginAnimation(Window.HeightProperty, new DoubleAnimation { To = HEADER_SIZE, Duration = TimeSpan.FromMilliseconds(d), EasingFunction = ease }); }
        }

        private void AnimateReveal()
        {
            if (BackgroundMedia.Visibility == Visibility.Visible && App.PauseMediaOnRollup) BackgroundMedia.Play();
            double d = 250;

            IEasingFunction? ease = _currentTheme.RollUpAnimation switch
            {
                Core.AnimationStyle.Bounce => new BounceEase() { Bounces = 2, Bounciness = 2, EasingMode = EasingMode.EaseOut },
                Core.AnimationStyle.Linear => null,
                _ => new QuarticEase() { EasingMode = EasingMode.EaseInOut }
            };

            if (_dockState == DockState.Left) { this.BeginAnimation(Window.WidthProperty, new DoubleAnimation { To = _expandedWidth, Duration = TimeSpan.FromMilliseconds(d), EasingFunction = ease }); }
            else if (_dockState == DockState.Right) { this.BeginAnimation(Window.WidthProperty, new DoubleAnimation { To = _expandedWidth, Duration = TimeSpan.FromMilliseconds(d), EasingFunction = ease }); this.BeginAnimation(Window.LeftProperty, new DoubleAnimation { To = _expandedLeft, Duration = TimeSpan.FromMilliseconds(d), EasingFunction = ease }); }
            else if (_dockState == DockState.Bottom) { this.BeginAnimation(Window.HeightProperty, new DoubleAnimation { To = _expandedHeight, Duration = TimeSpan.FromMilliseconds(d), EasingFunction = ease }); this.BeginAnimation(Window.TopProperty, new DoubleAnimation { To = _expandedTop, Duration = TimeSpan.FromMilliseconds(d), EasingFunction = ease }); }
            else { this.BeginAnimation(Window.HeightProperty, new DoubleAnimation { To = _expandedHeight, Duration = TimeSpan.FromMilliseconds(d), EasingFunction = ease }); }
        }

        public void ApplyTheme(Core.ThemeSettings theme)
        {
            RefreshLabelHalos();

            ResolveBodyColor();
            _currentTheme = theme;

            try
            {
                if (!string.IsNullOrWhiteSpace(theme.BackgroundMediaPath) && System.IO.File.Exists(theme.BackgroundMediaPath))
                {
                    BackgroundMedia.Source = new Uri(theme.BackgroundMediaPath);
                    BackgroundMedia.Opacity = theme.MediaOpacity;
                    BackgroundMedia.Visibility = Visibility.Visible;
                    if (!_isRolledUp || !App.PauseMediaOnRollup) BackgroundMedia.Play();
                }
                else
                {
                    BackgroundMedia.Visibility = Visibility.Hidden;
                    BackgroundMedia.Stop();
                }
            }
            catch { }
        }

        private void BackgroundMedia_MediaEnded(object sender, RoutedEventArgs e)
        {
            BackgroundMedia.Position = TimeSpan.Zero;
            BackgroundMedia.Play();
        }

        public void SetMergeHighlight(bool isHighlighted)
        {
            if (isHighlighted)
            {
                MainBorder.SetResourceReference(Border.BorderBrushProperty, "CustomSelectionStrokeBrush");
                MainBorder.BorderThickness = new Thickness(2);

                if (_isRolledUp && !_isTemporarilyRevealed)
                {
                    AnimateReveal();
                    _isTemporarilyRevealed = true;
                }
            }
            else
            {
                MainBorder.BorderBrush = Brushes.Transparent;
                MainBorder.BorderThickness = new Thickness(0);

                if (_isRolledUp && _isTemporarilyRevealed)
                {
                    if (!this.IsMouseOver)
                    {
                        AnimateRollUp();
                        _isTemporarilyRevealed = false;
                    }
                }
            }
        }

        public void MergeWithFence(MainWindow source)
        {

            source.SaveCurrentTabState();
            foreach (var tab in source._tabs)
            {
                TransferTabFiles(tab, source.FenceId);
                this._tabs.Add(tab);
            }

            this._activeTabIndex = this._tabs.Count - 1;
            this.RefreshTabsUI();
            this.SaveFenceState();

            source._isDeleted = true;
            string sourceSaveFile = Path.Combine(App.ConfigFolder, "Fences", $"{source.FenceId}.json");
            if (File.Exists(sourceSaveFile)) File.Delete(sourceSaveFile);
            source.Close();
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            this.Focus();
            if (e.ChangedButton == MouseButton.Left)
            {
                if (e.ClickCount == 2) { ToggleRollUp(); e.Handled = true; return; }
                this.BeginAnimation(Window.LeftProperty, null); this.BeginAnimation(Window.TopProperty, null);
                this.BeginAnimation(Window.WidthProperty, null); this.BeginAnimation(Window.HeightProperty, null);
                _wasRolledUpBeforeDrag = _isRolledUp || _isTemporarilyRevealed;
                if (_wasRolledUpBeforeDrag) { this.Width = _expandedWidth; this.Height = _expandedHeight; this.Left = _expandedLeft; this.Top = _expandedTop; }
                _isRolledUp = false; _isTemporarilyRevealed = false; _isManualDragging = true;
                _manualDragStartMouse = new System.Windows.Point(System.Windows.Forms.Cursor.Position.X, System.Windows.Forms.Cursor.Position.Y);
                _manualDragStartLeft = this.Left; _manualDragStartTop = this.Top; HeaderBorder.CaptureMouse();
                e.Handled = true;
            }
        }

        private void Header_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isManualDragging)
            {
                PresentationSource? source = PresentationSource.FromVisual(this);
                double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

                double mouseDX = (System.Windows.Forms.Cursor.Position.X - _manualDragStartMouse.X) / dpiX;
                double mouseDY = (System.Windows.Forms.Cursor.Position.Y - _manualDragStartMouse.Y) / dpiY;

                double newLeft = _manualDragStartLeft + mouseDX;
                double newTop = _manualDragStartTop + mouseDY;

                double snapThresh = 25;
                double? snappedLeft = null;
                double? snappedTop = null;

                foreach (var screen in System.Windows.Forms.Screen.AllScreens)
                {
                    double sLeft = screen.WorkingArea.Left / dpiX;
                    double sTop = screen.WorkingArea.Top / dpiY;
                    double sRight = screen.WorkingArea.Right / dpiX;
                    double sBottom = screen.WorkingArea.Bottom / dpiY;

                    if (Math.Abs(newLeft - sLeft) < snapThresh) snappedLeft = sLeft;
                    else if (Math.Abs((newLeft + this.Width) - sRight) < snapThresh) snappedLeft = sRight - this.Width;

                    if (Math.Abs(newTop - sTop) < snapThresh) snappedTop = sTop;
                    else if (Math.Abs((newTop + this.Height) - sBottom) < snapThresh) snappedTop = sBottom - this.Height;
                }

                this.Left = snappedLeft ?? newLeft;
                this.Top = snappedTop ?? newTop;

                DockState oldState = _dockState;
                DetermineDockState();
                if (oldState != _dockState) UpdateDockOrientation();

                Point mousePos = new Point(System.Windows.Forms.Cursor.Position.X, System.Windows.Forms.Cursor.Position.Y);
                MainWindow? currentTarget = null;
                foreach (Window w in Application.Current.Windows)
                {
                    if (w is MainWindow other && other != this && other.Visibility == Visibility.Visible)
                    {
                        double dX = PresentationSource.FromVisual(other)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                        double dY = PresentationSource.FromVisual(other)?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
                        Rect bounds = new Rect(other.Left * dX, other.Top * dY, other.Width * dX, other.Height * dY);
                        if (bounds.Contains(mousePos))
                        {
                            currentTarget = other;
                            break;
                        }
                    }
                }

                if (currentTarget != _hoveredTargetFence)
                {
                    _hoveredTargetFence?.SetMergeHighlight(false);
                    _hoveredTargetFence = currentTarget;
                    _hoveredTargetFence?.SetMergeHighlight(true);
                }
            }
        }

        private void Header_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isManualDragging)
            {
                _isManualDragging = false; HeaderBorder.ReleaseMouseCapture();

                if (_hoveredTargetFence != null)
                {
                    var target = _hoveredTargetFence;
                    _hoveredTargetFence = null;

                    target.MergeWithFence(this);
                    target.SetMergeHighlight(false);
                    return;
                }

                double savedExpandedWidth = _expandedWidth; double savedExpandedHeight = _expandedHeight;
                DetermineDockState(); UpdateDockOrientation();
                _expandedWidth = savedExpandedWidth; _expandedHeight = savedExpandedHeight;

                var helper = new WindowInteropHelper(this);
                var screen = System.Windows.Forms.Screen.FromHandle(helper.Handle).WorkingArea;
                double dpiX = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                double dpiY = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

                if (_dockState == DockState.Bottom) { _expandedTop = (screen.Bottom / dpiY) - _expandedHeight; _expandedLeft = this.Left; }
                else if (_dockState == DockState.Right) { _expandedLeft = (screen.Right / dpiX) - _expandedWidth; _expandedTop = this.Top; }
                else if (_dockState == DockState.Left) { _expandedLeft = screen.Left / dpiX; _expandedTop = this.Top; }
                else if (_dockState == DockState.Top) { _expandedTop = screen.Top / dpiY; _expandedLeft = this.Left; }
                else { _expandedLeft = this.Left; _expandedTop = this.Top; }

                if (_wasRolledUpBeforeDrag)
                {
                    _isRolledUp = true;
                    if (this.IsMouseOver) { _isTemporarilyRevealed = true; if (_dockState == DockState.Left || _dockState == DockState.Right) this.Width = _expandedWidth; else this.Height = _expandedHeight; this.Left = _expandedLeft; this.Top = _expandedTop; }
                    else { _isTemporarilyRevealed = false; if (_dockState == DockState.Bottom) { this.Height = HEADER_SIZE; this.Top = _expandedTop + (_expandedHeight - HEADER_SIZE); } else if (_dockState == DockState.Right) { this.Width = HEADER_SIZE; this.Left = _expandedLeft + (_expandedWidth - HEADER_SIZE); } else { this.Height = HEADER_SIZE; this.Width = _expandedWidth; this.Left = _expandedLeft; this.Top = _expandedTop; } AnimateRollUp(); }
                }
                else { this.Left = _expandedLeft; this.Top = _expandedTop; }
                SaveFenceState();
                _ = App.SaveCurrentLayoutAsync();
            }
        }

        private void ToggleRollUp()
        {
            if (_isRolledUp) { AnimateReveal(); _isRolledUp = false; _isTemporarilyRevealed = false; }
            else { DetermineDockState(); UpdateDockOrientation(); this.BeginAnimation(Window.HeightProperty, null); this.BeginAnimation(Window.WidthProperty, null); this.BeginAnimation(Window.LeftProperty, null); this.BeginAnimation(Window.TopProperty, null); _expandedHeight = this.Height; _expandedWidth = this.Width; _expandedLeft = this.Left; _expandedTop = this.Top; AnimateRollUp(); _isRolledUp = true; _isTemporarilyRevealed = false; }
            SaveFenceState();
        }

        private void StartNativeResize(int direction)
        {
            if (_isRolledUp && !_isTemporarilyRevealed) return;
            this.BeginAnimation(Window.HeightProperty, null); this.BeginAnimation(Window.WidthProperty, null); this.BeginAnimation(Window.LeftProperty, null); this.BeginAnimation(Window.TopProperty, null);

            bool wasRolledUp = _isRolledUp || _isTemporarilyRevealed;
            if (_isRolledUp || _isTemporarilyRevealed) { _isRolledUp = false; _isTemporarilyRevealed = false; }

            NativeMethods.ReleaseCapture();
            var helper = new WindowInteropHelper(this);
            NativeMethods.SendMessage(helper.Handle, NativeMethods.WM_SYSCOMMAND, (IntPtr)(NativeMethods.SC_SIZE + direction), IntPtr.Zero);

            DetermineDockState(); UpdateDockOrientation();
            _expandedLeft = this.Left; _expandedTop = this.Top; _expandedWidth = this.Width; _expandedHeight = this.Height;

            if (wasRolledUp) { _isRolledUp = true; _isTemporarilyRevealed = true; }
            SaveFenceState();
            _ = App.SaveCurrentLayoutAsync();
        }

        private void Resize_Left(object sender, MouseButtonEventArgs e) { e.Handled = true; StartNativeResize(1); }
        private void Resize_Right(object sender, MouseButtonEventArgs e) { e.Handled = true; StartNativeResize(2); }
        private void Resize_Top(object sender, MouseButtonEventArgs e) { e.Handled = true; StartNativeResize(3); }
        private void Resize_TopLeft(object sender, MouseButtonEventArgs e) { e.Handled = true; StartNativeResize(4); }
        private void Resize_TopRight(object sender, MouseButtonEventArgs e) { e.Handled = true; StartNativeResize(5); }
        private void Resize_Bottom(object sender, MouseButtonEventArgs e) { e.Handled = true; StartNativeResize(6); }
        private void Resize_BottomLeft(object sender, MouseButtonEventArgs e) { e.Handled = true; StartNativeResize(7); }
        private void Resize_BottomRight(object sender, MouseButtonEventArgs e) { e.Handled = true; StartNativeResize(8); }

        private void TitleText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) return; e.Handled = true; TitleText.Visibility = Visibility.Collapsed;
            TextBox renameBox = new() { Text = TitleText.Text, Width = 140, Height = 22, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Left, FontFamily = TitleText.FontFamily, FontWeight = FontWeights.SemiBold, Padding = new Thickness(4, 0, 0, 0) };
            System.Windows.Automation.AutomationProperties.SetAutomationId(renameBox, "TitleRenameTextBox");
            System.Windows.Automation.AutomationProperties.SetName(renameBox, "Rename Fence Title");
            ApplyInlineEditorTheme(renameBox);
            TitleStack.Children.Add(renameBox);
            Dispatcher.BeginInvoke(new Action(() => { renameBox.Focus(); Keyboard.Focus(renameBox); renameBox.SelectAll(); }), DispatcherPriority.Input);

            void CommitTitle()
            {
                if (TitleStack.Children.Contains(renameBox))
                {
                    TitleText.Text = renameBox.Text;
                    System.Windows.Automation.AutomationProperties.SetName(this, TitleText.Text ?? string.Empty);
                    if (_tabs.Count == 1) _tabs[0].Title = renameBox.Text;
                    TitleStack.Children.Remove(renameBox);
                    TitleText.Visibility = Visibility.Visible;
                    SaveFenceState();
                }
            }

            renameBox.KeyDown += (s2, e2) => { if (e2.Key == Key.Enter) { CommitTitle(); } else if (e2.Key == Key.Escape) { TitleStack.Children.Remove(renameBox); TitleText.Visibility = Visibility.Visible; } };
            renameBox.LostFocus += (s2, e2) => { CommitTitle(); };
        }

        private static readonly System.Windows.Media.Effects.DropShadowEffect _darkHalo = CreateFrozenHalo(Colors.Black);
        private static readonly System.Windows.Media.Effects.DropShadowEffect _lightHalo = CreateFrozenHalo(Colors.White);

        private static System.Windows.Media.Effects.DropShadowEffect CreateFrozenHalo(Color color)
        {
            var effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = color,
                BlurRadius = 2,
                ShadowDepth = 0,
                Opacity = 1.0,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
            };
            effect.Freeze();
            return effect;
        }

        private static System.Windows.Media.Effects.DropShadowEffect BuildLabelHalo()
        {
            System.Windows.Media.Color text = Colors.White;
            if (Application.Current?.TryFindResource("CustomTextPrimaryColor") is System.Windows.Media.Color c)
                text = c;

            double luminance = (0.2126 * text.R + 0.7152 * text.G + 0.0722 * text.B) / 255.0;
            bool lightText = luminance > 0.5;

            return lightText ? _darkHalo : _lightHalo;
        }

        private void RefreshLabelHalos()
        {
            try
            {
                var halo = BuildLabelHalo();
                foreach (UIElement child in IconPanel.Children)
                {
                    if (child is StackPanel sp)
                        foreach (UIElement inner in sp.Children)
                            if (inner is TextBlock tb) tb.Effect = halo;
                }
            }
            catch (Exception ex) { LogError("RefreshLabelHalos", ex); }
        }

        private static void ApplyInlineEditorTheme(System.Windows.Controls.TextBox box)
        {
            box.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "CustomSurfaceSubtleBrush");
            box.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "CustomTextPrimaryBrush");
            box.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "CustomBorderBrush");
            box.SetResourceReference(System.Windows.Controls.TextBox.CaretBrushProperty, "CustomTextPrimaryBrush");
        }

        private void SearchIcon_Click(object sender, RoutedEventArgs e) => ToggleSearch();

        private void MenuIcon_Click(object sender, RoutedEventArgs e)
            => OpenFenceMenu(sender as UIElement);

        private void ToggleSearch()
        {
            if (SearchBox.Visibility == Visibility.Visible)
            {
                SearchBox.Text = "";
                SearchBox.Visibility = Visibility.Collapsed;

                MainGrid.Focus();
            }
            else
            {
                SearchBox.Visibility = Visibility.Visible;
                SearchBox.Focus();
            }
        }

        private void OpenFenceMenu(UIElement? anchor)
        {
            _isContextMenuOpen = true;
            HeaderContextMenu.PlacementTarget = anchor ?? HeaderBorder;
            HeaderContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            HeaderContextMenu.IsOpen = true;
        }

        private bool HandleFenceShortcuts(KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

            if (e.Key == Key.Escape && SearchBox.Visibility == Visibility.Visible)
            {
                SearchBox.Text = "";
                SearchBox.Visibility = Visibility.Collapsed;
                MainGrid.Focus();
                return true;
            }

            if (SearchBox.IsKeyboardFocusWithin) return false;

            switch (e.Key)
            {
                case Key.F when ctrl:
                    ToggleSearch();
                    return true;

                case Key.T when ctrl:
                    AddTabBtn_Click(this, new RoutedEventArgs());
                    return true;

                case Key.F10:
                    OpenFenceMenu(MenuIcon);
                    return true;

                default:
                    return false;
            }
        }

        private void SearchIcon_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ToggleSearch();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { ApplySorting(); }
        private async void SearchBox_LostFocus(object sender, RoutedEventArgs e)
        {
            await Task.Delay(50);
            if (this.IsLoaded && !SearchBox.IsFocused && string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                SearchBox.Visibility = Visibility.Collapsed;
                if (!this.IsMouseOver && _isRolledUp && _isTemporarilyRevealed)
                {
                    AnimateRollUp();
                    _isTemporarilyRevealed = false;
                }
            }
        }
        private void SearchBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { SearchBox.Text = ""; SearchBox.Visibility = Visibility.Collapsed; e.Handled = true; MainGrid.Focus(); } }

        private void Window_MouseEnter(object sender, MouseEventArgs e) { AnimateGhostMode(true); if (_isRolledUp && !_isTemporarilyRevealed) { AnimateReveal(); _isTemporarilyRevealed = true; } }
        private void Window_MouseLeave(object sender, MouseEventArgs e) { if (string.IsNullOrWhiteSpace(SearchBox.Text) && !SearchBox.IsKeyboardFocusWithin) { SearchBox.Visibility = Visibility.Collapsed; } if (_isContextMenuOpen || _isDraggingSelectionBox || SearchBox.IsKeyboardFocusWithin) return; AnimateGhostMode(false); if (_isRolledUp && _isTemporarilyRevealed) { AnimateRollUp(); _isTemporarilyRevealed = false; } }
        private void Window_DragEnter(object sender, DragEventArgs e) { AnimateGhostMode(true); if (_isRolledUp && !_isTemporarilyRevealed) { AnimateReveal(); _isTemporarilyRevealed = true; } }
        private void Window_DragOver(object sender, DragEventArgs e) { AnimateGhostMode(true); if (_isRolledUp && !_isTemporarilyRevealed) { AnimateReveal(); _isTemporarilyRevealed = true; } }
        private void Window_DragLeave(object sender, DragEventArgs e) { Point pt = e.GetPosition(this); if (pt.X <= 2 || pt.Y <= 2 || pt.X >= this.ActualWidth - 2 || pt.Y >= this.ActualHeight - 2) { AnimateGhostMode(false); if (_isRolledUp && _isTemporarilyRevealed) { AnimateRollUp(); _isTemporarilyRevealed = false; } } }

        private void HandleMenuClosed() { _isContextMenuOpen = false; if (!this.IsMouseOver && !_isDraggingSelectionBox) { AnimateGhostMode(false); if (_isRolledUp && _isTemporarilyRevealed) { AnimateRollUp(); _isTemporarilyRevealed = false; } } }

        private void MenuIcon_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { e.Handled = true; OpenFenceMenu(sender as UIElement); }

        private static readonly (string Title, string DefaultExtensions, HashSet<string> Extensions)[] _fileTypeCategories =
        [
            ("Shortcuts & Apps", ".lnk, .url, .appref-ms, .exe, .msi", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".lnk", ".url", ".appref-ms", ".exe", ".msi" }),
            ("Documents", ".pdf, .doc, .docx, .xls, .xlsx, .ppt, .pptx, .txt, .rtf, .csv, .md, .odt, .ods, .odp, .epub", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".rtf", ".csv", ".md", ".odt", ".ods", ".odp", ".epub" }),
            ("Images", ".jpg, .jpeg, .png, .gif, .bmp, .webp, .svg, .ico, .tiff, .tif, .heic, .psd, .ai, .raw", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".svg", ".ico", ".tiff", ".tif", ".heic", ".psd", ".ai", ".raw" }),
            ("Media", ".mp4, .mkv, .avi, .mov, .wmv, .webm, .flv, .m4v, .mp3, .wav, .flac, .ogg, .m4a, .aac, .wma", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv", ".m4v", ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".wma" }),
            ("Archives", ".zip, .rar, .7z, .tar, .gz, .bz2, .xz, .tgz, .iso, .cab", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".tgz", ".iso", ".cab" }),
            ("Code & Scripts", ".cs, .py, .js, .ts, .jsx, .tsx, .html, .htm, .css, .scss, .json, .xml, .yaml, .yml, .toml, .ini, .cfg, .sql, .ps1, .bat, .cmd, .sh, .cpp, .c, .h, .hpp, .java, .go, .rs, .php, .rb, .lua, .sln, .csproj", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".py", ".js", ".ts", ".jsx", ".tsx", ".html", ".htm", ".css", ".scss", ".json", ".xml", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".sql", ".ps1", ".bat", ".cmd", ".sh", ".cpp", ".c", ".h", ".hpp", ".java", ".go", ".rs", ".php", ".rb", ".lua", ".sln", ".csproj" })
        ];

        private void Menu_AutoOrganize_Click(object sender, RoutedEventArgs e) { AutoOrganize(); }

        private void AutoOrganize()
        {
            AutoOrganizeDesktop(this);
        }

        public static void AutoOrganizeDesktop(MainWindow? initiator = null)
        {
            try
            {
                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                if (!Directory.Exists(desktopPath)) return;

                List<string> desktopFiles = [];
                foreach (string file in Directory.GetFiles(desktopPath))
                {
                    string fileName = Path.GetFileName(file);
                    if (string.IsNullOrWhiteSpace(fileName)) continue;
                    if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Equals(".ds_store", StringComparison.OrdinalIgnoreCase) ||
                        fileName.StartsWith("~$", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        FileAttributes attrs = File.GetAttributes(file);
                        if ((attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    }
                    catch { continue; }

                    desktopFiles.Add(file);
                }

                if (desktopFiles.Count == 0)
                {
                    MessageBox.Show("No organizable files were found on the desktop.", "Auto Organize", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                List<MainWindow> openFences = Application.Current.Windows
                    .OfType<MainWindow>()
                    .Where(w => !w._isDeleted && !w._isClosing && !w._isPortal)
                    .ToList();

                if (initiator is not null && openFences.Remove(initiator))
                {
                    openFences.Insert(0, initiator);
                }

                HashSet<MainWindow> modifiedExistingFences = [];
                List<string> remainingFiles = [];
                int totalMovedCount = 0;
                HashSet<string> affectedCategoryNames = new(StringComparer.OrdinalIgnoreCase);

                foreach (string file in desktopFiles)
                {
                    string ext = Path.GetExtension(file);
                    MainWindow? matchedCustomFence = null;

                    foreach (MainWindow fence in openFences)
                    {
                        if (MatchesExtensionFilter(ext, fence.AutoSortExtensions))
                        {
                            matchedCustomFence = fence;
                            break;
                        }
                    }

                    if (matchedCustomFence is not null)
                    {
                        if (TryMoveFileIntoFence(matchedCustomFence, file))
                        {
                            modifiedExistingFences.Add(matchedCustomFence);
                            affectedCategoryNames.Add(matchedCustomFence.FenceTitle);
                            totalMovedCount++;
                        }
                    }
                    else
                    {
                        remainingFiles.Add(file);
                    }
                }

                Dictionary<string, (string DefaultExtensions, List<string> Files)> groupedByCategory = new(StringComparer.OrdinalIgnoreCase);
                foreach (string file in remainingFiles)
                {
                    string ext = Path.GetExtension(file);
                    (string categoryTitle, string defaultExts) = ResolveCategoryForExtension(ext);
                    if (!groupedByCategory.TryGetValue(categoryTitle, out var entry))
                    {
                        entry = (defaultExts, []);
                        groupedByCategory[categoryTitle] = entry;
                    }
                    entry.Files.Add(file);
                }

                bool canReuseInitiator = initiator is not null &&
                    !initiator._isPortal &&
                    !initiator._isDeleted &&
                    !initiator._isClosing &&
                    initiator._currentFiles.Count == 0 &&
                    initiator._tabs.Count <= 1 &&
                    string.IsNullOrWhiteSpace(initiator.AutoSortExtensions) &&
                    IsDefaultFenceTitle(initiator.FenceTitle);

                Rect workArea = initiator is not null
                    ? initiator.GetCurrentScreenWorkingAreaLogical()
                    : SystemParameters.WorkArea;

                double fenceWidth = 280;
                double fenceHeight = 320;
                double margin = 24;
                double startLeft = workArea.Left + 40;
                double startTop = workArea.Top + 40;
                int createdFenceCount = 0;

                foreach (var kvp in groupedByCategory)
                {
                    string categoryTitle = kvp.Key;
                    string defaultExtensions = kvp.Value.DefaultExtensions;
                    List<string> categoryFiles = kvp.Value.Files;
                    if (categoryFiles.Count == 0) continue;

                    MainWindow? existingCategoryFence = openFences.FirstOrDefault(f =>
                        string.Equals(f.FenceTitle, categoryTitle, StringComparison.OrdinalIgnoreCase) ||
                        (f._activeTabIndex < f._tabs.Count && string.Equals(f._tabs[f._activeTabIndex].Title, categoryTitle, StringComparison.OrdinalIgnoreCase)));

                    if (existingCategoryFence is null && canReuseInitiator && initiator is not null)
                    {
                        existingCategoryFence = initiator;
                        canReuseInitiator = false;
                        existingCategoryFence.FenceTitle = categoryTitle;
                        existingCategoryFence.AutoSortExtensions = defaultExtensions;
                        if (existingCategoryFence._activeTabIndex < existingCategoryFence._tabs.Count)
                        {
                            existingCategoryFence._tabs[existingCategoryFence._activeTabIndex].Title = categoryTitle;
                            existingCategoryFence._tabs[existingCategoryFence._activeTabIndex].AutoSortExtensions = defaultExtensions;
                        }
                    }

                    if (existingCategoryFence is not null)
                    {
                        if (string.IsNullOrWhiteSpace(existingCategoryFence.AutoSortExtensions) && !string.IsNullOrWhiteSpace(defaultExtensions))
                        {
                            existingCategoryFence.AutoSortExtensions = defaultExtensions;
                            if (existingCategoryFence._activeTabIndex < existingCategoryFence._tabs.Count)
                            {
                                existingCategoryFence._tabs[existingCategoryFence._activeTabIndex].AutoSortExtensions = defaultExtensions;
                            }
                        }

                        foreach (string file in categoryFiles)
                        {
                            if (TryMoveFileIntoFence(existingCategoryFence, file))
                            {
                                modifiedExistingFences.Add(existingCategoryFence);
                                affectedCategoryNames.Add(existingCategoryFence.FenceTitle);
                                totalMovedCount++;
                            }
                        }
                    }
                    else
                    {
                        int col = createdFenceCount % 3;
                        int row = createdFenceCount / 3;
                        double nextLeft = startLeft + col * (fenceWidth + margin);
                        double nextTop = startTop + row * (fenceHeight + margin);

                        if (nextLeft + fenceWidth > workArea.Right)
                        {
                            nextLeft = Math.Max(workArea.Left + 20, workArea.Right - fenceWidth - 20);
                        }
                        if (nextTop + fenceHeight > workArea.Bottom)
                        {
                            nextTop = Math.Max(workArea.Top + 20, workArea.Bottom - fenceHeight - 20);
                        }

                        MainWindow newFence = new(Guid.NewGuid().ToString())
                        {
                            Left = nextLeft,
                            Top = nextTop,
                            Width = fenceWidth,
                            Height = fenceHeight
                        };
                        newFence._expandedLeft = nextLeft;
                        newFence._expandedTop = nextTop;
                        newFence._expandedWidth = fenceWidth;
                        newFence._expandedHeight = fenceHeight;

                        List<string> movedCategoryFiles = [];
                        foreach (string file in categoryFiles)
                        {
                            try
                            {
                                string vaultedPath = newFence.MoveToVault(file);
                                if (!string.Equals(vaultedPath, file, StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
                                {
                                    if (!movedCategoryFiles.Contains(vaultedPath, StringComparer.OrdinalIgnoreCase))
                                    {
                                        movedCategoryFiles.Add(vaultedPath);
                                        totalMovedCount++;
                                    }
                                }
                            }
                            catch { }
                        }

                        if (movedCategoryFiles.Count > 0)
                        {
                            FenceTab initialTab = new()
                            {
                                Title = categoryTitle,
                                Files = movedCategoryFiles,
                                AutoSortExtensions = defaultExtensions,
                                SortMethod = "Name"
                            };
                            newFence.SetInitialTab(initialTab);
                            newFence.FenceTitle = categoryTitle;
                            newFence.AutoSortExtensions = defaultExtensions;
                            newFence.Show();
                            openFences.Add(newFence);
                            affectedCategoryNames.Add(categoryTitle);
                            createdFenceCount++;
                        }
                        else
                        {
                            newFence._isDeleted = true;
                            newFence.Close();
                        }
                    }
                }

                foreach (MainWindow fence in modifiedExistingFences)
                {
                    fence.SaveCurrentTabState();
                    fence.RefreshTabsUI();
                    fence.SaveFenceState();
                }

                if (totalMovedCount > 0)
                {
                    string summary = string.Join(", ", affectedCategoryNames);
                    MessageBox.Show($"Organized {totalMovedCount} desktop file(s) into {affectedCategoryNames.Count} fence(s):\n{summary}", "Auto Organize", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("No desktop files needed to be organized.", "Auto Organize", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error organizing files: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static bool MatchesExtensionFilter(string fileExtension, string? filterExtensions)
        {
            if (string.IsNullOrWhiteSpace(fileExtension) || string.IsNullOrWhiteSpace(filterExtensions)) return false;
            string[] targets = filterExtensions.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries);
            foreach (string target in targets)
            {
                string normalized = target.StartsWith('.') ? target : "." + target;
                if (string.Equals(fileExtension, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static (string CategoryTitle, string DefaultExtensions) ResolveCategoryForExtension(string fileExtension)
        {
            if (!string.IsNullOrWhiteSpace(fileExtension))
            {
                foreach (var category in _fileTypeCategories)
                {
                    if (category.Extensions.Contains(fileExtension))
                    {
                        return (category.Title, category.DefaultExtensions);
                    }
                }
            }
            return ("Other Files", string.Empty);
        }

        private static bool IsDefaultFenceTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return true;
            return title.Equals("Fluid Fence", StringComparison.OrdinalIgnoreCase) ||
                   title.Equals("My First Fence", StringComparison.OrdinalIgnoreCase) ||
                   title.Equals("Main", StringComparison.OrdinalIgnoreCase) ||
                   title.Equals("New Tab", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryMoveFileIntoFence(MainWindow targetFence, string desktopFilePath)
        {
            try
            {
                string vaultedPath = targetFence.MoveToVault(desktopFilePath);
                if (!string.Equals(vaultedPath, desktopFilePath, StringComparison.OrdinalIgnoreCase) || !File.Exists(desktopFilePath))
                {
                    if (!targetFence._currentFiles.Contains(vaultedPath, StringComparer.OrdinalIgnoreCase))
                    {
                        targetFence._currentFiles.Add(vaultedPath);
                    }
                    return true;
                }
            }
            catch { }
            return false;
        }

        private void Menu_NewFence_Click(object sender, RoutedEventArgs e) { MainWindow newFence = new(Guid.NewGuid().ToString()) { Left = this.Left + 50, Top = this.Top + 50 }; newFence.Show(); }
        private void Menu_NewTab_Click(object sender, RoutedEventArgs e) { AddTabBtn_Click(sender, e); }
        private void Menu_DeleteFence_Click(object sender, RoutedEventArgs e) { if (MessageBox.Show("Permanently delete this fence?", "Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) DashboardDelete(); }
        private void Menu_RollUp_Click(object sender, RoutedEventArgs e) { ToggleRollUp(); }
        private void Menu_Sort_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem clickedSort || clickedSort.Tag is null) return;
            _saveSortMethod = clickedSort.Tag.ToString() ?? "None";
            UpdateViewMenuCheckmarks();
            ApplySorting();
            SaveFenceState();
        }

        private void Menu_IconSize_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem clickedItem || clickedItem.Tag is null) return;
            string tagStr = clickedItem.Tag.ToString() ?? "";
            if (tagStr == "Default") { CurrentIconSize = 48; }
            else if (double.TryParse(tagStr, out double newSize)) { CurrentIconSize = newSize; }
            UpdateViewMenuCheckmarks();
            ApplySorting();
            SaveFenceState();
        }

        private void UpdateViewMenuCheckmarks()
        {
            if (MenuSortBy is not null)
            {
                foreach (var item in MenuSortBy.Items.OfType<MenuItem>())
                {
                    item.IsCheckable = true;
                    item.IsChecked = string.Equals(item.Tag as string, _saveSortMethod, StringComparison.OrdinalIgnoreCase);
                }
            }

            if (MenuIconSize is not null)
            {
                string currentSizeTag = ((int)CurrentIconSize).ToString(System.Globalization.CultureInfo.InvariantCulture);
                foreach (var item in MenuIconSize.Items.OfType<MenuItem>())
                {
                    item.IsCheckable = true;
                    item.IsChecked = string.Equals(item.Tag as string, currentSizeTag, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        private void Menu_Settings_Click(object sender, RoutedEventArgs e) { SettingsWindow settings = new(this) { Owner = this }; settings.ShowDialog(); }
        private void Menu_EditColor_Click(object sender, RoutedEventArgs e) { SettingsWindow settings = new(this) { Owner = this }; settings.SwitchToTab("ThemeEditorGrid"); settings.ShowDialog(); }
        private void Menu_NewPortal_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Select a folder to mirror in the Portal Fence" };
            if (dialog.ShowDialog() == true) { MainWindow portalFence = new(Guid.NewGuid().ToString()) { Left = this.Left + 50, Top = this.Top + 50 }; portalFence.MakePortal(dialog.FolderName); portalFence.Show(); }
        }

        public void MakePortal(string path)
        {
            _pendingPortalPath = path;
            if (IsLoaded) ApplyPendingPortal();
        }

        private void ApplyPendingPortal()
        {
            string? path = _pendingPortalPath;
            if (string.IsNullOrWhiteSpace(path)) return;
            _pendingPortalPath = null;

            _isPortal = true;
            _portalPath = path;

            if (_activeTabIndex < _tabs.Count)
            {
                _tabs[_activeTabIndex].IsPortal = true;
                _tabs[_activeTabIndex].PortalPath = path;
            }

            try
            {
                TitleText.Text = new DirectoryInfo(path).Name + " (Portal)";
                System.Windows.Automation.AutomationProperties.SetName(this, TitleText.Text ?? string.Empty);
            }
            catch (Exception ex) { LogError("MakePortal title", ex); }

            _ = SetupPortalWatcherAsync();
            SaveFenceState();
        }

        private async Task SetupPortalWatcherAsync()
        {
            DisposePortalWatcher();

            string targetPortalPath = _portalPath;
            if (string.IsNullOrWhiteSpace(targetPortalPath) || !Directory.Exists(targetPortalPath))
            {
                SchedulePortalReconnect();
                return;
            }

            _portalCts = new CancellationTokenSource();
            var token = _portalCts.Token;

            try
            {
                string[] files = await Task.Run(() => Directory.GetFileSystemEntries(targetPortalPath), token);
                if (token.IsCancellationRequested || !_isPortal || !string.Equals(_portalPath, targetPortalPath, StringComparison.OrdinalIgnoreCase))
                    return;

                _currentFiles.Clear();
                _currentFiles.AddRange(files);

                RebuildPortalIcons();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { LogError("SetupPortalWatcher enumerate", ex); }

            if (token.IsCancellationRequested || !_isPortal || !string.Equals(_portalPath, targetPortalPath, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                var watcher = new FileSystemWatcher(targetPortalPath)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
                    InternalBufferSize = 65536,
                    EnableRaisingEvents = true
                };
                watcher.Created += Portal_FileChanged;
                watcher.Deleted += Portal_FileChanged;
                watcher.Renamed += Portal_FileChanged;
                watcher.Error += (_, _) => Dispatcher.InvokeAsync(() =>
                {
                    DisposePortalWatcher();
                    SchedulePortalReconnect();
                }, System.Windows.Threading.DispatcherPriority.Normal);

                _portalWatcher = watcher;
            }
            catch (Exception ex)
            {
                LogError("SetupPortalWatcher create", ex);
                SchedulePortalReconnect();
            }
        }

        private void RebuildPortalIcons()
        {
            try
            {
                if (IconPanel is null) return;
                IconPanel.Children.Clear();
                foreach (string file in _currentFiles.ToList()) AddIconToUI(file);
                ApplySorting();
            }
            catch (Exception ex) { LogError("RebuildPortalIcons", ex); }
        }

        private void DisposePortalWatcher()
        {
            _portalDebounceTimer?.Stop();

            try
            {
                _portalCts?.Cancel();
                _portalCts?.Dispose();
            }
            catch { }
            _portalCts = null;

            var watcher = _portalWatcher;
            _portalWatcher = null;
            if (watcher is null) return;

            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Created -= Portal_FileChanged;
                watcher.Deleted -= Portal_FileChanged;
                watcher.Renamed -= Portal_FileChanged;
                watcher.Dispose();
            }
            catch (Exception ex) { LogError("DisposePortalWatcher", ex); }
        }

        private void SchedulePortalReconnect()
        {
            if (_isDeleted || _isClosing) return;
            if (_portalRetryTimer is not null) return;

            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            timer.Tick += (_, _) =>
            {
                if (_isDeleted || _isClosing) { timer.Stop(); _portalRetryTimer = null; return; }
                if (!Directory.Exists(_portalPath)) return;

                timer.Stop();
                _portalRetryTimer = null;
                _ = SetupPortalWatcherAsync();
            };

            _portalRetryTimer = timer;
            timer.Start();
        }

        private void Portal_FileChanged(object sender, FileSystemEventArgs e)
        {
            Dispatcher.InvokeAsync(() =>
            {
                _portalDebounceTimer.Stop();
                _portalDebounceTimer.Start();
            }, DispatcherPriority.Background);
        }

        private async Task RefreshPortalUIAsync()
        {
            if (!_isPortal) return;
            string targetPortalPath = _portalPath;

            if (string.IsNullOrWhiteSpace(targetPortalPath) || !Directory.Exists(targetPortalPath))
            {
                DisposePortalWatcher();
                if (_currentFiles.Count > 0)
                {
                    _currentFiles.Clear();
                    RebuildPortalIcons();
                }
                SchedulePortalReconnect();
                return;
            }

            try
            {
                _portalCts?.Cancel();
                _portalCts?.Dispose();
            }
            catch { }
            _portalCts = new CancellationTokenSource();
            var token = _portalCts.Token;

            long currentGen = System.Threading.Interlocked.Increment(ref _portalRefreshGeneration);
            try
            {
                string[] newFiles = await Task.Run(() => Directory.GetFileSystemEntries(targetPortalPath), token);
                if (token.IsCancellationRequested || !_isPortal || !string.Equals(_portalPath, targetPortalPath, StringComparison.OrdinalIgnoreCase))
                    return;
                if (currentGen != System.Threading.Interlocked.Read(ref _portalRefreshGeneration)) return;

                var currentSet = new HashSet<string>(_currentFiles, StringComparer.OrdinalIgnoreCase);
                var newSet = new HashSet<string>(newFiles, StringComparer.OrdinalIgnoreCase);

                var toAdd = newFiles.Where(f => !currentSet.Contains(f)).ToList();
                var toRemove = _currentFiles.Where(f => !newSet.Contains(f)).ToList();

                if (toAdd.Count == 0 && toRemove.Count == 0) return;

                foreach (var f in toRemove)
                {
                    _currentFiles.Remove(f);
                    var panel = IconPanel.Children.OfType<StackPanel>().FirstOrDefault(p => string.Equals(p.ToolTip?.ToString(), f, StringComparison.OrdinalIgnoreCase));
                    if (panel != null) IconPanel.Children.Remove(panel);
                }

                foreach (var f in toAdd)
                {
                    _currentFiles.Add(f);
                    AddIconToUI(f);
                }

                if (_saveSortMethod != "None" && _saveSortMethod != "Custom" && _saveSortMethod != "DateAdd")
                {
                    ApplySorting();
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        private void RefreshTabsUI()
        {
            foreach (var timer in _tabDragTimers) timer.Stop();
            _tabDragTimers.Clear();

            TabBar.SelectionChanged -= TabBar_SelectionChanged;
            TabBar.Items.Clear();

            foreach (var tab in _tabs)
            {
                ListBoxItem item = new ListBoxItem { Tag = tab, Background = Brushes.Transparent };
                System.Windows.Automation.AutomationProperties.SetAutomationId(item, $"Tab_{tab.TabId}");
                System.Windows.Automation.AutomationProperties.SetName(item, tab.Title ?? string.Empty);

                Grid grid = new Grid();
                TextBlock tb = new TextBlock { Text = tab.Title, VerticalAlignment = VerticalAlignment.Center };
                TextBox tbox = new TextBox { Text = tab.Title, Visibility = Visibility.Collapsed, Width = 80, BorderThickness = new Thickness(1), Padding = new Thickness(2) };
                System.Windows.Automation.AutomationProperties.SetAutomationId(tbox, "TabRenameTextBox");
                System.Windows.Automation.AutomationProperties.SetName(tbox, "Rename Tab");
                ApplyInlineEditorTheme(tbox);

                grid.Children.Add(tb);
                grid.Children.Add(tbox);
                item.Content = grid;

                ContextMenu menu = new ContextMenu();

                menu.Opened += (s, e) => _isContextMenuOpen = true;
                menu.Closed += (s, e) => HandleMenuClosed();

                MenuItem renameItem = new MenuItem { Header = "Rename Tab" };
                renameItem.Click += (s, e) => StartTabRename(tb, tbox, tab);

                MenuItem deleteItem = new MenuItem { Header = "Delete Tab" };
                deleteItem.SetResourceReference(MenuItem.ForegroundProperty, "CustomErrorBrush");
                deleteItem.Click += (s, e) => DeleteTab(tab);

                menu.Items.Add(renameItem);
                menu.Items.Add(deleteItem);
                item.ContextMenu = menu;

                item.MouseDoubleClick += (s, e) => { e.Handled = true; StartTabRename(tb, tbox, tab); };
                item.PreviewMouseLeftButtonDown += (s, e) => { _tabDragStartPoint = e.GetPosition(null); };
                item.PreviewMouseMove += (s, e) => { HandleTabDrag(e, item, tab); };

                DispatcherTimer hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                _tabDragTimers.Add(hoverTimer);
                hoverTimer.Tick += (ts, te) =>
                {
                    hoverTimer.Stop();
                    if (TabBar.SelectedItem != item) TabBar.SelectedItem = item;
                };

                item.AllowDrop = true;
                item.DragEnter += (s, e) => { item.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "CustomSurfaceSelectedBrush"); hoverTimer.Start(); };
                item.DragLeave += (s, e) => { item.Background = Brushes.Transparent; hoverTimer.Stop(); };
                item.Drop += (s, e) => { hoverTimer.Stop(); TabItem_Drop(s, e); };

                TabBar.Items.Add(item);
            }

            TabBarContainer.Visibility = _tabs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

            if (_tabs.Count == 1) TitleText.Text = _tabs[0].Title;
            else if (string.IsNullOrWhiteSpace(TitleText.Text)) TitleText.Text = "Fluid Fence";
            System.Windows.Automation.AutomationProperties.SetName(this, TitleText.Text ?? string.Empty);

            if (_activeTabIndex >= _tabs.Count) _activeTabIndex = Math.Max(0, _tabs.Count - 1);

            TabBar.SelectedIndex = _activeTabIndex;
            TabBar.SelectionChanged += TabBar_SelectionChanged;

            if (_tabs.Count > 0) LoadTabIntoView(_tabs[_activeTabIndex]);
        }

        private void TabItem_Drop(object sender, DragEventArgs e)
        {
            if (sender is ListBoxItem item) item.Background = Brushes.Transparent;
            if (e.Handled) return;

            if (sender is ListBoxItem listItem && listItem.Tag is FenceTab targetTab)
            {
                if (e.Data.GetDataPresent("TearOffTab"))
                {
                    FenceTab? draggedTab = e.Data.GetData("TearOffTab") as FenceTab;
                    string? sourceId = e.Data.GetData("SourceFenceId") as string;
                    if (draggedTab != null && sourceId != null)
                    {
                        if (sourceId == FenceId)
                        {
                            int oldIndex = _tabs.IndexOf(draggedTab);
                            int newIndex = _tabs.IndexOf(targetTab);

                            if (oldIndex >= 0 && newIndex >= 0 && oldIndex != newIndex)
                            {
                                SaveCurrentTabState();
                                _tabs.RemoveAt(oldIndex);
                                _tabs.Insert(newIndex, draggedTab);
                                _activeTabIndex = newIndex;
                                RefreshTabsUI();
                                SaveFenceState();
                            }

                            e.Effects = DragDropEffects.Copy;
                            e.Handled = true;
                            return;
                        }
                        else
                        {
                            TransferTabFiles(draggedTab, sourceId);
                            _tabs.Add(draggedTab);
                            _activeTabIndex = _tabs.Count - 1;
                            RefreshTabsUI();
                            SaveFenceState();

                            e.Effects = DragDropEffects.Move;
                            e.Handled = true;
                            return;
                        }
                    }
                }

                if (targetTab == _tabs[_activeTabIndex]) return;

                if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
                {
                    string? sourceId = e.Data.GetData("FenceSourceId") as string;
                    bool isInternal = (sourceId == FenceId);
                    bool isCopy = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;

                    SaveCurrentTabState();

                    if (isInternal && !isCopy)
                    {
                        foreach (string f in files) _currentFiles.Remove(f);
                    }

                    foreach (string file in files)
                    {
                        try
                        {
                            if (!targetTab.IsPortal)
                            {
                                string vaultedPath = MoveToVault(file);
                                if (!targetTab.Files.Contains(vaultedPath)) targetTab.Files.Add(vaultedPath);
                            }
                            else
                            {
                                string dest = MoveIntoPortal(file, targetTab.PortalPath);
                                if (!targetTab.Files.Contains(dest)) targetTab.Files.Add(dest);
                            }
                        }
                        catch (Exception ex)
                        {
                            LogError($"Drop failed for '{file}'", ex);

                            if (!targetTab.IsPortal && !targetTab.Files.Contains(file))
                                targetTab.Files.Add(file);
                        }
                    }

                    if (isInternal) ApplySorting();
                    SaveFenceState();
                    e.Effects = isCopy ? DragDropEffects.Copy : DragDropEffects.Move;
                    e.Handled = true;
                }
            }
        }

        private void StartTabRename(TextBlock tb, TextBox tbox, FenceTab tab)
        {
            tb.Visibility = Visibility.Collapsed;
            tbox.Visibility = Visibility.Visible;
            tbox.Focus();
            tbox.SelectAll();
            bool isRenaming = true;

            void CommitRename()
            {
                if (!isRenaming) return;
                isRenaming = false;
                if (!string.IsNullOrWhiteSpace(tbox.Text))
                {
                    tab.Title = tbox.Text;
                    tb.Text = tbox.Text;
                    if (_tabs.Count == 1)
                    {
                        TitleText.Text = tbox.Text;
                        System.Windows.Automation.AutomationProperties.SetName(this, TitleText.Text ?? string.Empty);
                    }
                }
                tbox.Visibility = Visibility.Collapsed;
                tb.Visibility = Visibility.Visible;
                SaveFenceState();
            }

            tbox.LostFocus += (s, e) => CommitRename();
            tbox.KeyDown += (s, e) => { if (e.Key == Key.Enter) CommitRename(); else if (e.Key == Key.Escape) { isRenaming = false; tbox.Visibility = Visibility.Collapsed; tb.Visibility = Visibility.Visible; } };
        }

        private async void DeleteTab(FenceTab tab)
        {
            if (_tabs.Count <= 1)
            {
                MessageBox.Show("You cannot delete the last tab. Delete the Fence instead.", "Delete Tab", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (MessageBox.Show($"Delete tab '{tab.Title}' and remove it from this fence?", "Delete Tab", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {

                int removedIndex = _tabs.IndexOf(tab);
                bool removedActive = removedIndex == _activeTabIndex;

                _tabs.Remove(tab);

                if (removedIndex >= 0 && removedIndex < _activeTabIndex)
                {

                    _activeTabIndex--;
                }

                if (_activeTabIndex >= _tabs.Count) _activeTabIndex = Math.Max(0, _tabs.Count - 1);
                if (_activeTabIndex < 0) _activeTabIndex = 0;

                RefreshTabsUI();

                if (removedActive && _activeTabIndex < _tabs.Count)
                    LoadTabIntoView(_tabs[_activeTabIndex]);

                SaveFenceState();
            }

            await Task.Delay(50);
            if (this.IsLoaded && !this.IsMouseOver && !_isContextMenuOpen && !_isDraggingSelectionBox)
            {
                AnimateGhostMode(false);
                if (_isRolledUp && _isTemporarilyRevealed)
                {
                    AnimateRollUp();
                    _isTemporarilyRevealed = false;
                }
            }
        }

        private void HandleTabDrag(MouseEventArgs e, ListBoxItem item, FenceTab tab)
        {
            if (e.LeftButton == MouseButtonState.Pressed && _tabDragStartPoint.HasValue)
            {
                Point currentPoint = e.GetPosition(null);
                if (Math.Abs(currentPoint.X - _tabDragStartPoint.Value.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(currentPoint.Y - _tabDragStartPoint.Value.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    if (_tabs.Count <= 1) return;

                    SaveCurrentTabState();

                    DataObject dragData = new DataObject("TearOffTab", tab);
                    dragData.SetData("SourceFenceId", FenceId);
                    _tabDragStartPoint = null;

                    DragDropEffects result = DragDrop.DoDragDrop(item, dragData, DragDropEffects.Move | DragDropEffects.Copy);

                    if (result == DragDropEffects.Copy)
                    {
                        return;
                    }
                    else if (result == DragDropEffects.None)
                    {
                        double mouseX = System.Windows.Forms.Cursor.Position.X;
                        double mouseY = System.Windows.Forms.Cursor.Position.Y;
                        PresentationSource source = PresentationSource.FromVisual(this);
                        double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                        double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

                        MainWindow newFence = new MainWindow(Guid.NewGuid().ToString())
                        {
                            Left = (mouseX / dpiX) - 50,
                            Top = (mouseY / dpiY) - 20
                        };

                        newFence.TransferTabFiles(tab, this.FenceId);
                        _tabs.Remove(tab);
                        RefreshTabsUI();
                        SaveFenceState();

                        newFence.SetInitialTab(tab);
                        newFence.Show();
                    }
                    else if (result == DragDropEffects.Move)
                    {
                        _tabs.Remove(tab);
                        RefreshTabsUI();
                        SaveFenceState();
                    }
                }
            }
        }

        private void TabBar_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TabBar.SelectedIndex >= 0 && TabBar.SelectedIndex != _activeTabIndex && TabBar.SelectedIndex < _tabs.Count)
            {
                SaveCurrentTabState();
                _activeTabIndex = TabBar.SelectedIndex;
                LoadTabIntoView(_tabs[_activeTabIndex]);
            }
        }

        private void LoadTabIntoView(FenceTab tab)
        {
            _currentFiles.Clear();
            _currentFiles.AddRange(tab.Files);
            _saveSortMethod = tab.SortMethod;
            AutoSortExtensions = tab.AutoSortExtensions;
            _isPortal = tab.IsPortal;
            _portalPath = tab.PortalPath;

            if (_isPortal) _ = SetupPortalWatcherAsync();
            else { DisposePortalWatcher(); }

            ApplySorting();
        }

        private void SaveCurrentTabState()
        {
            if (_activeTabIndex < _tabs.Count)
            {
                _tabs[_activeTabIndex].Files = _currentFiles.ToList();
                _tabs[_activeTabIndex].SortMethod = _saveSortMethod;
                _tabs[_activeTabIndex].AutoSortExtensions = AutoSortExtensions;

                _tabs[_activeTabIndex].IsPortal = _isPortal;
                _tabs[_activeTabIndex].PortalPath = _portalPath ?? string.Empty;
            }
        }

        private void AddTabBtn_Click(object sender, RoutedEventArgs e)
        {
            var newTab = new FenceTab { Title = "New Tab" };
            _tabs.Add(newTab);
            _activeTabIndex = _tabs.Count - 1;
            RefreshTabsUI();
            SaveFenceState();
        }

        private void TransferTabFiles(FenceTab tab, string sourceFenceId)
        {
            if (string.IsNullOrWhiteSpace(sourceFenceId) ||
                Path.GetFileName(sourceFenceId) != sourceFenceId ||
                sourceFenceId is "." or ".." ||
                sourceFenceId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return;
            }

            string sourceStorageDir = Path.GetFullPath(Path.Combine(App.ConfigFolder, "Storage", sourceFenceId));
            string targetStorageDir = Path.GetFullPath(Path.Combine(App.ConfigFolder, "Storage", this.FenceId));

            if (!Directory.Exists(sourceStorageDir)) return;
            Directory.CreateDirectory(targetStorageDir);

            string sourcePrefix = sourceStorageDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            for (int i = 0; i < tab.Files.Count; i++)
            {
                string filePath = tab.Files[i];
                if (string.IsNullOrWhiteSpace(filePath)) continue;

                string fullFilePath;
                try { fullFilePath = Path.GetFullPath(filePath); }
                catch { continue; }

                if (fullFilePath.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    string fileName = Path.GetFileName(fullFilePath);
                    string newPath = Path.Combine(targetStorageDir, fileName);

                    int counter = 1;
                    while (File.Exists(newPath) || Directory.Exists(newPath))
                    {
                        string nameOnly = Path.GetFileNameWithoutExtension(fileName);
                        string ext = Path.GetExtension(fileName);
                        newPath = Path.Combine(targetStorageDir, $"{nameOnly} ({counter}){ext}");
                        counter++;
                    }

                    try
                    {
                        if (Directory.Exists(fullFilePath)) Directory.Move(fullFilePath, newPath);
                        else if (File.Exists(fullFilePath)) File.Move(fullFilePath, newPath);
                        tab.Files[i] = newPath;
                    }
                    catch { }
                }
            }

            try
            {
                if (Directory.Exists(sourceStorageDir) && !Directory.EnumerateFileSystemEntries(sourceStorageDir).Any())
                {
                    Directory.Delete(sourceStorageDir);
                }
            }
            catch { }
        }

        private string MoveIntoPortal(string source, string portalPath)
        {
            string fileName = Path.GetFileName(source);
            string dest = Path.Combine(portalPath, fileName);

            if (string.Equals(source, dest, StringComparison.OrdinalIgnoreCase)) return dest;

            int counter = 1;
            while (File.Exists(dest) || Directory.Exists(dest))
            {
                string nameOnly = Path.GetFileNameWithoutExtension(fileName);
                string ext = Path.GetExtension(fileName);
                dest = Path.Combine(portalPath, $"{nameOnly} ({counter}){ext}");
                counter++;
            }

            string sourceRoot = Path.GetPathRoot(source) ?? "";
            string destRoot = Path.GetPathRoot(dest) ?? "";

            if (sourceRoot.Equals(destRoot, StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.Exists(source)) Directory.Move(source, dest);
                else File.Move(source, dest);
            }
            else
            {

                int result = NativeMethods.PerformFileOperation(
                    new WindowInteropHelper(this).Handle, 0x0001, source, dest, 0x0000);

                if (result != 0) throw new IOException($"Shell move reported {result}");
            }

            return dest;
        }

        private string MoveToVault(string originalPath)
        {
            string storageDir = Path.Combine(App.ConfigFolder, "Storage", FenceId);
            Directory.CreateDirectory(storageDir);
            if (Path.GetDirectoryName(originalPath) == storageDir) return originalPath;

            string fileName = Path.GetFileName(originalPath);
            string newPath = Path.Combine(storageDir, fileName);
            int counter = 1;

            while (File.Exists(newPath) || Directory.Exists(newPath))
            {
                string nameOnly = Path.GetFileNameWithoutExtension(fileName);
                string ext = Path.GetExtension(fileName);
                newPath = Path.Combine(storageDir, $"{nameOnly} ({counter}){ext}");
                counter++;
            }

            try
            {
                string sourceRoot = Path.GetPathRoot(originalPath) ?? "";
                string destRoot = Path.GetPathRoot(newPath) ?? "";

                if (sourceRoot.Equals(destRoot, StringComparison.OrdinalIgnoreCase))
                {
                    if (Directory.Exists(originalPath)) Directory.Move(originalPath, newPath);
                    else if (File.Exists(originalPath)) File.Move(originalPath, newPath);
                }
                else
                {
                    int res = NativeMethods.PerformFileOperation(new WindowInteropHelper(this).Handle, 0x0001, originalPath, newPath, 0x0000);
                    if (res != 0) return originalPath;
                }
            }
            catch (Exception ex)
            {
                LogError("MoveToVault", ex);
                return originalPath;
            }

            return newPath;
        }

        private void SendToRecycleBin(string path)
        {
            try { if (!File.Exists(path) && !Directory.Exists(path)) return; _ = NativeMethods.PerformFileOperation(new WindowInteropHelper(this).Handle, 0x0003, path, null, 0x0040 | 0x0010 | 0x0004); } catch (Exception ex) { LogError($"Recycle Bin Error: {path}", ex); }
        }

        private void RemoveFileFromVaultAndList(string filePath)
        {
            bool isUsedElsewhere = false;
            for (int i = 0; i < _tabs.Count; i++)
            {
                if (i != _activeTabIndex && _tabs[i].Files.Contains(filePath))
                {
                    isUsedElsewhere = true;
                    break;
                }
            }

            if (!isUsedElsewhere)
            {
                try { SendToRecycleBin(filePath); } catch { }
            }

            _currentFiles.Remove(filePath);
        }

        private void ApplySorting()
        {
            if (_saveSortMethod != "None" && _saveSortMethod != "DateAdd" && _saveSortMethod != "Custom")
            {
                try
                {
                    switch (_saveSortMethod)
                    {
                        case "Name":
                        {
                            var keys = new Dictionary<string, string>(_currentFiles.Count, StringComparer.OrdinalIgnoreCase);
                            foreach (var f in _currentFiles) keys[f] = Path.GetFileName(f);
                            _currentFiles.Sort((a, b) => string.Compare(keys.GetValueOrDefault(a), keys.GetValueOrDefault(b), StringComparison.OrdinalIgnoreCase));
                            break;
                        }
                        case "Size":
                        {
                            var keys = new Dictionary<string, long>(_currentFiles.Count, StringComparer.OrdinalIgnoreCase);
                            foreach (var f in _currentFiles) keys[f] = GetFileSize(f);
                            _currentFiles.Sort((a, b) => keys.GetValueOrDefault(b).CompareTo(keys.GetValueOrDefault(a)));
                            break;
                        }
                        case "Type":
                        {
                            var keys = new Dictionary<string, string>(_currentFiles.Count, StringComparer.OrdinalIgnoreCase);
                            foreach (var f in _currentFiles) keys[f] = Path.GetExtension(f);
                            _currentFiles.Sort((a, b) => string.Compare(keys.GetValueOrDefault(a), keys.GetValueOrDefault(b), StringComparison.OrdinalIgnoreCase));
                            break;
                        }
                        case "DateMod":
                        {
                            var keys = new Dictionary<string, DateTime>(_currentFiles.Count, StringComparer.OrdinalIgnoreCase);
                            foreach (var f in _currentFiles) { try { keys[f] = File.GetLastWriteTimeUtc(f); } catch { keys[f] = DateTime.MinValue; } }
                            _currentFiles.Sort((a, b) => keys.GetValueOrDefault(b).CompareTo(keys.GetValueOrDefault(a)));
                            break;
                        }
                        case "DateCre":
                        {
                            var keys = new Dictionary<string, DateTime>(_currentFiles.Count, StringComparer.OrdinalIgnoreCase);
                            foreach (var f in _currentFiles) { try { keys[f] = File.GetCreationTimeUtc(f); } catch { keys[f] = DateTime.MinValue; } }
                            _currentFiles.Sort((a, b) => keys.GetValueOrDefault(b).CompareTo(keys.GetValueOrDefault(a)));
                            break;
                        }
                    }
                }
                catch { }
            }
            RefreshIconUI();
        }

        private static long GetFileSize(string path) { try { return new FileInfo(path).Length; } catch { return 0; } }

        public void RefreshIconUI()
        {
            var existingPanels = IconPanel.Children.OfType<StackPanel>().ToList();
            IconPanel.Children.Clear();
            ClearSelection();

            if (_currentFiles.Count == 0)
            {

                var emptyHint = new TextBlock { Margin = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Text = "Drop shortcuts here\u2026" };
                emptyHint.SetResourceReference(TextBlock.ForegroundProperty, "CustomTextSecondaryBrush");
                IconPanel.Children.Add(emptyHint);
                return;
            }

            string searchQuery = SearchBox.Text;
            foreach (string file in _currentFiles)
            {
                string fileName = Path.GetFileNameWithoutExtension(file);
                if (!string.IsNullOrWhiteSpace(searchQuery) && !fileName.Contains(searchQuery, StringComparison.OrdinalIgnoreCase)) continue;

                var panel = existingPanels.FirstOrDefault(p => string.Equals(p.ToolTip?.ToString(), file, StringComparison.OrdinalIgnoreCase));

                if (panel != null && panel.Width == CurrentIconSize + 24)
                {
                    IconPanel.Children.Add(panel);
                }
                else
                {
                    AddIconToUI(file);
                }
            }
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Handled) return;

            if (e.Data.GetDataPresent("TearOffTab"))
            {
                FenceTab? tab = e.Data.GetData("TearOffTab") as FenceTab;
                string? sourceId = e.Data.GetData("SourceFenceId") as string;
                if (tab != null && sourceId != null)
                {
                    if (sourceId == FenceId)
                    {
                        e.Effects = DragDropEffects.Copy;
                        e.Handled = true;
                        return;
                    }
                    else
                    {
                        TransferTabFiles(tab, sourceId);
                        _tabs.Add(tab);
                        _activeTabIndex = _tabs.Count - 1;
                        RefreshTabsUI();
                        SaveFenceState();
                        e.Effects = DragDropEffects.Move;
                        e.Handled = true;
                        return;
                    }
                }
            }

            if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                SaveCurrentTabState();

                string? sourceId = e.Data.GetData("FenceSourceId") as string; bool isInternal = (sourceId == FenceId); int insertIndex = _currentFiles.Count; Point dropPoint = e.GetPosition(IconPanel);
                for (int i = 0; i < IconPanel.Children.Count; i++) { if (IconPanel.Children[i] is FrameworkElement child) { Point childPos = child.TranslatePoint(new Point(0, 0), IconPanel); if (dropPoint.Y >= childPos.Y && dropPoint.Y <= childPos.Y + child.ActualHeight) { if (dropPoint.X < childPos.X + (child.ActualWidth / 2)) { insertIndex = i; break; } } else if (dropPoint.Y < childPos.Y) { insertIndex = i; break; } } }
                string? targetPath = null; if (insertIndex < _currentFiles.Count) targetPath = _currentFiles[insertIndex];
                if (isInternal) { foreach (string f in files) _currentFiles.Remove(f); int finalIndex = _currentFiles.Count; if (targetPath is not null) { finalIndex = _currentFiles.IndexOf(targetPath); if (finalIndex < 0) finalIndex = _currentFiles.Count; } foreach (string f in files) { _currentFiles.Insert(finalIndex, f); finalIndex++; } _saveSortMethod = "Custom"; ApplySorting(); SaveFenceState(); e.Effects = DragDropEffects.None; e.Handled = true; return; }
                List<string> processedFiles = [];
                foreach (string file in files) { try { if (!_isPortal) { string vaultedPath = MoveToVault(file); if (!_currentFiles.Contains(vaultedPath)) processedFiles.Add(vaultedPath); } else { string fileName = Path.GetFileName(file); string dest = Path.Combine(_portalPath, fileName); if (file != dest && !File.Exists(dest) && !Directory.Exists(dest)) { if (Directory.Exists(file)) Directory.Move(file, dest); else File.Move(file, dest); } if (!_currentFiles.Contains(dest)) processedFiles.Add(dest); } } catch { if (!_currentFiles.Contains(file)) processedFiles.Add(file); } }
                int extFinalIndex = _currentFiles.Count; if (targetPath is not null) { extFinalIndex = _currentFiles.IndexOf(targetPath); if (extFinalIndex < 0) extFinalIndex = _currentFiles.Count; }
                foreach (string pf in processedFiles) { _currentFiles.Insert(extFinalIndex, pf); extFinalIndex++; }
                if (string.IsNullOrEmpty(_saveSortMethod) || _saveSortMethod == "None")
                {
                    _saveSortMethod = "Custom";
                }
                ApplySorting();
                SaveFenceState();
                if (!string.IsNullOrEmpty(sourceId) && sourceId != FenceId) e.Effects = DragDropEffects.Move; else e.Effects = DragDropEffects.None;
            }
        }

        private async void AddIconToUI(string file)
        {
            string currentFilePath = file; double containerWidth = CurrentIconSize + 24;
            StackPanel itemContainer = new() { Orientation = Orientation.Vertical, Margin = new Thickness(8), Width = containerWidth, ToolTip = currentFilePath, Background = System.Windows.Media.Brushes.Transparent, AllowDrop = true };

            ScaleTransform scaleTransform = new() { ScaleX = 1.0, ScaleY = 1.0, CenterX = containerWidth / 2, CenterY = CurrentIconSize / 2 };
            itemContainer.RenderTransform = scaleTransform;
            itemContainer.MouseEnter += (s, e) =>
            {
                if (s is StackPanel sp && sp.RenderTransform is ScaleTransform st)
                {
                    st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.05, TimeSpan.FromMilliseconds(150)) { EasingFunction = new QuarticEase() { EasingMode = EasingMode.EaseOut } });
                    st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.05, TimeSpan.FromMilliseconds(150)) { EasingFunction = new QuarticEase() { EasingMode = EasingMode.EaseOut } });
                }
            };
            itemContainer.MouseLeave += (s, e) =>
            {
                if (s is StackPanel sp && sp.RenderTransform is ScaleTransform st)
                {
                    st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(200)) { EasingFunction = new QuarticEase() { EasingMode = EasingMode.EaseOut } });
                    st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(200)) { EasingFunction = new QuarticEase() { EasingMode = EasingMode.EaseOut } });
                }
            };

            double imageHeight = CurrentIconSize;
            System.Windows.Controls.Image iconImage = new() { Width = CurrentIconSize, Height = imageHeight, Margin = new Thickness(0, 0, 0, 5), SnapsToDevicePixels = true, UseLayoutRounding = true, Stretch = Stretch.Uniform };

            string fileExt = Path.GetExtension(currentFilePath);
            string nameWithoutExt = Path.GetFileNameWithoutExtension(currentFilePath);
            bool isShortcutOrUrl = fileExt.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || fileExt.Equals(".url", StringComparison.OrdinalIgnoreCase);
            bool hasDoubleExt = !string.IsNullOrEmpty(Path.GetExtension(nameWithoutExt));
            string displayLabel = Directory.Exists(currentFilePath)
                ? Path.GetFileName(currentFilePath)
                : ((isShortcutOrUrl && !hasDoubleExt)
                    ? nameWithoutExt
                    : ((hasDoubleExt && _dangerousExtensions.Contains(fileExt))
                        ? Path.GetFileName(currentFilePath)
                        : (isShortcutOrUrl ? nameWithoutExt : Path.GetFileName(currentFilePath))));

            Border imageBorder = new Border { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(0), Child = iconImage, ClipToBounds = true };
            TextBlock textBlock = new() { Text = displayLabel, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center, FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxHeight = 35 };
            textBlock.SetResourceReference(TextBlock.ForegroundProperty, "CustomTextPrimaryBrush");
            textBlock.Effect = BuildLabelHalo();
            imageBorder.SetResourceReference(Border.BorderBrushProperty, "CustomBorderSubtleBrush");

            System.Windows.Automation.AutomationProperties.SetAutomationId(itemContainer, $"Icon_{Path.GetFileName(currentFilePath)}");
            System.Windows.Automation.AutomationProperties.SetName(itemContainer, textBlock.Text);

            itemContainer.Children.Add(imageBorder); itemContainer.Children.Add(textBlock); IconPanel.Children.Add(itemContainer);

            try
            {
                ImageSource? fetchedSource = null;

                await _iconSemaphore.WaitAsync();
                try
                {
                    fetchedSource = await System.Threading.Tasks.Task.Run(() => GetHighResIcon(currentFilePath));
                }
                finally
                {
                    try { _iconSemaphore.Release(); } catch (ObjectDisposedException) { }
                }

                if (fetchedSource is not null) { iconImage.Source = fetchedSource; RenderOptions.SetBitmapScalingMode(iconImage, BitmapScalingMode.HighQuality); }
            }
            catch (Exception ex) { LogError($"Async Icon Failure: {currentFilePath}", ex); }

            ContextMenu rightClickMenu = new();
            MenuItem renameItem = new() { Header = "Rename File" }; MenuItem removeItem = new() { Header = "Remove from Fence" };
            removeItem.Click += (s, args) => { if (_isPortal) { if (MessageBox.Show("This is a Folder Portal.\n\nDeleting items here will send the actual files on your computer to the Recycle Bin. Are you sure you want to delete?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return; } if (_selectedItems.Count > 1 && _selectedItems.Contains(itemContainer)) { foreach (StackPanel selectedPanel in _selectedItems.ToList()) { if (selectedPanel.ToolTip is string path) RemoveFileFromVaultAndList(path); } } else { RemoveFileFromVaultAndList(currentFilePath); } ClearSelection(); ApplySorting(); SaveFenceState(); };

            void TriggerRename()
            {
                textBlock.Visibility = Visibility.Collapsed;
                TextBox renameBox = new()
                {
                    Text = textBlock.Text,
                    Width = containerWidth,
                    Margin = new Thickness(-5, 0, -5, 0),
                    BorderThickness = new Thickness(1),
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Padding = new Thickness(2)
                };
                System.Windows.Automation.AutomationProperties.SetAutomationId(renameBox, "IconRenameTextBox");
                System.Windows.Automation.AutomationProperties.SetName(renameBox, "Rename File");
                ApplyInlineEditorTheme(renameBox);
                itemContainer.Children.Add(renameBox);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    renameBox.Focus();
                    Keyboard.Focus(renameBox);
                    renameBox.SelectAll();
                }), DispatcherPriority.Input);

                bool isHandlingRename = false;

                void EndRenameEditor()
                {
                    if (itemContainer.Children.Contains(renameBox))
                    {
                        itemContainer.Children.Remove(renameBox);
                        textBlock.Visibility = Visibility.Visible;
                    }
                }

                void CommitRename()
                {
                    if (isHandlingRename) return;
                    isHandlingRename = true;
                    try
                    {
                        string proposedName = renameBox.Text.Trim();
                        string currentName = textBlock.Text.Trim();

                        if (string.Equals(proposedName, currentName, StringComparison.Ordinal))
                        {
                            EndRenameEditor();
                            return;
                        }

                        if (!string.IsNullOrWhiteSpace(proposedName) &&
                            proposedName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
                            !proposedName.Contains('/') && !proposedName.Contains('\\') &&
                            proposedName != "." && proposedName != "..")
                        {
                            string? dir = Path.GetDirectoryName(currentFilePath);
                            bool isDirectory = Directory.Exists(currentFilePath);
                            string ext = isDirectory ? string.Empty : Path.GetExtension(currentFilePath);
                            if (dir is not null)
                            {
                                string finalFileName = (!string.IsNullOrEmpty(ext) && proposedName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                                    ? proposedName
                                    : proposedName + ext;
                                string newPath = Path.GetFullPath(Path.Combine(dir, finalFileName));
                                string fullDir = Path.GetFullPath(dir);

                                if (newPath.StartsWith(fullDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(Path.GetDirectoryName(newPath), fullDir, StringComparison.OrdinalIgnoreCase))
                                {
                                    bool isCaseOnlyChange = string.Equals(currentFilePath, newPath, StringComparison.OrdinalIgnoreCase);

                                    bool isDestinationInUseByFence = !isCaseOnlyChange && (
                                        _currentFiles.Any(f => string.Equals(f, newPath, StringComparison.OrdinalIgnoreCase)) ||
                                        _tabs.Any(t => t.Files.Any(f => string.Equals(f, newPath, StringComparison.OrdinalIgnoreCase)))
                                    );

                                    if (isDestinationInUseByFence)
                                    {
                                        MessageBox.Show(
                                            $"An item named '{proposedName}' already exists in this fence.\n\nPlease choose a different name.",
                                            "Cannot Rename",
                                            MessageBoxButton.OK,
                                            MessageBoxImage.Warning);

                                        isHandlingRename = false;
                                        Dispatcher.BeginInvoke(new Action(() =>
                                        {
                                            renameBox.Focus();
                                            renameBox.SelectAll();
                                        }), DispatcherPriority.Input);
                                        return;
                                    }

                                    bool targetExistsOnDisk = isDirectory ? Directory.Exists(newPath) : File.Exists(newPath);

                                    if (targetExistsOnDisk && !isCaseOnlyChange)
                                    {
                                        if (_isPortal)
                                        {
                                            if (MessageBox.Show(
                                                $"A file named '{finalFileName}' already exists in this folder.\n\nDo you want to replace it?",
                                                "Confirm Rename",
                                                MessageBoxButton.YesNo,
                                                MessageBoxImage.Warning) != MessageBoxResult.Yes)
                                            {
                                                isHandlingRename = false;
                                                Dispatcher.BeginInvoke(new Action(() =>
                                                {
                                                    renameBox.Focus();
                                                    renameBox.SelectAll();
                                                }), DispatcherPriority.Input);
                                                return;
                                            }
                                            SendToRecycleBin(newPath);
                                        }
                                        else
                                        {
                                            try { SendToRecycleBin(newPath); } catch { }
                                            try { if (File.Exists(newPath)) File.Delete(newPath); } catch { }
                                        }
                                    }

                                    if (isDirectory)
                                    {
                                        if (isCaseOnlyChange)
                                        {
                                            string tempPath = Path.Combine(dir, $"{proposedName}_{Guid.NewGuid():N}{ext}");
                                            Directory.Move(currentFilePath, tempPath);
                                            Directory.Move(tempPath, newPath);
                                        }
                                        else
                                        {
                                            Directory.Move(currentFilePath, newPath);
                                        }
                                    }
                                    else
                                    {
                                        File.Move(currentFilePath, newPath, overwrite: true);
                                    }

                                    int fileIndex = _currentFiles.IndexOf(currentFilePath);
                                    if (fileIndex >= 0) _currentFiles[fileIndex] = newPath;
                                    else _currentFiles.Add(newPath);

                                    SaveCurrentTabState();
                                    ApplySorting();
                                    SaveFenceState();
                                    return;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogError($"Rename Failed: {currentFilePath}", ex);
                        MessageBox.Show($"Could not rename item: {ex.Message}", "Rename Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }

                    EndRenameEditor();
                }

                renameBox.KeyDown += (s2, e2) =>
                {
                    if (e2.Key == Key.Enter)
                    {
                        e2.Handled = true;
                        CommitRename();
                    }
                    else if (e2.Key == Key.Escape)
                    {
                        e2.Handled = true;
                        isHandlingRename = true;
                        EndRenameEditor();
                    }
                };
                renameBox.LostFocus += (s2, e2) => { CommitRename(); };
            }

            renameItem.Click += (s, args) => TriggerRename(); rightClickMenu.Items.Add(renameItem); rightClickMenu.Items.Add(removeItem); itemContainer.ContextMenu = rightClickMenu; rightClickMenu.Opened += (s, args) => _isContextMenuOpen = true; rightClickMenu.Closed += (s, args) => HandleMenuClosed();
            textBlock.MouseLeftButtonDown += (s, args) => { if (args.ClickCount == 2) return; args.Handled = true; TriggerRename(); };
            System.Windows.Point? dragStartPoint = null;

            itemContainer.PreviewMouseLeftButtonDown += (s, args) => { this.Focus(); dragStartPoint = args.GetPosition(null); bool isCtrlPressed = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl); if (isCtrlPressed) { if (_selectedItems.Contains(itemContainer)) { itemContainer.Background = System.Windows.Media.Brushes.Transparent; _selectedItems.Remove(itemContainer); } else { itemContainer.Background = _highlightBrush; _selectedItems.Add(itemContainer); } } else if (!_selectedItems.Contains(itemContainer)) { ClearSelection(); itemContainer.Background = _highlightBrush; _selectedItems.Add(itemContainer); } };
            itemContainer.PreviewMouseRightButtonDown += (s, args) => { if (!_selectedItems.Contains(itemContainer)) { ClearSelection(); itemContainer.Background = _highlightBrush; _selectedItems.Add(itemContainer); } };
            itemContainer.PreviewMouseMove += (s, args) => { if (args.LeftButton == MouseButtonState.Pressed && dragStartPoint.HasValue) { System.Windows.Point currentPoint = args.GetPosition(null); if (Math.Abs(currentPoint.X - dragStartPoint.Value.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(currentPoint.Y - dragStartPoint.Value.Y) > SystemParameters.MinimumVerticalDragDistance) { string[] draggedFiles; if (_selectedItems.Contains(itemContainer) && _selectedItems.Count > 1) { draggedFiles = _selectedItems.Select(panel => panel.ToolTip.ToString()!).ToArray(); } else { draggedFiles = [currentFilePath]; } DataObject dragData = new(DataFormats.FileDrop, draggedFiles); dragData.SetData("FenceSourceId", FenceId); DragDropEffects result = DragDrop.DoDragDrop(itemContainer, dragData, DragDropEffects.Move | DragDropEffects.Copy); if (result == DragDropEffects.Move) { foreach (string f in draggedFiles) _currentFiles.Remove(f); ClearSelection(); ApplySorting(); SaveFenceState(); } dragStartPoint = null; } } };
            itemContainer.MouseDown += (s, args) =>
            {
                if (args.ClickCount == 2 && args.ChangedButton == MouseButton.Left)
                {
                    if (File.Exists(currentFilePath) || Directory.Exists(currentFilePath))
                    {
                        try
                        {
                            if (_isPortal)
                            {
                                string ext = Path.GetExtension(currentFilePath).ToLowerInvariant();
                                if (ext == ".lnk")
                                {
                                    string resolvedTarget = ResolveShortcutTarget(currentFilePath);
                                    bool resolutionFailed = string.IsNullOrWhiteSpace(resolvedTarget)
                                        || string.Equals(resolvedTarget, currentFilePath, StringComparison.OrdinalIgnoreCase)
                                        || (!File.Exists(resolvedTarget) && !Directory.Exists(resolvedTarget));
                                    string targetExt = Path.GetExtension(resolvedTarget);
                                    if (resolutionFailed || _dangerousExtensions.Contains(targetExt))
                                    {
                                        string targetDisplay = resolutionFailed ? "(unresolved target)" : resolvedTarget;
                                        if (MessageBox.Show($"\"{Path.GetFileName(currentFilePath)}\" is a shortcut pointing to:\n{targetDisplay}\n\nAre you sure you want to run this shortcut from this Folder Portal?", "Security Confirmation", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                                            return;
                                    }
                                }
                                else if (_dangerousExtensions.Contains(ext))
                                {
                                    if (MessageBox.Show($"\"{Path.GetFileName(currentFilePath)}\" is an executable or script.\n\nAre you sure you want to run this file from this Folder Portal?", "Security Confirmation", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                                        return;
                                }
                            }
                            string? workDir = Path.GetDirectoryName(currentFilePath);
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = currentFilePath,
                                WorkingDirectory = string.IsNullOrEmpty(workDir) ? string.Empty : workDir,
                                UseShellExecute = true
                            });
                        }
                        catch (Exception ex) { MessageBox.Show($"Could not open file.\nError: {ex.Message}", "Launch Error"); }
                    }
                    else
                    {
                        MessageBox.Show("This file or folder no longer exists.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                        _currentFiles.Remove(currentFilePath);
                        RefreshIconUI();
                        SaveFenceState();
                    }
                }
            };
        }

        private static object? _wshShell;
        private static bool _shellUnavailable;
        private static readonly object _shellGate = new();

        private static dynamic? GetShell()
        {
            if (_shellUnavailable) return null;
            if (_wshShell is not null) return _wshShell;

            lock (_shellGate)
            {
                if (_shellUnavailable) return null;
                if (_wshShell is not null) return _wshShell;
                try
                {
                    Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                    if (shellType is null) { _shellUnavailable = true; return null; }

                    _wshShell = Activator.CreateInstance(shellType);
                    if (_wshShell is null) _shellUnavailable = true;
                    return _wshShell;
                }
                catch (Exception ex)
                {

                    LogError("WScript.Shell unavailable; shortcuts will use their own icons", ex);
                    _shellUnavailable = true;
                    return null;
                }
            }
        }

        internal static void ReleaseShell()
        {
            lock (_shellGate)
            {
                if (_wshShell is null) return;
                try { if (Marshal.IsComObject(_wshShell)) Marshal.ReleaseComObject(_wshShell); }
                catch { }
                _wshShell = null;
            }
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Stamp, string Target)>
            _shortcutCache = new(StringComparer.OrdinalIgnoreCase);

        private static string ResolveShortcutTarget(string shortcutPath)
        {
            DateTime writeTime;
            try { writeTime = File.GetLastWriteTimeUtc(shortcutPath); }
            catch { writeTime = DateTime.MinValue; }

            if (_shortcutCache.TryGetValue(shortcutPath, out var cached) && cached.Stamp == writeTime)
                return cached.Target;

            string resolved = ResolveShortcutTargetUncached(shortcutPath);
            _shortcutCache[shortcutPath] = (writeTime, resolved);
            return resolved;
        }

        private static string ResolveShortcutTargetUncached(string shortcutPath)
        {
            try
            {
                lock (_shellGate)
                {
                    dynamic? shell = GetShell();
                    if (shell is null) return shortcutPath;

                    var shortcut = shell.CreateShortcut(shortcutPath);
                    try
                    {
                        string targetPath = shortcut.TargetPath;
                        if (!string.IsNullOrEmpty(targetPath) && (File.Exists(targetPath) || Directory.Exists(targetPath))) return targetPath;

                        string iconLocation = shortcut.IconLocation;
                        if (!string.IsNullOrEmpty(iconLocation))
                        {
                            string[] parts = iconLocation.Split(',');
                            if (parts.Length > 0 && File.Exists(parts[0])) return parts[0];
                        }
                    }
                    finally
                    {
                        if (shortcut is not null && Marshal.IsComObject(shortcut))
                        {
                            try { Marshal.FinalReleaseComObject(shortcut); } catch { }
                        }
                    }
                }
            }
            catch (Exception ex) { LogError($"Shortcut Resolve Failed: {shortcutPath}", ex); }

            return shortcutPath;
        }

        private static ImageSource? GetHighResIcon(string filePath)
        {
            if (!File.Exists(filePath) && !Directory.Exists(filePath)) return null;
            string ext = Path.GetExtension(filePath);

            if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                filePath = ResolveShortcutTarget(filePath);
                ext = Path.GetExtension(filePath);
            }

            if (_imageExtensions.Contains(ext))
            {
                try { BitmapImage bitmap = new(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 256; bitmap.UriSource = new Uri(filePath, UriKind.Absolute); bitmap.EndInit(); bitmap.Freeze(); return bitmap; }
                catch (Exception ex) { LogError($"Native Image Loader Failed: {filePath}", ex); }
            }

            if (ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string[] lines = File.ReadAllLines(filePath);
                    string? iconFile = null;
                    int iconIndex = 0;
                    foreach (string line in lines)
                    {
                        if (line.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase)) iconFile = line.Substring(9).Trim();
                        if (line.StartsWith("IconIndex=", StringComparison.OrdinalIgnoreCase)) _ = int.TryParse(line.Substring(10).Trim(), out iconIndex);
                    }

                    if (!string.IsNullOrEmpty(iconFile))
                    {
                        iconFile = Environment.ExpandEnvironmentVariables(iconFile);
                        if (!iconFile.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase) && !iconFile.StartsWith("//", StringComparison.OrdinalIgnoreCase) && File.Exists(iconFile))
                        {
                            uint sizeRequest = (uint)((16 << 16) | 256);
                            IntPtr hIconL = IntPtr.Zero;
                            IntPtr hIconS = IntPtr.Zero;
                            try
                            {
                                int res = NativeMethods.SHDefExtractIcon(iconFile, iconIndex, 0, out hIconL, out hIconS, sizeRequest);
                                if (res == 0 && hIconL != IntPtr.Zero)
                                {
                                    ImageSource imgSrc = Imaging.CreateBitmapSourceFromHIcon(hIconL, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                                    imgSrc.Freeze();
                                    return imgSrc;
                                }
                            }
                            finally
                            {
                                if (hIconL != IntPtr.Zero) NativeMethods.DestroyIcon(hIconL);
                                if (hIconS != IntPtr.Zero) NativeMethods.DestroyIcon(hIconS);
                            }
                        }
                    }
                }
                catch { }

                try
                {
                    NativeMethods.SHFILEINFO shinfo = new();
                    IntPtr result = NativeMethods.SHGetFileInfo(filePath, 0, ref shinfo, (uint)Marshal.SizeOf(shinfo), 0x000000100 | 0x000000000);
                    if (result != IntPtr.Zero && shinfo.hIcon != IntPtr.Zero)
                    {
                        ImageSource imgSrc;
                        try
                        {
                            imgSrc = Imaging.CreateBitmapSourceFromHIcon(shinfo.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                            imgSrc.Freeze();
                        }
                        finally
                        {
                            NativeMethods.DestroyIcon(shinfo.hIcon);
                        }
                        return imgSrc;
                    }
                }
                catch { }
                return null;
            }

            try
            {
                Guid iid = new("bcc18b79-ba16-442f-80c4-8a59c30d463b");
                int hr = NativeMethods.SHCreateItemFromParsingName(filePath, IntPtr.Zero, in iid, out NativeMethods.IShellItemImageFactory factory);
                if (hr == 0 && factory is not null)
                {
                    IntPtr hbitmap = IntPtr.Zero;
                    try
                    {
                        try { factory.GetImage(new NativeMethods.NativeSize { Width = 256, Height = 256 }, 0x0, out hbitmap); } catch { }
                        if (hbitmap == IntPtr.Zero) { try { factory.GetImage(new NativeMethods.NativeSize { Width = 256, Height = 256 }, 0x4, out hbitmap); } catch { } }

                        if (hbitmap != IntPtr.Zero)
                        {
                            ImageSource imgSrc = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                            imgSrc.Freeze();
                            return imgSrc;
                        }
                    }
                    finally
                    {
                        if (hbitmap != IntPtr.Zero) NativeMethods.DeleteObject(hbitmap);
                    }
                }
            }
            catch (Exception ex) { LogError($"Shell API Factory Failed: {filePath}", ex); }

            if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".ico", StringComparison.OrdinalIgnoreCase) || ext.Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    uint sizeRequest = (uint)((16 << 16) | 256);
                    IntPtr hIconLarge = IntPtr.Zero;
                    IntPtr hIconSmall = IntPtr.Zero;
                    try
                    {
                        int res = NativeMethods.SHDefExtractIcon(filePath, 0, 0, out hIconLarge, out hIconSmall, sizeRequest);
                        if (res == 0 && hIconLarge != IntPtr.Zero)
                        {
                            ImageSource imgSrc = Imaging.CreateBitmapSourceFromHIcon(hIconLarge, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                            imgSrc.Freeze();
                            return imgSrc;
                        }
                    }
                    finally
                    {
                        if (hIconLarge != IntPtr.Zero) NativeMethods.DestroyIcon(hIconLarge);
                        if (hIconSmall != IntPtr.Zero) NativeMethods.DestroyIcon(hIconSmall);
                    }
                }
                catch (Exception ex) { LogError($"Binary Extraction Failed: {filePath}", ex); }
            }
            try { NativeMethods.SHFILEINFO shinfo = new(); IntPtr result = NativeMethods.SHGetFileInfo(filePath, 0, ref shinfo, (uint)Marshal.SizeOf(shinfo), 0x000000100 | 0x000000000); if (result != IntPtr.Zero && shinfo.hIcon != IntPtr.Zero) { ImageSource imgSrc; try { imgSrc = Imaging.CreateBitmapSourceFromHIcon(shinfo.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); imgSrc.Freeze(); } finally { NativeMethods.DestroyIcon(shinfo.hIcon); } return imgSrc; } } catch (Exception ex) { LogError($"Win32 Fallback Failed: {filePath}", ex); }
            return null;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            string globalConfigPath = Path.Combine(App.ConfigFolder, "global_config.json");
            bool isFirstRun = !File.Exists(globalConfigPath);
            bool showInTaskbar = true;
            GlobalConfig? loadedConfig = null;

            try
            {
                loadedConfig = await ConfigStore.ReadAsync();
                showInTaskbar = loadedConfig.ShowTaskbarIcon;
                _globalGhostMode = loadedConfig.EnableGhostMode;
            }
            catch { }

            this.ShowInTaskbar = showInTaskbar;

            try
            {
                string fenceDir = Path.GetDirectoryName(_saveFilePath)!;
                if (Directory.Exists(fenceDir))
                {
                    foreach (string stale in Directory.GetFiles(fenceDir, "*.json.tmp"))
                    {

                        if (File.GetLastWriteTimeUtc(stale) < DateTime.UtcNow.AddMinutes(-5))
                            try { File.Delete(stale); } catch { }
                    }
                }
            }
            catch (Exception ex) { LogError("SweepStaleTempFiles", ex); }

            await LoadFenceStateAsync();

            if (loadedConfig is not null)
            {
                try
                {
                    var active = Core.Theming.ThemeCatalog.Get(
                        !string.IsNullOrWhiteSpace(loadedConfig.ThemeId) ? loadedConfig.ThemeId : loadedConfig.Theme.ThemeName);

                    ApplyTheme(new Core.ThemeSettings
                    {
                        ThemeName           = active.Name,
                        BackgroundColor     = active.Colors.Background    ?? "",
                        HeaderColor         = active.Colors.Header        ?? "",
                        FontColor           = active.Colors.TextPrimary   ?? "",
                        SecondaryFontColor  = active.Colors.TextSecondary ?? "",
                        AccentColor         = active.Colors.Accent        ?? "",
                        BackgroundMediaPath = loadedConfig.Theme.BackgroundMediaPath,
                        MediaOpacity        = loadedConfig.Theme.MediaOpacity
                    });
                }
                catch { }
            }

            ResolveBodyColor();

            var helper = new WindowInteropHelper(this); IntPtr myWindowHandle = helper.Handle; IntPtr exStyle = NativeMethods.GetWindowLongPtr(myWindowHandle, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLongPtr(myWindowHandle, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle.ToInt64() | NativeMethods.WS_EX_TOOLWINDOW));

            _hwndSource = HwndSource.FromHwnd(myWindowHandle);
            if (_hwndSource is not null) { _wndProcHook = new HwndSourceHook(WndProc); _hwndSource.AddHook(_wndProcHook); }

            this.PreviewKeyDown += Window_PreviewKeyDown; this.PreviewMouseDown += Window_PreviewMouseDown;
            IconPanel.MouseRightButtonDown += (s, args) => { if (args.OriginalSource is ScrollViewer || args.OriginalSource is WrapPanel || args.OriginalSource == IconPanel) { ClearSelection(); _isContextMenuOpen = true; HeaderContextMenu.PlacementTarget = IconPanel; HeaderContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint; HeaderContextMenu.IsOpen = true; args.Handled = true; } };

            if (MenuRollUpFence is not null) MenuRollUpFence.IsCheckable = true;

            HeaderContextMenu.Opened += (s, args) =>
            {
                _isContextMenuOpen = true;
                if (MenuRollUpFence is not null) MenuRollUpFence.IsChecked = _isRolledUp;
                UpdateViewMenuCheckmarks();
            };
            HeaderContextMenu.Closed += (s, args) => HandleMenuClosed();
            this.PreviewMouseRightButtonDown += (s, args) => _isContextMenuOpen = true;

            if (isFirstRun)
            {
                await ConfigStore.UpdateAsync(c =>
                {
                    c.FirstRunComplete = true;
                    c.ShowTaskbarIcon = true;
                    c.RestoreFilesOnDelete = true;
                    c.EnableGhostMode = false;
                });
                SettingsWindow settings = new(this, true) { Owner = this };
                settings.ShowDialog();
            }
            if (!this.IsMouseOver) AnimateGhostMode(false);

            ApplyPendingPortal();

            if (!_isDeleted && !_loadFailed && !File.Exists(_saveFilePath))
            {
                try { await PerformDiskWriteAsync(); }
                catch (Exception ex) { LogError("InitialFenceWrite", ex); }
            }
        }

        private unsafe IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_WINDOWPOSCHANGING && lParam != IntPtr.Zero)
            {
                NativeMethods.WINDOWPOS* pPos = (NativeMethods.WINDOWPOS*)lParam;
                if ((pPos->flags & NativeMethods.SWP_NOMOVE) == 0 && (pPos->flags & NativeMethods.SWP_NOSIZE) != 0)
                {
                    int snapMargin = 20;
                    foreach (var workArea in GetCachedWorkingAreas())
                    {
                        if (Math.Abs(pPos->x - workArea.Left) < snapMargin) pPos->x = workArea.Left;
                        else if (Math.Abs((pPos->x + pPos->cx) - workArea.Right) < snapMargin) pPos->x = workArea.Right - pPos->cx;

                        if (Math.Abs(pPos->y - workArea.Top) < snapMargin) pPos->y = workArea.Top;
                        else if (Math.Abs((pPos->y + pPos->cy) - workArea.Bottom) < snapMargin) pPos->y = workArea.Bottom - pPos->cy;
                    }
                }
            }
            if (msg == NativeMethods.WM_SETTINGCHANGE && (nint)wParam == NativeMethods.SPI_SETDESKWALLPAPER)
            {
                if (_colorSource == FenceColorSource.AutoMatch)
                {
                    _ = Dispatcher.InvokeAsync(() => ResolveBodyColor(), System.Windows.Threading.DispatcherPriority.Background);
                }
            }
            return IntPtr.Zero;
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {

            if (HandleFenceShortcuts(e)) { e.Handled = true; return; }

            if (e.Key == Key.Delete && _selectedItems.Count > 0 && e.OriginalSource is not TextBox) { if (_isPortal) { if (MessageBox.Show("This is a Folder Portal.\n\nDeleting items here will send the actual files on your computer to the Recycle Bin. Are you sure you want to delete?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return; } foreach (StackPanel selectedPanel in _selectedItems.ToList()) { if (selectedPanel.ToolTip is string path) RemoveFileFromVaultAndList(path); } ClearSelection(); ApplySorting(); SaveFenceState(); e.Handled = true; return; }
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                Rect workArea = GetCurrentScreenWorkingAreaLogical();
                double logicalLeft = workArea.Left;
                double logicalTop = workArea.Top;
                double logicalRight = workArea.Right;
                double logicalBottom = workArea.Bottom;
                bool wasHandled = false;
                this.BeginAnimation(Window.LeftProperty, null);
                this.BeginAnimation(Window.TopProperty, null);
                this.BeginAnimation(Window.WidthProperty, null);
                this.BeginAnimation(Window.HeightProperty, null);
                if (_isRolledUp)
                {
                    _isRolledUp = false;
                    _isTemporarilyRevealed = false;
                    this.Width = _expandedWidth;
                    this.Height = _expandedHeight;
                }
                if (e.Key == Key.Up) { this.Top = logicalTop; wasHandled = true; }
                else if (e.Key == Key.Down) { this.Top = logicalBottom - this.Height; wasHandled = true; }
                else if (e.Key == Key.Left) { this.Left = logicalLeft; wasHandled = true; }
                else if (e.Key == Key.Right) { this.Left = logicalRight - this.Width; wasHandled = true; }
                if (wasHandled)
                {
                    e.Handled = true;
                    DetermineDockState();
                    UpdateDockOrientation();
                    _expandedLeft = this.Left;
                    _expandedTop = this.Top;
                    _expandedWidth = this.Width;
                    _expandedHeight = this.Height;
                    SaveFenceState();
                }
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_wndProcHook is not null && _hwndSource is not null)
            {
                try { _hwndSource.RemoveHook(_wndProcHook); } catch (Exception ex) { LogError("RemoveHook", ex); }
                _wndProcHook = null;
                _hwndSource = null;
            }

            ReleaseOwnedResources();
            base.OnClosed(e);
        }

        private void ReleaseOwnedResources()
        {
            if (_resourcesReleased) return;
            _resourcesReleased = true;

            try { _iconSemaphore?.Dispose(); } catch (Exception ex) { LogError("DisposeSemaphore", ex); }
            DisposePortalWatcher();

            if (_portalRetryTimer != null) { _portalRetryTimer.Stop(); _portalRetryTimer = null; }
            _portalDebounceTimer?.Stop();
            _saveTimer?.Stop();

            foreach (var timer in _tabDragTimers) timer.Stop();
            _tabDragTimers.Clear();

            try
            {
                BackgroundMedia?.Stop();
                BackgroundMedia?.Close();
                if (BackgroundMedia != null) BackgroundMedia.Source = null;
            }
            catch { }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            _isClosing = true;
            if (_isDeleted || _loadFailed)
            {
                base.OnClosing(e);
                return;
            }

            try
            {
                PerformDiskWriteSynchronous();
            }
            catch (Exception ex)
            {
                LogError("OnClosing", ex);
            }

            base.OnClosing(e);
        }
        private void ClearSelection() { foreach (var item in _selectedItems) item.Background = System.Windows.Media.Brushes.Transparent; _selectedItems.Clear(); }
        private void IconPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { this.Focus(); if (e.OriginalSource is ScrollViewer || e.OriginalSource is WrapPanel) { ClearSelection(); _selectionStartPoint = e.GetPosition(SelectionCanvas); _isDraggingSelectionBox = true; SelectionBox.Visibility = Visibility.Visible; SelectionBox.Width = 0; SelectionBox.Height = 0; Canvas.SetLeft(SelectionBox, _selectionStartPoint.X); Canvas.SetTop(SelectionBox, _selectionStartPoint.Y); IconPanel.CaptureMouse(); } }
        private void IconPanel_MouseMove(object sender, MouseEventArgs e) { if (_isDraggingSelectionBox) { System.Windows.Point currentPoint = e.GetPosition(SelectionCanvas); double x = Math.Min(currentPoint.X, _selectionStartPoint.X); double y = Math.Min(currentPoint.Y, _selectionStartPoint.Y); double width = Math.Abs(currentPoint.X - _selectionStartPoint.X); double height = Math.Abs(currentPoint.Y - _selectionStartPoint.Y); Canvas.SetLeft(SelectionBox, x); Canvas.SetTop(SelectionBox, y); SelectionBox.Width = width; SelectionBox.Height = height; Rect selectionRect = new(x, y, width, height); foreach (UIElement child in IconPanel.Children) { if (child is StackPanel item) { System.Windows.Point itemPos = item.TranslatePoint(new System.Windows.Point(0, 0), SelectionCanvas); Rect itemRect = new(itemPos, new Size(item.ActualWidth, item.ActualHeight)); if (selectionRect.IntersectsWith(itemRect)) { if (!_selectedItems.Contains(item)) { item.Background = _highlightBrush; _selectedItems.Add(item); } } else if (_selectedItems.Contains(item)) { item.Background = System.Windows.Media.Brushes.Transparent; _selectedItems.Remove(item); } } } } }
        private void IconPanel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) { if (_isDraggingSelectionBox) { _isDraggingSelectionBox = false; SelectionBox.Visibility = Visibility.Collapsed; IconPanel.ReleaseMouseCapture(); } }
        public void Dispose()
        {
            ReleaseOwnedResources();
            GC.SuppressFinalize(this);
        }
    }
}
