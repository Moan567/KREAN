using System.Reflection;
using KREAN.Core.ECS;
using KREAN.Core.Physics;
using KREAN.Runtime;

namespace KREAN.Application.Scripting;

/// <summary>
/// Discovers and instantiates scripts from a compiled assembly. Supports hot reload via FileSystemWatcher.
/// Scripts may implement ISystem or IGameScript. Constructor injection supports World, CollisionWorld, InputState, etc.
/// </summary>
public sealed class ScriptManager : IDisposable
{
    readonly string _scriptsRoot;
    Assembly? _assembly;
    readonly List<ISystem> _systems = new();
    FileSystemWatcher? _watcher;
    DateTime _lastCompile = DateTime.MinValue;

    public IReadOnlyList<ISystem> Systems => _systems;
    public event Action<IReadOnlyList<ISystem>, IReadOnlyList<ISystem>>? Reloaded; // (old, new)

    public ScriptManager(string scriptsRoot) => _scriptsRoot = Path.GetFullPath(scriptsRoot);

    public bool CompileAndLoad(World world, CollisionWorld collision, InputState input, out string log)
    {
        var old = _systems.ToArray();
        _systems.Clear();
        var asm = ScriptCompiler.Compile(_scriptsRoot, out log);
        if (asm == null) return false;
        _assembly = asm;
        var discovered = DiscoverSystems(asm, world, collision, input);
        _systems.AddRange(discovered);
        Reloaded?.Invoke(old, _systems);
        _lastCompile = DateTime.UtcNow;
        return true;
    }

    public void EnableHotReload(World world, CollisionWorld collision, InputState input)
    {
        if (_watcher != null) return;
        if (!Directory.Exists(_scriptsRoot)) return;
        _watcher = new FileSystemWatcher(_scriptsRoot, "*.cs")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _watcher.Changed += (_, _) => DebouncedReload(world, collision, input);
        _watcher.Created += (_, _) => DebouncedReload(world, collision, input);
        _watcher.Deleted += (_, _) => DebouncedReload(world, collision, input);
        _watcher.Renamed += (_, _) => DebouncedReload(world, collision, input);
        Console.WriteLine($"[scripts] hot reload watching {_scriptsRoot}");
    }

    void DebouncedReload(World world, CollisionWorld collision, InputState input)
    {
        // simple debounce 400ms
        if ((DateTime.UtcNow - _lastCompile).TotalMilliseconds < 600) return;
        Task.Delay(400).ContinueWith(_ =>
        {
            string log;
            bool ok = CompileAndLoad(world, collision, input, out log);
            Console.WriteLine(ok ? $"[scripts] hot reload OK: {log}" : $"[scripts] hot reload FAIL: {log}");
        });
    }

    static List<ISystem> DiscoverSystems(Assembly asm, World world, CollisionWorld collision, InputState input)
    {
        var list = new List<ISystem>();
        foreach (var type in asm.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface) continue;
            if (!typeof(ISystem).IsAssignableFrom(type)) continue;

            // Skip types that require impossible ctor; we will try to create
            ISystem? inst = TryCreate(type, world, collision, input);
            if (inst != null)
            {
                list.Add(inst);
                Console.WriteLine($"[scripts] discovered system: {type.FullName}");
            }
        }
        return list;
    }

    static ISystem? TryCreate(Type type, World world, CollisionWorld collision, InputState input)
    {
        // Try constructors in order of most params first
        var ctors = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length);
        foreach (var ctor in ctors)
        {
            var ps = ctor.GetParameters();
            var args = new object?[ps.Length];
            bool ok = true;
            for (int i = 0; i < ps.Length; i++)
            {
                var p = ps[i];
                if (p.ParameterType == typeof(World)) args[i] = world;
                else if (p.ParameterType == typeof(CollisionWorld)) args[i] = collision;
                else if (p.ParameterType == typeof(InputState)) args[i] = input;
                else if (p.ParameterType.IsAssignableFrom(world.GetType())) args[i] = world;
                else
                {
                    // try service lookup or default
                    if (p.HasDefaultValue) args[i] = p.DefaultValue;
                    else { ok = false; break; }
                }
            }
            if (!ok) continue;
            try { return (ISystem)ctor.Invoke(args); }
            catch (Exception ex) { Console.WriteLine($"[scripts] failed to create {type.Name}: {ex.InnerException?.Message ?? ex.Message}"); }
        }

        // fallback parameterless
        try { return (ISystem)Activator.CreateInstance(type)!; }
        catch { return null; }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
    }
}
