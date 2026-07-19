using FenceDesk.Models;
using Microsoft.UI.Dispatching;

namespace FenceDesk.Services;

public sealed class PortalService : IDisposable
{
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new();
    private readonly Dictionary<string, Action<string>> _callbacks = new();
    private readonly object _gate = new();
    private DispatcherQueue? _dispatcher;

    public void SetDispatcher(DispatcherQueue dispatcher) => _dispatcher = dispatcher;

    public static IReadOnlyList<FenceItem> GetPortalItems(string? folderPath, int maxItems = 200)
    {
        var items = new List<FenceItem>();
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return items;

        try
        {
            var entries = Directory.EnumerateFileSystemEntries(folderPath)
                .Select(p => new FileInfo(p))
                .Where(e =>
                {
                    try
                    {
                        var name = Path.GetFileName(e.FullName);
                        if (name is "desktop.ini" or "Thumbs.db" or "$RECYCLE.BIN") return false;
                        return true;
                    }
                    catch { return false; }
                })
                .OrderBy(e => Directory.Exists(e.FullName) ? 0 : 1)
                .ThenBy(e => Path.GetFileName(e.FullName), StringComparer.OrdinalIgnoreCase)
                .Take(maxItems);

            foreach (var e in entries)
            {
                items.Add(new FenceItem
                {
                    Path = e.FullName,
                    Label = Path.GetFileName(e.FullName)
                });
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

            void Handler(object? s, FileSystemEventArgs e) => Raise(fenceId);
            void Renamed(object? s, RenamedEventArgs e) => Raise(fenceId);

            fsw.Created += Handler;
            fsw.Deleted += Handler;
            fsw.Changed += Handler;
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

    private void Raise(string fenceId)
    {
        Action<string>? cb;
        lock (_gate)
            _callbacks.TryGetValue(fenceId, out cb);
        if (cb is null) return;

        var dq = _dispatcher ?? DispatcherQueue.GetForCurrentThread();
        if (dq is not null)
        {
            dq.TryEnqueue(() =>
            {
                try { cb(fenceId); } catch { /* ignore */ }
            });
        }
        else
        {
            try { cb(fenceId); } catch { /* ignore */ }
        }
    }

    public void Unregister(string fenceId)
    {
        lock (_gate)
        {
            if (_watchers.TryGetValue(fenceId, out var fsw))
            {
                try
                {
                    fsw.EnableRaisingEvents = false;
                    fsw.Dispose();
                }
                catch { /* ignore */ }
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
