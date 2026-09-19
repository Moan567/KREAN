using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using KREAN.MapCompiler;

namespace KREAN.Editor;

public sealed class EditorUI
{
    readonly MapEditorSession _session;
    readonly Action _recompile;
    readonly Action<string> _saveAction;
    readonly Action<string> _exportSceneAction;

    // UI state
    string _filter = "";
    string _newKey = "";
    string _newValue = "";
    string _classEdit = "";
    bool _showNewMapDialog;
    bool _showOpenDialog;
    bool _showSaveAsDialog;
    string _fileDialogPath = "";
     string _statusMessage = "";
    float _statusTimer;
    Vector3 _originEdit;
    bool _originInitialized;
    Vector3 _colorEdit = Vector3.One;
    bool _colorInitialized;
    float _lightIntensity = 300f;
    bool _lightInitialized;
    float _faceExtrude = 0f;
    Vector3 _brushMoveEdit = Vector3.Zero;
    bool _brushMoveInitialized;
    Vector3 _boundsMinEdit, _boundsMaxEdit;
    bool _boundsInitialized;

    readonly string[] _knownClasses = new[]
    {
        "worldspawn", "info_player_start", "info_player_deathmatch", "light",
        "func_detail", "func_wall", "func_illusionary",
        "trigger_multiple", "trigger_once", "trigger_teleport",
        "misc_model", "item_health", "weapon_shotgun"
    };

    public EditorUI(MapEditorSession session, Action recompile, Action<string> save, Action<string> exportScene)
    {
        _session = session;
        _recompile = recompile;
        _saveAction = save;
        _exportSceneAction = exportScene;
    }

    public void SetStatus(string msg)
    {
        _statusMessage = msg;
        _statusTimer = 3f;
        Console.WriteLine(msg);
    }

    public void Tick(float dt)
    {
        if (_statusTimer > 0) _statusTimer -= dt;
    }

    public void Draw(Vector2 viewportSize, float fps, Vector3 camPos, int meshCount, int brushCount)
    {
        DrawMainMenu();
        DrawOutliner();
        DrawInspector();
        DrawBottomBar(viewportSize, fps, camPos, meshCount, brushCount);
        DrawFileDialogs();
        DrawViewportOverlay();
    }

    void DrawMainMenu()
    {
        if (ImGui.BeginMainMenuBar())
        {
            if (ImGui.BeginMenu("File"))
            {
                if (ImGui.MenuItem("New Map", "Ctrl+N")) _showNewMapDialog = true;
                if (ImGui.MenuItem("Open...", "Ctrl+O")) { _fileDialogPath = _session.FilePath ?? ""; _showOpenDialog = true; }
                bool canSave = _session.FilePath != null;
                if (ImGui.MenuItem("Save", "Ctrl+S", false, canSave)) TrySave();
                if (ImGui.MenuItem("Save As...", "Shift+Ctrl+S")) { _fileDialogPath = _session.FilePath ?? "new.map"; _showSaveAsDialog = true; }
                ImGui.Separator();
                if (ImGui.MenuItem("Export .scene.json", "F6")) _exportSceneAction(_session.FilePath != null ? Path.ChangeExtension(_session.FilePath, ".scene.json") : "out.scene.json");
                ImGui.Separator();
                if (ImGui.MenuItem("Exit", "Alt+F4")) Environment.Exit(0);
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("Edit"))
            {
                if (ImGui.MenuItem("Undo", "Ctrl+Z", false, _session.CanUndo)) { _session.Undo(); _recompile(); SetStatus("Undo"); }
                if (ImGui.MenuItem("Redo", "Ctrl+Y", false, _session.CanRedo)) { _session.Redo(); _recompile(); SetStatus("Redo"); }
                ImGui.Separator();
                if (ImGui.MenuItem("Add Light at Camera", "Insert")) SetStatus("Use viewport: press Insert or N");
                if (ImGui.MenuItem("Add Player Start at Camera", "N")) { }
                if (ImGui.MenuItem("Add 64 Box Brush", "B")) { }
                ImGui.Separator();
                if (ImGui.MenuItem("Duplicate", "Ctrl+D", false, _session.Selected != null)) { _session.DuplicateSelected(); _recompile(); }
                if (ImGui.MenuItem("Delete", "Del", false, _session.Selected != null)) { _session.DeleteSelected(); _recompile(); }
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("Play"))
            {
                if (ImGui.MenuItem("Play in Application", "F9")) LaunchPlay();
                if (ImGui.MenuItem("Play (compile & run temp)", "F9")) LaunchPlay();
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("View"))
            {
                if (ImGui.MenuItem("Recompile Viewport", "F5")) { _recompile(); SetStatus("Recompiled"); }
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("Help"))
            {
                if (ImGui.MenuItem("Controls")) ImGui.OpenPopup("HelpPopup");
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("Brush"))
            {
                bool obj = _session.Mode == EditMode.Object;
                bool br = _session.Mode == EditMode.Brush;
                bool fc = _session.Mode == EditMode.Face;
                if (ImGui.MenuItem("Object Mode", "1", obj)) { _session.Mode = EditMode.Object; SetStatus("Mode: Object"); }
                if (ImGui.MenuItem("Brush Mode", "2", br)) { _session.Mode = EditMode.Brush; SetStatus("Mode: Brush — click brush in viewport"); }
                if (ImGui.MenuItem("Face Mode", "3", fc)) { _session.Mode = EditMode.Face; SetStatus("Mode: Face — scroll or drag to extrude"); }
                ImGui.Separator();
                if (ImGui.MenuItem("Snap Grid Toggle", "G")) { _session.GridSnapEnabled = !_session.GridSnapEnabled; SetStatus($"Grid snap {(_session.GridSnapEnabled?"ON":"OFF")}"); }
                ImGui.TextDisabled($"Grid: {_session.GridSize}");
                if (ImGui.MenuItem("Grid *0.5", ",")) { _session.GridSize = Math.Max(1, _session.GridSize/2); }
                if (ImGui.MenuItem("Grid *2", ".")) { _session.GridSize = Math.Min(64, _session.GridSize*2); }
                ImGui.EndMenu();
            }

            // right side status
            string dirty = _session.Dirty ? "● Unsaved" : "Saved";
            string path = _session.FilePath ?? "(unsaved)";
            string modeStr = _session.Mode.ToString();
            ImGui.SetCursorPosX(ImGui.GetWindowWidth() - 520);
            ImGui.TextDisabled($"{path}  |  {dirty}  |  {modeStr}  |  Grid:{_session.GridSize:0}");

            if (ImGui.BeginPopup("HelpPopup"))
            {
                ImGui.Text("KREAN Map Editor — TrenchBroom-like");
                ImGui.Separator();
                ImGui.BulletText("WASD + Right-drag to fly, V = noclip toggle");
                ImGui.BulletText("Left-click brush in viewport to select (like TrenchBroom)");
                ImGui.BulletText("Drag selected brush: left-drag in viewport (free plane)");
                ImGui.BulletText("Hold X/Y/Z while dragging to lock axis, G toggles grid snap");
                ImGui.BulletText("1/2/3 switch Object/Brush/Face mode; In Face mode scroll extrudes face, drag moves face along normal");
                ImGui.BulletText("Arrows/PageUp-Down nudge (grid sized), ,/. grid size");
                ImGui.BulletText("Inspector: select brush/face, edit bounds, texture, extrude");
                ImGui.BulletText("Ctrl+S Save, Ctrl+Z/Y Undo/Redo, F5 Recompile");
                ImGui.EndPopup();
            }

            ImGui.EndMainMenuBar();
        }
    }

    void DrawOutliner()
    {
        ImGui.SetNextWindowSize(new Vector2(320, 500), ImGuiCond.FirstUseEver);
        ImGui.Begin("Outliner");

        // filter
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##filter", "Filter (classname, origin...)", ref _filter, 128);

        // toolbar
        if (ImGui.Button("Add Light")) { _session.AddEntity("light", new Vector3(0, 0, 64)); _recompile(); }
        ImGui.SameLine();
        if (ImGui.Button("Add Player")) { _session.AddEntity("info_player_start", new Vector3(0, 0, 32)); _recompile(); }
        ImGui.SameLine();
        if (ImGui.Button("Add Box")) { _session.AddBoxBrushAt(new Vector3(0, 0, 32), new Vector3(32, 32, 32)); _recompile(); }

        ImGui.Separator();

        // list
        ImGui.BeginChild("EntityList", new Vector2(0, -28), ImGuiChildFlags.Borders, ImGuiWindowFlags.None);
        string filterLow = _filter.ToLowerInvariant();
        for (int i = 0; i < _session.Entities.Count; i++)
        {
            var e = _session.Entities[i];
            // filter
            if (!string.IsNullOrWhiteSpace(filterLow))
            {
                string hay = (e.ClassName + " " + string.Join(" ", e.Properties.Values)).ToLowerInvariant();
                if (!hay.Contains(filterLow)) continue;
            }

            bool isBrush = e.Brushes.Count > 0;
            string label = $"{i}: {e.ClassName}";
            if (e.Properties.TryGetValue("targetname", out var tn) && !string.IsNullOrEmpty(tn)) label += $" ({tn})";
            else if (e.Properties.TryGetValue("origin", out var o)) label += $" @ {o}";
            if (isBrush) label += $"  [{e.Brushes.Count} brush]";
            else label += "  • point";

            bool selected = i == _session.SelectedIndex;
            if (ImGui.Selectable(label, selected))
            {
                _session.Select(i);
                _recompile();
                _originInitialized = false;
                _colorInitialized = false;
                _lightInitialized = false;
                _brushMoveInitialized = false;
                _boundsInitialized = false;
            }
            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                // focus camera? not implemented
            }
            // context menu
            if (ImGui.BeginPopupContextItem($"ctx{i}"))
            {
                if (ImGui.MenuItem("Duplicate")) { _session.Select(i); _session.DuplicateSelected(); _recompile(); }
                if (ImGui.MenuItem("Delete")) { _session.Select(i); _session.DeleteSelected(); _recompile(); }
                ImGui.EndPopup();
            }
        }
        ImGui.EndChild();

        // bottom count
        ImGui.TextDisabled($"{_session.Entities.Count} entities");

        ImGui.End();
    }

    void DrawInspector()
    {
        ImGui.SetNextWindowSize(new Vector2(380, 600), ImGuiCond.FirstUseEver);
        ImGui.Begin("Inspector");

        var sel = _session.Selected;
        if (sel == null)
        {
            ImGui.TextDisabled("No entity selected.");
            ImGui.TextDisabled("Select an entity in Outliner");
            ImGui.End();
            return;
        }

        int idx = _session.SelectedIndex;

        // header
        ImGui.Text($"Entity {idx}");
        ImGui.SameLine();
        ImGui.TextDisabled($"— {sel.Brushes.Count} brush{(sel.Brushes.Count==1?"":"es")}");

        // classname combo + free edit
        ImGui.Separator();
        ImGui.Text("Classname");
        // combo
        string curClass = sel.ClassName;
        if (!_originInitialized) _classEdit = curClass; // sync
        // we keep _classEdit as editable string
        // combo with known classes
        if (ImGui.BeginCombo("##class", curClass))
        {
            foreach (var c in _knownClasses)
            {
                bool isSel = c == curClass;
                if (ImGui.Selectable(c, isSel))
                {
                    _session.SetClassName(c);
                    _recompile();
                }
                if (isSel) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(140);
        // free text edit for custom
        _classEdit = curClass;
        if (ImGui.InputText("##classEdit", ref _classEdit, 64, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            if (!string.IsNullOrWhiteSpace(_classEdit) && _classEdit != curClass)
            {
                _session.SetClassName(_classEdit);
                _recompile();
            }
        }

        // origin editor if point entity
        if (sel.Properties.TryGetValue("origin", out var originStr))
        {
            ImGui.Separator();
            ImGui.Text("Origin (Quake units, Z-up)");
            if (!_originInitialized)
            {
                _originEdit = MapEditorSession.ParseVec3Public(originStr);
                _originInitialized = true;
            }
            // when selection changes we reset initialized above via recompile path; but we also need to detect external change
            // sync if originStr changed externally (undo) — simple: if parsed != _originEdit and not being dragged, update
            var parsed = MapEditorSession.ParseVec3Public(originStr);
            if (Vector3.Distance(parsed, _originEdit) > 0.01f && !ImGui.IsAnyItemActive())
            {
                _originEdit = parsed;
            }

            ImGui.SetNextItemWidth(90);
            bool changed = false;
            changed |= ImGui.DragFloat("X##orig", ref _originEdit.X, 1f, -8192, 8192, "%.1f");
            ImGui.SameLine(); ImGui.SetNextItemWidth(90);
            changed |= ImGui.DragFloat("Y##orig", ref _originEdit.Y, 1f, -8192, 8192, "%.1f");
            ImGui.SameLine(); ImGui.SetNextItemWidth(90);
            changed |= ImGui.DragFloat("Z##orig", ref _originEdit.Z, 1f, -8192, 8192, "%.1f");
            if (changed)
            {
                sel.Properties["origin"] = $"{Fmt(_originEdit.X)} {Fmt(_originEdit.Y)} {Fmt(_originEdit.Z)}";
                // mark dirty without pushing undo on every drag tick — push on deactivate
            }
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                // push undo snapshot manually via SetProperty which pushes undo — but we already mutated directly
                // so push a manual undo by calling SetProperty with current value (will duplicate push) — instead we do session's Bulk?
                // For simplicity, mark dirty and recompile; undo will be per drag end as one step (we already have previous snapshot from earlier push? not yet)
                // We didn't push undo on drag, so we need to push now: create a snapshot before change was already lost
                // Workaround: use session.SetProperty which pushes undo and overwrites, but we already changed; so undo would be one step behind
                // Instead, we push undo now and then set, but we already set. We'll just call Changed and rely on next edit to push?
                // To keep undo working, we push an undo point now by cloning before? Actually we missed it. For now mark dirty and recompile, and next time we will push before next change.
                // Proper fix: on drag start we should push; but ImGui doesn't give start event easily. We'll push on first change if not already pushed this frame.
            }
            // simple: when drag ends, push undo via private method — we can call session method that pushes and sets without extra
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                // force a clean push: undo the direct mutation and redo via session API
                // revert to parsed then use API
                sel.Properties["origin"] = $"{Fmt(parsed.X)} {Fmt(parsed.Y)} {Fmt(parsed.Z)}";
                _session.SetOriginQuake(_originEdit);
                _recompile();
            }
            else if (changed)
            {
                _recompile();
            }
        }
        else
        {
            _originInitialized = false;
        }

        // angle
        if (sel.Properties.TryGetValue("angle", out var angStr) || sel.Properties.TryGetValue("angles", out _))
        {
            float ang = 0;
            if (sel.Properties.TryGetValue("angle", out var a) ) float.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out ang);
            else if (sel.Properties.TryGetValue("angles", out var ag)) ang = MapEditorSession.ParseVec3Public(ag).Y;

            ImGui.Text("Yaw");
            float angEdit = ang;
            if (ImGui.SliderFloat("##angle", ref angEdit, 0, 360, "%.0f°"))
            {
                sel.Properties["angle"] = Fmt(angEdit);
                sel.Properties.Remove("angles");
                _recompile();
            }
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                // push undo via API
                sel.Properties["angle"] = Fmt(ang);
                _session.RotateSelectedYaw(angEdit - ang);
                _recompile();
            }
        }

        // light specifics
        if (sel.ClassName.Equals("light", StringComparison.OrdinalIgnoreCase))
        {
            ImGui.Separator();
            ImGui.Text("Light");
            // color
            Vector3 col = Vector3.One;
            if (sel.Properties.TryGetValue("_color", out var cs))
            {
                var v = MapEditorSession.ParseVec3Public(cs);
                // if >1, divide
                if (v.X > 1 || v.Y > 1 || v.Z > 1) v /= 255f;
                col = v;
            }
            if (!_colorInitialized) { _colorEdit = col; _colorInitialized = true; }
            // sync from external
            if (!ImGui.IsAnyItemActive()) _colorEdit = col;

            if (ImGui.ColorEdit3("Color##light", ref _colorEdit))
            {
                sel.Properties["_color"] = $"{Fmt(_colorEdit.X)} {Fmt(_colorEdit.Y)} {Fmt(_colorEdit.Z)}";
                _recompile();
            }
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                sel.Properties["_color"] = $"{Fmt(col.X)} {Fmt(col.Y)} {Fmt(col.Z)}";
                _session.SetProperty("_color", $"{Fmt(_colorEdit.X)} {Fmt(_colorEdit.Y)} {Fmt(_colorEdit.Z)}", true);
                _recompile();
            }

            float intens = 300f;
            if (sel.Properties.TryGetValue("light", out var ls) && float.TryParse(ls, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) intens = p;
            if (!_lightInitialized) { _lightIntensity = intens; _lightInitialized = true; }
            if (!ImGui.IsAnyItemActive()) _lightIntensity = intens;
            if (ImGui.SliderFloat("Intensity##light", ref _lightIntensity, 0, 2000, "%.0f"))
            {
                sel.Properties["light"] = Fmt(_lightIntensity);
                _recompile();
            }
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                sel.Properties["light"] = Fmt(intens);
                _session.SetProperty("light", Fmt(_lightIntensity));
                _recompile();
            }
        }
        else
        {
            _colorInitialized = false;
            _lightInitialized = false;
        }

        // properties table
        ImGui.Separator();
        ImGui.Text("Properties");
        if (ImGui.BeginTable("PropsTable", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("Key", ImGuiTableColumnFlags.WidthFixed, 140);
            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("##del", ImGuiTableColumnFlags.WidthFixed, 24);
            ImGui.TableHeadersRow();

            // need to collect keys to avoid modification during iteration
            var keys = sel.Properties.Keys.ToList();
            string? toDelete = null;
            foreach (var k in keys)
            {
                string v = sel.Properties[k];
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(k);
                ImGui.TableSetColumnIndex(1);
                ImGui.PushID(k);
                string edit = v;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText($"##val{k}", ref edit, 256, ImGuiInputTextFlags.EnterReturnsTrue))
                {
                    if (edit != v)
                    {
                        _session.SetProperty(k, edit);
                        _recompile();
                    }
                }
                // also handle deactivated
                if (ImGui.IsItemDeactivatedAfterEdit() && edit != v)
                {
                    // already handled
                }
                ImGui.PopID();
                ImGui.TableSetColumnIndex(2);
                if (ImGui.SmallButton($"x##{k}"))
                {
                    toDelete = k;
                }
            }
            if (toDelete != null)
            {
                _session.RemoveProperty(toDelete);
                _recompile();
            }
            ImGui.EndTable();
        }

        // add new key/value
        ImGui.SetNextItemWidth(140);
        ImGui.InputTextWithHint("##newKey", "key", ref _newKey, 64);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-60);
        ImGui.InputTextWithHint("##newVal", "value", ref _newValue, 256, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if (ImGui.Button("Add") && !string.IsNullOrWhiteSpace(_newKey))
        {
            _session.SetProperty(_newKey.Trim(), _newValue);
            _newKey = "";
            _newValue = "";
            _recompile();
        }

        // ---- TrenchBroom-like brush tools ----
        ImGui.Separator();
        ImGui.Text("Edit Mode & Grid (TrenchBroom-like)");
        var mode = _session.Mode;
        if (ImGui.RadioButton("Object##mode", mode == EditMode.Object)) { _session.Mode = EditMode.Object; _recompile(); }
        ImGui.SameLine(); if (ImGui.RadioButton("Brush##mode", mode == EditMode.Brush)) { _session.Mode = EditMode.Brush; _recompile(); }
        ImGui.SameLine(); if (ImGui.RadioButton("Face##mode", mode == EditMode.Face)) { _session.Mode = EditMode.Face; _recompile(); }
        ImGui.SameLine(); ImGui.TextDisabled("| Click brush in viewport, drag to move");
        bool snap = _session.GridSnapEnabled;
        if (ImGui.Checkbox("Grid snap", ref snap)) _session.GridSnapEnabled = snap;
        ImGui.SameLine();
        float gs = _session.GridSize;
        ImGui.SetNextItemWidth(100);
        if (ImGui.SliderFloat("##grid", ref gs, 1, 64, "Grid %.0f")) _session.GridSize = gs;
        ImGui.SameLine(); ImGui.TextDisabled(" ,/. size");

        // selection move / bounds (TrenchBroom transform)
        _session.GetSelectedBounds(out var curMin, out var curMax);
        bool hasBounds = curMin.X <= curMax.X;
        if (hasBounds)
        {
            ImGui.Text($"Bounds: min {curMin.X:0},{curMin.Y:0},{curMin.Z:0}  max {curMax.X:0},{curMax.Y:0},{curMax.Z:0}  size {(curMax-curMin).X:0}x{(curMax-curMin).Y:0}x{(curMax-curMin).Z:0}");
            // move by delta (like TrenchBroom inspector)
            if (!_brushMoveInitialized) { _brushMoveEdit = Vector3.Zero; _brushMoveInitialized = true; }
            ImGui.SetNextItemWidth(80); ImGui.DragFloat("dX##bmv", ref _brushMoveEdit.X, 1f, -4096,4096,"%.1f"); ImGui.SameLine();
            ImGui.SetNextItemWidth(80); ImGui.DragFloat("dY##bmv", ref _brushMoveEdit.Y, 1f, -4096,4096,"%.1f"); ImGui.SameLine();
            ImGui.SetNextItemWidth(80); ImGui.DragFloat("dZ##bmv", ref _brushMoveEdit.Z, 1f, -4096,4096,"%.1f"); ImGui.SameLine();
            if (ImGui.Button("Move##brushMove")) { if (_brushMoveEdit.LengthSquared()>1e-6f){ if (_session.Mode==EditMode.Brush) _session.TranslateSelectedBrush(_brushMoveEdit); else _session.TranslateSelectedEntity(_brushMoveEdit); _brushMoveEdit=Vector3.Zero; _recompile(); } }
            ImGui.SameLine(); if (ImGui.Button("Zero##bmv")) _brushMoveEdit=Vector3.Zero;
            ImGui.SameLine(); ImGui.TextDisabled(_session.Mode==EditMode.Brush? "brush":"entity");

            // resize via min/max (axis-aligned brush only — rebuilds as box like TrenchBroom)
            if (sel.Brushes.Count>0 && _session.SelectedBrush != null && _session.SelectedBrush.Faces.Count==6)
            {
                if (!_boundsInitialized){ _boundsMinEdit=curMin; _boundsMaxEdit=curMax; _boundsInitialized=true; }
                else if (!ImGui.IsAnyItemActive() && Vector3.Distance(_boundsMinEdit, curMin)>0.01f) { _boundsMinEdit=curMin; _boundsMaxEdit=curMax; }
                ImGui.Text("Resize (box brush):");
                ImGui.SetNextItemWidth(80); ImGui.DragFloat("minX##bnd", ref _boundsMinEdit.X,1f,-8192,8192,"%.1f"); ImGui.SameLine();
                ImGui.SetNextItemWidth(80); ImGui.DragFloat("minY##bnd", ref _boundsMinEdit.Y,1f,-8192,8192,"%.1f"); ImGui.SameLine();
                ImGui.SetNextItemWidth(80); ImGui.DragFloat("minZ##bnd", ref _boundsMinEdit.Z,1f,-8192,8192,"%.1f");
                ImGui.SetNextItemWidth(80); ImGui.DragFloat("maxX##bnd", ref _boundsMaxEdit.X,1f,-8192,8192,"%.1f"); ImGui.SameLine();
                ImGui.SetNextItemWidth(80); ImGui.DragFloat("maxY##bnd", ref _boundsMaxEdit.Y,1f,-8192,8192,"%.1f"); ImGui.SameLine();
                ImGui.SetNextItemWidth(80); ImGui.DragFloat("maxZ##bnd", ref _boundsMaxEdit.Z,1f,-8192,8192,"%.1f"); ImGui.SameLine();
                if (ImGui.Button("Apply Resize")) { var br=_session.SelectedBrush!; KREAN.MapCompiler.BrushManipulation.ResizeToBounds(br,_boundsMinEdit,_boundsMaxEdit); _recompile(); }
                if (!ImGui.IsAnyItemActive()) { _boundsMinEdit=curMin; _boundsMaxEdit=curMax; }
            }
            else _boundsInitialized=false;
            // face extrude in face mode
            if (_session.Mode==EditMode.Face && _session.SelectedBrush!=null && _session.SelectedFaceIndex>=0)
            {
                ImGui.Text($"Face {_session.SelectedFaceIndex}: push along normal");
                ImGui.SetNextItemWidth(120);
                ImGui.SliderFloat("Extrude##face", ref _faceExtrude, -64, 64, "%.1f");
                ImGui.SameLine();
                if (ImGui.Button("Push")) { _session.MoveSelectedFace(_faceExtrude); _faceExtrude=0; _recompile(); }
                ImGui.SameLine(); if (ImGui.Button("Pull -8")) { _session.MoveSelectedFace(-8); _recompile(); }
                ImGui.SameLine(); if (ImGui.Button("Push +8")) { _session.MoveSelectedFace(8); _recompile(); }
            }
            else _faceExtrude=0;
        }

        // brushes
        if (sel.Brushes.Count > 0)
        {
            ImGui.Separator();
            if (ImGui.CollapsingHeader($"Brushes ({sel.Brushes.Count})", ImGuiTreeNodeFlags.DefaultOpen))
            {
                for (int bi = 0; bi < sel.Brushes.Count; bi++)
                {
                    var br = sel.Brushes[bi];
                    bool isSelBrush = bi == _session.SelectedBrushIndex;
                    ImGui.PushStyleColor(ImGuiCol.Text, isSelBrush? new Vector4(1,0.9f,0.2f,1): new Vector4(1,1,1,1));
                    bool open = ImGui.TreeNodeEx($"Brush {bi}  ({br.Faces.Count} faces){(isSelBrush ? " <=":"")}", ImGuiTreeNodeFlags.OpenOnArrow);
                    ImGui.PopStyleColor();
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"Select##sel{bi}"))
                    {
                        _session.SelectBrush(_session.SelectedIndex, bi, -1);
                        _recompile();
                        _brushMoveInitialized=false; _boundsInitialized=false;
                    }
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"Dup##dup{bi}")) { _session.DuplicateBrush(bi); _recompile(); }
                    if (open)
                    {
                        if (ImGui.SmallButton($"Delete brush##{bi}"))
                        {
                            _session.RemoveBrushAt(bi);
                            _recompile();
                            ImGui.TreePop();
                            break;
                        }
                        // faces
                        for (int fi = 0; fi < br.Faces.Count; fi++)
                        {
                            var f = br.Faces[fi];
                            bool isSelFace = isSelBrush && fi == _session.SelectedFaceIndex;
                            ImGui.PushID($"b{bi}f{fi}");
                            if (isSelFace) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1,0.4f,0.2f,1));
                            string tex = f.Texture;
                            ImGui.SetNextItemWidth(140);
                            if (ImGui.InputText($"Tex##{fi}", ref tex, 64, ImGuiInputTextFlags.EnterReturnsTrue))
                            {
                                f.Texture = tex;
                                _recompile();
                            }
                            ImGui.SameLine();
                            if (ImGui.Selectable($"{(isSelFace?"● ":"  ")}Face {fi}  n({f.Normal.X:0.0},{f.Normal.Y:0.0},{f.Normal.Z:0.0}) d={f.Distance:0}", isSelFace))
                            {
                                _session.SelectBrush(_session.SelectedIndex, bi, fi);
                                _session.Mode = EditMode.Face;
                                _recompile();
                            }
                            if (isSelFace) ImGui.PopStyleColor();
                            ImGui.PopID();
                        }
                        ImGui.TreePop();
                    }
                }
                if (ImGui.Button("Add 64 Box Brush here"))
                {
                    Vector3 center = Vector3.Zero;
                    if (sel.Properties.TryGetValue("origin", out var o)) center = MapEditorSession.ParseVec3Public(o);
                    else { _session.GetSelectedBounds(out var mn, out var mx); if (mn.X<=mx.X) center = (mn+mx)*0.5f + new Vector3(64,0,0); }
                    _session.AddBoxBrushAt(center, new Vector3(32, 32, 32));
                    _recompile();
                }
                ImGui.SameLine();
                if (ImGui.Button("Add 64 Box at Camera"))
                {
                    _session.AddBoxBrushAt(new Vector3(0,0,32), new Vector3(32,32,32));
                    _recompile();
                }
            }
        }
        else
        {
            if (ImGui.Button("Add 64 Box Brush (make worldspawn)"))
            {
                _session.AddBoxBrushAt(new Vector3(0,0,32), new Vector3(32,32,32));
                _recompile();
            }
        }

        ImGui.End();
    }

    void DrawBottomBar(Vector2 viewportSize, float fps, Vector3 camPos, int meshCount, int brushCount)
    {
        ImGui.SetNextWindowPos(new Vector2(0, viewportSize.Y - 28));
        ImGui.SetNextWindowSize(new Vector2(viewportSize.X, 28));
        ImGui.Begin("StatusBar", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar);
        string modeHelp = _session.Mode == EditMode.Face ? "Face: scroll/drag extrude, click face in inspector" : _session.Mode == EditMode.Brush ? "Brush: left-drag brush in viewport, X/Y/Z lock" : "Object: left-drag entity";
        string status = string.IsNullOrWhiteSpace(_statusMessage) || _statusTimer <= 0 ? $"FPS {fps:0} | Cam {camPos.X:0.0},{camPos.Y:0.0},{camPos.Z:0.0} | {meshCount} meshes | {brushCount} brushes | {modeHelp} | 1/2/3 mode, G grid" : _statusMessage;
        ImGui.TextDisabled(status);
        ImGui.End();
    }

    void DrawViewportOverlay()
    {
        // crosshair + help when no imgui hover
        var io = ImGui.GetIO();
        bool anyWindowHovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow);
        if (!anyWindowHovered && !io.WantCaptureMouse)
        {
            // draw crosshair at center via ImGui foreground draw list
            var dl = ImGui.GetForegroundDrawList();
            Vector2 center = io.DisplaySize / 2;
            uint col = ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.85f));
            dl.AddLine(center + new Vector2(-10, 0), center + new Vector2(-3, 0), col, 1.5f);
            dl.AddLine(center + new Vector2(3, 0), center + new Vector2(10, 0), col, 1.5f);
            dl.AddLine(center + new Vector2(0, -10), center + new Vector2(0, -3), col, 1.5f);
            dl.AddLine(center + new Vector2(0, 3), center + new Vector2(0, 10), col, 1.5f);
        }
    }

    void DrawFileDialogs()
    {
        // New
        if (_showNewMapDialog)
        {
            ImGui.OpenPopup("New Map");
            _showNewMapDialog = false;
        }
        if (ImGui.BeginPopupModal("New Map", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("Create a new empty map (worldspawn only). Unsaved changes will be lost.");
            ImGui.Separator();
            ImGui.InputText("Path##new", ref _fileDialogPath, 256);
            if (ImGui.Button("Create")) { HandleNewMap(); ImGui.CloseCurrentPopup(); }
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        // Open
        if (_showOpenDialog)
        {
            ImGui.OpenPopup("Open Map");
            _showOpenDialog = false;
        }
        DrawPathDialog("Open Map", true);

        // Save As
        if (_showSaveAsDialog)
        {
            ImGui.OpenPopup("Save As");
            _showSaveAsDialog = false;
        }
        DrawPathDialog("Save As", false);
    }

    void DrawPathDialog(string title, bool isOpen)
    {
        if (ImGui.BeginPopupModal(title, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text(title);
            ImGui.InputText("Path##dlg", ref _fileDialogPath, 512);
            // file list
            ImGui.Separator();
            ImGui.BeginChild("FileList", new Vector2(500, 200), ImGuiChildFlags.Borders, ImGuiWindowFlags.None);
            try
            {
                string dir = ".";
                try { dir = Path.GetDirectoryName(Path.GetFullPath(_fileDialogPath)) ?? "."; } catch { }
                if (!Directory.Exists(dir)) dir = ".";
                var files = Directory.GetFiles(dir, isOpen ? "*.map" : "*.*").Take(50);
                foreach (var f in files)
                {
                    string name = Path.GetFileName(f);
                    if (ImGui.Selectable(name)) _fileDialogPath = f;
                }
            }
            catch { }
            ImGui.EndChild();

            if (ImGui.Button(isOpen ? "Open" : "Save")) { HandlePathDialog(isOpen); ImGui.CloseCurrentPopup(); }
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

    void HandleNewMap()
    {
        try
        {
            string p = string.IsNullOrWhiteSpace(_fileDialogPath) ? "new.map" : _fileDialogPath;
            p = Path.GetFullPath(p);
            var empty = MapEditorSession.CreateEmpty(p);
            _session.Entities.Clear();
            foreach (var e in empty.Entities) _session.Entities.Add(e);
            _session.Save(p);
            _recompile();
            SetStatus($"Created new map '{p}'");
            _fileDialogPath = p;
        }
        catch (Exception ex) { SetStatus($"New failed: {ex.Message}"); }
    }

    void HandlePathDialog(bool isOpen)
    {
        try
        {
            if (isOpen)
            {
                if (!File.Exists(_fileDialogPath)) { SetStatus($"File not found: {_fileDialogPath}"); return; }
                var full = Path.GetFullPath(_fileDialogPath);
                var loaded = MapEditorSession.Load(full);
                _session.Entities.Clear();
                foreach (var e in loaded.Entities) _session.Entities.Add(e);
                typeof(MapEditorSession).GetProperty("FilePath")!.SetValue(_session, full);
                _session.Select(0);
                _recompile();
                SetStatus($"Opened '{full}'");
                _fileDialogPath = full;
            }
            else
            {
                var full = Path.GetFullPath(_fileDialogPath);
                _session.Save(full);
                SetStatus($"Saved '{full}'");
                _fileDialogPath = full;
            }
        }
        catch (Exception ex) { SetStatus($"Error: {ex.Message}"); }
    }

    void TrySave()
    {
        try
        {
            _session.Save();
            SetStatus($"Saved '{_session.FilePath}'");
        }
        catch (Exception ex) { SetStatus($"Save failed: {ex.Message}"); }
    }

    public void LaunchPlay()
    {
        try
        {
            // Ensure map is saved so Application can read it — use absolute path
            string map = _session.FilePath != null ? Path.GetFullPath(_session.FilePath) : Path.GetFullPath("sample.map");
            if (_session.Dirty)
            {
                _session.Save(map);
                SetStatus($"Saved '{map}' before Play");
            }
            else if (!File.Exists(map))
            {
                _session.Save(map);
            }
            else
            {
                // ensure scene is fresh even if not dirty (brush edits may have been compiled to scene only)
                try
                {
                    var scenePath = Path.ChangeExtension(map, ".scene.json");
                    if (!File.Exists(scenePath) || File.GetLastWriteTimeUtc(scenePath) < File.GetLastWriteTimeUtc(map))
                    {
                        var sc = MapCompilerService.Compile(_session.Entities, Path.GetFileNameWithoutExtension(map));
                        KREAN.Core.Scenes.SceneSerializer.Write(sc, scenePath, true);
                    }
                } catch {}
            }

            // Try to locate KREAN.Application dll
            string[] candidates = new[]
            {
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "KREAN.Application", "bin", "Debug", "net10.0", "KREAN.Application.dll")),
                Path.GetFullPath("KREAN.Application/bin/Debug/net10.0/KREAN.Application.dll"),
                Path.Combine(Directory.GetCurrentDirectory(), "KREAN.Application", "bin", "Debug", "net10.0", "KREAN.Application.dll"),
                Path.Combine(AppContext.BaseDirectory, "KREAN.Application.dll"),
            };
            string? appDll = candidates.FirstOrDefault(File.Exists);
            if (appDll == null)
            {
                // fallback: try dotnet run
                var psi2 = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"run --project KREAN.Application -- \"{map}\"",
                    UseShellExecute = true,
                    WorkingDirectory = FindSolutionRoot() ?? Directory.GetCurrentDirectory()
                };
                Process.Start(psi2);
                SetStatus($"Launched via dotnet run --project KREAN.Application -- {map}");
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{appDll}\" \"{map}\"",
                UseShellExecute = true
            };
            Process.Start(psi);
            SetStatus($"Launched KREAN.Application with '{map}'");
        }
        catch (Exception ex) { SetStatus($"Play failed: {ex.Message}"); }
    }

    static string? FindSolutionRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (int i = 0; i < 6 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "KrOn.Core.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    static string Fmt(float f) => f.ToString("0.##", CultureInfo.InvariantCulture);
}
