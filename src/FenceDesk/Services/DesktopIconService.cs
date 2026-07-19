using System.Text.Json;
using FenceDesk.Models;
using FenceDesk.Native;
using Microsoft.Win32;

namespace FenceDesk.Services;

public sealed class DesktopIconService
{
    public static readonly IReadOnlyDictionary<string, ShellIconInfo> ShellDesktopIcons =
        new Dictionary<string, ShellIconInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["{645FF040-5081-101B-9F08-00AA002F954E}"] = new("Recycle Bin", "shell:RecycleBinFolder",
                "::{645FF040-5081-101B-9F08-00AA002F954E}"),
            ["{20D04FE0-3AEA-1069-A2D8-08002B30309D}"] = new("This PC", "shell:MyComputerFolder",
                "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"),
            ["{59031A47-3F72-44A7-89C5-5595FE6B30EE}"] = new("User Files", "shell:UsersFilesFolder",
                "::{59031A47-3F72-44A7-89C5-5595FE6B30EE}"),
            ["{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}"] = new("Network", "shell:NetworkPlacesFolder",
                "::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}"),
            ["{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}"] = new("Control Panel", "shell:ControlPanelFolder",
                "::{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}"),
        };

    private readonly LayoutStore _layoutStore;
    private readonly string _statePath;
    private readonly string _shelveDir;
    private readonly HashSet<string> _hiddenPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _shelvedMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _hiddenShell = new(StringComparer.OrdinalIgnoreCase);

    public DesktopIconService(LayoutStore layoutStore)
    {
        _layoutStore = layoutStore;
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FenceDesk");
        _statePath = Path.Combine(data, "hidden-desktop.json");
        _shelveDir = Path.Combine(data, "desktop-shelved");
    }

    public void Initialize()
    {
        Directory.CreateDirectory(_shelveDir);
        ReadState();
        RepairShelvedMap();
        SyncVisibility();
    }

    public static bool IsShellNamespacePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.StartsWith("::{", StringComparison.Ordinal) || path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            return true;
        return System.Text.RegularExpressions.Regex.IsMatch(path, @"^::\{[0-9A-Fa-f\-]+\}$");
    }

    public static string? GetShellClsid(string path)
    {
        var m = System.Text.RegularExpressions.Regex.Match(path, @"::\{([0-9A-Fa-f\-]+)\}");
        if (m.Success) return "{" + m.Groups[1].Value.ToUpperInvariant() + "}";
        foreach (var kv in ShellDesktopIcons)
        {
            if (path.Equals(kv.Value.Path, StringComparison.OrdinalIgnoreCase) ||
                path.Equals(kv.Value.Launch, StringComparison.OrdinalIgnoreCase))
                return kv.Key;
        }
        return null;
    }

    public static string RecycleBinPath => "::{645FF040-5081-101B-9F08-00AA002F954E}";

    public string ResolveItemPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || IsShellNamespacePath(path)) return path;
        if (File.Exists(path) || Directory.Exists(path)) return path;

        var key = path;
        try { key = Path.GetFullPath(path); } catch { /* ignore */ }

        if (_shelvedMap.TryGetValue(key, out var s) && File.Exists(s)) return s;
        var name = Path.GetFileName(path);
        if (!string.IsNullOrEmpty(name) && _shelvedMap.TryGetValue(name, out s) && File.Exists(s)) return s;

        var candidate = Path.Combine(_shelveDir, name ?? "");
        if (File.Exists(candidate)) return candidate;
        return path;
    }

    public void LaunchItem(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (IsShellNamespacePath(path))
            {
                var clsid = GetShellClsid(path);
                var launch = path;
                if (clsid is not null && ShellDesktopIcons.TryGetValue(clsid, out var info))
                    launch = info.Launch;
                if (path.StartsWith("::{", StringComparison.Ordinal))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = path,
                        UseShellExecute = true
                    });
                    return;
                }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = launch,
                    UseShellExecute = true
                });
                return;
            }

            var resolved = ResolveItemPath(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = resolved,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLog.Write($"Launch failed ({path}): {ex.Message}");
            try
            {
                System.Windows.Forms.MessageBox.Show($"Could not open:\n{path}", "FenceDesk");
            }
            catch { /* ignore */ }
        }
    }

    public void SyncVisibility()
    {
        try
        {
            var fenced = GetAllFencedPaths();
            var shouldHideFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var shouldHideShell = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fencedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (key, path) in fenced)
            {
                if (IsShellNamespacePath(path))
                {
                    var clsid = GetShellClsid(path);
                    if (clsid is not null) shouldHideShell.Add(clsid);
                    continue;
                }
                var fn = Path.GetFileName(path);
                if (!string.IsNullOrEmpty(fn)) fencedFileNames.Add(fn);
                if (IsFencedDesktopPath(path))
                {
                    try { shouldHideFiles[Path.GetFullPath(path)] = path; }
                    catch { shouldHideFiles[key] = path; }
                }
            }

            var changed = false;
            foreach (var (key, path) in shouldHideFiles)
            {
                var alreadyShelved = _shelvedMap.ContainsKey(key);
                var fn = Path.GetFileName(path);
                if (!string.IsNullOrEmpty(fn))
                {
                    foreach (var sk in _shelvedMap.Keys.ToList())
                    {
                        if (Path.GetFileName(sk).Equals(fn, StringComparison.OrdinalIgnoreCase) ||
                            Path.GetFileName(_shelvedMap[sk]).Equals(fn, StringComparison.OrdinalIgnoreCase))
                        {
                            alreadyShelved = true;
                            break;
                        }
                    }
                    if (File.Exists(Path.Combine(_shelveDir, fn))) alreadyShelved = true;
                }

                if (alreadyShelved)
                {
                    if (File.Exists(path) || Directory.Exists(path))
                    {
                        if (HideOrShelve(path)) changed = true;
                    }
                    continue;
                }

                if (_hiddenPaths.Contains(key))
                {
                    if (File.Exists(path) || Directory.Exists(path))
                        HideByAttribute(path);
                    continue;
                }

                if (File.Exists(path) || Directory.Exists(path))
                {
                    if (HideOrShelve(path)) changed = true;
                }
            }

            foreach (var clsid in shouldHideShell)
            {
                if (!_hiddenShell.Contains(clsid))
                {
                    if (SetShellHidden(clsid, true))
                    {
                        _hiddenShell.Add(clsid);
                        changed = true;
                    }
                }
                else SetShellHidden(clsid, true);
            }

            foreach (var key in _hiddenPaths.ToList())
            {
                if (shouldHideFiles.ContainsKey(key)) continue;
                var fn = Path.GetFileName(key);
                if (!string.IsNullOrEmpty(fn) && fencedFileNames.Contains(fn)) continue;
                var path = ResolveExistingDesktopPath(key);
                ShowByAttribute(path);
                _hiddenPaths.Remove(key);
                changed = true;
            }

            foreach (var key in _shelvedMap.Keys.ToList())
            {
                if (shouldHideFiles.ContainsKey(key)) continue;
                var fn = Path.GetFileName(key);
                if (string.IsNullOrEmpty(fn)) fn = Path.GetFileName(_shelvedMap[key]);
                if (!string.IsNullOrEmpty(fn) && fencedFileNames.Contains(fn)) continue;
                if (!key.Contains('\\') && !key.Contains('/')) continue;
                if (RestoreShelved(key)) changed = true;
            }

            foreach (var clsid in _hiddenShell.ToList())
            {
                if (shouldHideShell.Contains(clsid)) continue;
                if (SetShellHidden(clsid, false))
                {
                    _hiddenShell.Remove(clsid);
                    changed = true;
                }
            }

            if (changed)
            {
                WriteState();
                NativeMethods.RefreshDesktop();
                AppLog.Write($"Desktop icon sync: hidden={_hiddenPaths.Count} shelved={_shelvedMap.Count} shell={_hiddenShell.Count}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"SyncVisibility: {ex.Message}");
        }
    }

    private Dictionary<string, string> GetAllFencedPaths()
    {
        var set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in _layoutStore.Layout.Fences)
        {
            if (f.IsPortal) continue;
            foreach (var t in f.Tabs)
            {
                foreach (var it in t.Items)
                {
                    if (string.IsNullOrWhiteSpace(it.Path)) continue;
                    if (IsShellNamespacePath(it.Path))
                        set[it.Path] = it.Path;
                    else
                    {
                        try { set[Path.GetFullPath(it.Path)] = it.Path; }
                        catch { set[it.Path] = it.Path; }
                    }
                }
            }
        }
        return set;
    }

    private bool IsFencedDesktopPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || IsShellNamespacePath(path)) return false;
        if (IsOnDesktop(path)) return true;
        try
        {
            var full = path;
            try { full = Path.GetFullPath(path); } catch { /* ignore */ }
            var parent = Path.GetDirectoryName(full);
            if (parent is not null)
            {
                foreach (var desk in GetDesktopFolders())
                {
                    if (string.Equals(Path.GetFullPath(parent).TrimEnd('\\'),
                            Path.GetFullPath(desk).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        catch { /* ignore */ }

        var key = path;
        try { key = Path.GetFullPath(path); } catch { /* ignore */ }
        if (_shelvedMap.ContainsKey(key)) return true;
        var name = Path.GetFileName(path);
        if (!string.IsNullOrEmpty(name) && File.Exists(Path.Combine(_shelveDir, name))) return true;
        return false;
    }

    private static bool IsOnDesktop(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || IsShellNamespacePath(path)) return false;
        if (!File.Exists(path) && !Directory.Exists(path)) return false;
        try
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(path))?.TrimEnd('\\');
            if (parent is null) return false;
            foreach (var desk in GetDesktopFolders())
            {
                if (string.Equals(parent, Path.GetFullPath(desk).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { /* ignore */ }
        return false;
    }

    private static IEnumerable<string> GetDesktopFolders()
    {
        var list = new List<string>();
        try
        {
            list.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            list.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
        }
        catch { /* ignore */ }
        return list.Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p))
            .Select(p => Path.GetFullPath(p).TrimEnd('\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private bool HideOrShelve(string path)
    {
        if (IsShellNamespacePath(path))
        {
            var clsid = GetShellClsid(path);
            if (clsid is null) return false;
            if (SetShellHidden(clsid, true))
            {
                _hiddenShell.Add(clsid);
                return true;
            }
            return false;
        }

        if (!IsOnDesktop(path)) return false;
        if (HideByAttribute(path))
        {
            try { _hiddenPaths.Add(Path.GetFullPath(path)); } catch { _hiddenPaths.Add(path); }
            return true;
        }
        return Shelve(path);
    }

    private static bool HideByAttribute(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return false;
            var attrs = File.GetAttributes(path);
            if (attrs.HasFlag(FileAttributes.Hidden)) return true;
            File.SetAttributes(path, attrs | FileAttributes.Hidden);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool ShowByAttribute(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return false;
            var attrs = File.GetAttributes(path);
            if (attrs.HasFlag(FileAttributes.Hidden))
                File.SetAttributes(path, attrs & ~FileAttributes.Hidden);
            return true;
        }
        catch { return false; }
    }

    private bool Shelve(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return false;
            var key = Path.GetFullPath(path);
            if (_shelvedMap.ContainsKey(key)) return true;
            Directory.CreateDirectory(_shelveDir);
            var name = Path.GetFileName(path);
            var dest = Path.Combine(_shelveDir, name);
            var i = 1;
            while (File.Exists(dest) || Directory.Exists(dest))
            {
                dest = Path.Combine(_shelveDir,
                    $"{Path.GetFileNameWithoutExtension(name)}_{i}{Path.GetExtension(name)}");
                i++;
            }
            if (Directory.Exists(path))
                Directory.Move(path, dest);
            else
                File.Move(path, dest, overwrite: true);
            _shelvedMap[key] = dest;
            AppLog.Write($"Shelved desktop icon: {path} -> {dest}");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write($"Shelve failed ({path}): {ex.Message}");
            return false;
        }
    }

    private bool RestoreShelved(string originalKey)
    {
        try
        {
            if (!_shelvedMap.TryGetValue(originalKey, out var shelved)) return false;
            if (string.IsNullOrWhiteSpace(shelved) || !File.Exists(shelved))
            {
                _shelvedMap.Remove(originalKey);
                return true;
            }

            var userDesk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var name = Path.GetFileName(shelved);
            var dest = originalKey;
            if (!originalKey.Contains('\\') || string.IsNullOrEmpty(Path.GetDirectoryName(dest)) ||
                !Directory.Exists(Path.GetDirectoryName(dest)!))
            {
                dest = Path.Combine(userDesk, name);
            }

            if (File.Exists(dest) || Directory.Exists(dest))
            {
                var i = 1;
                var baseName = Path.GetFileNameWithoutExtension(name);
                var ext = Path.GetExtension(name);
                do
                {
                    dest = Path.Combine(userDesk, $"{baseName}_{i}{ext}");
                    i++;
                } while (File.Exists(dest) || Directory.Exists(dest));
            }

            File.Move(shelved, dest, overwrite: true);
            foreach (var k in _shelvedMap.Where(kv => kv.Value == shelved).Select(kv => kv.Key).ToList())
                _shelvedMap.Remove(k);
            AppLog.Write($"Restored shelved icon: {shelved} -> {dest}");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write($"Restore shelved failed ({originalKey}): {ex.Message}");
            return false;
        }
    }

    private static bool SetShellHidden(string clsid, bool hidden)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(clsid, @"^\{[0-9A-Fa-f\-]+\}$")) return false;
        clsid = clsid.ToUpperInvariant();
        var value = hidden ? 1 : 0;
        var keys = new[]
        {
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\ClassicStartMenu"
        };
        var ok = false;
        foreach (var reg in keys)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(reg);
                key?.SetValue(clsid, value, RegistryValueKind.DWord);
                ok = true;
            }
            catch (Exception ex)
            {
                AppLog.Write($"Registry hide {clsid} failed ({reg}): {ex.Message}");
            }
        }
        return ok;
    }

    private string ResolveExistingDesktopPath(string key)
    {
        if (File.Exists(key) || Directory.Exists(key)) return key;
        var name = Path.GetFileName(key);
        foreach (var desk in GetDesktopFolders())
        {
            var candidate = Path.Combine(desk, name);
            if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
        }
        return key;
    }

    private void ReadState()
    {
        _hiddenPaths.Clear();
        _shelvedMap.Clear();
        _hiddenShell.Clear();
        if (!File.Exists(_statePath)) return;
        try
        {
            var obj = JsonSerializer.Deserialize<DesktopHiddenState>(File.ReadAllText(_statePath));
            if (obj is null) return;
            foreach (var p in obj.Paths)
                if (!string.IsNullOrEmpty(p)) _hiddenPaths.Add(p);
            foreach (var kv in obj.Shelved)
                _shelvedMap[kv.Key] = kv.Value;
            foreach (var c in obj.ShellIcons)
                if (!string.IsNullOrEmpty(c)) _hiddenShell.Add(c);
        }
        catch (Exception ex)
        {
            AppLog.Write($"Read hidden-desktop state failed: {ex.Message}");
        }
    }

    private void WriteState()
    {
        try
        {
            var dir = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var obj = new DesktopHiddenState
            {
                Paths = _hiddenPaths.ToList(),
                Shelved = new Dictionary<string, string>(_shelvedMap, StringComparer.OrdinalIgnoreCase),
                ShellIcons = _hiddenShell.ToList()
            };
            var tmp = _statePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _statePath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Write($"Write hidden-desktop state failed: {ex.Message}");
        }
    }

    private void RepairShelvedMap()
    {
        try
        {
            if (!Directory.Exists(_shelveDir)) return;
            var desks = GetDesktopFolders().ToList();
            var changed = false;
            foreach (var file in Directory.GetFiles(_shelveDir))
            {
                var name = Path.GetFileName(file);
                foreach (var desk in desks)
                {
                    var orig = Path.Combine(desk, name);
                    if (!_shelvedMap.ContainsKey(orig) || !File.Exists(_shelvedMap[orig]))
                    {
                        _shelvedMap[orig] = file;
                        changed = true;
                    }
                }
                if (!_shelvedMap.ContainsKey(name))
                {
                    _shelvedMap[name] = file;
                    changed = true;
                }
            }
            if (changed) WriteState();
        }
        catch (Exception ex)
        {
            AppLog.Write($"RepairShelvedMap: {ex.Message}");
        }
    }
}

public sealed record ShellIconInfo(string Name, string Launch, string Path);
