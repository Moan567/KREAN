using System.Numerics;
using Silk.NET.OpenGL;

namespace KREAN.Runtime.Rendering;

public sealed unsafe class Shader : IDisposable
{
    readonly GL _gl;
    readonly Dictionary<string, int> _uniforms = new();

    public uint Handle { get; }

    public Shader(GL gl, string vertexSource, string fragmentSource)
    {
        _gl = gl;

        uint vs = Compile(ShaderType.VertexShader, vertexSource);
        uint fs = Compile(ShaderType.FragmentShader, fragmentSource);

        Handle = _gl.CreateProgram();
        _gl.AttachShader(Handle, vs);
        _gl.AttachShader(Handle, fs);
        _gl.LinkProgram(Handle);

        _gl.GetProgram(Handle, GLEnum.LinkStatus, out int status);
        if (status == 0)
            throw new Exception("Shader link error: " + _gl.GetProgramInfoLog(Handle));

        _gl.DetachShader(Handle, vs);
        _gl.DetachShader(Handle, fs);
        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);
    }

    uint Compile(ShaderType type, string source)
    {
        uint shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, source);
        _gl.CompileShader(shader);

        _gl.GetShader(shader, GLEnum.CompileStatus, out int status);
        if (status == 0)
            throw new Exception($"{type} compile error: " + _gl.GetShaderInfoLog(shader));
        return shader;
    }

    public void Use() => _gl.UseProgram(Handle);

    int Location(string name)
    {
        if (!_uniforms.TryGetValue(name, out int loc))
        {
            loc = _gl.GetUniformLocation(Handle, name);
            _uniforms[name] = loc;
        }
        return loc;
    }

    // System.Numerics is row-vector/row-major; uploading without transpose gives the column-vector
    // form GLSL expects, so the shader multiplies as  proj * view * model * vec4(pos, 1).
    public void Set(string name, Matrix4x4 m) => _gl.UniformMatrix4(Location(name), 1, false, (float*)&m);
    public void Set(string name, Vector3 v) => _gl.Uniform3(Location(name), v.X, v.Y, v.Z);
    public void Set(string name, float f) => _gl.Uniform1(Location(name), f);
    public void Set(string name, int i) => _gl.Uniform1(Location(name), i);

    public void Dispose() => _gl.DeleteProgram(Handle);
}
