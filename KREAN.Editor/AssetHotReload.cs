using System.IO;

namespace KREAN.Editor;

public sealed class AssetHotReload : IDisposable
{
    readonly MapEditorSession _session;
    readonly Action _recompile;
    readonly List<FileSystemWatcher> _watchers = new();
    DateTime _lastReload = DateTime.MinValue;

    public AssetHotReload(MapEditorSession session, Action recompile)
    {
        _session = session;
        _recompile = recompile;
    }

    public void Start()
    {
        var watchDirs = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), "textures"),
            Path.Combine(Directory.GetCurrentDirectory(), "assets", "textures"),
            Path.Combine(Directory.GetCurrentDirectory(), "models"),
            Path.Combine(Directory.GetCurrentDirectory(), "assets", "models"),
            Path.Combine(AppContext.BaseDirectory, "textures"),
            Path.Combine(AppContext.BaseDirectory, "models"),
            Path.Combine("KREAN.Runtime", "assets", "shaders"),
            Path.Combine(Directory.GetCurrentDirectory(), "KREAN.Runtime", "assets", "shaders"),
        };
        foreach(var d in watchDirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if(!Directory.Exists(d)) continue;
            try
            {
                var w = new FileSystemWatcher(d)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    Filter = "*.*",
                    EnableRaisingEvents = true
                };
                w.Changed += OnChanged;
                w.Created += OnChanged;
                w.Renamed += OnChanged;
                w.Deleted += OnChanged;
                _watchers.Add(w);
                Console.WriteLine($"[hotreload] watching {Path.GetFullPath(d)}");
            } catch {}
        }
        // also watch current map's directory for prefabs
        try
        {
            var mapDir = _session.FilePath != null ? Path.GetDirectoryName(Path.GetFullPath(_session.FilePath)) : Directory.GetCurrentDirectory();
            if(!string.IsNullOrEmpty(mapDir) && Directory.Exists(mapDir))
            {
                var w2 = new FileSystemWatcher(mapDir, "*.map") { IncludeSubdirectories = true, EnableRaisingEvents = true };
                w2.Changed += OnChanged; w2.Created += OnChanged; w2.Renamed += OnChanged;
                _watchers.Add(w2);
            }
        } catch {}
    }

    void OnChanged(object s, FileSystemEventArgs e)
    {
        if((DateTime.UtcNow - _lastReload).TotalMilliseconds < 800) return;
        _lastReload = DateTime.UtcNow;
        // debounce
        Task.Delay(300).ContinueWith(_ =>
        {
            try
            {
                Console.WriteLine($"[hotreload] {e.ChangeType} {e.FullPath} -> recompile");
                _recompile();
            } catch {}
        });
    }

    public void Dispose()
    {
        foreach(var w in _watchers) try{ w.Dispose(); }catch{}
        _watchers.Clear();
    }
}
