# FenceDesk

Desktop fence organizer for Windows — inspired by [Stardock Fences](https://www.stardock.com/products/fences/). Independent open-source-style clone (not affiliated with Stardock).

Organize shortcuts, files, and folders into translucent **fences** on your desktop. Built with **PowerShell + WPF** only (no Node, Python, or extra runtime).

## Features

- **Fences** — translucent panels you can move and resize
- **Drag & drop** — drop files, folders, or shortcuts onto a fence
- **Launch** — double-click an icon to open it
- **Roll-up** — double-click the fence title (or use the ▲ button) to collapse to the title bar
- **Tabs** — right-click a fence → **Add tab** (e.g. Client A / B / C)
- **Portals** — fence that mirrors a real folder and refreshes live
- **Background color** — right-click fence → **Background color...** (full color wheel)
- **Desktop-sized icons** — icon and label size match your Windows desktop icon settings
- **Layout saved** under `%LOCALAPPDATA%\FenceDesk\layout.json`
- **Tray control** — Show / Hide fences, New fence, colors, Exit (fences stay out of Alt+Tab)

## Requirements

- Windows 10 or 11
- PowerShell 5.1+ (built into Windows)

## Quick start

1. Double-click **`Start.bat`** (or `Start.vbs`)
2. Find the **FenceDesk** tray icon near the clock (left-click opens the control panel)
3. Three starter fences appear: **Apps**, **Files**, and a **Downloads** portal
4. Drag desktop shortcuts into Apps / Files
5. Fences and the control panel do **not** show up in Alt+Tab

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

## Usage

| Action | How |
|--------|-----|
| Move fence | Drag the title bar |
| Resize | Drag edges / grip |
| Roll up | Double-click title or ▲ |
| Add items | Drop files onto the fence |
| Remove item | Right-click icon → Remove from fence |
| Rename fence | Right-click fence → Rename |
| Background color | Right-click fence → **Background color** → This fence / All fences / Reset |
| Add tab | Right-click fence → Add tab |
| Switch tab | Click tab labels |
| Portal | Right-click → Convert to portal → pick folder |
| New fence | Tray menu, control panel, or right-click fence → New fence |
| Show fences | Tray / control panel **Show fences** |
| Hide fences | Tray / control panel **Hide fences** |
| Exit completely | Tray **Exit**, or close the control panel (confirm) |
| Open control panel | Left-click the tray icon |
| Opacity | Right-click fence → Opacity... (slider with live preview) |
| Desktop icons | Shortcuts/files on the desktop that are in a fence are **hidden** on the desktop (still openable from the fence) |

## Known limits (vs Stardock Fences)

- Does **not** replace or hide the normal Explorer desktop icon layer
- Fences are normal windows (apps can cover them); use tray **Bring fences to front**
- No auto-sort rules, snapshots, or Peek (yet)
- Portal drop **copies** into the portal folder
- Desktop double-click to hide/show is **not** used (use taskbar/tray instead)

## Data

| Path | Purpose |
|------|---------|
| `%LOCALAPPDATA%\FenceDesk\layout.json` | Fence layout |
| `%LOCALAPPDATA%\FenceDesk\fencedesk.log` | Log file |
| `%LOCALAPPDATA%\FenceDesk\icon-cache\` | Icon cache (if used) |

## License

Free to use and modify. Fences™ is a trademark of Stardock; this project is an independent recreation of the idea.
