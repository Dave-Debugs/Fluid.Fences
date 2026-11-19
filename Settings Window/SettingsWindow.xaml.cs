using Microsoft.Win32;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DesktopFences
{
    public sealed partial class SettingsWindow : Wpf.Ui.Controls.FluentWindow
    {
        public sealed class FenceListItem
        {
            public MainWindow Fence { get; }
            public string Title { get; set; }
            public FenceListItem(MainWindow fence) { Fence = fence; Title = fence.FenceTitle; }
            public override string ToString() => Title;
        }

        private readonly MainWindow? _callingFence;
        private MainWindow? _currentlySelectedFence;
        private bool _isLoadingFenceData;
        private bool _isColorInitializing;
        private bool _suppressMediaSliderAutoSave;
        private bool _isGeneratingTheme;
        private bool? _lastSavedStartupEnabled;
        private IntPtr _windowHandle;
        private int? _lastAppliedBorderColorRef;

        private readonly DispatcherTimer _liveUpdateTimer = new();
        private readonly DispatcherTimer _mediaOpacityTimer = new();
        private Color _pendingColor;
        private double _pendingOpacity;
        private bool _hasPendingColor;
        private bool _pendingColorIsCustom;

        private Action? _pendingCommitAction;

        private static readonly ConcurrentDictionary<string, string?> LoopResolveCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly CubicEase TabEaseOut = CreateFrozenCubicEase();
        private readonly Dictionary<string, FrameworkElement?> _pages;

        private static CubicEase CreateFrozenCubicEase()
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            ease.Freeze();
            return ease;
        }

        public SettingsWindow(MainWindow? callingFence, bool isFirstRun = false)
        {
            InitializeComponent();
            _callingFence = callingFence;

            _pages = new Dictionary<string, FrameworkElement?>(StringComparer.Ordinal)
            {
                ["AboutGrid"] = AboutGrid,
                ["HowToGrid"] = HowToGrid,
                ["GeneralGrid"] = GeneralGrid,
                ["FencesGrid"] = FencesGrid,
                ["ThemeEditorGrid"] = ThemeEditorGrid,
                ["FeedbackGrid"] = FeedbackGrid,
            };

            Loaded += (_, _) =>
            {
                _windowHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                Core.Theming.WallpaperSampler.ExcludedWindowHandle = _windowHandle;
                UpdateWindowBorderColor();
                if (_currentTag == "AboutGrid")
                    StartHomeLogoAnimation();
            };

            PresetDropdown.SelectedIndex = 0;

            BuildNavigation();
            BuildHowTo();
            LoadThemeCatalog();
            LoadAllFences();
            RefreshStats();
            SwitchToTab("AboutGrid");

            _statusTimer.Tick += (_, _) => HideStatus(commitPending: true);

            Core.Theming.ThemeEngine.ThemeApplied += OnThemeApplied;

            if (HomeLogoAnim is not null)
            {
                HomeLogoAnim.MediaOpened += (_, _) =>
                {
                    if (_currentTag != "AboutGrid" || Core.Theming.ThemeEngine.IsReducedMotion())
                    {
                        try { HomeLogoAnim.Pause(); } catch { }
                    }
                };
                HomeLogoAnim.MediaFailed += (_, _) =>
                {
                    try { HomeLogoAnim.Stop(); HomeLogoAnim.Close(); HomeLogoAnim.Source = null; } catch { }
                };
            }

            WireVersionEasterEgg();

            if (isFirstRun)
            {
                AboutDescriptionText.Text =
                    "Welcome. Right-click the Fluid Fences icon in your system tray to create your first fence, " +
                    "then drag anything you like into it. Ctrl+Alt+Z hides them all when you need a clean desktop.";
                SwitchToTab("HowToGrid");
                ShowStatus("New here? These are the basics.", StatusKind.Info);
            }

            _ = LoadGlobalSettingsAsync();

            _liveUpdateTimer.Interval = TimeSpan.FromMilliseconds(33);
            _liveUpdateTimer.Tick += (_, _) =>
            {
                if (!FlushPendingColor())
                {
                    _liveUpdateTimer.Stop();
                }
            };

            _mediaOpacityTimer.Interval = TimeSpan.FromMilliseconds(200);
            _mediaOpacityTimer.Tick += (_, _) =>
            {
                _mediaOpacityTimer.Stop();
                _ = AutoSaveGlobalAsync();
            };
        }

        private bool FlushPendingColor()
        {
            if (!_hasPendingColor || _currentlySelectedFence is null) return false;

            if (_pendingColorIsCustom || _currentlySelectedFence.ColorSource == FenceColorSource.Custom)
            {
                _currentlySelectedFence.SetFenceColor(_pendingColor, _pendingOpacity);
                _pendingColorIsCustom = false;
                UpdateColorSourceCard();
            }
            else
            {
                _currentlySelectedFence.SetFenceOpacity(_pendingOpacity);
            }
            _hasPendingColor = false;
            return true;
        }

        private string _themeCategoryMode = "All";

        private void LoadThemeCatalog(string? filter = null)
        {
            try
            {
                if (Core.Theming.ThemeCatalog.Themes.Count == 0)
                    Core.Theming.ThemeCatalog.Reload();

                IEnumerable<Core.Theming.ThemeDefinition> items = Core.Theming.ThemeCatalog.Themes;

                if (!string.IsNullOrWhiteSpace(filter))
                {
                    string f = filter.Trim();
                    items = items.Where(t =>
                        (t.Name ?? "").Contains(f, StringComparison.OrdinalIgnoreCase) ||
                        (t.Category ?? "").Contains(f, StringComparison.OrdinalIgnoreCase) ||
                        (t.BaseTheme ?? "").Contains(f, StringComparison.OrdinalIgnoreCase));
                }

                var allList = items.ToList();

                var darkList = allList
                    .Where(t => !string.Equals(t.BaseTheme, "Light", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(t => t.Category, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                var lightList = allList
                    .Where(t => string.Equals(t.BaseTheme, "Light", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(t => t.Category, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                DarkThemeGallery.ItemsSource = darkList;
                LightThemeGallery.ItemsSource = lightList;

                DarkThemesCountText.Text = $" ({darkList.Count})";
                LightThemesCountText.Text = $" ({lightList.Count})";

                ThemeTabAll.Content = $"All Themes ({allList.Count})";
                ThemeTabDark.Content = $"Dark Themes ({darkList.Count})";
                ThemeTabLight.Content = $"Light Themes ({lightList.Count})";

                UpdateThemeTabStyles();
                ApplyThemeCategoryVisibility();

                Dispatcher.BeginInvoke(new Action(HighlightActiveSwatch), DispatcherPriority.Loaded);
            }
            catch (Exception ex) { LogError("LoadThemeCatalog", ex); }
        }

        private void SetThemeCategoryMode(string mode)
        {
            _themeCategoryMode = mode;
            UpdateThemeTabStyles();
            ApplyThemeCategoryVisibility();
        }

        private void UpdateThemeTabStyles()
        {
            SetPillStyle(ThemeTabAll, _themeCategoryMode == "All");
            SetPillStyle(ThemeTabDark, _themeCategoryMode == "Dark");
            SetPillStyle(ThemeTabLight, _themeCategoryMode == "Light");
        }

        private static void SetPillStyle(Button btn, bool isActive)
        {
            if (btn is null) return;
            if (isActive)
            {
                btn.SetResourceReference(Control.BackgroundProperty, "CustomAccentBrush");
                btn.SetResourceReference(Control.ForegroundProperty, "CustomTextOnAccentBrush");
            }
            else
            {
                btn.Background = Brushes.Transparent;
                btn.SetResourceReference(Control.ForegroundProperty, "CustomTextSecondaryBrush");
            }
        }

        private void ApplyThemeCategoryVisibility()
        {
            bool showDark = _themeCategoryMode == "All" || _themeCategoryMode == "Dark";
            bool showLight = _themeCategoryMode == "All" || _themeCategoryMode == "Light";

            DarkThemesSection.Visibility = showDark ? Visibility.Visible : Visibility.Collapsed;
            LightThemesSection.Visibility = showLight ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ThemeTabAll_Click(object sender, RoutedEventArgs e) => SetThemeCategoryMode("All");
        private void ThemeTabDark_Click(object sender, RoutedEventArgs e) => SetThemeCategoryMode("Dark");
        private void ThemeTabLight_Click(object sender, RoutedEventArgs e) => SetThemeCategoryMode("Light");

        private void HighlightActiveSwatch()
        {
            try
            {
                HighlightGallery(DarkThemeGallery);
                HighlightGallery(LightThemeGallery);
            }
            catch (Exception ex) { LogError("HighlightActiveSwatch", ex); }
        }

        private static void HighlightGallery(ListBox gallery)
        {
            if (gallery is null) return;
            foreach (var item in gallery.Items)
            {
                if (gallery.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container)
                    continue;

                container.ApplyTemplate();
                if (FindDescendant<Border>(container, b => b.Tag is string) is not Border border) continue;

                bool active = item is Core.Theming.ThemeDefinition t &&
                              string.Equals(t.Id, App.ActiveThemeId, StringComparison.OrdinalIgnoreCase);

                if (active)
                    border.SetResourceReference(Border.BorderBrushProperty, "CustomAccentBrush");
                else
                    border.BorderBrush = Brushes.Transparent;
            }
        }

        private static T? FindDescendant<T>(DependencyObject root, Func<T, bool>? match = null)
            where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T typed && (match is null || match(typed))) return typed;

                var deeper = FindDescendant(child, match);
                if (deeper is not null) return deeper;
            }
            return null;
        }

        private void ThemeSwatch_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not string id) return;
            ApplyThemeById(id);
        }

        private async void GenerateTheme_Click(object sender, RoutedEventArgs e)
        {
            if (_isGeneratingTheme) return;
            _isGeneratingTheme = true;
            var triggerButton = sender as UIElement;
            if (triggerButton is not null) triggerButton.IsEnabled = false;

            try
            {
                bool dark = !string.Equals(
                    Core.Theming.ThemeCatalog.Get(App.ActiveThemeId).BaseTheme, "Light",
                    StringComparison.OrdinalIgnoreCase);

                var ourWindows = Application.Current.Windows.OfType<Window>()
                                    .Where(w => w.Visibility == Visibility.Visible).ToList();

                ShowStatus("Reading your desktop...", StatusKind.Info);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

                foreach (var w in ourWindows)
                {
                    try { w.Visibility = Visibility.Hidden; } catch { }
                }

                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                await Task.Delay(220);

                (Core.Theming.ThemeDefinition? generated, Core.Theming.WallpaperSource source, string? problem) result;
                try
                {
                    uint? monitor = ResolveCurrentMonitorIndex();
                    Rect? region = ResolveCurrentMonitorBounds();

                    result = await Core.Theming.ThemeGenerator.GenerateFromDesktopAsync(
                        dark, allowScreenCapture: true, monitorIndex: monitor, region: region);
                }
                finally
                {
                    foreach (var w in ourWindows)
                    {
                        try
                        {
                            if (PresentationSource.FromVisual(w) is not null)
                                w.Visibility = Visibility.Visible;
                        }
                        catch { }
                    }
                    try { Activate(); } catch { }
                }

                var (generated, source, problem) = result;

                if (generated is null)
                {
                    ShowStatus(problem ?? "Could not read your desktop background.", StatusKind.Warning);
                    return;
                }

                generated.Name = Core.Theming.ThemeGenerator.GeneratedThemeName;

                var saved = await Core.Theming.ThemeCatalog.SaveAsync(
                    generated,
                    overwriteId: Core.Theming.ThemeGenerator.GeneratedThemeId);

                LoopResolveCache.Clear();
                string? loop = ResolveThemeLoop(saved);
                App.SetTheme(saved.Id, animate: true, mediaPath: loop,
                             mediaOpacity: MediaOpacitySlider.Value / 100.0);
                ApplyMediaToOpenFences(saved, loop, MediaOpacitySlider.Value / 100.0);

                LoadThemeCatalog(ThemeFilter.Text);
                RefreshStats();

                string how = source switch
                {
                    Core.Theming.WallpaperSource.StaticFile => "from your wallpaper image",
                    Core.Theming.WallpaperSource.DesktopLayer => "from your live desktop",
                    _ => "from your desktop"
                };

                var warnings = Core.Theming.ThemeEngine.AuditContrast(saved);
                string quality = warnings.Count == 0
                    ? "Meets WCAG AA throughout."
                    : $"{warnings.Count} token(s) below WCAG AA.";

                ShowStatus($"Theme generated {how}. {quality}", StatusKind.Success);
            }
            catch (Exception ex)
            {
                LogError("GenerateTheme", ex);
                ShowStatus("Theme generation failed. See app_error.log for details.", StatusKind.Error);
            }
            finally
            {
                _isGeneratingTheme = false;
                if (triggerButton is not null) triggerButton.IsEnabled = true;
            }
        }

        private void ThemeSwatch_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not string id) return;
            ShowSwatchMenu(Core.Theming.ThemeCatalog.Get(id), fe);
            e.Handled = true;
        }

        private void ShowSwatchMenu(Core.Theming.ThemeDefinition theme, FrameworkElement anchor)
        {
            var menu = new ContextMenu();

            var apply = new MenuItem { Header = "Apply" };
            apply.Click += (_, _) => ApplyThemeById(theme.Id);
            menu.Items.Add(apply);

            var export = new MenuItem { Header = "Export..." };
            export.Click += async (_, _) => await ExportThemeAsync(theme);
            menu.Items.Add(export);

            menu.Items.Add(new Separator());

            if (theme.IsBuiltIn)
            {
                menu.Items.Add(new MenuItem { Header = "Built-in theme", IsEnabled = false });
            }
            else
            {
                var delete = new MenuItem { Header = $"Delete '{theme.Name}'" };
                delete.SetResourceReference(MenuItem.ForegroundProperty, "CustomErrorBrush");
                delete.Click += (_, _) => DeleteTheme(theme);
                menu.Items.Add(delete);
            }

            menu.PlacementTarget = anchor;
            menu.IsOpen = true;
        }

        private void DeleteTheme(Core.Theming.ThemeDefinition theme)
        {
            if (theme.IsBuiltIn) return;

            CommitPendingDeletions();

            var snapshot = Core.Theming.ThemeCatalog.Clone(theme);
            bool wasActive = string.Equals(theme.Id, App.ActiveThemeId, StringComparison.OrdinalIgnoreCase);

            if (wasActive)
                App.SetTheme(Core.Theming.ThemeCatalog.Default.Id, animate: true);

            if (!Core.Theming.ThemeCatalog.Delete(theme, deleteMedia: false))
            {
                ShowStatus($"Could not delete '{theme.Name}'.", StatusKind.Error);
                return;
            }

            LoopResolveCache.Clear();
            LoadThemeCatalog(ThemeFilter.Text);
            RefreshStats();

            ShowStatus(
                $"Deleted '{theme.Name}'.",
                StatusKind.Success,
                undo: () =>
                {
                    var restored = Core.Theming.ThemeCatalog.Save(snapshot, overwriteId: snapshot.Id);
                    LoopResolveCache.Clear();
                    if (wasActive) App.SetTheme(restored.Id, animate: true);
                    LoadThemeCatalog(ThemeFilter.Text);
                    RefreshStats();
                    ShowStatus($"Restored '{restored.Name}'.", StatusKind.Success);
                },
                onCommit: () => Core.Theming.ThemeCatalog.DeleteThemeMediaFiles(snapshot.Id));
        }

        private void ApplyThemeById(string id)
        {
            var theme = Core.Theming.ThemeCatalog.Get(id);
            string? loop = ResolveThemeLoop(theme);

            if (loop is not null)
            {
                _suppressMediaSliderAutoSave = true;
                try
                {
                    MediaFilePathInput.Text = loop;
                    if (MediaOpacitySlider.Value <= 0) MediaOpacitySlider.Value = 85;
                }
                finally
                {
                    _suppressMediaSliderAutoSave = false;
                }
            }

            App.SetTheme(theme.Id, animate: true, mediaPath: loop,
                         mediaOpacity: MediaOpacitySlider.Value / 100.0);
            ApplyMediaToOpenFences(theme, loop, MediaOpacitySlider.Value / 100.0);

            HighlightActiveSwatch();
            UpdateContrastReadout();
            RefreshStats();
            UpdateColorSourceCard();
            ShowStatus($"{theme.Name} applied.", StatusKind.Success);
            UpdateWindowBorderColor(theme);
        }

        private void OnThemeApplied(object? sender, Core.Theming.ThemeDefinition theme)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnThemeApplied(sender, theme)));
                return;
            }

            try
            {
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(this);
                UpdateThemeTabStyles();
                HighlightActiveSwatch();
                UpdateWindowBorderColor(theme);
            }
            catch (Exception ex) { LogError("OnThemeApplied", ex); }
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            UpdateWindowBorderColor();
        }

        private void UpdateWindowBorderColor(Core.Theming.ThemeDefinition? theme = null)
        {
            try
            {
                if (Environment.OSVersion.Version.Build < 22000) return;

                IntPtr hwnd = _windowHandle != IntPtr.Zero
                    ? _windowHandle
                    : new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                int colorRef;
                if (Wpf.Ui.Appearance.ApplicationAccentColorManager.IsAccentColorOnTitleBarsEnabled)
                {
                    Color accent = Wpf.Ui.Appearance.ApplicationAccentColorManager.SystemAccent;
                    theme ??= Core.Theming.ThemeEngine.Current;
                    if (theme != null && Core.Theming.ThemeEngine.TryParseColor(theme.Colors.Accent, out Color parsedAccent))
                    {
                        accent = parsedAccent;
                    }
                    colorRef = (accent.B << 16) | (accent.G << 8) | accent.R;
                }
                else
                {
                    colorRef = NativeMethods.DWMWA_COLOR_DEFAULT;
                }

                if (_lastAppliedBorderColorRef == colorRef) return;
                _lastAppliedBorderColorRef = colorRef;

                NativeMethods.DwmSetWindowAttribute(
                    hwnd,
                    (int)NativeMethods.DWMWINDOWATTRIBUTE.DWMWA_BORDER_COLOR,
                    ref colorRef,
                    sizeof(int));

                NativeMethods.SetWindowPos(
                    hwnd,
                    IntPtr.Zero,
                    0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
            }
            catch (Exception ex)
            {
                LogError("UpdateWindowBorderColor", ex);
            }
        }

        private void ThemeGallery_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter && e.Key != Key.Space) return;
            if (sender is not ListBox gallery || gallery.SelectedItem is not Core.Theming.ThemeDefinition theme) return;

            ApplyThemeById(theme.Id);
            e.Handled = true;
        }

        private void ThemeGallery_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled || PageScroller is null) return;
            e.Handled = true;
            var forwarded = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = sender
            };
            PageScroller.RaiseEvent(forwarded);
        }

        private void ThemeGallery_ContextMenuKey(object sender, KeyEventArgs e)
        {
            bool menuKey = e.Key == Key.Apps ||
                           (e.Key == Key.F10 && (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift);
            if (!menuKey) return;
            if (sender is not ListBox gallery || gallery.SelectedItem is not Core.Theming.ThemeDefinition theme) return;

            ShowSwatchMenu(theme, gallery);
            e.Handled = true;
        }

        private void ThemeFilter_TextChanged(object sender, TextChangedEventArgs e)
            => LoadThemeCatalog(ThemeFilter.Text);

        private static void UpdateContrastReadout()
        {
            try
            {
                var theme = Core.Theming.ThemeCatalog.Get(App.ActiveThemeId);
                var warnings = Core.Theming.ThemeEngine.AuditContrast(theme);

                if (warnings.Count > 0)
                {
                    Core.Theming.ThemeLog.Warn("Contrast",
                        $"{theme.Name}: {warnings.Count} token(s) below WCAG AA -> {string.Join("; ", warnings)}");
                }
            }
            catch (Exception ex) { LogError("UpdateContrastReadout", ex); }
        }

        private async void ImportTheme_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new OpenFileDialog
                {
                    Title = "Import theme",
                    Filter = "Fluid Fences themes (*.fftheme;*.json)|*.fftheme;*.json|All files (*.*)|*.*"
                };
                if (dlg.ShowDialog() != true) return;

                var imported = await Core.Theming.ThemeCatalog.ImportAsync(dlg.FileName);
                if (imported is null)
                {
                    string reason = !string.IsNullOrWhiteSpace(Core.Theming.ThemeCatalog.LastImportProblem)
                        ? Core.Theming.ThemeCatalog.LastImportProblem!
                        : "That file could not be read as a theme.";
                    ShowStatus(reason, StatusKind.Error);
                    return;
                }

                LoopResolveCache.Clear();
                LoadThemeCatalog(ThemeFilter.Text);
                RefreshStats();
                ShowStatus($"Imported '{imported.Name}'.", StatusKind.Success);
            }
            catch (Exception ex)
            {
                LogError("ImportTheme", ex);
                ShowStatus("Theme import failed. See app_error.log for details.", StatusKind.Error);
            }
        }

        private async void ExportTheme_Click(object sender, RoutedEventArgs e)
        {
            var theme = Core.Theming.ThemeCatalog.Get(App.ActiveThemeId);
            await ExportThemeAsync(theme);
        }

        private async Task ExportThemeAsync(Core.Theming.ThemeDefinition theme)
        {
            var dlg = new SaveFileDialog
            {
                Title = "Export theme",
                FileName = theme.Id + Core.Theming.ThemeCatalog.ThemePackageExtension,
                Filter = "Fluid Fences theme (*.fftheme)|*.fftheme"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                string? media = null;
                if (string.Equals(theme.Id, App.ActiveThemeId, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(MediaFilePathInput.Text)
                    && File.Exists(MediaFilePathInput.Text))
                {
                    media = MediaFilePathInput.Text;
                }
                else
                {
                    string? resolved = ResolveThemeLoop(theme);
                    if (!string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved))
                        media = resolved;
                }

                await Core.Theming.ThemeCatalog.ExportAsync(theme, dlg.FileName, media);
                ShowStatus($"Exported '{theme.Name}'.", StatusKind.Success);
            }
            catch (Exception ex)
            {
                LogError("ExportTheme", ex);
                ShowStatus("Export failed. See app_error.log for details.", StatusKind.Error);
            }
        }

        private static void LogError(string context, Exception ex)
        {
            App.LogError($"Settings_{context}", ex);
        }

        public void CommitPendingDeletions()
        {
            var commit = _pendingCommitAction;
            _pendingCommitAction = null;
            if (commit is null) return;

            try { commit.Invoke(); }
            catch (Exception ex) { LogError("CommitPendingDeletions", ex); }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            FlushPendingColor();
            SaveCurrentFenceSettings();
            if (_mediaOpacityTimer.IsEnabled)
            {
                _mediaOpacityTimer.Stop();
                _ = SaveGlobalSettingsCoreAsync(showSavedStatus: false);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            CommitPendingDeletions();

            _statusTimer.Stop();
            _liveUpdateTimer.Stop();
            _mediaOpacityTimer.Stop();

            try
            {
                if (HomeLogoAnim is not null)
                {
                    HomeLogoAnim.Stop();
                    HomeLogoAnim.Close();
                    HomeLogoAnim.Source = null;
                }
            }
            catch { }

            if (Application.Current is not null)
            {
                foreach (Window w in Application.Current.Windows)
                {
                    if (w is MainWindow fence && fence.GetIsRolledUp() && !fence.IsMouseOver)
                    {
                        fence.DismissTemporaryReveal();
                    }
                }
            }

            Core.Theming.ThemeEngine.ThemeApplied -= OnThemeApplied;
            if (_windowHandle != IntPtr.Zero)
                Core.Theming.WallpaperSampler.ClearExcludedWindowHandleIfMatches(_windowHandle);

            base.OnClosed(e);
        }

        private static Color HslToRgb(double h, double s, double l)
        {
            double r, g, b;
            if (s == 0) { r = g = b = l; }
            else
            {
                static double Hue2Rgb(double p, double q, double t)
                {
                    if (t < 0) t += 1;
                    if (t > 1) t -= 1;
                    if (t < 1 / 6.0) return p + (q - p) * 6 * t;
                    if (t < 1 / 2.0) return q;
                    if (t < 2 / 3.0) return p + (q - p) * (2 / 3.0 - t) * 6;
                    return p;
                }
                double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
                double p = 2 * l - q;
                h /= 360;
                r = Hue2Rgb(p, q, h + 1 / 3.0);
                g = Hue2Rgb(p, q, h);
                b = Hue2Rgb(p, q, h - 1 / 3.0);
            }
            return Color.FromRgb(
                (byte)Math.Round(Math.Clamp(r * 255.0, 0.0, 255.0)),
                (byte)Math.Round(Math.Clamp(g * 255.0, 0.0, 255.0)),
                (byte)Math.Round(Math.Clamp(b * 255.0, 0.0, 255.0)));
        }

        private async Task LoadGlobalSettingsAsync()
        {
            try
            {
                var config = await ConfigStore.ReadAsync();
                if (config is not null)
                {
                    TaskbarToggle.IsChecked = config.ShowTaskbarIcon;
                    RestoreFilesToggle.IsChecked = config.RestoreFilesOnDelete;
                    GhostModeToggle.IsChecked = config.EnableGhostMode;
                    PauseMediaToggle.IsChecked = config.PauseMediaOnRollup;

                    if (config.Theme != null)
                    {
                        _suppressMediaSliderAutoSave = true;
                        try
                        {
                            MediaFilePathInput.Text = config.Theme.BackgroundMediaPath;
                            MediaOpacitySlider.Value = config.Theme.MediaOpacity * 100.0;
                            AnimationDropdown.SelectedIndex = Math.Clamp((int)config.Theme.RollUpAnimation, 0, Math.Max(0, AnimationDropdown.Items.Count - 1));
                        }
                        finally
                        {
                            _suppressMediaSliderAutoSave = false;
                        }
                    }
                }
            }
            catch (Exception ex) { LogError("LoadConfig", ex); }

            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", false);
                bool enabled = key?.GetValue("FluidFences") is not null;
                StartupToggle.IsChecked = enabled;
                _lastSavedStartupEnabled = enabled;
            }
            catch (Exception ex) { LogError("ReadRegistry", ex); }

            ArmAutoSave();
        }

        private async Task SaveGlobalSettingsCoreAsync(bool showSavedStatus)
        {
            try
            {
                bool showTaskbar = TaskbarToggle.IsChecked ?? true;
                bool restoreFiles = RestoreFilesToggle.IsChecked ?? true;
                bool ghostMode = GhostModeToggle.IsChecked ?? false;
                bool pauseMedia = PauseMediaToggle.IsChecked ?? true;
                int animIdx = AnimationDropdown.SelectedIndex >= 0 ? AnimationDropdown.SelectedIndex : 0;

                var selectedTheme = Core.Theming.ThemeCatalog.Get(App.ActiveThemeId);

                Core.ThemeSettings newTheme = new()
                {
                    ThemeName = selectedTheme.Name,
                    RollUpAnimation = (Core.AnimationStyle)animIdx,
                    BackgroundMediaPath = MediaFilePathInput.Text,
                    MediaOpacity = MediaOpacitySlider.Value / 100.0,
                    BackgroundColor = selectedTheme.Colors.Background ?? "#01000000",
                    HeaderColor = selectedTheme.Colors.Header ?? "#22000000",
                    FontColor = selectedTheme.Colors.TextPrimary ?? "#FFFFFFFF",
                    SecondaryFontColor = selectedTheme.Colors.TextSecondary ?? "#FFAAAAAA",
                    AccentColor = selectedTheme.Colors.Accent ?? "#0078D7"
                };

                bool saved = await ConfigStore.UpdateAsync(c =>
                {
                    c.FirstRunComplete = true;
                    c.ShowTaskbarIcon = showTaskbar;
                    c.RestoreFilesOnDelete = restoreFiles;
                    c.EnableGhostMode = ghostMode;
                    c.PauseMediaOnRollup = pauseMedia;
                    c.ThemeId = selectedTheme.Id;
                    c.Theme = newTheme;
                });

                if (!saved)
                {
                    ShowStatus("Could not save settings to disk.", StatusKind.Error);
                    return;
                }

                App.PauseMediaOnRollup = pauseMedia;

                foreach (Window window in Application.Current.Windows)
                {
                    if (window is MainWindow fence && !fence.IsPendingDeletion)
                    {
                        fence.ShowInTaskbar = showTaskbar;
                        fence.UpdateGlobalGhostMode(ghostMode);
                        fence.ApplyTheme(newTheme);
                    }
                }

                bool wantStartup = StartupToggle.IsChecked == true;
                if (_lastSavedStartupEnabled != wantStartup)
                {
                    try
                    {
                        using RegistryKey? key = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);

                        if (wantStartup)
                        {
                            string exePath = Environment.ProcessPath!;
                            key?.SetValue("FluidFences", $"\"{exePath}\"");
                        }
                        else
                        {
                            key?.DeleteValue("FluidFences", false);
                        }
                        _lastSavedStartupEnabled = wantStartup;
                    }
                    catch (Exception ex)
                    {
                        LogError("WriteRegistry", ex);
                        ShowStatus("Could not change the Windows startup setting.", StatusKind.Warning);
                        return;
                    }
                }

                if (showSavedStatus)
                    ShowStatus("Saved.", StatusKind.Success);
            }
            catch (Exception ex)
            {
                LogError("SaveGlobalSettings", ex);
                ShowStatus("Could not save settings.", StatusKind.Error);
            }
        }

        private async void SaveLayout_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = await App.SaveCurrentLayoutAsync();
                if (result == SnapshotResult.Succeeded)
                    ShowStatus("Layout snapshot saved.", StatusKind.Success);
                else
                    ShowStatus("Could not save layout snapshot.", StatusKind.Error);
            }
            catch (Exception ex)
            {
                LogError("SaveLayout", ex);
                ShowStatus("Could not save layout snapshot.", StatusKind.Error);
            }
        }

        private async void RestoreLayout_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = await App.RestoreLayoutSnapshotAsync();
                switch (result)
                {
                    case SnapshotResult.Succeeded:
                        ShowStatus("Fences returned to the saved layout.", StatusKind.Success);
                        break;
                    case SnapshotResult.NotFound:
                        ShowStatus("No saved layout snapshot was found.", StatusKind.Warning);
                        break;
                    default:
                        ShowStatus("Could not restore layout snapshot.", StatusKind.Error);
                        break;
                }
            }
            catch (Exception ex)
            {
                LogError("RestoreLayout", ex);
                ShowStatus("Could not restore layout snapshot.", StatusKind.Error);
            }
        }

        private void LoadAllFences()
        {
            FenceListBox.Items.Clear();

            bool zenActive = Application.Current is App app && app.IsZenModeActive;
            foreach (Window window in Application.Current.Windows)
            {
                if (window is MainWindow fence &&
                    !fence.IsPendingDeletion &&
                    (fence.Visibility == Visibility.Visible || (zenActive && fence.Visibility == Visibility.Hidden)))
                {
                    FenceListItem item = new(fence);
                    FenceListBox.Items.Add(item);

                    if (fence == _callingFence || (_callingFence is null && FenceListBox.SelectedItem is null))
                        FenceListBox.SelectedItem = item;
                }
            }

            if (FenceListBox.Items.Count > 0 && FenceListBox.SelectedItem is null)
            {
                FenceListBox.SelectedIndex = 0;
            }

            bool any = FenceListBox.Items.Count > 0;
            if (!any)
            {
                _hasPendingColor = false;
                _pendingColorIsCustom = false;
                _currentlySelectedFence = null;
            }

            NoFencesHint.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            FenceDetailPanel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            RefreshStats();
        }

        private void FenceListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            FlushPendingColor();

            if (sender is ListView { SelectedItem: FenceListItem listItem })
            {
                MainWindow fence = listItem.Fence;

                _currentlySelectedFence = fence;
                _isLoadingFenceData = true;
                TitleInput.Text = fence.FenceTitle;
                AutoSortInput.Text = fence.AutoSortExtensions;
                EnableSearchToggle.IsChecked = fence.ShowSearch;
                PresetDropdown.SelectedIndex = 0;
                GhostModeDropdown.SelectedIndex = fence.GhostModeOverride;

                SortDropdown.SelectedItem = SortDropdown.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(i => string.Equals(i.Tag as string, fence.FenceSortMethod,
                                                       StringComparison.OrdinalIgnoreCase))
                    ?? SortDropdown.Items.OfType<ComboBoxItem>().FirstOrDefault();

                UpdateColorSlidersFromFence(fence);
                UpdateColorSourceCard();
                if (fence.GetIsRolledUp() && fence.Visibility == Visibility.Visible)
                {
                    fence.RevealTemporarily();
                }
                _isLoadingFenceData = false;
            }
        }

        private void UpdateColorSlidersFromFence(MainWindow fence)
        {
            _isColorInitializing = true;
            Color startColor = fence.FenceColor;
            AlphaSlider.Value = fence.FenceOpacity * 100.0;
            if (BlendWallpaperToggle is not null) BlendWallpaperToggle.IsChecked = (AlphaSlider.Value <= 2.0);
            if (AutoMatchToggle is not null) AutoMatchToggle.IsChecked = fence.AutoMatchColor;

            bool isAuto = fence.AutoMatchColor;
            HueSlider.IsEnabled = !isAuto;
            SaturationSlider.IsEnabled = !isAuto;
            LightnessSlider.IsEnabled = !isAuto;
            double opacity = isAuto ? 0.4 : 1.0;
            HueSlider.Opacity = opacity;
            SaturationSlider.Opacity = opacity;
            LightnessSlider.Opacity = opacity;

            System.Drawing.Color sysColor = System.Drawing.Color.FromArgb(255, startColor.R, startColor.G, startColor.B);
            HueSlider.Value = sysColor.GetHue();
            SaturationSlider.Value = sysColor.GetSaturation() * 100.0;
            LightnessSlider.Value = sysColor.GetBrightness() * 100.0;
            UpdateColorPreview();
            _isColorInitializing = false;
        }

        private void AutoMatchToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_isColorInitializing || _currentlySelectedFence is null) return;

            bool isAuto = AutoMatchToggle.IsChecked == true;
            _currentlySelectedFence.AutoMatchColor = isAuto;

            if (isAuto)
            {
                ThemeUtility.InvalidateWallpaperCache();
                if (_currentlySelectedFence.FenceOpacity <= 0.05)
                {
                    _currentlySelectedFence.SetFenceOpacity(0.75);
                }
            }

            _currentlySelectedFence.ColorSource = isAuto
                ? FenceColorSource.AutoMatch
                : FenceColorSource.Theme;

            HueSlider.IsEnabled = !isAuto;
            SaturationSlider.IsEnabled = !isAuto;
            LightnessSlider.IsEnabled = !isAuto;
            double opacity = isAuto ? 0.4 : 1.0;
            HueSlider.Opacity = opacity;
            SaturationSlider.Opacity = opacity;
            LightnessSlider.Opacity = opacity;

            UpdateColorSlidersFromFence(_currentlySelectedFence);
            UpdateColorSourceCard();
            _currentlySelectedFence.DashboardSaveAndRefresh();
        }

        private void UseThemeColor_Click(object sender, RoutedEventArgs e)
        {
            if (_currentlySelectedFence is null) return;

            _currentlySelectedFence.UseThemeColor();

            _isColorInitializing = true;
            AutoMatchToggle.IsChecked = false;
            HueSlider.IsEnabled = SaturationSlider.IsEnabled = LightnessSlider.IsEnabled = true;
            HueSlider.Opacity = SaturationSlider.Opacity = LightnessSlider.Opacity = 1.0;
            _isColorInitializing = false;

            UpdateColorSlidersFromFence(_currentlySelectedFence);
            UpdateColorSourceCard();
            ShowStatus("This fence follows the theme again.", StatusKind.Success);
        }

        private void UpdateColorSourceCard()
        {
            if (_currentlySelectedFence is null) return;

            switch (_currentlySelectedFence.ColorSource)
            {
                case FenceColorSource.Custom:
                    ColorSourceText.Text = "Colour is set on this fence";
                    ColorSourceHint.Text = "Themes will not change it while a custom colour is set.";
                    UseThemeColorBtn.Visibility = Visibility.Visible;
                    break;

                case FenceColorSource.AutoMatch:
                    ColorSourceText.Text = "Colour is matched to your wallpaper";
                    ColorSourceHint.Text = "Tracks the wallpaper's average colour and ignores the theme.";
                    UseThemeColorBtn.Visibility = Visibility.Visible;
                    break;

                default:
                    ColorSourceText.Text = "Colour is coming from the theme";
                    ColorSourceHint.Text = "Switching themes will restyle this fence.";
                    UseThemeColorBtn.Visibility = Visibility.Collapsed;
                    break;
            }
        }

        private void ColorSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isColorInitializing || _currentlySelectedFence is null) return;
            bool isOpacityOnly = (sender == AlphaSlider);
            if (isOpacityOnly && BlendWallpaperToggle is not null)
            {
                BlendWallpaperToggle.IsChecked = (AlphaSlider.Value <= 2.0);
            }
            if (!isOpacityOnly)
            {
                _pendingColorIsCustom = true;
            }
            UpdateColorPreview();
        }

        private void BlendWallpaperToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_isColorInitializing || _currentlySelectedFence is null) return;
            _isColorInitializing = true;
            double targetAlpha = BlendWallpaperToggle.IsChecked == true ? 1.0 : 75.0;
            AlphaSlider.Value = targetAlpha;
            UpdateColorPreview();
            _isColorInitializing = false;

            _currentlySelectedFence.SetFenceOpacity(targetAlpha / 100.0);
            UpdateColorSourceCard();
        }

        private void UpdateColorPreview()
        {
            double h = HueSlider.Value;
            double s = SaturationSlider.Value / 100.0;
            double l = LightnessSlider.Value / 100.0;
            double a = AlphaSlider.Value / 100.0;
            Color rgbColor = HslToRgb(h, s, l);
            Color finalColor = Color.FromArgb((byte)Math.Round(Math.Clamp(a * 255.0, 0.0, 255.0)), rgbColor.R, rgbColor.G, rgbColor.B);
            var previewBrush = new SolidColorBrush(finalColor);
            previewBrush.Freeze();
            ColorPreview.Background = previewBrush;

            Color pureHue = HslToRgb(h, 1.0, 0.5);
            BrightnessEndColor.Color = pureHue;
            AlphaEndColor.Color = finalColor with { A = 255 };

            SaturationStartColor.Color = HslToRgb(h, 0.0, l);
            SaturationEndColor.Color = HslToRgb(h, 1.0, l);

            if (!_isColorInitializing && _currentlySelectedFence is not null)
            {
                _pendingColor = finalColor;
                _pendingOpacity = a;
                _hasPendingColor = true;
                if (!_liveUpdateTimer.IsEnabled) _liveUpdateTimer.Start();
            }
        }

        private void PresetDropdown_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoadingFenceData || PresetDropdown.SelectedItem is null || AutoSortInput is null) return;
            string selection = ((ComboBoxItem)PresetDropdown.SelectedItem).Content.ToString() ?? "";
            bool matched = true;
            switch (selection)
            {
                case "Images & Photos": AutoSortInput.Text = ".jpg, .jpeg, .png, .gif, .bmp, .webp, .svg, .ico, .tif"; break;
                case "Documents": AutoSortInput.Text = ".doc, .docx, .pdf, .txt, .rtf, .odt, .xls, .xlsx, .csv, .ppt, .pptx"; break;
                case "Audio & Music": AutoSortInput.Text = ".mp3, .wav, .flac, .ogg, .m4a, .wma"; break;
                case "Videos": AutoSortInput.Text = ".mp4, .mkv, .avi, .mov, .wmv, .webm, .m4v"; break;
                case "Archives (.zip)": AutoSortInput.Text = ".zip, .rar, .7z, .tar, .gz"; break;
                case "Apps & Shortcuts": AutoSortInput.Text = ".lnk, .exe, .url, .bat, .msi"; break;
                default: matched = false; break;
            }
            if (matched) AutoSaveFence();
        }

        private void SaveCurrentFenceSettings()
        {
            if (_currentlySelectedFence is not null && !_currentlySelectedFence.IsPendingDeletion && !_isLoadingFenceData)
            {
                _currentlySelectedFence.FenceTitle = TitleInput.Text;
                _currentlySelectedFence.AutoSortExtensions = AutoSortInput.Text;
                _currentlySelectedFence.ShowSearch = EnableSearchToggle.IsChecked ?? true;
                _currentlySelectedFence.UpdateSearchVisibility();
                _currentlySelectedFence.GhostModeOverride = GhostModeDropdown.SelectedIndex;

                if (SortDropdown.SelectedItem is ComboBoxItem selectedSort && selectedSort.Tag is string sortKey)
                    _currentlySelectedFence.FenceSortMethod = sortKey;

                _currentlySelectedFence.DashboardSaveAndRefresh();

                if (FenceListBox.SelectedItem is FenceListItem item1)
                {
                    item1.Title = TitleInput.Text;
                    FenceListBox.Items.Refresh();
                }
            }
        }

        private void DeleteFenceBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_currentlySelectedFence is null) return;

            CommitPendingDeletions();

            var fence = _currentlySelectedFence;
            string title = fence.FenceTitle;
            Visibility priorVisibility = fence.Visibility;

            if (Owner == fence) Owner = null;

            _hasPendingColor = false;
            _pendingColorIsCustom = false;
            fence.IsPendingDeletion = true;
            fence.Visibility = Visibility.Collapsed;

            LoadAllFences();

            ShowStatus(
                $"Deleted '{title}'.",
                StatusKind.Success,
                undo: () =>
                {
                    fence.IsPendingDeletion = false;
                    bool zenActive = Application.Current is App app && app.IsZenModeActive;
                    fence.Visibility = zenActive ? Visibility.Hidden : priorVisibility;
                    LoadAllFences();
                    ShowStatus($"Restored '{title}'.", StatusKind.Success);
                },
                onCommit: () =>
                {
                    if (fence.IsPendingDeletion)
                    {
                        fence.DashboardDelete();
                    }
                });
        }

        private void VenmoQr_Click(object sender, RoutedEventArgs e)
        {
            const string venmoUrl = "https://account.venmo.com/u/davedebugs";
            try
            {
                Process.Start(new ProcessStartInfo(venmoUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                LogError("VenmoQr_Click", ex);
                ShowStatus("Could not open your browser.", StatusKind.Error);
            }
        }

        private void BuyMeACoffee_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://buymeacoffee.com/davedebugs") { UseShellExecute = true });
            }
            catch (Exception ex) { LogError("BuyMeACoffee_Click", ex); }
        }

        private void OpenGitHub_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://github.com/Dave-Debugs") { UseShellExecute = true });
            }
            catch (Exception ex) { LogError("OpenGitHub_Click", ex); }
        }

        private static void ApplyMediaToOpenFences(Core.Theming.ThemeDefinition theme, string? mediaPath, double opacity)
        {
            try
            {
                var legacy = new Core.ThemeSettings
                {
                    ThemeName = theme.Name,
                    BackgroundColor = theme.Colors.Background ?? "",
                    HeaderColor = theme.Colors.Header ?? "",
                    FontColor = theme.Colors.TextPrimary ?? "",
                    SecondaryFontColor = theme.Colors.TextSecondary ?? "",
                    AccentColor = theme.Colors.Accent ?? "",
                    BackgroundMediaPath = mediaPath ?? "",
                    MediaOpacity = opacity
                };

                foreach (Window w in Application.Current.Windows)
                {
                    if (w is MainWindow fence && !fence.IsPendingDeletion)
                    {
                        fence.ApplyTheme(legacy);
                        if (fence.GetIsRolledUp() && fence.Visibility == Visibility.Visible)
                        {
                            fence.RevealTemporarily();
                        }
                    }
                }
            }
            catch (Exception ex) { LogError("ApplyMediaToOpenFences", ex); }
        }

        private static string? ResolveThemeLoop(Core.Theming.ThemeDefinition theme)
        {
            string cacheKey = $"{theme.Id}|{theme.LoopFile ?? ""}";
            if (LoopResolveCache.TryGetValue(cacheKey, out string? cached))
            {
                if (cached is null || File.Exists(cached)) return cached;
                LoopResolveCache.TryRemove(cacheKey, out _);
            }

            string? resolved = ResolveThemeLoopCore(theme);
            if (LoopResolveCache.Count < 128)
                LoopResolveCache[cacheKey] = resolved;
            return resolved;
        }

        private static string? ResolveThemeLoopCore(Core.Theming.ThemeDefinition theme)
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string loopsDir = Path.Combine(baseDir, "Assets", "Loops");

                if (!string.IsNullOrWhiteSpace(theme.LoopFile))
                {
                    string safeLoopFile = Path.GetFileName(theme.LoopFile);
                    string userMediaPath = Path.Combine(Core.Theming.ThemeCatalog.UserThemeFolder, "media", safeLoopFile);
                    if (File.Exists(userMediaPath)) return userMediaPath;

                    string builtinLoopPath = Path.Combine(loopsDir, safeLoopFile);
                    if (File.Exists(builtinLoopPath)) return builtinLoopPath;
                }

                if (!Directory.Exists(loopsDir)) return null;

                foreach (string candidate in new[] { theme.Id, theme.Id.Replace('-', '_') })
                {
                    string path = Path.Combine(loopsDir, candidate + ".mp4");
                    if (File.Exists(path)) return path;
                }

                return BorrowNearestLoop(theme, loopsDir);
            }
            catch (Exception ex) { LogError("ResolveThemeLoop", ex); return null; }
        }

        private static string? BorrowNearestLoop(Core.Theming.ThemeDefinition theme, string loopFolder)
        {
            try
            {
                if (!Core.Theming.ThemeEngine.TryParseColor(theme.Colors.Background, out Color bg) ||
                    !Core.Theming.ThemeEngine.TryParseColor(theme.Colors.Accent, out Color accent))
                    return null;

                var bgLab = Core.Theming.ColorSpace.RgbToOklab(bg.R, bg.G, bg.B);
                var accLab = Core.Theming.ColorSpace.RgbToOklab(accent.R, accent.G, accent.B);

                string? best = null;
                double bestDistance = double.MaxValue;

                foreach (var candidate in Core.Theming.ThemeCatalog.Themes)
                {
                    if (!candidate.IsBuiltIn) continue;
                    if (string.Equals(candidate.Id, theme.Id, StringComparison.OrdinalIgnoreCase)) continue;

                    string path = Path.Combine(loopFolder, candidate.Id + ".mp4");
                    if (!File.Exists(path))
                    {
                        path = Path.Combine(loopFolder, candidate.Id.Replace('-', '_') + ".mp4");
                        if (!File.Exists(path)) continue;
                    }

                    if (!Core.Theming.ThemeEngine.TryParseColor(candidate.Colors.Background, out Color cbg) ||
                        !Core.Theming.ThemeEngine.TryParseColor(candidate.Colors.Accent, out Color cacc))
                        continue;

                    var cbgLab = Core.Theming.ColorSpace.RgbToOklab(cbg.R, cbg.G, cbg.B);
                    var caccLab = Core.Theming.ColorSpace.RgbToOklab(cacc.R, cacc.G, cacc.B);

                    double distance = OklabDistance(bgLab, cbgLab) * 2.0
                                    + OklabDistance(accLab, caccLab);

                    if (distance < bestDistance) { bestDistance = distance; best = path; }
                }

                if (best is not null)
                    Core.Theming.ThemeLog.Info("ResolveThemeLoop",
                        $"'{theme.Id}' ships no loop; borrowing {Path.GetFileName(best)} (distance {bestDistance:F3})");

                return best;
            }
            catch (Exception ex) { LogError("BorrowNearestLoop", ex); return null; }
        }

        private static double OklabDistance((double L, double a, double b) x, (double L, double a, double b) y)
        {
            double dL = x.L - y.L, da = x.a - y.a, db = x.b - y.b;
            return Math.Sqrt(dL * dL + da * da + db * db);
        }

        private uint? ResolveCurrentMonitorIndex()
        {
            try
            {
                uint count = Core.Theming.WallpaperSampler.GetMonitorCount();
                if (count <= 1) return null;

                IntPtr hwnd = _windowHandle != IntPtr.Zero
                    ? _windowHandle
                    : new System.Windows.Interop.WindowInteropHelper(this).Handle;
                var current = System.Windows.Forms.Screen.FromHandle(hwnd);

                var screens = System.Windows.Forms.Screen.AllScreens;
                for (uint i = 0; i < count && i < screens.Length; i++)
                {
                    if (screens[i].Bounds == current.Bounds) return i;
                }
                return null;
            }
            catch (Exception ex) { LogError("ResolveCurrentMonitorIndex", ex); return null; }
        }

        private Rect? ResolveCurrentMonitorBounds()
        {
            try
            {
                IntPtr hwnd = _windowHandle != IntPtr.Zero
                    ? _windowHandle
                    : new System.Windows.Interop.WindowInteropHelper(this).Handle;
                var b = System.Windows.Forms.Screen.FromHandle(hwnd).Bounds;
                return new Rect(b.X, b.Y, b.Width, b.Height);
            }
            catch (Exception ex) { LogError("ResolveCurrentMonitorBounds", ex); return null; }
        }

        private void BrowseMedia_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new()
            {
                Filter = "Media Files (*.mp4;*.gif)|*.mp4;*.gif|All Files (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                MediaFilePathInput.Text = dlg.FileName;
                ApplyMediaToOpenFences(
                    Core.Theming.ThemeCatalog.Get(App.ActiveThemeId),
                    string.IsNullOrWhiteSpace(MediaFilePathInput.Text) ? null : MediaFilePathInput.Text,
                    MediaOpacitySlider.Value / 100.0);
                _ = AutoSaveGlobalAsync();
            }
        }

        private void ClearMedia_Click(object sender, RoutedEventArgs e)
        {
            MediaFilePathInput.Text = string.Empty;
            ApplyMediaToOpenFences(
                Core.Theming.ThemeCatalog.Get(App.ActiveThemeId),
                null,
                MediaOpacitySlider.Value / 100.0);
            _ = AutoSaveGlobalAsync();
        }

        private void AskAQuestion_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("mailto:davedebugs@outlook.com?subject=Fluid%20Fences%20-%20Question") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                LogError("AskAQuestion_Click", ex);
                ShowStatus("Could not open your mail app.", StatusKind.Error);
            }
        }

        private void SuggestAnIdea_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("mailto:davedebugs@outlook.com?subject=Fluid%20Fences%20-%20Suggestion") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                LogError("SuggestAnIdea_Click", ex);
                ShowStatus("Could not open your mail app.", StatusKind.Error);
            }
        }

        private bool _autoSaveArmed;

        private void ArmAutoSave()
        {
            if (_autoSaveArmed) return;
            _autoSaveArmed = true;

            foreach (var toggle in new[] { StartupToggle, TaskbarToggle, RestoreFilesToggle,
                                           GhostModeToggle, PauseMediaToggle })
            {
                toggle.Checked   += (_, _) => _ = AutoSaveGlobalAsync();
                toggle.Unchecked += (_, _) => _ = AutoSaveGlobalAsync();
            }

            EnableSearchToggle.Checked   += (_, _) => AutoSaveFence();
            EnableSearchToggle.Unchecked += (_, _) => AutoSaveFence();
            SortDropdown.SelectionChanged      += (_, _) => AutoSaveFence();
            GhostModeDropdown.SelectionChanged += (_, _) => AutoSaveFence();
            AnimationDropdown.SelectionChanged += (_, _) => _ = AutoSaveGlobalAsync();

            TitleInput.LostFocus     += (_, _) => AutoSaveFence();
            AutoSortInput.LostFocus  += (_, _) => AutoSaveFence();

            MediaOpacitySlider.ValueChanged += (_, _) =>
            {
                if (!_autoSaveArmed || _isLoadingFenceData || _suppressMediaSliderAutoSave) return;
                ApplyMediaToOpenFences(
                    Core.Theming.ThemeCatalog.Get(App.ActiveThemeId),
                    string.IsNullOrWhiteSpace(MediaFilePathInput.Text) ? null : MediaFilePathInput.Text,
                    MediaOpacitySlider.Value / 100.0);
                _mediaOpacityTimer.Stop();
                _mediaOpacityTimer.Start();
            };
        }

        private async Task AutoSaveGlobalAsync()
        {
            if (!_autoSaveArmed || _isLoadingFenceData) return;
            await SaveGlobalSettingsCoreAsync(showSavedStatus: true);
        }

        private void AutoSaveFence()
        {
            if (!_autoSaveArmed || _isLoadingFenceData) return;
            SaveCurrentFenceSettings();
            ShowStatus("Saved.", StatusKind.Success);
        }

        private sealed record NavEntry(string Tag, string Label, Wpf.Ui.Controls.SymbolRegular Icon, string Keywords);

        private static readonly NavEntry[] NavEntries =
        [
            new("AboutGrid",       "Home",       Wpf.Ui.Controls.SymbolRegular.Home24,     "about version support donate coffee"),
            new("GeneralGrid",     "General",    Wpf.Ui.Controls.SymbolRegular.Settings24, "startup windows taskbar ghost mode delete restore layout snapshot media pause"),
            new("FencesGrid",      "Fences",     Wpf.Ui.Controls.SymbolRegular.Board24,    "fence name title sort ghost search auto organise colour color opacity hue background video delete"),
            new("ThemeEditorGrid", "Themes",     Wpf.Ui.Controls.SymbolRegular.Color24,    "theme colour color dark light contrast accessibility import export animation motion"),
            new("HowToGrid",       "How to use", Wpf.Ui.Controls.SymbolRegular.Question24, "help guide shortcuts keyboard zen portal tabs merge"),
            new("FeedbackGrid",    "Feedback",   Wpf.Ui.Controls.SymbolRegular.Mail24,     "feedback bug question suggestion contact"),
        ];

        private readonly Dictionary<string, System.Windows.Controls.Primitives.ToggleButton> _navItems = new();
        private string _currentTag = "AboutGrid";
        private bool _suppressNavChecked;

        private void BuildNavigation()
        {
            NavList.Children.Clear();
            _navItems.Clear();

            foreach (var entry in NavEntries)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new Wpf.Ui.Controls.SymbolIcon
                {
                    Symbol = entry.Icon,
                    FontSize = 16,
                    Margin = new Thickness(0, 0, 12, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
                row.Children.Add(new TextBlock
                {
                    Text = entry.Label,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 13
                });

                var button = new System.Windows.Controls.Primitives.ToggleButton
                {
                    Content = row,
                    Style = (Style)FindResource("NavButton"),
                    Tag = entry.Tag
                };
                System.Windows.Automation.AutomationProperties.SetName(button, entry.Label);
                System.Windows.Automation.AutomationProperties.SetAutomationId(button, $"Nav_{entry.Tag}");

                string tag = entry.Tag;

                button.Checked += (_, _) =>
                {
                    if (_suppressNavChecked) return;
                    SwitchToTab(tag);
                };

                button.Unchecked += (sender, _) =>
                {
                    if (_suppressNavChecked) return;
                    if (string.Equals(_currentTag, tag, StringComparison.Ordinal) &&
                        sender is System.Windows.Controls.Primitives.ToggleButton tb)
                    {
                        tb.IsChecked = true;
                    }
                };

                _navItems[tag] = button;
                NavList.Children.Add(button);
            }
        }

        private void SettingsSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            string q = SettingsSearch.Text?.Trim() ?? "";

            if (q.Length == 0)
            {
                foreach (var kv in _navItems) kv.Value.Visibility = Visibility.Visible;
                return;
            }

            var matches = new List<string>();
            foreach (var entry in NavEntries)
            {
                bool hit = entry.Label.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || entry.Keywords.Contains(q, StringComparison.OrdinalIgnoreCase);
                _navItems[entry.Tag].Visibility = hit ? Visibility.Visible : Visibility.Collapsed;
                if (hit) matches.Add(entry.Tag);
            }

            if (matches.Count > 0 && !matches.Contains(_currentTag))
                SwitchToTab(matches[0]);
        }

        public void SwitchToTab(string tag)
        {
            foreach (var kv in _pages)
                if (kv.Value is not null)
                    kv.Value.Visibility = kv.Key == tag ? Visibility.Visible : Visibility.Collapsed;

            _currentTag = tag;

            try
            {
                if (tag == "AboutGrid")
                {
                    if (IsLoaded) StartHomeLogoAnimation();
                }
                else if (HomeLogoAnim?.Source is not null)
                {
                    HomeLogoAnim.Pause();
                }
            }
            catch { }

            _suppressNavChecked = true;
            try { foreach (var kv in _navItems) kv.Value.IsChecked = kv.Key == tag; }
            finally { _suppressNavChecked = false; }

            PageScroller?.ScrollToTop();

            var duration = Core.Theming.ThemeEngine.ScaleDuration(150);
            if (duration == TimeSpan.Zero || PageHost is null) return;

            PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));

            if (PageHost.RenderTransform is TranslateTransform tt)
                tt.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(8, 0, duration) { EasingFunction = TabEaseOut });
        }

        private enum StatusKind { Success, Info, Warning, Error }

        private Action? _undoAction;
        private readonly DispatcherTimer _statusTimer = new();

        private void ShowStatus(string message, StatusKind kind = StatusKind.Info, Action? undo = null, Action? onCommit = null)
        {
            try
            {
                if (_pendingCommitAction is not null && onCommit is null)
                {
                    CommitPendingDeletions();
                }

                StatusText.Text = message;
                _undoAction = undo;
                _pendingCommitAction = onCommit;
                StatusUndo.Visibility = undo is null ? Visibility.Collapsed : Visibility.Visible;

                string brushKey;
                switch (kind)
                {
                    case StatusKind.Success:
                        StatusIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24;
                        brushKey = "CustomSuccessBrush"; break;
                    case StatusKind.Warning:
                        StatusIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Warning24;
                        brushKey = "CustomWarningBrush"; break;
                    case StatusKind.Error:
                        StatusIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.ErrorCircle24;
                        brushKey = "CustomErrorBrush"; break;
                    default:
                        StatusIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Info24;
                        brushKey = "CustomInfoBrush"; break;
                }
                StatusIcon.SetResourceReference(ForegroundProperty, brushKey);

                StatusStrip.Visibility = Visibility.Visible;

                _statusTimer.Stop();
                if (kind != StatusKind.Error)
                {
                    _statusTimer.Interval = TimeSpan.FromSeconds(undo is null ? 4 : 7);
                    _statusTimer.Start();
                }
            }
            catch (Exception ex) { LogError("ShowStatus", ex); }
        }

        private void HideStatus(bool commitPending = true)
        {
            _statusTimer.Stop();
            _undoAction = null;
            if (commitPending) CommitPendingDeletions();
            else _pendingCommitAction = null;
            StatusStrip.Visibility = Visibility.Collapsed;
            StatusUndo.Visibility = Visibility.Collapsed;
        }

        private void StatusDismiss_Click(object sender, RoutedEventArgs e) => HideStatus(commitPending: true);

        private void StatusUndo_Click(object sender, RoutedEventArgs e)
        {
            var action = _undoAction;
            HideStatus(commitPending: false);
            if (action is null)
            {
                ShowStatus("There is nothing to undo.", StatusKind.Warning);
                return;
            }

            try { action.Invoke(); }
            catch (Exception ex)
            {
                LogError("Undo", ex);
                ShowStatus($"Could not undo that: {ex.Message}", StatusKind.Error);
            }
        }

        private void RefreshStats()
        {
            try
            {
                bool zenActive = Application.Current is App app && app.IsZenModeActive;
                int fences = Application.Current.Windows.OfType<MainWindow>()
                                .Count(f => !f.IsPendingDeletion &&
                                            (f.Visibility == Visibility.Visible || (zenActive && f.Visibility == Visibility.Hidden)));
                StatFenceCount.Text = fences.ToString(System.Globalization.CultureInfo.CurrentCulture);
                StatThemeCount.Text = Core.Theming.ThemeCatalog.Themes.Count
                                          .ToString(System.Globalization.CultureInfo.CurrentCulture);
                StatActiveTheme.Text = Core.Theming.ThemeCatalog.Get(App.ActiveThemeId).Name;

                var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                VersionText.Text = v is null ? "" : $"Version {v.Major}.{v.Minor}.{v.Build}";
            }
            catch (Exception ex) { LogError("RefreshStats", ex); }
        }

        private void BuildHowTo()
        {
            static void Row(Panel host, string left, string right)
            {
                var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var keyText = new TextBlock { Text = left, FontSize = 12, FontWeight = FontWeights.SemiBold };
                var key = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 3, 8, 3),
                    Margin = new Thickness(0, 0, 12, 0),
                    BorderThickness = new Thickness(1),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Child = keyText
                };
                key.SetResourceReference(Border.BackgroundProperty, "ControlFillColorSecondaryBrush");
                key.SetResourceReference(Border.BorderBrushProperty, "ControlElevationBorderBrush");
                Grid.SetColumn(key, 0);

                var desc = new TextBlock { Text = right, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
                desc.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                Grid.SetColumn(desc, 1);

                grid.Children.Add(key);
                grid.Children.Add(desc);
                host.Children.Add(grid);
            }

            ShortcutList.Children.Clear();
            Row(ShortcutList, "Ctrl + Alt + Z", "Zen Mode: hide every fence, then bring them all back.");
            Row(ShortcutList, "Ctrl + F",       "Search inside the focused fence.");
            Row(ShortcutList, "Ctrl + T",       "New tab in the focused fence.");
            Row(ShortcutList, "F10",            "Open the fence menu without reaching for the mouse.");
            Row(ShortcutList, "Ctrl + Arrows",  "Snap the fence to an edge of the current screen.");
            Row(ShortcutList, "Delete",         "Remove the selected items from the fence.");
            Row(ShortcutList, "Double-click",   "Roll the fence up into its title bar, or open it again.");

            BasicsList.Children.Clear();
            Row(BasicsList, "Create",   "Right-click the tray icon for an empty fence, or a Folder Portal that mirrors a real folder.");
            Row(BasicsList, "Fill",     "Drag files, folders or shortcuts straight in from anywhere.");
            Row(BasicsList, "Move",     "Drag the header. Fences snap to screen edges and follow you across monitors.");
            Row(BasicsList, "Resize",   "Drag any edge or corner.");
            Row(BasicsList, "Organise", "Set file extensions under Fences to sweep matching files off the Desktop automatically.");

            TabsList.Children.Clear();
            Row(TabsList, "New tab",  "Use the + button in the tab strip, or Ctrl+T.");
            Row(TabsList, "Reorder",  "Drag tabs left and right.");
            Row(TabsList, "Tear off", "Drag a tab onto the desktop and it becomes its own fence.");
            Row(TabsList, "Merge",    "Drop one fence's header onto another to combine them.");
        }

        private void StartHomeLogoAnimation()
        {
            try
            {
                if (HomeLogoAnim is null || Core.Theming.ThemeEngine.IsReducedMotion()) return;

                if (HomeLogoAnim.Source is null)
                {
                    string path = Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory, "Assets", "morph_icon.mp4");
                    if (!File.Exists(path)) return;
                    HomeLogoAnim.Source = new Uri(path, UriKind.Absolute);
                }

                HomeLogoAnim.Play();
            }
            catch (Exception ex) { LogError("StartHomeLogoAnimation", ex); }
        }

        private void HomeLogoAnim_Ended(object sender, RoutedEventArgs e)
        {
            if (_currentTag != "AboutGrid" || Core.Theming.ThemeEngine.IsReducedMotion()) return;
            try { HomeLogoAnim.Position = TimeSpan.Zero; HomeLogoAnim.Play(); }
            catch (Exception ex) { LogError("HomeLogoAnim_Ended", ex); }
        }

        private int _versionClicks;
        private DateTime _lastVersionClick = DateTime.MinValue;

        private void WireVersionEasterEgg()
        {
            if (VersionText is null) return;
            VersionText.Background = Brushes.Transparent;
            VersionText.MouseLeftButtonDown += VersionText_Clicked;
        }

        private void VersionText_Clicked(object sender, MouseButtonEventArgs e)
        {
            var now = DateTime.UtcNow;

            _versionClicks = (now - _lastVersionClick).TotalSeconds <= 2.0 ? _versionClicks + 1 : 1;
            _lastVersionClick = now;

            if (_versionClicks >= 5)
            {
                _versionClicks = 0;
                try
                {
                    var egg = new RabbitEasterEgg { Owner = this };
                    egg.Show();
                }
                catch (Exception ex) { LogError("RabbitEasterEgg", ex); }
            }
        }
    }
}
