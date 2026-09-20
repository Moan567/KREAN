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
    // BVH
    struct BvhNode{ public Vector3 Min, Max; public int Left, Right; public int Start, Count; public bool IsLeaf; }
    readonly List<BvhNode> _bvh = new();
    readonly List<int> _bvhIndices = new();
    int _bvhRoot = -1;
    const int BvhLeafSize = 4;

    public int BrushCount => _brushes.Count;

    public void Clear(){ _brushes.Clear(); _bvh.Clear(); _bvhIndices.Clear(); _bvhRoot=-1; }

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
        BuildBvh();
    }
    void BuildBvh()
    {
        _bvh.Clear(); _bvhIndices.Clear(); _bvhRoot=-1;
        if(_brushes.Count==0) return;
        _bvhIndices.AddRange(Enumerable.Range(0,_brushes.Count));
        _bvhRoot = BuildNode(0, _brushes.Count);
    }
    int BuildNode(int start, int count)
    {
        // compute bounds
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        for(int i=start;i<start+count;i++){ var b=_brushes[_bvhIndices[i]]; mn=Vector3.Min(mn,b.Min); mx=Vector3.Max(mx,b.Max); }
        int nodeIdx=_bvh.Count;
        _bvh.Add(new BvhNode{Min=mn,Max=mx,Start=start,Count=count,IsLeaf=false,Left=-1,Right=-1});
        if(count<=BvhLeafSize)
        {
            var leaf=_bvh[nodeIdx]; leaf.IsLeaf=true; _bvh[nodeIdx]=leaf; return nodeIdx;
        }
        // longest axis
        var extent=mx-mn;
        int axis = extent.X>extent.Y ? (extent.X>extent.Z?0:2) : (extent.Y>extent.Z?1:2);
        // sort by center on axis
        var slice=_bvhIndices.GetRange(start,count);
        slice.Sort((a,b)=>{
            float ca= axis==0? (_brushes[a].Min.X+_brushes[a].Max.X)*0.5f : axis==1? (_brushes[a].Min.Y+_brushes[a].Max.Y)*0.5f : (_brushes[a].Min.Z+_brushes[a].Max.Z)*0.5f;
            float cb= axis==0? (_brushes[b].Min.X+_brushes[b].Max.X)*0.5f : axis==1? (_brushes[b].Min.Y+_brushes[b].Max.Y)*0.5f : (_brushes[b].Min.Z+_brushes[b].Max.Z)*0.5f;
            return ca.CompareTo(cb);
        });
        for(int i=0;i<count;i++) _bvhIndices[start+i]=slice[i];
        int mid=count/2;
        int left=BuildNode(start,mid);
        int right=BuildNode(start+mid,count-mid);
        var n=_bvh[nodeIdx]; n.Left=left; n.Right=right; n.IsLeaf=false; _bvh[nodeIdx]=n;
        return nodeIdx;
    }

    public TraceResult Trace(Vector3 start, Vector3 end, Vector3 halfExtents)
    {
        var result = new TraceResult { Fraction = 1f, EndPos = end };
        var sweepMin = Vector3.Min(start, end) - halfExtents - new Vector3(Epsilon);
        var sweepMax = Vector3.Max(start, end) + halfExtents + new Vector3(Epsilon);
        if(_bvhRoot>=0 && _bvh.Count>0)
            TraceBvh(_bvhRoot, sweepMin, sweepMax, start, end, halfExtents, ref result);
        else
        {
            foreach (var brush in _brushes)
            {
                if (brush.Max.X < sweepMin.X || brush.Min.X > sweepMax.X ||
                    brush.Max.Y < sweepMin.Y || brush.Min.Y > sweepMax.Y ||
                    brush.Max.Z < sweepMin.Z || brush.Min.Z > sweepMax.Z)
                    continue;
                TraceBrush(brush, start, end, halfExtents, ref result);
                if (result.AllSolid) break;
            }
        }
        result.EndPos = start + (end - start) * result.Fraction;
        return result;
    }
    void TraceBvh(int nodeIdx, Vector3 sweepMin, Vector3 sweepMax, Vector3 start, Vector3 end, Vector3 half, ref TraceResult result)
    {
        if(result.AllSolid) return;
        var node=_bvh[nodeIdx];
        if(node.Max.X < sweepMin.X || node.Min.X > sweepMax.X ||
           node.Max.Y < sweepMin.Y || node.Min.Y > sweepMax.Y ||
           node.Max.Z < sweepMin.Z || node.Min.Z > sweepMax.Z) return;
        if(node.IsLeaf)
        {
            for(int i=node.Start;i<node.Start+node.Count;i++)
            {
                var brush=_brushes[_bvhIndices[i]];
                // already broadphase via node, but still check individual
                if (brush.Max.X < sweepMin.X || brush.Min.X > sweepMax.X ||
                    brush.Max.Y < sweepMin.Y || brush.Min.Y > sweepMax.Y ||
                    brush.Max.Z < sweepMin.Z || brush.Min.Z > sweepMax.Z) continue;
                TraceBrush(brush, start, end, half, ref result);
                if(result.AllSolid) return;
            }
        }
        else
        {
            if(node.Left>=0) TraceBvh(node.Left, sweepMin, sweepMax, start, end, half, ref result);
            if(node.Right>=0) TraceBvh(node.Right, sweepMin, sweepMax, start, end, half, ref result);
        }
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
