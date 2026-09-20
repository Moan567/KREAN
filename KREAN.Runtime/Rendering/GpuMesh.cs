using System.Numerics;
using KREAN.Core.Scenes;
using Silk.NET.OpenGL;

namespace KREAN.Runtime.Rendering;

public sealed unsafe class GpuMesh : IDisposable
{
    readonly GL _gl;
    readonly uint _vao, _vbo, _ebo;

    public string Material { get; }
    public int IndexCount { get; }
    public Vector3 BoundsMin { get; }
    public Vector3 BoundsMax { get; }

    /// <summary>Vertex layout: position(3) normal(3) uv(2).</summary>
    public GpuMesh(GL gl, MeshData data)
    {
        _gl = gl;
        Material = data.Material;
        IndexCount = data.Indices.Length;
        // compute bounds
        var bmin=new Vector3(float.MaxValue); var bmax=new Vector3(float.MinValue);
        for(int i=0;i<data.Positions.Length;i+=3){ var p=new Vector3(data.Positions[i], data.Positions[i+1], data.Positions[i+2]); bmin=Vector3.Min(bmin,p); bmax=Vector3.Max(bmax,p); }
        if(bmin.X> bmax.X){ bmin=Vector3.Zero; bmax=Vector3.Zero; }
        BoundsMin=bmin; BoundsMax=bmax;

        int vertexCount = data.Positions.Length / 3;
        var vertices = new float[vertexCount * 8];
        for (int i = 0; i < vertexCount; i++)
        {
            vertices[i * 8 + 0] = data.Positions[i * 3 + 0];
            vertices[i * 8 + 1] = data.Positions[i * 3 + 1];
            vertices[i * 8 + 2] = data.Positions[i * 3 + 2];
            vertices[i * 8 + 3] = data.Normals[i * 3 + 0];
            vertices[i * 8 + 4] = data.Normals[i * 3 + 1];
            vertices[i * 8 + 5] = data.Normals[i * 3 + 2];
            vertices[i * 8 + 6] = data.UVs[i * 2 + 0];
            vertices[i * 8 + 7] = data.UVs[i * 2 + 1];
        }

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferData(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<float>(vertices), BufferUsageARB.StaticDraw);

        _ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        _gl.BufferData(BufferTargetARB.ElementArrayBuffer, new ReadOnlySpan<int>(data.Indices), BufferUsageARB.StaticDraw);

        uint stride = 8 * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));

        _gl.BindVertexArray(0);
    }

    public void Draw()
    {
        _gl.BindVertexArray(_vao);
        _gl.DrawElements(PrimitiveType.Triangles, (uint)IndexCount, DrawElementsType.UnsignedInt, (void*)0);
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteVertexArray(_vao);
    }
}
