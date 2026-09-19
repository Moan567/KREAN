using System.Numerics;
using KREAN.Core;
using KREAN.Core.Components;
using KREAN.Core.ECS;
using KREAN.Core.Physics;
using KREAN.Core.Scenes;
using KREAN.Runtime.Rendering;
using KREAN.Runtime.Systems;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace KREAN.Runtime;

public sealed class EngineOptions
{
    public string Title { get; init; } = "KREAN";
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;
    public bool VSync { get; init; } = true;
}

/// <summary>Owns the window, GL context, input, ECS world and the main loop.</summary>
public sealed class Engine : IDisposable
{
    readonly EngineOptions _options;
    readonly List<ISystem> _systems = new();

    IWindow _window = null!;
    GL _gl = null!;
    IInputContext _inputContext = null!;
    Renderer _renderer = null!;

    string? _pendingScene;
    double _fpsTimer;
    int _frames;

    public World World { get; } = new();
    public CollisionWorld Collision { get; } = new();
    public InputState Input { get; } = new();
    public SceneData? Scene { get; private set; }

    public Engine(EngineOptions? options = null)
    {
        _options = options ?? new EngineOptions();
        _systems.Add(new PlayerMoveSystem(Collision, Input));
    }

    public void AddSystem(ISystem system) => _systems.Add(system);

    /// <summary>Scene to load as soon as the GL context exists.</summary>
    public void QueueScene(string path) => _pendingScene = path;

    public void Run()
    {
        var options = WindowOptions.Default;
        options.Size = new Silk.NET.Maths.Vector2D<int>(_options.Width, _options.Height);
        options.Title = _options.Title;
        options.VSync = _options.VSync;
        options.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3));

        _window = Window.Create(options);
        _window.Load += OnLoad;
        _window.Update += OnUpdate;
        _window.Render += OnRender;
        _window.Closing += OnClosing;
        _window.Run();
    }

    // ------------------------------------------------------------------ lifecycle

    void OnLoad()
    {
        _gl = GL.GetApi(_window);
        _inputContext = _window.CreateInput();

        foreach (var keyboard in _inputContext.Keyboards)
        {
            keyboard.KeyDown += (_, key, _) => OnKeyDown(key);
            keyboard.KeyUp += (_, key, _) => Input.KeyUp(key);
        }
        foreach (var mouse in _inputContext.Mice)
        {
            mouse.MouseMove += (_, position) => Input.MouseMove(position);
            mouse.MouseDown += (_, _) => { if (!Input.MouseCaptured) SetMouseCapture(true); };
        }

        _renderer = new Renderer(_gl);
        SetMouseCapture(true);

        if (_pendingScene != null) LoadScene(_pendingScene);
        foreach (var system in _systems) system.Init(World);
    }

    void OnUpdate(double dt)
    {
        float d = MathF.Min((float)dt, 0.1f);
        foreach (var system in _systems) system.Update(World, d);
        Input.EndFrame();
    }

    void OnRender(double dt)
    {
        var size = _window.FramebufferSize;
        _renderer.Render(World, size.X, size.Y);

        _frames++;
        _fpsTimer += dt;
        if (_fpsTimer >= 0.5)
        {
            _window.Title = $"{_options.Title}  |  {_frames / _fpsTimer:0} fps";
            _frames = 0;
            _fpsTimer = 0;
        }
    }

    void OnClosing()
    {
        _renderer?.Dispose();
        _inputContext?.Dispose();
    }

    void OnKeyDown(Key key)
    {
        Input.KeyDown(key);
        if (key == Key.Escape) SetMouseCapture(!Input.MouseCaptured);
    }

    void SetMouseCapture(bool captured)
    {
        Input.MouseCaptured = captured;
        Input.ResetMouse();
        foreach (var mouse in _inputContext.Mice)
            mouse.Cursor.CursorMode = captured ? CursorMode.Disabled : CursorMode.Normal;
    }

    // ------------------------------------------------------------------ scene loading

    void LoadScene(string path)
    {
        Scene = SceneSerializer.Read(path);
        SceneSerializer.Populate(World, Scene);
        Collision.Load(Scene.Collision);
        _renderer.UploadMeshes(Scene.Meshes);
        SpawnPlayer();

        Console.WriteLine($"[engine] loaded '{Scene.Name}': {Scene.Entities.Count} entities, " +
                          $"{Scene.Meshes.Count} meshes, {Scene.Collision.Count} collision brushes");
    }

    void SpawnPlayer()
    {
        var pos = new Vector3(0f, 2f, 0f);
        float yaw = 0f;

        World.Query<PlayerSpawn, Transform>((Entity e, ref PlayerSpawn spawn, ref Transform t) =>
        {
            pos = t.Position;
            yaw = spawn.Yaw;
        });

        // Nudge upward if the spawn point is inside the floor.
        for (int i = 0; i < 16 && Collision.Trace(pos, pos, PlayerMoveSystem.Half).StartSolid; i++)
            pos.Y += 4f * Units.QuakeToMeters;

        var player = World.CreateEntity();
        World.Add(player, new EntityName { Value = "player" });
        World.Add(player, new Transform { Position = pos, Rotation = Quaternion.Identity, Scale = Vector3.One });
        World.Add(player, new Velocity());
        World.Add(player, new Camera { FovDegrees = 100f, Near = 0.05f, Far = 500f });
        World.Add(player, new PlayerController { Yaw = yaw, EyeOffset = 18f * Units.QuakeToMeters });
        World.Add(player, new Transient());
    }

    public void Dispose() => _window?.Dispose();
}
