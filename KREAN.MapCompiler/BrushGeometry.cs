using System.Numerics;

namespace KREAN.MapCompiler;

public sealed class BrushPolygon
{
    public MapFace Face { get; }
    public List<Vector3> Vertices { get; } = new();
    public BrushPolygon(MapFace face) => Face = face;
}

/// <summary>Turns a brush (a set of planes) into actual polygons by intersecting plane triples.</summary>
public static class BrushGeometry
{
    const float InsideEpsilon = 0.05f;   // Quake units
    const float MergeDistSq = 0.01f;

    public static List<BrushPolygon> Build(MapBrush brush, out Vector3 min, out Vector3 max)
    {
        var faces = brush.Faces;
        var polys = faces.Select(f => new BrushPolygon(f)).ToList();

        for (int i = 0; i < faces.Count; i++)
        for (int j = i + 1; j < faces.Count; j++)
        for (int k = j + 1; k < faces.Count; k++)
        {
            if (!TryIntersect(faces[i], faces[j], faces[k], out var p)) continue;
            if (!IsInside(faces, p)) continue;

            AddUnique(polys[i].Vertices, p);
            AddUnique(polys[j].Vertices, p);
            AddUnique(polys[k].Vertices, p);
        }

        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);

        foreach (var poly in polys)
        {
            if (poly.Vertices.Count < 3) continue;
            SortWinding(poly);
            foreach (var v in poly.Vertices)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
        }

        if (min.X > max.X)   // no real geometry (degenerate brush)
        {
            min = max = Vector3.Zero;
            return new List<BrushPolygon>();
        }
        return polys;
    }

    static bool TryIntersect(MapFace a, MapFace b, MapFace c, out Vector3 point)
    {
        var bc = Vector3.Cross(b.Normal, c.Normal);
        float denom = Vector3.Dot(a.Normal, bc);
        if (MathF.Abs(denom) < 1e-5f) { point = default; return false; }

        point = (a.Distance * bc
               + b.Distance * Vector3.Cross(c.Normal, a.Normal)
               + c.Distance * Vector3.Cross(a.Normal, b.Normal)) / denom;
        return true;
    }

    static bool IsInside(List<MapFace> faces, Vector3 p)
    {
        foreach (var f in faces)
            if (Vector3.Dot(f.Normal, p) - f.Distance > InsideEpsilon) return false;
        return true;
    }

    static void AddUnique(List<Vector3> list, Vector3 p)
    {
        foreach (var v in list)
            if (Vector3.DistanceSquared(v, p) < MergeDistSq) return;
        list.Add(p);
    }

    /// <summary>Orders vertices counter-clockwise when viewed from outside the brush.</summary>
    static void SortWinding(BrushPolygon poly)
    {
        var verts = poly.Vertices;
        var n = poly.Face.Normal;

        var center = Vector3.Zero;
        foreach (var v in verts) center += v;
        center /= verts.Count;

        var u = Vector3.Normalize(verts[0] - center);
        var w = Vector3.Cross(n, u);

        verts.Sort((a, b) =>
        {
            float aa = MathF.Atan2(Vector3.Dot(a - center, w), Vector3.Dot(a - center, u));
            float ab = MathF.Atan2(Vector3.Dot(b - center, w), Vector3.Dot(b - center, u));
            return aa.CompareTo(ab);
        });
    }
}

/// <summary>Texture coordinate math (results are in texels – divide by texture size once textures exist).</summary>
public static class TextureMath
{
    // Quake's base axis table: normal, u axis, v axis (used for the Standard format).
    static readonly Vector3[] Axes =
    {
        new(0, 0, 1),  new(1, 0, 0), new(0, -1, 0),   // floor
        new(0, 0, -1), new(1, 0, 0), new(0, -1, 0),   // ceiling
        new(1, 0, 0),  new(0, 1, 0), new(0, 0, -1),   // west wall
        new(-1, 0, 0), new(0, 1, 0), new(0, 0, -1),   // east wall
        new(0, 1, 0),  new(1, 0, 0), new(0, 0, -1),   // south wall
        new(0, -1, 0), new(1, 0, 0), new(0, 0, -1),   // north wall
    };

    public static Vector2 ComputeUV(MapFace f, Vector3 p)
    {
        float sx = f.ScaleX == 0 ? 1f : f.ScaleX;
        float sy = f.ScaleY == 0 ? 1f : f.ScaleY;

        if (f.IsValve)
            return new Vector2(Vector3.Dot(p, f.UAxis) / sx + f.UOffset,
                               Vector3.Dot(p, f.VAxis) / sy + f.VOffset);

        int best = 0;
        float bestDot = float.MinValue;
        for (int i = 0; i < 6; i++)
        {
            float d = Vector3.Dot(f.Normal, Axes[i * 3]);
            if (d > bestDot) { bestDot = d; best = i; }
        }

        float ru = Vector3.Dot(p, Axes[best * 3 + 1]);
        float rv = Vector3.Dot(p, Axes[best * 3 + 2]);

        float rad = f.Rotation * MathF.PI / 180f;
        float c = MathF.Cos(rad), s = MathF.Sin(rad);

        float u = ru * c - rv * s;
        float v = ru * s + rv * c;
        return new Vector2(u / sx + f.UOffset, v / sy + f.VOffset);
    }
}
