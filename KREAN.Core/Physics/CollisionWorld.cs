using System.Numerics;
using KREAN.Core.Scenes;

namespace KREAN.Core.Physics;

public struct TraceResult
{
    public float Fraction;     // 0..1 how far the box travelled before hitting something
    public Vector3 Normal;     // surface normal at the hit
    public Vector3 EndPos;
    public bool StartSolid;
    public bool AllSolid;
}

/// <summary>
/// Quake-style collision: convex brushes made of planes, swept-AABB traces.
/// Also use it for hitscan weapons (trace with halfExtents = Vector3.Zero).
/// </summary>
public sealed class CollisionWorld
{
    struct Brush
    {
        public Vector4[] Planes;
        public Vector3 Min, Max;
    }

    public const float Epsilon = 0.125f * Units.QuakeToMeters;

    readonly List<Brush> _brushes = new();

    public int BrushCount => _brushes.Count;

    public void Clear() => _brushes.Clear();

    public void Load(IEnumerable<CollisionBrushData> data)
    {
        Clear();
        foreach (var d in data)
        {
            int n = d.Planes.Length / 4;
            var planes = new Vector4[n];
            for (int i = 0; i < n; i++)
                planes[i] = new Vector4(d.Planes[i * 4], d.Planes[i * 4 + 1], d.Planes[i * 4 + 2], d.Planes[i * 4 + 3]);
            _brushes.Add(new Brush { Planes = planes, Min = d.Min, Max = d.Max });
        }
    }

    public TraceResult Trace(Vector3 start, Vector3 end, Vector3 halfExtents)
    {
        var result = new TraceResult { Fraction = 1f, EndPos = end };

        var sweepMin = Vector3.Min(start, end) - halfExtents - new Vector3(Epsilon);
        var sweepMax = Vector3.Max(start, end) + halfExtents + new Vector3(Epsilon);

        foreach (var brush in _brushes)
        {
            if (brush.Max.X < sweepMin.X || brush.Min.X > sweepMax.X ||
                brush.Max.Y < sweepMin.Y || brush.Min.Y > sweepMax.Y ||
                brush.Max.Z < sweepMin.Z || brush.Min.Z > sweepMax.Z)
                continue;

            TraceBrush(brush, start, end, halfExtents, ref result);
            if (result.AllSolid) break;
        }

        result.EndPos = start + (end - start) * result.Fraction;
        return result;
    }

    static void TraceBrush(in Brush brush, Vector3 start, Vector3 end, Vector3 half, ref TraceResult result)
    {
        float enter = -1f, leave = 1f;
        bool startOut = false, endOut = false;
        Vector3 hitNormal = Vector3.Zero;

        foreach (var pl in brush.Planes)
        {
            var n = new Vector3(pl.X, pl.Y, pl.Z);

            // Push the plane out by the box's extent along the normal (Minkowski sum).
            float offset = MathF.Abs(n.X) * half.X + MathF.Abs(n.Y) * half.Y + MathF.Abs(n.Z) * half.Z;
            float dist = pl.W + offset;

            float d1 = Vector3.Dot(n, start) - dist;
            float d2 = Vector3.Dot(n, end) - dist;

            if (d1 > 0) startOut = true;
            if (d2 > 0) endOut = true;

            if (d1 > 0 && (d2 >= Epsilon || d2 >= d1)) return;   // completely in front of this plane
            if (d1 <= 0 && d2 <= 0) continue;                    // completely behind it

            if (d1 > d2)
            {
                float f = MathF.Max(0f, (d1 - Epsilon) / (d1 - d2));
                if (f > enter) { enter = f; hitNormal = n; }
            }
            else
            {
                float f = MathF.Min(1f, (d1 + Epsilon) / (d1 - d2));
                if (f < leave) leave = f;
            }
        }

        if (!startOut)
        {
            result.StartSolid = true;
            if (!endOut)
            {
                result.AllSolid = true;
                result.Fraction = 0f;
            }
            return;
        }

        if (enter < leave && enter > -1f && enter < result.Fraction)
        {
            result.Fraction = MathF.Max(0f, enter);
            result.Normal = hitNormal;
        }
    }
}
