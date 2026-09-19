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
    public EditMode Mode { get; set; } = EditMode.Object;
    public float GridSize { get; set; } = 8f;
    public bool GridSnapEnabled { get; set; } = true;
    public bool Dirty { get; private set; }

    // undo/redo
    readonly Stack<Snapshot> _undo = new();
    readonly Stack<Snapshot> _redo = new();

    sealed class Snapshot
    {
        public List<MapEntity> Entities = null!;
        public int Selected;
        public int SelBrush;
        public int SelFace;
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
        _undo.Push(new Snapshot { Entities = DeepClone(Entities), Selected = SelectedIndex, SelBrush = SelectedBrushIndex, SelFace = SelectedFaceIndex, Mode = Mode, Path = FilePath });
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
        _redo.Push(new Snapshot { Entities = DeepClone(Entities), Selected = SelectedIndex, SelBrush = SelectedBrushIndex, SelFace = SelectedFaceIndex, Mode = Mode, Path = FilePath });
        var s = _undo.Pop();
        Entities = s.Entities;
        SelectedIndex = s.Selected;
        SelectedBrushIndex = s.SelBrush;
        SelectedFaceIndex = s.SelFace;
        Mode = s.Mode;
        FilePath = s.Path;
        Dirty = true;
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (!CanRedo) return;
        _undo.Push(new Snapshot { Entities = DeepClone(Entities), Selected = SelectedIndex, SelBrush = SelectedBrushIndex, SelFace = SelectedFaceIndex, Mode = Mode, Path = FilePath });
        var s = _redo.Pop();
        Entities = s.Entities;
        SelectedIndex = s.Selected;
        SelectedBrushIndex = s.SelBrush;
        SelectedFaceIndex = s.SelFace;
        Mode = s.Mode;
        FilePath = s.Path;
        Dirty = true;
        Changed?.Invoke();
    }

    public void Select(int index)
    {
        if (Entities.Count == 0) { SelectedIndex = -1; SelectedBrushIndex = -1; SelectedFaceIndex = -1; return; }
        SelectedIndex = Math.Clamp(index, 0, Entities.Count - 1);
        SelectedBrushIndex = -1;
        SelectedFaceIndex = -1;
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
        if (e == null || e.Brushes.Count == 0) { SelectedBrushIndex = -1; SelectedFaceIndex = -1; return; }
        if (SelectedBrushIndex < 0 || SelectedBrushIndex >= e.Brushes.Count) { SelectedBrushIndex = -1; SelectedFaceIndex = -1; return; }
        var b = e.Brushes[SelectedBrushIndex];
        if (SelectedFaceIndex < -1 || SelectedFaceIndex >= b.Faces.Count) SelectedFaceIndex = -1;
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
        if (Selected == null) return;
        PushUndo("delete");
        Entities.RemoveAt(SelectedIndex);
        if (SelectedIndex >= Entities.Count) SelectedIndex = Entities.Count - 1;
        Dirty = true;
        Changed?.Invoke();
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

    public void AddBoxBrushAt(Vector3 quakeCenter, Vector3 quakeHalfExtents, string texture = "wall")
    {
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
        var brush = new MapBrush();
        var min = quakeCenter - quakeHalfExtents;
        var max = quakeCenter + quakeHalfExtents;
        AddFace(new Vector3(min.X, min.Y, min.Z), new Vector3(min.X, max.Y, min.Z), new Vector3(min.X, min.Y, max.Z));
        AddFace(new Vector3(max.X, min.Y, min.Z), new Vector3(max.X, min.Y, max.Z), new Vector3(max.X, max.Y, min.Z));
        AddFace(new Vector3(min.X, min.Y, min.Z), new Vector3(min.X, min.Y, max.Z), new Vector3(max.X, min.Y, min.Z));
        AddFace(new Vector3(min.X, max.Y, min.Z), new Vector3(max.X, max.Y, min.Z), new Vector3(min.X, max.Y, max.Z));
        AddFace(new Vector3(min.X, min.Y, min.Z), new Vector3(max.X, min.Y, min.Z), new Vector3(min.X, max.Y, min.Z));
        AddFace(new Vector3(min.X, min.Y, max.Z), new Vector3(min.X, max.Y, max.Z), new Vector3(max.X, min.Y, max.Z));
        e!.Brushes.Add(brush);
        Dirty = true;
        Changed?.Invoke();

        void AddFace(Vector3 p1, Vector3 p2, Vector3 p3)
        {
            var f = new MapFace { P1 = p1, P2 = p2, P3 = p3, Texture = texture, ScaleX = 1, ScaleY = 1 };
            f.ComputePlane();
            brush.Faces.Add(f);
        }
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
        var e = Selected;
        if (e == null || SelectedBrushIndex < 0 || SelectedBrushIndex >= e.Brushes.Count) return false;
        if (deltaQuake.LengthSquared() < 1e-6f) return false;
        if (pushUndo) PushUndo("move brush");
        if (GridSnapEnabled && GridSize > 0) deltaQuake = BrushManipulation.Snap(deltaQuake, GridSize);
        BrushManipulation.Translate(e.Brushes[SelectedBrushIndex], deltaQuake);
        Dirty = true;
        Changed?.Invoke();
        return true;
    }

    public bool TranslateSelectedEntity(Vector3 deltaQuake, bool pushUndo = true)
    {
        var e = Selected;
        if (e == null) return false;
        if (deltaQuake.LengthSquared() < 1e-6f) return false;
        if (pushUndo) PushUndo("move entity");
        if (GridSnapEnabled && GridSize > 0) deltaQuake = BrushManipulation.Snap(deltaQuake, GridSize);
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
        var e = Selected;
        if (e == null) return Vector3.Zero;
        if (SelectedBrush != null) return BrushManipulation.GetCenter(SelectedBrush);
        if (e.Brushes.Count > 0)
        {
            Vector3 s = Vector3.Zero; int cnt = 0;
            foreach (var b in e.Brushes) { s += BrushManipulation.GetCenter(b); cnt++; }
            return cnt > 0 ? s / cnt : Vector3.Zero;
        }
        if (e.Properties.TryGetValue("origin", out var o)) return ParseVec3(o);
        return Vector3.Zero;
    }

    public void GetSelectedBounds(out Vector3 min, out Vector3 max)
    {
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
        if (e.Properties.TryGetValue("origin", out var o))
        {
            var p = ParseVec3(o); min = p - new Vector3(8); max = p + new Vector3(8);
            return;
        }
        min = max = Vector3.Zero;
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
