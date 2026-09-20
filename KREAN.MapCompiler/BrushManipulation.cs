using System.Numerics;

namespace KREAN.MapCompiler;

/// <summary>TrenchBroom-like brush manipulation utilities. All coords are Quake space (Z-up).</summary>
public static class BrushManipulation
{
    public static Vector3 GetCenter(MapBrush brush)
    {
        var polys = BrushGeometry.Build(brush, out var min, out var max);
        if (polys.Count == 0)
        {
            // fallback: average of plane points
            var s = Vector3.Zero;
            int n = 0;
            foreach (var f in brush.Faces) { s += f.P1 + f.P2 + f.P3; n += 3; }
            return n > 0 ? s / n : Vector3.Zero;
        }
        return (min + max) * 0.5f;
    }

    public static void GetBounds(MapBrush brush, out Vector3 min, out Vector3 max)
    {
        BrushGeometry.Build(brush, out min, out max);
    }

    public static void Translate(MapBrush brush, Vector3 delta)
    {
        foreach (var f in brush.Faces)
        {
            f.P1 += delta;
            f.P2 += delta;
            f.P3 += delta;
            f.ComputePlane();
        }
    }

    public static void TranslateEntity(MapEntity entity, Vector3 delta)
    {
        foreach (var b in entity.Brushes) Translate(b, delta);
        if (entity.Properties.TryGetValue("origin", out var o))
        {
            var v = ParseVec3(o);
            v += delta;
            entity.Properties["origin"] = $"{Fmt(v.X)} {Fmt(v.Y)} {Fmt(v.Z)}";
        }
    }

    /// <summary>Move a single face along its normal by distance (Quake units). Positive pushes outward.</summary>
    public static bool MoveFace(MapBrush brush, int faceIndex, float distance)
    {
        if (faceIndex < 0 || faceIndex >= brush.Faces.Count) return false;
        var f = brush.Faces[faceIndex];
        var delta = f.Normal * distance;
        f.P1 += delta; f.P2 += delta; f.P3 += delta;
        f.ComputePlane();
        if (!IsBrushValid(brush))
        {
            // revert
            f.P1 -= delta; f.P2 -= delta; f.P3 -= delta;
            f.ComputePlane();
            return false;
        }
        return true;
    }

    /// <summary>Resize brush to exact AABB (axis-aligned). Rebuilds 6 faces.</summary>
    public static void ResizeToBounds(MapBrush brush, Vector3 newMin, Vector3 newMax, string defaultTexture = "wall")
    {
        if (brush.Faces.Count == 0) return;
        // keep textures per axis if possible
        string texXMin = FindFaceForNormal(brush, new Vector3(-1, 0, 0))?.Texture ?? defaultTexture;
        string texXMax = FindFaceForNormal(brush, new Vector3(1, 0, 0))?.Texture ?? defaultTexture;
        string texYMin = FindFaceForNormal(brush, new Vector3(0, -1, 0))?.Texture ?? defaultTexture;
        string texYMax = FindFaceForNormal(brush, new Vector3(0, 1, 0))?.Texture ?? defaultTexture;
        string texZMin = FindFaceForNormal(brush, new Vector3(0, 0, -1))?.Texture ?? defaultTexture;
        string texZMax = FindFaceForNormal(brush, new Vector3(0, 0, 1))?.Texture ?? defaultTexture;

        brush.Faces.Clear();
        void Add(Vector3 p1, Vector3 p2, Vector3 p3, string tex)
        {
            var f = new MapFace { P1 = p1, P2 = p2, P3 = p3, Texture = tex, ScaleX = 1, ScaleY = 1 };
            f.ComputePlane(); brush.Faces.Add(f);
        }
        var min = Vector3.Min(newMin, newMax);
        var max = Vector3.Max(newMin, newMax);
        Add(new Vector3(min.X, min.Y, min.Z), new Vector3(min.X, max.Y, min.Z), new Vector3(min.X, min.Y, max.Z), texXMin);
        Add(new Vector3(max.X, min.Y, min.Z), new Vector3(max.X, min.Y, max.Z), new Vector3(max.X, max.Y, min.Z), texXMax);
        Add(new Vector3(min.X, min.Y, min.Z), new Vector3(min.X, min.Y, max.Z), new Vector3(max.X, min.Y, min.Z), texYMin);
        Add(new Vector3(min.X, max.Y, min.Z), new Vector3(max.X, max.Y, min.Z), new Vector3(min.X, max.Y, max.Z), texYMax);
        Add(new Vector3(min.X, min.Y, min.Z), new Vector3(max.X, min.Y, min.Z), new Vector3(min.X, max.Y, min.Z), texZMin);
        Add(new Vector3(min.X, min.Y, max.Z), new Vector3(min.X, max.Y, max.Z), new Vector3(max.X, min.Y, max.Z), texZMax);
    }

    public static void ScaleAroundCenter(MapBrush brush, Vector3 centerQuake, Vector3 scale)
    {
        if (scale.X==0||scale.Y==0||scale.Z==0) return;
        foreach(var f in brush.Faces)
        {
            f.P1 = centerQuake + (f.P1 - centerQuake) * scale;
            f.P2 = centerQuake + (f.P2 - centerQuake) * scale;
            f.P3 = centerQuake + (f.P3 - centerQuake) * scale;
            f.ComputePlane();
        }
    }

    public static void RotateAroundCenter(MapBrush brush, Vector3 centerQuake, Quaternion rot)
    {
        foreach(var f in brush.Faces)
        {
            f.P1 = Vector3.Transform(f.P1 - centerQuake, rot) + centerQuake;
            f.P2 = Vector3.Transform(f.P2 - centerQuake, rot) + centerQuake;
            f.P3 = Vector3.Transform(f.P3 - centerQuake, rot) + centerQuake;
            f.ComputePlane();
        }
    }

    public static Vector3 Snap(Vector3 v, float grid)
    {
        if (grid <= 0) return v;
        return new Vector3(SnapF(v.X, grid), SnapF(v.Y, grid), SnapF(v.Z, grid));
    }
    static float SnapF(float f, float g) => MathF.Round(f / g) * g;

    /// <summary>Ray vs convex brush (Quake space). Returns t if hit.</summary>
    public static bool RayIntersect(MapBrush brush, Vector3 origin, Vector3 dir, out float t, out Vector3 hitNormal, out int hitFace)
    {
        t = float.MaxValue; hitNormal = Vector3.Zero; hitFace = -1;
        float tEnter = float.NegativeInfinity;
        float tExit = float.PositiveInfinity;
        int enterFace = -1;

        for (int i = 0; i < brush.Faces.Count; i++)
        {
            var f = brush.Faces[i];
            if (!f.Valid) continue;
            float denom = Vector3.Dot(f.Normal, dir);
            float dist = Vector3.Dot(f.Normal, origin) - f.Distance;

            if (MathF.Abs(denom) < 1e-6f)
            {
                if (dist > 0) return false; // parallel and outside
                continue;
            }
            float th = -dist / denom;
            if (denom < 0) // entering
            {
                if (th > tEnter) { tEnter = th; enterFace = i; }
            }
            else // exiting
            {
                if (th < tExit) tExit = th;
            }
            if (tEnter > tExit) return false;
        }
        if (tEnter < 0) tEnter = tExit; // inside: exit is hit
        if (tEnter < 0 || tEnter > 1e6f) return false;
        if (float.IsInfinity(tEnter)) return false;
        t = tEnter;
        hitFace = enterFace;
        if (enterFace >= 0) hitNormal = brush.Faces[enterFace].Normal;
        else hitNormal = -dir;
        return true;
    }

    public static bool IsBrushValid(MapBrush brush)
    {
        var polys = BrushGeometry.Build(brush, out _, out _);
        return polys.Count > 0;
    }

    static MapFace? FindFaceForNormal(MapBrush brush, Vector3 n)
    {
        float best = -2f; MapFace? bestF = null;
        foreach (var f in brush.Faces)
        {
            float d = Vector3.Dot(f.Normal, n);
            if (d > best) { best = d; bestF = f; }
        }
        return bestF;
    }

    static Vector3 ParseVec3(string text)
    {
        var p = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        float F(int i) => i < p.Length && float.TryParse(p[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0f;
        return new Vector3(F(0), F(1), F(2));
    }
    static string Fmt(float f) => f.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    // ---------- Clip (TrenchBroom X) ----------

    static MapFace CreatePlaneFace(Vector3 normal, float dist, string texture)
    {
        normal = Vector3.Normalize(normal);
        Vector3 center = normal * dist;
        Vector3 helper = MathF.Abs(normal.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
        Vector3 t1 = Vector3.Normalize(Vector3.Cross(normal, helper));
        if (t1.LengthSquared() < 1e-6f)
        {
            helper = Vector3.UnitY;
            t1 = Vector3.Normalize(Vector3.Cross(normal, helper));
        }
        Vector3 t2 = Vector3.Normalize(Vector3.Cross(normal, t1));
        float s = 8192f;
        Vector3 p1 = center + t1 * s + t2 * s;
        Vector3 p2 = center - t1 * s + t2 * s;
        Vector3 p3 = center - t1 * s - t2 * s;
        var f = new MapFace { P1 = p1, P2 = p2, P3 = p3, Texture = string.IsNullOrEmpty(texture) ? "clip" : texture, ScaleX = 1, ScaleY = 1 };
        f.ComputePlane();
        if (Vector3.Dot(f.Normal, normal) < 0.9f)
        {
            (f.P2, f.P3) = (f.P3, f.P2);
            f.ComputePlane();
        }
        return f;
    }

    public static bool TryClip(MapBrush brush, Vector3 point, Vector3 normal, string clipTexture, out MapBrush? front, out MapBrush? back)
    {
        front = null; back = null;
        if (brush.Faces.Count < 4) return false;
        normal = Vector3.Normalize(normal);
        if (normal.LengthSquared() < 0.5f) return false;
        float dist = Vector3.Dot(normal, point);

        // quick classify using brush vertices
        var polys = BrushGeometry.Build(brush, out _, out _);
        if (polys.Count == 0) return false;
        float minD = float.MaxValue, maxD = float.MinValue;
        foreach (var poly in polys)
            foreach (var v in poly.Vertices)
            {
                float d = Vector3.Dot(normal, v) - dist;
                minD = MathF.Min(minD, d);
                maxD = MathF.Max(maxD, d);
            }
        const float eps = 0.5f;
        if (minD > eps || maxD < -eps) return false; // entirely on one side - no split
        if (maxD - minD < eps) return false;

        string tex = clipTexture;
        if (string.IsNullOrEmpty(tex)) tex = brush.Faces.FirstOrDefault()?.Texture ?? "clip";

        MapBrush Make(Vector3 n, float d)
        {
            var nb = new MapBrush();
            foreach (var f in brush.Faces)
            {
                var nf = new MapFace
                {
                    P1 = f.P1, P2 = f.P2, P3 = f.P3,
                    Texture = f.Texture, IsValve = f.IsValve, UAxis = f.UAxis, VAxis = f.VAxis,
                    UOffset = f.UOffset, VOffset = f.VOffset, Rotation = f.Rotation, ScaleX = f.ScaleX, ScaleY = f.ScaleY
                };
                nf.ComputePlane(); nb.Faces.Add(nf);
            }
            var clip = CreatePlaneFace(n, d, tex);
            nb.Faces.Add(clip);
            return nb;
        }

        var fBrush = Make(normal, dist);
        var bBrush = Make(-normal, -dist);

        bool fValid = IsBrushValid(fBrush);
        bool bValid = IsBrushValid(bBrush);
        if (!fValid && !bValid) return false;
        front = fValid ? fBrush : null;
        back = bValid ? bBrush : null;
        // at least one valid and we had intersection - success if at least one side valid and both originally straddled plane
        // if one side invalid due to coplanar collapse, treat as not splittable
        if (front == null || back == null) return false;
        return true;
    }

    public static bool ClipInPlace(MapBrush brush, Vector3 point, Vector3 normal, bool keepFront, string clipTexture = "")
    {
        if (!TryClip(brush, point, normal, clipTexture, out var front, out var back)) return false;
        var chosen = keepFront ? front! : back!;
        brush.Faces.Clear();
        foreach (var f in chosen.Faces) brush.Faces.Add(f);
        return true;
    }

    // ---------- CSG ----------
    public static List<MapBrush> Subtract(MapBrush a, MapBrush b)
    {
        // A - B : fragments of A outside B
        var fragments = new List<MapBrush>{ CloneBrush(a) };
        var outside = new List<MapBrush>();
        foreach(var plane in b.Faces)
        {
            var n = plane.Normal;
            float d = plane.Distance;
            // point on plane for TryClip
            Vector3 pt = n * d;
            var next = new List<MapBrush>();
            foreach(var frag in fragments)
            {
                if (TryClip(frag, pt, n, "", out var front, out var back))
                {
                    // front = inside (dot <= d), back = outside (dot >= d)
                    // outside part is fully outside B -> keep
                    outside.Add(back!);
                    next.Add(front!);
                }
                else
                {
                    // not straddling: classify
                    var c = GetCenter(frag);
                    float dist = Vector3.Dot(n, c) - d;
                    if (dist > 0.5f) outside.Add(frag); // outside -> keep as outside fragment (done)
                    else next.Add(frag); // inside -> continue to next plane
                }
            }
            fragments = next;
            if (fragments.Count==0) break;
        }
        // fragments left are inside B -> discard, outside are result
        // filter valid
        return outside.Where(IsBrushValid).ToList();
    }

    public static MapBrush? Intersect(MapBrush a, MapBrush b)
    {
        var cur = CloneBrush(a);
        foreach(var plane in b.Faces)
        {
            var n = plane.Normal; float d = plane.Distance; Vector3 pt = n*d;
            if (TryClip(cur, pt, n, "", out var front, out var back))
            {
                // keep inside (front)
                cur = front!;
            }
            else
            {
                var c = GetCenter(cur);
                float dist = Vector3.Dot(n, c)-d;
                if (dist > 0.5f) return null; // entirely outside -> empty intersection
                // inside -> keep
            }
            if (!IsBrushValid(cur)) return null;
        }
        // also clip by A's planes? already - intersection is A inside B. To be symmetric, if A inside B already handled.
        // Validate still inside all planes of both (cur already inside B and is subset of A)
        return cur;
    }

    static MapBrush CloneBrush(MapBrush src)
    {
        var nb = new MapBrush();
        foreach(var f in src.Faces)
        {
            var nf = new MapFace{ P1=f.P1, P2=f.P2, P3=f.P3, Texture=f.Texture, IsValve=f.IsValve, UAxis=f.UAxis, VAxis=f.VAxis, UOffset=f.UOffset, VOffset=f.VOffset, Rotation=f.Rotation, ScaleX=f.ScaleX, ScaleY=f.ScaleY };
            nf.ComputePlane(); nb.Faces.Add(nf);
        }
        return nb;
    }
}
