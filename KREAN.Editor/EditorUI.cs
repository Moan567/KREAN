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
        "misc_model", "item_health", "weapon_shotgun"
    };

    readonly string[] _commonTextures = new[]
    {
        "wall","floor","ceiling","concrete","metal","brick","wood",
        "clip","skip","hint","origin","trigger","nodraw","caulk"
    };

    // s&box — dark flat, blue accent, soft 6px rounding, thin 1px borders. Clean sans.
    static readonly Vector4 ColBg = new(0.095f, 0.095f, 0.105f, 1f);     // #18181b
    static readonly Vector4 ColBgDark = new(0.075f, 0.075f, 0.085f, 1f); // #13131a
    static readonly Vector4 ColPanel = new(0.15f, 0.15f, 0.165f, 1f);    // #26262a
    static readonly Vector4 ColBeige = new(0.92f, 0.92f, 0.94f, 1f);     // near white (kept name)
    static readonly Vector4 ColAccent = new(0.26f, 0.48f, 0.96f, 1f);    // s&box blue #4266f5
    static readonly Vector4 ColAccentHover = new(0.35f, 0.56f, 0.98f, 1f);
    static readonly Vector4 ColBorderHi = new(0.22f, 0.22f, 0.24f, 1f);  // flat separator
    static readonly Vector4 ColBorderSh = new(0.08f, 0.08f, 0.09f, 1f);

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

    public void Draw(Vector2 viewportSize, float fps, Vector3 camPos, int meshCount, int brushCount)
    {
        EnsureTheme();
        DrawMainMenu();
        DrawToolbar(viewportSize);
        // Outliner removed — brush list no longer shown on side (inspect via Inspector → Brushes)
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
        // s&box: flat, soft 6px, thin 1px — modern source2
        s.WindowRounding = 6; s.FrameRounding = 4; s.GrabRounding = 4; s.ScrollbarRounding = 8; s.TabRounding = 4; s.PopupRounding = 6;
        s.WindowBorderSize = 1; s.FrameBorderSize = 0; s.ChildBorderSize = 0; s.PopupBorderSize = 1;
        s.FramePadding = new Vector2(6, 4); s.ItemSpacing = new Vector2(6, 4); s.ItemInnerSpacing = new Vector2(4, 4);
        s.WindowPadding = new Vector2(8, 6); s.WindowTitleAlign = new Vector2(0.02f, 0.5f);
        s.ScrollbarSize = 10; s.GrabMinSize = 12;
        var c = s.Colors;
        c[(int)ImGuiCol.Text] = new Vector4(0.92f, 0.92f, 0.94f, 1f);
        c[(int)ImGuiCol.TextDisabled] = new Vector4(0.50f, 0.50f, 0.56f, 1f);
        c[(int)ImGuiCol.WindowBg] = new Vector4(0.14f, 0.14f, 0.155f, 1f);
        c[(int)ImGuiCol.ChildBg] = new Vector4(0.13f, 0.13f, 0.145f, 1f);
        c[(int)ImGuiCol.PopupBg] = new Vector4(0.16f, 0.16f, 0.18f, 1f);
        c[(int)ImGuiCol.Border] = new Vector4(0.22f, 0.22f, 0.24f, 1f);
        c[(int)ImGuiCol.BorderShadow] = new Vector4(0f, 0f, 0f, 0f);
        c[(int)ImGuiCol.FrameBg] = new Vector4(0.20f, 0.20f, 0.23f, 1f);
        c[(int)ImGuiCol.FrameBgHovered] = new Vector4(0.26f, 0.26f, 0.30f, 1f);
        c[(int)ImGuiCol.FrameBgActive] = new Vector4(0.22f, 0.22f, 0.26f, 1f);
        c[(int)ImGuiCol.TitleBg] = new Vector4(0.10f, 0.10f, 0.115f, 1f);
        c[(int)ImGuiCol.TitleBgActive] = new Vector4(0.14f, 0.14f, 0.16f, 1f);
        c[(int)ImGuiCol.TitleBgCollapsed] = new Vector4(0.10f, 0.10f, 0.115f, 1f);
        c[(int)ImGuiCol.MenuBarBg] = new Vector4(0.12f, 0.12f, 0.135f, 1f);
        c[(int)ImGuiCol.ScrollbarBg] = new Vector4(0.12f, 0.12f, 0.135f, 1f);
        c[(int)ImGuiCol.ScrollbarGrab] = new Vector4(0.24f, 0.24f, 0.28f, 1f);
        c[(int)ImGuiCol.ScrollbarGrabHovered] = new Vector4(0.30f, 0.30f, 0.35f, 1f);
        c[(int)ImGuiCol.ScrollbarGrabActive] = ColAccent;
        c[(int)ImGuiCol.CheckMark] = ColAccent;
        c[(int)ImGuiCol.SliderGrab] = ColAccent;
        c[(int)ImGuiCol.SliderGrabActive] = ColAccentHover;
        c[(int)ImGuiCol.Button] = new Vector4(0.20f, 0.20f, 0.24f, 1f);
        c[(int)ImGuiCol.ButtonHovered] = new Vector4(0.28f, 0.28f, 0.33f, 1f);
        c[(int)ImGuiCol.ButtonActive] = new Vector4(0.18f, 0.18f, 0.22f, 1f);
        c[(int)ImGuiCol.Header] = new Vector4(0.18f, 0.18f, 0.22f, 1f);
        c[(int)ImGuiCol.HeaderHovered] = new Vector4(0.26f, 0.26f, 0.31f, 1f);
        c[(int)ImGuiCol.HeaderActive] = new Vector4(0.22f, 0.32f, 0.70f, 1f);
        c[(int)ImGuiCol.Separator] = new Vector4(0.22f, 0.22f, 0.24f, 1f);
        c[(int)ImGuiCol.SeparatorHovered] = ColAccent;
        c[(int)ImGuiCol.SeparatorActive] = ColAccentHover;
        c[(int)ImGuiCol.Tab] = new Vector4(0.16f, 0.16f, 0.18f, 1f);
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
            ImGui.Separator();
            if (ImGui.MenuItem("Create Arch...", "Shift+A")) _showArchDialog = true;
            ImGui.Separator();
            if (ImGui.MenuItem("Unlink Brushes (explode)", "Ctrl+Shift+G", false, _session.Selected != null && _session.Selected.Brushes.Count > 1))
            { if (_session.UnlinkSelectedBrushes()) { _recompile(); SetStatus("Unlinked — each brush is now independent (no linked system)"); } else SetStatus("Unlink failed"); }
            if (ImGui.MenuItem("Group Highlighted", "Ctrl+G", false, _session.IsMultiMode)) { if (_session.GroupHighlightedBrushes()) { _recompile(); SetStatus("Grouped highlighted into one entity — now linked"); } else SetStatus("Group failed"); }
            bool link = _session.LinkBrushes;
            if (ImGui.MenuItem("Link Highlighted Move", "", link)) { _session.LinkBrushes = !link; SetStatus(link ? "Link OFF — brushes independent" : "Link ON — Ctrl+highlighted move together"); }
            if (ImGui.MenuItem("Clip (planned)", "X")) SetStatus("Clip tool planned — use face handles for now");
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("View"))
        {
            if (ImGui.MenuItem("Frame Selection", "F")) SetStatus("Press F in viewport");
            if (ImGui.MenuItem("Grid Snap", "G", _session.GridSnapEnabled)) _session.GridSnapEnabled = !_session.GridSnapEnabled;
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

        ImGui.SameLine(); ImGui.TextDisabled("|"); ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.16f,0.55f,0.30f,1f));
        if (ImGui.Button("Play")) LaunchPlay();
        ImGui.PopStyleColor();

        ImGui.End();
    }

    void DrawOutliner()
    {
        ImGui.SetNextWindowSize(new Vector2(240, 400), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Outliner")) { ImGui.End(); return; }
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
            if (!string.IsNullOrWhiteSpace(low))
            {
                string hay=(e.ClassName+" "+string.Join(" ",e.Properties.Values)).ToLowerInvariant();
                if (!hay.Contains(low)) continue;
            }
            string label2 = $"{i}: {e.ClassName}";
            if (e.Properties.TryGetValue("targetname", out var tn2) && !string.IsNullOrEmpty(tn2)) label2 += $" ({tn2})";
            if (e.Brushes.Count==1) { var br0=e.Brushes[0]; KREAN.MapCompiler.BrushManipulation.GetBounds(br0, out var mn0, out var mx0); label2+=$" [{mx0.X-mn0.X:0}x{mx0.Y-mn0.Y:0}]"; }
            else if (e.Brushes.Count>0) label2 += $" [{e.Brushes.Count}]";
            bool sel2=i==_session.SelectedIndex || _session.IsMultiEntitySelected(i);
            if (sel2 && _session.IsMultiEntitySelected(i)) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f,0.55f,0.15f,1f));
            bool clicked2 = ImGui.Selectable(label2, sel2);
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
                ImGui.EndPopup();
            }
        }
        ImGui.EndChild();
        ImGui.TextDisabled($"{_session.Entities.Count} entities  •  click brush # to move alone");
        if (_session.MultiSelectedBrushes.Count>0) ImGui.TextColored(new Vector4(1,0.55f,0.15f,1), $"{_session.MultiSelectedBrushes.Count} brushes highlighted — independent (Link OFF)");
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

        // --- Entity -------------------------------------------------------------
        if (ImGui.CollapsingHeader("Entity", ImGuiTreeNodeFlags.DefaultOpen))
        {
            string cur=sel.ClassName;
            if (ImGui.BeginCombo("Class", cur))
            {
                foreach(var c in _knownClasses) { bool s=c==cur; if(ImGui.Selectable(c,s)){ _session.SetClassName(c); _recompile(); } if(s) ImGui.SetItemDefaultFocus(); }
                ImGui.EndCombo();
            }
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
