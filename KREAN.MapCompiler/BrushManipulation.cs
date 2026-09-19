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
}
