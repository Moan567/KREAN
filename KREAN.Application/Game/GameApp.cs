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
        // Resolve scene to queue
        string? sceneToQueue = ResolveScene();

        // Compile scripts before engine starts (so systems are available during Init)
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
            // fallback: compile map on the fly to temp scene
            var tmp = Path.Combine(Path.GetTempPath(), $"krean_{Guid.NewGuid():N}.scene.json");
            Console.WriteLine($"[app] compiling map '{_mapPath}' -> temp scene");
            MapCompilerService.CompileToFile(_mapPath, tmp);
            _engine.QueueScene(tmp);
        }

        Console.WriteLine($"[app] starting — map={_mapPath ?? "none"} scene={_scenePath ?? "auto"} scripts={_scriptsRoot} ({_scriptSystems.Count} systems)");
        PrintControls();

        _engine.Run();
    }

    string? ResolveScene()
    {
        if (_scenePath != null && File.Exists(_scenePath)) return _scenePath;
        if (_mapPath != null && File.Exists(_mapPath))
        {
            bool needCompile = true;
            var sceneGuess = Path.ChangeExtension(_mapPath, ".scene.json");
            if (File.Exists(sceneGuess))
            {
                // use existing if newer than map
                if (File.GetLastWriteTimeUtc(sceneGuess) >= File.GetLastWriteTimeUtc(_mapPath))
                {
                    needCompile = false;
                    return sceneGuess;
                }
            }
            if (needCompile)
            {
                var outPath = sceneGuess;
                // don't overwrite silently if Editor is expected to export; but for play we compile temp instead?
                // compile to temp to avoid polluting
                return null;
            }
        }
        // try default sample
        if (File.Exists("sample.scene.json")) return "sample.scene.json";
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
