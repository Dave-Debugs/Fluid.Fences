# Fluid Fences

A fast, modern, open-source desktop organization application for Windows. Group your shortcuts, files, and folders into clean, translucent containers right on your desktop, and keep your workspace effortlessly organized.

Built natively with **C#**, **WPF**, and **.NET 8** for Windows 10 and 11.

![Version](https://img.shields.io/badge/version-2.2.0-blue.svg)
![License](https://img.shields.io/badge/license-GPLv3-blue.svg)
![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011%20(64--bit)-0078D6)
![WCAG AAA](https://img.shields.io/badge/accessibility-100%25%20WCAG%20AAA-brightgreen.svg)

---

## What's New in v2.2.0

- **30 Flagship Modern Themes**: An expansive catalog of 30 handcrafted themes spanning Dark, Light, Warm, Pastel, and Vibrant aesthetics.
- **Categorized Themes Dashboard**: Distinct **Dark Themes** and **Light Themes** sections with a dedicated mode selector bar (`All`, `Dark`, `Light`) for seamless browsing.
- **Dual-Mode Procedural Motion Loops**: Every theme features a bespoke 30fps H.264 procedural video loop. Light canvases use smooth watercolor alpha compositing, while dark canvases feature emissive additive glow.
- **100% WCAG AAA Certified Readability**: Multi-surface contrast algorithms ensure ultra-crisp typography (up to 19:1 contrast) with automatic label halos that stay legible across light, dark, and animated wallpapers.
- **Direct Donation & Verified QR Integration**: One-click donation via Venmo and Buy Me a Coffee directly in the dashboard.

---

## Features

### Desktop Organization
- **Tabbed Fences**: Group related projects or categories into multiple tabs within a single fence window.
- **Tear-Off & Merge**: Drag any tab onto the desktop to spawn a new fence, or drop one fence header onto another to merge them.
- **Folder Portals**: Mirror any folder from your drive directly onto the desktop. Changes synchronize bi-directionally in real time.
- **Auto-Organize**: Define file extension rules to automatically sweep new desktop files into designated fences.
- **Layout Snapshots**: Save and restore your complete multi-monitor layout at any time.

### Theming & Visual Experience
- **30 Built-in Themes**:
  - **Dark & Cyber (17)**: *Obsidian Prism, Solaris Flare, Neo Shinjuku, Synth Matrix, Cosmic Void, Kyoto Bamboo, Velvet Gold, Sunset Boulevard, Abyssal Trench, Supernova Core, Prismatic Aurora, Liquid Chrome, Solar Eclipse, Cyber Lotus, Hex Void, Molten Obsidian.*
  - **Light, Warm & Pastel (13)**: *Matcha Latte, Nordic Daylight, Peach Blossom, Lavender Mist, Desert Sandstone, Oceanic Breeze, Sunlit Citrus, Sakura Studio, Cobalt Architectural, Earthy Terracotta, Ethereal Frost, Paper Minimal, Rose Quartz, Nordic Mirage.*
- **Auto-Match Wallpaper Sampling**: Automatically extracts dominant and accent colors from your desktop wallpaper and creates a custom matching theme.
- **Motion Video Loops**: 12 mathematical pattern families (Quantum Nebula, Vortex Swirl, Frost Caustic, Kaleido Crystal, Thermal Magma, Retro Grid, etc.) with adjustable opacity slider.
- **Ghost Mode**: Fences stay completely translucent until hovered over.
- **Window Roll-Up**: Double-click any fence header to roll it up into a slim title bar.

### Navigation & Shortcuts
- **Zen Mode (`Ctrl + Alt + Z` or `Ctrl + Shift + Z`)**: Instantly hide all fences for an unobstructed view of your desktop, and press again to reveal them.
- **Quick Search**: Filter files and shortcuts instantly inside any fence via the built-in search bar.
- **Multi-Property Sorting**: Sort items by Name, Date Modified, Size, or Type with persistent sorting on drag-and-drop.
- **Per-Monitor DPI Awareness**: Crisp rendering and smooth snapping across multi-monitor setups with mixed scaling factors.

---

## How to Use Fluid Fences

Fluid Fences runs quietly in your Windows system tray (near the clock).

### 1. Creating and Customizing Fences
- **Create a New Fence**: Right-click the Fluid Fences tray icon and select **"Create New Fence"**.
- **Move & Resize**: Click and drag the fence header to move it anywhere. Drag any edge or corner to resize.
- **Add Items**: Drag and drop shortcuts, documents, folders, or files directly into the fence.
- **Rename a Fence**: Double-click the header title text, type the new name, and press Enter.

### 2. Tabs, Merging & Portals
- **Add a Tab**: Right-click a fence title bar and select **"New Tab Inside This Fence"**, or click the **`+`** button.
- **Reorder Tabs**: Click and drag a tab left or right along the header bar.
- **Tear Off a Tab**: Drag a tab out of the fence and release it onto the empty desktop to create an independent fence.
- **Merge Fences**: Drag the header of one fence directly over another fence until the merge highlight appears, then release.
- **Folder Portals**: Right-click the system tray icon and select **"Create New Folder Portal"**, then choose any local directory to mirror.

### 3. Themes & Motion Settings
1. Double-click the Fluid Fences tray icon (or right-click and select **"Settings"**) to open the dashboard.
2. Click the **Themes** tab in the sidebar.
3. Use the mode bar at the top to filter by:
   - **All Themes**: Browse the full 30-theme catalog separated by Dark and Light section bars.
   - **Dark Themes**: View only the 17 dark, cyber, and deep space themes.
   - **Light Themes**: View only the 13 light, pastel, warm, and vibrant themes.
4. Click any theme swatch to apply it immediately to all open fences.
5. Use the **Loop Opacity** slider to dial in your preferred video motion intensity.

### 4. Useful Shortcuts
- **Hide / Show All Fences (Zen Mode)**: `Ctrl + Alt + Z` (or `Ctrl + Shift + Z`)
- **Roll Up / Unroll Fence**: Double-click the fence header
- **Move Snapping**: Hold `Ctrl` while moving a fence with arrow keys for precision alignment

---

## Installation

Download the latest release from the [Releases page](https://github.com/DaveDebugs/Fluid.Fences/releases), run the installer, and launch Fluid Fences.

- Self-contained build: No separate .NET runtime installation required.
- In-place updates: Preserves your existing fences, tabs, and custom layouts.
- System requirements: Windows 10 or 11 (64-bit).

---

## Building from Source

Fluid Fences can be compiled using **Visual Studio 2026** (or 2022) with the .NET 8 SDK:

```powershell
# Clone the repository
git clone https://github.com/DaveDebugs/Fluid.Fences.git

# Build the Debug configuration
dotnet build DesktopFences/DesktopFences.csproj -c Debug

# Build the Release configuration
dotnet build DesktopFences/DesktopFences.csproj -c Release
```

The output executable will be generated in `P:\Programming Repositories\Compiled\Nuget\Debug\net8.0-windows\FluidFences.exe` (or local `bin/`).

---

## Support & Contributing

Bug reports, suggestions, and pull requests are warmly welcomed on the [GitHub Repository](https://github.com/DaveDebugs/Fluid.Fences).

If Fluid Fences enhances your daily workflow and you'd like to support ongoing development:
- **Buy Me a Coffee**: [@Davedebugs](https://buymeacoffee.com/davedebugs)
- **Venmo**: [@Davedebugs](https://account.venmo.com/u/davedebugs)

---

## License

Fluid Fences is free and open-source software licensed under the **GNU General Public License v3.0**. See the [LICENSE](LICENSE) file for complete details.

© 2026 DaveDebugs (David Daniel)
