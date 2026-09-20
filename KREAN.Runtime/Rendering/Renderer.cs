using System.Numerics;
using KREAN.Core;
using KREAN.Core.Components;
using KREAN.Core.ECS;
using KREAN.Core.Scenes;
using Silk.NET.OpenGL;

namespace KREAN.Runtime.Rendering;

public sealed class Renderer : IDisposable
{
    const string VertexSource = @"#version 330 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aUv;

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProj;

out vec3 vNormal;
out vec3 vWorld;
out vec2 vUv;
out float vDepth;

void main()
{
    vec4 world = uModel * vec4(aPos, 1.0);
    vWorld = world.xyz;
    vNormal = mat3(transpose(inverse(uModel))) * aNormal;
    vUv = aUv;
    vec4 clip = uProj * uView * world;
    vDepth = clip.w;
    gl_Position = clip;
}";

    const string FragmentSource = @"#version 330 core
in vec3 vNormal;
in vec3 vWorld;
in vec2 vUv;
in float vDepth;

uniform vec3 uColor;
uniform vec3 uFogColor;
uniform vec3 uSunDir;
uniform sampler2D uTex;
uniform bool uUseTex;
uniform int uPointLightCount;
uniform vec3 uPointLightPos[16];
uniform vec3 uPointLightColor[16];
uniform float uPointLightRange[16];
uniform float uPointLightIntensity[16];

out vec4 FragColor;

void main()
{
    vec3 n = normalize(vNormal);

    float diff = max(dot(n, normalize(uSunDir)), 0.0);
    float hemi = n.y * 0.5 + 0.5;
    float light = 0.30 + 0.30 * hemi + 0.50 * diff;
    light = floor(light * 5.0 + 0.5) / 5.0;

    // point lights - additive diffuse + attenuation
    float pointAccum = 0.0;
    vec3 pointColorAccum = vec3(0.0);
    for(int i=0;i<16;i++) {
        if(i >= uPointLightCount) break;
        vec3 lp = uPointLightPos[i];
        vec3 lc = uPointLightColor[i];
        float range = uPointLightRange[i];
        float intens = uPointLightIntensity[i];
        vec3 toLight = lp - vWorld;
        float dist = length(toLight);
        if(dist > range) continue;
        vec3 L = toLight / max(dist, 0.001);
        float ndotl = max(dot(n, L), 0.0);
        float atten = clamp(1.0 - dist / range, 0.0, 1.0);
        atten = atten * atten; // quadratic falloff
        pointAccum += ndotl * atten * intens;
        pointColorAccum += lc * ndotl * atten * intens;
    }
    // blend point lights into base light (tinted)
    light = clamp(light + pointAccum * 0.9, 0.0, 1.8);

    vec3 baseColor = uColor;
    vec3 texColor = vec3(1.0);
    if(uUseTex) texColor = texture(uTex, vUv).rgb;
    // if textured, use tex; otherwise checker
    vec3 col;
    if(uUseTex) col = baseColor * texColor;
    else {
        vec3 a = abs(n);
        vec2 uv = (a.y > a.x && a.y > a.z) ? vWorld.xz : ((a.x > a.z) ? vWorld.zy : vWorld.xy);
        float checker = mod(floor(uv.x) + floor(uv.y), 2.0);
        col = baseColor * mix(0.90, 1.0, checker);
    }
    // tint by point light color (average)
    if(pointAccum > 0.01) {
        vec3 avgPointCol = pointColorAccum / max(pointAccum, 0.001);
        // blend towards warm point color
        col = mix(col, col * avgPointCol * 1.2, clamp(pointAccum*0.6, 0.0, 0.7));
        col *= (1.0 + pointAccum*0.35);
    }
    col *= light;

    float fog = clamp((vDepth - 25.0) / 90.0, 0.0, 1.0);
    FragColor = vec4(mix(col, uFogColor, fog), 1.0);
}";

    readonly GL _gl;
    Shader _shader;
    readonly Dictionary<string, GpuMesh> _meshes = new();
    TextureManager? _textures;
    readonly List<FileSystemWatcher> _shaderWatchers = new();
    DateTime _lastShaderReload = DateTime.MinValue;
    bool _shaderReloadPending;

    public Vector3 SkyColor { get; set; } = new(0.62f, 0.78f, 0.95f);
    public Vector3 SunDirection { get; set; } = new(0.4f, 0.8f, 0.3f);

    public Renderer(GL gl)
    {
        _gl = gl;
        var vs = TryLoadShaderFile("world.vert") ?? VertexSource;
        var fs = TryLoadShaderFile("world.frag") ?? FragmentSource;
        _shader = new Shader(gl, vs, fs);
        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(TriangleFace.Back);
        _gl.FrontFace(FrontFaceDirection.Ccw);
        try { _textures = new TextureManager(gl); _textures.EnableHotReload(); } catch { _textures = null; }
        try { EnableShaderHotReload(); } catch {}
    }
    string? TryLoadShaderFile(string name)
    {
        string[] candidates = new[]{
            Path.Combine("KREAN.Runtime","assets","shaders",name),
            Path.Combine("assets","shaders",name),
            Path.Combine(AppContext.BaseDirectory,"assets","shaders",name),
            Path.Combine(AppContext.BaseDirectory,"..","..","..","KREAN.Runtime","assets","shaders",name),
            name
        };
        foreach(var p in candidates) if(File.Exists(p)) try{ return File.ReadAllText(p); }catch{}
        return null;
    }
    void EnableShaderHotReload()
    {
        string[] dirs = new[]{
            Path.Combine(Directory.GetCurrentDirectory(),"KREAN.Runtime","assets","shaders"),
            Path.Combine(Directory.GetCurrentDirectory(),"assets","shaders"),
            Path.Combine(AppContext.BaseDirectory,"assets","shaders"),
            "assets/shaders"
        };
        foreach(var d in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if(!Directory.Exists(d)) continue;
            try{
                var w=new FileSystemWatcher(d){ Filter="*.frag", IncludeSubdirectories=true, NotifyFilter=NotifyFilters.LastWrite|NotifyFilters.Size, EnableRaisingEvents=true };
                w.Changed+=OnShaderChanged; w.Created+=OnShaderChanged; _shaderWatchers.Add(w);
                var w2=new FileSystemWatcher(d){ Filter="*.vert", IncludeSubdirectories=true, NotifyFilter=NotifyFilters.LastWrite|NotifyFilters.Size, EnableRaisingEvents=true };
                w2.Changed+=OnShaderChanged; w2.Created+=OnShaderChanged; _shaderWatchers.Add(w2);
            }catch{}
        }
    }
    void OnShaderChanged(object s, FileSystemEventArgs e)
    {
        if((DateTime.UtcNow-_lastShaderReload).TotalMilliseconds<800) return;
        _lastShaderReload=DateTime.UtcNow;
        _shaderReloadPending=true;
        Console.WriteLine($"[hotreload] shader change {e.FullPath} -> pending");
    }
    public void ReloadShaders()
    {
        var vs = TryLoadShaderFile("world.vert") ?? VertexSource;
        var fs = TryLoadShaderFile("world.frag") ?? FragmentSource;
        var newShader = new Shader(_gl, vs, fs);
        var old = _shader;
        _shader = newShader;
        try{ old.Dispose(); }catch{}
    }

    public void UploadMeshes(IEnumerable<MeshData> meshes)
    {
        foreach (var m in _meshes.Values) m.Dispose();
        _meshes.Clear();
        foreach (var data in meshes) _meshes[data.Id] = new GpuMesh(_gl, data);
    }

    public void SetTextureSearchPaths(params string[] paths) => _textures?.SetSearchPaths(paths);

    public void Render(World world, int width, int height)
    {
        if(_shaderReloadPending){ _shaderReloadPending=false; try{ ReloadShaders(); Console.WriteLine("[hotreload] shaders reloaded"); }catch(Exception ex){ Console.WriteLine($"[hotreload] shader reload failed: {ex.Message}"); } }
        _gl.Viewport(0, 0, (uint)width, (uint)height);
        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(TriangleFace.Back);
        _gl.FrontFace(FrontFaceDirection.Ccw);
        _gl.DepthFunc(DepthFunction.Lequal);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.ScissorTest);
        _gl.ClearColor(SkyColor.X, SkyColor.Y, SkyColor.Z, 1f);
        _gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));
        if (height <= 0) return;

        float aspect = width / (float)height;
        Matrix4x4 view = Matrix4x4.Identity, proj = Matrix4x4.Identity;
        bool hasCamera = false;

        world.Query<Transform, Camera, PlayerController>(
            (Entity e, ref Transform t, ref Camera cam, ref PlayerController pc) =>
            {
                var eye = t.Position + Vector3.UnitY * pc.EyeOffset;
                var forward = PlayerController.LookDirection(pc.Yaw, pc.Pitch);
                view = Matrix4x4.CreateLookAt(eye, eye + forward, Vector3.UnitY);
                proj = PerspectiveGL(cam.FovDegrees * Units.Deg2Rad, aspect, cam.Near, cam.Far);
                hasCamera = true;
            });

        if (!hasCamera) return;
        // frustum culling planes from view*proj
        var vp = view * proj;
        var frustum = ExtractFrustum(vp);

        _shader.Use();
        _shader.Set("uView", view);
        _shader.Set("uProj", proj);
        _shader.Set("uFogColor", SkyColor);
        _shader.Set("uSunDir", SunDirection);

        // collect point lights (max 16)
        var lightPos = new List<Vector3>();
        var lightCol = new List<Vector3>();
        var lightRange = new List<float>();
        var lightInt = new List<float>();
        world.Query<Transform, PointLight>((Entity e, ref Transform t, ref PointLight pl) =>
        {
            if (lightPos.Count >= 16) return;
            lightPos.Add(t.Position);
            lightCol.Add(pl.Color);
            lightRange.Add(pl.Range <= 0 ? 10f : pl.Range);
            lightInt.Add(pl.Intensity);
        });
        // also query point lights without transform? fallback to world origin offset
        // push to shader arrays
        _gl.Uniform1(_gl.GetUniformLocation(_shader.Handle, "uPointLightCount"), lightPos.Count);
        for (int i = 0; i < lightPos.Count; i++)
        {
            _shader.Set($"uPointLightPos[{i}]", lightPos[i]);
            _shader.Set($"uPointLightColor[{i}]", lightCol[i]);
            _shader.Set($"uPointLightRange[{i}]", lightRange[i]);
            _shader.Set($"uPointLightIntensity[{i}]", lightInt[i]);
        }
        // zero remaining
        for (int i = lightPos.Count; i < 16; i++)
        {
            _shader.Set($"uPointLightRange[{i}]", 0f);
            _shader.Set($"uPointLightIntensity[{i}]", 0f);
        }

        world.Query<Transform, Model>((Entity e, ref Transform t, ref Model model) =>
        {
            if (model.Meshes == null) return;
            var mMat = t.ToMatrix();
            // frustum cull per entity (approximate using mesh bounds combined)
            // compute world bounds for culling by transforming mesh bounds (rough)
            // if all meshes culled, skip entity
            bool anyVisible = false;
            foreach (var id in model.Meshes) if (_meshes.TryGetValue(id, out var mm)) { var wb = TransformBounds(mm.BoundsMin, mm.BoundsMax, mMat); if (IsBoxInFrustum(wb.min, wb.max, frustum)) { anyVisible = true; break; } }
            if (!anyVisible && model.Meshes.Length>0) return;
            _shader.Set("uModel", mMat);

            foreach (var id in model.Meshes)
            {
                if (!_meshes.TryGetValue(id, out var mesh)) continue;
                var wb2 = TransformBounds(mesh.BoundsMin, mesh.BoundsMax, mMat);
                if (!IsBoxInFrustum(wb2.min, wb2.max, frustum)) continue;
                _shader.Set("uColor", MaterialPalette.ColorFor(mesh.Material));
                // texture binding
                bool hasTex = false;
                if (_textures != null && _textures.TryGet(mesh.Material, out var tex))
                {
                    _gl.ActiveTexture(TextureUnit.Texture0);
                    _gl.BindTexture(TextureTarget.Texture2D, tex.Handle);
                    _shader.Set("uTex", 0);
                    hasTex = true;
                }
                _shader.Set("uUseTex", hasTex ? 1f : 0f);
                // bool uniform: use int 0/1 via float hack? Shader expects bool, setting as int via uniform1i would be needed. Use Set float but gl will convert.
                // Workaround: set via int location manually
                _gl.Uniform1(_gl.GetUniformLocation(_shader.Handle, "uUseTex"), hasTex ? 1 : 0);
                mesh.Draw();
            }
        });
    }

    /// <summary>OpenGL clip space (z in -1..1). System.Numerics' own version targets 0..1 (D3D).</summary>
    static Matrix4x4 PerspectiveGL(float fovY, float aspect, float near, float far)
    {
        float t = 1f / MathF.Tan(fovY * 0.5f);
        return new Matrix4x4(
            t / aspect, 0, 0, 0,
            0, t, 0, 0,
            0, 0, (far + near) / (near - far), -1,
            0, 0, 2f * far * near / (near - far), 0);
    }
    static Vector4[] ExtractFrustum(Matrix4x4 m)
    {
        var planes = new Vector4[6];
        // row-major: m is view*proj
        // left
        planes[0] = new Vector4(m.M14 + m.M11, m.M24 + m.M21, m.M34 + m.M31, m.M44 + m.M41);
        planes[1] = new Vector4(m.M14 - m.M11, m.M24 - m.M21, m.M34 - m.M31, m.M44 - m.M41);
        planes[2] = new Vector4(m.M14 + m.M12, m.M24 + m.M22, m.M34 + m.M32, m.M44 + m.M42);
        planes[3] = new Vector4(m.M14 - m.M12, m.M24 - m.M22, m.M34 - m.M32, m.M44 - m.M42);
        planes[4] = new Vector4(m.M14 + m.M13, m.M24 + m.M23, m.M34 + m.M33, m.M44 + m.M43);
        planes[5] = new Vector4(m.M14 - m.M13, m.M24 - m.M23, m.M34 - m.M33, m.M44 - m.M43);
        for(int i=0;i<6;i++){ float len = new Vector3(planes[i].X, planes[i].Y, planes[i].Z).Length(); if(len>1e-6f) planes[i]/=len; }
        return planes;
    }
    static bool IsBoxInFrustum(Vector3 mn, Vector3 mx, Vector4[] planes)
    {
        foreach(var p in planes)
        {
            Vector3 pv = new Vector3(p.X>0? mx.X:mn.X, p.Y>0? mx.Y:mn.Y, p.Z>0? mx.Z:mn.Z);
            if(Vector3.Dot(new Vector3(p.X,p.Y,p.Z), pv) + p.W < 0) return false;
        }
        return true;
    }
    static (Vector3 min, Vector3 max) TransformBounds(Vector3 mn, Vector3 mx, Matrix4x4 mat)
    {
        var corners = new Vector3[8]{
            new(mn.X,mn.Y,mn.Z), new(mx.X,mn.Y,mn.Z), new(mx.X,mx.Y,mn.Z), new(mn.X,mx.Y,mn.Z),
            new(mn.X,mn.Y,mx.Z), new(mx.X,mn.Y,mx.Z), new(mx.X,mx.Y,mx.Z), new(mn.X,mx.Y,mx.Z)
        };
        var outMin=new Vector3(float.MaxValue); var outMax=new Vector3(float.MinValue);
        foreach(var c in corners){ var tc=Vector3.Transform(c, mat); outMin=Vector3.Min(outMin,tc); outMax=Vector3.Max(outMax,tc); }
        return (outMin,outMax);
    }

    public void Dispose()
    {
        foreach(var w in _shaderWatchers) try{ w.Dispose(); }catch{}
        _shaderWatchers.Clear();
        foreach (var m in _meshes.Values) m.Dispose();
        _meshes.Clear();
        _textures?.Dispose();
        _shader.Dispose();
    }
}
