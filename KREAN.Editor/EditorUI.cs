using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using KREAN.MapCompiler;

namespace KREAN.Editor;

/// <summary>
/// Dumb-simple TrenchBroom-style UI. No clutter panels.
/// Only: MainMenu | Toolbar (tools + grid) | Outliner (left) | Inspector (right) | Status.
/// Viewport is the rest — 3D is king.
/// </summary>
public sealed class EditorUI
{
    readonly MapEditorSession _session;
    readonly Action _recompile;
    readonly Action<string> _saveAction;
    readonly Action<string> _exportSceneAction;

    string _filter = "";
    string _newKey = "";
    string _newValue = "";
    string _classEdit = "";
    bool _showNewMapDialog;
    bool _showOpenDialog;
    bool _showSaveAsDialog;
    bool _showArchDialog;
    string _fileDialogPath = "";
    string _statusMessage = "";
    float _statusTimer;
    float _archInner = 64, _archWall = 16, _archDepth = 32;
    int _archSegments = 8;
    float _archSweep = 180;

    Vector3 _originEdit;
    bool _originInitialized;
    Vector3 _colorEdit = Vector3.One;
    bool _colorInitialized;
    float _lightIntensity = 300f;
    bool _lightInitialized;
    float _faceExtrude;
    Vector3 _brushMoveEdit;
    bool _brushMoveInitialized;
    Vector3 _boundsMinEdit, _boundsMaxEdit;
    bool _boundsInitialized;
    Vector3 _newBrushSize = new(64, 64, 64);
    bool _newBrushInit;
    string _newBrushTex = "wall";
    bool _newBrushTexInit;

    readonly string[] _knownClasses = new[]
    {
        "worldspawn", "info_player_start", "info_player_deathmatch", "light",
        "func_detail", "func_wall", "func_illusionary",
        "trigger_multiple", "trigger_once", "trigger_teleport",
        "misc_model", "misc_prefab", "item_health", "weapon_shotgun"
    };
    List<FgdClass> _fgdClasses = new();
    bool _fgdLoaded;
    IEnumerable<string> AllClassNames => _fgdClasses.Count>0 ? _fgdClasses.Select(c=>c.ClassName).OrderBy(s=>s) : _knownClasses;
    FgdClass? FindFgd(string classname) => _fgdClasses.FirstOrDefault(c=>c.ClassName.Equals(classname, StringComparison.OrdinalIgnoreCase));
    void EnsureFgd()
    {
        if(_fgdLoaded) return; _fgdLoaded=true;
        _fgdClasses = FgdParser.TryLoadFromSearch();
        if(_fgdClasses.Count==0)
        {
            // create from knownClasses as fallback with colors
            foreach(var n in _knownClasses) _fgdClasses.Add(new FgdClass{ ClassName=n, Description=n, Kind=n.StartsWith("info_")||n=="light"?"PointClass":"SolidClass", Color=n=="light"?new Vector3(1,1,0.3f): n.StartsWith("trigger")?new Vector3(1,0.3f,0.3f): new Vector3(0.6f,0.6f,0.9f) });
        }
    }

    readonly string[] _commonTextures = new[]
    {
        "wall","floor","ceiling","concrete","metal","brick","wood",
        "clip","skip","hint","origin","trigger","nodraw","caulk"
    };

    // Nuake — Quake/DOS pixel, IBM VGA 8x16, olive/drab + Quake orange, sharp 0px, 1px border
    static readonly Vector4 ColBg = new(0.06f, 0.08f, 0.11f, 1f);     // #0f141c Nuake bg
    static readonly Vector4 ColBgDark = new(0.04f, 0.06f, 0.09f, 1f); // #0a0e13
    static readonly Vector4 ColPanel = new(0.10f, 0.13f, 0.17f, 1f);    // #1a212c panel
    static readonly Vector4 ColBeige = new(0.88f, 0.84f, 0.78f, 1f);     // quake beige #e0d8c8
    static readonly Vector4 ColAccent = new(1f, 0.55f, 0f, 1f);    // Nuake Quake orange #ff8c00
    static readonly Vector4 ColAccentHover = new(1f, 0.65f, 0.18f, 1f);
    static readonly Vector4 ColBorderHi = new(0.18f, 0.24f, 0.31f, 1f);  // quake olive border
    static readonly Vector4 ColBorderSh = new(0.05f, 0.07f, 0.11f, 1f);

    bool _themePushed;

    public EditorUI(MapEditorSession session, Action recompile, Action<string> save, Action<string> exportScene)
    {
        _session = session;
        _recompile = recompile;
        _saveAction = save;
        _exportSceneAction = exportScene;
    }

    public void SetStatus(string msg) { _statusMessage = msg; _statusTimer = 3.5f; Console.WriteLine(msg); }
    public void ShowArchDialog() { _showArchDialog = true; }
    public void Tick(float dt) { if (_statusTimer > 0) _statusTimer -= dt; }

    bool _showSceneBrowser = true;
    bool _showMaterialBrowser = true;
    bool _showEntityPalette = false;
    bool _showOrtho = true;
    public bool ShowSearchReport = false;
    string _searchQuery = "";
    string _replaceKey = "";
    string _replaceValue = "";
    bool _resetLayoutRequested = false;
    public bool ResetLayoutRequested => _resetLayoutRequested;
    public void ClearResetFlag() => _resetLayoutRequested = false;
    public void SetShowFlags(bool scene, bool mat, bool entity, bool ortho){ _showSceneBrowser=scene; _showMaterialBrowser=mat; _showEntityPalette=entity; _showOrtho=ortho; }
    public bool GetShowFlag(string which)=> which switch{ "scene"=>_showSceneBrowser, "material"=>_showMaterialBrowser, "entity"=>_showEntityPalette, "ortho"=>_showOrtho, _=>false };
    public (Vector2 topPan,float topZoom, Vector2 frontPan,float frontZoom, Vector2 sidePan,float sideZoom) GetOrtho()=> (_topView.Pan,_topView.Zoom,_frontView.Pan,_frontView.Zoom,_sideView.Pan,_sideView.Zoom);
    public void SetOrtho(Vector2 tp,float tz, Vector2 fp,float fz, Vector2 sp,float sz){ _topView.Pan=tp; _topView.Zoom=tz; _frontView.Pan=fp; _frontView.Zoom=fz; _sideView.Pan=sp; _sideView.Zoom=sz; }
    OrthoView _topView = new(OrthoAxis.Top);
    OrthoView _frontView = new(OrthoAxis.Front);
    OrthoView _sideView = new(OrthoAxis.Side);
    string _matFilter = "";
    string _entityFilter = "";
    List<string> _matList = new();
    float _matRefreshTimer = 0;
    int _csgOp = 0; // 0 subtract,1 intersect,2 union

    public void ResetLayout()
    {
        _showSceneBrowser = true;
        _showMaterialBrowser = true;
        _showEntityPalette = false;
        _showOrtho = true;
        _topView.Pan = Vector2.Zero; _topView.Zoom = 0.6f;
        _frontView.Pan = Vector2.Zero; _frontView.Zoom = 0.6f;
        _sideView.Pan = Vector2.Zero; _sideView.Zoom = 0.6f;
        _resetLayoutRequested = true;
        // delete ImGui ini to clear docking
        try
        {
            foreach(var p in new[]{ "imgui.ini", Path.Combine(AppContext.BaseDirectory,"imgui.ini"), Path.Combine(Directory.GetCurrentDirectory(),"imgui.ini")})
                if(File.Exists(p)) File.Delete(p);
        } catch {}
        SetStatus("Layout reset — windows will snap to default next frame");
    }

    public void Draw(Vector2 viewportSize, float fps, Vector3 camPos, int meshCount, int brushCount)
    {
        EnsureTheme();
        // if reset requested, force next windows to always position and clear dock
        if (_resetLayoutRequested)
        {
            // clear docking by loading empty ini
            ImGui.LoadIniSettingsFromDisk("");
            _resetLayoutRequested = false;
        }
        DrawMainMenu();
        DrawToolbar(viewportSize);
        if (_showSceneBrowser) DrawSceneBrowser();
        if (_showMaterialBrowser) DrawMaterialBrowser();
        if (_showEntityPalette) DrawEntityPalette();
        if (_showOrtho)
        {
            OrthoRenderer.DrawOrthoWindow("Top (XY) — Middle-drag pan, Wheel zoom", _topView, _session, _recompile, SetStatus);
            OrthoRenderer.DrawOrthoWindow("Front (XZ)", _frontView, _session, _recompile, SetStatus);
            OrthoRenderer.DrawOrthoWindow("Side (YZ)", _sideView, _session, _recompile, SetStatus);
        }
        if (ShowSearchReport) DrawSearchReport();
        DrawInspector();
        DrawStatusBar(viewportSize, fps, camPos, meshCount, brushCount);
        DrawFileDialogs();
        DrawArchDialog();
        DrawViewportOverlay();
    }

    void EnsureTheme()
    {
        if (_themePushed) return;
        _themePushed = true;
        var s = ImGui.GetStyle();
        // Nuake: Quake/DOS IBM VGA 8x16, sharp pixel, olive drab + Quake orange
        s.WindowRounding = 0; s.FrameRounding = 0; s.GrabRounding = 0; s.ScrollbarRounding = 0; s.TabRounding = 0; s.PopupRounding = 0;
        s.WindowBorderSize = 1; s.FrameBorderSize = 0; s.ChildBorderSize = 0; s.PopupBorderSize = 1;
        s.FramePadding = new Vector2(4, 2); s.ItemSpacing = new Vector2(6, 3); s.ItemInnerSpacing = new Vector2(4, 2);
        s.WindowPadding = new Vector2(6, 4); s.WindowTitleAlign = new Vector2(0.02f, 0.5f);
        s.ScrollbarSize = 8; s.GrabMinSize = 10;
        var c = s.Colors;
        c[(int)ImGuiCol.Text] = new Vector4(0.88f, 0.84f, 0.78f, 1f); // quake beige
        c[(int)ImGuiCol.TextDisabled] = new Vector4(0.42f, 0.45f, 0.50f, 1f);
        c[(int)ImGuiCol.WindowBg] = new Vector4(0.07f, 0.09f, 0.13f, 1f); // #12151f
        c[(int)ImGuiCol.ChildBg] = new Vector4(0.09f, 0.12f, 0.16f, 1f);
        c[(int)ImGuiCol.PopupBg] = new Vector4(0.11f, 0.14f, 0.19f, 1f);
        c[(int)ImGuiCol.Border] = new Vector4(0.18f, 0.24f, 0.31f, 1f);
        c[(int)ImGuiCol.BorderShadow] = new Vector4(0f, 0f, 0f, 0f);
        c[(int)ImGuiCol.FrameBg] = new Vector4(0.13f, 0.16f, 0.21f, 1f);
        c[(int)ImGuiCol.FrameBgHovered] = new Vector4(0.18f, 0.22f, 0.28f, 1f);
        c[(int)ImGuiCol.FrameBgActive] = new Vector4(0.16f, 0.20f, 0.26f, 1f);
        c[(int)ImGuiCol.TitleBg] = new Vector4(0.05f, 0.07f, 0.11f, 1f);
        c[(int)ImGuiCol.TitleBgActive] = new Vector4(0.08f, 0.11f, 0.15f, 1f);
        c[(int)ImGuiCol.TitleBgCollapsed] = new Vector4(0.05f, 0.07f, 0.11f, 1f);
        c[(int)ImGuiCol.MenuBarBg] = new Vector4(0.06f, 0.09f, 0.12f, 1f);
        c[(int)ImGuiCol.ScrollbarBg] = new Vector4(0.06f, 0.09f, 0.12f, 1f);
        c[(int)ImGuiCol.ScrollbarGrab] = new Vector4(0.18f, 0.24f, 0.31f, 1f);
        c[(int)ImGuiCol.ScrollbarGrabHovered] = new Vector4(0.22f, 0.28f, 0.35f, 1f);
        c[(int)ImGuiCol.ScrollbarGrabActive] = ColAccent;
        c[(int)ImGuiCol.CheckMark] = ColAccent;
        c[(int)ImGuiCol.SliderGrab] = ColAccent;
        c[(int)ImGuiCol.SliderGrabActive] = ColAccentHover;
        c[(int)ImGuiCol.Button] = new Vector4(0.14f, 0.17f, 0.22f, 1f);
        c[(int)ImGuiCol.ButtonHovered] = new Vector4(0.20f, 0.24f, 0.30f, 1f);
        c[(int)ImGuiCol.ButtonActive] = new Vector4(0.12f, 0.15f, 0.20f, 1f);
        c[(int)ImGuiCol.Header] = new Vector4(0.13f, 0.17f, 0.23f, 1f);
        c[(int)ImGuiCol.HeaderHovered] = new Vector4(0.18f, 0.23f, 0.30f, 1f);
        c[(int)ImGuiCol.HeaderActive] = new Vector4(0.95f, 0.45f, 0.05f, 1f); // orange active
        c[(int)ImGuiCol.Separator] = new Vector4(0.18f, 0.24f, 0.31f, 1f);
        c[(int)ImGuiCol.SeparatorHovered] = ColAccent;
        c[(int)ImGuiCol.SeparatorActive] = ColAccentHover;
        c[(int)ImGuiCol.Tab] = new Vector4(0.10f, 0.13f, 0.17f, 1f);
        c[(int)ImGuiCol.TabHovered] = ColAccent;
        c[(int)ImGuiCol.DockingPreview] = new Vector4(ColAccent.X, ColAccent.Y, ColAccent.Z, 0.35f);
        c[(int)ImGuiCol.DockingEmptyBg] = ColBg;
    }

    void DrawMainMenu()
    {
        if (!ImGui.BeginMainMenuBar()) return;
        if (ImGui.BeginMenu("File"))
        {
            if (ImGui.MenuItem("New", "Ctrl+N")) _showNewMapDialog = true;
            if (ImGui.MenuItem("Open...", "Ctrl+O")) { _fileDialogPath = _session.FilePath ?? ""; _showOpenDialog = true; }
            if (ImGui.MenuItem("Save", "Ctrl+S", false, _session.FilePath != null)) TrySave();
            if (ImGui.MenuItem("Save As...")) { _fileDialogPath = _session.FilePath ?? "new.map"; _showSaveAsDialog = true; }
            ImGui.Separator();
            if (ImGui.MenuItem("Export .scene.json")) _exportSceneAction(_session.FilePath != null ? Path.ChangeExtension(_session.FilePath, ".scene.json") : "out.scene.json");
            ImGui.Separator();
            if (ImGui.MenuItem("Exit")) Environment.Exit(0);
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Edit"))
        {
            if (ImGui.MenuItem("Undo", "Ctrl+Z", false, _session.CanUndo)) { _session.Undo(); _recompile(); SetStatus("Undo"); }
            if (ImGui.MenuItem("Redo", "Ctrl+Shift+Z", false, _session.CanRedo)) { _session.Redo(); _recompile(); SetStatus("Redo"); }
            ImGui.Separator();
            if (ImGui.MenuItem("Copy", "Ctrl+C", false, _session.Selected!=null || _session.IsMultiMode)) { if(_session.CopySelected()) SetStatus("Copied to clipboard (Ctrl+V to paste)"); }
            if (ImGui.MenuItem("Cut", "Ctrl+X", false, _session.Selected!=null || _session.IsMultiMode)) { if(_session.CutSelected()) { _recompile(); SetStatus("Cut"); } }
            if (ImGui.MenuItem("Paste", "Ctrl+V", false, _session.CanPaste)) { if(_session.PasteAt()) { _recompile(); SetStatus("Pasted (+32 offset)"); } }
            ImGui.Separator();
            if (ImGui.MenuItem("Duplicate", "Ctrl+D", false, _session.Selected != null)) { if (_session.Mode==EditMode.Brush && _session.SelectedBrush!=null) { _session.DuplicateBrush(_session.SelectedBrushIndex); SetStatus("Duplicated brush"); } else { _session.DuplicateSelected(); SetStatus("Duplicated entity"); } _recompile(); }
            if (ImGui.MenuItem("Duplicate (Alt+drag)", "Alt+LMB", false, _session.Selected != null)) SetStatus("Hold Alt and drag to duplicate");
            if (ImGui.MenuItem("Delete", "Del", false, _session.Selected != null)) { _session.DeleteSelected(); _recompile(); }
            ImGui.Separator();
            if (ImGui.MenuItem("Flip X", "Ctrl+Alt+X", false, _session.SelectedBrush!=null)) { _session.FlipSelected(GizmoAxis.X); _recompile(); SetStatus("Flipped X"); }
            if (ImGui.MenuItem("Flip Y", "Ctrl+Alt+Y", false, _session.SelectedBrush!=null)) { _session.FlipSelected(GizmoAxis.Y); _recompile(); SetStatus("Flipped Y"); }
            if (ImGui.MenuItem("Flip Z", "Ctrl+Alt+Z", false, _session.SelectedBrush!=null)) { _session.FlipSelected(GizmoAxis.Z); _recompile(); SetStatus("Flipped Z"); }
            if (ImGui.MenuItem("Rotate 90°", "Ctrl+R", false, _session.SelectedBrush!=null)) { _session.RotateSelected90(); _recompile(); SetStatus("Rotated 90°"); }
            if (ImGui.MenuItem("Hollow", "", false, _session.SelectedBrush!=null && _session.IsBoxBrush(_session.SelectedBrush))) { if(_session.HollowSelected()) { _recompile(); SetStatus("Hollowed"); } else SetStatus("Hollow failed — too small or not box"); }
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Brush"))
        {
            if (ImGui.MenuItem("Move Brush", "Alt+drag / Gizmo")) SetStatus("Move: gizmo, drag, arrows (Alt+arrows extrude)");
            if (ImGui.MenuItem("Extrude Face", "Face tool + drag/wheel/arrows")) SetStatus("Face: pick face squares, drag along normal");
            if (ImGui.MenuItem("Move Vertex", "Vertex tool + drag/arrows")) SetStatus("Vertex: pick green cross, drag/arrows + X/Y/Z lock");
            if (ImGui.MenuItem("Move Edge", "Edge tool (5) — pick yellow edge, drag")) SetStatus("Edge: pick handle, drag + X/Y/Z lock");
            ImGui.Separator();
            if (ImGui.MenuItem("Create Arch...", "Ctrl+Shift+A")) _showArchDialog = true;
            ImGui.Separator();
            if (ImGui.MenuItem("CSG Subtract", "Ctrl+Shift+S", false, _session.MultiSelectedBrushes.Count>=1)) { if(_session.CsgSubtract()){ _recompile(); SetStatus("CSG Subtract"); } else SetStatus("CSG Subtract failed — need 2 brushes (select target + cutter)"); }
            if (ImGui.MenuItem("CSG Intersect", "Ctrl+Shift+I", false, _session.MultiSelectedBrushes.Count>=2)) { if(_session.CsgIntersect()){ _recompile(); SetStatus("CSG Intersect"); } else SetStatus("CSG Intersect failed"); }
            if (ImGui.MenuItem("CSG Union", "Ctrl+Shift+U", false, _session.MultiSelectedBrushes.Count>=2)) { if(_session.CsgUnion()){ _recompile(); SetStatus("CSG Union"); } else SetStatus("Union failed"); }
            ImGui.Separator();
            if (ImGui.MenuItem("Unlink Brushes (explode)", "Ctrl+Shift+G", false, _session.Selected != null && _session.Selected.Brushes.Count > 1))
            { if (_session.UnlinkSelectedBrushes()) { _recompile(); SetStatus("Unlinked — each brush is now independent (no linked system)"); } else SetStatus("Unlink failed"); }
            if (ImGui.MenuItem("Group Highlighted", "Ctrl+G", false, _session.IsMultiMode)) { if (_session.GroupHighlightedBrushes()) { _recompile(); SetStatus("Grouped highlighted into one entity — now linked"); } else SetStatus("Group failed"); }
            bool link = _session.LinkBrushes;
            if (ImGui.MenuItem("Link Highlighted Move", "", link)) { _session.LinkBrushes = !link; SetStatus(link ? "Link OFF — brushes independent" : "Link ON — Ctrl+highlighted move together"); }
            if (ImGui.MenuItem("Clip: set plane (X)", "X", _session.Mode==EditMode.Clip)) { _session.Mode=EditMode.Clip; SetStatus("Clip (X): click 2-3 points, Enter keep front, Shift+Enter keep back, Ctrl+Enter split both"); }
            if (ImGui.MenuItem("Clip: Execute Front", "Enter", _session.ClipPoints.Count>=2)) { if(_session.ExecuteClip(true,false)) { _recompile(); SetStatus("Clipped — kept front"); } else SetStatus("Clip failed — plane didn't intersect"); }
            if (ImGui.MenuItem("Clip: Execute Back", "Shift+Enter", _session.ClipPoints.Count>=2)) { if(_session.ExecuteClip(false,false)) { _recompile(); SetStatus("Clipped — kept back"); } else SetStatus("Clip failed"); }
            if (ImGui.MenuItem("Clip: Split Both", "Ctrl+Enter", _session.ClipPoints.Count>=2)) { if(_session.ExecuteClip(true,true)) { _recompile(); SetStatus("Split — kept both halves"); } else SetStatus("Split failed"); }
            if (ImGui.MenuItem("Clip: Clear Points", "Esc", _session.ClipPoints.Count>0)) { _session.ClearClipPoints(); SetStatus("Clip points cleared"); }
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("View"))
        {
            if (ImGui.MenuItem("Frame Selection", "F")) SetStatus("Press F in viewport");
            if (ImGui.MenuItem("Grid Snap", "G", _session.GridSnapEnabled)) _session.GridSnapEnabled = !_session.GridSnapEnabled;
            ImGui.Separator();
            if (ImGui.MenuItem("Scene Browser", "", _showSceneBrowser)) _showSceneBrowser=!_showSceneBrowser;
            if (ImGui.MenuItem("Material Browser", "", _showMaterialBrowser)) _showMaterialBrowser=!_showMaterialBrowser;
            if (ImGui.MenuItem("Entity Palette", "", _showEntityPalette)) _showEntityPalette=!_showEntityPalette;
            if (ImGui.MenuItem("2D Views (Top/Front/Side)", "", _showOrtho)) _showOrtho=!_showOrtho;
            if (ImGui.MenuItem("Search / Entity Report", "Ctrl+F", ShowSearchReport)) ShowSearchReport=!ShowSearchReport;
            ImGui.Separator();
            if (ImGui.MenuItem("Reset Layout", "F12")) ResetLayout();
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Window"))
        {
            if (ImGui.MenuItem("Reset Layout to Default")) ResetLayout();
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Help"))
        {
            if (ImGui.MenuItem("Controls")) ImGui.OpenPopup("HelpPopup");
            ImGui.EndMenu();
        }
        string dirty = _session.Dirty ? "● Unsaved" : "Saved";
        string path = Path.GetFileName(_session.FilePath ?? "(unsaved)");
        string tool = _session.Mode.ToString();
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowWidth() - 420));
        ImGui.TextDisabled($"{path} | {dirty} | {tool} | Grid:{_session.GridSize:0}");
        if (ImGui.BeginPopup("HelpPopup"))
        {
            ImGui.TextWrapped(EditorShortcuts.HelpText);
            ImGui.EndPopup();
        }
        ImGui.EndMainMenuBar();
    }

    void SetTool(EditMode m) { _session.Mode = m; _recompile(); SetStatus($"Tool: {m}"); }

    void DrawToolbar(Vector2 vp)
    {
        float h = 30f;
        ImGui.SetNextWindowPos(new Vector2(0, ImGui.GetFrameHeight()));
        ImGui.SetNextWindowSize(new Vector2(vp.X, h));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(6, 3));
        ImGui.Begin("##Toolbar", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking);
        ImGui.PopStyleVar(3);

        void ToolBtn(string label, EditMode m, string tip)
        {
            bool a = _session.Mode == m;
            if (a) ImGui.PushStyleColor(ImGuiCol.Button, ColAccent);
            if (ImGui.Button(label, new Vector2(58, 20))) SetTool(m);
            if (a) ImGui.PopStyleColor();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
            ImGui.SameLine();
        }
        ToolBtn("Select", EditMode.Object, "Pick / move (Q / 1)");
        ToolBtn("Brush", EditMode.Brush, "Draw brushes — default (B / 2). Drag empty space. Alt+Arrows extrude");
        ToolBtn("Face", EditMode.Face, "Pick + extrude face (3) — arrows/wheel");
        ToolBtn("Vertex", EditMode.Vertex, "Edit brush corners (4) — arrows/drag move corner");
        ToolBtn("Edge", EditMode.Edge, "Edge (5) — pick yellow edge, drag");
        ToolBtn("Clip", EditMode.Clip, "Clip brush by plane (X) — 2-3 clicks define plane, Enter to clip");
        ToolBtn("Entity", EditMode.Entity, "Entity (E) — click to place point entity");
        ImGui.TextDisabled("|"); ImGui.SameLine();

        bool snap = _session.GridSnapEnabled;
        if (ImGui.Checkbox("Snap", ref snap)) _session.GridSnapEnabled = snap;
        ImGui.SameLine();
        float gs = _session.GridSize;
        ImGui.SetNextItemWidth(80);
        if (ImGui.SliderFloat("##grid", ref gs, 1, 64, "Grid %.0f")) _session.GridSize = Math.Clamp(gs, 1, 64);
        ImGui.SameLine(); ImGui.TextDisabled("|"); ImGui.SameLine();

        if (!_newBrushInit) { _newBrushSize = _session.BrushDefaultSize; _newBrushInit = true; }
        if (!_newBrushTexInit) { _newBrushTex = _session.DefaultTexture; _newBrushTexInit = true; }
        ImGui.TextDisabled("Brush"); ImGui.SameLine();
        ImGui.SetNextItemWidth(44); ImGui.DragFloat("##bw", ref _newBrushSize.X, 1, 8, 2048, "%.0f"); ImGui.SameLine();
        ImGui.SetNextItemWidth(44); ImGui.DragFloat("##bd", ref _newBrushSize.Y, 1, 8, 2048, "%.0f"); ImGui.SameLine();
        ImGui.SetNextItemWidth(44); ImGui.DragFloat("##bh", ref _newBrushSize.Z, 1, 8, 2048, "%.0f");
        _newBrushSize.X = Math.Clamp(_newBrushSize.X, 8, 4096); _newBrushSize.Y = Math.Clamp(_newBrushSize.Y, 8, 4096); _newBrushSize.Z = Math.Clamp(_newBrushSize.Z, 8, 4096);
        _session.BrushDefaultSize = _newBrushSize;
        ImGui.SameLine(); ImGui.SetNextItemWidth(90);
        if (ImGui.InputTextWithHint("##tex", "texture", ref _newBrushTex, 64)) _session.DefaultTexture = string.IsNullOrWhiteSpace(_newBrushTex) ? "wall" : _newBrushTex.Trim();
        ImGui.SameLine();
        if (ImGui.Button("New Cube")) { var s=_session.BrushDefaultSize; var c=new Vector3(0,0,s.Z*0.5f); _session.CreateBoxBrush(c-s*0.5f,c+s*0.5f); _recompile(); }
        ImGui.SameLine();
        if (ImGui.Button("Arch")) _showArchDialog = true;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Create arch — doorway/tunnel (Shift+A)");

        ImGui.SameLine(); ImGui.SetNextItemWidth(120); if(ImGui.InputTextWithHint("##toolbarSearch","Search...", ref _searchQuery, 64)){ ShowSearchReport=true; }
        ImGui.SameLine(); if(ImGui.Button("Find")) ShowSearchReport=!ShowSearchReport;
        ImGui.SameLine(); ImGui.TextDisabled("|"); ImGui.SameLine();
        if (ImGui.Button("Reset UI")) ResetLayout();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Reset all panels to default — Scene/Browser/Materials/2D Views (F12)");
        ImGui.SameLine(); ImGui.TextDisabled("|"); ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.16f,0.55f,0.30f,1f));
        if (ImGui.Button("Play")) LaunchPlay();
        ImGui.PopStyleColor();

        ImGui.End();
    }

    void DrawOutliner()
    {
        ImGui.SetNextWindowSize(new Vector2(320, 500), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Scene Browser")) { ImGui.End(); return; }
        // Layers row
        ImGui.TextDisabled("Layers:");
        ImGui.SameLine();
        foreach(var layer in _session.AllLayers)
        {
            bool vis=_session.IsLayerVisible(layer);
            string lbl = (vis?"● ":"○ ")+layer + (layer==_session.ActiveLayer?" ★":"");
            if(vis) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.85f,0.85f,1f,1f));
            if(ImGui.SmallButton(lbl)){ _session.SetLayerVisible(layer, !vis); _recompile(); SetStatus($"{layer} {(vis?"hidden":"shown")}"); }
            if(vis) ImGui.PopStyleColor();
            ImGui.SameLine();
        }
        ImGui.NewLine();
        string newLayer=""; // inline create
        ImGui.SetNextItemWidth(100); if(ImGui.InputTextWithHint("##newlayer","New layer", ref newLayer, 32, ImGuiInputTextFlags.EnterReturnsTrue) && !string.IsNullOrWhiteSpace(newLayer)){ _session.ActiveLayer=newLayer; _session.SetLayerVisible(newLayer,true); SetStatus($"Active layer {newLayer}"); }
        ImGui.SameLine(); ImGui.TextDisabled($"Active:{_session.ActiveLayer}");
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("New brushes/entities go to active layer");
        ImGui.Separator();
        ImGui.TextDisabled("worldspawn = world geometry. Each brush below is independent.");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##filter", "Filter brush/texture...", ref _filter, 128);
        string low = _filter.ToLowerInvariant();
        if (ImGui.Button("Clear Multi", new Vector2(-1,0))) { _session.ClearAllMulti(); _recompile(); }
        ImGui.Separator();
        ImGui.BeginChild("##list", new Vector2(0, -24), ImGuiChildFlags.Borders);
        for (int i=0;i<_session.Entities.Count;i++)
        {
            var e=_session.Entities[i];
            // worldspawn with multiple brushes — show each brush as its own row (unlinked)
            if (e.ClassName=="worldspawn" && e.Brushes.Count>1)
            {
                string header = $"World  [{e.Brushes.Count} brushes]  — each moves alone";
                bool open = ImGui.TreeNodeEx($"##w{i}", ImGuiTreeNodeFlags.DefaultOpen, header);
                // header click = clear brush multi
                if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen()) { _session.ClearAllMulti(); _session.Select(i); _recompile(); }
                if (open)
                {
                    for (int bi=0; bi<e.Brushes.Count; bi++)
                    {
                        var br=e.Brushes[bi];
                        string hay = (br.Faces.FirstOrDefault()?.Texture ?? "") + $" {bi}";
                        if (!string.IsNullOrWhiteSpace(low) && !hay.ToLowerInvariant().Contains(low)) continue;
                        _session.GetSelectedBounds(out var _mn, out var _mx); // not used
                        KREAN.MapCompiler.BrushManipulation.GetBounds(br, out var mn, out var mx);
                        string label = $"  #{bi}: {br.Faces[0].Texture}  {mx.X-mn.X:0}x{mx.Y-mn.Y:0}x{mx.Z-mn.Z:0}";
                        bool selBrush = (i==_session.SelectedIndex && bi==_session.SelectedBrushIndex) || _session.IsMultiSelected(i, bi);
                        if (selBrush && _session.IsMultiSelected(i, bi)) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f,0.55f,0.15f,1f));
                        else if (selBrush) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.35f,0.85f,1f,1f));
                        bool clicked = ImGui.Selectable(label, selBrush);
                        if (selBrush) ImGui.PopStyleColor();
                        if (clicked)
                        {
                            bool ctrl = ImGui.GetIO().KeyCtrl;
                            if (ctrl)
                            {
                                if (_session.MultiSelectedBrushes.Count==0 && _session.SelectedBrush!=null) _session.MultiSelectedBrushes.Add((_session.SelectedIndex,_session.SelectedBrushIndex));
                                _session.ToggleMultiBrush(i, bi);
                                _session.SelectBrush(i, bi, -1);
                                SetStatus($"Multi brushes: { _session.MultiSelectedBrushes.Count} — Ctrl+D dup, gizmo moves only primary (Link OFF)");
                            }
                            else
                            {
                                _session.ClearAllMulti();
                                _session.SelectBrush(i, bi, -1);
                                _session.Mode = EditMode.Brush;
                            }
                            _recompile(); _faceExtrude=0; _brushMoveInitialized=false;
                        }
                        if (ImGui.BeginPopupContextItem($"bctx{i}_{bi}"))
                        {
                            if (ImGui.MenuItem($"Duplicate brush {bi}")) { _session.SelectBrush(i, bi, -1); _session.DuplicateBrush(bi); _recompile(); }
                            if (ImGui.MenuItem($"Delete brush {bi}")) { _session.SelectBrush(i, bi, -1); _session.RemoveBrushAt(bi); _recompile(); }
                            if (ImGui.MenuItem("Unlink all brushes (explode)")) { _session.UnlinkSelectedBrushes(); _recompile(); }
                            ImGui.EndPopup();
                        }
                    }
                    ImGui.TreePop();
                }
                continue;
            }
            // point entity or single-brush entity
            string layer2=_session.GetEntityLayer(e);
            bool vis2=_session.IsLayerVisible(layer2);
            if (!vis2) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.45f,0.45f,0.48f,1f));
            if (!string.IsNullOrWhiteSpace(low))
            {
                string hay=(e.ClassName+" "+string.Join(" ",e.Properties.Values)+ " "+layer2).ToLowerInvariant();
                if (!hay.Contains(low)) { if(!vis2) ImGui.PopStyleColor(); continue; }
            }
            string label2 = $"{i}: {e.ClassName} [{layer2}]";
            if (e.Properties.TryGetValue("targetname", out var tn2) && !string.IsNullOrEmpty(tn2)) label2 += $" ({tn2})";
            if (e.Brushes.Count==1) { var br0=e.Brushes[0]; KREAN.MapCompiler.BrushManipulation.GetBounds(br0, out var mn0, out var mx0); label2+=$" [{mx0.X-mn0.X:0}x{mx0.Y-mn0.Y:0}]"; }
            else if (e.Brushes.Count>0) label2 += $" [{e.Brushes.Count}]";
            bool sel2=i==_session.SelectedIndex || _session.IsMultiEntitySelected(i);
            if (sel2 && _session.IsMultiEntitySelected(i)) { if(!vis2) ImGui.PopStyleColor(); ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f,0.55f,0.15f,1f)); }
            bool clicked2 = ImGui.Selectable(label2, sel2);
            if(!vis2 && !(sel2 && _session.IsMultiEntitySelected(i))) ImGui.PopStyleColor();
            if (sel2 && _session.IsMultiEntitySelected(i)) ImGui.PopStyleColor();
            if (clicked2)
            {
                bool ctrl = ImGui.GetIO().KeyCtrl;
                if (ctrl)
                {
                    if (_session.IsMultiEntitySelected(i) && _session.MultiSelectedEntities.Count==1 && _session.SelectedIndex==i) { _session.ClearMultiEntity(); _session.Select(i); }
                    else { if (_session.MultiSelectedEntities.Count==0 && _session.SelectedIndex>=0 && _session.SelectedIndex!=i) _session.MultiSelectedEntities.Add(_session.SelectedIndex); _session.ToggleMultiEntity(i); _session.Select(i); }
                    SetStatus($"Multi: { _session.MultiSelectedEntities.Count} entities");
                }
                else { _session.ClearAllMulti(); _session.Select(i); }
                _recompile(); _originInitialized=false; _faceExtrude=0; _brushMoveInitialized=false; _boundsInitialized=false;
            }
            if (ImGui.BeginPopupContextItem($"ctx{i}"))
            {
                if (ImGui.MenuItem("Duplicate")) { _session.Select(i); _session.DuplicateSelected(); _recompile(); }
                if (ImGui.MenuItem("Delete")) { _session.Select(i); _session.DeleteSelected(); _recompile(); }
                if (e.Brushes.Count>1 && ImGui.MenuItem("Unlink brushes")) { _session.Select(i); if(_session.UnlinkSelectedBrushes()){ _recompile(); SetStatus("Unlinked — each brush is now its own worldspawn"); } }
                if (ImGui.BeginMenu("Move to layer"))
                {
                    foreach(var ly in _session.AllLayers){ if(ImGui.MenuItem(ly, "", layer2==ly)){ _session.SetEntityLayer(i, ly); _recompile(); } }
                    ImGui.Separator(); string nl=""; if(ImGui.InputTextWithHint("##nl","New layer", ref nl, 32, ImGuiInputTextFlags.EnterReturnsTrue) && !string.IsNullOrWhiteSpace(nl)){ _session.SetEntityLayer(i,nl); _recompile(); }
                    ImGui.EndMenu();
                }
                if (ImGui.MenuItem(vis2?"Hide layer":"Show layer")){ _session.SetLayerVisible(layer2, !vis2); _recompile(); }
                ImGui.EndPopup();
            }
        }
        ImGui.EndChild();
        ImGui.TextDisabled($"{_session.Entities.Count} entities  •  click brush # to move alone");
        if (_session.MultiSelectedBrushes.Count>0) ImGui.TextColored(new Vector4(1,0.55f,0.15f,1), $"{_session.MultiSelectedBrushes.Count} brushes highlighted — independent (Link OFF)");
        ImGui.End();
    }

    void DrawSceneBrowser()
    {
        // reuse Outliner as Scene Browser
        DrawOutliner();
    }

    void DrawMaterialBrowser()
    {
        ImGui.SetNextWindowSize(new Vector2(260, 300), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Materials")) { ImGui.End(); return; }
        // refresh list periodically or on filter change
        _matRefreshTimer -= ImGui.GetIO().DeltaTime;
        if (_matList.Count==0 || _matRefreshTimer<=0)
        {
            _matRefreshTimer = 2f;
            var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var t in _commonTextures) discovered.Add(t);
            // scan textures folders
            foreach(var dir in new[]{"textures","assets/textures","Data/textures","Materials","materials"})
            {
                if(!Directory.Exists(dir)) continue;
                try{ foreach(var f in Directory.GetFiles(dir, "*.*", SearchOption.TopDirectoryOnly).Take(200)){ var n=Path.GetFileNameWithoutExtension(f); if(!string.IsNullOrWhiteSpace(n)) discovered.Add(n); } }catch{}
                try{ foreach(var f in Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories).Take(200)){ var n=Path.GetFileNameWithoutExtension(f); if(!string.IsNullOrWhiteSpace(n)) discovered.Add(n); } }catch{}
            }
            // also collect from map
            foreach(var e in _session.Entities) foreach(var b in e.Brushes) foreach(var f in b.Faces) if(!string.IsNullOrEmpty(f.Texture)) discovered.Add(f.Texture);
            _matList = discovered.OrderBy(s=>s).ToList();
        }
        ImGui.InputTextWithHint("##matfilter","Filter", ref _matFilter, 64);
        ImGui.TextDisabled($"{_matList.Count} materials  •  click to apply to selected face");
        ImGui.BeginChild("##matgrid", new Vector2(0, -28), ImGuiChildFlags.Borders);
        string low=_matFilter.ToLowerInvariant();
        foreach(var mat in _matList)
        {
            if(!string.IsNullOrWhiteSpace(low) && !mat.ToLowerInvariant().Contains(low)) continue;
            bool isSel = _session.DefaultTexture==mat || (_session.SelectedBrush!=null && _session.SelectedFaceIndex>=0 && _session.SelectedBrush.Faces[_session.SelectedFaceIndex].Texture==mat);
            if(isSel) ImGui.PushStyleColor(ImGuiCol.Button, ColAccent);
            // color preview
            var col = KREAN.Runtime.Rendering.MaterialPalette.ColorFor(mat);
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(col.X*0.6f, col.Y*0.6f, col.Z*0.6f, 1));
            if(ImGui.Button(mat, new Vector2(-1, 20))) { _session.DefaultTexture=mat; _newBrushTex=mat; if(_session.SelectedBrush!=null && _session.SelectedFaceIndex>=0){ _session.SelectedBrush.Faces[_session.SelectedFaceIndex].Texture=mat; _recompile(); SetStatus($"Texture {mat}"); } }
            ImGui.PopStyleColor();
            if(isSel) ImGui.PopStyleColor();
        }
        ImGui.EndChild();
        if(ImGui.Button("Apply to Brush", new Vector2(-1,0)) && _session.SelectedBrush!=null){ foreach(var f in _session.SelectedBrush.Faces) f.Texture=_session.DefaultTexture; _recompile(); }
        ImGui.End();
    }

    void DrawEntityPalette()
    {
        EnsureFgd();
        ImGui.SetNextWindowSize(new Vector2(280, 380), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Entities")) { ImGui.End(); return; }
        ImGui.InputTextWithHint("##efilter","Filter classname", ref _entityFilter, 64);
        string low=_entityFilter.ToLowerInvariant();
        ImGui.BeginChild("##entlist", new Vector2(0,-28), ImGuiChildFlags.Borders);
        foreach(var fgd in _fgdClasses.OrderBy(c=>c.ClassName))
        {
            var c=fgd.ClassName;
            if(!string.IsNullOrWhiteSpace(low) && !c.ToLowerInvariant().Contains(low) && !fgd.Description.ToLowerInvariant().Contains(low)) continue;
            var col=fgd.Color;
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(col.X, col.Y, col.Z, 1f));
            bool sel = ImGui.Selectable($"{c}##{c}", false);
            ImGui.PopStyleColor();
            if(ImGui.IsItemHovered()) ImGui.SetTooltip($"{fgd.Kind}: {fgd.Description}");
            if(sel){ _session.Mode=EditMode.Entity; SetStatus($"Entity tool: {c} — click in 3D to place"); _entityFilter=c; }
        }
        ImGui.EndChild();
        ImGui.TextDisabled("Select class, then click in viewport (Entity mode).");
        string curFilt=_entityFilter;
        if(ImGui.Button($"Place {curFilt} at origin", new Vector2(-1,0))){ string cls=AllClassNames.FirstOrDefault(x=>x==curFilt) ?? "light"; _session.AddEntity(cls, new Vector3(0,0,32)); _recompile(); }
        ImGui.End();
    }

    void DrawSearchReport()
    {
        ImGui.SetNextWindowSize(new Vector2(420, 320), ImGuiCond.FirstUseEver);
        if(!ImGui.Begin("Search / Entity Report")){ ImGui.End(); return; }
        ImGui.InputTextWithHint("##search","Search classname / property / texture / layer...", ref _searchQuery, 128);
        string low=_searchQuery.ToLowerInvariant();
        var matches=new List<int>();
        if(!string.IsNullOrWhiteSpace(low))
        {
            for(int i=0;i<_session.Entities.Count;i++)
            {
                var e=_session.Entities[i];
                string hay = e.ClassName + " " + string.Join(" ", e.Properties.Select(kv=>kv.Key+" "+kv.Value)) + " " + _session.GetEntityLayer(e);
                if(hay.ToLowerInvariant().Contains(low)) { matches.Add(i); continue; }
                // also check textures
                foreach(var b in e.Brushes) foreach(var f in b.Faces) if(f.Texture.ToLowerInvariant().Contains(low)){ matches.Add(i); break; }
            }
        }
        ImGui.TextDisabled($"{matches.Count} matches / {_session.Entities.Count} entities");
        ImGui.BeginChild("##sresults", new Vector2(0,120), ImGuiChildFlags.Borders);
        foreach(var idx in matches.Take(200))
        {
            var e=_session.Entities[idx];
            string label = $"{idx}: {e.ClassName} [{_session.GetEntityLayer(e)}]";
            if(e.Properties.TryGetValue("targetname", out var tn) && !string.IsNullOrEmpty(tn)) label+=$" ({tn})";
            bool sel = idx==_session.SelectedIndex;
            if(ImGui.Selectable(label, sel)){ _session.ClearAllMulti(); _session.Select(idx); _recompile(); SetStatus($"Selected {idx}"); }
            if(ImGui.IsItemHovered()) ImGui.SetTooltip(string.Join("\n", e.Properties.Select(kv=>kv.Key+" = "+kv.Value)));
        }
        ImGui.EndChild();
        ImGui.Separator();
        ImGui.Text("Replace:");
        ImGui.SetNextItemWidth(120); ImGui.InputTextWithHint("##rkey","key", ref _replaceKey, 64); ImGui.SameLine();
        ImGui.SetNextItemWidth(120); ImGui.InputTextWithHint("##rval","value (empty=remove)", ref _replaceValue, 64); ImGui.SameLine();
        if(ImGui.Button("Replace in matches") && !string.IsNullOrWhiteSpace(_replaceKey) && matches.Count>0)
        {
            _session.BeginUndoGroup("replace");
            int cnt=0;
            foreach(var idx in matches)
            {
                var e=_session.Entities[idx];
                if(string.IsNullOrEmpty(_replaceValue)) { if(e.Properties.ContainsKey(_replaceKey)){ e.Properties.Remove(_replaceKey); cnt++; } }
                else { e.Properties[_replaceKey]=_replaceValue; cnt++; }
            }
            _session.EndUndoGroup(true);
            _recompile();
            SetStatus($"Replaced {_replaceKey} in {cnt} entities");
        }
        ImGui.SameLine();
        if(ImGui.Button("Select all matches"))
        {
            _session.ClearAllMulti();
            foreach(var idx in matches) _session.MultiSelectedEntities.Add(idx);
            if(matches.Count>0) _session.Select(matches[0]);
            _recompile();
            SetStatus($"Selected {matches.Count} matches");
        }
        ImGui.End();
    }

    void DrawInspector()
    {
        ImGui.SetNextWindowSize(new Vector2(320, 500), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Inspector")) { ImGui.End(); return; }
        var sel=_session.Selected;
        if (sel==null) { ImGui.TextDisabled("No selection."); ImGui.TextDisabled(_session.Mode==EditMode.Brush?"Drag empty space to draw a brush.":"Click to select."); ImGui.End(); return; }

        ImGui.Text($"{_session.SelectedIndex}: "); ImGui.SameLine(); ImGui.TextColored(ColAccent, sel.ClassName);
        ImGui.Separator();

        if (_session.Mode == EditMode.Clip || _session.ClipPoints.Count>0)
        {
            if (ImGui.CollapsingHeader("Clip Tool (X)", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Text($"Points: {_session.ClipPoints.Count}/3");
                for(int i=0;i<_session.ClipPoints.Count;i++) { var p=_session.ClipPoints[i]; ImGui.Text($"  {i}: {p.X:0},{p.Y:0},{p.Z:0}"); ImGui.SameLine(); if(ImGui.SmallButton($"x##cp{i}")){ _session.ClipPoints.RemoveAt(i); _recompile(); } }
                if (_session.ClipPoints.Count>=2)
                {
                    if (ImGui.Button("Keep Front (Enter)")) { if(_session.ExecuteClip(true,false)) { _recompile(); SetStatus("Clipped front"); } else SetStatus("Clip failed"); }
                    ImGui.SameLine(); if (ImGui.Button("Keep Back (Shift+Enter)")) { if(_session.ExecuteClip(false,false)) { _recompile(); SetStatus("Clipped back"); } else SetStatus("Clip failed"); }
                    if (ImGui.Button("Split Both (Ctrl+Enter)")) { if(_session.ExecuteClip(true,true)) { _recompile(); SetStatus("Split both"); } else SetStatus("Split failed"); }
                }
                if (ImGui.Button("Clear Points (Esc)")) { _session.ClearClipPoints(); SetStatus("Clip cleared"); }
                ImGui.TextDisabled("X to enter Clip, click brush surface to place points");
            }
        }

        // --- Entity -------------------------------------------------------------
        if (ImGui.CollapsingHeader("Entity", ImGuiTreeNodeFlags.DefaultOpen))
        {
            EnsureFgd();
            string cur=sel.ClassName;
            if (ImGui.BeginCombo("Class", cur))
            {
                foreach(var c in AllClassNames) { bool s=c==cur; var fgd=FindFgd(c); if(fgd!=null) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(fgd.Color.X, fgd.Color.Y, fgd.Color.Z, 1f)); if(ImGui.Selectable(c,s)){ _session.SetClassName(c); _recompile(); } if(fgd!=null) ImGui.PopStyleColor(); if(s) ImGui.SetItemDefaultFocus(); }
                ImGui.EndCombo();
            }
            var curFgd=FindFgd(cur);
            if(curFgd!=null) ImGui.TextDisabled($"{curFgd.Kind}: {curFgd.Description}");
            _classEdit=cur;
            if (ImGui.InputTextWithHint("##cls", "custom + Enter", ref _classEdit, 64, ImGuiInputTextFlags.EnterReturnsTrue) && _classEdit!=cur && !string.IsNullOrWhiteSpace(_classEdit)) { _session.SetClassName(_classEdit); _recompile(); }

            if (sel.Properties.TryGetValue("origin", out var o))
            {
                if (!_originInitialized) { _originEdit=MapEditorSession.ParseVec3Public(o); _originInitialized=true; }
                var parsed=MapEditorSession.ParseVec3Public(o);
                if (Vector3.Distance(parsed,_originEdit)>0.01f && !ImGui.IsAnyItemActive()) _originEdit=parsed;
                ImGui.TextDisabled("origin");
                bool ch=false;
                ImGui.SetNextItemWidth(80); ch|=ImGui.DragFloat("X##o", ref _originEdit.X,1,-8192,8192,"%.0f");
                ImGui.SameLine(); ImGui.SetNextItemWidth(80); ch|=ImGui.DragFloat("Y##o", ref _originEdit.Y,1,-8192,8192,"%.0f");
                ImGui.SameLine(); ImGui.SetNextItemWidth(80); ch|=ImGui.DragFloat("Z##o", ref _originEdit.Z,1,-8192,8192,"%.0f");
                if(ch){ sel.Properties["origin"]=$"{Fmt(_originEdit.X)} {Fmt(_originEdit.Y)} {Fmt(_originEdit.Z)}"; _recompile(); }
                if(ImGui.IsItemDeactivatedAfterEdit()){ sel.Properties["origin"]=$"{Fmt(parsed.X)} {Fmt(parsed.Y)} {Fmt(parsed.Z)}"; _session.SetOriginQuake(_originEdit); _recompile(); }
            }
            if (sel.Properties.ContainsKey("angle") || sel.Properties.ContainsKey("angles"))
            {
                float ang=0; if(sel.Properties.TryGetValue("angle",out var a)) float.TryParse(a,NumberStyles.Float,CultureInfo.InvariantCulture,out ang); else if(sel.Properties.TryGetValue("angles",out var ag)) ang=MapEditorSession.ParseVec3Public(ag).Y;
                float e=ang; if(ImGui.SliderFloat("yaw", ref e, 0,360,"%.0f°")){ sel.Properties["angle"]=Fmt(e); sel.Properties.Remove("angles"); _recompile(); }
                if(ImGui.IsItemDeactivatedAfterEdit()){ sel.Properties["angle"]=Fmt(ang); _session.RotateSelectedYaw(e-ang); _recompile(); }
            }
            if (sel.ClassName=="light") DrawLight();
            // layer
            string curLayer=_session.GetEntityLayer(sel);
            if(ImGui.BeginCombo("Layer", curLayer))
            {
                foreach(var ly in _session.AllLayers){ bool s=ly==curLayer; if(ImGui.Selectable(ly,s)){ _session.SetEntityLayer(_session.SelectedIndex, ly); _recompile(); } if(s) ImGui.SetItemDefaultFocus(); }
                ImGui.EndCombo();
            }
            string newLy=""; ImGui.SetNextItemWidth(120); if(ImGui.InputTextWithHint("##newly","New layer +Enter", ref newLy, 32, ImGuiInputTextFlags.EnterReturnsTrue) && !string.IsNullOrWhiteSpace(newLy)){ _session.SetEntityLayer(_session.SelectedIndex, newLy); _recompile(); }
            ImGui.SameLine(); bool visCur=_session.IsLayerVisible(curLayer); if(ImGui.Checkbox("Visible", ref visCur)){ _session.SetLayerVisible(curLayer, visCur); _recompile(); }
            if(curFgd!=null && curFgd.Properties.Count>0)
            {
                ImGui.TextDisabled("FGD props:");
                foreach(var fp in curFgd.Properties)
                {
                    if(sel.Properties.ContainsKey(fp.Name)) continue;
                    ImGui.SameLine();
                    if(ImGui.SmallButton($"+{fp.Name}")){ _session.SetProperty(fp.Name, string.IsNullOrEmpty(fp.DefaultValue)? "0": fp.DefaultValue); _recompile(); }
                    if(ImGui.IsItemHovered()) ImGui.SetTooltip($"{fp.Type}: {fp.Description}");
                }
            }
        }

        // --- Properties (key/values) — TrenchBroom style -----------------------
        if (ImGui.CollapsingHeader("Properties", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (ImGui.BeginTable("kv", 3, ImGuiTableFlags.Borders|ImGuiTableFlags.RowBg|ImGuiTableFlags.Resizable|ImGuiTableFlags.ScrollY, new Vector2(0,140)))
            {
                ImGui.TableSetupColumn("Key", ImGuiTableColumnFlags.WidthFixed, 90);
                ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 18);
                ImGui.TableHeadersRow();
                var keys=sel.Properties.Keys.ToList(); string? del=null;
                foreach(var k in keys)
                {
                    string v=sel.Properties[k];
                    ImGui.TableNextRow(); ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(k);
                    ImGui.TableSetColumnIndex(1); ImGui.PushID(k); string ed=v; ImGui.SetNextItemWidth(-1);
                    if(ImGui.InputText($"##{k}", ref ed, 256, ImGuiInputTextFlags.EnterReturnsTrue) && ed!=v){ _session.SetProperty(k,ed); _recompile(); }
                    ImGui.PopID(); ImGui.TableSetColumnIndex(2); if(ImGui.SmallButton($"x##{k}")) del=k;
                }
                if(del!=null){ _session.RemoveProperty(del); _recompile(); }
                ImGui.EndTable();
            }
            ImGui.SetNextItemWidth(90); ImGui.InputTextWithHint("##nk","key",ref _newKey,64); ImGui.SameLine(); ImGui.SetNextItemWidth(-36); ImGui.InputTextWithHint("##nv","value",ref _newValue,256);
            ImGui.SameLine(); if(ImGui.Button("+") && !string.IsNullOrWhiteSpace(_newKey)){ _session.SetProperty(_newKey.Trim(),_newValue); _newKey=""; _newValue=""; _recompile(); }
        }

        // --- Brush / Face — TrenchBroom Face tab --------------------------------
        if (sel.Brushes.Count>0)
        {
            if (ImGui.CollapsingHeader($"Brushes ({sel.Brushes.Count})", ImGuiTreeNodeFlags.DefaultOpen))
            {
                _session.GetSelectedBounds(out var mn, out var mx);
                if(mn.X<=mx.X)
                {
                    var sz=mx-mn; ImGui.TextDisabled($"bounds {sz.X:0} x {sz.Y:0} x {sz.Z:0}");
                    if(!_brushMoveInitialized){ _brushMoveEdit=Vector3.Zero; _brushMoveInitialized=true; }
                    ImGui.SetNextItemWidth(60); ImGui.DragFloat("dX", ref _brushMoveEdit.X,1,-4096,4096,"%.0f"); ImGui.SameLine();
                    ImGui.SetNextItemWidth(60); ImGui.DragFloat("dY", ref _brushMoveEdit.Y,1,-4096,4096,"%.0f"); ImGui.SameLine();
                    ImGui.SetNextItemWidth(60); ImGui.DragFloat("dZ", ref _brushMoveEdit.Z,1,-4096,4096,"%.0f"); ImGui.SameLine();
                    if(ImGui.Button("Move")){ if(_brushMoveEdit.LengthSquared()>1e-6f){ if(_session.Mode==EditMode.Brush) _session.TranslateSelectedBrush(_brushMoveEdit); else _session.TranslateSelectedEntity(_brushMoveEdit); _brushMoveEdit=Vector3.Zero; _recompile(); } }
                }
                if (_session.Mode==EditMode.Face && _session.SelectedBrush!=null && _session.SelectedFaceIndex>=0)
                {
                    ImGui.Text($"Face {_session.SelectedFaceIndex} — arrows / drag / wheel extrude");
                    ImGui.SetNextItemWidth(100); ImGui.SliderFloat("##ext", ref _faceExtrude, -64,64,"%.0f"); ImGui.SameLine();
                    if(ImGui.Button("Push")){ _session.MoveSelectedFace(_faceExtrude); _faceExtrude=0; _recompile(); }
                    ImGui.SameLine(); if(ImGui.Button("-8")){ _session.MoveSelectedFace(-8); _recompile(); }
                    ImGui.SameLine(); if(ImGui.Button("+8")){ _session.MoveSelectedFace(8); _recompile(); }
                    ImGui.TextDisabled("Tip: Arrow keys also extrude along normal");
                }
                if (_session.Mode==EditMode.Vertex && _session.SelectedBrush!=null)
                {
                    var corners=_session.GetSelectedBrushCorners();
                    if (corners.Length>0)
                    {
                        ImGui.Text(corners.Length==8 ? $"Vertex {_session.SelectedVertexIndex} / 8 corners — arrows move, X/Y/Z lock" : "Vertex: box brush only");
                        if (corners.Length==8)
                        {
                            int vi=_session.SelectedVertexIndex;
                            ImGui.TextDisabled(vi>=0 ? $"Selected {vi}: {corners[vi].X:0},{corners[vi].Y:0},{corners[vi].Z:0}" : "Click a green cross in 3D or buttons below");
                            for(int i=0;i<8;i++)
                            {
                                bool isSel=i==vi;
                                if(isSel) ImGui.PushStyleColor(ImGuiCol.Button, ColAccent);
                                if(ImGui.Button($"V{i}", new Vector2(32,20))) { _session.SelectVertex(_session.SelectedIndex, _session.SelectedBrushIndex, i); _recompile(); }
                                if(isSel) ImGui.PopStyleColor();
                                if(ImGui.IsItemHovered()) ImGui.SetTooltip($"{corners[i].X:0},{corners[i].Y:0},{corners[i].Z:0} — click to select, arrows to move");
                                if(i<7) ImGui.SameLine();
                            }
                            ImGui.TextDisabled("Arrows: move corner  •  Shift 8x  •  X/Y/Z hold to lock axis");
                            if(_session.SelectedVertexIndex>=0)
                            {
                                var c=corners[_session.SelectedVertexIndex];
                                Vector3 edit=c;
                                ImGui.SetNextItemWidth(80); if(ImGui.DragFloat("VX", ref edit.X,1,-8192,8192,"%.0f")){ _session.MoveSelectedVertex(new Vector3(edit.X-c.X,0,0)); _recompile(); }
                                ImGui.SameLine(); ImGui.SetNextItemWidth(80); if(ImGui.DragFloat("VY", ref edit.Y,1,-8192,8192,"%.0f")){ _session.MoveSelectedVertex(new Vector3(0,edit.Y-c.Y,0)); _recompile(); }
                                ImGui.SameLine(); ImGui.SetNextItemWidth(80); if(ImGui.DragFloat("VZ", ref edit.Z,1,-8192,8192,"%.0f")){ _session.MoveSelectedVertex(new Vector3(0,0,edit.Z-c.Z)); _recompile(); }
                            }
                        }
                    }
                    else ImGui.TextDisabled("Select a brush with 6 faces to edit vertices");
                }
                if (_session.Mode==EditMode.Edge && _session.SelectedBrush!=null)
                {
                    var mids=_session.GetSelectedEdgeMidpoints();
                    if(mids.Length==12)
                    {
                        int ei=_session.SelectedEdgeIndex;
                        ImGui.Text(ei>=0? $"Edge {ei} — drag + X/Y/Z lock" : "Edge: pick yellow cross (5)");
                        for(int i=0;i<12;i++){ bool isSel=i==ei; if(isSel) ImGui.PushStyleColor(ImGuiCol.Button, ColAccent); if(ImGui.Button($"E{i}", new Vector2(30,20))){ _session.SelectEdge(_session.SelectedIndex,_session.SelectedBrushIndex,i); _recompile(); } if(isSel) ImGui.PopStyleColor(); if(i<11) ImGui.SameLine(); }
                        if(ei>=0){ var m=mids[ei]; ImGui.TextDisabled($"Mid {m.X:0},{m.Y:0},{m.Z:0}"); Vector3 edit=m; ImGui.SetNextItemWidth(80); if(ImGui.DragFloat("EX", ref edit.X,1,-8192,8192,"%.0f")){_session.MoveSelectedEdge(new Vector3(edit.X-m.X,0,0)); _recompile();} ImGui.SameLine(); ImGui.SetNextItemWidth(80); if(ImGui.DragFloat("EY", ref edit.Y,1,-8192,8192,"%.0f")){_session.MoveSelectedEdge(new Vector3(0,edit.Y-m.Y,0)); _recompile();} ImGui.SameLine(); ImGui.SetNextItemWidth(80); if(ImGui.DragFloat("EZ", ref edit.Z,1,-8192,8192,"%.0f")){_session.MoveSelectedEdge(new Vector3(0,0,edit.Z-m.Z)); _recompile();} }
                    } else ImGui.TextDisabled("Box brush only");
                }
                for(int bi=0;bi<sel.Brushes.Count;bi++)
                {
                    var br=sel.Brushes[bi];
                    bool isSel=bi==_session.SelectedBrushIndex;
                    bool open=ImGui.TreeNodeEx($"Brush {bi} ({br.Faces.Count} faces){(isSel?" ◀":"")}", ImGuiTreeNodeFlags.OpenOnArrow);
                    ImGui.SameLine(); if(ImGui.SmallButton($"Sel##{bi}")){ _session.SelectBrush(_session.SelectedIndex,bi,-1); _recompile(); }
                    ImGui.SameLine(); if(ImGui.SmallButton($"Dup##{bi}")){ _session.DuplicateBrush(bi); _recompile(); }
                    if(!open) continue;
                    if(ImGui.SmallButton($"Delete##{bi}")){ _session.RemoveBrushAt(bi); _recompile(); ImGui.TreePop(); break; }
                    for(int fi=0;fi<br.Faces.Count;fi++)
                    {
                        var f=br.Faces[fi]; bool isFace=isSel&&fi==_session.SelectedFaceIndex;
                        ImGui.PushID($"b{bi}f{fi}");
                        if(isFace) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1,0.35f,0.15f,1));
                        string tex=f.Texture; ImGui.SetNextItemWidth(90);
                        if(ImGui.InputText($"##tex{fi}", ref tex, 64, ImGuiInputTextFlags.EnterReturnsTrue)){ f.Texture=tex; _recompile(); }
                        ImGui.SameLine();
                        if(ImGui.Selectable($"{(isFace?"● ":"")}Face {fi} n({f.Normal.X:0.0},{f.Normal.Y:0.0},{f.Normal.Z:0.0})", isFace)){ _session.SelectBrush(_session.SelectedIndex,bi,fi); _session.Mode=EditMode.Face; _recompile(); }
                        if(isFace) ImGui.PopStyleColor();
                        ImGui.PopID();
                    }
                    // face texture quick pick
                    ImGui.TextDisabled("textures:"); ImGui.SameLine();
                    foreach(var t in _commonTextures)
                    {
                        bool cur=false; if(_session.SelectedBrush!=null && _session.SelectedFaceIndex>=0) cur=_session.SelectedBrush.Faces[_session.SelectedFaceIndex].Texture==t;
                        if(cur) ImGui.PushStyleColor(ImGuiCol.Button, ColAccent);
                        if(ImGui.SmallButton(t)){ _session.DefaultTexture=t; _newBrushTex=t; if(_session.SelectedBrush!=null && _session.SelectedFaceIndex>=0){ _session.SelectedBrush.Faces[_session.SelectedFaceIndex].Texture=t; _recompile(); } }
                        if(cur) ImGui.PopStyleColor();
                        ImGui.SameLine();
                    }
                    ImGui.NewLine();
                    // UV editing for selected face
                    if (_session.SelectedBrush!=null && _session.SelectedFaceIndex>=0 && isSel)
                    {
                        var f = _session.SelectedBrush.Faces[_session.SelectedFaceIndex];
                        ImGui.Separator(); ImGui.Text($"UV Face {_session.SelectedFaceIndex}");
                        bool uvChanged=false;
                        float ro=f.Rotation, sx=f.ScaleX==0?1:f.ScaleX, sy=f.ScaleY==0?1:f.ScaleY, uo=f.UOffset, vo=f.VOffset;
                        ImGui.SetNextItemWidth(88); if(ImGui.DragFloat("Rot##uv", ref ro, 1f, -180,180,"%.0f°")) uvChanged=true;
                        ImGui.SameLine(); ImGui.SetNextItemWidth(70); if(ImGui.DragFloat("SX##uv", ref sx, 0.05f, 0.1f,8f,"%.2f")) uvChanged=true;
                        ImGui.SameLine(); ImGui.SetNextItemWidth(70); if(ImGui.DragFloat("SY##uv", ref sy, 0.05f, 0.1f,8f,"%.2f")) uvChanged=true;
                        ImGui.SetNextItemWidth(88); if(ImGui.DragFloat("U##uv", ref uo, 1f, -1024,1024,"%.0f")) uvChanged=true;
                        ImGui.SameLine(); ImGui.SetNextItemWidth(88); if(ImGui.DragFloat("V##uv", ref vo, 1f, -1024,1024,"%.0f")) uvChanged=true;
                        ImGui.SameLine(); if(ImGui.Button("Reset UV")){ ro=0; sx=1; sy=1; uo=0; vo=0; uvChanged=true; }
                        if(uvChanged){ f.Rotation=ro; f.ScaleX=sx; f.ScaleY=sy; f.UOffset=uo; f.VOffset=vo; _recompile(); }
                        if(ImGui.Button("Fit")){ f.ScaleX=1; f.ScaleY=1; f.UOffset=0; f.VOffset=0; f.Rotation=0; _recompile(); }
                        ImGui.SameLine(); if(ImGui.Button("Fit X")){ f.ScaleX=1; _recompile(); }
                        ImGui.SameLine(); if(ImGui.Button("Fit Y")){ f.ScaleY=1; _recompile(); }
                        float aw=64, ah=64;
                        // texture preview color
                        ImGui.TextDisabled($"UV: rot {f.Rotation:0}° scale {f.ScaleX:0.##}x{f.ScaleY:0.##} off {f.UOffset:0},{f.VOffset:0}");
                    }
                    ImGui.TreePop();
                }
            }
        }
        else
        {
            if(ImGui.Button("Make Brush", new Vector2(-1,0))){ var s=_session.BrushDefaultSize; var c=new Vector3(0,0,s.Z*0.5f); if(sel.Properties.TryGetValue("origin",out var o)) c=MapEditorSession.ParseVec3Public(o); _session.CreateBoxBrush(c-s*0.5f,c+s*0.5f); _recompile(); }
        }

        ImGui.End();
    }

    void DrawLight()
    {
        var sel=_session.Selected!;
        Vector3 col=Vector3.One;
        if(sel.Properties.TryGetValue("_color", out var cs)){ var v=MapEditorSession.ParseVec3Public(cs); if(v.X>1||v.Y>1||v.Z>1) v/=255f; col=v; }
        if(!_colorInitialized){ _colorEdit=col; _colorInitialized=true; }
        if(!ImGui.IsAnyItemActive()) _colorEdit=col;
        if(ImGui.ColorEdit3("Color", ref _colorEdit)){ sel.Properties["_color"]=$"{Fmt(_colorEdit.X)} {Fmt(_colorEdit.Y)} {Fmt(_colorEdit.Z)}"; _recompile(); }
        if(ImGui.IsItemDeactivatedAfterEdit()){ sel.Properties["_color"]=$"{Fmt(col.X)} {Fmt(col.Y)} {Fmt(col.Z)}"; _session.SetProperty("_color",$"{Fmt(_colorEdit.X)} {Fmt(_colorEdit.Y)} {Fmt(_colorEdit.Z)}",true); _recompile(); }
        float intens=300; if(sel.Properties.TryGetValue("light", out var ls) && float.TryParse(ls,NumberStyles.Float,CultureInfo.InvariantCulture,out var p)) intens=p;
        if(!_lightInitialized){ _lightIntensity=intens; _lightInitialized=true; }
        if(!ImGui.IsAnyItemActive()) _lightIntensity=intens;
        if(ImGui.SliderFloat("Intensity", ref _lightIntensity, 0, 3000, "%.0f")){ sel.Properties["light"]=Fmt(_lightIntensity); _recompile(); }
        if(ImGui.IsItemDeactivatedAfterEdit()){ sel.Properties["light"]=Fmt(intens); _session.SetProperty("light",Fmt(_lightIntensity)); _recompile(); }
    }

    void DrawStatusBar(Vector2 vp, float fps, Vector3 camPos, int meshCount, int brushCount)
    {
        ImGui.SetNextWindowPos(new Vector2(0, vp.Y - 20));
        ImGui.SetNextWindowSize(new Vector2(vp.X, 20));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(6,1));
        ImGui.Begin("##Status", ImGuiWindowFlags.NoTitleBar|ImGuiWindowFlags.NoResize|ImGuiWindowFlags.NoMove|ImGuiWindowFlags.NoScrollbar|ImGuiWindowFlags.NoDocking);
        ImGui.PopStyleVar();
        string left = string.IsNullOrWhiteSpace(_statusMessage)||_statusTimer<=0
            ? (_session.Mode==EditMode.Brush?"Brush: drag empty space to draw  •  wheel = height":"Select: click + drag")
            : _statusMessage;
        string right = $"FPS {fps:0} | {meshCount} meshes {brushCount} brushes | Grid { _session.GridSize:0} | RMB+WASD  F frame";
        ImGui.TextDisabled(left);
        float rw=ImGui.CalcTextSize(right).X+6;
        ImGui.SameLine(ImGui.GetWindowWidth()-rw); ImGui.TextDisabled(right);
        ImGui.End();
    }

    void DrawViewportOverlay()
    {
        var io=ImGui.GetIO();
        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow) || io.WantCaptureMouse) return;
        var dl=ImGui.GetForegroundDrawList();
        Vector2 c=io.DisplaySize/2;
        uint col=ImGui.ColorConvertFloat4ToU32(new Vector4(1,1,1,0.75f));
        dl.AddLine(c+new Vector2(-9,0), c+new Vector2(-3,0), col, 1.2f);
        dl.AddLine(c+new Vector2(3,0), c+new Vector2(9,0), col, 1.2f);
        dl.AddLine(c+new Vector2(0,-9), c+new Vector2(0,-3), col, 1.2f);
        dl.AddLine(c+new Vector2(0,3), c+new Vector2(0,9), col, 1.2f);
    }

    void DrawFileDialogs()
    {
        if(_showNewMapDialog){ ImGui.OpenPopup("New Map"); _showNewMapDialog=false; }
        if(ImGui.BeginPopupModal("New Map", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("New empty map — unsaved changes lost.");
            ImGui.InputText("Path", ref _fileDialogPath, 256);
            if(ImGui.Button("Create")){ HandleNewMap(); ImGui.CloseCurrentPopup(); }
            ImGui.SameLine(); if(ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        if(_showOpenDialog){ ImGui.OpenPopup("Open Map"); _showOpenDialog=false; }
        DrawPath("Open Map", true);
        if(_showSaveAsDialog){ ImGui.OpenPopup("Save As"); _showSaveAsDialog=false; }
        DrawPath("Save As", false);
    }

    void DrawArchDialog()
    {
        if (_showArchDialog) { ImGui.OpenPopup("Create Arch"); _showArchDialog = false; }
        if (!ImGui.BeginPopupModal("Create Arch", ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.Text("Arch — doorway / tunnel. Edit each wedge with Face / Vertex after creation.");
        ImGui.Separator();
        ImGui.DragFloat("Inner radius", ref _archInner, 1, 8, 512, "%.0f");
        ImGui.DragFloat("Wall thickness", ref _archWall, 1, 4, 128, "%.0f");
        ImGui.DragFloat("Depth (X thickness)", ref _archDepth, 1, 8, 512, "%.0f");
        ImGui.SliderInt("Segments", ref _archSegments, 3, 24);
        ImGui.SliderFloat("Sweep °", ref _archSweep, 30, 360, "%.0f");
        ImGui.TextDisabled($"Outer radius {_archInner+_archWall:0}  •  { _archSegments} wedges");
        ImGui.Separator();
        if (ImGui.Button("Create", new Vector2(90,0)))
        {
            Vector3 center = new Vector3(0, 0, 64);
            if (_session.Selected != null)
            {
                _session.GetSelectedBounds(out var mn, out var mx);
                if (mn.X <= mx.X) center = (mn + mx) * 0.5f;
                else if (_session.TryGetOriginQuake(out var o)) center = o + new Vector3(0,0,32);
            }
            _session.CreateArch(center, _archInner, _archWall, _archDepth, _archSegments, 0, _archSweep);
            _recompile();
            SetStatus($"Created arch { _archSegments} segments r={_archInner:0}+{_archWall:0} depth={_archDepth:0}");
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine(); if (ImGui.Button("Cancel", new Vector2(90,0))) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }
    void DrawPath(string title, bool isOpen)
    {
        if(!ImGui.BeginPopupModal(title, ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.InputText("Path", ref _fileDialogPath, 512);
        ImGui.BeginChild("##fl", new Vector2(480,160), ImGuiChildFlags.Borders);
        try{
            string dir="."; try{ dir=Path.GetDirectoryName(Path.GetFullPath(_fileDialogPath))??"."; }catch{}
            if(!Directory.Exists(dir)) dir=".";
            foreach(var f in Directory.GetFiles(dir, isOpen?"*.map":"*.*").Take(40)) if(ImGui.Selectable(Path.GetFileName(f))) _fileDialogPath=f;
        }catch{}
        ImGui.EndChild();
        if(ImGui.Button(isOpen?"Open":"Save")){ HandlePath(isOpen); ImGui.CloseCurrentPopup(); }
        ImGui.SameLine(); if(ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }
    void HandleNewMap(){ try{ string p=string.IsNullOrWhiteSpace(_fileDialogPath)?"new.map":_fileDialogPath; p=Path.GetFullPath(p); var e=MapEditorSession.CreateEmpty(p); _session.Entities.Clear(); foreach(var x in e.Entities) _session.Entities.Add(x); _session.Save(p); _recompile(); SetStatus($"Created '{p}'"); _fileDialogPath=p; }catch(Exception ex){ SetStatus(ex.Message);} }
    void HandlePath(bool isOpen){ try{ if(isOpen){ if(!File.Exists(_fileDialogPath)){SetStatus($"Not found: {_fileDialogPath}"); return;} var full=Path.GetFullPath(_fileDialogPath); var l=MapEditorSession.Load(full); _session.Entities.Clear(); foreach(var x in l.Entities) _session.Entities.Add(x); typeof(MapEditorSession).GetProperty("FilePath")!.SetValue(_session,full); _session.Select(0); _recompile(); SetStatus($"Opened '{full}'"); _fileDialogPath=full; }else{ var full=Path.GetFullPath(_fileDialogPath); _session.Save(full); SetStatus($"Saved '{full}'"); _fileDialogPath=full; } }catch(Exception ex){ SetStatus(ex.Message);} }
    void TrySave(){ try{ _session.Save(); SetStatus($"Saved '{_session.FilePath}'"); }catch(Exception ex){ SetStatus(ex.Message); } }

    public void LaunchPlay()
    {
        try{
            string map=_session.FilePath!=null?Path.GetFullPath(_session.FilePath):Path.GetFullPath("sample.map");
            if(_session.Dirty) _session.Save(map);
            else if(!File.Exists(map)) _session.Save(map);
            else { try{ var sp=Path.ChangeExtension(map,".scene.json"); if(!File.Exists(sp)||File.GetLastWriteTimeUtc(sp)<File.GetLastWriteTimeUtc(map)){ var sc=MapCompilerService.Compile(_session.Entities,Path.GetFileNameWithoutExtension(map)); KREAN.Core.Scenes.SceneSerializer.Write(sc,sp,true); } }catch{} }
            string[] cand=new[]{ Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..","KREAN.Application","bin","Debug","net10.0","KREAN.Application.dll")), Path.GetFullPath("KREAN.Application/bin/Debug/net10.0/KREAN.Application.dll"), Path.Combine(Directory.GetCurrentDirectory(),"KREAN.Application","bin","Debug","net10.0","KREAN.Application.dll"), Path.Combine(AppContext.BaseDirectory,"KREAN.Application.dll"), };
            string? dll=cand.FirstOrDefault(File.Exists);
            if(dll==null){ var psi2=new ProcessStartInfo{ FileName="dotnet", Arguments=$"run --project KREAN.Application -- \"{map}\"", UseShellExecute=true, WorkingDirectory=FindRoot()??Directory.GetCurrentDirectory() }; Process.Start(psi2); SetStatus($"Launched via dotnet run {map}"); return; }
            Process.Start(new ProcessStartInfo{ FileName="dotnet", Arguments=$"\"{dll}\" \"{map}\"", UseShellExecute=true}); SetStatus($"Launched app with '{map}'");
        }catch(Exception ex){ SetStatus(ex.Message); }
    }
    static string? FindRoot(){ var d=new DirectoryInfo(Directory.GetCurrentDirectory()); for(int i=0;i<6&&d!=null;i++){ if(File.Exists(Path.Combine(d.FullName,"KrOn.Core.slnx"))) return d.FullName; d=d.Parent; } return null; }
    static string Fmt(float f)=>f.ToString("0.##",CultureInfo.InvariantCulture);
}
