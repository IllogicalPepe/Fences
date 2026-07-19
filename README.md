# FenceDesk

Desktop fence organizer for Windows — inspired by [Stardock Fences](https://www.stardock.com/products/fences/). Independent open-source-style clone (not affiliated with Stardock).

Organize shortcuts, files, and folders into translucent **fences** on your desktop.

**Stack:** C# + **WinUI 3** (Windows App SDK), unpackaged self-contained app.

The previous PowerShell + WPF implementation is preserved under `legacy-powershell/` (and original files remain in `Modules/` until you remove them).

## Features

- **Fences** — translucent panels you can move and resize
- **Drag & drop** — drop files, folders, or shortcuts onto a fence
- **Launch** — double-click an icon to open it
- **Roll-up** — double-click the fence title (or use the ▲ button) to collapse to the title bar
- **Tabs** — right-click a fence → **Add tab**
- **Portals** — fence that mirrors a real folder and refreshes live
- **Background color** / **opacity** — right-click fence
- **Desktop-sized icons** — matches Windows desktop icon size settings when possible
- **Layout saved** under `%LOCALAPPDATA%\FenceDesk\layout.json` (compatible with the PowerShell layout format)
- **Tray control** — Show / Hide fences, New fence, colors, Exit (fences stay out of Alt+Tab)
- **Smart z-order** — topmost on the desktop / Win+D; under focused apps and fullscreen games

## Requirements

- Windows 10 (1809+) or Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build
- Runtime is **self-contained** (Windows App SDK shipped with the app)

## Quick start

### Already built

1. Double-click **`Start.bat`** (or `Start.vbs`)
2. Find the **FenceDesk** tray icon near the clock (left-click opens the control panel)
3. Drag desktop shortcuts into fences

### Build from source

```bat
Build.bat
```

Or:

```bat
cd src\FenceDesk
dotnet build -c Release -p:Platform=x64
```

Executable:

`src\FenceDesk\bin\x64\Release\net8.0-windows10.0.19041.0\FenceDesk.exe`

### Install (optional)

```bat
Install.bat
```

Or silent:

```bat
powershell -ExecutionPolicy Bypass -File Install.ps1 -Silent -StartWithWindows
```

Copies the app to `%LOCALAPPDATA%\Programs\FenceDesk`, creates shortcuts, optional autostart.

**Uninstall:** Start Menu → FenceDesk → Uninstall, or run `Uninstall.bat`.

## Project layout

```
FenceDesk/
  src/FenceDesk/          # C# WinUI 3 app
    Models/               # layout.json models
    Services/             # layout, icons, portals, desktop hide, tray
    Native/               # Win32 interop (z-order, shell icons)
    Windows/              # FenceWindow
  Assets/                 # App icon
  legacy-powershell/      # Previous PS implementation (backup)
  Start.bat / Build.bat
```

## Usage

| Action | How |
|--------|-----|
| Move fence | Drag the title bar |
| Resize | Drag edges (window chrome / resize grip) |
| Roll up | Double-click title or ▲ |
| Add items | Drop files onto the fence |
| Remove item | Right-click icon → Remove from fence |
| Rename fence | Right-click fence → Rename |
| Background color | Right-click fence → Background color |
| Add tab | Right-click fence → Add tab |
| Portal | Right-click → Convert to portal → pick folder |
| New fence | Tray menu or control panel |
| Show / Hide | Tray / control panel |
| Exit completely | Tray **Exit**, or control panel Exit |

## Data

| Path | Purpose |
|------|---------|
| `%LOCALAPPDATA%\FenceDesk\layout.json` | Fence layout |
| `%LOCALAPPDATA%\FenceDesk\fencedesk.log` | Log file |
| `%LOCALAPPDATA%\FenceDesk\icon-cache\` | Icon cache (if used) |
| `%LOCALAPPDATA%\FenceDesk\desktop-shelved\` | Shelved public-desktop shortcuts |
| `%LOCALAPPDATA%\FenceDesk\hidden-desktop.json` | Desktop icon hide state |

## Known limits (vs Stardock Fences)

- Does **not** replace the Explorer desktop icon layer completely
- Fences are normal windows (apps can cover them); use tray **Bring fences to front**
- No auto-sort rules, snapshots, or Peek (yet)
- Portal drop **copies** into the portal folder
- True WPF-style glass transparency is approximated with semi-transparent WinUI panels

## License

Free to use and modify. Fences™ is a trademark of Stardock; this project is an independent recreation of the idea.
