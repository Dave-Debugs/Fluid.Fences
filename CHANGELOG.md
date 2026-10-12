# Changelog

All notable changes to Fluid Fences are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.3.5] - 2026-10-12

### Added
- Smart Multi-Fence Auto-Organize: automatically sorts desktop files into separate fences by file type (`Shortcuts & Apps`, `Documents`, `Images`, `Media`, `Archives`, `Code & Scripts`, and `Other Files`) while honoring custom per-fence extension rules, accessible from both the fence menu and the system tray icon.
- Direct GitHub (`Dave-Debugs`) button on the Settings Home page (`https://github.com/Dave-Debugs`).
- xUnit test suite (`DesktopFences.Tests`) covering OKLCH color space math, WCAG contrast ratios, deterministic K-means palette extraction, theme generation, path-traversal protection, zip-bomb defense, and concurrent `ConfigStore` updates.
- Full UI Automation (`AutomationId` and `Name`) coverage across fences, tabs, icons, and Settings navigation for automated E2E testing.

### Changed
- Migrated repository, package metadata, built-in theme author credits, installer URLs, and auto-updater endpoints to `Dave-Debugs`.
- Hardened `ThemeCatalog` import/save path validation, streaming `.fftheme` decompression limits, and `ThemeEngine` custom token allowlists.
- Synchronized STA `WScript.Shell` COM access, fixed `Window_Loaded` HWND recreation ordering, cancelled stale Folder Portal watchers on tab switches, and converted `ConfigStore` and `WallpaperSampler` to true async I/O.

## [2.2.0] - 2026-09-11

### Added
- Complete catalog of 30 modern flagship themes categorized across Dark, Light, Warm, Pastel, and Vibrant palettes.
- Categorized Themes dashboard featuring a dedicated Dark / Light mode selector bar and section header bars.
- Dual-mode procedural motion engine delivering seamless 30fps H.264 video loops with watercolor alpha compositing on light canvases and additive glow on dark canvases.
- Guaranteed 100% WCAG AAA multi-surface readability standards across light, gray, and dark desktop backdrops.
- Persistent auto-sorting when files are dropped into a fence: dropping new files now preserves and applies the fence's active sort method (`Name`, `Date Modified`, `Size`, `Type`).
- Active visual checkmarks on the selected sort mode under `View -> Sort By` and icon size under `View -> Icon Size` in fence context menus.
- Graceful IPC application shutdown mechanism via named event `Local\FluidFences.Exit` and `--exit` command-line switch, coordinated with Inno Setup installer.
- Security confirmation dialog before executing scripts or executables (.exe, .msi, .bat, .cmd, .ps1, etc.) from Folder Portals.
- Architecture-specific 32-bit and 64-bit safe `SHFILEOPSTRUCT` Win32 definitions.
- Automated backup creation (`.corrupt.bak`) upon encountering malformed or corrupted configuration files.
- Media file extension whitelist (`.mp4`, `.webm`, `.jpg`, `.jpeg`, `.png`, `.gif`, `.bmp`, `.webp`) for imported themes.
- Direct donation link and verified high-error-correction QR code for Venmo (@Davedebugs).

### Changed
- Converted Per-Monitor DPI coordinate transforms using `PointFromScreen` in `ClampToScreen` and Ctrl+Arrow snapping, eliminating off-screen shifting on mixed-DPI secondary monitors.
- Cached screen working areas in `WndProc` during `WM_WINDOWPOSCHANGING` to eliminate continuous `EnumDisplayMonitors` allocation storms during fence drags.
- Added thread-safe 2.0-second cache for wallpaper dominant color sampling with explicit display settings invalidation.
- Removed unbuffered `FirstChanceException` file logging on `debug_exceptions.log` that caused disk lock contention storms.
- Replaced unmanaged heap memory allocation in window acrylic blur effects with stack-allocated structs, eliminating memory leak hazards.
- Converted layout snapshots and disk persistence routines to use unique GUID temporary files, preventing write collisions.
- Enhanced Updater checksum verification to support multi-asset GNU and BSD style manifests, with asynchronous download streaming.

### Fixed
- Fixed bloated translucent acrylic border/frame around fence context menus caused by unnecessary Win32 HWND blur hooks.
- Fixed fence item rename failures and reverts caused by orphaned storage shortcut collisions and case-only rename conflicts.
- Fixed item selection and focus loss when renaming fence items.
- Fixed WIC native image loader errors in `error.log` by removing `.ico` from image extensions, letting shell extractors natively handle multi-frame icons.
- Guarded against remote UNC SMB paths in `.url` shortcut `IconFile` extraction to prevent potential NTLM hash leaks.
- Fixed directory rename bug where dots in directory names were stripped as file extensions.
- Fixed Folder Portals to enumerate both files and subdirectories (`GetFileSystemEntries`).
- Fixed `SetMergeHighlight` mouse position bounds test using native `this.IsMouseOver`.
- Fixed a potential deadlock in `FileSystemWatcher.Error` by dispatching asynchronously to the UI thread.
- Fixed an issue where `OnClosing` could abort Windows shutdown or restart sequences.
- Fixed thumbnail generation failure caused by improper COM wrapper release on `.NET 8` source-generated interfaces.
- Fixed cross-thread UI collection mutation in `ThemeCatalog.SaveAsync`.
- Removed modification of undocumented system Explorer registry keys (`Explorer\Serialize`).
- Hardened file renaming logic against path traversal attempts.

## [2.0.5] - 2026-08-12

### Added
- Automated CI/CD pipeline to build and publish the FluidFences NuGet package.

### Changed
- Rebuilt the Settings window list selection logic using a custom data class to correctly render the native ModernWpf/Wpf.Ui container, restoring hover effects and full-width click hitboxes.

### Fixed
- Fixed an issue where clicking on a rolled-up fence after disconnecting a multi-monitor display could cause the window to instantly teleport off-screen and disappear.

## [2.0.0] - 2026-07-23

The biggest release since the project started. The theming layer was rebuilt from
scratch, the app has a new look, and installing and updating are now handled by a
proper Windows installer. Installs in place over 1.3.x and keeps your fences, tabs,
and settings.

### Added
- New theming engine. Themes are generated from a shared set of design tokens rather
  than fixed color values, so a preset can be refined later and existing users pick up
  the improvement instead of being frozen on their first selection.
- Animated backgrounds on every theme.
- Auto-Match to wallpaper. Samples the current desktop wallpaper, builds a palette from
  it, and tints your fences to match. Fences follow when the wallpaper changes.
- New application icon and refreshed settings dashboard, with an animated logo on the
  Home page.
- Windows installer. Upgrades an existing 1.3.x install in place, offers to start with
  Windows, and asks before removing saved data on uninstall.

### Changed
- The app now ships as a self contained build, so no separate .NET runtime install is
  required.
- Auto-updater rewritten. It downloads the signed installer, verifies it with a SHA-256
  checksum before running anything, installs silently, and relaunches.
- The reported version is read directly from the build so it cannot drift out of sync.

### Fixed
- Wallpaper sampling on multi-monitor systems and when displays are changed at runtime.
- Hardened Folder Portals and the underlying file operations against edge cases.
- The app recovers cleanly from a corrupt or truncated config file instead of failing
  to start.
- The start-with-Windows entry is repointed correctly after an upgrade or a change of
  install location.
- General stability and security pass across the codebase, plus removal of dead code.

## [1.3.6]

- Hotfix: corrected auto-updating for installations under Program Files.

## [1.3.0]

- Added tabs, the in-app updater, and a range of UI enhancements.

## [1.2.5]

- Stability, security, and MVVM refactor. Async file I/O and modular native interop.
