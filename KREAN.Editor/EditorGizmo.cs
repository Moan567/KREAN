using System.Numerics;
using KREAN.Core;
using Silk.NET.OpenGL;

namespace KREAN.Editor;

public enum GizmoAxis { None, X, Y, Z, XY, XZ, YZ, Screen }
public enum GizmoMode { Translate, Rotate, Scale }

/// <summary>TrenchBroom-like translate gizmo — 3 arrows + 3 plane squares, axis + plane hit test.</summary>
public sealed class EditorGizmo : IDisposable
{
    readonly GL _gl;
    uint _vao, _vbo, _program;
    int _uView, _uProj, _uColor;

    public GizmoAxis Hovered { get; private set; } = GizmoAxis.None;
    public GizmoAxis Active { get; set; } = GizmoAxis.None;
    public GizmoMode Mode { get; set; } = GizmoMode.Translate;
    public float HandleLength { get; set; } = 1.1f; // meters
    public float PlaneSize { get; set; } = 0.28f;
    public float PickRadiusPx { get; set; } = 12f;

    public EditorGizmo(GL gl)
    {
        _gl = gl;
        const string vs = @"#version 330 core
layout(location=0) in vec3 aPos; uniform mat4 uView; uniform mat4 uProj; void main(){ gl_Position = uProj * uView * vec4(aPos,1.0); }";
        const string fs = @"#version 330 core
uniform vec3 uColor; out vec4 FragColor; void main(){ FragColor = vec4(uColor,1.0); }";
        _program = _gl.CreateProgram();
        uint v = Compile(ShaderType.VertexShader, vs);
        uint f = Compile(ShaderType.FragmentShader, fs);
        _gl.AttachShader(_program, v); _gl.AttachShader(_program, f); _gl.LinkProgram(_program);
        _gl.GetProgram(_program, GLEnum.LinkStatus, out int ok);
        if (ok==0) throw new Exception(_gl.GetProgramInfoLog(_program));
        _gl.DetachShader(_program, v); _gl.DetachShader(_program, f);
        _gl.DeleteShader(v); _gl.DeleteShader(f);
        _uView = _gl.GetUniformLocation(_program, "uView");
        _uProj = _gl.GetUniformLocation(_program, "uProj");
        _uColor = _gl.GetUniformLocation(_program, "uColor");
        _vao = _gl.GenVertexArray(); _vbo = _gl.GenBuffer();
        _gl.BindVertexArray(_vao); _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.EnableVertexAttribArray(0);
        unsafe { _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 12, (void*)0); }
        _gl.BindVertexArray(0);
        uint Compile(ShaderType t,string s){ uint sh=_gl.CreateShader(t); _gl.ShaderSource(sh,s); _gl.CompileShader(sh); _gl.GetShader(sh,GLEnum.CompileStatus,out int c); if(c==0) throw new Exception(_gl.GetShaderInfoLog(sh)); return sh; }
    }

    public unsafe void Draw(Vector3 centerEngine, Matrix4x4 view, Matrix4x4 proj, GizmoAxis highlight = GizmoAxis.None)
    {
        if (Mode == GizmoMode.Rotate) { DrawRotate(centerEngine, view, proj, highlight); return; }
        if (Mode == GizmoMode.Scale) { DrawScale(centerEngine, view, proj, highlight); return; }
        var axes = new[] { (GizmoAxis.X, new Vector3(1,0,0), new Vector3(1,0.15f,0.15f)), (GizmoAxis.Y, new Vector3(0,1,0), new Vector3(0.15f,1,0.15f)), (GizmoAxis.Z, new Vector3(0,0,1), new Vector3(0.35f,0.6f,1f)) };
        _gl.UseProgram(_program);
        _gl.UniformMatrix4(_uView, 1, false, GetMat(view));
        _gl.UniformMatrix4(_uProj, 1, false, GetMat(proj));
        _gl.BindVertexArray(_vao);
        _gl.LineWidth(3.5f);
        _gl.Disable(EnableCap.DepthTest);
        // axes with thicker highlight and cone-like heads
        foreach(var (ax, dir, col) in axes)
        {
            bool isHi = ax==highlight || ax==Hovered || ax==Active;
            Vector3 c = isHi ? Vector3.One : col;
            if (ax==Hovered && ax!=Active) c = Vector3.Lerp(c, Vector3.One, 0.45f);
            // duplicate hint: if Active and Alt held, tint purple
            if (Active!=GizmoAxis.None && isHi) c = Vector3.Lerp(c, new Vector3(0.9f,0.4f,1f), 0.35f);
            _gl.Uniform3(_uColor, c.X, c.Y, c.Z);
            var l = new float[]{ centerEngine.X, centerEngine.Y, centerEngine.Z, centerEngine.X+dir.X*HandleLength, centerEngine.Y+dir.Y*HandleLength, centerEngine.Z+dir.Z*HandleLength };
            fixed(float* p=l) _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(l.Length*sizeof(float)), p, BufferUsageARB.DynamicDraw);
            _gl.DrawArrays(PrimitiveType.Lines, 0, 2);
            float head = 0.19f;
            float hw = head * 0.55f;
            Vector3 end = centerEngine + dir*HandleLength;
            // 4-line cone for better visibility
            Vector3 a1, a2, a3, a4;
            if (dir.X>0) { a1=new Vector3(-head, hw,0); a2=new Vector3(-head,-hw,0); a3=new Vector3(-head,0,hw); a4=new Vector3(-head,0,-hw); }
            else if (dir.Y>0) { a1=new Vector3(hw,-head,0); a2=new Vector3(-hw,-head,0); a3=new Vector3(0,-head,hw); a4=new Vector3(0,-head,-hw); }
            else { a1=new Vector3(hw,0,-head); a2=new Vector3(-hw,0,-head); a3=new Vector3(0,hw,-head); a4=new Vector3(0,-hw,-head); }
            float[] h = { end.X,end.Y,end.Z, end.X+a1.X,end.Y+a1.Y,end.Z+a1.Z, end.X,end.Y,end.Z, end.X+a2.X,end.Y+a2.Y,end.Z+a2.Z,
                          end.X,end.Y,end.Z, end.X+a3.X,end.Y+a3.Y,end.Z+a3.Z, end.X,end.Y,end.Z, end.X+a4.X,end.Y+a4.Y,end.Z+a4.Z,
                          end.X+a1.X,end.Y+a1.Y,end.Z+a1.Z, end.X+a2.X,end.Y+a2.Y,end.Z+a2.Z, end.X+a3.X,end.Y+a3.Y,end.Z+a3.Z, end.X+a4.X,end.Y+a4.Y,end.Z+a4.Z };
            fixed(float* ph=h) _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(h.Length*sizeof(float)), ph, BufferUsageARB.DynamicDraw);
            _gl.DrawArrays(PrimitiveType.Lines, 0, 10);
            // scale cube at end (small square)
            float cs = 0.06f;
            Vector3 ce = end + dir * 0.04f;
            Vector3 u = dir.X>0? new Vector3(0,cs,0) : new Vector3(cs,0,0);
            Vector3 v = dir.X>0? new Vector3(0,0,cs) : dir.Y>0? new Vector3(0,0,cs) : new Vector3(cs,0,0);
            // cube as wire
            Vector3 p0=ce+u+v, p1=ce+u-v, p2=ce-u-v, p3=ce-u+v;
            float[] cube = { p0.X,p0.Y,p0.Z, p1.X,p1.Y,p1.Z, p1.X,p1.Y,p1.Z, p2.X,p2.Y,p2.Z, p2.X,p2.Y,p2.Z, p3.X,p3.Y,p3.Z, p3.X,p3.Y,p3.Z, p0.X,p0.Y,p0.Z };
            fixed(float* pc=cube) _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(cube.Length*sizeof(float)), pc, BufferUsageARB.DynamicDraw);
            _gl.DrawArrays(PrimitiveType.Lines, 0, 8);
        }
        // plane squares (XY yellow, XZ magenta, YZ cyan) — TrenchBroom 2-axis drag
        var planes = new[] { (GizmoAxis.XY, new Vector3(1,1,0), new Vector3(0.85f,0.85f,0.2f)), (GizmoAxis.XZ, new Vector3(1,0,1), new Vector3(0.9f,0.35f,0.9f)), (GizmoAxis.YZ, new Vector3(0,1,1), new Vector3(0.25f,0.85f,0.85f)) };
        float ps = PlaneSize;
        foreach(var (ax, off, col) in planes)
        {
            bool isHi = ax==highlight || ax==Hovered || ax==Active;
            Vector3 c = isHi ? Vector3.One : col * 0.9f;
            _gl.Uniform3(_uColor, c.X, c.Y, c.Z);
            Vector3 o = centerEngine;
            Vector3 p0 = new(), p1 = new(), p2 = new(), p3 = new();
            if (ax==GizmoAxis.XY) { p0=o+new Vector3(ps,ps,0); p1=o+new Vector3(ps,0,0); p2=o; p3=o+new Vector3(0,ps,0); }
            else if (ax==GizmoAxis.XZ) { p0=o+new Vector3(ps,0,ps); p1=o+new Vector3(ps,0,0); p2=o; p3=o+new Vector3(0,0,ps); }
            else { p0=o+new Vector3(0,ps,ps); p1=o+new Vector3(0,ps,0); p2=o; p3=o+new Vector3(0,0,ps); }
            float[] quad = { p0.X,p0.Y,p0.Z, p1.X,p1.Y,p1.Z, p1.X,p1.Y,p1.Z, p2.X,p2.Y,p2.Z, p2.X,p2.Y,p2.Z, p3.X,p3.Y,p3.Z, p3.X,p3.Y,p3.Z, p0.X,p0.Y,p0.Z };
            fixed(float* pq=quad) _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(quad.Length*sizeof(float)), pq, BufferUsageARB.DynamicDraw);
            _gl.DrawArrays(PrimitiveType.Lines, 0, 8);
            if (isHi) { _gl.LineWidth(1.5f); fixed(float* pq2=quad) _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(quad.Length*sizeof(float)), pq2, BufferUsageARB.DynamicDraw); _gl.DrawArrays(PrimitiveType.Lines, 0, 8); _gl.LineWidth(3.5f); }
        }
        // center cube (screen move) — small white box at centre
        {
            bool isHi = GizmoAxis.Screen==highlight || GizmoAxis.Screen==Hovered || GizmoAxis.Screen==Active;
            Vector3 c = isHi ? Vector3.One : new Vector3(0.9f,0.9f,0.9f);
            _gl.Uniform3(_uColor, c.X, c.Y, c.Z);
            float cs = HandleLength * 0.08f;
            Vector3 cc = centerEngine;
            float[] box = {
                cc.X-cs,cc.Y-cs,cc.Z-cs, cc.X+cs,cc.Y-cs,cc.Z-cs, cc.X+cs,cc.Y-cs,cc.Z-cs, cc.X+cs,cc.Y+cs,cc.Z-cs, cc.X+cs,cc.Y+cs,cc.Z-cs, cc.X-cs,cc.Y+cs,cc.Z-cs, cc.X-cs,cc.Y+cs,cc.Z-cs, cc.X-cs,cc.Y-cs,cc.Z-cs,
                cc.X-cs,cc.Y-cs,cc.Z+cs, cc.X+cs,cc.Y-cs,cc.Z+cs, cc.X+cs,cc.Y-cs,cc.Z+cs, cc.X+cs,cc.Y+cs,cc.Z+cs, cc.X+cs,cc.Y+cs,cc.Z+cs, cc.X-cs,cc.Y+cs,cc.Z+cs, cc.X-cs,cc.Y+cs,cc.Z+cs, cc.X-cs,cc.Y-cs,cc.Z+cs,
                cc.X-cs,cc.Y-cs,cc.Z-cs, cc.X-cs,cc.Y-cs,cc.Z+cs, cc.X+cs,cc.Y-cs,cc.Z-cs, cc.X+cs,cc.Y-cs,cc.Z+cs, cc.X+cs,cc.Y+cs,cc.Z-cs, cc.X+cs,cc.Y+cs,cc.Z+cs, cc.X-cs,cc.Y+cs,cc.Z-cs, cc.X-cs,cc.Y+cs,cc.Z+cs
            };
            fixed(float* pb=box) _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(box.Length*sizeof(float)), pb, BufferUsageARB.DynamicDraw);
            _gl.DrawArrays(PrimitiveType.Lines, 0, 24);
        }
        _gl.Enable(EnableCap.DepthTest);
        _gl.BindVertexArray(0); _gl.UseProgram(0);
        static unsafe float* GetMat(Matrix4x4 m) => (float*)&m;
    }

    unsafe void DrawRotate(Vector3 c, Matrix4x4 view, Matrix4x4 proj, GizmoAxis hi)
    {
        _gl.UseProgram(_program);
        _gl.UniformMatrix4(_uView, 1, false, GetMat(view));
        _gl.UniformMatrix4(_uProj, 1, false, GetMat(proj));
        _gl.BindVertexArray(_vao);
        _gl.Disable(EnableCap.DepthTest);
        _gl.LineWidth(2.5f);
        foreach(var (ax, col) in new[] { (GizmoAxis.X, new Vector3(1,0.15f,0.15f)), (GizmoAxis.Y, new Vector3(0.15f,1,0.15f)), (GizmoAxis.Z, new Vector3(0.35f,0.6f,1f)) })
        {
            bool isHi = ax==hi || ax==Hovered || ax==Active;
            Vector3 cc = isHi ? Vector3.One : col;
            _gl.Uniform3(_uColor, cc.X, cc.Y, cc.Z);
            int segs = 32;
            var pts = new List<Vector3>();
            float r = HandleLength * 0.9f;
            for(int i=0;i<=segs;i++){ float a=i/(float)segs* MathF.PI*2; Vector3 p = ax==GizmoAxis.X ? new Vector3(0, MathF.Cos(a)*r, MathF.Sin(a)*r) : ax==GizmoAxis.Y ? new Vector3(MathF.Cos(a)*r,0,MathF.Sin(a)*r) : new Vector3(MathF.Cos(a)*r, MathF.Sin(a)*r,0); pts.Add(c+p); }
            for(int i=0;i<pts.Count-1;i++){ float[] l={pts[i].X,pts[i].Y,pts[i].Z, pts[i+1].X,pts[i+1].Y,pts[i+1].Z}; fixed(float* p=l) _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(l.Length*sizeof(float)), p, BufferUsageARB.DynamicDraw); _gl.DrawArrays(PrimitiveType.Lines,0,2); }
        }
        // screen rotate circle (view-aligned)
        {
            bool isHi = GizmoAxis.Screen==hi || GizmoAxis.Screen==Hovered || GizmoAxis.Screen==Active;
            _gl.Uniform3(_uColor, isHi?1:0.9f, isHi?1:0.9f, isHi?1:0.9f);
            // approximate screen circle by using view right/up - build in world around c
            // for simplicity draw slightly larger axis circles already cover; just draw center dot
            float cs = HandleLength*0.08f;
            _gl.Uniform3(_uColor, isHi?1:0.9f, isHi?1:0.9f, 0.2f);
            // small box already drawn via translate center? add small circle hint
        }
        _gl.Enable(EnableCap.DepthTest);
        _gl.BindVertexArray(0); _gl.UseProgram(0);
        static unsafe float* GetMat(Matrix4x4 m) => (float*)&m;
    }

    unsafe void DrawScale(Vector3 centerEngine, Matrix4x4 view, Matrix4x4 proj, GizmoAxis highlight)
    {
        var axes = new[] { (GizmoAxis.X, new Vector3(1,0,0), new Vector3(1,0.15f,0.15f)), (GizmoAxis.Y, new Vector3(0,1,0), new Vector3(0.15f,1,0.15f)), (GizmoAxis.Z, new Vector3(0,0,1), new Vector3(0.35f,0.6f,1f)) };
        _gl.UseProgram(_program);
        _gl.UniformMatrix4(_uView, 1, false, GetMat(view));
        _gl.UniformMatrix4(_uProj, 1, false, GetMat(proj));
        _gl.BindVertexArray(_vao);
        _gl.LineWidth(3.5f);
        _gl.Disable(EnableCap.DepthTest);
        foreach(var (ax, dir, col) in axes)
        {
            bool isHi = ax==highlight || ax==Hovered || ax==Active;
            Vector3 cc = isHi ? Vector3.One : col;
            _gl.Uniform3(_uColor, cc.X, cc.Y, cc.Z);
            var l = new float[]{ centerEngine.X, centerEngine.Y, centerEngine.Z, centerEngine.X+dir.X*HandleLength, centerEngine.Y+dir.Y*HandleLength, centerEngine.Z+dir.Z*HandleLength };
            fixed(float* p=l) _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(l.Length*sizeof(float)), p, BufferUsageARB.DynamicDraw);
            _gl.DrawArrays(PrimitiveType.Lines,0,2);
            // scale handle = wire cube larger + filled tint
            float cs = 0.12f;
            Vector3 ce = centerEngine + dir*HandleLength;
            Vector3 u = dir.X>0? new Vector3(0,cs,0) : new Vector3(cs,0,0);
            Vector3 v = dir.X>0? new Vector3(0,0,cs) : dir.Y>0? new Vector3(0,0,cs) : new Vector3(cs,0,0);
            Vector3 p0=ce+u+v, p1=ce+u-v, p2=ce-u-v, p3=ce-u+v;
            float[] cube = { p0.X,p0.Y,p0.Z, p1.X,p1.Y,p1.Z, p1.X,p1.Y,p1.Z, p2.X,p2.Y,p2.Z, p2.X,p2.Y,p2.Z, p3.X,p3.Y,p3.Z, p3.X,p3.Y,p3.Z, p0.X,p0.Y,p0.Z };
            fixed(float* pc=cube) _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(cube.Length*sizeof(float)), pc, BufferUsageARB.DynamicDraw);
            _gl.DrawArrays(PrimitiveType.Lines,0,8);
            // inner fill hint
            if(isHi){ _gl.LineWidth(2f); fixed(float* pc2=cube) _gl.BufferData(BufferTargetARB.ArrayBuffer,(nuint)(cube.Length*sizeof(float)),pc2,BufferUsageARB.DynamicDraw); _gl.DrawArrays(PrimitiveType.Lines,0,8); _gl.LineWidth(3.5f); }
        }
        // uniform scale center
        {
            bool isHi = GizmoAxis.Screen==highlight || GizmoAxis.Screen==Hovered || GizmoAxis.Screen==Active;
            _gl.Uniform3(_uColor, isHi?1:0.8f, isHi?1:0.8f, isHi?1:0.3f);
            float cs = HandleLength*0.14f;
            Vector3 cc=centerEngine;
            float[] box={ cc.X-cs,cc.Y-cs,cc.Z-cs, cc.X+cs,cc.Y-cs,cc.Z-cs, cc.X+cs,cc.Y-cs,cc.Z-cs, cc.X+cs,cc.Y+cs,cc.Z-cs, cc.X+cs,cc.Y+cs,cc.Z-cs, cc.X-cs,cc.Y+cs,cc.Z-cs, cc.X-cs,cc.Y+cs,cc.Z-cs, cc.X-cs,cc.Y-cs,cc.Z-cs, cc.X-cs,cc.Y-cs,cc.Z+cs, cc.X+cs,cc.Y-cs,cc.Z+cs, cc.X+cs,cc.Y-cs,cc.Z+cs, cc.X+cs,cc.Y+cs,cc.Z+cs, cc.X+cs,cc.Y+cs,cc.Z+cs, cc.X-cs,cc.Y+cs,cc.Z+cs, cc.X-cs,cc.Y+cs,cc.Z+cs, cc.X-cs,cc.Y-cs,cc.Z+cs, cc.X-cs,cc.Y-cs,cc.Z-cs, cc.X-cs,cc.Y-cs,cc.Z+cs, cc.X+cs,cc.Y-cs,cc.Z-cs, cc.X+cs,cc.Y-cs,cc.Z+cs, cc.X+cs,cc.Y+cs,cc.Z-cs, cc.X+cs,cc.Y+cs,cc.Z+cs, cc.X-cs,cc.Y+cs,cc.Z-cs, cc.X-cs,cc.Y+cs,cc.Z+cs };
            fixed(float* pb=box) _gl.BufferData(BufferTargetARB.ArrayBuffer,(nuint)(box.Length*sizeof(float)),pb,BufferUsageARB.DynamicDraw); _gl.DrawArrays(PrimitiveType.Lines,0,24);
        }
        _gl.Enable(EnableCap.DepthTest);
        _gl.BindVertexArray(0); _gl.UseProgram(0);
        static unsafe float* GetMat(Matrix4x4 m) => (float*)&m;
    }

    public void UpdateHover(Vector3 centerEngine, Matrix4x4 view, Matrix4x4 proj, Vector2 mousePx, Vector2 viewportPx)
    {
        var vp = viewportPx;
        Hovered = GizmoAxis.None;
        float best = PickRadiusPx;
        // axis lines
        foreach(var ax in new[] { GizmoAxis.X, GizmoAxis.Y, GizmoAxis.Z })
        {
            Vector3 dir = ax==GizmoAxis.X? Vector3.UnitX : ax==GizmoAxis.Y? Vector3.UnitY : Vector3.UnitZ;
            var p0 = Project(centerEngine, view, proj, vp);
            var p1 = Project(centerEngine + dir*HandleLength, view, proj, vp);
            if (!p0.HasValue || !p1.HasValue) continue;
            float d = DistanceToSegment(mousePx, p0.Value, p1.Value);
            if (d < best) { best = d; Hovered = ax; }
        }
        // plane squares — distance to quad center
        foreach(var ax in new[] { GizmoAxis.XY, GizmoAxis.XZ, GizmoAxis.YZ })
        {
            Vector3 off = ax==GizmoAxis.XY? new Vector3(PlaneSize*0.5f, PlaneSize*0.5f,0) : ax==GizmoAxis.XZ? new Vector3(PlaneSize*0.5f,0,PlaneSize*0.5f) : new Vector3(0,PlaneSize*0.5f,PlaneSize*0.5f);
            var pc = Project(centerEngine + off, view, proj, vp);
            if (!pc.HasValue) continue;
            float d = (mousePx - pc.Value).Length();
            if (d < 18f && d < best) { best = d; Hovered = ax; }
        }
        // center cube — screen move
        {
            var pc = Project(centerEngine, view, proj, vp);
            if (pc.HasValue)
            {
                float d = (mousePx - pc.Value).Length();
                if (d < 14f && d < best) { best = d; Hovered = GizmoAxis.Screen; }
            }
        }
    }

    static Vector2? Project(Vector3 world, Matrix4x4 view, Matrix4x4 proj, Vector2 vp)
    {
        var vpMat = view * proj; // actually proj*view
        var clip = Vector4.Transform(new Vector4(world,1), view);
        clip = Vector4.Transform(clip, proj);
        if (clip.W < 0.001f) return null;
        var ndc = new Vector2(clip.X/clip.W, clip.Y/clip.W);
        return new Vector2((ndc.X*0.5f+0.5f)*vp.X, (1-(ndc.Y*0.5f+0.5f))*vp.Y);
    }
    static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b-a; float t = Vector2.Dot(p-a, ab)/Math.Max(ab.LengthSquared(),1e-6f); t=Math.Clamp(t,0,1); var q=a+ab*t; return (p-q).Length();
    }

    public void Dispose(){ _gl.DeleteBuffer(_vbo); _gl.DeleteVertexArray(_vao); _gl.DeleteProgram(_program); }
}
