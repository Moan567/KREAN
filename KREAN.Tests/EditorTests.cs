using System.Numerics;
using KREAN.MapCompiler;
using KREAN.Core.Physics;
using KREAN.Core.Scenes;
using System.IO;

namespace KREAN.Tests;

public class MapParserTests
{
    [Fact]
    public void ParseSample()
    {
        var txt = SampleMap.Generate();
        var ents = MapParser.Parse(txt);
        Assert.True(ents.Count>0);
        Assert.Contains(ents, e=>e.ClassName=="worldspawn");
    }
    [Fact]
    public void ValveFormat()
    {
        string valve = "{\n\"classname\" \"worldspawn\"\n{\n( -64 -64 -16 ) ( -64 64 -16 ) ( 64 -64 -16 ) wall [ 1 0 0 0 ] [ 0 1 0 0 ] 0 1 1\n}\n}";
        var ents = MapParser.Parse(valve);
        Assert.Single(ents[0].Brushes);
        Assert.True(ents[0].Brushes[0].Faces[0].IsValve);
    }
}

public class BrushGeometryTests
{
    [Fact]
    public void BuildBox()
    {
        var br = new MapBrush();
        void Add(Vector3 a, Vector3 b, Vector3 c){ var f=new MapFace{P1=a,P2=b,P3=c,Texture="wall"}; f.ComputePlane(); br.Faces.Add(f); }
        var mn=new Vector3(-32,-32,-32); var mx=new Vector3(32,32,32);
        Add(new Vector3(mn.X,mn.Y,mn.Z), new Vector3(mn.X,mx.Y,mn.Z), new Vector3(mn.X,mn.Y,mx.Z));
        Add(new Vector3(mx.X,mn.Y,mn.Z), new Vector3(mx.X,mn.Y,mx.Z), new Vector3(mx.X,mx.Y,mn.Z));
        Add(new Vector3(mn.X,mn.Y,mn.Z), new Vector3(mn.X,mn.Y,mx.Z), new Vector3(mx.X,mn.Y,mn.Z));
        Add(new Vector3(mn.X,mx.Y,mn.Z), new Vector3(mx.X,mx.Y,mn.Z), new Vector3(mn.X,mx.Y,mx.Z));
        Add(new Vector3(mn.X,mn.Y,mn.Z), new Vector3(mx.X,mn.Y,mn.Z), new Vector3(mn.X,mx.Y,mn.Z));
        Add(new Vector3(mn.X,mn.Y,mx.Z), new Vector3(mn.X,mx.Y,mx.Z), new Vector3(mx.X,mn.Y,mx.Z));
        var polys=BrushGeometry.Build(br, out var bmin, out var bmax);
        Assert.True(polys.Count>=6);
        Assert.Equal(mn.X, bmin.X, 0.1);
    }
}

public class BrushManipulationTests
{
    [Fact]
    public void ClipSplits()
    {
        var br=new MapBrush();
        var mn=new Vector3(-32,-32,-32); var mx=new Vector3(32,32,32);
        void Add(Vector3 a, Vector3 b, Vector3 c){ var f=new MapFace{P1=a,P2=b,P3=c,Texture="wall"}; f.ComputePlane(); br.Faces.Add(f); }
        Add(new Vector3(mn.X,mn.Y,mn.Z), new Vector3(mn.X,mx.Y,mn.Z), new Vector3(mn.X,mn.Y,mx.Z));
        Add(new Vector3(mx.X,mn.Y,mn.Z), new Vector3(mx.X,mn.Y,mx.Z), new Vector3(mx.X,mx.Y,mn.Z));
        Add(new Vector3(mn.X,mn.Y,mn.Z), new Vector3(mn.X,mn.Y,mx.Z), new Vector3(mx.X,mn.Y,mn.Z));
        Add(new Vector3(mn.X,mx.Y,mn.Z), new Vector3(mx.X,mx.Y,mn.Z), new Vector3(mn.X,mx.Y,mx.Z));
        Add(new Vector3(mn.X,mn.Y,mn.Z), new Vector3(mx.X,mn.Y,mn.Z), new Vector3(mn.X,mx.Y,mn.Z));
        Add(new Vector3(mn.X,mn.Y,mx.Z), new Vector3(mn.X,mx.Y,mx.Z), new Vector3(mx.X,mn.Y,mx.Z));
        bool ok=BrushManipulation.TryClip(br, Vector3.Zero, Vector3.UnitX, "wall", out var front, out var back);
        Assert.True(ok);
        Assert.NotNull(front); Assert.NotNull(back);
        Assert.True(BrushManipulation.IsBrushValid(front!));
        Assert.True(BrushManipulation.IsBrushValid(back!));
    }
    [Fact]
    public void CsgSubtract()
    {
        var a=new MapBrush(); var b=new MapBrush();
        void AddBox(MapBrush br, Vector3 mn, Vector3 mx){ void Add(Vector3 p1,Vector3 p2,Vector3 p3){var f=new MapFace{P1=p1,P2=p2,P3=p3,Texture="wall"};f.ComputePlane();br.Faces.Add(f);} Add(new Vector3(mn.X,mn.Y,mn.Z), new Vector3(mn.X,mx.Y,mn.Z), new Vector3(mn.X,mn.Y,mx.Z)); Add(new Vector3(mx.X,mn.Y,mn.Z), new Vector3(mx.X,mn.Y,mx.Z), new Vector3(mx.X,mx.Y,mn.Z)); Add(new Vector3(mn.X,mn.Y,mn.Z), new Vector3(mn.X,mn.Y,mx.Z), new Vector3(mx.X,mn.Y,mn.Z)); Add(new Vector3(mn.X,mx.Y,mn.Z), new Vector3(mx.X,mx.Y,mn.Z), new Vector3(mn.X,mx.Y,mx.Z)); Add(new Vector3(mn.X,mn.Y,mn.Z), new Vector3(mx.X,mn.Y,mn.Z), new Vector3(mn.X,mx.Y,mn.Z)); Add(new Vector3(mn.X,mn.Y,mx.Z), new Vector3(mn.X,mx.Y,mx.Z), new Vector3(mx.X,mn.Y,mx.Z)); }
        AddBox(a, new Vector3(-32,-32,-32), new Vector3(32,32,32));
        AddBox(b, new Vector3(0,-32,-32), new Vector3(64,32,32));
        var res=BrushManipulation.Subtract(a,b);
        Assert.NotEmpty(res);
        foreach(var r in res) Assert.True(BrushManipulation.IsBrushValid(r));
    }
}

public class CollisionWorldTests
{
    [Fact]
    public void TraceWithBvh()
    {
        var world=new CollisionWorld();
        var data=new List<CollisionBrushData>();
        // ground plane brush at z=0.. -10? quick box 10x10
        var planes=new float[]{ 0,0,1,0, 0,0,-1,10, 1,0,0,5, -1,0,0,5, 0,1,0,5, 0,-1,0,5 };
        data.Add(new CollisionBrushData{Planes=planes, Min=new Vector3(-5,-5,-10), Max=new Vector3(5,5,0)});
        world.Load(data);
        var res=world.Trace(new Vector3(0,5,0), new Vector3(0,-5,0), new Vector3(0.5f,0.5f,0.5f));
        Assert.True(res.Fraction<1f);
    }
}

public class ObjLoaderTests
{
    [Fact]
    public void LoadSampleObj()
    {
        string tmp=Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.obj");
        File.WriteAllText(tmp, "v 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\nvt 0 0\nvt 1 0\nvt 1 1\nvn 0 0 1\nf 1/1/1 2/2/1 3/3/1\nf 1/1/1 3/3/1 4/1/1\n");
        bool ok=ObjModelLoader.TryLoad(tmp, out var mesh, "test");
        File.Delete(tmp);
        Assert.True(ok);
        Assert.NotNull(mesh);
        Assert.True(mesh.Positions.Length>0);
        Assert.True(mesh.Indices.Length>0);
    }
}
