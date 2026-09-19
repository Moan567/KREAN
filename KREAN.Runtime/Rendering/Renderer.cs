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
out float vDepth;

void main()
{
    vec4 world = uModel * vec4(aPos, 1.0);
    vWorld = world.xyz;
    vNormal = mat3(uModel) * aNormal;
    vec4 clip = uProj * uView * world;
    vDepth = clip.w;
    gl_Position = clip;
}";

    // Toy look: banded lighting, 1 m checker (free scale reference), soft fog.
    const string FragmentSource = @"#version 330 core
in vec3 vNormal;
in vec3 vWorld;
in float vDepth;

uniform vec3 uColor;
uniform vec3 uFogColor;
uniform vec3 uSunDir;

out vec4 FragColor;

void main()
{
    vec3 n = normalize(vNormal);

    float diff = max(dot(n, normalize(uSunDir)), 0.0);
    float hemi = n.y * 0.5 + 0.5;
    float light = 0.30 + 0.30 * hemi + 0.50 * diff;
    light = floor(light * 5.0 + 0.5) / 5.0;

    vec3 a = abs(n);
    vec2 uv = (a.y > a.x && a.y > a.z) ? vWorld.xz : ((a.x > a.z) ? vWorld.zy : vWorld.xy);
    float checker = mod(floor(uv.x) + floor(uv.y), 2.0);

    vec3 col = uColor * light * mix(0.90, 1.0, checker);

    float fog = clamp((vDepth - 25.0) / 90.0, 0.0, 1.0);
    FragColor = vec4(mix(col, uFogColor, fog), 1.0);
}";

    readonly GL _gl;
    readonly Shader _shader;
    readonly Dictionary<string, GpuMesh> _meshes = new();

    public Vector3 SkyColor { get; set; } = new(0.62f, 0.78f, 0.95f);
    public Vector3 SunDirection { get; set; } = new(0.4f, 0.8f, 0.3f);

    public Renderer(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);
        _gl.Enable(EnableCap.DepthTest);
    }

    public void UploadMeshes(IEnumerable<MeshData> meshes)
    {
        foreach (var m in _meshes.Values) m.Dispose();
        _meshes.Clear();
        foreach (var data in meshes) _meshes[data.Id] = new GpuMesh(_gl, data);
    }

    public void Render(World world, int width, int height)
    {
        _gl.Viewport(0, 0, (uint)width, (uint)height);
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

        _shader.Use();
        _shader.Set("uView", view);
        _shader.Set("uProj", proj);
        _shader.Set("uFogColor", SkyColor);
        _shader.Set("uSunDir", SunDirection);

        world.Query<Transform, Model>((Entity e, ref Transform t, ref Model model) =>
        {
            if (model.Meshes == null) return;
            _shader.Set("uModel", t.ToMatrix());

            foreach (var id in model.Meshes)
            {
                if (!_meshes.TryGetValue(id, out var mesh)) continue;
                _shader.Set("uColor", MaterialPalette.ColorFor(mesh.Material));
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

    public void Dispose()
    {
        foreach (var m in _meshes.Values) m.Dispose();
        _meshes.Clear();
        _shader.Dispose();
    }
}
