using KREAN.Application.Scripting;
using KREAN.Core;
using KREAN.Core.Components;
using KREAN.Core.ECS;
using KREAN.Core.Physics;
using KREAN.Core.Scenes;
using KREAN.MapCompiler;
using KREAN.Runtime;
using KREAN.Runtime.Systems;
using System.Numerics;

namespace KREAN.Application.Game;

/// <summary>
/// Application-side game host. Owns the Engine loop, character movement, and script lifecycle.
/// Scripts are stored as .cs files under KREAN.Application/Scripts and compiled via Roslyn.
/// </summary>
public sealed class GameApp : IDisposable
{
    readonly Engine _engine;
    readonly ScriptManager _scripts;
    readonly string _scriptsRoot;
    readonly List<ISystem> _scriptSystems = new();

    string? _mapPath;
    string? _scenePath;

    public GameApp(string? mapPath, string? scenePath, string scriptsRoot)
    {
        _mapPath = mapPath;
        _scenePath = scenePath;
        _scriptsRoot = scriptsRoot;
        _engine = new Engine(new EngineOptions { Title = "KREAN — Play", Width = 1280, Height = 720, VSync = true });
        _scripts = new ScriptManager(_scriptsRoot);
        _scripts.Reloaded += OnScriptsReloaded;
    }

    public void Run()
    {
        // normalize paths
        if (_mapPath != null) _mapPath = Path.GetFullPath(_mapPath);
        if (_scenePath != null) _scenePath = Path.GetFullPath(_scenePath);
        // try to locate map if relative failed (editor saved to bin vs repo root mismatch)
        _mapPath = TryResolveMapPath(_mapPath);
        _scenePath = TryResolveScenePath(_scenePath);

        string? sceneToQueue = ResolveScene();

        string log;
        bool hasScripts = _scripts.CompileAndLoad(_engine.World, _engine.Collision, _engine.Input, out log);
        if (hasScripts)
        {
            foreach (var s in _scripts.Systems)
            {
                _engine.AddSystem(s);
                _scriptSystems.Add(s);
            }
            _scripts.EnableHotReload(_engine.World, _engine.Collision, _engine.Input);
        }
        else
        {
            Console.WriteLine(log);
            Console.WriteLine("[app] running without custom scripts (add .cs files to Scripts/)");
        }

        if (sceneToQueue != null)
            _engine.QueueScene(sceneToQueue);
        else if (_mapPath != null)
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"krean_{Guid.NewGuid():N}.scene.json");
            Console.WriteLine($"[app] compiling map '{_mapPath}' -> temp scene '{tmp}'");
            try
            {
                MapCompilerService.CompileToFile(_mapPath, tmp);
                if (!File.Exists(tmp)) throw new FileNotFoundException($"compile did not produce '{tmp}'");
                Console.WriteLine($"[app] compiled temp scene: {new FileInfo(tmp).Length} bytes");
                _engine.QueueScene(tmp);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[app] FAILED to compile map '{_mapPath}': {ex}");
                // fallback: try existing scene guesses
                var guess = _mapPath != null ? Path.ChangeExtension(_mapPath, ".scene.json") : null;
                if (guess != null && File.Exists(guess))
                {
                    Console.WriteLine($"[app] falling back to existing scene '{guess}'");
                    _engine.QueueScene(guess);
                }
                else if (File.Exists("sample.scene.json"))
                    _engine.QueueScene(Path.GetFullPath("sample.scene.json"));
                else
                    throw;
            }
        }

        Console.WriteLine($"[app] starting — map={_mapPath ?? "none"} scene={_scenePath ?? "auto"} scripts={_scriptsRoot} ({_scriptSystems.Count} systems)");
        PrintControls();

        _engine.Run();
    }

    static string? TryResolveMapPath(string? p)
    {
        if (p == null) return null;
        if (File.Exists(p)) return Path.GetFullPath(p);
        // try repo root, editor bin, app bin
        var candidates = new[]
        {
            Path.GetFullPath(p),
            Path.Combine(Directory.GetCurrentDirectory(), Path.GetFileName(p)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..","..","..","..", Path.GetFileName(p))),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..","..","..", "KREAN.Editor","bin","Debug","net10.0", Path.GetFileName(p))),
            Path.Combine(Path.GetTempPath(), Path.GetFileName(p)),
        };
        foreach(var c in candidates) if (File.Exists(c)) { Console.WriteLine($"[app] resolved map '{p}' -> '{c}'"); return Path.GetFullPath(c); }
        return p;
    }
    static string? TryResolveScenePath(string? p)
    {
        if (p == null) return null;
        if (File.Exists(p)) return Path.GetFullPath(p);
        return p;
    }

    string? ResolveScene()
    {
        if (_scenePath != null && File.Exists(_scenePath)) return Path.GetFullPath(_scenePath);
        if (_mapPath != null && File.Exists(_mapPath))
        {
            var sceneGuess = Path.ChangeExtension(_mapPath, ".scene.json");
            if (File.Exists(sceneGuess))
            {
                var mapTime = File.GetLastWriteTimeUtc(_mapPath);
                var sceneTime = File.GetLastWriteTimeUtc(sceneGuess);
                Console.WriteLine($"[app] checking scene guess '{sceneGuess}' mapTime={mapTime:o} sceneTime={sceneTime:o}");
                if (sceneTime >= mapTime)
                {
                    Console.WriteLine($"[app] using up-to-date scene '{sceneGuess}'");
                    return Path.GetFullPath(sceneGuess);
                }
                Console.WriteLine($"[app] scene stale, will recompile map");
            }
            // staleness -> let caller compile to temp; but also try to ensure we have something
            // compile directly to sceneGuess for caching (optional) and use temp
            try
            {
                var tmp = Path.Combine(Path.GetTempPath(), $"krean_{Guid.NewGuid():N}.scene.json");
                Console.WriteLine($"[app] recompiling stale map to temp '{tmp}'");
                MapCompilerService.CompileToFile(_mapPath, tmp);
                return tmp;
            }
            catch(Exception ex)
            {
                Console.WriteLine($"[app] recompile failed: {ex.Message}");
                // fallback to stale guess if exists
                if (File.Exists(sceneGuess)) return Path.GetFullPath(sceneGuess);
            }
            return null;
        }
        var defaultScene = Path.GetFullPath("sample.scene.json");
        if (File.Exists(defaultScene)) return defaultScene;
        if (File.Exists("sample.scene.json")) return Path.GetFullPath("sample.scene.json");
        return null;
    }

    void OnScriptsReloaded(IReadOnlyList<ISystem> old, IReadOnlyList<ISystem> @new)
    {
        // Engine doesn't support removing systems yet; we need to restart or swap.
        // For now, we add new systems and keep old (they will run double). Better to dispose and re-add.
        // Since Engine holds List<ISystem> privately, we cannot remove. We workaround by tracking and noting that hot reload requires restart in this version.
        // We will just add new ones; old remain. Log warning.
        Console.WriteLine($"[scripts] hot reload: {old.Count} -> {@new.Count} systems. New systems added; restart app for clean reload.");
        foreach (var s in @new)
        {
            if (!_scriptSystems.Contains(s))
            {
                _engine.AddSystem(s);
                _scriptSystems.Add(s);
                try { s.Init(_engine.World); } catch { }
            }
        }
    }

    static void PrintControls()
    {
        Console.WriteLine("""
            [controls] WASD move, Mouse look, Space jump/bunny-hop, V noclip toggle, Esc release mouse
            [scripts] Edit files under KREAN.Application/Scripts/*.cs — they auto-compile on save (hot reload)
            """);
    }

    public void Dispose()
    {
        _scripts.Dispose();
        _engine.Dispose();
    }
}
