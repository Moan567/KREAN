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

    public void DrawSelection(MapEditorSession session, Matrix4x4 view, Matrix4x4 proj)
    {
        // multi-brush / multi-entity highlight — TrenchBroom orange
        if (session.MultiSelectedBrushes.Count > 0)
        {
            var multiLines = new List<Vector3>();
            foreach (var (ei, bi) in session.MultiSelectedBrushes)
            {
                if (ei < 0 || ei >= session.Entities.Count) continue;
                var e = session.Entities[ei];
                if (bi < 0 || bi >= e.Brushes.Count) continue;
                if (ei == session.SelectedIndex && bi == session.SelectedBrushIndex) continue;
                var br = e.Brushes[bi];
                BrushManipulation.GetBounds(br, out var mn, out var mx);
                if (mn.X <= mx.X) AddBoxLines(multiLines, mn, mx);
            }
            if (multiLines.Count > 0) DrawLines(multiLines, view, proj, new Vector3(1f, 0.55f, 0.15f), 1.8f);
        }
        if (session.MultiSelectedEntities.Count > 0)
        {
            var multiEntLines = new List<Vector3>();
            foreach (var ei in session.MultiSelectedEntities)
            {
                if (ei < 0 || ei >= session.Entities.Count) continue;
                if (ei == session.SelectedIndex) continue;
                var e = session.Entities[ei];
                if (e.Brushes.Count > 0)
                {
                    foreach (var br in e.Brushes) { BrushManipulation.GetBounds(br, out var mn, out var mx); if (mn.X <= mx.X) AddBoxLines(multiEntLines, mn, mx); }
                }
                else if (e.Properties.TryGetValue("origin", out var o))
                {
                    var p = MapEditorSession.ParseVec3Public(o);
                    AddBoxLines(multiEntLines, p - new Vector3(8), p + new Vector3(8));
                }
            }
            if (multiEntLines.Count > 0) DrawLines(multiEntLines, view, proj, new Vector3(1f, 0.55f, 0.15f), 1.8f);
        }
        var lines = CollectSelectionLines(session);
        if (lines.Count > 0)
        {
            Vector3 col = session.Mode == EditMode.Face && session.SelectedFaceIndex >= 0
                ? new Vector3(1, 0.3f, 0.2f) : session.Mode == EditMode.Vertex ? new Vector3(0.35f, 0.95f, 0.35f) : new Vector3(1, 0.85f, 0.2f);
            DrawLines(lines, view, proj, col, 2.2f);
        }
        if (session.Mode == EditMode.Vertex && session.SelectedBrush != null)
            DrawVertexHandles(session, view, proj);
        if ((session.Mode == EditMode.Brush || session.Mode == EditMode.Face) && session.SelectedBrush != null)
            DrawFaceHandles(session, view, proj);
    }

    void DrawFaceHandles(MapEditorSession session, Matrix4x4 view, Matrix4x4 proj)
    {
        var br = session.SelectedBrush;
        if (br == null) return;
        var lines = new List<Vector3>();
        float s = Units.QuakeToMeters;
        Vector3 ToEng(Vector3 q) => new Vector3(q.X, q.Z, -q.Y) * s;
        for (int i = 0; i < br.Faces.Count; i++)
        {
            var c = session.GetFaceCenter(br, i);
            Vector3 ce = ToEng(c);
            bool isSel = i == session.SelectedFaceIndex;
            float r = isSel ? 0.10f : 0.065f;
            var n = br.Faces[i].Normal;
            // small square oriented to face — approximate as axis-aligned small cross + square
            // square in plane: use two tangents
            Vector3 up = MathF.Abs(n.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitY;
            Vector3 u = Vector3.Normalize(Vector3.Cross(n, up));
            Vector3 v = Vector3.Normalize(Vector3.Cross(u, n));
            // square
            Vector3 p0 = ce + u * r + v * r;
            Vector3 p1 = ce + u * r - v * r;
            Vector3 p2 = ce - u * r - v * r;
            Vector3 p3 = ce - u * r + v * r;
            lines.Add(p0); lines.Add(p1);
            lines.Add(p1); lines.Add(p2);
            lines.Add(p2); lines.Add(p3);
            lines.Add(p3); lines.Add(p0);
            // normal tick
            lines.Add(ce); lines.Add(ce + new Vector3(n.X, n.Z, -n.Y) * (r * 1.2f));
        }
        Vector3 col = new Vector3(0.45f, 0.75f, 1f);
        DrawLines(lines, view, proj, col, 1.6f);
        if (session.SelectedFaceIndex >= 0 && session.SelectedFaceIndex < br.Faces.Count)
        {
            var ci = session.GetFaceCenter(br, session.SelectedFaceIndex);
            Vector3 ce = ToEng(ci);
            var n = br.Faces[session.SelectedFaceIndex].Normal;
            float r = 0.12f;
            Vector3 up = MathF.Abs(n.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitY;
            Vector3 u = Vector3.Normalize(Vector3.Cross(n, up));
            Vector3 v = Vector3.Normalize(Vector3.Cross(u, n));
            var hi = new List<Vector3>();
            Vector3 p0 = ce + u * r + v * r;
            Vector3 p1 = ce + u * r - v * r;
            Vector3 p2 = ce - u * r - v * r;
            Vector3 p3 = ce - u * r + v * r;
            hi.Add(p0); hi.Add(p1); hi.Add(p1); hi.Add(p2); hi.Add(p2); hi.Add(p3); hi.Add(p3); hi.Add(p0);
            hi.Add(ce); hi.Add(ce + new Vector3(n.X, n.Z, -n.Y) * 0.22f);
            DrawLines(hi, view, proj, new Vector3(1, 0.55f, 0.2f), 2.2f);
        }
    }

    void DrawVertexHandles(MapEditorSession session, Matrix4x4 view, Matrix4x4 proj)
    {
        var corners = session.GetSelectedBrushCorners();
        if (corners.Length == 0) return;
        var lines = new List<Vector3>();
        float s = Units.QuakeToMeters;
        Vector3 ToEng(Vector3 q) => new Vector3(q.X, q.Z, -q.Y) * s;
        for (int i = 0; i < corners.Length; i++)
        {
            Vector3 c = ToEng(corners[i]);
            bool sel = i == session.SelectedVertexIndex;
            float r = sel ? 0.12f : 0.06f;
            // small 3-axis cross
            lines.Add(c + new Vector3(-r, 0, 0)); lines.Add(c + new Vector3(r, 0, 0));
            lines.Add(c + new Vector3(0, -r, 0)); lines.Add(c + new Vector3(0, r, 0));
            lines.Add(c + new Vector3(0, 0, -r)); lines.Add(c + new Vector3(0, 0, r));
            // box around handle if not selected
            if (!sel)
            {
                float b = 0.04f;
                Vector3 a = c + new Vector3(-b, -b, -b);
                Vector3 b2 = c + new Vector3(b, b, b);
                // just small cube edges? reuse AddBoxLines but tiny
                // approximate with cross is enough
            }
        }
        Vector3 col = new Vector3(0.55f, 1f, 0.55f);
        // highlight selected in orange
        DrawLines(lines, view, proj, col, 1.8f);
        if (session.SelectedVertexIndex >= 0 && session.SelectedVertexIndex < corners.Length)
        {
            var selLines = new List<Vector3>();
            Vector3 sc = ToEng(corners[session.SelectedVertexIndex]);
            float r2 = 0.14f;
            selLines.Add(sc + new Vector3(-r2, 0, 0)); selLines.Add(sc + new Vector3(r2, 0, 0));
            selLines.Add(sc + new Vector3(0, -r2, 0)); selLines.Add(sc + new Vector3(0, r2, 0));
            selLines.Add(sc + new Vector3(0, 0, -r2)); selLines.Add(sc + new Vector3(0, 0, r2));
            DrawLines(selLines, view, proj, new Vector3(1, 0.6f, 0.15f), 2.5f);
        }
    }

    /// <summary>TrenchBroom-style live brush-creation preview (cyan box).</summary>
    public void DrawBrushPreview(Vector3 minQuake, Vector3 maxQuake, Matrix4x4 view, Matrix4x4 proj)
    {
        var lo = Vector3.Min(minQuake, maxQuake);
        var hi = Vector3.Max(minQuake, maxQuake);
        var lines = new List<Vector3>();
        AddBoxLines(lines, lo, hi);
        DrawLines(lines, view, proj, new Vector3(0.3f, 0.9f, 1f), 2.2f);
    }

    /// <summary>Hover ghost — where a click would place a default brush (Hammer preview).</summary>
    public void DrawGhostPreview(Vector3 minQuake, Vector3 maxQuake, Matrix4x4 view, Matrix4x4 proj)
    {
        var lo = Vector3.Min(minQuake, maxQuake);
        var hi = Vector3.Max(minQuake, maxQuake);
        var lines = new List<Vector3>();
        AddBoxLines(lines, lo, hi);
        // add center cross on top face for easier alignment
        float s = Units.QuakeToMeters;
        Vector3 ToEng(Vector3 q) => new Vector3(q.X, q.Z, -q.Y) * s;
        Vector3 centerTop = ToEng(new Vector3((lo.X + hi.X) * 0.5f, (lo.Y + hi.Y) * 0.5f, hi.Z));
        float r = 0.25f;
        lines.Add(centerTop + new Vector3(-r, 0, 0)); lines.Add(centerTop + new Vector3(r, 0, 0));
        lines.Add(centerTop + new Vector3(0, -r, 0)); lines.Add(centerTop + new Vector3(0, r, 0));
        DrawLines(lines, view, proj, new Vector3(1f, 0.92f, 0.32f), 1.6f);
    }

    List<Vector3> CollectSelectionLines(MapEditorSession session)
    {
        var lines = new List<Vector3>();
        var e = session.Selected;
        if (e == null) return lines;

        if (session.SelectedBrush != null)
        {
            var br = session.SelectedBrush;
            BrushManipulation.GetBounds(br, out var min, out var max);
            if (min.X <= max.X) AddBoxLines(lines, min, max);
            if (session.SelectedFaceIndex >= 0 && session.SelectedFaceIndex < br.Faces.Count)
            {
                var face = br.Faces[session.SelectedFaceIndex];
                var polys = BrushGeometry.Build(br, out _, out _);
                float s = Units.QuakeToMeters;
                foreach (var poly in polys)
                {
                    if (poly.Face != face) continue;
                    if (poly.Vertices.Count < 3) continue;
                    for (int i = 0; i < poly.Vertices.Count; i++)
                    {
                        var v0 = new Vector3(poly.Vertices[i].X, poly.Vertices[i].Z, -poly.Vertices[i].Y) * s;
                        var v1 = new Vector3(poly.Vertices[(i + 1) % poly.Vertices.Count].X, poly.Vertices[(i + 1) % poly.Vertices.Count].Z, -poly.Vertices[(i + 1) % poly.Vertices.Count].Y) * s;
                        lines.Add(v0); lines.Add(v1);
                    }
                }
            }
        }
        else if (e.Brushes.Count > 0)
        {
            session.GetSelectedBounds(out var min, out var max);
            if (min.X <= max.X) AddBoxLines(lines, min, max);
        }
        else if (e.Properties.TryGetValue("origin", out var o))
        {
            var p = MapEditorSession.ParseVec3Public(o);
            AddBoxLines(lines, p - new Vector3(8), p + new Vector3(8));
            float s = Units.QuakeToMeters;
            Vector3 cen = new Vector3(p.X, p.Z, -p.Y) * s;
            lines.Add(cen); lines.Add(cen + new Vector3(0.5f, 0, 0));
            lines.Add(cen); lines.Add(cen + new Vector3(0, 0.5f, 0));
            lines.Add(cen); lines.Add(cen + new Vector3(0, 0, 0.5f));
        }
        return lines;
    }

    static void AddBoxLines(List<Vector3> lines, Vector3 minQ, Vector3 maxQ)
    {
        float s = Units.QuakeToMeters;
        Vector3 ToEng(Vector3 q) => new Vector3(q.X, q.Z, -q.Y) * s;
        Vector3 a = ToEng(new Vector3(minQ.X, minQ.Y, minQ.Z));
        Vector3 b = ToEng(new Vector3(maxQ.X, minQ.Y, minQ.Z));
        Vector3 c = ToEng(new Vector3(maxQ.X, maxQ.Y, minQ.Z));
        Vector3 d = ToEng(new Vector3(minQ.X, maxQ.Y, minQ.Z));
        Vector3 e1 = ToEng(new Vector3(minQ.X, minQ.Y, maxQ.Z));
        Vector3 f = ToEng(new Vector3(maxQ.X, minQ.Y, maxQ.Z));
        Vector3 g = ToEng(new Vector3(maxQ.X, maxQ.Y, maxQ.Z));
        Vector3 h = ToEng(new Vector3(minQ.X, maxQ.Y, maxQ.Z));
        void AddEdge(Vector3 p1, Vector3 p2) { lines.Add(p1); lines.Add(p2); }
        AddEdge(a, b); AddEdge(b, c); AddEdge(c, d); AddEdge(d, a);
        AddEdge(e1, f); AddEdge(f, g); AddEdge(g, h); AddEdge(h, e1);
        AddEdge(a, e1); AddEdge(b, f); AddEdge(c, g); AddEdge(d, h);
    }

    unsafe void DrawLines(List<Vector3> lines, Matrix4x4 view, Matrix4x4 proj, Vector3 col, float width)
    {
        if (lines.Count == 0) return;
        var data = new float[lines.Count * 3];
        for (int i = 0; i < lines.Count; i++) { data[i * 3] = lines[i].X; data[i * 3 + 1] = lines[i].Y; data[i * 3 + 2] = lines[i].Z; }

        _gl.UseProgram(_program);
        _gl.UniformMatrix4(_uView, 1, false, GetMat(view));
        _gl.UniformMatrix4(_uProj, 1, false, GetMat(proj));
        _gl.Uniform3(_uColor, col.X, col.Y, col.Z);
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = data) _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(float)), p, BufferUsageARB.DynamicDraw);
        _gl.Enable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);
        _gl.LineWidth(width);
        _gl.DrawArrays(PrimitiveType.Lines, 0, (uint)lines.Count);
        _gl.BindVertexArray(0);
        _gl.UseProgram(0);
        static unsafe float* GetMat(Matrix4x4 m) => (float*)&m;
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteProgram(_program);
    }
}
