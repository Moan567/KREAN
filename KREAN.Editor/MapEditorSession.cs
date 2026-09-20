using System.Globalization;
using System.Numerics;
using KREAN.Core;
using KREAN.MapCompiler;

namespace KREAN.Editor;

public enum EditMode { Object, Brush, Face, Vertex, Clip, Edge, Entity }

public sealed class MapEditorSession
{
    public List<MapEntity> Entities { get; private set; }
    public string? FilePath { get; private set; }
    public int SelectedIndex { get; private set; } = -1;
    public int SelectedBrushIndex { get; private set; } = -1;
    public int SelectedFaceIndex { get; private set; } = -1;
    public int SelectedVertexIndex { get; private set; } = -1;
    public int SelectedEdgeIndex { get; private set; } = -1;
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

    // Clip tool state (X)
    public List<Vector3> ClipPoints { get; } = new();
    public bool ClipKeepFront { get; set; } = true;
    public bool ClipKeepBoth { get; set; } = false;

    // Layers / visibility
    public string ActiveLayer { get; set; } = "Default";
    readonly Dictionary<string,bool> _layerVisible = new(StringComparer.OrdinalIgnoreCase){{"Default",true}};
    public IReadOnlyDictionary<string,bool> LayerVisibility => _layerVisible;
    public string GetEntityLayer(MapEntity e)
    {
        if(e.Properties.TryGetValue("_layer", out var l) && !string.IsNullOrWhiteSpace(l)) return l;
        if(e.Properties.TryGetValue("layer", out var l2) && !string.IsNullOrWhiteSpace(l2)) return l2;
        return "Default";
    }
    public bool IsLayerVisible(string layer) => _layerVisible.TryGetValue(layer, out var v) ? v : true;
    public bool IsEntityVisible(MapEntity e) => IsLayerVisible(GetEntityLayer(e));
    public bool IsEntityVisible(int ei) => ei>=0 && ei<Entities.Count && IsEntityVisible(Entities[ei]);
    public void SetEntityLayer(int ei, string layer)
    {
        if(ei<0||ei>=Entities.Count) return;
        if(string.IsNullOrWhiteSpace(layer)) layer="Default";
        PushUndo("layer");
        Entities[ei].Properties["_layer"]=layer;
        if(!_layerVisible.ContainsKey(layer)) _layerVisible[layer]=true;
        ActiveLayer=layer;
        Dirty=true; Changed?.Invoke();
    }
    public void SetLayerVisible(string layer, bool visible)
    {
        _layerVisible[layer]=visible;
        Changed?.Invoke();
    }
    public IEnumerable<string> AllLayers
    {
        get
        {
            var set=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"Default"};
            foreach(var e in Entities) set.Add(GetEntityLayer(e));
            foreach(var k in _layerVisible.Keys) set.Add(k);
            return set.OrderBy(s=>s);
        }
    }

    // TrenchBroom-style brush tool defaults (Quake units, Z-up).
    public Vector3 BrushDefaultSize { get; set; } = new Vector3(64, 64, 64);
    public string DefaultTexture { get; set; } = "wall";

    // undo/redo
    readonly Stack<Snapshot> _undo = new();
    readonly Stack<Snapshot> _redo = new();
    string? _lastUndoLabel;
    DateTime _lastUndoTime = DateTime.MinValue;
    bool _inUndoGroup;
    Snapshot? _groupSnapshot;

    sealed class Snapshot
    {
        public List<MapEntity> Entities = null!;
        public int Selected;
        public int SelBrush;
        public int SelFace;
        public int SelVertex;
        public int SelEdge;
        public EditMode Mode;
        public string? Path;
        public string Label="";
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

    void PushUndo(string label, bool coalesce = false)
    {
        if (_inUndoGroup)
        {
            if (_groupSnapshot==null)
            {
                _groupSnapshot = new Snapshot { Entities = DeepClone(Entities), Selected = SelectedIndex, SelBrush = SelectedBrushIndex, SelFace = SelectedFaceIndex, SelVertex = SelectedVertexIndex, SelEdge = SelectedEdgeIndex, Mode = Mode, Path = FilePath, Label = label };
            }
            return;
        }
        if (coalesce && _lastUndoLabel==label && (DateTime.UtcNow - _lastUndoTime).TotalMilliseconds < 600 && _undo.Count>0)
        {
            // coalesce: keep earlier snapshot, don't push duplicate for rapid edits (typing properties)
            _lastUndoTime = DateTime.UtcNow;
            return;
        }
        _undo.Push(new Snapshot { Entities = DeepClone(Entities), Selected = SelectedIndex, SelBrush = SelectedBrushIndex, SelFace = SelectedFaceIndex, SelVertex = SelectedVertexIndex, SelEdge = SelectedEdgeIndex, Mode = Mode, Path = FilePath, Label = label });
        _redo.Clear();
        _lastUndoLabel = label; _lastUndoTime = DateTime.UtcNow;
        if (_undo.Count > 64)
        {
            var tmp = _undo.Reverse().Skip(1).Reverse().ToList();
            _undo.Clear();
            foreach (var s in tmp.Reverse<MapEditorSession.Snapshot>()) _undo.Push(s);
        }
    }

    public void BeginUndoGroup(string label)
    {
        if (_inUndoGroup) return;
        _inUndoGroup = true;
        _groupSnapshot = new Snapshot { Entities = DeepClone(Entities), Selected = SelectedIndex, SelBrush = SelectedBrushIndex, SelFace = SelectedFaceIndex, SelVertex = SelectedVertexIndex, SelEdge = SelectedEdgeIndex, Mode = Mode, Path = FilePath, Label = label };
    }
    public void EndUndoGroup(bool commit = true)
    {
        if (!_inUndoGroup) return;
        _inUndoGroup = false;
        if (commit && _groupSnapshot!=null)
        {
            _undo.Push(_groupSnapshot);
            _redo.Clear();
            if (_undo.Count > 64)
            {
                var tmp = _undo.Reverse().Skip(1).Reverse().ToList();
                _undo.Clear();
                foreach (var s in tmp.Reverse<MapEditorSession.Snapshot>()) _undo.Push(s);
            }
        }
        _groupSnapshot = null;
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Undo()
    {
        if (_inUndoGroup) EndUndoGroup(false);
        if (!CanUndo) return;
        _redo.Push(new Snapshot { Entities = DeepClone(Entities), Selected = SelectedIndex, SelBrush = SelectedBrushIndex, SelFace = SelectedFaceIndex, SelVertex = SelectedVertexIndex, SelEdge = SelectedEdgeIndex, Mode = Mode, Path = FilePath });
        var s = _undo.Pop();
        Entities = s.Entities;
        SelectedIndex = s.Selected;
        SelectedBrushIndex = s.SelBrush;
        SelectedFaceIndex = s.SelFace;
        SelectedVertexIndex = s.SelVertex;
        SelectedEdgeIndex = s.SelEdge;
        Mode = s.Mode;
        FilePath = s.Path;
        Dirty = true;
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (_inUndoGroup) EndUndoGroup(false);
        if (!CanRedo) return;
        _undo.Push(new Snapshot { Entities = DeepClone(Entities), Selected = SelectedIndex, SelBrush = SelectedBrushIndex, SelFace = SelectedFaceIndex, SelVertex = SelectedVertexIndex, SelEdge = SelectedEdgeIndex, Mode = Mode, Path = FilePath });
        var s = _redo.Pop();
        Entities = s.Entities;
        SelectedIndex = s.Selected;
        SelectedBrushIndex = s.SelBrush;
        SelectedFaceIndex = s.SelFace;
        SelectedVertexIndex = s.SelVertex;
        SelectedEdgeIndex = s.SelEdge;
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
        if (e == null || e.Brushes.Count == 0) { SelectedBrushIndex = -1; SelectedFaceIndex = -1; SelectedVertexIndex = -1; SelectedEdgeIndex = -1; return; }
        if (SelectedBrushIndex < 0 || SelectedBrushIndex >= e.Brushes.Count) { SelectedBrushIndex = -1; SelectedFaceIndex = -1; SelectedVertexIndex = -1; SelectedEdgeIndex = -1; return; }
        var b = e.Brushes[SelectedBrushIndex];
        if (SelectedFaceIndex < -1 || SelectedFaceIndex >= b.Faces.Count) SelectedFaceIndex = -1;
        if (SelectedBrush != null && SelectedBrush.Faces.Count == 6)
        {
            if (SelectedVertexIndex < -1 || SelectedVertexIndex >= 8) SelectedVertexIndex = -1;
            if (SelectedEdgeIndex < -1 || SelectedEdgeIndex >= 12) SelectedEdgeIndex = -1;
        }
        else
        {
            SelectedVertexIndex = -1;
            if (SelectedEdgeIndex < -1 || SelectedEdgeIndex >= 64) SelectedEdgeIndex = -1;
        }
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
        if (pushUndo) PushUndo($"set {key}", coalesce: true);
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
        if (!string.IsNullOrWhiteSpace(ActiveLayer) && ActiveLayer!="Default") e.Properties["_layer"]=ActiveLayer;
        else if(!_layerVisible.ContainsKey("Default")) _layerVisible["Default"]=true;
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
        int ws = Entities.FindIndex(e => e.ClassName == "worldspawn" && GetEntityLayer(e)==ActiveLayer);
        if (ws<0) ws = Entities.FindIndex(e => e.ClassName == "worldspawn");
        MapEntity target;
        if (ws < 0)
        {
            target = new MapEntity();
            target.Properties["classname"] = "worldspawn";
            if(ActiveLayer!="Default") target.Properties["_layer"]=ActiveLayer;
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
        if (e == null) return false;
        // func_group ungrooup: explode brushes and clear _group from members
        if (e.ClassName=="func_group" && e.Properties.TryGetValue("_group", out var gname))
        {
            if(e.Brushes.Count<=1 && !Entities.Any(o=>o.Properties.TryGetValue("_group", out var gg) && gg==gname)) return false;
            PushUndo("ungroup");
            var brushes = e.Brushes.ToList();
            // move brushes to worldspawn
            foreach(var b in brushes)
            {
                var ne=new MapEntity(); ne.Properties["classname"]="worldspawn"; ne.Brushes.Add(b); Entities.Add(ne);
            }
            Entities.Remove(e);
            // clear group from point entities
            foreach(var o in Entities) if(o.Properties.TryGetValue("_group", out var gg2) && gg2==gname) o.Properties.Remove("_group");
            ClearAllMulti();
            SelectedIndex=Math.Clamp(SelectedIndex,0,Entities.Count-1);
            Dirty=true; Changed?.Invoke(); return true;
        }
        if (e.Brushes.Count <= 1) return false;
        PushUndo("unlink brushes");
        var brushes2 = e.Brushes.ToList();
        e.Brushes.Clear();
        e.Brushes.Add(brushes2[0]);
        for (int i = 1; i < brushes2.Count; i++)
        {
            var ne = new MapEntity();
            ne.Properties["classname"] = e.ClassName;
            ne.Brushes.Add(brushes2[i]);
            Entities.Add(ne);
        }
        // keep selection on original, clear multi
        ClearAllMulti();
        SelectedBrushIndex = 0;
        Dirty = true; Changed?.Invoke(); return true;
    }

    // ---------- Clip tool ----------

    public void ClearClipPoints() { if (ClipPoints.Count > 0) { ClipPoints.Clear(); Changed?.Invoke(); } }

    public bool AddClipPoint(Vector3 quakePoint)
    {
        if (GridSnapEnabled && GridSize > 0) quakePoint = BrushManipulation.Snap(quakePoint, GridSize);
        if (ClipPoints.Count >= 3) ClipPoints.Clear();
        ClipPoints.Add(quakePoint);
        Changed?.Invoke();
        return ClipPoints.Count == 3;
    }

    public bool TryGetClipPlane(out Vector3 point, out Vector3 normal)
    {
        point = default; normal = default;
        if (ClipPoints.Count < 2) return false;
        if (ClipPoints.Count == 2)
        {
            // 2-point: extrude along view? caller must supply third - treat as vertical plane along Z
            var dir = ClipPoints[1] - ClipPoints[0];
            if (dir.LengthSquared() < 1e-4f) return false;
            // vertical plane: normal is perpendicular to dir in XY, up is Z
            Vector3 n = Vector3.Normalize(new Vector3(-dir.Y, dir.X, 0));
            if (n.LengthSquared() < 0.1f) n = Vector3.UnitX;
            point = ClipPoints[0]; normal = n; return true;
        }
        var a = ClipPoints[0]; var b = ClipPoints[1]; var c = ClipPoints[2];
        var ab = b - a; var ac = c - a;
        var n3 = Vector3.Cross(ab, ac);
        if (n3.LengthSquared() < 1e-4f) return false;
        normal = Vector3.Normalize(n3);
        point = a;
        return true;
    }

    public bool TryGetClipPlaneFromView(Vector3 viewDirQuake, out Vector3 point, out Vector3 normal)
    {
        point = default; normal = default;
        if (ClipPoints.Count == 2)
        {
            var dir = ClipPoints[1] - ClipPoints[0];
            if (dir.LengthSquared() < 1e-4f) return false;
            // plane contains the line and is facing view
            Vector3 up = Vector3.Normalize(Vector3.Cross(dir, viewDirQuake));
            if (up.LengthSquared() < 1e-4f) up = Vector3.UnitZ;
            normal = Vector3.Normalize(Vector3.Cross(dir, up));
            // ensure normal faces view
            if (Vector3.Dot(normal, viewDirQuake) > 0) normal = -normal;
            point = ClipPoints[0];
            return true;
        }
        return TryGetClipPlane(out point, out normal);
    }

    public bool ExecuteClip(bool keepFront, bool keepBoth)
    {
        if (!TryGetClipPlane(out var pt, out var n)) return false;
        // collect targets: selected brush + multi brushes, else all brushes of selected entity
        List<(int ei, int bi)> targets = new();
        if (MultiSelectedBrushes.Count > 0) targets.AddRange(MultiSelectedBrushes);
        else if (SelectedBrush != null) targets.Add((SelectedIndex, SelectedBrushIndex));
        else if (Selected != null && Selected.Brushes.Count > 0)
            for (int i = 0; i < Selected.Brushes.Count; i++) targets.Add((SelectedIndex, i));
        if (targets.Count == 0) return false;

        PushUndo("clip");
        bool any = false;
        // sort descending so adding new brushes doesn't affect indices of pending
        var byEnt = targets.GroupBy(k => k.ei).ToDictionary(g => g.Key, g => g.Select(x => x.bi).OrderByDescending(x => x).ToList());
        foreach (var kv in byEnt)
        {
            var ent = Entities[kv.Key];
            foreach (var bi in kv.Value)
            {
                if (bi < 0 || bi >= ent.Brushes.Count) continue;
                var br = ent.Brushes[bi];
                if (!BrushManipulation.TryClip(br, pt, n, DefaultTexture, out var front, out var back))
                    continue;
                any = true;
                if (keepBoth)
                {
                    ent.Brushes.RemoveAt(bi);
                    ent.Brushes.Add(front!);
                    ent.Brushes.Add(back!);
                }
                else if (keepFront)
                {
                    ent.Brushes.RemoveAt(bi);
                    ent.Brushes.Add(front!);
                }
                else
                {
                    ent.Brushes.RemoveAt(bi);
                    ent.Brushes.Add(back!);
                }
            }
        }
        if (any)
        {
            ClipPoints.Clear();
            SelectedFaceIndex = -1; SelectedVertexIndex = -1;
            if (MultiSelectedBrushes.Count > 0) ClearMultiBrush();
            // select last created
            if (Selected != null && Selected.Brushes.Count > 0) SelectedBrushIndex = Selected.Brushes.Count - 1;
            Dirty = true; Changed?.Invoke();
        }
        else
        {
            // revert undo if nothing happened
            Undo(); _redo.Clear();
        }
        return any;
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
        string groupName = $"group_{Guid.NewGuid():N}".Substring(0,8);
        // handle point entities in toRemoveEnts: set _group, keep them
        foreach (var ei in toRemoveEnts.OrderByDescending(x=>x))
        {
            if (ei<0||ei>=Entities.Count) continue;
            var ent = Entities[ei];
            if (ent.Brushes.Count==0)
            {
                ent.Properties["_group"]=groupName;
            }
            else
            {
                // brush entity to be merged into group — remove it (its brushes already in allBrushes)
                Entities.RemoveAt(ei);
            }
        }
        // create new entity as func_group (hierarchy)
        var ne2 = new MapEntity();
        // if grouping entities, keep as func_group with group id; if grouping brushes, also func_group
        ne2.Properties["classname"] = "func_group";
        ne2.Properties["targetname"] = groupName;
        ne2.Properties["_group"] = groupName;
        // give group a color for editor
        var grpColor = new Vector3(0.6f + (groupName.GetHashCode()&0xFF)/512f, 0.7f, 0.9f);
        ne2.Properties["_color"] = $"{grpColor.X:0.##} {grpColor.Y:0.##} {grpColor.Z:0.##}";
        foreach (var b in allBrushes) ne2.Brushes.Add(b);
        Entities.Add(ne2);
        ClearAllMulti();
        SelectedIndex = Entities.Count - 1;
        SelectedBrushIndex = 0;
        LinkBrushes = true;
        Dirty = true; Changed?.Invoke(); return true;
    }

    public bool TryPickBrush(Vector3 rayOriginQuake, Vector3 rayDirQuake, out int entityIndex, out int brushIndex, out float t, out int faceIndex)
    {
        entityIndex = -1; brushIndex = -1; t = float.MaxValue; faceIndex = -1;
        bool hit = false;
        for (int ei = 0; ei < Entities.Count; ei++)
        {
            var e = Entities[ei];
            if (!IsEntityVisible(e)) continue;
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
                if (!IsEntityVisible(e)) continue;
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

    // ---------- Edge editing ----------
    static readonly (int a,int b)[] BoxEdges = new[]{ (0,1),(1,2),(2,3),(3,0),(4,5),(5,6),(6,7),(7,4),(0,4),(1,5),(2,6),(3,7) };

    List<(Vector3 a, Vector3 b)> GetBrushEdges(MapBrush? br)
    {
        var list = new List<(Vector3 a, Vector3 b)>();
        if (br==null) return list;
        // try box fast path
        if (IsBoxBrush(br))
        {
            var corners=GetSelectedBrushCorners();
            // if called for non-selected brush, build generic
            if (br!=SelectedBrush)
            {
                BrushManipulation.GetBounds(br, out var mn, out var mx);
                corners = new[]{ new Vector3(mn.X,mn.Y,mn.Z), new Vector3(mx.X,mn.Y,mn.Z), new Vector3(mx.X,mx.Y,mn.Z), new Vector3(mn.X,mx.Y,mn.Z), new Vector3(mn.X,mn.Y,mx.Z), new Vector3(mx.X,mn.Y,mx.Z), new Vector3(mx.X,mx.Y,mx.Z), new Vector3(mn.X,mx.Y,mx.Z)};
            }
            for(int i=0;i<BoxEdges.Length;i++) list.Add((corners[BoxEdges[i].a], corners[BoxEdges[i].b]));
            return list;
        }
        // generic: build from geometry
        var polys=BrushGeometry.Build(br, out _, out _);
        var seen=new HashSet<string>();
        foreach(var poly in polys)
        {
            for(int i=0;i<poly.Vertices.Count;i++)
            {
                var a=poly.Vertices[i]; var b=poly.Vertices[(i+1)%poly.Vertices.Count];
                var mid=(a+b)*0.5f;
                string key=$"{MathF.Round(mid.X)}{MathF.Round(mid.Y)}{MathF.Round(mid.Z)}:{MathF.Round((b-a).Length())}";
                // dedup via mid distance <0.1
                bool dup=false;
                foreach(var e in list){ var m2=(e.a+e.b)*0.5f; if(Vector3.DistanceSquared(mid,m2)<0.5f) {dup=true;break;} }
                if(!dup) list.Add((a,b));
            }
        }
        return list;
    }

    public void SelectEdge(int entityIdx, int brushIdx, int edgeIdx)
    {
        SelectBrush(entityIdx, brushIdx, -1);
        var edges=GetBrushEdges(SelectedBrush);
        int max=edges.Count>0?edges.Count-1:11;
        SelectedEdgeIndex = Math.Clamp(edgeIdx, -1, max);
        ValidateBrushSelection();
        Changed?.Invoke();
    }

    public bool TryPickEdge(Vector3 rayOriginQuake, Vector3 rayDirQuake, out int edgeIndex, out float t)
    {
        edgeIndex=-1; t=float.MaxValue;
        var br = SelectedBrush; if(br==null) return false;
        var edges=GetBrushEdges(br);
        if(edges.Count==0) return false;
        float best=float.MaxValue; int bestIdx=-1;
        for(int i=0;i<edges.Count;i++)
        {
            var mid=(edges[i].a+edges[i].b)*0.5f;
            var oc = rayOriginQuake - mid;
            float bv=Vector3.Dot(oc, rayDirQuake);
            float c=Vector3.Dot(oc,oc)-144f; // 12^2
            float disc=bv*bv-c; if(disc<0) continue;
            float th=-bv-MathF.Sqrt(disc); if(th<0) th=-bv+MathF.Sqrt(disc);
            if(th>=0 && th<best){ best=th; bestIdx=i; }
        }
        if(bestIdx>=0){ edgeIndex=bestIdx; t=best; return true; }
        return false;
    }

    public bool MoveSelectedEdge(Vector3 deltaQuake, bool pushUndo=true)
    {
        var br=SelectedBrush; if(br==null || SelectedEdgeIndex<0) return false;
        if(deltaQuake.LengthSquared()<1e-6f) return false;
        if(GridSnapEnabled && GridSize>0) deltaQuake=BrushManipulation.Snap(deltaQuake, GridSize);
        var edges=GetBrushEdges(br);
        if(SelectedEdgeIndex<0 || SelectedEdgeIndex>=edges.Count) return false;
        var (a,b)=edges[SelectedEdgeIndex];
        Vector3 edgeDir=b-a; float len=edgeDir.Length(); if(len<1e-4f) return false; edgeDir/=len;
        float along=Vector3.Dot(deltaQuake, edgeDir);
        Vector3 perp=deltaQuake - edgeDir*along;
        if(perp.LengthSquared()<1e-6f) return false;
        // box fast path: keep exact AABB behavior for boxes (more precise, grid-friendly)
        if(IsBoxBrush(br))
        {
            var corners=GetSelectedBrushCorners();
            // find which box edge we are (by closest mid)
            int boxIdx=-1; float bestD=float.MaxValue;
            for(int i=0;i<BoxEdges.Length;i++){ var m=(corners[BoxEdges[i].a]+corners[BoxEdges[i].b])*0.5f; var d=Vector3.DistanceSquared(m,(a+b)*0.5f); if(d<bestD){bestD=d; boxIdx=i;}}
            if(boxIdx>=0)
            {
                BrushManipulation.GetBounds(br, out var curMin, out var curMax);
                Vector3 newMin=curMin, newMax=curMax;
                switch(boxIdx){
                    case 0: newMin.Y+=perp.Y; newMin.Z+=perp.Z; break;
                    case 1: newMax.X+=perp.X; newMin.Z+=perp.Z; break;
                    case 2: newMax.Y+=perp.Y; newMin.Z+=perp.Z; break;
                    case 3: newMin.X+=perp.X; newMin.Z+=perp.Z; break;
                    case 4: newMin.Y+=perp.Y; newMax.Z+=perp.Z; break;
                    case 5: newMax.X+=perp.X; newMax.Z+=perp.Z; break;
                    case 6: newMax.Y+=perp.Y; newMax.Z+=perp.Z; break;
                    case 7: newMin.X+=perp.X; newMax.Z+=perp.Z; break;
                    case 8: newMin.X+=perp.X; newMin.Y+=perp.Y; break;
                    case 9: newMax.X+=perp.X; newMin.Y+=perp.Y; break;
                    case 10: newMax.X+=perp.X; newMax.Y+=perp.Y; break;
                    case 11: newMin.X+=perp.X; newMax.Y+=perp.Y; break;
                }
                float minEdge=Math.Max(GridSize>0?GridSize:1f,1f);
                if(newMax.X-newMin.X<minEdge || newMax.Y-newMin.Y<minEdge || newMax.Z-newMin.Z<minEdge) return false;
                if(GridSnapEnabled && GridSize>0){ newMin=BrushManipulation.Snap(newMin, GridSize); newMax=BrushManipulation.Snap(newMax, GridSize); if(newMax.X-newMin.X<minEdge||newMax.Y-newMin.Y<minEdge||newMax.Z-newMin.Z<minEdge) return false; }
                if(pushUndo) PushUndo("move edge");
                BrushManipulation.ResizeToBounds(br, newMin, newMax);
                Dirty=true; Changed?.Invoke(); return true;
            }
        }
        // generic: move adjacent faces together
        const float eps=0.6f;
        var adj=new List<MapFace>();
        foreach(var f in br.Faces)
        {
            float da=MathF.Abs(Vector3.Dot(f.Normal,a)-f.Distance);
            float db=MathF.Abs(Vector3.Dot(f.Normal,b)-f.Distance);
            if(da<eps && db<eps) adj.Add(f);
        }
        if(adj.Count==0) return false;
        if(pushUndo) PushUndo("move edge");
        // store backup for revert
        var backup=adj.Select(f=>(f.P1,f.P2,f.P3)).ToList();
        foreach(var f in adj)
        {
            float off=Vector3.Dot(perp, f.Normal);
            if(MathF.Abs(off)<1e-4f) continue;
            f.P1+=f.Normal*off; f.P2+=f.Normal*off; f.P3+=f.Normal*off; f.ComputePlane();
        }
        if(!BrushManipulation.IsBrushValid(br))
        {
            for(int i=0;i<adj.Count;i++){ adj[i].P1=backup[i].P1; adj[i].P2=backup[i].P2; adj[i].P3=backup[i].P3; adj[i].ComputePlane(); }
            if(pushUndo){ Undo(); _redo.Clear(); }
            return false;
        }
        Dirty=true; Changed?.Invoke(); return true;
    }

    public Vector3[] GetSelectedEdgeMidpoints()
    {
        var edges=GetBrushEdges(SelectedBrush);
        var mids=new Vector3[edges.Count];
        for(int i=0;i<edges.Count;i++) mids[i]=(edges[i].a+edges[i].b)*0.5f;
        return mids;
    }

    // ---------- Scale / Rotate ----------
    public bool ScaleSelectedBrush(Vector3 scale, bool pushUndo=true)
    {
        var br=SelectedBrush; if(br==null) return false;
        if(scale.X<=0||scale.Y<=0||scale.Z<=0) return false;
        if(Math.Abs(scale.X-1)<0.001f && Math.Abs(scale.Y-1)<0.001f && Math.Abs(scale.Z-1)<0.001f) return false;
        if(pushUndo) PushUndo("scale");
        var c=BrushManipulation.GetCenter(br);
        BrushManipulation.ScaleAroundCenter(br, c, scale);
        if(!BrushManipulation.IsBrushValid(br)){ Undo(); _redo.Clear(); return false; }
        Dirty=true; Changed?.Invoke(); return true;
    }

    public bool RotateSelectedBrush(Quaternion rot, bool pushUndo=true)
    {
        var br=SelectedBrush; if(br==null) return false;
        if(pushUndo) PushUndo("rotate");
        var c=BrushManipulation.GetCenter(br);
        BrushManipulation.RotateAroundCenter(br, c, rot);
        if(!BrushManipulation.IsBrushValid(br)){ Undo(); _redo.Clear(); return false; }
        Dirty=true; Changed?.Invoke(); return true;
    }

    // ---------- CSG ----------
    public bool CsgSubtract()
    {
        // require 2 brushes selected: first = target, second = cutter (multi or sequential)
        List<(int ei,int bi)> targets=new();
        if(MultiSelectedBrushes.Count>=1 && SelectedBrush!=null){
            // treat multi + primary as cutters, primary target is Selected
            // collect cutters = multi minus primary
            var cutters=new List<(int,int)>(MultiSelectedBrushes);
            cutters.Remove((SelectedIndex, SelectedBrushIndex));
            if(cutters.Count==0) return false;
            // use first cutter
            var cut=cutters[0];
            var targetBrush=SelectedBrush;
            var cutterBrush=Entities[cut.Item1].Brushes[cut.Item2];
            var res=BrushManipulation.Subtract(targetBrush, cutterBrush);
            if(res.Count==0) return false;
            PushUndo("csg subtract");
            // replace target with fragments, keep cutter? remove cutter? In TrenchBroom subtract removes cutter and replaces target with fragments
            var ent=Entities[SelectedIndex];
            // remove target
            ent.Brushes.RemoveAt(SelectedBrushIndex);
            foreach(var nb in res) ent.Brushes.Add(nb);
            Dirty=true; Changed?.Invoke(); return true;
        }
        if(MultiSelectedBrushes.Count==2){
            var a=MultiSelectedBrushes.ElementAt(0); var b=MultiSelectedBrushes.ElementAt(1);
            var ba=Entities[a.Item1].Brushes[a.Item2]; var bb=Entities[b.Item1].Brushes[b.Item2];
            var res=BrushManipulation.Subtract(ba, bb);
            if(res.Count==0) return false;
            PushUndo("csg subtract");
            Entities[a.Item1].Brushes.RemoveAt(a.Item2);
            foreach(var nb in res) Entities[a.Item1].Brushes.Add(nb);
            Dirty=true; Changed?.Invoke(); return true;
        }
        return false;
    }

    public bool CsgIntersect()
    {
        if(MultiSelectedBrushes.Count>=2){
            var a=MultiSelectedBrushes.ElementAt(0); var b=MultiSelectedBrushes.ElementAt(1);
            var ba=Entities[a.Item1].Brushes[a.Item2]; var bb=Entities[b.Item1].Brushes[b.Item2];
            var inter=BrushManipulation.Intersect(ba, bb);
            if(inter==null) return false;
            PushUndo("csg intersect");
            Entities[a.Item1].Brushes[a.Item2]=inter;
            Dirty=true; Changed?.Invoke(); return true;
        }
        if(MultiSelectedBrushes.Count==1 && SelectedBrush!=null && MultiSelectedBrushes.Contains((SelectedIndex,SelectedBrushIndex))){
            // find other brush in same entity? no
            return false;
        }
        return false;
    }

    public bool CsgUnion()
    {
        // Union just groups? For now duplicate cutter into target entity
        if(MultiSelectedBrushes.Count>=2){
            var a=MultiSelectedBrushes.ElementAt(0); var b=MultiSelectedBrushes.ElementAt(1);
            var bb2=Entities[b.Item1].Brushes[b.Item2];
            // clone bb into a's entity
            PushUndo("csg union");
            var clone=new MapBrush();
            foreach(var f in bb2.Faces){ var nf=new MapFace{P1=f.P1,P2=f.P2,P3=f.P3,Texture=f.Texture,IsValve=f.IsValve,UAxis=f.UAxis,VAxis=f.VAxis,UOffset=f.UOffset,VOffset=f.VOffset,Rotation=f.Rotation,ScaleX=f.ScaleX,ScaleY=f.ScaleY}; nf.ComputePlane(); clone.Faces.Add(nf); }
            Entities[a.Item1].Brushes.Add(clone);
            Dirty=true; Changed?.Invoke(); return true;
        }
        return false;
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

    // ---------- Copy / Paste Clipboard (Ctrl+C/V/X) ----------

    sealed class ClipboardData
    {
        public List<MapEntity> Entities = new();
        public Vector3 Center;
    }
    static ClipboardData? _clipboard;

    public bool CanPaste => _clipboard != null && _clipboard.Entities.Count > 0;

    public bool CopySelected()
    {
        List<MapEntity> src = new();
        if (MultiSelectedBrushes.Count > 0)
        {
            var groups = MultiSelectedBrushes.GroupBy(k => k.ei);
            foreach (var g in groups)
            {
                var ent = Entities[g.Key];
                foreach (var bi in g.Select(x => x.bi))
                {
                    if (bi < 0 || bi >= ent.Brushes.Count) continue;
                    var ne = new MapEntity();
                    ne.Properties["classname"] = "worldspawn";
                    // clone single brush
                    var b = ent.Brushes[bi];
                    var nb = new MapBrush();
                    foreach (var f in b.Faces)
                    {
                        var nf = new MapFace { P1=f.P1, P2=f.P2, P3=f.P3, Texture=f.Texture, IsValve=f.IsValve, UAxis=f.UAxis, VAxis=f.VAxis, UOffset=f.UOffset, VOffset=f.VOffset, Rotation=f.Rotation, ScaleX=f.ScaleX, ScaleY=f.ScaleY };
                        nf.ComputePlane(); nb.Faces.Add(nf);
                    }
                    ne.Brushes.Add(nb);
                    src.Add(ne);
                }
            }
        }
        else if (MultiSelectedEntities.Count > 0)
        {
            foreach (var ei in MultiSelectedEntities)
            {
                if (ei < 0 || ei >= Entities.Count) continue;
                src.Add(CloneEntity(Entities[ei]));
            }
            if (Selected != null && !MultiSelectedEntities.Contains(SelectedIndex)) src.Add(CloneEntity(Selected));
        }
        else if (Selected != null)
        {
            if (Mode == EditMode.Brush && SelectedBrush != null)
            {
                var ne = new MapEntity(); ne.Properties["classname"] = "worldspawn";
                var nb = new MapBrush();
                foreach (var f in SelectedBrush.Faces) { var nf = new MapFace{ P1=f.P1, P2=f.P2, P3=f.P3, Texture=f.Texture, IsValve=f.IsValve, UAxis=f.UAxis, VAxis=f.VAxis, UOffset=f.UOffset, VOffset=f.VOffset, Rotation=f.Rotation, ScaleX=f.ScaleX, ScaleY=f.ScaleY }; nf.ComputePlane(); nb.Faces.Add(nf); }
                ne.Brushes.Add(nb); src.Add(ne);
            }
            else src.Add(CloneEntity(Selected));
        }
        if (src.Count == 0) return false;
        var center = Vector3.Zero; int cnt=0;
        foreach (var e in src)
        {
            if (e.Brushes.Count>0) { foreach(var b in e.Brushes) { BrushManipulation.GetBounds(b, out var mn, out var mx); center += (mn+mx)*0.5f; cnt++; } }
            else if (e.Properties.TryGetValue("origin", out var o)) { center += ParseVec3(o); cnt++; }
        }
        if (cnt>0) center/=cnt;
        _clipboard = new ClipboardData{ Entities = src, Center = center };
        return true;
    }

    static MapEntity CloneEntity(MapEntity src)
    {
        var c = new MapEntity();
        foreach (var kv in src.Properties) c.Properties[kv.Key]=kv.Value;
        foreach (var b in src.Brushes)
        {
            var nb = new MapBrush();
            foreach (var f in b.Faces) { var nf = new MapFace{ P1=f.P1, P2=f.P2, P3=f.P3, Texture=f.Texture, IsValve=f.IsValve, UAxis=f.UAxis, VAxis=f.VAxis, UOffset=f.UOffset, VOffset=f.VOffset, Rotation=f.Rotation, ScaleX=f.ScaleX, ScaleY=f.ScaleY }; nf.ComputePlane(); nb.Faces.Add(nf); }
            c.Brushes.Add(nb);
        }
        return c;
    }

    public bool CutSelected()
    {
        if (!CopySelected()) return false;
        PushUndo("cut");
        if (MultiSelectedBrushes.Count>0)
        {
            var byEnt = MultiSelectedBrushes.GroupBy(k=>k.ei).ToDictionary(g=>g.Key,g=>g.Select(x=>x.bi).OrderByDescending(x=>x).ToList());
            foreach(var kv in byEnt){ var ent=Entities[kv.Key]; foreach(var bi in kv.Value) if(bi>=0&&bi<ent.Brushes.Count) ent.Brushes.RemoveAt(bi); }
            ClearMultiBrush(); SelectedBrushIndex=-1; SelectedFaceIndex=-1;
        }
        else if (MultiSelectedEntities.Count>0)
        {
            var sorted = MultiSelectedEntities.OrderByDescending(x=>x).ToList();
            foreach(var ei in sorted) if(ei>=0&&ei<Entities.Count) Entities.RemoveAt(ei);
            ClearMultiEntity(); if(SelectedIndex>=Entities.Count) SelectedIndex=Entities.Count-1;
        }
        else if (Selected != null)
        {
            if (Mode==EditMode.Brush && SelectedBrush!=null) RemoveBrushAt(SelectedBrushIndex);
            else { Entities.RemoveAt(SelectedIndex); if(SelectedIndex>=Entities.Count) SelectedIndex=Entities.Count-1; }
        }
        Dirty=true; Changed?.Invoke(); return true;
    }

    public bool PasteAt(Vector3? quakeOrigin = null)
    {
        if (_clipboard==null || _clipboard.Entities.Count==0) return false;
        PushUndo("paste");
        Vector3 offset;
        if (quakeOrigin.HasValue) offset = quakeOrigin.Value - _clipboard.Center;
        else offset = new Vector3(32,32,0); // default nudge
        if (GridSnapEnabled && GridSize>0) offset = BrushManipulation.Snap(offset, GridSize);
        var newIndices = new List<int>();
        foreach(var src in _clipboard.Entities)
        {
            var ne = CloneEntity(src);
            if (ne.Brushes.Count>0) foreach(var b in ne.Brushes) BrushManipulation.Translate(b, offset);
            if (ne.Properties.TryGetValue("origin", out var o)) { var p=ParseVec3(o)+offset; ne.Properties["origin"]=$"{F(p.X)} {F(p.Y)} {F(p.Z)}"; }
            Entities.Add(ne); newIndices.Add(Entities.Count-1);
        }
        // select pasted
        ClearAllMulti();
        if (newIndices.Count==1) SelectedIndex=newIndices[0];
        else { foreach(var ni in newIndices) MultiSelectedEntities.Add(ni); SelectedIndex=newIndices.Last(); }
        Dirty=true; Changed?.Invoke(); return true;
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
