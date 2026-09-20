using System.Globalization;
using System.Numerics;
using KREAN.Core;
using KREAN.MapCompiler;

namespace KREAN.Editor;

public enum EditMode { Object, Brush, Face, Vertex }

public sealed class MapEditorSession
{
    public List<MapEntity> Entities { get; private set; }
    public string? FilePath { get; private set; }
    public int SelectedIndex { get; private set; } = -1;
    public int SelectedBrushIndex { get; private set; } = -1;
    public int SelectedFaceIndex { get; private set; } = -1;
    public int SelectedVertexIndex { get; private set; } = -1;
    public EditMode Mode { get; set; } = EditMode.Brush;
    // multi-highlight (Ctrl+click) — brushes + entities (highlight only, not linked)
    public HashSet<(int ei, int bi)> MultiSelectedBrushes { get; } = new();
    public HashSet<int> MultiSelectedEntities { get; } = new();
    public bool IsMultiBrushMode => MultiSelectedBrushes.Count > 0;
    public bool IsMultiEntityMode => MultiSelectedEntities.Count > 0;
    public bool IsMultiMode => MultiSelectedBrushes.Count > 0 || MultiSelectedEntities.Count > 0;
    // removed linked-brush system — brushes are independent by default
    // set true only if you explicitly want Ctrl+highlighted brushes to move together (Group)
    public bool LinkBrushes { get; set; } = false;
    public float GridSize { get; set; } = 8f;
    public bool GridSnapEnabled { get; set; } = true;
    public bool Dirty { get; private set; }

    // TrenchBroom-style brush tool defaults (Quake units, Z-up).
    public Vector3 BrushDefaultSize { get; set; } = new Vector3(64, 64, 64);
    public string DefaultTexture { get; set; } = "wall";

    // undo/redo
    readonly Stack<Snapshot> _undo = new();
    readonly Stack<Snapshot> _redo = new();

    sealed class Snapshot
    {
        public List<MapEntity> Entities = null!;
        public int Selected;
        public int SelBrush;
        public int SelFace;
        public int SelVertex;
        public EditMode Mode;
        public string? Path;
    }

    public event Action? Changed;

    public MapEditorSession(List<MapEntity> entities, string? filePath)
    {
        Entities = entities;
        FilePath = filePath;
        if (entities.Count > 0) SelectedIndex = 0;
    }

    public static MapEditorSession Load(string path)
    {
        var full = Path.GetFullPath(path);
        var entities = MapParser.ParseFile(full);
        Console.WriteLine($"[map] loaded '{full}': {entities.Count} entities, {entities.Sum(e => e.Brushes.Count)} brushes");
        return new MapEditorSession(entities, full);
    }

    public static MapEditorSession CreateSample(string? path = "sample.map")
    {
        var text = SampleMap.Generate();
        var entities = MapParser.Parse(text);
        if (path != null)
        {
            File.WriteAllText(path, text);
            Console.WriteLine($"[map] wrote sample to '{path}'");
            return new MapEditorSession(entities, path);
        }
        return new MapEditorSession(entities, null);
    }

    public static MapEditorSession CreateEmpty(string? path = null)
    {
        var e = new MapEntity();
        e.Properties["classname"] = "worldspawn";
        var list = new List<MapEntity> { e };
        if (path != null) MapWriter.Write(path, list);
        return new MapEditorSession(list, path);
    }

    public MapEntity? Selected => SelectedIndex >= 0 && SelectedIndex < Entities.Count ? Entities[SelectedIndex] : null;

    List<MapEntity> DeepClone(List<MapEntity> src)
    {
        var dst = new List<MapEntity>(src.Count);
        foreach (var s in src)
        {
            var c = new MapEntity();
            foreach (var kv in s.Properties) c.Properties[kv.Key] = kv.Value;
            foreach (var b in s.Brushes)
            {
                var nb = new MapBrush();
                foreach (var f in b.Faces)
                {
                    var nf = new MapFace
                    {
                        P1 = f.P1, P2 = f.P2, P3 = f.P3,
                        Texture = f.Texture,
                        IsValve = f.IsValve,
                        UAxis = f.UAxis, VAxis = f.VAxis,
                        UOffset = f.UOffset, VOffset = f.VOffset,
                        Rotation = f.Rotation, ScaleX = f.ScaleX, ScaleY = f.ScaleY
                    };
                    nf.ComputePlane();
                    nb.Faces.Add(nf);
                }
                c.Brushes.Add(nb);
            }
            dst.Add(c);
        }
        return dst;
    }

    void PushUndo(string label)
    {
        _undo.Push(new Snapshot { Entities = DeepClone(Entities), Selected = SelectedIndex, SelBrush = SelectedBrushIndex, SelFace = SelectedFaceIndex, SelVertex = SelectedVertexIndex, Mode = Mode, Path = FilePath });
        _redo.Clear();
        // optional: cap at 64
        if (_undo.Count > 64)
        {
            var tmp = _undo.Reverse().Skip(1).Reverse().ToList();
            _undo.Clear();
            foreach (var s in tmp.Reverse<MapEditorSession.Snapshot>()) _undo.Push(s);
        }
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Undo()
    {
        if (!CanUndo) return;
        _redo.Push(new Snapshot { Entities = DeepClone(Entities), Selected = SelectedIndex, SelBrush = SelectedBrushIndex, SelFace = SelectedFaceIndex, SelVertex = SelectedVertexIndex, Mode = Mode, Path = FilePath });
        var s = _undo.Pop();
        Entities = s.Entities;
        SelectedIndex = s.Selected;
        SelectedBrushIndex = s.SelBrush;
        SelectedFaceIndex = s.SelFace;
        SelectedVertexIndex = s.SelVertex;
        Mode = s.Mode;
        FilePath = s.Path;
        Dirty = true;
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (!CanRedo) return;
        _undo.Push(new Snapshot { Entities = DeepClone(Entities), Selected = SelectedIndex, SelBrush = SelectedBrushIndex, SelFace = SelectedFaceIndex, SelVertex = SelectedVertexIndex, Mode = Mode, Path = FilePath });
        var s = _redo.Pop();
        Entities = s.Entities;
        SelectedIndex = s.Selected;
        SelectedBrushIndex = s.SelBrush;
        SelectedFaceIndex = s.SelFace;
        SelectedVertexIndex = s.SelVertex;
        Mode = s.Mode;
        FilePath = s.Path;
        Dirty = true;
        Changed?.Invoke();
    }

    public void Select(int index)
    {
        if (Entities.Count == 0) { SelectedIndex = -1; SelectedBrushIndex = -1; SelectedFaceIndex = -1; SelectedVertexIndex = -1; return; }
        SelectedIndex = Math.Clamp(index, 0, Entities.Count - 1);
        SelectedBrushIndex = -1;
        SelectedFaceIndex = -1;
        SelectedVertexIndex = -1;
        ValidateBrushSelection();
    }

    public void SelectBrush(int entityIndex, int brushIndex, int faceIndex = -1)
    {
        Select(entityIndex);
        SelectedBrushIndex = brushIndex;
        SelectedFaceIndex = faceIndex;
        ValidateBrushSelection();
        Changed?.Invoke();
    }

    void ValidateBrushSelection()
    {
        var e = Selected;
        if (e == null || e.Brushes.Count == 0) { SelectedBrushIndex = -1; SelectedFaceIndex = -1; SelectedVertexIndex = -1; return; }
        if (SelectedBrushIndex < 0 || SelectedBrushIndex >= e.Brushes.Count) { SelectedBrushIndex = -1; SelectedFaceIndex = -1; SelectedVertexIndex = -1; return; }
        var b = e.Brushes[SelectedBrushIndex];
        if (SelectedFaceIndex < -1 || SelectedFaceIndex >= b.Faces.Count) SelectedFaceIndex = -1;
        if (SelectedBrush != null && SelectedBrush.Faces.Count == 6)
        {
            if (SelectedVertexIndex < -1 || SelectedVertexIndex >= 8) SelectedVertexIndex = -1;
        }
        else SelectedVertexIndex = -1;
    }

    public MapBrush? SelectedBrush
    {
        get
        {
            var e = Selected;
            if (e == null || SelectedBrushIndex < 0 || SelectedBrushIndex >= e.Brushes.Count) return null;
            return e.Brushes[SelectedBrushIndex];
        }
    }

    public void SetSelectedFace(int faceIdx)
    {
        SelectedFaceIndex = faceIdx;
        ValidateBrushSelection();
        Changed?.Invoke();
    }

    public void SelectNext(int delta = 1)
    {
        if (Entities.Count == 0) return;
        SelectedIndex = (SelectedIndex + delta + Entities.Count) % Entities.Count;
        Changed?.Invoke();
    }

    public bool ToggleMultiBrush(int ei, int bi)
    {
        var key = (ei, bi);
        if (MultiSelectedBrushes.Contains(key)) { MultiSelectedBrushes.Remove(key); Changed?.Invoke(); return false; }
        MultiSelectedBrushes.Add(key); Changed?.Invoke(); return true;
    }
    public void ClearMultiBrush() { if (MultiSelectedBrushes.Count>0) { MultiSelectedBrushes.Clear(); Changed?.Invoke(); } }
    public bool IsMultiSelected(int ei, int bi) => MultiSelectedBrushes.Contains((ei, bi));
    public void SelectBrushMulti(int ei, int bi, bool keepMulti)
    {
        if (!keepMulti) ClearMultiBrush();
        // primary always
        SelectBrush(ei, bi, -1);
        if (keepMulti) MultiSelectedBrushes.Add((ei, bi));
    }

    public bool ToggleMultiEntity(int ei)
    {
        if (MultiSelectedEntities.Contains(ei)) { MultiSelectedEntities.Remove(ei); Changed?.Invoke(); return false; }
        MultiSelectedEntities.Add(ei); Changed?.Invoke(); return true;
    }
    public void ClearMultiEntity() { if (MultiSelectedEntities.Count>0) { MultiSelectedEntities.Clear(); Changed?.Invoke(); } }
    public void ClearAllMulti() { bool had = MultiSelectedBrushes.Count>0 || MultiSelectedEntities.Count>0; MultiSelectedBrushes.Clear(); MultiSelectedEntities.Clear(); if (had) Changed?.Invoke(); }
    public bool IsMultiEntitySelected(int ei) => MultiSelectedEntities.Contains(ei);

    public void PrintAll()
    {
        Console.WriteLine($"=== {Entities.Count} entities ===");
        for (int i = 0; i < Entities.Count; i++)
        {
            var e = Entities[i];
            string marker = i == SelectedIndex ? " >" : "  ";
            string extra = e.Properties.TryGetValue("origin", out var o) ? $" origin={o}" : "";
            string brushes = e.Brushes.Count > 0 ? $" [{e.Brushes.Count} brushes]" : "";
            Console.WriteLine($"{marker} [{i}] {e.ClassName}{extra}{brushes}");
        }
    }

    public void PrintSelected()
    {
        var e = Selected;
        if (e == null) { Console.WriteLine("[editor] no selection"); return; }
        Console.WriteLine($"[editor] selected [{SelectedIndex}] classname={e.ClassName}");
        foreach (var (k, v) in e.Properties)
            Console.WriteLine($"         {k} = \"{v}\"");
        if (e.Brushes.Count > 0)
            Console.WriteLine($"         brushes: {e.Brushes.Count}");
    }

    // ---------- mutations (each pushes undo) ----------

    public void SetProperty(string key, string value, bool pushUndo = true)
    {
        var e = Selected;
        if (e == null) return;
        if (pushUndo) PushUndo($"set {key}");
        e.Properties[key] = value;
        Dirty = true;
        Changed?.Invoke();
    }

    public void RemoveProperty(string key)
    {
        var e = Selected;
        if (e == null) return;
        if (e.Properties.ContainsKey(key))
        {
            PushUndo($"remove {key}");
            e.Properties.Remove(key);
            Dirty = true;
            Changed?.Invoke();
        }
    }

    public void BulkSetProperties(Dictionary<string, string> newProps)
    {
        var e = Selected;
        if (e == null) return;
        PushUndo("bulk edit");
        e.Properties.Clear();
        foreach (var kv in newProps) e.Properties[kv.Key] = kv.Value;
        Dirty = true;
        Changed?.Invoke();
    }

    public void AddEntity(string classname, Vector3? quakeOrigin = null)
    {
        PushUndo("add entity");
        var e = new MapEntity();
        e.Properties["classname"] = classname;
        if (quakeOrigin.HasValue)
            e.Properties["origin"] = $"{F(quakeOrigin.Value.X)} {F(quakeOrigin.Value.Y)} {F(quakeOrigin.Value.Z)}";
        // sensible defaults for some classes
        if (classname == "light" && !e.Properties.ContainsKey("light")) e.Properties["light"] = "300";
        if (classname == "light" && !e.Properties.ContainsKey("_color")) e.Properties["_color"] = "1 1 1";
        Entities.Add(e);
        SelectedIndex = Entities.Count - 1;
        Dirty = true;
        Changed?.Invoke();
    }

    public void DeleteSelected()
    {
        DeleteAtSelection();
    }

    public void DeleteAtSelection()
    {
        var e = Selected;
        if (e == null) return;

        if (Mode == EditMode.Brush && MultiSelectedBrushes.Count > 1)
        {
            PushUndo("delete multi brush");
            // group by entity
            var byEnt = MultiSelectedBrushes.GroupBy(k => k.ei).ToDictionary(g => g.Key, g => g.Select(x => x.bi).OrderByDescending(x => x).ToList());
            foreach (var kv in byEnt)
            {
                var ent = Entities[kv.Key];
                foreach (var bi in kv.Value) if (bi >= 0 && bi < ent.Brushes.Count) ent.Brushes.RemoveAt(bi);
            }
            ClearMultiBrush(); SelectedBrushIndex = -1; SelectedFaceIndex = -1; SelectedVertexIndex = -1;
            Dirty = true; Changed?.Invoke(); return;
        }
        if (Mode == EditMode.Brush && SelectedBrushIndex >= 0 && SelectedBrushIndex < e.Brushes.Count)
        {
            RemoveBrushAt(SelectedBrushIndex);
        }
        else if (Mode == EditMode.Face && SelectedBrush != null && SelectedFaceIndex >= 0)
        {
            // Face deletion: remove the face from the brush
            // If brush would have < 4 faces, delete the whole brush
            var br = SelectedBrush;
            if (br.Faces.Count <= 4)
            {
                RemoveBrushAt(SelectedBrushIndex);
            }
            else
            {
                PushUndo("delete face");
                br.Faces.RemoveAt(SelectedFaceIndex);
                SelectedFaceIndex = -1;
                Dirty = true;
                Changed?.Invoke();
            }
        }
        else
        {
            // Object mode or no brush selected: delete entity
            PushUndo("delete");
            Entities.RemoveAt(SelectedIndex);
            if (SelectedIndex >= Entities.Count) SelectedIndex = Entities.Count - 1;
            Dirty = true;
            Changed?.Invoke();
        }
    }

    public void DuplicateSelected()
    {
        if (Selected == null) return;
        PushUndo("duplicate");
        var src = Selected!;
        var clone = new MapEntity();
        foreach (var (k, v) in src.Properties) clone.Properties[k] = v;
        foreach (var b in src.Brushes)
        {
            var nb = new MapBrush();
            foreach (var f in b.Faces)
            {
                var nf = new MapFace
                {
                    P1 = f.P1, P2 = f.P2, P3 = f.P3,
                    Texture = f.Texture,
                    IsValve = f.IsValve,
                    UAxis = f.UAxis, VAxis = f.VAxis,
                    UOffset = f.UOffset, VOffset = f.VOffset,
                    Rotation = f.Rotation, ScaleX = f.ScaleX, ScaleY = f.ScaleY
                };
                nf.ComputePlane();
                nb.Faces.Add(nf);
            }
            clone.Brushes.Add(nb);
        }
        if (clone.Properties.TryGetValue("origin", out var o))
        {
            var v = ParseVec3(o);
            v.X += 64f;
            clone.Properties["origin"] = $"{F(v.X)} {F(v.Y)} {F(v.Z)}";
        }
        Entities.Add(clone);
        SelectedIndex = Entities.Count - 1;
        Dirty = true;
        Changed?.Invoke();
    }

    public void MoveSelectedQuake(Vector3 deltaQuake)
    {
        var e = Selected;
        if (e == null) return;
        if (!e.Properties.TryGetValue("origin", out var o))
            return;
        PushUndo("move");
        var pos = ParseVec3(o);
        pos += deltaQuake;
        e.Properties["origin"] = $"{F(pos.X)} {F(pos.Y)} {F(pos.Z)}";
        Dirty = true;
        Changed?.Invoke();
    }

    public void SetOriginQuake(Vector3 pos)
    {
        var e = Selected;
        if (e == null) return;
        PushUndo("move");
        e.Properties["origin"] = $"{F(pos.X)} {F(pos.Y)} {F(pos.Z)}";
        Dirty = true;
        Changed?.Invoke();
    }

    public void MoveSelectedEngine(Vector3 deltaEngine)
    {
        float s = Units.QuakeToMeters;
        var deltaQuake = new Vector3(deltaEngine.X / s, -deltaEngine.Z / s, deltaEngine.Y / s);
        MoveSelectedQuake(deltaQuake);
    }

    public void RotateSelectedYaw(float deltaDegrees)
    {
        var e = Selected;
        if (e == null) return;
        PushUndo("rotate");
        float yaw = 0f;
        if (e.Properties.TryGetValue("angle", out var a) && float.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            yaw = parsed;
        else if (e.Properties.TryGetValue("angles", out var ag))
            yaw = ParseVec3(ag).Y;
        yaw = (yaw + deltaDegrees) % 360f;
        if (yaw < 0) yaw += 360f;
        e.Properties["angle"] = F(yaw);
        e.Properties.Remove("angles");
        Dirty = true;
        Changed?.Invoke();
    }

    public void SetClassName(string newClass)
    {
        var e = Selected;
        if (e == null) return;
        if (e.ClassName == newClass) return;
        PushUndo("classname");
        e.Properties["classname"] = newClass;
        Dirty = true;
        Changed?.Invoke();
    }

    public void AddBoxBrushAt(Vector3 quakeCenter, Vector3 quakeHalfExtents, string? texture = null)
    {
        texture ??= DefaultTexture;
        PushUndo("add brush");
        var e = Selected;
        bool makeNewEntity = e == null || e.ClassName != "worldspawn";
        if (makeNewEntity)
        {
            e = new MapEntity();
            e.Properties["classname"] = "worldspawn";
            Entities.Add(e);
            SelectedIndex = Entities.Count - 1;
        }
        var brush = BuildBoxBrush(quakeCenter - quakeHalfExtents, quakeCenter + quakeHalfExtents, texture);
        e!.Brushes.Add(brush);
        SelectedBrushIndex = e.Brushes.Count - 1;
        SelectedFaceIndex = -1;
        Dirty = true;
        Changed?.Invoke();
    }

    /// <summary>TrenchBroom-style: create a brush from explicit bounds (snapped, min-size enforced).
    /// Always goes to worldspawn and becomes the active brush selection.</summary>
    public bool CreateBoxBrush(Vector3 aQuake, Vector3 bQuake, string? texture = null)
    {
        texture ??= DefaultTexture;
        var min = Vector3.Min(aQuake, bQuake);
        var max = Vector3.Max(aQuake, bQuake);
        if (GridSnapEnabled && GridSize > 0)
        {
            min = BrushManipulation.Snap(min, GridSize);
            max = BrushManipulation.Snap(max, GridSize);
        }
        // enforce minimum thickness so the convex brush stays valid
        float minEdge = Math.Max(GridSize > 0 ? GridSize : 1f, 1f);
        if (max.X - min.X < minEdge) max.X = min.X + minEdge;
        if (max.Y - min.Y < minEdge) max.Y = min.Y + minEdge;
        if (max.Z - min.Z < minEdge) max.Z = min.Z + minEdge;

        PushUndo("create brush");
        int ws = Entities.FindIndex(e => e.ClassName == "worldspawn");
        MapEntity target;
        if (ws < 0)
        {
            target = new MapEntity();
            target.Properties["classname"] = "worldspawn";
            Entities.Insert(0, target);
            ws = 0;
        }
        else target = Entities[ws];
        var brush = BuildBoxBrush(min, max, texture);
        target.Brushes.Add(brush);
        SelectedIndex = ws;
        SelectedBrushIndex = target.Brushes.Count - 1;
        SelectedFaceIndex = -1;
        Mode = EditMode.Brush;
        Dirty = true;
        Changed?.Invoke();
        return true;
    }

    public Vector3 SnapQuake(Vector3 v)
        => GridSnapEnabled && GridSize > 0 ? BrushManipulation.Snap(v, GridSize) : v;

    public void CreateArch(Vector3 centerQuake, float innerRadius, float wallThickness, float depth, int segments, float startDeg = 0f, float sweepDeg = 180f, string? texture = null)
    {
        texture ??= DefaultTexture;
        if (segments < 3) segments = 3;
        if (segments > 32) segments = 32;
        float outerRadius = innerRadius + wallThickness;
        if (outerRadius <= 0 || innerRadius <= 0) return;
        PushUndo("create arch");
        int ws = Entities.FindIndex(e => e.ClassName == "worldspawn");
        MapEntity target;
        if (ws < 0) { target = new MapEntity(); target.Properties["classname"] = "worldspawn"; Entities.Insert(0, target); ws = 0; }
        else target = Entities[ws];

        float step = sweepDeg / segments * MathF.PI / 180f;
        float start = startDeg * MathF.PI / 180f;
        float halfDepth = depth * 0.5f;

        for (int i = 0; i < segments; i++)
        {
            float a0 = start + i * step;
            float a1 = start + (i + 1) * step;
            // arch in YZ plane, thickness along X
            float y0i = centerQuake.Y + MathF.Cos(a0) * innerRadius;
            float z0i = centerQuake.Z + MathF.Sin(a0) * innerRadius;
            float y0o = centerQuake.Y + MathF.Cos(a0) * outerRadius;
            float z0o = centerQuake.Z + MathF.Sin(a0) * outerRadius;
            float y1i = centerQuake.Y + MathF.Cos(a1) * innerRadius;
            float z1i = centerQuake.Z + MathF.Sin(a1) * innerRadius;
            float y1o = centerQuake.Y + MathF.Cos(a1) * outerRadius;
            float z1o = centerQuake.Z + MathF.Sin(a1) * outerRadius;

            float x0 = centerQuake.X - halfDepth;
            float x1 = centerQuake.X + halfDepth;

            // 8 vertices
            Vector3 v0 = new(x0, y0i, z0i); // inner a0 minX
            Vector3 v1 = new(x1, y0i, z0i); // inner a0 maxX
            Vector3 v2 = new(x1, y0o, z0o); // outer a0 maxX
            Vector3 v3 = new(x0, y0o, z0o); // outer a0 minX
            Vector3 v4 = new(x0, y1i, z1i); // inner a1
            Vector3 v5 = new(x1, y1i, z1i);
            Vector3 v6 = new(x1, y1o, z1o);
            Vector3 v7 = new(x0, y1o, z1o);

            var br = new MapBrush();
            Vector3 wedgeCenter = (v0 + v1 + v2 + v3 + v4 + v5 + v6 + v7) * 0.125f;
            void AddQuad(Vector3 p1, Vector3 p2, Vector3 p3, Vector3 p4)
            {
                var f = new MapFace { P1 = p1, P2 = p2, P3 = p3, Texture = texture, ScaleX = 1, ScaleY = 1 };
                f.ComputePlane();
                // ensure outward: normal should point away from wedge center
                Vector3 faceCenter = (p1 + p2 + p3 + p4) * 0.25f;
                if (Vector3.Dot(f.Normal, wedgeCenter - faceCenter) > 0)
                {
                    (f.P2, f.P3) = (f.P3, f.P2);
                    f.ComputePlane();
                }
                br.Faces.Add(f);
            }
            // X- thickness face
            AddQuad(v0, v4, v7, v3);
            // X+ thickness face
            AddQuad(v1, v2, v6, v5);
            // inner radial face (hole side) — normal toward center (inward)
            AddQuad(v0, v1, v5, v4);
            // outer radial face — normal outward
            AddQuad(v3, v7, v6, v2);
            // angular side faces at a0 and a1
            AddQuad(v0, v3, v2, v1); // a0 side
            AddQuad(v4, v5, v6, v7); // a1 side

            if (!BrushManipulation.IsBrushValid(br)) continue;
            // snap not needed for arch — keep precise
            target.Brushes.Add(br);
        }
        SelectedIndex = ws;
        SelectedBrushIndex = target.Brushes.Count - segments;
        if (SelectedBrushIndex < 0) SelectedBrushIndex = target.Brushes.Count - 1;
        SelectedFaceIndex = -1; SelectedVertexIndex = -1;
        Dirty = true; Changed?.Invoke();
    }

    static MapBrush BuildBoxBrush(Vector3 min, Vector3 max, string texture)
    {
        var brush = new MapBrush();
        void AddFace(Vector3 p1, Vector3 p2, Vector3 p3)
        {
            var f = new MapFace { P1 = p1, P2 = p2, P3 = p3, Texture = texture, ScaleX = 1, ScaleY = 1 };
            f.ComputePlane();
            brush.Faces.Add(f);
        }
        AddFace(new Vector3(min.X, min.Y, min.Z), new Vector3(min.X, max.Y, min.Z), new Vector3(min.X, min.Y, max.Z));
        AddFace(new Vector3(max.X, min.Y, min.Z), new Vector3(max.X, min.Y, max.Z), new Vector3(max.X, max.Y, min.Z));
        AddFace(new Vector3(min.X, min.Y, min.Z), new Vector3(min.X, min.Y, max.Z), new Vector3(max.X, min.Y, min.Z));
        AddFace(new Vector3(min.X, max.Y, min.Z), new Vector3(max.X, max.Y, min.Z), new Vector3(min.X, max.Y, max.Z));
        AddFace(new Vector3(min.X, min.Y, min.Z), new Vector3(max.X, min.Y, min.Z), new Vector3(min.X, max.Y, min.Z));
        AddFace(new Vector3(min.X, min.Y, max.Z), new Vector3(min.X, max.Y, max.Z), new Vector3(max.X, min.Y, max.Z));
        return brush;
    }

    public void RemoveBrushAt(int brushIndex)
    {
        var e = Selected;
        if (e == null) return;
        if (brushIndex < 0 || brushIndex >= e.Brushes.Count) return;
        PushUndo("remove brush");
        e.Brushes.RemoveAt(brushIndex);
        if (SelectedBrushIndex == brushIndex) { SelectedBrushIndex = -1; SelectedFaceIndex = -1; }
        else if (SelectedBrushIndex > brushIndex) SelectedBrushIndex--;
        Dirty = true;
        Changed?.Invoke();
    }

    // ---------- TrenchBroom-like brush editing ----------

    public bool TranslateSelectedBrush(Vector3 deltaQuake, bool pushUndo = true)
    {
        if (deltaQuake.LengthSquared() < 1e-6f) return false;
        if (GridSnapEnabled && GridSize > 0) deltaQuake = BrushManipulation.Snap(deltaQuake, GridSize);
        // highlighted = move together (user requested)
        if (MultiSelectedBrushes.Count > 0)
        {
            var set = new HashSet<(int,int)>(MultiSelectedBrushes);
            if (SelectedBrush != null) set.Add((SelectedIndex, SelectedBrushIndex));
            if (set.Count > 1)
            {
                if (pushUndo) PushUndo("move highlighted");
                foreach (var (ei, bi) in set)
                {
                    if (ei < 0 || ei >= Entities.Count) continue;
                    var ent = Entities[ei];
                    if (bi < 0 || bi >= ent.Brushes.Count) continue;
                    BrushManipulation.Translate(ent.Brushes[bi], deltaQuake);
                }
                Dirty = true; Changed?.Invoke(); return true;
            }
        }
        var e = Selected;
        if (e == null || SelectedBrushIndex < 0 || SelectedBrushIndex >= e.Brushes.Count) return false;
        if (pushUndo) PushUndo("move brush");
        BrushManipulation.Translate(e.Brushes[SelectedBrushIndex], deltaQuake);
        Dirty = true;
        Changed?.Invoke();
        return true;
    }

    public bool TranslateSelectedEntity(Vector3 deltaQuake, bool pushUndo = true)
    {
        if (deltaQuake.LengthSquared() < 1e-6f) return false;
        if (GridSnapEnabled && GridSize > 0) deltaQuake = BrushManipulation.Snap(deltaQuake, GridSize);
        // highlighted = move together
        if (MultiSelectedEntities.Count > 0 || MultiSelectedBrushes.Count > 0)
        {
            bool hasBrushMulti = MultiSelectedBrushes.Count > 0;
            // if we have brush multi, let TranslateSelectedBrush handle it — but in Object mode we also move brushes
            if (Mode == EditMode.Object && (MultiSelectedEntities.Count > 0 || hasBrushMulti))
            {
                // move all highlighted entities and also any highlighted brushes (as entities)
                var entSet = new HashSet<int>(MultiSelectedEntities);
                if (SelectedIndex >= 0) entSet.Add(SelectedIndex);
                // if brush multi exists in Object mode, treat each brush's entity
                foreach (var (ei, _) in MultiSelectedBrushes) entSet.Add(ei);
                if (entSet.Count > 1 || hasBrushMulti)
                {
                    if (pushUndo) PushUndo("move highlighted");
                    foreach (var ei in entSet)
                    {
                        if (ei < 0 || ei >= Entities.Count) continue;
                        var ent = Entities[ei];
                        if (ent.Brushes.Count > 0) BrushManipulation.TranslateEntity(ent, deltaQuake);
                        else if (ent.Properties.TryGetValue("origin", out var o))
                        {
                            var pos = ParseVec3(o); pos += deltaQuake;
                            if (GridSnapEnabled) pos = BrushManipulation.Snap(pos, GridSize);
                            ent.Properties["origin"] = $"{F(pos.X)} {F(pos.Y)} {F(pos.Z)}";
                        }
                    }
                    Dirty = true; Changed?.Invoke(); return true;
                }
            }
        }
        var e = Selected;
        if (e == null) return false;
        if (pushUndo) PushUndo("move entity");
        if (e.Brushes.Count > 0)
            BrushManipulation.TranslateEntity(e, deltaQuake);
        else if (e.Properties.TryGetValue("origin", out var o))
        {
            var pos = ParseVec3(o);
            pos += deltaQuake;
            if (GridSnapEnabled) pos = BrushManipulation.Snap(pos, GridSize);
            e.Properties["origin"] = $"{F(pos.X)} {F(pos.Y)} {F(pos.Z)}";
        }
        Dirty = true;
        Changed?.Invoke();
        return true;
    }

    public bool NudgeSelected(float dx, float dy, float dz)
    {
        var delta = new Vector3(dx, dy, dz);
        if (Mode == EditMode.Brush && SelectedBrush != null) return TranslateSelectedBrush(delta);
        return TranslateSelectedEntity(delta);
    }

    public bool MoveSelectedFace(float distance, bool pushUndo = true)
    {
        var b = SelectedBrush;
        if (b == null || SelectedFaceIndex < 0) return false;
        if (MathF.Abs(distance) < 1e-4f) return false;
        if (pushUndo) PushUndo("move face");
        if (GridSnapEnabled && GridSize > 0) distance = MathF.Round(distance / GridSize) * GridSize;
        bool ok = BrushManipulation.MoveFace(b, SelectedFaceIndex, distance);
        if (!ok && pushUndo) { Undo(); _redo.Clear(); }
        else { Dirty = true; Changed?.Invoke(); }
        return ok;
    }

    public bool SetFaceTexture(int brushIdx, int faceIdx, string texture)
    {
        var e = Selected;
        if (e == null || brushIdx < 0 || brushIdx >= e.Brushes.Count) return false;
        var b = e.Brushes[brushIdx];
        if (faceIdx < 0 || faceIdx >= b.Faces.Count) return false;
        PushUndo("texture");
        b.Faces[faceIdx].Texture = texture;
        Dirty = true;
        Changed?.Invoke();
        return true;
    }

    public void DuplicateBrush(int brushIdx)
    {
        var e = Selected;
        if (e == null || brushIdx < 0 || brushIdx >= e.Brushes.Count) return;
        PushUndo("duplicate brush");
        var src = e.Brushes[brushIdx];
        var nb = new MapBrush();
        foreach (var f in src.Faces)
        {
            var nf = new MapFace { P1 = f.P1 + new Vector3(16,16,0), P2 = f.P2 + new Vector3(16,16,0), P3 = f.P3 + new Vector3(16,16,0), Texture = f.Texture, IsValve = f.IsValve, UAxis = f.UAxis, VAxis = f.VAxis, UOffset = f.UOffset, VOffset = f.VOffset, Rotation = f.Rotation, ScaleX = f.ScaleX, ScaleY = f.ScaleY };
            nf.ComputePlane(); nb.Faces.Add(nf);
        }
        e.Brushes.Add(nb);
        SelectedBrushIndex = e.Brushes.Count - 1;
        Dirty = true;
        Changed?.Invoke();
    }

    public void DuplicateMultiBrushes()
    {
        if (MultiSelectedBrushes.Count == 0) { if (SelectedBrush != null) DuplicateBrush(SelectedBrushIndex); return; }
        PushUndo("duplicate multi brush");
        // group by entity to avoid index shift issues
        var groups = MultiSelectedBrushes.GroupBy(k => k.ei).ToDictionary(g => g.Key, g => g.Select(x => x.bi).ToList());
        var newSelection = new HashSet<(int ei, int bi)>();
        foreach (var kv in groups)
        {
            var ent = Entities[kv.Key];
            int before = ent.Brushes.Count;
            foreach (var bi in kv.Value)
            {
                if (bi < 0 || bi >= before) continue;
                var src = ent.Brushes[bi];
                var nb = new MapBrush();
                foreach (var f in src.Faces)
                {
                    var nf = new MapFace { P1 = f.P1 + new Vector3(16,16,0), P2 = f.P2 + new Vector3(16,16,0), P3 = f.P3 + new Vector3(16,16,0), Texture = f.Texture, IsValve = f.IsValve, UAxis = f.UAxis, VAxis = f.VAxis, UOffset = f.UOffset, VOffset = f.VOffset, Rotation = f.Rotation, ScaleX = f.ScaleX, ScaleY = f.ScaleY };
                    nf.ComputePlane(); nb.Faces.Add(nf);
                }
                ent.Brushes.Add(nb);
                newSelection.Add((kv.Key, ent.Brushes.Count - 1));
            }
        }
        MultiSelectedBrushes.Clear();
        foreach (var k in newSelection) MultiSelectedBrushes.Add(k);
        // set primary to last duplicated
        if (newSelection.Count > 0) { var last = newSelection.Last(); SelectedIndex = last.ei; SelectedBrushIndex = last.bi; }
        Dirty = true; Changed?.Invoke();
    }

    public void DuplicateMultiEntities()
    {
        if (MultiSelectedEntities.Count == 0) return;
        PushUndo("duplicate multi entity");
        var sorted = MultiSelectedEntities.OrderByDescending(x => x).ToList();
        var newIndices = new List<int>();
        foreach (var ei in sorted)
        {
            if (ei < 0 || ei >= Entities.Count) continue;
            var src = Entities[ei];
            var clone = new MapEntity();
            foreach (var (k, v) in src.Properties) clone.Properties[k] = v;
            foreach (var b in src.Brushes)
            {
                var nb = new MapBrush();
                foreach (var f in b.Faces)
                {
                    var nf = new MapFace { P1 = f.P1 + new Vector3(16,16,0), P2 = f.P2 + new Vector3(16,16,0), P3 = f.P3 + new Vector3(16,16,0), Texture = f.Texture, IsValve = f.IsValve, UAxis = f.UAxis, VAxis = f.VAxis, UOffset = f.UOffset, VOffset = f.VOffset, Rotation = f.Rotation, ScaleX = f.ScaleX, ScaleY = f.ScaleY };
                    nf.ComputePlane(); nb.Faces.Add(nf);
                }
                clone.Brushes.Add(nb);
            }
            if (clone.Properties.TryGetValue("origin", out var o)) { var v = ParseVec3(o); v.X += 32; clone.Properties["origin"] = $"{F(v.X)} {F(v.Y)} {F(v.Z)}"; }
            Entities.Add(clone);
            newIndices.Add(Entities.Count - 1);
        }
        MultiSelectedEntities.Clear();
        foreach (var ni in newIndices) MultiSelectedEntities.Add(ni);
        if (newIndices.Count > 0) SelectedIndex = newIndices.Last();
        Dirty = true; Changed?.Invoke();
    }

    public void DuplicateHighlighted()
    {
        if (MultiSelectedBrushes.Count > 0) DuplicateMultiBrushes();
        else if (MultiSelectedEntities.Count > 0) DuplicateMultiEntities();
        else if (SelectedBrush != null && Mode == EditMode.Brush) DuplicateBrush(SelectedBrushIndex);
        else DuplicateSelected();
    }

    // ---------- TrenchBroom brush ops: flip / rotate / hollow ----------

    public bool FlipSelected(GizmoAxis axis)
    {
        var br = SelectedBrush;
        if (br == null) return false;
        PushUndo($"flip {axis}");
        BrushManipulation.GetBounds(br, out var mn, out var mx);
        Vector3 center = (mn + mx) * 0.5f;
        foreach (var f in br.Faces)
        {
            Vector3 Mirror(Vector3 p) => axis==GizmoAxis.X? new Vector3(center.X*2 - p.X, p.Y, p.Z) : axis==GizmoAxis.Y? new Vector3(p.X, center.Y*2 - p.Y, p.Z) : new Vector3(p.X, p.Y, center.Z*2 - p.Z);
            f.P1 = Mirror(f.P1); f.P2 = Mirror(f.P2); f.P3 = Mirror(f.P3);
            // winding flips when mirroring — swap P2/P3 to keep normal outward
            (f.P2, f.P3) = (f.P3, f.P2);
            f.ComputePlane();
        }
        Dirty = true; Changed?.Invoke(); return true;
    }

    public bool RotateSelected90()
    {
        var br = SelectedBrush;
        if (br == null) return false;
        PushUndo("rotate 90");
        BrushManipulation.GetBounds(br, out var mn, out var mx);
        Vector3 center = (mn + mx) * 0.5f;
        foreach (var f in br.Faces)
        {
            Vector3 Rot(Vector3 p){ var d = p - center; return new Vector3(center.X - d.Y, center.Y + d.X, p.Z); }
            f.P1 = Rot(f.P1); f.P2 = Rot(f.P2); f.P3 = Rot(f.P3);
            f.ComputePlane();
        }
        Dirty = true; Changed?.Invoke(); return true;
    }

    public bool HollowSelected(float wall = 8f)
    {
        var br = SelectedBrush;
        var e = Selected;
        if (br == null || e == null) return false;
        if (!IsBoxBrush(br)) return false;
        BrushManipulation.GetBounds(br, out var mn, out var mx);
        if (mx.X - mn.X <= wall*2 + 1 || mx.Y - mn.Y <= wall*2 + 1 || mx.Z - mn.Z <= wall*2 + 1) return false;
        PushUndo("hollow");
        // remove original
        e.Brushes.Remove(br);
        // 6 walls
        Vector3 innerMin = mn + new Vector3(wall, wall, wall);
        Vector3 innerMax = mx - new Vector3(wall, wall, wall);
        // floor
        e.Brushes.Add(BuildBoxBrush(new Vector3(mn.X, mn.Y, mn.Z), new Vector3(mx.X, mx.Y, innerMin.Z), br.Faces[0].Texture));
        // ceiling
        e.Brushes.Add(BuildBoxBrush(new Vector3(mn.X, mn.Y, innerMax.Z), new Vector3(mx.X, mx.Y, mx.Z), br.Faces[0].Texture));
        // walls X
        e.Brushes.Add(BuildBoxBrush(new Vector3(mn.X, mn.Y, innerMin.Z), new Vector3(innerMin.X, mx.Y, innerMax.Z), br.Faces[0].Texture));
        e.Brushes.Add(BuildBoxBrush(new Vector3(innerMax.X, mn.Y, innerMin.Z), new Vector3(mx.X, mx.Y, innerMax.Z), br.Faces[0].Texture));
        // walls Y
        e.Brushes.Add(BuildBoxBrush(new Vector3(innerMin.X, mn.Y, innerMin.Z), new Vector3(innerMax.X, innerMin.Y, innerMax.Z), br.Faces[0].Texture));
        e.Brushes.Add(BuildBoxBrush(new Vector3(innerMin.X, innerMax.Y, innerMin.Z), new Vector3(innerMax.X, mx.Y, innerMax.Z), br.Faces[0].Texture));
        SelectedBrushIndex = -1; SelectedFaceIndex = -1; SelectedVertexIndex = -1;
        Dirty = true; Changed?.Invoke(); return true;
    }

    // unlink = explode brushes in selected entity into separate worldspawn entities (each brush independent)
    public bool UnlinkSelectedBrushes()
    {
        var e = Selected;
        if (e == null || e.Brushes.Count <= 1) return false;
        PushUndo("unlink brushes");
        var brushes = e.Brushes.ToList();
        e.Brushes.Clear();
        e.Brushes.Add(brushes[0]);
        for (int i = 1; i < brushes.Count; i++)
        {
            var ne = new MapEntity();
            ne.Properties["classname"] = e.ClassName;
            ne.Brushes.Add(brushes[i]);
            Entities.Add(ne);
        }
        // keep selection on original, clear multi
        ClearAllMulti();
        SelectedBrushIndex = 0;
        Dirty = true; Changed?.Invoke(); return true;
    }

    public bool GroupHighlightedBrushes()
    {
        if (MultiSelectedBrushes.Count < 2 && MultiSelectedEntities.Count < 2) return false;
        PushUndo("group brushes");
        // collect all brushes from multi sets
        var allBrushes = new List<MapBrush>();
        string tex = DefaultTexture;
        var toRemoveBrushes = MultiSelectedBrushes.GroupBy(k => k.ei).ToDictionary(g => g.Key, g => g.Select(x => x.bi).OrderByDescending(x => x).ToList());
        var toRemoveEnts = MultiSelectedEntities.OrderByDescending(x => x).ToList();
        foreach (var kv in toRemoveBrushes)
        {
            var ent = Entities[kv.Key];
            foreach (var bi in kv.Value) { allBrushes.Add(ent.Brushes[bi]); tex = ent.Brushes[bi].Faces.FirstOrDefault()?.Texture ?? tex; }
        }
        foreach (var ei in toRemoveEnts)
        {
            var ent = Entities[ei];
            foreach (var b in ent.Brushes) allBrushes.Add(b);
        }
        // remove
        foreach (var kv in toRemoveBrushes)
        {
            var ent = Entities[kv.Key];
            foreach (var bi in kv.Value) ent.Brushes.RemoveAt(bi);
        }
        // remove empty entities (except keep at least one worldspawn)
        foreach (var ei in toRemoveEnts) { if (Entities[ei].Brushes.Count == 0) Entities.RemoveAt(ei); }
        // create new entity
        var ne2 = new MapEntity();
        ne2.Properties["classname"] = "worldspawn";
        foreach (var b in allBrushes) ne2.Brushes.Add(b);
        Entities.Add(ne2);
        ClearAllMulti();
        SelectedIndex = Entities.Count - 1;
        SelectedBrushIndex = 0;
        LinkBrushes = true; // grouped brushes move together when highlighted again
        Dirty = true; Changed?.Invoke(); return true;
    }

    public bool TryPickBrush(Vector3 rayOriginQuake, Vector3 rayDirQuake, out int entityIndex, out int brushIndex, out float t, out int faceIndex)
    {
        entityIndex = -1; brushIndex = -1; t = float.MaxValue; faceIndex = -1;
        bool hit = false;
        for (int ei = 0; ei < Entities.Count; ei++)
        {
            var e = Entities[ei];
            for (int bi = 0; bi < e.Brushes.Count; bi++)
            {
                var br = e.Brushes[bi];
                if (BrushManipulation.RayIntersect(br, rayOriginQuake, rayDirQuake, out float bt, out _, out int bf) && bt < t && bt >= 0)
                {
                    t = bt; entityIndex = ei; brushIndex = bi; faceIndex = bf; hit = true;
                }
            }
        }
        // also try point entities as sphere 16 units
        if (!hit)
        {
            for (int ei = 0; ei < Entities.Count; ei++)
            {
                var e = Entities[ei];
                if (e.Brushes.Count > 0) continue;
                if (!e.Properties.TryGetValue("origin", out var o)) continue;
                var pos = ParseVec3(o);
                // ray-sphere
                var oc = rayOriginQuake - pos;
                float b = Vector3.Dot(oc, rayDirQuake);
                float c = Vector3.Dot(oc, oc) - 256f; // 16^2
                float disc = b*b - c;
                if (disc < 0) continue;
                float sq = MathF.Sqrt(disc);
                float th = -b - sq;
                if (th < 0) th = -b + sq;
                if (th >= 0 && th < t) { t = th; entityIndex = ei; brushIndex = -1; faceIndex = -1; hit = true; }
            }
        }
        return hit;
    }

    public Vector3 GetSelectedCenter()
    {
        if (MultiSelectedBrushes.Count > 0)
        {
            Vector3 s = Vector3.Zero; int cnt = 0;
            foreach (var (ei, bi) in MultiSelectedBrushes)
            {
                if (ei < 0 || ei >= Entities.Count) continue;
                var ent = Entities[ei];
                if (bi < 0 || bi >= ent.Brushes.Count) continue;
                s += BrushManipulation.GetCenter(ent.Brushes[bi]); cnt++;
            }
            if (cnt > 0) return s / cnt;
        }
        if (MultiSelectedEntities.Count > 0)
        {
            Vector3 s = Vector3.Zero; int cnt = 0;
            foreach (var ei in MultiSelectedEntities)
            {
                if (ei < 0 || ei >= Entities.Count) continue;
                var ent = Entities[ei];
                if (ent.Brushes.Count > 0) { foreach (var b in ent.Brushes) s += BrushManipulation.GetCenter(b); cnt += ent.Brushes.Count; }
                else if (ent.Properties.TryGetValue("origin", out var eo)) { s += ParseVec3(eo); cnt++; }
            }
            if (cnt > 0) return s / cnt;
        }
        var e = Selected;
        if (e == null) return Vector3.Zero;
        if (SelectedBrush != null) return BrushManipulation.GetCenter(SelectedBrush);
        if (e.Brushes.Count > 0)
        {
            Vector3 s = Vector3.Zero; int cnt = 0;
            foreach (var b in e.Brushes) { s += BrushManipulation.GetCenter(b); cnt++; }
            return cnt > 0 ? s / cnt : Vector3.Zero;
        }
        if (e.Properties.TryGetValue("origin", out var o2)) return ParseVec3(o2);
        return Vector3.Zero;
    }

    public void GetSelectedBounds(out Vector3 min, out Vector3 max)
    {
        if (MultiSelectedBrushes.Count > 0)
        {
            min = new Vector3(float.MaxValue); max = new Vector3(float.MinValue);
            foreach (var (ei, bi) in MultiSelectedBrushes)
            {
                if (ei < 0 || ei >= Entities.Count) continue;
                var ent = Entities[ei];
                if (bi < 0 || bi >= ent.Brushes.Count) continue;
                BrushManipulation.GetBounds(ent.Brushes[bi], out var bmin, out var bmax);
                if (bmin.X > bmax.X) continue;
                min = Vector3.Min(min, bmin); max = Vector3.Max(max, bmax);
            }
            if (min.X <= max.X) return;
        }
        if (MultiSelectedEntities.Count > 0)
        {
            min = new Vector3(float.MaxValue); max = new Vector3(float.MinValue);
            foreach (var ei in MultiSelectedEntities)
            {
                if (ei < 0 || ei >= Entities.Count) continue;
                var ent = Entities[ei];
                if (ent.Brushes.Count > 0)
                {
                    foreach (var b in ent.Brushes) { BrushManipulation.GetBounds(b, out var bmin, out var bmax); if (bmin.X > bmax.X) continue; min = Vector3.Min(min, bmin); max = Vector3.Max(max, bmax); }
                }
                else if (ent.Properties.TryGetValue("origin", out var eo)) { var p = ParseVec3(eo); min = Vector3.Min(min, p - new Vector3(8)); max = Vector3.Max(max, p + new Vector3(8)); }
            }
            if (min.X <= max.X) return;
        }
        var e = Selected;
        if (e == null) { min = max = Vector3.Zero; return; }
        if (SelectedBrush != null) { BrushManipulation.GetBounds(SelectedBrush, out min, out max); return; }
        if (e.Brushes.Count > 0)
        {
            min = new Vector3(float.MaxValue); max = new Vector3(float.MinValue);
            foreach (var b in e.Brushes)
            {
                BrushManipulation.GetBounds(b, out var bmin, out var bmax);
                if (bmin.X > bmax.X) continue;
                min = Vector3.Min(min, bmin);
                max = Vector3.Max(max, bmax);
            }
            return;
        }
        if (e.Properties.TryGetValue("origin", out var o2))
        {
            var p = ParseVec3(o2); min = p - new Vector3(8); max = p + new Vector3(8);
            return;
        }
        min = max = Vector3.Zero;
    }

    public Vector3 GetFaceCenter(MapBrush br, int faceIndex)
    {
        if (br == null || faceIndex < 0 || faceIndex >= br.Faces.Count) return BrushManipulation.GetCenter(br!);
        var polys = BrushGeometry.Build(br, out _, out _);
        var target = br.Faces[faceIndex];
        foreach (var poly in polys)
        {
            if (poly.Face != target) continue;
            if (poly.Vertices.Count == 0) continue;
            Vector3 sum = Vector3.Zero;
            foreach (var v in poly.Vertices) sum += v;
            return sum / poly.Vertices.Count;
        }
        // fallback: plane point projected
        return target.P1;
    }

    public bool TryPickFaceHandle(Vector3 rayOriginQuake, Vector3 rayDirQuake, out int faceIndex, out float t)
    {
        faceIndex = -1; t = float.MaxValue;
        var br = SelectedBrush;
        if (br == null) return false;
        bool hit = false;
        for (int i = 0; i < br.Faces.Count; i++)
        {
            var c = GetFaceCenter(br, i);
            var oc = rayOriginQuake - c;
            float b = Vector3.Dot(oc, rayDirQuake);
            float c2 = Vector3.Dot(oc, oc) - 144f; // 12^2
            float disc = b * b - c2;
            if (disc < 0) continue;
            float th = -b - MathF.Sqrt(disc);
            if (th < 0) th = -b + MathF.Sqrt(disc);
            if (th >= 0 && th < t) { t = th; faceIndex = i; hit = true; }
        }
        return hit;
    }

    // ---------- vertex editing (TrenchBroom / Hammer vertex tool) ----------

    public bool IsBoxBrush(MapBrush? br)
    {
        if (br == null || br.Faces.Count != 6) return false;
        foreach (var f in br.Faces)
        {
            var n = f.Normal;
            bool ax = (MathF.Abs(MathF.Abs(n.X) - 1) < 0.01f && MathF.Abs(n.Y) < 0.01f && MathF.Abs(n.Z) < 0.01f)
                   || (MathF.Abs(MathF.Abs(n.Y) - 1) < 0.01f && MathF.Abs(n.X) < 0.01f && MathF.Abs(n.Z) < 0.01f)
                   || (MathF.Abs(MathF.Abs(n.Z) - 1) < 0.01f && MathF.Abs(n.X) < 0.01f && MathF.Abs(n.Y) < 0.01f);
            if (!ax) return false;
        }
        return true;
    }

    public Vector3[] GetSelectedBrushCorners()
    {
        var br = SelectedBrush;
        if (!IsBoxBrush(br)) return Array.Empty<Vector3>();
        BrushManipulation.GetBounds(br, out var min, out var max);
        return new[]
        {
            new Vector3(min.X, min.Y, min.Z), // 0
            new Vector3(max.X, min.Y, min.Z), // 1
            new Vector3(max.X, max.Y, min.Z), // 2
            new Vector3(min.X, max.Y, min.Z), // 3
            new Vector3(min.X, min.Y, max.Z), // 4
            new Vector3(max.X, min.Y, max.Z), // 5
            new Vector3(max.X, max.Y, max.Z), // 6
            new Vector3(min.X, max.Y, max.Z), // 7
        };
    }

    public bool TryGetSelectedVertex(out Vector3 pos)
    {
        pos = default;
        if (SelectedVertexIndex < 0 || SelectedBrush == null) return false;
        var corners = GetSelectedBrushCorners();
        if (SelectedVertexIndex < 0 || SelectedVertexIndex >= corners.Length) return false;
        pos = corners[SelectedVertexIndex];
        return true;
    }

    public void SelectVertex(int brushEntityIndex, int brushIndex, int vertexIndex)
    {
        SelectBrush(brushEntityIndex, brushIndex, -1);
        SelectedVertexIndex = Math.Clamp(vertexIndex, -1, 7);
        ValidateBrushSelection();
        Changed?.Invoke();
    }

    public bool MoveSelectedVertex(Vector3 deltaQuake, bool pushUndo = true)
    {
        var br = SelectedBrush;
        if (br == null || SelectedVertexIndex < 0) return false;
        if (!IsBoxBrush(br)) return false;
        if (deltaQuake.LengthSquared() < 1e-6f) return false;
        if (GridSnapEnabled && GridSize > 0) deltaQuake = BrushManipulation.Snap(deltaQuake, GridSize);
        BrushManipulation.GetBounds(br, out var curMin, out var curMax);
        int vi = SelectedVertexIndex;
        // bit 0 = maxX, bit1 = maxY, bit2 = maxZ
        Vector3 newMin = curMin, newMax = curMax;
        if ((vi & 1) != 0) newMax.X += deltaQuake.X; else newMin.X += deltaQuake.X;
        if ((vi & 2) != 0) newMax.Y += deltaQuake.Y; else newMin.Y += deltaQuake.Y;
        if ((vi & 4) != 0) newMax.Z += deltaQuake.Z; else newMin.Z += deltaQuake.Z;
        // order + min thickness
        float minEdge = Math.Max(GridSize > 0 ? GridSize : 1f, 1f);
        if (newMax.X - newMin.X < minEdge || newMax.Y - newMin.Y < minEdge || newMax.Z - newMin.Z < minEdge) return false;
        if (GridSnapEnabled && GridSize > 0)
        {
            newMin = BrushManipulation.Snap(newMin, GridSize);
            newMax = BrushManipulation.Snap(newMax, GridSize);
            if (newMax.X - newMin.X < minEdge || newMax.Y - newMin.Y < minEdge || newMax.Z - newMin.Z < minEdge) return false;
        }
        if (pushUndo) PushUndo("move vertex");
        BrushManipulation.ResizeToBounds(br, newMin, newMax);
        // keep same vertex selected — it stays at the moved corner
        Dirty = true;
        Changed?.Invoke();
        return true;
    }

    public bool TryPickVertex(Vector3 rayOriginQuake, Vector3 rayDirQuake, out int vertexIndex, out float t)
    {
        vertexIndex = -1; t = float.MaxValue;
        var br = SelectedBrush;
        if (br == null || !IsBoxBrush(br)) return false;
        var corners = GetSelectedBrushCorners();
        float best = float.MaxValue;
        for (int i = 0; i < corners.Length; i++)
        {
            var p = corners[i];
            // ray-sphere 10 units
            var oc = rayOriginQuake - p;
            float b = Vector3.Dot(oc, rayDirQuake);
            float c = Vector3.Dot(oc, oc) - 100f; // 10^2
            float disc = b * b - c;
            if (disc < 0) continue;
            float th = -b - MathF.Sqrt(disc);
            if (th < 0) th = -b + MathF.Sqrt(disc);
            if (th >= 0 && th < best) { best = th; vertexIndex = i; }
        }
        if (vertexIndex >= 0) { t = best; return true; }
        return false;
    }

    // Extrude selected face(s) via world-axis delta — finds most aligned face normal.
    public bool ExtrudeSelectedBrush(Vector3 deltaQuake, bool pushUndo = true)
    {
        var br = SelectedBrush;
        if (br == null) return false;
        if (deltaQuake.LengthSquared() < 1e-6f) return false;
        if (GridSnapEnabled && GridSize > 0) deltaQuake = BrushManipulation.Snap(deltaQuake, GridSize);
        // prefer already-selected face; otherwise pick dominant axis
        if (SelectedFaceIndex >= 0)
            return MoveSelectedFace(Vector3.Dot(deltaQuake, br.Faces[SelectedFaceIndex].Normal), pushUndo);
        // pick face whose normal best aligns with delta
        float best = -2f; int bestIdx = -1;
        for (int i = 0; i < br.Faces.Count; i++) { float d = Vector3.Dot(Vector3.Normalize(deltaQuake), br.Faces[i].Normal); if (d > best) { best = d; bestIdx = i; } }
        if (bestIdx >= 0 && best > 0.3f)
        {
            int prev = SelectedFaceIndex;
            SelectedFaceIndex = bestIdx;
            bool ok = MoveSelectedFace(Vector3.Dot(deltaQuake, br.Faces[bestIdx].Normal), pushUndo);
            if (!ok) SelectedFaceIndex = prev;
            return ok;
        }
        // fallback: resize AABB along delta (pushes the positive side)
        BrushManipulation.GetBounds(br, out var mn, out var mx);
        Vector3 newMin = mn, newMax = mx;
        if (deltaQuake.X > 0) newMax.X += deltaQuake.X; else if (deltaQuake.X < 0) newMin.X += deltaQuake.X;
        if (deltaQuake.Y > 0) newMax.Y += deltaQuake.Y; else if (deltaQuake.Y < 0) newMin.Y += deltaQuake.Y;
        if (deltaQuake.Z > 0) newMax.Z += deltaQuake.Z; else if (deltaQuake.Z < 0) newMin.Z += deltaQuake.Z;
        float minEdge = Math.Max(GridSize, 1f);
        if (newMax.X - newMin.X < minEdge || newMax.Y - newMin.Y < minEdge || newMax.Z - newMin.Z < minEdge) return false;
        if (pushUndo) PushUndo("extrude brush");
        BrushManipulation.ResizeToBounds(br, newMin, newMax);
        Dirty = true; Changed?.Invoke();
        return true;
    }

    // ---------- persistence ----------

    public void Save(string? path = null)
    {
        path ??= FilePath;
        if (path == null) throw new InvalidOperationException("No file path");
        var full = Path.GetFullPath(path);
        // ensure directory exists
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        MapWriter.Write(full, Entities);
        FilePath = full;
        Dirty = false;
        Console.WriteLine($"[editor] saved {Entities.Count} entities -> '{full}'");
        // keep .scene.json in sync so GameApp can use up-to-date scene without temp compile (TrenchBroom exports on save)
        try
        {
            var scenePath = Path.ChangeExtension(full, ".scene.json");
            var scene = MapCompilerService.Compile(Entities, Path.GetFileNameWithoutExtension(full));
            KREAN.Core.Scenes.SceneSerializer.Write(scene, scenePath, true);
            Console.WriteLine($"[editor] exported scene -> '{scenePath}' ({scene.Meshes.Count} meshes, {scene.Collision.Count} brushes)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[editor] scene export failed: {ex.Message}");
        }
    }

    public void SaveCopy(string path)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        MapWriter.Write(full, Entities);
        Console.WriteLine($"[editor] saved copy -> '{full}'");
    }

    // ---------- helpers ----------

    static Vector3 ParseVec3(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        float F2(int i) => i < parts.Length && float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;
        return new Vector3(F2(0), F2(1), F2(2));
    }

    public static Vector3 ParseVec3Public(string text) => ParseVec3(text);

    static string F(float f) => f.ToString("0.##", CultureInfo.InvariantCulture);

    public bool TryGetOriginQuake(out Vector3 pos)
    {
        pos = default;
        var e = Selected;
        if (e == null) return false;
        if (!e.Properties.TryGetValue("origin", out var s)) return false;
        pos = ParseVec3(s);
        return true;
    }
}
