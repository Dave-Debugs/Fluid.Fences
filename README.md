# Fluid Fences

**Keep your Windows desktop clean, organized, and beautiful.**

**Fluid Fences** is a fast, lightweight, open-source desktop organizer for Windows 10 and 11. It lets you group your desktop shortcuts, files, and folders into sleek, translucent glass containers ("fences") that stay right on your wallpaper.

![Version](https://img.shields.io/badge/version-2.3.5-blue.svg)
![License](https://img.shields.io/badge/license-GPLv3-blue.svg)
![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011%20(64--bit)-0078D6)
![WCAG AAA](https://img.shields.io/badge/accessibility-100%25%20WCAG%20AAA-brightgreen.svg)

---

## What's New in v2.3.5

- **Smart Multi-Fence Auto-Organize**: One click sweeps your cluttered desktop and automatically sorts your files into separate fences by file type (**Shortcuts & Apps**, **Documents**, **Images**, **Media**, **Archives**, **Code & Scripts**, and **Other Files**) while respecting any custom extension rules you've set.
- **30 Built-In Animated & Glass Themes**: Switch instantly between **17 Dark & Cyber** themes and **13 Light, Warm & Pastel** themes, each paired with a smooth 30fps looping background animation.
- **Generate a Theme from Your Wallpaper**: Sample your current desktop wallpaper (even animated wallpapers) with one click to build a custom, contrast-checked theme that matches your setup.
- **Faster, Safer & More Reliable**: Upgraded file sorting performance, smoother multi-monitor DPI handling, stronger security checks for Folder Portals and theme packages, and moved to the official **[Dave-Debugs](https://github.com/Dave-Debugs/Fluid.Fences)** GitHub home.

---

## Key Features at a Glance

| Feature | What It Does |
| :--- | :--- |
| **Smart Auto-Organize** | Pulls loose files off your desktop and neatly sorts them into separate fences based on file type (or your own custom file extension rules). |
| **Tabbed Fences** | Add multiple tabs to a single fence to group projects together. Drag a tab out to turn it into its own fence, or drop one fence onto another to merge them. |
| **Folder Portals** | Mirror any folder on your PC (like *Downloads* or *Projects*) directly on your desktop without moving the original files. |
| **30 Themes + Motion Loops** | Pick from 30 handcrafted dark and light themes with optional animated background loops and adjustable opacity. |
| **Auto-Match Wallpaper** | Automatically tints your fences to match your desktop wallpaper—and updates automatically whenever your wallpaper changes. |
| **Ghost Mode & Roll-Up** | Keep fences invisible until you hover over them (**Ghost Mode**), or double-click any fence's title bar to roll it up into a compact strip. |
| **Zen Mode (`Ctrl+Alt+Z`)** | Instantly hide or show all fences on your desktop with a single keyboard shortcut. |
| **Layout Snapshots** | Save your exact fence positions and sizes across all monitors and restore them anytime with one click. |

---

## Quick Start Guide

Fluid Fences lives quietly in your **Windows System Tray** (bottom-right corner near the clock).

### 1. Organize Your Desktop in One Click
- Right-click the **Fluid Fences tray icon** (or click the **menu button** on any fence) and select **Auto-Organize Desktop**.
- Fluid Fences will automatically group your desktop items into clean, separate fences by category:
  - **Shortcuts & Apps** (`.lnk`, `.url`, `.exe`, `.msi`)
  - **Documents** (`.pdf`, `.docx`, `.xlsx`, `.pptx`, `.txt`, `.csv`, `.md`, etc.)
  - **Images** (`.png`, `.jpg`, `.gif`, `.webp`, `.svg`, `.psd`, etc.)
  - **Media** (`.mp4`, `.mkv`, `.mov`, `.mp3`, `.wav`, `.flac`, etc.)
  - **Archives** (`.zip`, `.rar`, `.7z`, `.tar`, `.gz`, `.iso`, etc.)
  - **Code & Scripts** (`.cs`, `.py`, `.js`, `.ts`, `.html`, `.json`, `.ps1`, etc.)
  - **Other Files** (anything else)
- Want custom rules? Open **Settings -> Customize Fence** and type any extensions you want that specific fence to collect (e.g., `.blend, .fbx`).

### 2. Everyday Fence Controls
- **Create a Fence**: Right-click the tray icon -> **Create New Fence** (or use a fence's `Menu -> Create New` option).
- **Add Files**: Drag and drop any file, folder, or shortcut straight into a fence.
- **Rename Anything**:
  - **Fence Title**: Double-click the fence header text (or right-click -> **Rename Fence**).
  - **Tab Title**: Double-click the tab name.
  - **Item Name**: Single-click an icon's text label (or right-click the icon -> **Rename**).
- **Roll Up / Unroll**: Double-click a fence's empty header bar to collapse it into a thin title bar.
- **Dock to Screen Edges**: Drag a fence to the edge of your monitor (or press `Ctrl + Arrow Keys`) to snap it cleanly.

### 3. Tabs, Merging & Folder Portals
- **Add a Tab**: Click the **`+`** button on a fence's header or select `Menu -> Create New -> New Tab Inside This Fence`.
- **Separate a Tab**: Drag any tab off the tab bar and drop it onto your desktop to turn it into a standalone fence.
- **Merge Two Fences**: Hold **Shift** while dragging one fence over another until it highlights, then release to combine them into tabs.
- **Create a Folder Portal**: Right-click the tray icon -> **Create Folder Portal...** and pick any folder on your computer to view and manage its contents live on your desktop.

### 4. Change Themes & Colors
1. Double-click the **Fluid Fences tray icon** to open **Settings**.
2. Go to the **Themes** tab to browse all 30 built-in **Dark** and **Light** themes, or click **Generate from Wallpaper** to create a theme from your current desktop background.
3. Want one specific fence to stand out? Go to **Customize Fence** to give an individual fence its own custom color, opacity, or **Auto-Match Wallpaper** tint.

---

## Keyboard & Mouse Shortcuts

| Shortcut / Action | Result |
| :--- | :--- |
| **`Ctrl + Alt + Z`** *(or `Ctrl + Shift + Z`)* | Toggle **Zen Mode** (hide/show all fences) |
| **Double-click empty desktop** | Toggle **Zen Mode** *(can be enabled/disabled in Settings)* |
| **Double-click fence header** | Roll up or unroll the fence |
| **`Ctrl + Arrow Keys`** | Snap the active fence to the top, bottom, left, or right edge of your screen |
| **`Shift + Drag` fence onto another fence** | Merge the dragged fence into the target fence as new tabs |
| **`Ctrl + C` / `Ctrl + V`** | Copy selected files in a fence / Paste files from clipboard into a fence |
| **`F2`** | Rename the selected item |
| **`Delete`** | Remove selected items from the fence |

---

## Installation

1. Download the latest **`FluidFences-2.3.5-Setup.exe`** installer from the [Releases page](https://github.com/Dave-Debugs/Fluid.Fences/releases).
2. Run the installer and launch **Fluid Fences**.

- **No extra downloads needed**: The installer is 100% self-contained (you do not need to install .NET separately).
- **Safe upgrades**: Installing a new version updates in place and keeps all of your existing fences, tabs, files, and settings intact.
- **Requirements**: Windows 10 or Windows 11 (64-bit).

---

## Building from Source

To build Fluid Fences yourself, install the **.NET 8 SDK** (or Visual Studio 2022 / 2026) and run:

```powershell
git clone https://github.com/Dave-Debugs/Fluid.Fences.git
cd Fluid.Fences
dotnet build DesktopFences/DesktopFences.csproj -c Release
```

To run the automated unit test suite:

```powershell
dotnet test DesktopFences.Tests/DesktopFences.Tests.csproj -c Release
```

---

## Support & Feedback

Found a bug or have an idea for a new feature? Open an issue on our [GitHub Repository](https://github.com/Dave-Debugs/Fluid.Fences/issues).

If Fluid Fences helps keep your desktop organized and you'd like to support the project:

<a href="https://www.buymeacoffee.com/davedebugs" target="_blank"><img src="https://cdn.buymeacoffee.com/buttons/v2/default-violet.png" alt="Buy Me a Coffee" height="60" width="217" style="height: 60px !important;width: 217px !important;" ></a>

- **Venmo**: [@Davedebugs](https://account.venmo.com/u/davedebugs)

---

## License

Fluid Fences is free and open-source software licensed under the **GNU General Public License v3.0 (GPL-3.0)**.

Copyright (c) 2026 [Dave-Debugs](https://github.com/Dave-Debugs) (David Daniel)

---

## Keywords

Desktop organization, desktop, stardock, fencesclone, fences, desktop fences, opensource, fencesalternative, Fluid Fences, Windows desktop organizer, Stardock Fences alternative, desktop icon organizer.
