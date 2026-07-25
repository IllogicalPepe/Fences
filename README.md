# FenceDesk

Desktop fence organizer for Windows — inspired by [Stardock Fences](https://www.stardock.com/products/fences/). Independent recreation (not affiliated with Stardock).

Organize shortcuts, files, and folders into translucent **fences** on your desktop.

**Stack:** C# + WPF (.NET 8), self-contained Windows app.

## Install (users)

1. Download **`FenceDesk-Setup-2.1.0.exe`** from the [latest GitHub Release](https://github.com/IllogicalPepe/Fences/releases/latest)
2. Double-click the installer (no admin required)
3. Find the **FenceDesk** tray icon near the clock

Uninstall via **Settings → Apps**, or Start Menu → FenceDesk → Uninstall.

## Features

- Translucent movable/resizable fences
- Drag & drop files, folders, and shortcuts
- Multi-select (marquee) and rearrange icons inside a fence
- Unified **Appearance** editor (colors, opacity, apply to all)
- Emoji in fence titles (`fire emoji`, `:tada:`, …)
- Recycle Bin empty/full icon state
- Tabs and portal (folder) fences
- Double-click empty desktop to show/hide fences
- Smart z-order (on desktop after Win+D; under focused apps/games)
- Layout saved under `%LOCALAPPDATA%\FenceDesk\`

## Build from source

Requirements: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), [Inno Setup 6](https://jrsoftware.org/isinfo.php) (for the installer).

```bat
Build.bat
```

Creates `src\FenceDesk.Wpf\bin\Release\net8.0-windows\FenceDesk.exe`.

### Windows installer

```bat
Build-Installer.bat
```

Produces `dist\FenceDesk-Setup-2.1.0.exe` (self-contained; friends do not need the .NET runtime).

### Local update (dev)

```bat
Launch.bat
```

Publishes a Release build and copies it over your installed copy under `%LOCALAPPDATA%\Programs\FenceDesk\`.

## Project layout

```
Fences/
  src/FenceDesk.Wpf/     # App (WPF)
  installer/             # Inno Setup script
  Assets/                # App icon
  Build.bat              # Compile Release
  Build-Installer.bat    # Publish + Inno setup.exe
  Launch.bat             # Dev: rebuild + update install
```

## Data

| Path | Purpose |
|------|---------|
| `%LOCALAPPDATA%\FenceDesk\layout.json` | Fence layout |
| `%LOCALAPPDATA%\FenceDesk\fencedesk.log` | Log file |
| `%LOCALAPPDATA%\Programs\FenceDesk\` | Installed app |

## License

Free to use and modify. Fences™ is a trademark of Stardock; this project is an independent recreation of the idea.
