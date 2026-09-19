using System.Numerics;
using KREAN.Core;
using KREAN.MapCompiler;
using Silk.NET.OpenGL;

namespace KREAN.Editor;

public sealed class SelectionRenderer : IDisposable
{
    readonly GL _gl;
    uint _vao, _vbo, _program;
    int _uView, _uProj, _uColor;

    public unsafe SelectionRenderer(GL gl)
    {
        _gl = gl;
        const string vs = @"#version 330 core
layout(location=0) in vec3 aPos;
uniform mat4 uView; uniform mat4 uProj;
void main(){ gl_Position = uProj * uView * vec4(aPos,1.0); }";
        const string fs = @"#version 330 core
uniform vec3 uColor; out vec4 FragColor;
void main(){ FragColor = vec4(uColor,1.0); }";
        _program = _gl.CreateProgram();
        uint vsh = Compile(ShaderType.VertexShader, vs);
        uint fsh = Compile(ShaderType.FragmentShader, fs);
        _gl.AttachShader(_program, vsh); _gl.AttachShader(_program, fsh);
        _gl.LinkProgram(_program);
        _gl.GetProgram(_program, GLEnum.LinkStatus, out int ok);
        if (ok == 0) throw new Exception(_gl.GetProgramInfoLog(_program));
        _gl.DetachShader(_program, vsh); _gl.DetachShader(_program, fsh);
        _gl.DeleteShader(vsh); _gl.DeleteShader(fsh);
        _uView = _gl.GetUniformLocation(_program, "uView");
        _uProj = _gl.GetUniformLocation(_program, "uProj");
        _uColor = _gl.GetUniformLocation(_program, "uColor");
        _vao = _gl.GenVertexArray();
        _vbo = _gl.GenBuffer();
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 12, (void*)0);
        _gl.BindVertexArray(0);
        uint Compile(ShaderType t, string s)
        {
            uint sh = _gl.CreateShader(t);
            _gl.ShaderSource(sh, s);
            _gl.CompileShader(sh);
            _gl.GetShader(sh, GLEnum.CompileStatus, out int c);
            if (c == 0) throw new Exception(_gl.GetShaderInfoLog(sh));
            return sh;
        }
    }

    public unsafe void DrawSelection(MapEditorSession session, Matrix4x4 view, Matrix4x4 proj)
    {
        var e = session.Selected;
        if (e == null) return;
        List<Vector3> lines = new();
        float s = Units.QuakeToMeters;

        void AddBox(Vector3 minQ, Vector3 maxQ)
        {
            // 12 edges
            Vector3 ToEng(Vector3 q) => new Vector3(q.X, q.Z, -q.Y) * s;
            Vector3 a = ToEng(new Vector3(minQ.X, minQ.Y, minQ.Z));
            Vector3 b = ToEng(new Vector3(maxQ.X, minQ.Y, minQ.Z));
            Vector3 c = ToEng(new Vector3(maxQ.X, maxQ.Y, minQ.Z));
            Vector3 d = ToEng(new Vector3(minQ.X, maxQ.Y, minQ.Z));
            Vector3 e1 = ToEng(new Vector3(minQ.X, minQ.Y, maxQ.Z));
            Vector3 f = ToEng(new Vector3(maxQ.X, minQ.Y, maxQ.Z));
            Vector3 g = ToEng(new Vector3(maxQ.X, maxQ.Y, maxQ.Z));
            Vector3 h = ToEng(new Vector3(minQ.X, maxQ.Y, maxQ.Z));
            AddEdge(a,b); AddEdge(b,c); AddEdge(c,d); AddEdge(d,a);
            AddEdge(e1,f); AddEdge(f,g); AddEdge(g,h); AddEdge(h,e1);
            AddEdge(a,e1); AddEdge(b,f); AddEdge(c,g); AddEdge(d,h);
            void AddEdge(Vector3 p1, Vector3 p2){ lines.Add(p1); lines.Add(p2); }
        }

        if (session.SelectedBrush != null)
        {
            var br = session.SelectedBrush;
            BrushManipulation.GetBounds(br, out var min, out var max);
            if (min.X <= max.X) AddBox(min, max);
            // also draw face highlight if selected face
            if (session.SelectedFaceIndex >= 0 && session.SelectedFaceIndex < br.Faces.Count)
            {
                var face = br.Faces[session.SelectedFaceIndex];
                var polys = BrushGeometry.Build(br, out _, out _);
                // find poly for face
                foreach (var poly in polys)
                {
                    if (poly.Face != face) continue;
                    if (poly.Vertices.Count < 3) continue;
                    for (int i=0;i<poly.Vertices.Count;i++)
                    {
                        var v0 = new Vector3(poly.Vertices[i].X, poly.Vertices[i].Z, -poly.Vertices[i].Y) * s;
                        var v1 = new Vector3(poly.Vertices[(i+1)%poly.Vertices.Count].X, poly.Vertices[(i+1)%poly.Vertices.Count].Z, -poly.Vertices[(i+1)%poly.Vertices.Count].Y) * s;
                        lines.Add(v0); lines.Add(v1);
                    }
                }
            }
        }
        else if (e.Brushes.Count > 0)
        {
            session.GetSelectedBounds(out var min, out var max);
            if (min.X <= max.X) AddBox(min, max);
        }
        else if (e.Properties.TryGetValue("origin", out var o))
        {
            var p = MapEditorSession.ParseVec3Public(o);
            AddBox(p - new Vector3(8), p + new Vector3(8));
            // also small axes
            Vector3 ToEng(Vector3 q) => new Vector3(q.X, q.Z, -q.Y) * s;
            Vector3 cen = ToEng(p);
            lines.Add(cen); lines.Add(cen + new Vector3(0.5f,0,0));
            lines.Add(cen); lines.Add(cen + new Vector3(0,0.5f,0));
            lines.Add(cen); lines.Add(cen + new Vector3(0,0,0.5f));
        }

        if (lines.Count == 0) return;
        var data = new float[lines.Count * 3];
        for (int i=0;i<lines.Count;i++){ data[i*3]=lines[i].X; data[i*3+1]=lines[i].Y; data[i*3+2]=lines[i].Z; }

        _gl.UseProgram(_program);
        _gl.UniformMatrix4(_uView, 1, false, GetMat(view));
        _gl.UniformMatrix4(_uProj, 1, false, GetMat(proj));
        Vector3 col = session.Mode == EditMode.Face && session.SelectedFaceIndex>=0 ? new Vector3(1,0.3f,0.2f) : new Vector3(1,0.85f,0.2f);
        _gl.Uniform3(_uColor, col.X, col.Y, col.Z);
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        unsafe { fixed(float* p=data) _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length*sizeof(float)), p, BufferUsageARB.DynamicDraw); }
        _gl.Enable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);
        _gl.LineWidth(2f);
        _gl.DrawArrays(PrimitiveType.Lines, 0, (uint)lines.Count);
        _gl.BindVertexArray(0);
        _gl.UseProgram(0);
        unsafe static float* GetMat(Matrix4x4 m) => (float*)&m;
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteProgram(_program);
    }
}
