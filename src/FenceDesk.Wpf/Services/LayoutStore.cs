using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using FenceDesk.Models;

namespace FenceDesk.Services;

public sealed class LayoutStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _dir;
    private DispatcherTimer? _saveTimer;
    private bool _savePending;
    private FenceLayout _layout = new();

    public LayoutStore()
    {
        _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FenceDesk");
        _path = Path.Combine(_dir, "layout.json");
    }

    public string DataDir
    {
        get
        {
            Directory.CreateDirectory(_dir);
            return _dir;
        }
    }

    public FenceLayout Layout
    {
        get { lock (_gate) return _layout; }
        private set { lock (_gate) _layout = value; }
    }

    public FenceLayout Load()
    {
        Directory.CreateDirectory(_dir);
        foreach (var candidate in new[] { _path, _path + ".bak", _path + ".bad" })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var raw = File.ReadAllText(candidate);
                if (string.IsNullOrWhiteSpace(raw)) continue;

                var layout = JsonSerializer.Deserialize<FenceLayout>(raw, JsonOptions);
                if (layout is null) continue;
                layout.Settings ??= new AppSettings();
                layout.Fences ??= new List<FenceModel>();
                layout.Groups ??= new Dictionary<string, string>(StringComparer.Ordinal);
                layout.Settings.DoubleClickDesktopHide = false;
                foreach (var f in layout.Fences)
                    f.EnsureDefaults();
                Layout = layout;
                if (!string.Equals(candidate, _path, StringComparison.OrdinalIgnoreCase))
                {
                    AppLog.Write($"Loaded layout from backup: {Path.GetFileName(candidate)}");
                    SaveImmediate();
                }
                return layout;
            }
            catch (Exception ex)
            {
                AppLog.Write($"Failed to read layout ({Path.GetFileName(candidate)}): {ex.Message}");
            }
        }

        Layout = CreateDefaultLayout();
        SaveImmediate();
        return Layout;
    }

    public void Save(bool immediate = false)
    {
        if (immediate)
        {
            WriteFile();
            return;
        }

        _savePending = true;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            WriteFile();
            return;
        }

        if (_saveTimer is null)
        {
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer!.Stop();
                if (!_savePending) return;
                _savePending = false;
                WriteFile();
            };
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveImmediate() => Save(immediate: true);

    private void WriteFile()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            string json;
            lock (_gate) json = JsonSerializer.Serialize(_layout, JsonOptions);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(_path))
            {
                try { File.Copy(_path, _path + ".bak", overwrite: true); } catch { /* ignore */ }
            }
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Write($"Failed to save layout: {ex.Message}");
        }
    }

    public FenceModel? FindFence(string id)
    {
        lock (_gate) return _layout.Fences.FirstOrDefault(f => f.Id == id);
    }

    public void UpdateFence(FenceModel model)
    {
        lock (_gate)
        {
            model.EnsureDefaults();
            var idx = _layout.Fences.FindIndex(f => f.Id == model.Id);
            if (idx >= 0) _layout.Fences[idx] = model;
            else _layout.Fences.Add(model);
        }
        Save();
    }

    public void AddFence(FenceModel model)
    {
        lock (_gate)
        {
            model.EnsureDefaults();
            _layout.Fences.Add(model);
        }
        SaveImmediate();
    }

    public void RemoveFence(string id)
    {
        lock (_gate) _layout.Fences.RemoveAll(f => f.Id == id);
        SaveImmediate();
    }

    public void SetAllLocked(bool locked)
    {
        lock (_gate)
        {
            foreach (var f in _layout.Fences)
                f.Locked = locked;
        }
        SaveImmediate();
    }

    public static FenceLayout CreateDefaultLayout()
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var wa = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea
                 ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        var right = Math.Max(40, wa.Right - 480);
        var top = wa.Top + 40;

        return new FenceLayout
        {
            Version = 1,
            Settings = new AppSettings(),
            Fences = new List<FenceModel>
            {
                FenceModel.Create("Apps", "items", null, right, top, 420, 180),
                FenceModel.Create("Files", "items", null, right, top + 200, 420, 160),
                FenceModel.Create(
                    "Downloads",
                    "portal",
                    Directory.Exists(downloads) ? downloads : desktop,
                    right, top + 380, 420, 200)
            }
        };
    }
}
