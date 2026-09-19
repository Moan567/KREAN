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
    mapPath = Path.GetFullPath(mapPath);
    if (mapPath.EndsWith("sample.map", StringComparison.OrdinalIgnoreCase) && !File.Exists(mapPath))
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
    scenePath = Path.GetFullPath(scenePath);
    if (!File.Exists(scenePath)) { Console.WriteLine($"Scene not found: '{scenePath}'"); return 1; }
    pendingScene = scenePath;
}
else
{
    var defaultMap = Path.GetFullPath("sample.map");
    if (File.Exists(defaultMap)) session = MapEditorSession.Load(defaultMap);
    else if (File.Exists("sample.map")) session = MapEditorSession.Load("sample.map");
    else session = MapEditorSession.CreateSample(defaultMap);
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
// Visual editor window — 3D viewport + ImGui overlay + TrenchBroom-like brush handling
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
    SelectionRenderer _selRenderer = null!;
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
    bool _leftDragging;
    float _camSpeed = 1f;

    // drag state (TrenchBroom-like)
    bool _isDraggingBrush;
    Vector3 _dragStartCenterQuake;
    Vector3 _dragStartHitQuake;
    Vector3 _dragPlaneNormalQuake;
    Vector3 _dragPlanePointQuake;
    Vector3 _dragCurrentDeltaQuake;
    bool _dragPushedUndo;
    Vector2 _lastMousePos;
    // view/proj cache for picking / selection draw
    Matrix4x4 _lastView, _lastProj;

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
        _selRenderer = new SelectionRenderer(_gl);
        _move = new PlayerMoveSystem(_collision, _input);
        _imgui = new ImGuiController(_gl, _window, _inputCtx);
        _ui = new EditorUI(_session, () => _needsRecompile = true,
            p => { try { _session.Save(p); _needsRecompile = true; _ui.SetStatus($"Saved '{p}'"); } catch (Exception ex) { _ui.SetStatus($"Save failed: {ex.Message}"); } },
            p => { try { var sc = MapCompilerService.Compile(_session.Entities, Path.GetFileNameWithoutExtension(p)); SceneSerializer.Write(sc, p); _ui.SetStatus($"Exported '{p}'"); } catch (Exception ex) { _ui.SetStatus($"Export failed: {ex.Message}"); } });

        foreach (var kb in _inputCtx.Keyboards)
        {
            kb.KeyDown += (_, k, _) => { _input.KeyDown(k); HandleGlobalHotkey(k); };
            kb.KeyUp += (_, k, _) => _input.KeyUp(k);
        }
        foreach (var m in _inputCtx.Mice)
        {
            m.MouseMove += (_, p) => { _input.MouseMove(p); _lastMousePos = p; };
            m.MouseDown += (_, b) =>
            {
                if (b == MouseButton.Right) _rightDragging = true;
                if (b == MouseButton.Left) { _leftDragging = true; HandleLeftDown(); }
            };
            m.MouseUp += (_, b) =>
            {
                if (b == MouseButton.Right) _rightDragging = false;
                if (b == MouseButton.Left) { _leftDragging = false; HandleLeftUp(); }
            };
            m.Scroll += (_, wheel) =>
            {
                if (!ImGui.GetIO().WantCaptureMouse)
                    _camSpeed = Math.Clamp(_camSpeed + wheel.Y * 0.15f, 0.2f, 5f);
                // in face mode scroll extrudes face
                if (_session.Mode == EditMode.Face && _session.SelectedFaceIndex >= 0 && !ImGui.GetIO().WantCaptureMouse)
                {
                    if (MathF.Abs(wheel.Y) > 0.01f)
                    {
                        _session.MoveSelectedFace(wheel.Y * _session.GridSize);
                        _needsRecompile = true;
                        _ui.SetStatus($"Extruded face by {wheel.Y * _session.GridSize:0.##}");
                    }
                }
            };
        }

        _session.Changed += () => _needsRecompile = true;

        Recompile(spawnPlayer: true);
        _window.Title = $"KREAN Map Editor — {_session.FilePath ?? "(unsaved)"}";

        var style = ImGui.GetStyle();
        style.FrameRounding = 4;
        style.WindowRounding = 6;
        style.Colors[(int)ImGuiCol.TitleBgActive] = new Vector4(0.18f, 0.22f, 0.32f, 1f);
    }

    void HandleGlobalHotkey(Key k)
    {
        bool ctrl = _input.IsDown(Key.ControlLeft) || _input.IsDown(Key.ControlRight);
        bool shift = _input.IsDown(Key.ShiftLeft) || _input.IsDown(Key.ShiftRight);
        if (ImGui.GetIO().WantTextInput && k != Key.Z && k != Key.Y && k != Key.S && k != Key.O) return;

        switch (k)
        {
            case Key.S when ctrl && !shift:
                try { _session.Save(); _ui.SetStatus($"Saved '{_session.FilePath}'"); } catch (Exception ex) { _ui.SetStatus(ex.Message); }
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
            case Key.F9:
                _ui.LaunchPlay();
                break;
            // TrenchBroom-like shortcuts
            case Key.Number1 when !ctrl:
                _session.Mode = EditMode.Object; _ui.SetStatus("Mode: Object (move entity/brush group)");
                break;
            case Key.Number2 when !ctrl:
                _session.Mode = EditMode.Brush; _ui.SetStatus("Mode: Brush (move single brush) — click brush to select");
                break;
            case Key.Number3 when !ctrl:
                _session.Mode = EditMode.Face; _ui.SetStatus("Mode: Face (scroll to extrude, or drag face along normal)");
                break;
            case Key.G when !ctrl:
                _session.GridSnapEnabled = !_session.GridSnapEnabled; _ui.SetStatus($"Grid snap: {(_session.GridSnapEnabled? "ON":"OFF")} ({_session.GridSize})");
                break;
            case Key.Comma when !ctrl:
                _session.GridSize = Math.Max(1, _session.GridSize / 2); _ui.SetStatus($"Grid: {_session.GridSize}");
                break;
            case Key.Period when !ctrl:
                _session.GridSize = Math.Min(64, _session.GridSize * 2); _ui.SetStatus($"Grid: {_session.GridSize}");
                break;
            case Key.Escape:
                if (_isDraggingBrush) { _session.Undo(); _needsRecompile = true; _isDraggingBrush = false; _ui.SetStatus("Drag cancelled"); }
                break;
        }

        // Arrow nudge (like TrenchBroom 2D views) — move selected
        if (!ctrl && !ImGui.GetIO().WantTextInput)
        {
            float step = _session.GridSize;
            if (shift) step *= 8;
            Vector3 delta = k switch
            {
                Key.Up => new Vector3(0, step, 0),
                Key.Down => new Vector3(0, -step, 0),
                Key.Left => new Vector3(-step, 0, 0),
                Key.Right => new Vector3(step, 0, 0),
                Key.PageUp => new Vector3(0, 0, step),
                Key.PageDown => new Vector3(0, 0, -step),
                _ => Vector3.Zero
            };
            if (delta != Vector3.Zero)
            {
                _session.NudgeSelected(delta.X, delta.Y, delta.Z);
                _needsRecompile = true;
            }
        }
    }

    void OnUpdate(double dt)
    {
        float d = (float)Math.Min(dt, 0.1);
        _ui.Tick(d);

        if (_needsRecompile)
        {
            _needsRecompile = false;
            Recompile(spawnPlayer: false);
        }

        // handle brush drag while left button held
        if (_isDraggingBrush && _leftDragging)
        {
            UpdateDrag();
        }

        var io = ImGui.GetIO();
        bool allowFly = !io.WantCaptureKeyboard && !io.WantCaptureMouse;
        bool lookActive = _rightDragging && (allowFly || !io.WantCaptureMouse);
        _input.MouseCaptured = lookActive;
        foreach (var m in _inputCtx.Mice)
        {
            if (lookActive) m.Cursor.CursorMode = CursorMode.Disabled;
            else if (!ImGui.GetIO().WantCaptureMouse) m.Cursor.CursorMode = CursorMode.Normal;
        }

        float effectiveDt = d * _camSpeed;
        _move.Update(_world, effectiveDt);
        _input.EndFrame();

        _fpsTimer += dt;
        _frames++;
        if (_fpsTimer >= 0.25) { _fps = (float)(_frames / _fpsTimer); _frames = 0; _fpsTimer = 0; }
    }

    void OnRender(double dt)
    {
        var fb = _window.FramebufferSize;
        _gl.Disable(EnableCap.ScissorTest);
        _renderer.Render(_world, fb.X, fb.Y);
        // capture view/proj for picking + overlay (same as renderer computes)
        CacheViewProj(fb.X, fb.Y);
        // selection outline (TrenchBroom-style)
        _selRenderer.DrawSelection(_session, _lastView, _lastProj);

        _imgui.Update((float)dt);
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

        Vector3 camPos = _world.IsAlive(_player) ? _world.Get<Transform>(_player).Position : Vector3.Zero;
        _ui.Draw(new Vector2(fb.X, fb.Y), _fps, camPos, _scene.Meshes.Count, _scene.Collision.Count);

        _imgui.Render();
    }

    void OnClosing()
    {
        if (_session.Dirty)
        {
            try { _session.SaveCopy("autosave.map"); Console.WriteLine("[editor] autosaved to autosave.map"); } catch { }
        }
        _renderer?.Dispose();
        _selRenderer?.Dispose();
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

    void CacheViewProj(int w, int h)
    {
        float aspect = h > 0 ? w / (float)h : 1f;
        _lastView = Matrix4x4.Identity; _lastProj = Matrix4x4.Identity;
        bool has = false;
        _world.Query<Transform, Camera, PlayerController>((Entity e, ref Transform t, ref Camera cam, ref PlayerController pc) =>
        {
            var eye = t.Position + Vector3.UnitY * pc.EyeOffset;
            var fwd = PlayerController.LookDirection(pc.Yaw, pc.Pitch);
            _lastView = Matrix4x4.CreateLookAt(eye, eye + fwd, Vector3.UnitY);
            _lastProj = PerspectiveGL(cam.FovDegrees * Units.Deg2Rad, aspect, cam.Near, cam.Far);
            has = true;
        });
        if (!has)
        {
            _lastView = Matrix4x4.Identity;
            _lastProj = Matrix4x4.Identity;
        }
    }

    static Matrix4x4 PerspectiveGL(float fovY, float aspect, float near, float far)
    {
        float t = 1f / MathF.Tan(fovY * 0.5f);
        return new Matrix4x4(t / aspect, 0, 0, 0, 0, t, 0, 0, 0, 0, (far + near) / (near - far), -1, 0, 0, 2f * far * near / (near - far), 0);
    }

    bool ScreenToRayQuake(Vector2 mouse, out Vector3 originQ, out Vector3 dirQ)
    {
        originQ = default; dirQ = default;
        if (!_world.IsAlive(_player)) return false;
        var tr = _world.Get<Transform>(_player);
        var pc = _world.Get<PlayerController>(_player);
        var cam = _world.Get<Camera>(_player);
        // engine ray
        Vector3 eye = tr.Position + Vector3.UnitY * pc.EyeOffset;
        Vector3 fwd = PlayerController.LookDirection(pc.Yaw, pc.Pitch);
        Vector3 right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitY));
        Vector3 up = Vector3.Cross(right, fwd);
        var fb = _window.FramebufferSize;
        float aspect = fb.Y > 0 ? fb.X / (float)fb.Y : 1f;
        float tan = MathF.Tan(cam.FovDegrees * Units.Deg2Rad * 0.5f);
        float ndcX = (2f * mouse.X / fb.X - 1f) * aspect * tan;
        float ndcY = (1f - 2f * mouse.Y / fb.Y) * tan;
        Vector3 dirEng = Vector3.Normalize(fwd + right * ndcX + up * ndcY);
        // convert to Quake space
        float s = Units.QuakeToMeters;
        originQ = new Vector3(eye.X / s, -eye.Z / s, eye.Y / s);
        dirQ = Vector3.Normalize(new Vector3(dirEng.X, -dirEng.Z, dirEng.Y));
        return true;
    }

    void HandleLeftDown()
    {
        if (ImGui.GetIO().WantCaptureMouse) return;
        if (_rightDragging) return; // fly look active
        if (!ScreenToRayQuake(_lastMousePos, out var originQ, out var dirQ)) return;

        bool hit = _session.TryPickBrush(originQ, dirQ, out int ei, out int bi, out float t, out int fi);
        if (!hit)
        {
            // click empty: keep selection but stop drag
            return;
        }

        bool shift = _input.IsDown(Key.ShiftLeft) || _input.IsDown(Key.ShiftRight);
        bool ctrl = _input.IsDown(Key.ControlLeft) || _input.IsDown(Key.ControlRight);

        // Selection logic like TrenchBroom
        if (_session.Mode == EditMode.Brush || _session.Mode == EditMode.Face)
        {
            _session.SelectBrush(ei, bi, _session.Mode == EditMode.Face ? fi : -1);
            _ui.SetStatus($"Selected entity {ei} brush {bi}" + (fi >= 0 ? $" face {fi}" : ""));
        }
        else // Object mode: select entity (whole entity)
        {
            _session.Select(ei);
            // if brush entity, also select brush for move in object mode?? Moves whole entity
            _session.SelectBrush(ei, bi, -1); // keep brush index for highlight but object move moves all
            if (!shift) _session.SelectBrush(ei, bi, -1);
            _ui.SetStatus($"Selected entity {ei} ({_session.Entities[ei].ClassName})");
        }
        _needsRecompile = true; // to refresh highlight via Changed

        // start drag: compute drag plane through selection center, normal = view direction inverted? Use plane that faces camera for translation, or face normal for face mode
        Vector3 center = _session.GetSelectedCenter();
        Vector3 planeNormal;
        if (_session.Mode == EditMode.Face && _session.SelectedBrush != null && _session.SelectedFaceIndex >= 0)
        {
            planeNormal = _session.SelectedBrush.Faces[_session.SelectedFaceIndex].Normal;
            // if nearly parallel to view, fall back to view plane
            if (MathF.Abs(Vector3.Dot(planeNormal, dirQ)) < 0.15f) planeNormal = -dirQ;
        }
        else
        {
            // TrenchBroom moves in plane orthogonal to view, or axis-locked if holding X/Y/Z
            bool lockX = _input.IsDown(Key.X);
            bool lockY = _input.IsDown(Key.Y);
            bool lockZ = _input.IsDown(Key.Z);
            if (lockX || lockY || lockZ)
            {
                // drag along axis: plane contains axis and is facing camera
                Vector3 axis = lockX ? Vector3.UnitX : lockY ? Vector3.UnitY : Vector3.UnitZ;
                Vector3 camRight = Vector3.Normalize(Vector3.Cross(dirQ, new Vector3(0,0,1)));
                if (camRight.LengthSquared() < 0.1f) camRight = Vector3.Normalize(Vector3.Cross(dirQ, Vector3.UnitX));
                planeNormal = Vector3.Normalize(Vector3.Cross(axis, camRight));
                if (planeNormal.LengthSquared() < 1e-6f) planeNormal = -dirQ;
            }
            else
            {
                planeNormal = -dirQ;
            }
        }
        _dragPlaneNormalQuake = planeNormal;
        _dragPlanePointQuake = center;
        _dragStartCenterQuake = center;
        // intersect ray with drag plane to get start hit
        if (RayPlaneIntersect(originQ, dirQ, _dragPlanePointQuake, _dragPlaneNormalQuake, out var hitQ))
        {
            _dragStartHitQuake = hitQ;
            _isDraggingBrush = true;
            _dragCurrentDeltaQuake = Vector3.Zero;
            _dragPushedUndo = false;
        }
        else _isDraggingBrush = false;
    }

    void HandleLeftUp()
    {
        if (_isDraggingBrush)
        {
            _isDraggingBrush = false;
            _dragPushedUndo = false;
            _ui.SetStatus($"Moved by {_dragCurrentDeltaQuake.X:0.##},{_dragCurrentDeltaQuake.Y:0.##},{_dragCurrentDeltaQuake.Z:0.##}");
        }
    }

    void UpdateDrag()
    {
        if (!ScreenToRayQuake(_lastMousePos, out var originQ, out var dirQ)) return;
        if (!RayPlaneIntersect(originQ, dirQ, _dragPlanePointQuake, _dragPlaneNormalQuake, out var curHit)) return;
        Vector3 desiredDelta = curHit - _dragStartHitQuake;

        // axis lock during drag (check keys)
        bool lockX = _input.IsDown(Key.X);
        bool lockY = _input.IsDown(Key.Y);
        bool lockZ = _input.IsDown(Key.Z);
        if (lockX) desiredDelta = new Vector3(desiredDelta.X, 0, 0);
        else if (lockY) desiredDelta = new Vector3(0, desiredDelta.Y, 0);
        else if (lockZ) desiredDelta = new Vector3(0, 0, desiredDelta.Z);

        // In face mode, project onto face normal
        if (_session.Mode == EditMode.Face && _session.SelectedBrush != null && _session.SelectedFaceIndex >= 0)
        {
            var n = _session.SelectedBrush.Faces[_session.SelectedFaceIndex].Normal;
            desiredDelta = n * Vector3.Dot(desiredDelta, n);
        }

        if (_session.GridSnapEnabled && _session.GridSize > 0)
            desiredDelta = BrushManipulation.Snap(desiredDelta, _session.GridSize);

        Vector3 incremental = desiredDelta - _dragCurrentDeltaQuake;
        if (incremental.LengthSquared() < 1e-6f) return;
        _dragCurrentDeltaQuake = desiredDelta;

        if (!_dragPushedUndo)
        {
            // push undo once at drag start by calling the move with pushUndo true then reverting incremental? Simpler: we already have method that pushes. We do first move with undo.
            // To capture undo before any movement, we need to push then apply incremental == desiredDelta, but we already moved by 0. So first incremental equals desiredDelta.
            // We'll just call with pushUndo true for this first incremental.
            bool ok = false;
            if (_session.Mode == EditMode.Face) ok = _session.MoveSelectedFace(Vector3.Dot(incremental, _session.SelectedBrush!.Faces[_session.SelectedFaceIndex].Normal), pushUndo: true);
            else if (_session.Mode == EditMode.Brush) ok = _session.TranslateSelectedBrush(incremental, pushUndo: true);
            else ok = _session.TranslateSelectedEntity(incremental, pushUndo: true);
            if (ok) _dragPushedUndo = true;
            else return;
        }
        else
        {
            if (_session.Mode == EditMode.Face)
            {
                float d = Vector3.Dot(incremental, _session.SelectedBrush!.Faces[_session.SelectedFaceIndex].Normal);
                _session.MoveSelectedFace(d, pushUndo: false);
            }
            else if (_session.Mode == EditMode.Brush) _session.TranslateSelectedBrush(incremental, pushUndo: false);
            else _session.TranslateSelectedEntity(incremental, pushUndo: false);
        }
        _needsRecompile = true;
    }

    static bool RayPlaneIntersect(Vector3 origin, Vector3 dir, Vector3 planePoint, Vector3 planeNormal, out Vector3 hit)
    {
        float denom = Vector3.Dot(planeNormal, dir);
        if (MathF.Abs(denom) < 1e-6f) { hit = default; return false; }
        float t = Vector3.Dot(planePoint - origin, planeNormal) / denom;
        if (t < 0) { hit = default; return false; }
        hit = origin + dir * t;
        return true;
    }

    public void Dispose() => _window?.Dispose();
}
