using System.IO;
using System.Windows.Threading;
using FenceDesk.Models;

namespace FenceDesk.Services;

public sealed class PortalService : IDisposable
{
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new();
    private readonly Dictionary<string, Action<string>> _callbacks = new();
    private readonly object _gate = new();
    private Dispatcher? _dispatcher;

    public void SetDispatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public static IReadOnlyList<FenceItem> GetPortalItems(string? folderPath, int maxItems = 200)
    {
        var items = new List<FenceItem>();
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return items;

        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(folderPath)
                         .OrderBy(p => Directory.Exists(p) ? 0 : 1)
                         .ThenBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                         .Take(maxItems * 2))
            {
                var name = Path.GetFileName(path);
                if (name is "desktop.ini" or "Thumbs.db" or "$RECYCLE.BIN") continue;
                items.Add(new FenceItem { Path = path, Label = name });
                if (items.Count >= maxItems) break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"Portal list failed for {folderPath}: {ex.Message}");
        }

        return items;
    }

    public void Register(string fenceId, string? folderPath, Action<string> onChanged)
    {
        Unregister(fenceId);
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return;

        try
        {
            var fsw = new FileSystemWatcher(folderPath)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };

            void Raise(object? s, FileSystemEventArgs e) => Invoke(fenceId);
            void Renamed(object? s, RenamedEventArgs e) => Invoke(fenceId);
            fsw.Created += Raise;
            fsw.Deleted += Raise;
            fsw.Changed += Raise;
            fsw.Renamed += Renamed;

            lock (_gate)
            {
                _watchers[fenceId] = fsw;
                _callbacks[fenceId] = onChanged;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"Portal watcher failed: {ex.Message}");
        }
    }

    private void Invoke(string fenceId)
    {
        Action<string>? cb;
        lock (_gate) _callbacks.TryGetValue(fenceId, out cb);
        if (cb is null) return;

        var d = _dispatcher ?? System.Windows.Application.Current?.Dispatcher;
        if (d is not null)
            d.BeginInvoke(() => { try { cb(fenceId); } catch { /* ignore */ } });
        else
            try { cb(fenceId); } catch { /* ignore */ }
    }

    public void Unregister(string fenceId)
    {
        lock (_gate)
        {
            if (_watchers.TryGetValue(fenceId, out var fsw))
            {
                try { fsw.EnableRaisingEvents = false; fsw.Dispose(); } catch { /* ignore */ }
                _watchers.Remove(fenceId);
            }
            _callbacks.Remove(fenceId);
        }
    }

    public void UnregisterAll()
    {
        foreach (var id in _watchers.Keys.ToList())
            Unregister(id);
    }

    public void Dispose() => UnregisterAll();
}
