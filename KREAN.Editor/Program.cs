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
// Professional editor window — smooth FPS fly, gizmo, ergonomic shortcuts
// ============================================================================
enum EditorTool { Select, Move, Rotate }

sealed class MapEditorWindow : IDisposable
{
    readonly MapEditorSession _session;
    readonly World _world = new();
    readonly CollisionWorld _collision = new();
    readonly InputState _input = new();
    readonly EditorCameraController _camCtrl = new();

    IWindow _window = null!;
    GL _gl = null!;
    IInputContext _inputCtx = null!;
    Renderer _renderer = null!;
    SelectionRenderer _selRenderer = null!;
    EditorGizmo _gizmo = null!;
    ImGuiController _imgui = null!;
    EditorUI _ui = null!;
    AssetHotReload? _hotReload;
    Entity _player;

    SceneData _scene = new();
    double _fpsTimer;
    int _frames;
    float _fps;
    bool _needsRecompile;
    bool _rightDragging;
    bool _leftDragging;
    bool _middleDragging;
    bool _baseLayoutBuilt;
    float _autosaveTimer;
    const float AutosaveInterval = 180f; // 3 minutes
    const int MaxAutosaveBackups = 10;

    // drag state (professional — gizmo + plane)
    bool _isDraggingBrush;
    Vector3 _dragStartCenterQuake;
    Vector3 _dragStartHitQuake;
    Vector3 _dragPlaneNormalQuake;
    Vector3 _dragPlanePointQuake;
    Vector3 _dragCurrentDeltaQuake;
    bool _dragPushedUndo;
    GizmoAxis _dragAxis = GizmoAxis.None;
    bool _duplicateOnDrag;
    bool _isDraggingVertex;
    Vector3 _vertexDragStartHit;
    Vector3 _vertexDragCurrentDelta;
    bool _isDraggingEdge;
    Vector3 _edgeDragStartHit;
    Vector3 _edgeDragCurrentDelta;
    // scale/rotate gizmo drag
    bool _isGizmoScaling;
    bool _isGizmoRotating;
    Vector3 _gizmoDragStart;
    float _gizmoRotateStartYaw;
    Vector3 _gizmoScaleStartSize;

    // TrenchBroom-style brush creation (draw-to-create in Brush tool)
    bool _isCreatingBrush;
    Vector3 _createAnchorQuake;
    Vector3 _createCurrentQuake;
    float _createPlaneZ;
    float _createHeight;
    bool _createHasDrag;
    Vector3 _createPlaneNormal;
    Vector3 _createPlanePoint;
    bool _createWallMode;
    Vector2 _lastMousePos;
    Matrix4x4 _lastView, _lastProj;

    // Hover ghost — shows where a new brush would appear before you click (Hammer block preview)
    bool _ghostValid;
    Vector3 _ghostMin, _ghostMax;
    float _ghostPlaneZ;
    Vector3 _ghostPlaneNormal;
    Vector3 _ghostPlanePoint;
    bool _ghostWallMode;

    public bool IsCreatingBrush => _isCreatingBrush;

    public void GetBrushPreview(out Vector3 min, out Vector3 max)
    {
        if (!_isCreatingBrush) { min = max = Vector3.Zero; return; }
        ComputeCreateBounds(out min, out max);
    }

    public MapEditorWindow(MapEditorSession s) => _session = s;

    public void Run()
    {
        var opts = WindowOptions.Default;
        opts.Size = new Silk.NET.Maths.Vector2D<int>(1600, 900);
        opts.Title = $"KREAN Map Editor — {_session.FilePath ?? "(unsaved)"}  [Brush: drag to draw | RMB Look | WASD Fly | B/Q/1/2/3 Tools]";
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
        _gizmo = new EditorGizmo(_gl);
        _imgui = new ImGuiController(_gl, _window, _inputCtx);
        _ui = new EditorUI(_session, () => _needsRecompile = true,
            p => { try { _session.Save(p); _needsRecompile = true; _ui.SetStatus($"Saved '{p}'"); } catch (Exception ex) { _ui.SetStatus($"Save failed: {ex.Message}"); } },
            p => { try { var sc = MapCompilerService.Compile(_session.Entities, Path.GetFileNameWithoutExtension(p)); SceneSerializer.Write(sc, p); _ui.SetStatus($"Exported '{p}'"); } catch (Exception ex) { _ui.SetStatus($"Export failed: {ex.Message}"); } });
        try{ _hotReload = new AssetHotReload(_session, ()=> _needsRecompile=true); _hotReload.Start(); }catch{}

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
                if (b == MouseButton.Middle) _middleDragging = true;
            };
            m.MouseUp += (_, b) =>
            {
                if (b == MouseButton.Right) _rightDragging = false;
                if (b == MouseButton.Left) { _leftDragging = false; HandleLeftUp(); }
                if (b == MouseButton.Middle) _middleDragging = false;
            };
            m.Scroll += (_, wheel) =>
            {
                // Height control while drawing a new brush — wheel adjusts thickness before you release
                if (_isCreatingBrush && MathF.Abs(wheel.Y) > 0.01f && !ImGui.GetIO().WantCaptureMouse)
                {
                    float step = Math.Max(_session.GridSize, 8f);
                    _createHeight = Math.Clamp(_createHeight + wheel.Y * step, step, 4096f);
                    _ui.SetStatus($"Brush height: {_createHeight:0}  (wheel ±{step:0})");
                    return;
                }
                bool isFace = _session.Mode == EditMode.Face && _session.SelectedFaceIndex >= 0 && !ImGui.GetIO().WantCaptureMouse;
                if (isFace && MathF.Abs(wheel.Y) > 0.01f)
                {
                    _session.MoveSelectedFace(wheel.Y * _session.GridSize);
                    _needsRecompile = true;
                    _ui.SetStatus($"Extruded face by {wheel.Y * _session.GridSize:0.##} (grid { _session.GridSize})");
                }
                else if (!ImGui.GetIO().WantCaptureMouse)
                {
                    _camCtrl.AdjustSpeed(wheel.Y);
                }
            };
        }

        _session.Changed += () => _needsRecompile = true;

        Recompile(spawnPlayer: true);
        _camCtrl.SetFromPlayer(_player, _world);
        _window.Title = $"KREAN Map Editor — {_session.FilePath ?? "(unsaved)"}";

        // load editor settings (persisted)
        try{ var s=EditorSettings.Load(_session.FilePath); s.ApplyTo(_session, _ui); Console.WriteLine($"[settings] loaded grid {s.GridSize} snap {s.GridSnapEnabled} layer {s.ActiveLayer}"); }catch{}
        // Quake style is fully owned by EditorUI.EnsureTheme — don't re-round here
        _ui.SetStatus("Brush tool (B): drag empty space to draw • click to select • RMB+WASD fly • F frame • Q select");
    }

    void HandleGlobalHotkey(Key k)
    {
        bool ctrl = _input.IsDown(Key.ControlLeft) || _input.IsDown(Key.ControlRight);
        bool shift = _input.IsDown(Key.ShiftLeft) || _input.IsDown(Key.ShiftRight);
        bool alt = _input.IsDown(Key.AltLeft) || _input.IsDown(Key.AltRight);
        bool text = ImGui.GetIO().WantTextInput;

        // text input gating — still allow global combos
        if (!text || k == Key.Z || k == Key.Y || k == Key.S || k == Key.O || k == Key.D || k == Key.F)
        {
            switch (k)
            {
                case Key.S when ctrl && !shift:
                    try { _session.Save(); _ui.SetStatus($"Saved '{_session.FilePath}'"); } catch (Exception ex) { _ui.SetStatus(ex.Message); }
                    break;
                case Key.S when ctrl && shift:
                    break;
                case Key.O when ctrl:
                    _ui.SetStatus("File → Open...");
                    break;
                case Key.Z when ctrl && !shift:
                    if (_session.CanUndo) { _session.Undo(); _needsRecompile = true; _ui.SetStatus("Undo"); }
                    break;
                case Key.Y when ctrl:
                case Key.Z when ctrl && shift:
                    if (_session.CanRedo) { _session.Redo(); _needsRecompile = true; _ui.SetStatus("Redo"); }
                    break;
                case Key.D when ctrl && !shift:
                {
                    if (_session.Selected == null && !_session.IsMultiMode) break;
                    int beforeB = _session.MultiSelectedBrushes.Count;
                    int beforeE = _session.MultiSelectedEntities.Count;
                    _session.DuplicateHighlighted();
                    _needsRecompile = true;
                    if (beforeB>0 || beforeE>0) _ui.SetStatus($"Duplicated {beforeB+beforeE} highlighted (Ctrl+D)");
                    else if (_session.Mode==EditMode.Brush && _session.SelectedBrush!=null) _ui.SetStatus("Duplicated brush (Ctrl+D)");
                    else _ui.SetStatus("Duplicated entity (Ctrl+D)");
                    break;
                }
                case Key.D when ctrl && shift: // Ctrl+Shift+D force entity duplicate even in Brush mode
                    if (_session.Selected != null) { _session.DuplicateSelected(); _needsRecompile = true; _ui.SetStatus("Duplicated entity (Ctrl+Shift+D)"); }
                    break;
                case Key.X when ctrl && alt:
                    if (_session.SelectedBrush != null) { _session.FlipSelected(GizmoAxis.X); _needsRecompile = true; _ui.SetStatus("Flipped X"); }
                    break;
                case Key.Y when ctrl && alt:
                    if (_session.SelectedBrush != null) { _session.FlipSelected(GizmoAxis.Y); _needsRecompile = true; _ui.SetStatus("Flipped Y"); }
                    break;
                case Key.Z when ctrl && alt:
                    if (_session.SelectedBrush != null) { _session.FlipSelected(GizmoAxis.Z); _needsRecompile = true; _ui.SetStatus("Flipped Z"); }
                    break;
                case Key.R when ctrl && !alt:
                    if (_session.SelectedBrush != null) { _session.RotateSelected90(); _needsRecompile = true; _ui.SetStatus("Rotated 90°"); }
                    break;
                case Key.H when ctrl:
                    if (_session.SelectedBrush != null) { if (_session.HollowSelected()) { _needsRecompile = true; _ui.SetStatus("Hollowed"); } else _ui.SetStatus("Hollow failed — too small or not box"); }
                    break;
                case Key.G when ctrl && !shift:
                    if (_session.IsMultiMode) { if (_session.GroupHighlightedBrushes()) { _needsRecompile = true; _ui.SetStatus("Grouped highlighted — now linked"); } }
                    else _ui.SetStatus("Ctrl+G needs Ctrl+highlighted brushes/entities");
                    break;
                case Key.G when ctrl && shift:
                    if (_session.Selected != null && _session.Selected.Brushes.Count > 1) { if (_session.UnlinkSelectedBrushes()) { _needsRecompile = true; _ui.SetStatus("Unlinked — brushes are now independent"); } }
                    else _ui.SetStatus("Unlink: select an entity with >1 brushes");
                    break;
                case Key.Delete:
                case Key.Backspace when !text:
                    if (_session.Selected != null) { _session.DeleteAtSelection(); _needsRecompile = true; _ui.SetStatus("Deleted"); }
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
                case Key.F12:
                    _ui.ResetLayout(); _ui.SetStatus("Layout reset to default (Scene/Materials/2D Views)");
                    break;
                case Key.F9:
                    _ui.LaunchPlay();
                    break;
                case Key.F when ctrl:
                    _ui.ShowSearchReport = !_ui.ShowSearchReport;
                    _ui.SetStatus(_ui.ShowSearchReport? "Search / Entity Report (Ctrl+F)" : "Search closed");
                    break;
                case Key.F when !ctrl && !text:
                    FrameSelection();
                    break;
                case Key.G when !ctrl && !text:
                    // _tool = EditorTool.Move; _ui.SetStatus("Tool: Move (G) — drag or use gizmo, X/Y/Z lock, Shift temp nosnap, Alt duplicate");
                    _ui.SetStatus("Tool: Move (G) — drag or use gizmo, X/Y/Z lock, Shift temp nosnap, Alt duplicate");
                    break;
                case Key.R when !ctrl && !text:
                    _ui.SetStatus("Rotate: drag yaw or use inspector slider");
                    break;
                case Key.C when ctrl && !shift:
                    if (_session.CopySelected()) _ui.SetStatus("Copied (Ctrl+C) — Ctrl+V to paste"); else _ui.SetStatus("Copy failed — nothing selected");
                    break;
                case Key.X when ctrl && !alt && !shift:
                    if (_session.CutSelected()) { _needsRecompile=true; _ui.SetStatus("Cut (Ctrl+X)"); } else _ui.SetStatus("Cut failed");
                    break;
                case Key.V when ctrl && !shift:
                    if (_session.PasteAt()) { _needsRecompile=true; _ui.SetStatus("Pasted (Ctrl+V)"); } else _ui.SetStatus("Paste failed — clipboard empty");
                    break;
                case Key.Enter when _session.Mode==EditMode.Clip || _session.ClipPoints.Count>=2:
                    {
                        bool keepBoth = ctrl;
                        bool keepFront = !shift;
                        if (_session.ClipPoints.Count<2) { _ui.SetStatus("Clip needs 2-3 points (X + click)"); break; }
                        // if 2 points, use view dir for plane
                        if (_session.ClipPoints.Count==2 && ScreenToRayQuake(_lastMousePos, out _, out var vd)) {
                            _session.TryGetClipPlaneFromView(vd, out var pp, out var nn);
                            // replace points with derived 3rd? just execute via 2-point view path - manually set
                            // Try view-based normal
                            if (_session.TryGetClipPlaneFromView(vd, out var p2, out var n2)) {
                                // temporarily add third point to make TryGetClipPlane work: we call ExecuteClip which handles 2
                            }
                        }
                        if (_session.ExecuteClip(keepFront, keepBoth)) { _needsRecompile=true; _ui.SetStatus(keepBoth?"Split both (Ctrl+Enter)":"Clipped " + (keepFront?"front":"back")); }
                        else _ui.SetStatus("Clip failed — plane didn't intersect or points collinear");
                    }
                    break;
                case Key.X when !ctrl && !alt && !text:
                    _session.Mode = EditMode.Clip; _ui.SetStatus("Clip Tool (X): click 2-3 points on brush, Enter=front Shift+Enter=back Ctrl+Enter=both Esc=cancel");
                    break;
                case Key.Escape:
                    if (_session.ClipPoints.Count>0) { _session.ClearClipPoints(); _ui.SetStatus("Clip points cleared (Esc)"); }
                    else if (_isCreatingBrush) { CancelBrushCreation(); }
                    else if (_isDraggingVertex) { _session.Undo(); _needsRecompile = true; _isDraggingVertex = false; _dragPushedUndo = false; _ui.SetStatus("Vertex drag cancelled (Esc)"); }
                    else if (_isDraggingBrush) { _session.Undo(); _needsRecompile = true; _isDraggingBrush = false; _gizmo.Active = GizmoAxis.None; _ui.SetStatus("Drag cancelled (Esc)"); }
                    else if (_session.MultiSelectedBrushes.Count > 0) { _session.ClearMultiBrush(); _needsRecompile = true; _ui.SetStatus("Cleared multi-selection (Esc)"); }
                    else { _gizmo.Active = GizmoAxis.None; }
                    break;
            }
        }

        if (text) return;

        if (k == Key.A && shift && ctrl && !text && !_rightDragging && !_input.MouseCaptured) { _ui.ShowArchDialog(); }

        // CSG shortcuts
        if (ctrl && shift && !text)
        {
            if (k==Key.S){ if(_session.CsgSubtract()){ _needsRecompile=true; _ui.SetStatus("CSG Subtract"); } else _ui.SetStatus("CSG Subtract needs 2 brushes"); }
            else if(k==Key.I){ if(_session.CsgIntersect()){ _needsRecompile=true; _ui.SetStatus("CSG Intersect"); } else _ui.SetStatus("CSG Intersect failed"); }
            else if(k==Key.U){ if(_session.CsgUnion()){ _needsRecompile=true; _ui.SetStatus("CSG Union"); } else _ui.SetStatus("Union failed"); }
        }
        // gizmo mode
        if(!text && !ctrl && !shift)
        {
            if(k==Key.T){ _gizmo.Mode=GizmoMode.Translate; _ui.SetStatus("Gizmo: Translate (T)"); }
            else if(k==Key.R && !ctrl){ _gizmo.Mode=GizmoMode.Rotate; _ui.SetStatus("Gizmo: Rotate (R) — drag axis to rotate 15° snap"); }
            else if(k==Key.Y){ _gizmo.Mode=GizmoMode.Scale; _ui.SetStatus("Gizmo: Scale (Y) — drag axis to scale"); }
        }
        // mode / tool shortcuts (TrenchBroom-style: B = brush tool, Q = select)
        switch (k)
        {
            case Key.Number1 when !ctrl: _session.Mode = EditMode.Object; _ui.SetStatus("Tool: Select (1/Q) — pick + move whole entity"); break;
            case Key.Number2 when !ctrl: _session.Mode = EditMode.Brush; _ui.SetStatus("Tool: Brush (2/B) — drag empty space to draw, click to select"); break;
            case Key.Number3 when !ctrl: _session.Mode = EditMode.Face; _ui.SetStatus("Tool: Face (3) — pick face → arrows/wheel extrude"); break;
            case Key.Number4 when !ctrl: _session.Mode = EditMode.Vertex; _ui.SetStatus("Tool: Vertex (4) — pick corner → arrows/drag move corner"); break;
            case Key.Number5 when !ctrl: _session.Mode = EditMode.Edge; _ui.SetStatus("Tool: Edge (5) — pick edge, drag"); break;
            case Key.B when !ctrl: _session.Mode = EditMode.Brush; _ui.SetStatus("Tool: Brush (B) — drag empty space to draw, click to select"); break;
            case Key.E when !ctrl && !shift: _session.Mode = EditMode.Entity; _ui.SetStatus("Tool: Entity (E) — click to place. Select class in Entity palette"); break;
            case Key.Q when !ctrl && _rightDragging: break; // fly down while freelooking
            case Key.Q when !ctrl: _session.Mode = EditMode.Object; _ui.SetStatus("Tool: Select (Q) — pick + move whole entity"); break;
            case Key.Tab when !ctrl: _session.Mode = (EditMode)(((int)_session.Mode + 1) % 7); _ui.SetStatus($"Mode: {_session.Mode} (Tab)"); break;
            case Key.Q when !ctrl && !_rightDragging && !text: /* handled as fly down in controller */ break;
            case Key.E when !ctrl && !_rightDragging && !text: break;
            case Key.G when !ctrl: _session.GridSnapEnabled = !_session.GridSnapEnabled; _ui.SetStatus($"Grid snap: {(_session.GridSnapEnabled? "ON":"OFF")} ({_session.GridSize}) — hold Shift to temp disable"); break;
            case Key.Comma when !ctrl:
            case Key.Minus when !ctrl: _session.GridSize = Math.Max(1, _session.GridSize / 2); _ui.SetStatus($"Grid: {_session.GridSize}"); break;
            case Key.Period when !ctrl:
            case Key.Equal when !ctrl: _session.GridSize = Math.Min(64, _session.GridSize * 2); _ui.SetStatus($"Grid: {_session.GridSize}"); break;
            case Key.V when !ctrl:
                // toggle noclip via controller? keep PlayerController toggle
                if (_world.IsAlive(_player)) { ref var pc = ref _world.Get<PlayerController>(_player); pc.NoClip = !pc.NoClip; _ui.SetStatus(pc.NoClip ? "Noclip ON (V)" : "Noclip OFF"); }
                break;
        }

        // nudge / extrude / vertex — arrows behave per mode (Hammer / TrenchBroom)
        if (!ctrl)
        {
            float step = _session.GridSize;
            if (shift) step *= 8;
            bool isArrow = k is Key.Up or Key.Down or Key.Left or Key.Right or Key.PageUp or Key.PageDown;
            if (isArrow)
            {
                // Face mode: extrude selected face along its normal — Up/Right/PgUp push out, Down/Left/PgDn pull in
                if (_session.Mode == EditMode.Face && _session.SelectedBrush != null && _session.SelectedFaceIndex >= 0)
                {
                    float dir = k switch { Key.Up or Key.Right or Key.PageUp => 1f, Key.Down or Key.Left or Key.PageDown => -1f, _ => 0f };
                    if (dir != 0 && _session.MoveSelectedFace(dir * step)) { _needsRecompile = true; _ui.SetStatus($"Extruded face {(dir*step):0.#} along normal (Shift 8x)"); }
                }
                // Vertex mode: move selected corner
                else if (_session.Mode == EditMode.Vertex && _session.SelectedVertexIndex >= 0)
                {
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
                    if (delta != Vector3.Zero && _session.MoveSelectedVertex(delta)) { _needsRecompile = true; _ui.SetStatus($"Moved vertex {delta.X:0.#},{delta.Y:0.#},{delta.Z:0.#}"); }
                }
                else
                {
                    // Object / Brush: move whole selection. Also allow Ctrl-less extrude via Alt+Arrow for brushes.
                    bool extrude = alt; // Alt+Arrow extrudes brush along its dominant face
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
                        bool ok;
                        if (extrude && _session.SelectedBrush != null)
                        {
                            ok = _session.ExtrudeSelectedBrush(delta);
                            if (ok) _ui.SetStatus($"Extruded brush {delta.X:0.#},{delta.Y:0.#},{delta.Z:0.#} (Alt+Arrow)");
                        }
                        else
                        {
                            ok = _session.NudgeSelected(delta.X, delta.Y, delta.Z);
                            if (ok) _ui.SetStatus($"Nudged {delta.X:0.#},{delta.Y:0.#},{delta.Z:0.#} (Shift 8x, Alt extrude in Brush mode)");
                        }
                        if (ok) _needsRecompile = true;
                    }
                }
            }
        }
    }

    void FrameSelection()
    {
        if (_session.Selected == null) { _ui.SetStatus("Nothing to frame"); return; }
        var targetQ = _session.GetSelectedCenter();
        float s = Units.QuakeToMeters;
        var targetEng = new Vector3(targetQ.X * s, targetQ.Z * s, -targetQ.Y * s);
        _camCtrl.FocusOn(targetEng, _player, _world, 8f);
        _ui.SetStatus($"Framed selection at {targetQ.X:0},{targetQ.Y:0},{targetQ.Z:0}");
    }

    void OnUpdate(double dt)
    {
        float d = (float)Math.Min(dt, 0.1);
        _ui.Tick(d);

        if (_needsRecompile)
        {
            _needsRecompile = false;
            Recompile(spawnPlayer: false);
            _camCtrl.SetFromPlayer(_player, _world);
        }

        if (_isDraggingBrush && _leftDragging) UpdateDrag();
        if (_isDraggingVertex && _leftDragging) UpdateVertexDrag();
        if (_isDraggingEdge && _leftDragging) UpdateEdgeDrag();
        if (_isCreatingBrush && _leftDragging) UpdateBrushCreation();
        else _ghostValid = false;

        // autosave / backup (every AutosaveInterval, only if dirty)
        _autosaveTimer += d;
        if (_autosaveTimer >= AutosaveInterval && _session.Dirty && !_isDraggingBrush && !_isDraggingVertex && !_isDraggingEdge && !_isCreatingBrush)
        {
            _autosaveTimer = 0f;
            try
            {
                _session.SaveCopy("autosave.map");
                string backupDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_session.FilePath ?? "autosave.map")) ?? ".", "autosave_backups");
                Directory.CreateDirectory(backupDir);
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string baseName = Path.GetFileNameWithoutExtension(_session.FilePath ?? "autosave");
                string backup = Path.Combine(backupDir, $"{baseName}_{stamp}.map");
                _session.SaveCopy(backup);
                // prune old backups
                var files = Directory.GetFiles(backupDir, $"{baseName}_*.map").OrderBy(f=>f).ToList();
                while(files.Count > MaxAutosaveBackups){ try{ File.Delete(files[0]); }catch{} files.RemoveAt(0); }
                _ui.SetStatus($"Autosaved {backup} (+autosave.map)");
                Console.WriteLine($"[autosave] {backup}");
            } catch (Exception ex){ Console.WriteLine($"[autosave] failed: {ex.Message}"); }
        }

        // any LMB cursor drag (brush/vertex/face/create) must freeze camera — left takes priority over RMB look
        bool isDragging = _leftDragging || _isDraggingBrush || _isDraggingVertex || _isDraggingEdge || _isCreatingBrush;
        var io = ImGui.GetIO();
        bool allowFly = !io.WantCaptureKeyboard && !io.WantCaptureMouse;
        bool lookActive = _rightDragging && (allowFly || !io.WantCaptureMouse);
        if (_rightDragging && !_middleDragging) lookActive = true;
        if (isDragging) lookActive = false;
        _input.MouseCaptured = lookActive;
        foreach (var m in _inputCtx.Mice)
        {
            // Fix cursor vanishing: always restore Normal when not freelooking, even over ImGui
            m.Cursor.CursorMode = lookActive ? CursorMode.Disabled : CursorMode.Normal;
            // Keep OS cursor visible for ImGui
            m.Cursor.CursorMode = m.Cursor.CursorMode;
        }
        // Ensure ImGui doesn't hide cursor when not captured
        if (!lookActive) ImGui.GetIO().ConfigFlags &= ~ImGuiConfigFlags.NoMouse;

        // update gizmo hover when not dragging and not freelooking — keep handle size stable on screen
        if (!_isDraggingBrush && !_isDraggingVertex && !lookActive && !io.WantCaptureMouse)
        {
            var centerQ = _session.GetSelectedCenter();
            float s = Units.QuakeToMeters;
            var centerEng = new Vector3(centerQ.X * s, centerQ.Z * s, -centerQ.Y * s);
            Vector3 camPosG = _world.IsAlive(_player) ? _world.Get<Transform>(_player).Position : Vector3.Zero;
            float dist = (centerEng - camPosG).Length();
            _gizmo.HandleLength = Math.Clamp(dist * 0.13f, 0.55f, 2.8f);
            _gizmo.PlaneSize = _gizmo.HandleLength * 0.30f;
            _gizmo.UpdateHover(centerEng, _lastView, _lastProj, _lastMousePos, new Vector2(_window.Size.X, _window.Size.Y));
        }
        else if (lookActive) _gizmo.UpdateHover(Vector3.Zero, _lastView, _lastProj, new Vector2(-9999,-9999), new Vector2(_window.Size.X, _window.Size.Y));

        // fully freeze camera while resizing/moving geometry — prevents drift up
        if (isDragging)
        {
            // zero any residual velocity so camera doesn't keep drifting up after you release E/Z
            _camCtrl.Freeze();
            // still consume input delta so it doesn't accumulate
            _input.EndFrame();
            _fpsTimer += dt; _frames++; if (_fpsTimer >= 0.25) { _fps = (float)(_frames / _fpsTimer); _frames=0; _fpsTimer=0; }
            return;
        }
        _camCtrl.Update(_world, _player, _input, d, lookActive, allowFlyKeys: true);

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
        CacheViewProj(fb.X, fb.Y);
        _selRenderer.DrawSelection(_session, _lastView, _lastProj);
        _selRenderer.DrawClipPreview(_session, _lastView, _lastProj);
        if (_isCreatingBrush)
        {
            ComputeCreateBounds(out var cmin, out var cmax);
            _selRenderer.DrawBrushPreview(cmin, cmax, _lastView, _lastProj);
        }
        // gizmo overlay at selection center — scaled with distance so it stays readable (TrenchBroom)
        if (_session.Selected != null)
        {
            var centerQ = _session.GetSelectedCenter();
            float s = Units.QuakeToMeters;
            var centerEng = new Vector3(centerQ.X * s, centerQ.Z * s, -centerQ.Y * s);
            Vector3 camPosGizmo = _world.IsAlive(_player) ? _world.Get<Transform>(_player).Position : Vector3.Zero;
            float dist = (centerEng - camPosGizmo).Length();
            _gizmo.HandleLength = Math.Clamp(dist * 0.13f, 0.55f, 2.8f);
            _gizmo.PlaneSize = _gizmo.HandleLength * 0.30f;
            var hi = _gizmo.Active != GizmoAxis.None ? _gizmo.Active : _gizmo.Hovered;
            if (_session.Mode != EditMode.Face && _session.Mode != EditMode.Vertex && !_rightDragging) _gizmo.Draw(centerEng, _lastView, _lastProj, hi);
        }

        _imgui.Update((float)dt);
        var viewport = ImGui.GetMainViewport();
        float menuH = ImGui.GetFrameHeight();
        const float toolbarH = 30f;
        const float statusH = 20f;
        // inset dock so toolbar/status don't cover docked panel title bars (the bug you saw)
        Vector2 dockPos = viewport.Pos + new Vector2(0, menuH + toolbarH);
        Vector2 dockSize = new Vector2(viewport.Size.X, Math.Max(0, viewport.Size.Y - menuH - toolbarH - statusH));
        ImGui.SetNextWindowPos(dockPos);
        ImGui.SetNextWindowSize(dockSize);
        ImGui.SetNextWindowViewport(viewport.ID);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0, 0, 0, 0));
        ImGuiWindowFlags hostFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavFocus | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.MenuBar | ImGuiWindowFlags.NoBackground;
        ImGui.Begin("DockHost", hostFlags);
        ImGui.PopStyleVar(3);
        uint dockId = ImGui.GetID("MainDock");
        ImGui.DockSpace(dockId, new Vector2(0, 0), ImGuiDockNodeFlags.PassthruCentralNode);
        ImGui.PopStyleColor();
        ImGui.End();

        // base layout: build dock on first run or reset
        bool wantsReset = _ui.ResetLayoutRequested;
        bool needBase = !_baseLayoutBuilt && !File.Exists("imgui.ini") && !File.Exists(Path.Combine(AppContext.BaseDirectory,"imgui.ini"));
        if (wantsReset || needBase)
        {
            BuildBaseLayout(dockId, dockSize);
            _baseLayoutBuilt = true;
            if (wantsReset) _ui.ClearResetFlag();
        }

        Vector3 camPos = _world.IsAlive(_player) ? _world.Get<Transform>(_player).Position : Vector3.Zero;
        _ui.Draw(new Vector2(fb.X, fb.Y), _fps, camPos, _scene.Meshes.Count, _scene.Collision.Count);

        // contextual hint overlay (professional)
        DrawContextHint();

        _imgui.Render();
    }

    void BuildBaseLayout(uint dockId, Vector2 vpSize)
    {
        // Base layout is defined by each window's SetNextWindowSize/Pos with ImGuiCond_FirstUseEver
        // in EditorUI.cs (Scene Browser 320x500 left, Inspector 320x500 right,
        // Materials 260x300, Top/Front/Side 320x260 bottom-tabbed) plus Viewport center.
        // Clearing imgui.ini and resetting flags in EditorUI.ResetLayout() restores that.
        // Keep this as no-op but mark built.
        try { ImGui.LoadIniSettingsFromDisk(""); } catch {}
    }

    void DrawContextHint()
    {
        var io = ImGui.GetIO();
        if (io.WantCaptureMouse || _rightDragging) return;
        var dl = ImGui.GetForegroundDrawList();
        string hint = "";
        if (_isCreatingBrush)
        {
            ComputeCreateBounds(out var cmin, out var cmax);
            var size = cmax - cmin;
            hint = $"New brush {size.X:0}x{size.Y:0}x{size.Z:0} — release to create [wheel height { _createHeight:0}] [Esc cancel] [Shift nosnap]";
        }
        else if (_isDraggingVertex) hint = $"Vertex {_session.SelectedVertexIndex} Δ{_vertexDragCurrentDelta.X:0.#},{_vertexDragCurrentDelta.Y:0.#},{_vertexDragCurrentDelta.Z:0.#} — drag or arrows, X/Y/Z lock, Esc cancel";
        else if (_gizmo.Hovered != GizmoAxis.None && !_isDraggingBrush) hint = $"Gizmo { _gizmo.Hovered} — drag to move (X/Y/Z lock, Shift nosnap, Alt duplicate)";
        else if (_session.Mode == EditMode.Clip) {
            string pts = _session.ClipPoints.Count==0? "click brush to place 1st point" : _session.ClipPoints.Count==1? "click 2nd point" : _session.ClipPoints.Count==2? "click 3rd point or Enter for 2-pt vertical" : "ready — Enter front / Shift+Enter back / Ctrl+Enter split";
            hint = $"Clip (X): {pts} • { _session.ClipPoints.Count}/3 • Esc clear";
        }
        else if (_isDraggingBrush) hint = $"Moving { _dragAxis}  Δ{_dragCurrentDeltaQuake.X:0.#},{_dragCurrentDeltaQuake.Y:0.#},{_dragCurrentDeltaQuake.Z:0.#}  [Shift nosnap] [X/Y/Z lock] [Esc cancel]";
        else if (_session.Mode == EditMode.Face) hint = "Face (3): LMB pick face • Drag/wheel/↑↓→←/PgUpDn to extrude • [ ] grid";
        else if (_session.Mode == EditMode.Vertex) hint = "Vertex (4): LMB pick corner • Arrows / PgUpDn move corner • X/Y/Z lock • drag";
        else if (_session.Mode == EditMode.Brush) hint = "Brush (B): drag empty space to draw • Alt+Arrows extrude • Click/Drag move";
        else hint = "Select (Q): LMB pick entity • Drag to move • F frame • B for Brush tool";
        if (!string.IsNullOrEmpty(hint))
        {
            var pos = new Vector2(10, io.DisplaySize.Y - 48);
            dl.AddRectFilled(pos - new Vector2(6,4), pos + ImGui.CalcTextSize(hint) + new Vector2(12,8), ImGui.ColorConvertFloat4ToU32(new Vector4(0,0,0,0.55f)), 4);
            dl.AddText(pos, ImGui.ColorConvertFloat4ToU32(new Vector4(1,1,1,0.92f)), hint);
        }
    }

    void OnClosing()
    {
        if (_session.Dirty)
        {
            try { _session.SaveCopy("autosave.map"); Console.WriteLine("[editor] autosaved to autosave.map"); } catch { }
        }
        try{ var s=new EditorSettings(); s.CaptureFrom(_session,_ui); s.Save(_session.FilePath); Console.WriteLine("[settings] saved"); }catch{}
        try{ _hotReload?.Dispose(); }catch{}
        _renderer?.Dispose();
        _selRenderer?.Dispose();
        _gizmo?.Dispose();
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
        var visEnts = _session.Entities.Where(e=>_session.IsEntityVisible(e)).ToList();
        _scene = MapCompilerService.Compile(visEnts, name);

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
        float s = Units.QuakeToMeters;
        originQ = new Vector3(eye.X / s, -eye.Z / s, eye.Y / s);
        dirQ = Vector3.Normalize(new Vector3(dirEng.X, -dirEng.Z, dirEng.Y));
        return true;
    }

    void HandleLeftDown()
    {
        if (ImGui.GetIO().WantCaptureMouse) return;
        if (_rightDragging) return;
        // preserve camera — clicking must never teleport it to the brush
        Vector3 _savedCamPos = Vector3.Zero; float _savedYaw = 0, _savedPitch = 0; bool _hasCam = false;
        if (_world.IsAlive(_player)) { _savedCamPos = _world.Get<Transform>(_player).Position; var _pc = _world.Get<PlayerController>(_player); _savedYaw = _pc.Yaw; _savedPitch = _pc.Pitch; _hasCam = true; }
        void RestoreCam() { if (_hasCam && _world.IsAlive(_player)) { ref var _tr = ref _world.Get<Transform>(_player); _tr.Position = _savedCamPos; ref var _pc2 = ref _world.Get<PlayerController>(_player); _pc2.Yaw = _savedYaw; _pc2.Pitch = _savedPitch; _camCtrl.SetFromPlayer(_player, _world); _camCtrl.Freeze(); } }

        // Clip tool: place points on brush surface (X mode)
        if (_session.Mode == EditMode.Clip)
        {
            if (ScreenToRayQuake(_lastMousePos, out var corig, out var cdir))
            {
                Vector3 hitPt;
                bool hasHit = false;
                if (_session.TryPickBrush(corig, cdir, out _, out _, out float ct, out _))
                { hitPt = corig + cdir * ct; hasHit = true; }
                else if (RayHorizontalPlane(corig, cdir, 0, out var gp)) { hitPt = gp; hasHit = true; }
                else hitPt = default;
                if (hasHit)
                {
                    _session.AddClipPoint(hitPt);
                    _ui.SetStatus($"Clip point {_session.ClipPoints.Count}/3 at {hitPt.X:0},{hitPt.Y:0},{hitPt.Z:0} — {( _session.ClipPoints.Count<2? "need 1-2 more": _session.ClipPoints.Count==2? "ready (Enter front / Shift+Enter back / Ctrl+Enter both)":"ready (Enter)")} ");
                    RestoreCam();
                    return;
                }
            }
        }
        // Entity tool: place point entity at hit
        if (_session.Mode == EditMode.Entity)
        {
            if (ScreenToRayQuake(_lastMousePos, out var eorig, out var edir))
            {
                Vector3 place = eorig + edir * 256f;
                if (_session.TryPickBrush(eorig, edir, out _, out _, out float et, out _)) place = eorig + edir * et;
                else if (RayHorizontalPlane(eorig, edir, 0, out var ep)) place = ep;
                // use entity filter from UI or default
                string cls = "light";
                // try to get from palette state? for now use info_player_start if light not selected; peek UI filter via reflection? just use light
                // We'll read DefaultTexture hack? Instead use knownClasses first
                var field = typeof(EditorUI).GetField("_entityFilter", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (field != null) { var v = field.GetValue(_ui) as string; if(!string.IsNullOrWhiteSpace(v)) cls=v; }
                _session.AddEntity(cls, place);
                _needsRecompile = true;
                _ui.SetStatus($"Placed {cls} at {place.X:0},{place.Y:0},{place.Z:0} (Entity E)");
                RestoreCam();
                return;
            }
        }
        // Edge mode: pick edge
        if (_session.Mode == EditMode.Edge && _session.SelectedBrush != null)
        {
            if (ScreenToRayQuake(_lastMousePos, out var eorig2, out var edir2) && _session.TryPickEdge(eorig2, edir2, out int edgeIdx, out _))
            {
                _session.SelectEdge(_session.SelectedIndex, _session.SelectedBrushIndex, edgeIdx);
                _needsRecompile = true;
                _ui.SetStatus($"Selected edge {edgeIdx} — drag or arrows to move (5 = Edge)");
                RestoreCam();
                StartEdgeDrag(eorig2, edir2, edgeIdx);
                return;
            }
        }
        // vertex mode: pick corner first (higher priority than gizmo)
        if (_session.Mode == EditMode.Vertex && _session.SelectedBrush != null)
        {
            if (ScreenToRayQuake(_lastMousePos, out var vorig, out var vdir) && _session.TryPickVertex(vorig, vdir, out int vi, out _))
            {
                _session.SelectVertex(_session.SelectedIndex, _session.SelectedBrushIndex, vi);
                _needsRecompile = true;
                _ui.SetStatus($"Selected vertex {vi} — arrows or drag to move (4 = Vertex mode)");
                RestoreCam();
                StartVertexDrag(vorig, vdir, vi);
                return;
            }
        }
        // brush mode: face handle for direct size change (TrenchBroom 3D face drag without entering Face mode)
        if (_session.Mode == EditMode.Brush && _session.SelectedBrush != null)
        {
            if (ScreenToRayQuake(_lastMousePos, out var forg, out var fdir) && _session.TryPickFaceHandle(forg, fdir, out int fh, out _))
            {
                _session.SelectBrush(_session.SelectedIndex, _session.SelectedBrushIndex, fh);
                // stay in Brush but treat drag as face extrude — switch visually to Face for handles
                _session.Mode = EditMode.Face;
                _needsRecompile = true;
                _ui.SetStatus($"Face {fh} handle — drag/wheel/arrows to resize");
                RestoreCam();
                StartDrag(viewDir: fdir);
                return;
            }
        }
        // face mode: pick handle or face
        if (_session.Mode == EditMode.Face && _session.SelectedBrush != null)
        {
            if (ScreenToRayQuake(_lastMousePos, out var forg2, out var fdir2) && _session.TryPickFaceHandle(forg2, fdir2, out int fh2, out _))
            {
                _session.SelectBrush(_session.SelectedIndex, _session.SelectedBrushIndex, fh2);
                _needsRecompile = true;
                RestoreCam();
                StartDrag(viewDir: fdir2);
                return;
            }
        }
        // gizmo priority (move existing selection)
        if (_gizmo.Hovered != GizmoAxis.None && _session.Selected != null && !_isCreatingBrush)
        {
            _dragAxis = _gizmo.Hovered;
            _gizmo.Active = _dragAxis;
            StartDragForAxis(_dragAxis);
            return;
        }
        if (!ScreenToRayQuake(_lastMousePos, out var originQ, out var dirQ)) return;

        bool hit = _session.TryPickBrush(originQ, dirQ, out int ei, out int bi, out float t, out int fi);
        if (!hit)
        {
            // TrenchBroom brush tool: drag on empty space draws a new brush.
            if (_session.Mode == EditMode.Brush)
            {
                StartBrushCreation(originQ, dirQ, t);
                return;
            }
            return;
        }

        bool ctrlDown = _input.IsDown(Key.ControlLeft) || _input.IsDown(Key.ControlRight);
        // Ctrl+click multi-brush highlight (TrenchBroom)
        if (ctrlDown && _session.Mode == EditMode.Brush)
        {
            // toggle into multi set; also keep primary
            bool wasMulti = _session.IsMultiSelected(ei, bi);
            if (wasMulti && _session.MultiSelectedBrushes.Count == 1 && _session.SelectedIndex == ei && _session.SelectedBrushIndex == bi)
            {
                // untoggling the primary leaves empty — clear
                _session.ClearMultiBrush(); _session.SelectBrush(ei, bi, -1);
                _ui.SetStatus($"Multi: removed brush {bi}");
            }
            else
            {
                if (_session.MultiSelectedBrushes.Count == 0 && _session.SelectedBrush != null)
                    _session.MultiSelectedBrushes.Add((_session.SelectedIndex, _session.SelectedBrushIndex));
                _session.ToggleMultiBrush(ei, bi);
                _session.SelectBrush(ei, bi, _session.Mode == EditMode.Face ? fi : -1);
                // keep the newly picked in multi set as well if we toggled on
                if (!wasMulti) _session.MultiSelectedBrushes.Add((ei, bi));
                _ui.SetStatus($"Multi: { _session.MultiSelectedBrushes.Count} brushes — Ctrl+click to toggle, Esc to clear");
            }
            _needsRecompile = true;
            // don't start drag on multi-toggle click alone
            return;
        }
        // cursor drag of highlighted group — if you click a brush that's already in the multi set, keep the group and drag all with cursor
        bool hitIsInMultiBrush = _session.IsMultiSelected(ei, bi);
        bool hitIsInMultiEntity = _session.IsMultiEntitySelected(ei);
        bool keepMultiForDrag = (hitIsInMultiBrush && _session.MultiSelectedBrushes.Count > 0) || (hitIsInMultiEntity && _session.MultiSelectedEntities.Count > 0);
        if (!keepMultiForDrag)
            _session.ClearAllMulti();
        if (_session.Mode == EditMode.Brush || _session.Mode == EditMode.Face)
        {
            // keep multi set intact when dragging a member
            var keepSet = new HashSet<(int,int)>(_session.MultiSelectedBrushes);
            _session.SelectBrush(ei, bi, _session.Mode == EditMode.Face ? fi : -1);
            if (keepMultiForDrag) { _session.MultiSelectedBrushes.Clear(); foreach(var k in keepSet) _session.MultiSelectedBrushes.Add(k); _session.MultiSelectedBrushes.Add((ei,bi)); }
            _ui.SetStatus(keepMultiForDrag ? $"Dragging { _session.MultiSelectedBrushes.Count} highlighted with cursor — release to place" : $"Selected entity {ei} brush {bi}" + (fi >= 0 ? $" face {fi}" : "") + " — Ctrl+click to multi, 4=Vertex, Alt+drag dup");
        }
        else
        {
            var keepEnt = new HashSet<int>(_session.MultiSelectedEntities);
            bool wasMultiEnt = keepMultiForDrag;
            _session.Select(ei);
            _session.SelectBrush(ei, bi, -1);
            if (wasMultiEnt) { _session.MultiSelectedEntities.Clear(); foreach(var k in keepEnt) _session.MultiSelectedEntities.Add(k); _session.MultiSelectedEntities.Add(ei); _session.Select(ei); }
            _ui.SetStatus(wasMultiEnt ? $"Dragging { _session.MultiSelectedEntities.Count} highlighted entities with cursor" : $"Selected entity {ei} ({_session.Entities[ei].ClassName}) — Ctrl+click to multi");
        }
        _needsRecompile = true;
        // never let a brush click teleport the camera — restore
        if (_hasCam && _world.IsAlive(_player))
        {
            ref var _tr = ref _world.Get<Transform>(_player);
            _tr.Position = _savedCamPos;
            ref var _pc = ref _world.Get<PlayerController>(_player);
            _pc.Yaw = _savedYaw; _pc.Pitch = _savedPitch;
            _camCtrl.SetFromPlayer(_player, _world);
            _camCtrl.Freeze();
        }
        // start drag — check gizmo not active, use view plane
        _dragAxis = GizmoAxis.None;
        // duplicate on Alt — TrenchBroom style clone-on-drag (Alt held at drag start)
        _duplicateOnDrag = _input.IsDown(Key.AltLeft) || _input.IsDown(Key.AltRight);
        if (_duplicateOnDrag && (_session.Selected != null || _session.IsMultiMode))
        {
            if (_session.IsMultiMode)
            {
                int cntB = _session.MultiSelectedBrushes.Count;
                int cntE = _session.MultiSelectedEntities.Count;
                _session.DuplicateHighlighted();
                _needsRecompile = true;
                _ui.SetStatus($"Duplicated {cntB+cntE} highlighted (Alt+drag)");
            }
            else if (_session.Mode == EditMode.Brush && _session.SelectedBrush != null)
            {
                int srcBrush = _session.SelectedBrushIndex;
                _session.DuplicateBrush(srcBrush);
                _session.TranslateSelectedBrush(new Vector3(0, 0, 0), pushUndo: false);
                _needsRecompile = true;
                _ui.SetStatus("Duplicated brush (Alt+drag) — Ctrl+click to multi");
            }
            else
            {
                _session.DuplicateSelected(); _needsRecompile = true;
                _ui.SetStatus("Duplicated entity (Alt+drag)");
            }
        }
        StartDrag(viewDir: dirQ);
    }

    void StartDragForAxis(GizmoAxis axis)
    {
        if (!ScreenToRayQuake(_lastMousePos, out var originQ, out var dirQ)) return;
        Vector3 center = _session.GetSelectedCenter();
        Vector3 planeNormal;
        if (axis == GizmoAxis.Screen) planeNormal = -dirQ;
        else if (axis == GizmoAxis.XY) planeNormal = new Vector3(0,0,1);
        else if (axis == GizmoAxis.XZ) planeNormal = new Vector3(0,1,0);
        else if (axis == GizmoAxis.YZ) planeNormal = new Vector3(1,0,0);
        else
        {
            Vector3 axisDir = axis == GizmoAxis.X ? Vector3.UnitX : axis == GizmoAxis.Y ? Vector3.UnitY : Vector3.UnitZ;
            Vector3 camRight = Vector3.Normalize(Vector3.Cross(dirQ, new Vector3(0,0,1)));
            if (camRight.LengthSquared() < 0.1f) camRight = Vector3.Normalize(Vector3.Cross(dirQ, Vector3.UnitX));
            planeNormal = Vector3.Normalize(Vector3.Cross(axisDir, camRight));
            if (planeNormal.LengthSquared() < 1e-6f) planeNormal = -dirQ;
        }
        _dragPlaneNormalQuake = planeNormal;
        _dragPlanePointQuake = center;
        _dragStartCenterQuake = center;
        _dragAxis = axis;
        _duplicateOnDrag = _input.IsDown(Key.AltLeft) || _input.IsDown(Key.AltRight);
        if (RayPlaneIntersect(originQ, dirQ, _dragPlanePointQuake, _dragPlaneNormalQuake, out var hitQ))
        {
            _dragStartHitQuake = hitQ;
            _isDraggingBrush = true;
            _dragCurrentDeltaQuake = Vector3.Zero;
            _dragPushedUndo = false;
            _session.BeginUndoGroup("move");
        }
    }

    void StartDrag(Vector3 viewDir)
    {
        Vector3 center = _session.GetSelectedCenter();
        Vector3 planeNormal;
        if (_session.Mode == EditMode.Face && _session.SelectedBrush != null && _session.SelectedFaceIndex >= 0)
        {
            planeNormal = _session.SelectedBrush.Faces[_session.SelectedFaceIndex].Normal;
            if (MathF.Abs(Vector3.Dot(planeNormal, viewDir)) < 0.15f) planeNormal = -viewDir;
        }
        else planeNormal = -viewDir;
        _dragPlaneNormalQuake = planeNormal;
        _dragPlanePointQuake = center;
        _dragStartCenterQuake = center;
        _dragAxis = GizmoAxis.None;
        if (!ScreenToRayQuake(_lastMousePos, out var o, out var d)) return;
        if (RayPlaneIntersect(o, d, _dragPlanePointQuake, _dragPlaneNormalQuake, out var hitQ))
        {
            _dragStartHitQuake = hitQ;
            _isDraggingBrush = true;
            _dragCurrentDeltaQuake = Vector3.Zero;
            _dragPushedUndo = false;
            _session.BeginUndoGroup("move");
        }
    }

    void HandleLeftUp()
    {
        if (_isCreatingBrush)
        {
            FinishBrushCreation(cancelled: false);
        }
        bool hadDrag = _isDraggingEdge || _isDraggingVertex || _isDraggingBrush;
        Vector3 totalDelta = _edgeDragCurrentDelta.LengthSquared()> _vertexDragCurrentDelta.LengthSquared() ? _edgeDragCurrentDelta : _vertexDragCurrentDelta;
        if(_dragCurrentDeltaQuake.LengthSquared()>totalDelta.LengthSquared()) totalDelta=_dragCurrentDeltaQuake;
        if (_isDraggingEdge)
        {
            _isDraggingEdge=false; _dragPushedUndo=false;
            if(_edgeDragCurrentDelta.LengthSquared()>1e-6f) _ui.SetStatus($"Moved edge {_edgeDragCurrentDelta.X:0.#},{_edgeDragCurrentDelta.Y:0.#},{_edgeDragCurrentDelta.Z:0.#}");
        }
        if (_isDraggingVertex)
        {
            _isDraggingVertex = false;
            _dragPushedUndo = false;
            if (_vertexDragCurrentDelta.LengthSquared() > 1e-6f)
                _ui.SetStatus($"Moved vertex {_vertexDragCurrentDelta.X:0.#},{_vertexDragCurrentDelta.Y:0.#},{_vertexDragCurrentDelta.Z:0.#}");
        }
        if (_isDraggingBrush)
        {
            _isDraggingBrush = false;
            _dragPushedUndo = false;
            _gizmo.Active = GizmoAxis.None;
            _dragAxis = GizmoAxis.None;
            if (_dragCurrentDeltaQuake.LengthSquared() > 1e-6f)
                _ui.SetStatus($"Moved by {_dragCurrentDeltaQuake.X:0.##},{_dragCurrentDeltaQuake.Y:0.##},{_dragCurrentDeltaQuake.Z:0.##}  (grid { _session.GridSize})");
        }
        if(hadDrag)
        {
            if(totalDelta.LengthSquared()>1e-6f) _session.EndUndoGroup(true);
            else _session.EndUndoGroup(false);
            _edgeDragCurrentDelta=Vector3.Zero; _vertexDragCurrentDelta=Vector3.Zero; _dragCurrentDeltaQuake=Vector3.Zero;
        }
    }

    void StartVertexDrag(Vector3 originQ, Vector3 dirQ, int vertexIndex)
    {
        if (!_session.TryGetSelectedVertex(out var pos)) return;
        // drag along view plane through vertex
        _dragPlaneNormalQuake = -dirQ;
        _dragPlanePointQuake = pos;
        _vertexDragStartHit = pos;
        // also snap start hit via ray
        if (RayPlaneIntersect(originQ, dirQ, _dragPlanePointQuake, _dragPlaneNormalQuake, out var hit))
            _vertexDragStartHit = hit;
        _vertexDragCurrentDelta = Vector3.Zero;
        _isDraggingVertex = true;
        _dragPushedUndo = false;
        _session.BeginUndoGroup("vertex");
    }

    void StartEdgeDrag(Vector3 originQ, Vector3 dirQ, int edgeIndex)
    {
        var mids = _session.GetSelectedEdgeMidpoints();
        if (edgeIndex<0||edgeIndex>=mids.Length) return;
        var pos=mids[edgeIndex];
        _dragPlaneNormalQuake=-dirQ; _dragPlanePointQuake=pos; _edgeDragStartHit=pos;
        if(RayPlaneIntersect(originQ, dirQ, _dragPlanePointQuake, _dragPlaneNormalQuake, out var hit)) _edgeDragStartHit=hit;
        _edgeDragCurrentDelta=Vector3.Zero; _isDraggingEdge=true; _dragPushedUndo=false;
        _session.BeginUndoGroup("edge");
    }
    void UpdateEdgeDrag()
    {
        if (!ScreenToRayQuake(_lastMousePos, out var originQ, out var dirQ)) return;
        if (!RayPlaneIntersect(originQ, dirQ, _dragPlanePointQuake, _dragPlaneNormalQuake, out var curHit)) return;
        Vector3 desiredDelta = curHit - _edgeDragStartHit;
        bool lockX=_input.IsDown(Key.X); bool lockY=_input.IsDown(Key.Y); bool lockZ=_input.IsDown(Key.Z);
        if(lockX) desiredDelta=new Vector3(desiredDelta.X,0,0);
        else if(lockY) desiredDelta=new Vector3(0,desiredDelta.Y,0);
        else if(lockZ) desiredDelta=new Vector3(0,0,desiredDelta.Z);
        bool shiftNosnap=_input.IsDown(Key.ShiftLeft)||_input.IsDown(Key.ShiftRight);
        if(_session.GridSnapEnabled && !shiftNosnap && _session.GridSize>0) desiredDelta=BrushManipulation.Snap(desiredDelta,_session.GridSize);
        Vector3 inc=desiredDelta-_edgeDragCurrentDelta;
        if(inc.LengthSquared()<1e-6f) return;
        _edgeDragCurrentDelta=desiredDelta;
        bool ok;
        if(!_dragPushedUndo){ ok=_session.MoveSelectedEdge(inc,true); if(ok) _dragPushedUndo=true; else return; }
        else ok=_session.MoveSelectedEdge(inc,false);
        if(ok) _needsRecompile=true;
    }

    void UpdateDrag()
    {
        if (!ScreenToRayQuake(_lastMousePos, out var originQ, out var dirQ)) return;
        if (!RayPlaneIntersect(originQ, dirQ, _dragPlanePointQuake, _dragPlaneNormalQuake, out var curHit)) return;
        Vector3 desiredDelta = curHit - _dragStartHitQuake;

        // axis / plane constraint from gizmo or X/Y/Z keys
        if (_dragAxis == GizmoAxis.X || _dragAxis == GizmoAxis.Y || _dragAxis == GizmoAxis.Z)
        {
            Vector3 ad = _dragAxis == GizmoAxis.X ? Vector3.UnitX : _dragAxis == GizmoAxis.Y ? Vector3.UnitY : Vector3.UnitZ;
            desiredDelta = ad * Vector3.Dot(desiredDelta, ad);
        }
        else if (_dragAxis == GizmoAxis.XY) desiredDelta = new Vector3(desiredDelta.X, desiredDelta.Y, 0);
        else if (_dragAxis == GizmoAxis.XZ) desiredDelta = new Vector3(desiredDelta.X, 0, desiredDelta.Z);
        else if (_dragAxis == GizmoAxis.YZ) desiredDelta = new Vector3(0, desiredDelta.Y, desiredDelta.Z);
        else
        {
            bool lockX = _input.IsDown(Key.X);
            bool lockY = _input.IsDown(Key.Y);
            bool lockZ = _input.IsDown(Key.Z);
            if (lockX) desiredDelta = new Vector3(desiredDelta.X, 0, 0);
            else if (lockY) desiredDelta = new Vector3(0, desiredDelta.Y, 0);
            else if (lockZ) desiredDelta = new Vector3(0, 0, desiredDelta.Z);
        }

        if (_session.Mode == EditMode.Face && _session.SelectedBrush != null && _session.SelectedFaceIndex >= 0)
        {
            var n = _session.SelectedBrush.Faces[_session.SelectedFaceIndex].Normal;
            desiredDelta = n * Vector3.Dot(desiredDelta, n);
        }

        bool shiftNosnap = _input.IsDown(Key.ShiftLeft) || _input.IsDown(Key.ShiftRight);
        bool doSnap = _session.GridSnapEnabled && !shiftNosnap;
        if (doSnap && _session.GridSize > 0) desiredDelta = BrushManipulation.Snap(desiredDelta, _session.GridSize);

        // scale/rotate gizmo handling
        if (_gizmo.Mode==GizmoMode.Scale && _dragAxis!=GizmoAxis.None)
        {
            // incremental already filtered to axis, use its length as scale factor
            Vector3 inc = desiredDelta - _dragCurrentDeltaQuake;
            if (inc.LengthSquared()<1e-6f) return;
            _dragCurrentDeltaQuake = desiredDelta;
            Vector3 center = _session.GetSelectedCenter();
            BrushManipulation.GetBounds(_session.SelectedBrush!, out var sMin, out var sMax);
            Vector3 size = sMax - sMin; if(size.LengthSquared()<1e-4f) size=new Vector3(64,64,64);
            Vector3 scale = new Vector3(1,1,1);
            if(_dragAxis==GizmoAxis.X) scale.X = 1 + Vector3.Dot(inc, Vector3.UnitX) / Math.Max(size.X,8f);
            else if(_dragAxis==GizmoAxis.Y) scale.Y = 1 + Vector3.Dot(inc, Vector3.UnitY) / Math.Max(size.Y,8f);
            else if(_dragAxis==GizmoAxis.Z) scale.Z = 1 + Vector3.Dot(inc, Vector3.UnitZ) / Math.Max(size.Z,8f);
            else if(_dragAxis==GizmoAxis.Screen) { float f=1+inc.Length()*0.01f; scale=new Vector3(f,f,f); }
            if(!_dragPushedUndo){ if(_session.ScaleSelectedBrush(scale,true)) _dragPushedUndo=true; else return; }
            else _session.ScaleSelectedBrush(scale,false);
            _needsRecompile=true; return;
        }
        if (_gizmo.Mode==GizmoMode.Rotate && _dragAxis!=GizmoAxis.None)
        {
            Vector3 inc = desiredDelta - _dragCurrentDeltaQuake;
            if (inc.LengthSquared()<1e-6f) return;
            _dragCurrentDeltaQuake = desiredDelta;
            float angle = inc.Length()*0.8f; // degrees approx
            // determine sign via cross with view?
            if(Vector3.Dot(inc, new Vector3(1,1,0))<0) angle=-angle;
            Quaternion rot = Quaternion.Identity;
            if(_dragAxis==GizmoAxis.X) rot=Quaternion.CreateFromAxisAngle(Vector3.UnitX, angle*Units.Deg2Rad);
            else if(_dragAxis==GizmoAxis.Y) rot=Quaternion.CreateFromAxisAngle(Vector3.UnitY, angle*Units.Deg2Rad);
            else if(_dragAxis==GizmoAxis.Z) rot=Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle*Units.Deg2Rad);
            else rot=Quaternion.CreateFromAxisAngle(Vector3.UnitY, angle*Units.Deg2Rad);
            if(!_dragPushedUndo){ if(_session.RotateSelectedBrush(rot,true)) _dragPushedUndo=true; else return; }
            else _session.RotateSelectedBrush(rot,false);
            _needsRecompile=true; return;
        }

        Vector3 incremental = desiredDelta - _dragCurrentDeltaQuake;
        if (incremental.LengthSquared() < 1e-6f) return;
        _dragCurrentDeltaQuake = desiredDelta;

        if (!_dragPushedUndo)
        {
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

    void UpdateVertexDrag()
    {
        if (!ScreenToRayQuake(_lastMousePos, out var originQ, out var dirQ)) return;
        if (!RayPlaneIntersect(originQ, dirQ, _dragPlanePointQuake, _dragPlaneNormalQuake, out var curHit)) return;
        Vector3 desiredDelta = curHit - _vertexDragStartHit;
        // axis locks
        bool lockX = _input.IsDown(Key.X);
        bool lockY = _input.IsDown(Key.Y);
        bool lockZ = _input.IsDown(Key.Z);
        if (lockX) desiredDelta = new Vector3(desiredDelta.X, 0, 0);
        else if (lockY) desiredDelta = new Vector3(0, desiredDelta.Y, 0);
        else if (lockZ) desiredDelta = new Vector3(0, 0, desiredDelta.Z);
        bool shiftNosnap = _input.IsDown(Key.ShiftLeft) || _input.IsDown(Key.ShiftRight);
        if (_session.GridSnapEnabled && !shiftNosnap && _session.GridSize > 0) desiredDelta = BrushManipulation.Snap(desiredDelta, _session.GridSize);
        Vector3 incremental = desiredDelta - _vertexDragCurrentDelta;
        if (incremental.LengthSquared() < 1e-6f) return;
        _vertexDragCurrentDelta = desiredDelta;
        bool ok;
        if (!_dragPushedUndo) { ok = _session.MoveSelectedVertex(incremental, pushUndo: true); if (ok) _dragPushedUndo = true; else return; }
        else ok = _session.MoveSelectedVertex(incremental, pushUndo: false);
        if (ok) _needsRecompile = true;
    }

    // ---------- TrenchBroom-style draw-to-create (hover ghost + smart plane) ----------

    float GetBestPlacementPlaneZ(Vector3 originQ, Vector3 dirQ)
    {
        // Prefer: face under cursor (place on top of hit brush), otherwise selection height, otherwise ground.
        // This makes stacking brushes much easier than always Z=0.
        if (_session.TryPickBrush(originQ, dirQ, out _, out _, out _, out _) )
        {
            // Re-trace with a horizontal plane through the hit point's Z would still be wrong for vertical faces;
            // use the actual hit Z if we can find it — do a quick ray vs all brushes horizontally.
            // For now use a simple ray vs horizontal planes sweep: try ground then selection height.
        }
        // Selection height as plane
        if (_session.Selected != null)
        {
            _session.GetSelectedBounds(out var sMin, out var sMax);
            if (sMin.X <= sMax.X)
            {
                // use bottom of selection for floor-like placement, top for stacking — pick the closer to the ray
                // heuristic: if camera is above selection, use top; else bottom. For now use sMin.Z (floor).
                return sMin.Z;
            }
        }
        return 0f;
    }

    void UpdateGhostPreview()
    {
        _ghostValid = false;
        if (ImGui.GetIO().WantCaptureMouse) return;
        if (!ScreenToRayQuake(_lastMousePos, out var originQ, out var dirQ)) return;

        bool shiftNosnap = _input.IsDown(Key.ShiftLeft) || _input.IsDown(Key.ShiftRight);

        // Wall-aligned ghost: if we look at a side face, show brush flush against that face (Hammer extrude preview)
        if (_session.TryPickBrush(originQ, dirQ, out int ei, out int bi, out float t, out int fi))
        {
            var br = _session.Entities[ei].Brushes[bi];
            if (fi >= 0 && fi < br.Faces.Count)
            {
                var n = br.Faces[fi].Normal;
                // wall if not mostly horizontal
                if (MathF.Abs(n.Z) < 0.6f)
                {
                    Vector3 hit = originQ + dirQ * t;
                    float thickness = Math.Max(_session.BrushDefaultSize.X, Math.Max(_session.GridSize, 8f));
                    // choose dominant axis for thickness direction
                    Vector3 halfWall = new Vector3(
                        Math.Max(_session.BrushDefaultSize.X, 8f) * 0.5f,
                        Math.Max(_session.BrushDefaultSize.Y, 8f) * 0.5f,
                        Math.Max(_session.BrushDefaultSize.Z, 8f) * 0.5f);
                    // build wall-flush axis-aligned box: extent along normal is thickness, other two centered at hit
                    Vector3 min, max;
                    if (MathF.Abs(n.X) > MathF.Abs(n.Y))
                    {
                        float wallX = hit.X;
                        if (n.X > 0) { min.X = wallX; max.X = wallX + thickness; }
                        else { max.X = wallX; min.X = wallX - thickness; }
                        min.Y = hit.Y - halfWall.Y; max.Y = hit.Y + halfWall.Y;
                        min.Z = hit.Z - halfWall.Z; max.Z = hit.Z + halfWall.Z;
                    }
                    else
                    {
                        float wallY = hit.Y;
                        if (n.Y > 0) { min.Y = wallY; max.Y = wallY + thickness; }
                        else { max.Y = wallY; min.Y = wallY - thickness; }
                        min.X = hit.X - halfWall.X; max.X = hit.X + halfWall.X;
                        min.Z = hit.Z - halfWall.Z; max.Z = hit.Z + halfWall.Z;
                    }
                    if (!shiftNosnap) { min = _session.SnapQuake(min); max = _session.SnapQuake(max); }
                    // clamp min size
                    float minEdge = Math.Max(_session.GridSize, 4f);
                    if (max.X - min.X < minEdge) max.X = min.X + minEdge;
                    if (max.Y - min.Y < minEdge) max.Y = min.Y + minEdge;
                    if (max.Z - min.Z < minEdge) max.Z = min.Z + minEdge;
                    _ghostMin = min; _ghostMax = max;
                    _ghostPlaneNormal = n; _ghostPlanePoint = hit; _ghostWallMode = true; _ghostPlaneZ = hit.Z;
                    _ghostValid = true;
                    return;
                }
            }
        }

        // Floor / top placement (default)
        float planeZ = GetBestPlacementPlaneZ(originQ, dirQ);
        if (_session.TryPickBrush(originQ, dirQ, out int ei2, out int bi2, out float t2, out _))
        {
            var br = _session.Entities[ei2].Brushes[bi2];
            KREAN.MapCompiler.BrushManipulation.GetBounds(br, out _, out var bmax);
            planeZ = bmax.Z;
        }
        if (!RayHorizontalPlane(originQ, dirQ, planeZ, out var hit2)) return;
        if (!shiftNosnap) hit2 = _session.SnapQuake(hit2);
        float height = Math.Max(_session.BrushDefaultSize.Z, Math.Max(_session.GridSize, 8f));
        float minEdge2 = Math.Max(_session.GridSize, 4f);
        var half2 = new Vector3(
            Math.Max(_session.BrushDefaultSize.X, minEdge2),
            Math.Max(_session.BrushDefaultSize.Y, minEdge2), 0) * 0.5f;
        _ghostMin = new Vector3(hit2.X - half2.X, hit2.Y - half2.Y, planeZ);
        _ghostMax = new Vector3(hit2.X + half2.X, hit2.Y + half2.Y, planeZ + height);
        if (!shiftNosnap) { _ghostMin = _session.SnapQuake(_ghostMin); _ghostMax = _session.SnapQuake(_ghostMax); }
        _ghostPlaneZ = planeZ; _ghostPlaneNormal = new Vector3(0,0,1); _ghostPlanePoint = new Vector3(0,0,planeZ);
        _ghostWallMode = false; _ghostValid = true;
    }

    void StartBrushCreation(Vector3 originQ, Vector3 dirQ, float pickT)
    {
        bool shiftNosnap = _input.IsDown(Key.ShiftLeft) || _input.IsDown(Key.ShiftRight);

        // Direct wall check — allows flush placement even without the hover ghost
        if (_session.TryPickBrush(originQ, dirQ, out int hEi, out int hBi, out float hT, out int hFi)
            && hFi >= 0 && hFi < _session.Entities[hEi].Brushes[hBi].Faces.Count)
        {
            var n = _session.Entities[hEi].Brushes[hBi].Faces[hFi].Normal;
            if (MathF.Abs(n.Z) < 0.6f)
            {
                Vector3 hitWall = originQ + dirQ * hT;
                if (!shiftNosnap) hitWall = _session.SnapQuake(hitWall);
                _createWallMode = true;
                _createPlaneNormal = n;
                _createPlanePoint = hitWall;
                _createPlaneZ = hitWall.Z;
                _createAnchorQuake = hitWall;
                _createCurrentQuake = hitWall;
                _createHeight = Math.Max(_session.BrushDefaultSize.X, Math.Max(_session.GridSize, 8f));
                _createHasDrag = false;
                _isCreatingBrush = true;
                _ghostValid = false;
                _ui.SetStatus($"Draw on wall (n {n.X:0.0},{n.Y:0.0},{n.Z:0.0}) — drag to size, wheel = thickness {_createHeight:0}");
                return;
            }
        }

        float planeZ = GetBestPlacementPlaneZ(originQ, dirQ);
        // snap to top of brush under cursor if hit
        if (_session.TryPickBrush(originQ, dirQ, out int tEi, out int tBi, out float tT, out _))
        {
            var br = _session.Entities[tEi].Brushes[tBi];
            KREAN.MapCompiler.BrushManipulation.GetBounds(br, out _, out var bmax);
            planeZ = bmax.Z;
        }
        _createWallMode = false;
        _createPlaneNormal = new Vector3(0, 0, 1);
        _createPlanePoint = new Vector3(0, 0, planeZ);
        Vector3 anchor;
        if (RayHorizontalPlane(originQ, dirQ, planeZ, out var groundHit))
            anchor = groundHit;
        else { anchor = originQ + dirQ * 256f; anchor.Z = planeZ; }
        if (!shiftNosnap) anchor = _session.SnapQuake(anchor); else anchor = _session.SnapQuake(anchor);
        _createAnchorQuake = anchor;
        _createCurrentQuake = anchor;
        _createPlaneZ = planeZ;
        _createHeight = Math.Max(_session.BrushDefaultSize.Z, Math.Max(_session.GridSize, 8f));
        _createHasDrag = false;
        _isCreatingBrush = true;
        _ghostValid = false;
        _ui.SetStatus($"Draw footprint on Z={planeZ:0} — drag to size, wheel = height {_createHeight:0} (Esc cancels)");
    }

    void UpdateBrushCreation()
    {
        if (!ScreenToRayQuake(_lastMousePos, out var originQ, out var dirQ)) return;
        bool shiftNosnap = _input.IsDown(Key.ShiftLeft) || _input.IsDown(Key.ShiftRight);
        Vector3 hit;
        if (_createWallMode)
        {
            if (!RayPlaneIntersect(originQ, dirQ, _createPlanePoint, _createPlaneNormal, out hit)) return;
            // keep hit on wall plane — allow dragging along wall's U/V
        }
        else
        {
            if (!RayHorizontalPlane(originQ, dirQ, _createPlaneZ, out hit)) return;
        }
        if (!shiftNosnap) hit = _session.SnapQuake(hit);

        bool heightDrag = _input.IsDown(Key.ControlLeft) || _input.IsDown(Key.ControlRight) || _input.IsDown(Key.AltLeft) || _input.IsDown(Key.AltRight);
        if (heightDrag && !_createWallMode)
        {
            float dy = (hit.Y - _createAnchorQuake.Y);
            float dx = (hit.X - _createAnchorQuake.X);
            float h = Math.Max(_session.GridSize, 8f) + MathF.Abs(dy) + MathF.Abs(dx) * 0.25f;
            _createHeight = Math.Clamp(_session.SnapQuake(new Vector3(0,0,h)).Z, Math.Max(_session.GridSize, 8f), 4096f);
        }
        else if (heightDrag && _createWallMode)
        {
            // In wall mode heightDrag adjusts thickness outward — use distance along plane normal projected
            // approximate via mouse wheel is primary; Ctrl-drag here toggles thickness via second axis (simplified)
        }

        if ((hit - _createAnchorQuake).LengthSquared() > 0.01f) _createHasDrag = true;
        _createCurrentQuake = hit;
    }

    void ComputeCreateBounds(out Vector3 min, out Vector3 max)
    {
        float minEdge = Math.Max(_session.GridSize, 4f);
        var cur = _createCurrentQuake;
        if (_createWallMode)
        {
            // Wall-aligned: footprint on wall plane (Y/Z or X/Z), thickness outward along plane normal
            Vector3 n = _createPlaneNormal;
            if (!_createHasDrag)
            {
                var half = new Vector3(
                    Math.Max(_session.BrushDefaultSize.X, minEdge) * 0.5f,
                    Math.Max(_session.BrushDefaultSize.Y, minEdge) * 0.5f,
                    Math.Max(_session.BrushDefaultSize.Z, minEdge) * 0.5f);
                // centered at anchor on plane
                if (MathF.Abs(n.X) > MathF.Abs(n.Y))
                {
                    float wallX = _createPlanePoint.X;
                    if (n.X > 0) { min.X = wallX; max.X = wallX + _createHeight; }
                    else { max.X = wallX; min.X = wallX - _createHeight; }
                    min.Y = _createAnchorQuake.Y - half.Y; max.Y = _createAnchorQuake.Y + half.Y;
                    min.Z = _createAnchorQuake.Z - half.Z; max.Z = _createAnchorQuake.Z + half.Z;
                }
                else
                {
                    float wallY = _createPlanePoint.Y;
                    if (n.Y > 0) { min.Y = wallY; max.Y = wallY + _createHeight; }
                    else { max.Y = wallY; min.Y = wallY - _createHeight; }
                    min.X = _createAnchorQuake.X - half.X; max.X = _createAnchorQuake.X + half.X;
                    min.Z = _createAnchorQuake.Z - half.Z; max.Z = _createAnchorQuake.Z + half.Z;
                }
                return;
            }
            // drag defines extents along wall's two tangent axes
            if (MathF.Abs(n.X) > MathF.Abs(n.Y))
            {
                float y0 = Math.Min(_createAnchorQuake.Y, cur.Y); float y1 = Math.Max(_createAnchorQuake.Y, cur.Y);
                float z0 = Math.Min(_createAnchorQuake.Z, cur.Z); float z1 = Math.Max(_createAnchorQuake.Z, cur.Z);
                if (y1 - y0 < minEdge) y1 = y0 + minEdge;
                if (z1 - z0 < minEdge) z1 = z0 + minEdge;
                float wallX = _createPlanePoint.X;
                if (n.X > 0) { min.X = wallX; max.X = wallX + _createHeight; }
                else { max.X = wallX; min.X = wallX - _createHeight; }
                min.Y = y0; max.Y = y1; min.Z = z0; max.Z = z1;
            }
            else
            {
                float x0 = Math.Min(_createAnchorQuake.X, cur.X); float x1 = Math.Max(_createAnchorQuake.X, cur.X);
                float z0 = Math.Min(_createAnchorQuake.Z, cur.Z); float z1 = Math.Max(_createAnchorQuake.Z, cur.Z);
                if (x1 - x0 < minEdge) x1 = x0 + minEdge;
                if (z1 - z0 < minEdge) z1 = z0 + minEdge;
                float wallY = _createPlanePoint.Y;
                if (n.Y > 0) { min.Y = wallY; max.Y = wallY + _createHeight; }
                else { max.Y = wallY; min.Y = wallY - _createHeight; }
                min.X = x0; max.X = x1; min.Z = z0; max.Z = z1;
            }
            return;
        }

        if (!_createHasDrag)
        {
            var half = new Vector3(
                Math.Max(_session.BrushDefaultSize.X, minEdge),
                Math.Max(_session.BrushDefaultSize.Y, minEdge), 0) * 0.5f;
            min = new Vector3(_createAnchorQuake.X - half.X, _createAnchorQuake.Y - half.Y, _createPlaneZ);
            max = new Vector3(_createAnchorQuake.X + half.X, _createAnchorQuake.Y + half.Y, _createPlaneZ + _createHeight);
            return;
        }
        min = new Vector3(Math.Min(_createAnchorQuake.X, cur.X), Math.Min(_createAnchorQuake.Y, cur.Y), _createPlaneZ);
        max = new Vector3(Math.Max(_createAnchorQuake.X, cur.X), Math.Max(_createAnchorQuake.Y, cur.Y), _createPlaneZ + _createHeight);
        if (max.X - min.X < minEdge) max.X = min.X + minEdge;
        if (max.Y - min.Y < minEdge) max.Y = min.Y + minEdge;
        if (max.Z - min.Z < 1f) max.Z = min.Z + 1f;
    }

    void FinishBrushCreation(bool cancelled)
    {
        _isCreatingBrush = false;
        if (cancelled)
        {
            _ui.SetStatus("Brush creation cancelled (Esc)");
            return;
        }
        ComputeCreateBounds(out var min, out var max);
        // Click without drag = quick-place a default-size cube / wall-flush block.
        if (!_createHasDrag && !_createWallMode)
        {
            var size = new Vector3(
                Math.Max(_session.BrushDefaultSize.X, 8),
                Math.Max(_session.BrushDefaultSize.Y, 8),
                Math.Max(_session.BrushDefaultSize.Z, 8));
            var center = new Vector3(_createAnchorQuake.X, _createAnchorQuake.Y, _createPlaneZ + size.Z * 0.5f);
            min = center - size * 0.5f;
            max = center + size * 0.5f;
        }
        // wall mode: keep flush bounds from ComputeCreateBounds (already correct)
        _session.CreateBoxBrush(min, max);
        _needsRecompile = true;
        var sz = max - min;
        _ui.SetStatus($"Created brush {sz.X:0}x{sz.Y:0}x{sz.Z:0} — drag faces in Face mode to shape");
    }

    void CancelBrushCreation()
    {
        if (!_isCreatingBrush) return;
        FinishBrushCreation(cancelled: true);
    }

    static bool RayHorizontalPlane(Vector3 origin, Vector3 dir, float planeZ, out Vector3 hit)
    {
        if (MathF.Abs(dir.Z) < 1e-6f) { hit = default; return false; }
        float t = (planeZ - origin.Z) / dir.Z;
        if (t < 0) { hit = default; return false; }
        hit = origin + dir * t;
        return true;
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
