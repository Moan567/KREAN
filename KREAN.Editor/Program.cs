using System.Globalization;
using System.Numerics;
using ImGuiNET;
using KREAN.Core;
using KREAN.Core.Components;
using KREAN.Core.ECS;
using KREAN.Core.Physics;
using KREAN.Core.Scenes;
using KREAN.Editor;
using KREAN.MapCompiler;
using KREAN.Runtime;
using KREAN.Runtime.Rendering;
using KREAN.Runtime.Systems;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

// ------------------------------------------------------------ CLI
string? mapPath = null;
string? scenePath = null;
bool compileOnly = false;
bool showHelp = false;

foreach (var arg in args)
{
    if (arg == "--compile-only") compileOnly = true;
    else if (arg == "--help" || arg == "-h") showHelp = true;
    else if (arg == "--sample") mapPath = "sample.map";
    else if (arg.StartsWith("-")) { Console.WriteLine($"Unknown flag: {arg}"); showHelp = true; }
    else
    {
        var ext = Path.GetExtension(arg).ToLowerInvariant();
        if (ext == ".map") mapPath = arg;
        else if (ext == ".scene.json" || ext == ".json") scenePath = arg;
        else mapPath = arg;
    }
}

if (showHelp) { PrintHelp(); return 0; }

MapEditorSession? session = null;
string? pendingScene = null;

if (mapPath != null)
{
    if (mapPath == "sample.map" && !File.Exists(mapPath))
        session = MapEditorSession.CreateSample(mapPath);
    else if (File.Exists(mapPath))
    {
        try { session = MapEditorSession.Load(mapPath); }
        catch (Exception ex) { Console.WriteLine($"Failed to read '{mapPath}': {ex.Message}"); return 1; }
    }
    else
    {
        Console.WriteLine($"Map not found: '{mapPath}' – creating sample");
        session = MapEditorSession.CreateSample(mapPath);
    }
    if (session != null && compileOnly)
    {
        var outPath = Path.ChangeExtension(mapPath, ".scene.json");
        MapCompilerService.CompileToFile(mapPath!, outPath);
        Console.WriteLine($"Compiled {mapPath} -> {outPath}");
        return 0;
    }
}
else if (scenePath != null)
{
    if (!File.Exists(scenePath)) { Console.WriteLine($"Scene not found: '{scenePath}'"); return 1; }
    pendingScene = scenePath;
}
else
{
    if (File.Exists("sample.map")) session = MapEditorSession.Load("sample.map");
    else session = MapEditorSession.CreateSample("sample.map");
}

if (session != null)
{
    using var win = new MapEditorWindow(session);
    win.Run();
}
else if (pendingScene != null)
{
    using var eng = new Engine(new EngineOptions { Title = "KREAN – Scene Preview" });
    eng.QueueScene(pendingScene);
    eng.Run();
}
else { PrintHelp(); return 1; }

return 0;

static void PrintHelp()
{
    Console.WriteLine("""
        KREAN.MapEditor — Real visual .map editor (ImGui)
        Usage:
          KREAN.Editor [level.map]              open .map (reads .map files, visual editing)
          KREAN.Editor level.scene.json         preview compiled scene (read-only)
          KREAN.Editor --sample                 create & open sample.map
          KREAN.Editor --compile-only level.map compile to .scene.json and exit
        """);
}

// ============================================================================
// Visual editor window — 3D viewport + ImGui overlay
// ============================================================================
sealed class MapEditorWindow : IDisposable
{
    readonly MapEditorSession _session;
    readonly World _world = new();
    readonly CollisionWorld _collision = new();
    readonly InputState _input = new();

    IWindow _window = null!;
    GL _gl = null!;
    IInputContext _inputCtx = null!;
    Renderer _renderer = null!;
    ImGuiController _imgui = null!;
    EditorUI _ui = null!;
    PlayerMoveSystem _move = null!;
    Entity _player;

    SceneData _scene = new();
    double _fpsTimer;
    int _frames;
    float _fps;
    bool _needsRecompile;
    bool _rightDragging;
    float _camSpeed = 1f;

    public MapEditorWindow(MapEditorSession s) => _session = s;

    public void Run()
    {
        var opts = WindowOptions.Default;
        opts.Size = new Silk.NET.Maths.Vector2D<int>(1600, 900);
        opts.Title = $"KREAN Map Editor — {_session.FilePath ?? "(unsaved)"}";
        opts.VSync = true;
        opts.ShouldSwapAutomatically = true;
        opts.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3));
        opts.WindowBorder = WindowBorder.Resizable;
        opts.WindowState = WindowState.Normal;
        _window = Window.Create(opts);
        _window.Load += OnLoad;
        _window.Update += OnUpdate;
        _window.Render += OnRender;
        _window.Closing += OnClosing;
        _window.Run();
    }

    void OnLoad()
    {
        _gl = GL.GetApi(_window);
        _inputCtx = _window.CreateInput();
        _renderer = new Renderer(_gl);
        _move = new PlayerMoveSystem(_collision, _input);
        _imgui = new ImGuiController(_gl, _window, _inputCtx);
        _ui = new EditorUI(_session, () => _needsRecompile = true,
            p => { try { _session.Save(p); _needsRecompile = true; _ui.SetStatus($"Saved '{p}'"); } catch (Exception ex) { _ui.SetStatus($"Save failed: {ex.Message}"); } },
            p => { try { var sc = MapCompilerService.Compile(_session.Entities, Path.GetFileNameWithoutExtension(p)); SceneSerializer.Write(sc, p); _ui.SetStatus($"Exported '{p}'"); } catch (Exception ex) { _ui.SetStatus($"Export failed: {ex.Message}"); } });

        // input for movement (we still feed to _input, but will gate with ImGui WantCapture)
        foreach (var kb in _inputCtx.Keyboards)
        {
            kb.KeyDown += (_, k, _) => { _input.KeyDown(k); HandleGlobalHotkey(k); };
            kb.KeyUp += (_, k, _) => _input.KeyUp(k);
        }
        foreach (var m in _inputCtx.Mice)
        {
            m.MouseMove += (_, p) => _input.MouseMove(p);
            m.MouseDown += (_, b) =>
            {
                if (b == MouseButton.Right) _rightDragging = true;
            };
            m.MouseUp += (_, b) =>
            {
                if (b == MouseButton.Right) _rightDragging = false;
            };
            m.Scroll += (_, wheel) =>
            {
                // use scroll to adjust fly speed when not over imgui
                if (!ImGui.GetIO().WantCaptureMouse)
                    _camSpeed = Math.Clamp(_camSpeed + wheel.Y * 0.15f, 0.2f, 5f);
            };
        }

        _session.Changed += () => _needsRecompile = true;

        Recompile(spawnPlayer: true);
        _window.Title = $"KREAN Map Editor — {_session.FilePath ?? "(unsaved)"}";

        // style
        var style = ImGui.GetStyle();
        style.FrameRounding = 4;
        style.WindowRounding = 6;
        style.Colors[(int)ImGuiCol.TitleBgActive] = new Vector4(0.18f, 0.22f, 0.32f, 1f);
    }

    void HandleGlobalHotkey(Key k)
    {
        bool ctrl = _input.IsDown(Key.ControlLeft) || _input.IsDown(Key.ControlRight);
        bool shift = _input.IsDown(Key.ShiftLeft) || _input.IsDown(Key.ShiftRight);
        // don't handle when ImGui is typing
        if (ImGui.GetIO().WantTextInput && k != Key.Z && k != Key.Y && k != Key.S && k != Key.O) return;

        switch (k)
        {
            case Key.S when ctrl && !shift:
                try { _session.Save(); _ui.SetStatus($"Saved '{_session.FilePath}'"); } catch (Exception ex) { _ui.SetStatus(ex.Message); }
                break;
            case Key.S when ctrl && shift:
                // save as handled via UI
                break;
            case Key.O when ctrl:
                _ui.SetStatus("Use File → Open");
                break;
            case Key.Z when ctrl && !shift:
                if (_session.CanUndo) { _session.Undo(); _needsRecompile = true; _ui.SetStatus("Undo"); }
                break;
            case Key.Y when ctrl:
            case Key.Z when ctrl && shift:
                if (_session.CanRedo) { _session.Redo(); _needsRecompile = true; _ui.SetStatus("Redo"); }
                break;
            case Key.F5:
                _needsRecompile = true; _ui.SetStatus("Recompile requested");
                break;
            case Key.F6:
                {
                    string p = _session.FilePath != null ? Path.ChangeExtension(_session.FilePath, ".scene.json") : "out.scene.json";
                    var sc = MapCompilerService.Compile(_session.Entities, Path.GetFileNameWithoutExtension(p));
                    SceneSerializer.Write(sc, p);
                    _ui.SetStatus($"Exported '{p}'");
                    break;
                }
        }
    }

    void OnUpdate(double dt)
    {
        float d = (float)Math.Min(dt, 0.1);
        _ui.Tick(d);

        // handle recompile queued
        if (_needsRecompile)
        {
            _needsRecompile = false;
            Recompile(spawnPlayer: false);
        }

        // decide whether to allow fly input
        var io = ImGui.GetIO();
        bool allowFly = !io.WantCaptureKeyboard && !io.WantCaptureMouse;
        // right-drag mode: when holding right mouse, we capture even if over UI? we treat right drag as look
        bool lookActive = _rightDragging && allowFly || (_rightDragging && io.WantCaptureMouse == false);
        // We'll use InputState.MouseCaptured flag to control PlayerMoveSystem look
        // Instead of hidden cursor, we just feed delta when right-dragging
        // Temporarily override: if not lookActive, zero delta
        Vector2 savedDelta = _input.MouseDelta;
        if (!lookActive)
        {
            // zero out delta so camera doesn't spin while interacting with UI
            // we need to set private — but InputState.MouseDelta is public with private set, we can only clear via EndFrame; so just prevent PlayerMoveSystem from seeing delta by resetting before update
            // Hack: use reflection or add method; easiest: if not lookActive, we temporarily clear via ResetMouse which also clears delta
            // but that loses delta. Better: we just not call _move.Update with delta
            // We'll snapshot and restore
        }

        // Actually PlayerMoveSystem reads _input.MouseDelta directly; we can simply zero it when not lookActive
        // Since MouseDelta has private set, we can't set it. We need to expose a way to suppress. Quick fix: add a public field to suppress or make MouseDelta settable internally via method.
        // For now, we call _input.ResetMouse() before update when not lookActive, then restore? That would discard.
        // Instead, add logic: if !lookActive, call _input.ResetMouse() before move, then restore after? Let's just add a helper to InputState to multiply delta.
        // Simplest: we add a property InputState.MouseCaptured to gate look; PlayerMoveSystem already checks MouseCaptured.
        // So we set MouseCaptured = lookActive
        _input.MouseCaptured = lookActive;
        // Also control cursor
        foreach (var m in _inputCtx.Mice)
        {
            if (lookActive) m.Cursor.CursorMode = CursorMode.Disabled;
            else if (!ImGui.GetIO().WantCaptureMouse) m.Cursor.CursorMode = CursorMode.Normal;
        }

        // apply speed multiplier via PlayerMoveSystem? not exposed; we'll scale dt
        float effectiveDt = d * _camSpeed;
        // Choose whether to use noclip forced? Editor camera is always noclip via PlayerController.NoClip=true
        _move.Update(_world, effectiveDt);

        // V toggle noclip is handled inside PlayerMoveSystem via WasPressed V — still works

        _input.EndFrame(); // clears MouseDelta and pressed

        // fps
        _fpsTimer += dt;
        _frames++;
        if (_fpsTimer >= 0.25) { _fps = (float)(_frames / _fpsTimer); _frames = 0; _fpsTimer = 0; }
    }

    void OnRender(double dt)
    {
        // 1) render 3D scene to backbuffer (full window) — with depth
        var fb = _window.FramebufferSize;
        _gl.Disable(EnableCap.ScissorTest);
        _renderer.Render(_world, fb.X, fb.Y);

        // 2) render ImGui overlay
        _imgui.Update((float)dt);
        // dockspace
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos);
        ImGui.SetNextWindowSize(viewport.Size);
        ImGui.SetNextWindowViewport(viewport.ID);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));
        ImGuiWindowFlags hostFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavFocus | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.MenuBar;
        ImGui.Begin("DockHost", hostFlags);
        ImGui.PopStyleVar(3);
        uint dockId = ImGui.GetID("MainDock");
        ImGui.DockSpace(dockId, new Vector2(0, 0), ImGuiDockNodeFlags.PassthruCentralNode);
        ImGui.End();

        // build UI panels (they will dock)
        Vector3 camPos = _world.IsAlive(_player) ? _world.Get<Transform>(_player).Position : Vector3.Zero;
        _ui.Draw(new Vector2(fb.X, fb.Y), _fps, camPos, _scene.Meshes.Count, _scene.Collision.Count);

        // central viewport help text if no panels hovered
        _imgui.Render();
    }

    void OnClosing()
    {
        if (_session.Dirty)
        {
            try { _session.SaveCopy("autosave.map"); Console.WriteLine("[editor] autosaved to autosave.map"); } catch { }
        }
        _renderer?.Dispose();
        _imgui?.Dispose();
        _inputCtx?.Dispose();
    }

    void Recompile(bool spawnPlayer = false)
    {
        Vector3? keepPos = null;
        float keepYaw = 0, keepPitch = 0;
        if (!spawnPlayer && !_player.IsNull && _world.IsAlive(_player))
        {
            keepPos = _world.Get<Transform>(_player).Position;
            var pc = _world.Get<PlayerController>(_player);
            keepYaw = pc.Yaw; keepPitch = pc.Pitch;
        }

        string name = _session.FilePath != null ? Path.GetFileNameWithoutExtension(_session.FilePath) : "map";
        _scene = MapCompilerService.Compile(_session.Entities, name);

        // rebuild world
        var toDestroy = _world.AllEntities().ToList();
        foreach (var e in toDestroy) _world.Destroy(e);
        SceneSerializer.Populate(_world, _scene);
        _collision.Load(_scene.Collision);
        _renderer.UploadMeshes(_scene.Meshes);

        if (spawnPlayer || keepPos == null) SpawnPlayer();
        else SpawnPlayerAt(keepPos.Value, keepYaw, keepPitch);
    }

    void SpawnPlayer()
    {
        var pos = new Vector3(0f, 2f, 0f);
        float yaw = 0f;
        _world.Query<PlayerSpawn, Transform>((Entity e, ref PlayerSpawn sp, ref Transform tr) => { pos = tr.Position; yaw = sp.Yaw; });
        SpawnPlayerAt(pos, yaw, 0f);
    }

    void SpawnPlayerAt(Vector3 pos, float yaw, float pitch)
    {
        for (int i = 0; i < 16 && _collision.Trace(pos, pos, PlayerMoveSystem.Half).StartSolid; i++)
            pos.Y += 4f * Units.QuakeToMeters;
        _player = _world.CreateEntity();
        _world.Add(_player, new EntityName { Value = "editor_camera" });
        _world.Add(_player, new Transform { Position = pos, Rotation = Quaternion.Identity, Scale = Vector3.One });
        _world.Add(_player, new Velocity());
        _world.Add(_player, new Camera { FovDegrees = 75f, Near = 0.05f, Far = 800f });
        _world.Add(_player, new PlayerController { Yaw = yaw, Pitch = pitch, EyeOffset = 0f, NoClip = true });
        _world.Add(_player, new Transient());
    }

    public void Dispose() => _window?.Dispose();
}
