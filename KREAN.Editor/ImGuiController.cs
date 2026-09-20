using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace KREAN.Editor;

public sealed unsafe class ImGuiController : IDisposable
{
    readonly GL _gl;
    readonly IWindow _window;
    readonly IInputContext _input;

    uint _vao, _vbo, _ebo, _program, _fontTex;
    int _attribPos, _attribUv, _attribCol, _uniformProj, _uniformTex;
    int _fbWidth, _fbHeight;
    int _winWidth, _winHeight;

    public ImGuiController(GL gl, IWindow window, IInputContext input)
    {
        _gl = gl;
        _window = window;
        _input = input;

        ImGui.CreateContext();
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        io.BackendFlags |= ImGuiBackendFlags.HasMouseCursors | ImGuiBackendFlags.HasSetMousePos;

        // Nuake font pack - try IBM VGA 8x16 (Quake) first, fallback to default
        try
        {
            string[] fontCandidates = new[]
            {
                Path.Combine("assets","fonts","Nuake_IBM_VGA.ttf"),
                Path.Combine("KREAN.Editor","assets","fonts","Nuake_IBM_VGA.ttf"),
                Path.Combine(AppContext.BaseDirectory,"assets","fonts","Nuake_IBM_VGA.ttf"),
                Path.Combine(AppContext.BaseDirectory,"..","..","..","KREAN.Editor","assets","fonts","Nuake_IBM_VGA.ttf"),
                Path.Combine(Directory.GetCurrentDirectory(),"assets","fonts","Nuake_IBM_VGA.ttf"),
                Path.Combine(Directory.GetCurrentDirectory(),"KREAN.Editor","assets","fonts","Nuake_IBM_VGA.ttf"),
                Path.Combine("oldschool_pc_font_pack_v2.2_FULL","ttf - Px (pixel outline)","Px437_IBM_VGA_8x16.ttf"),
                Path.Combine("oldschool_pc_font_pack_v2.2_FULL","ttf - Ac (aspect-corrected)","Ac437_IBM_VGA_8x16.ttf"),
            };
            string? found = fontCandidates.FirstOrDefault(File.Exists);
            if(found!=null)
            {
                // 16px pixel font, slightly oversampled for readability
                io.Fonts.AddFontFromFileTTF(found, 16f);
                Console.WriteLine($"[font] loaded Nuake {found}");
            }
            else io.Fonts.AddFontDefault();
        } catch { io.Fonts.AddFontDefault(); }
        io.FontGlobalScale = 1f;

        CreateDeviceObjects();
        SetPerFrameImGuiData(1f / 60f);

        foreach (var kb in _input.Keyboards)
        {
            kb.KeyChar += OnKeyChar;
            kb.KeyDown += OnKeyDown;
            kb.KeyUp += OnKeyUp;
        }
        foreach (var m in _input.Mice)
        {
            m.MouseDown += OnMouseDown;
            m.MouseUp += OnMouseUp;
            m.Scroll += OnScroll;
            m.MouseMove += OnMouseMove;
        }

        _window.FramebufferResize += s => { _fbWidth = s.X; _fbHeight = s.Y; };
        _window.Resize += s => { _winWidth = s.X; _winHeight = s.Y; };
        _winWidth = _window.Size.X;
        _winHeight = _window.Size.Y;
        _fbWidth = _window.FramebufferSize.X;
        _fbHeight = _window.FramebufferSize.Y;
    }

    void CreateDeviceObjects()
    {
        const string vert = @"#version 330 core
uniform mat4 ProjMtx;
in vec2 Position;
in vec2 UV;
in vec4 Color;
out vec2 Frag_UV;
out vec4 Frag_Color;
void main()
{
    Frag_UV = UV;
    Frag_Color = Color;
    gl_Position = ProjMtx * vec4(Position.xy, 0, 1);
}";
        const string frag = @"#version 330 core
uniform sampler2D Texture;
in vec2 Frag_UV;
in vec4 Frag_Color;
out vec4 Out_Color;
void main()
{
    Out_Color = Frag_Color * texture(Texture, Frag_UV.st);
}";

        _program = _gl.CreateProgram();
        uint vs = Compile(ShaderType.VertexShader, vert);
        uint fs = Compile(ShaderType.FragmentShader, frag);
        _gl.AttachShader(_program, vs);
        _gl.AttachShader(_program, fs);
        _gl.LinkProgram(_program);
        _gl.GetProgram(_program, GLEnum.LinkStatus, out int linked);
        if (linked == 0) throw new Exception("ImGui shader link failed: " + _gl.GetProgramInfoLog(_program));
        _gl.DetachShader(_program, vs);
        _gl.DetachShader(_program, fs);
        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);

        _uniformProj = _gl.GetUniformLocation(_program, "ProjMtx");
        _uniformTex = _gl.GetUniformLocation(_program, "Texture");
        _attribPos = _gl.GetAttribLocation(_program, "Position");
        _attribUv = _gl.GetAttribLocation(_program, "UV");
        _attribCol = _gl.GetAttribLocation(_program, "Color");

        _vao = _gl.GenVertexArray();
        _vbo = _gl.GenBuffer();
        _ebo = _gl.GenBuffer();

        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);

        CreateFontTexture();

        _gl.BindVertexArray(0);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);

        uint Compile(ShaderType type, string src)
        {
            uint sh = _gl.CreateShader(type);
            _gl.ShaderSource(sh, src);
            _gl.CompileShader(sh);
            _gl.GetShader(sh, GLEnum.CompileStatus, out int ok);
            if (ok == 0) throw new Exception($"{type} compile: {_gl.GetShaderInfoLog(sh)}");
            return sh;
        }
    }

    void CreateFontTexture()
    {
        var io = ImGui.GetIO();
        io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int w, out int h, out int bytesPerPixel);

        _fontTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _fontTex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba, (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, (void*)pixels);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        io.Fonts.SetTexID((IntPtr)_fontTex);
        io.Fonts.ClearTexData();
    }

    void SetPerFrameImGuiData(float dt)
    {
        var io = ImGui.GetIO();
        io.DisplaySize = new Vector2(_winWidth, _winHeight);
        if (_winWidth > 0 && _winHeight > 0)
            io.DisplayFramebufferScale = new Vector2((float)_fbWidth / _winWidth, (float)_fbHeight / _winHeight);
        io.DeltaTime = dt;
    }

    // ----- input callbacks using modern ImGui API
    void OnKeyChar(IKeyboard kb, char c)
    {
        ImGui.GetIO().AddInputCharacter((uint)c);
    }

    void OnKeyDown(IKeyboard kb, Key key, int scan)
    {
        var io = ImGui.GetIO();
        ImGuiKey imguiKey = ToImGuiKey(key);
        if (imguiKey != ImGuiKey.None) io.AddKeyEvent(imguiKey, true);
        UpdateModifiers(io);
        // also handle clipboard etc? ImGui handles via IO
    }

    void OnKeyUp(IKeyboard kb, Key key, int scan)
    {
        var io = ImGui.GetIO();
        ImGuiKey imguiKey = ToImGuiKey(key);
        if (imguiKey != ImGuiKey.None) io.AddKeyEvent(imguiKey, false);
        UpdateModifiers(io);
    }

    static void UpdateModifiers(ImGuiIOPtr io)
    {
        // Check via ImGui's key state already? We set ModCtrl etc via AddKeyEvent for modifiers too
        // Also set legacy bools for convenience
        // Silk doesn't expose easily global state; we approximate via checking if any keyboard has key down
        // Instead, we rely on the individual key events for ModCtrl etc to be set via the ImGuiKey events above.
        // Additionally set via AddKeyEvent for LeftCtrl etc.
    }

    void OnMouseDown(IMouse m, MouseButton b)
    {
        var io = ImGui.GetIO();
        int btn = b == MouseButton.Left ? 0 : b == MouseButton.Right ? 1 : b == MouseButton.Middle ? 2 : -1;
        if (btn >= 0) io.AddMouseButtonEvent(btn, true);
    }
    void OnMouseUp(IMouse m, MouseButton b)
    {
        var io = ImGui.GetIO();
        int btn = b == MouseButton.Left ? 0 : b == MouseButton.Right ? 1 : b == MouseButton.Middle ? 2 : -1;
        if (btn >= 0) io.AddMouseButtonEvent(btn, false);
    }
    void OnMouseMove(IMouse m, Vector2 pos)
    {
        ImGui.GetIO().AddMousePosEvent(pos.X, pos.Y);
    }
    void OnScroll(IMouse m, ScrollWheel wheel)
    {
        ImGui.GetIO().AddMouseWheelEvent(wheel.X, wheel.Y);
    }

    static ImGuiKey ToImGuiKey(Key k) => k switch
    {
        Key.Tab => ImGuiKey.Tab,
        Key.Left => ImGuiKey.LeftArrow,
        Key.Right => ImGuiKey.RightArrow,
        Key.Up => ImGuiKey.UpArrow,
        Key.Down => ImGuiKey.DownArrow,
        Key.PageUp => ImGuiKey.PageUp,
        Key.PageDown => ImGuiKey.PageDown,
        Key.Home => ImGuiKey.Home,
        Key.End => ImGuiKey.End,
        Key.Insert => ImGuiKey.Insert,
        Key.Delete => ImGuiKey.Delete,
        Key.Backspace => ImGuiKey.Backspace,
        Key.Space => ImGuiKey.Space,
        Key.Enter => ImGuiKey.Enter,
        Key.Escape => ImGuiKey.Escape,
        Key.A => ImGuiKey.A,
        Key.B => ImGuiKey.B,
        Key.C => ImGuiKey.C,
        Key.D => ImGuiKey.D,
        Key.E => ImGuiKey.E,
        Key.F => ImGuiKey.F,
        Key.G => ImGuiKey.G,
        Key.H => ImGuiKey.H,
        Key.I => ImGuiKey.I,
        Key.J => ImGuiKey.J,
        Key.K => ImGuiKey.K,
        Key.L => ImGuiKey.L,
        Key.M => ImGuiKey.M,
        Key.N => ImGuiKey.N,
        Key.O => ImGuiKey.O,
        Key.P => ImGuiKey.P,
        Key.Q => ImGuiKey.Q,
        Key.R => ImGuiKey.R,
        Key.S => ImGuiKey.S,
        Key.T => ImGuiKey.T,
        Key.U => ImGuiKey.U,
        Key.V => ImGuiKey.V,
        Key.W => ImGuiKey.W,
        Key.X => ImGuiKey.X,
        Key.Y => ImGuiKey.Y,
        Key.Z => ImGuiKey.Z,
        Key.Number0 => ImGuiKey._0,
        Key.Number1 => ImGuiKey._1,
        Key.Number2 => ImGuiKey._2,
        Key.Number3 => ImGuiKey._3,
        Key.Number4 => ImGuiKey._4,
        Key.Number5 => ImGuiKey._5,
        Key.Number6 => ImGuiKey._6,
        Key.Number7 => ImGuiKey._7,
        Key.Number8 => ImGuiKey._8,
        Key.Number9 => ImGuiKey._9,
        Key.F1 => ImGuiKey.F1,
        Key.F2 => ImGuiKey.F2,
        Key.F3 => ImGuiKey.F3,
        Key.F4 => ImGuiKey.F4,
        Key.F5 => ImGuiKey.F5,
        Key.F6 => ImGuiKey.F6,
        Key.ControlLeft or Key.ControlRight => ImGuiKey.ModCtrl,
        Key.ShiftLeft or Key.ShiftRight => ImGuiKey.ModShift,
        Key.AltLeft or Key.AltRight => ImGuiKey.ModAlt,
        Key.SuperLeft or Key.SuperRight => ImGuiKey.ModSuper,
        _ => ImGuiKey.None
    };

    public void Update(float deltaSeconds)
    {
        SetPerFrameImGuiData(deltaSeconds);
        ImGui.NewFrame();
    }

    public void Render()
    {
        ImGui.Render();
        RenderDrawData(ImGui.GetDrawData());
    }

    void RenderDrawData(ImDrawDataPtr drawData)
    {
        if (drawData.CmdListsCount == 0) return;

        // backup GL state
        _gl.GetInteger(GetPName.Blend, out int lastBlend);
        _gl.GetInteger(GetPName.ScissorTest, out int lastScissor);
        _gl.GetInteger(GetPName.CullFace, out int lastCull);
        _gl.GetInteger(GetPName.DepthTest, out int lastDepth);
        _gl.GetInteger(GetPName.BlendEquationRgb, out int lastBlendEq);
        int lastProgram = 0, lastTexture = 0, lastArrayBuffer = 0, lastVertexArray = 0;
        _gl.GetInteger(GetPName.CurrentProgram, out lastProgram);
        _gl.GetInteger(GetPName.TextureBinding2D, out lastTexture);
        _gl.GetInteger(GetPName.ArrayBufferBinding, out lastArrayBuffer);
        Span<int> lastViewport = stackalloc int[4];
        _gl.GetInteger(GetPName.Viewport, lastViewport);
        Span<int> lastScissorBox = stackalloc int[4];
        _gl.GetInteger(GetPName.ScissorBox, lastScissorBox);

        // setup render state
        _gl.Enable(EnableCap.Blend);
        _gl.BlendEquation(GLEnum.FuncAdd);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.Disable(EnableCap.CullFace);
        _gl.Disable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.ScissorTest);

        _gl.Viewport(0, 0, (uint)_fbWidth, (uint)_fbHeight);

        float L = drawData.DisplayPos.X;
        float R = drawData.DisplayPos.X + drawData.DisplaySize.X;
        float T = drawData.DisplayPos.Y;
        float B = drawData.DisplayPos.Y + drawData.DisplaySize.Y;

        var ortho = new Matrix4x4(
            2f / (R - L), 0, 0, 0,
            0, 2f / (T - B), 0, 0,
            0, 0, -1, 0,
            (R + L) / (L - R), (T + B) / (B - T), 0, 1);

        _gl.UseProgram(_program);
        _gl.Uniform1(_uniformTex, 0);
        _gl.UniformMatrix4(_uniformProj, 1, false, GetMatrix4x4(ortho));
        _gl.BindVertexArray(_vao);

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.EnableVertexAttribArray((uint)_attribPos);
        _gl.EnableVertexAttribArray((uint)_attribUv);
        _gl.EnableVertexAttribArray((uint)_attribCol);
        _gl.VertexAttribPointer((uint)_attribPos, 2, VertexAttribPointerType.Float, false, (uint)Marshal.SizeOf<ImDrawVert>(), (void*)0);
        _gl.VertexAttribPointer((uint)_attribUv, 2, VertexAttribPointerType.Float, false, (uint)Marshal.SizeOf<ImDrawVert>(), (void*)8);
        _gl.VertexAttribPointer((uint)_attribCol, 4, VertexAttribPointerType.UnsignedByte, true, (uint)Marshal.SizeOf<ImDrawVert>(), (void*)16);

        drawData.ScaleClipRects(ImGui.GetIO().DisplayFramebufferScale);

        for (int n = 0; n < drawData.CmdListsCount; n++)
        {
            var cmdList = drawData.CmdLists[n];
            int vtxSize = cmdList.VtxBuffer.Size * Marshal.SizeOf<ImDrawVert>();
            int idxSize = cmdList.IdxBuffer.Size * sizeof(ushort);

            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)vtxSize, (void*)cmdList.VtxBuffer.Data, BufferUsageARB.StreamDraw);
            _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)idxSize, (void*)cmdList.IdxBuffer.Data, BufferUsageARB.StreamDraw);

            for (int cmdI = 0; cmdI < cmdList.CmdBuffer.Size; cmdI++)
            {
                var cmd = cmdList.CmdBuffer[cmdI];
                if (cmd.UserCallback != IntPtr.Zero) throw new NotImplementedException("ImGui user callback not implemented");
                _gl.BindTexture(TextureTarget.Texture2D, (uint)cmd.TextureId);
                // Silk GL Scissor expects (x,y,w,h) with w/h as uint
                _gl.Scissor((int)cmd.ClipRect.X, (int)(_fbHeight - cmd.ClipRect.W), (uint)(cmd.ClipRect.Z - cmd.ClipRect.X), (uint)(cmd.ClipRect.W - cmd.ClipRect.Y));
                _gl.DrawElements(PrimitiveType.Triangles, cmd.ElemCount, DrawElementsType.UnsignedShort, (void*)(cmd.IdxOffset * sizeof(ushort)));
            }
        }

        // restore
        _gl.UseProgram((uint)lastProgram);
        _gl.BindTexture(TextureTarget.Texture2D, (uint)lastTexture);
        _gl.BindVertexArray((uint)lastVertexArray);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, (uint)lastArrayBuffer);
        if (lastBlend == 0) _gl.Disable(EnableCap.Blend); else _gl.Enable(EnableCap.Blend);
        if (lastScissor == 0) _gl.Disable(EnableCap.ScissorTest); else _gl.Enable(EnableCap.ScissorTest);
        if (lastCull == 0) _gl.Disable(EnableCap.CullFace); else _gl.Enable(EnableCap.CullFace);
        if (lastDepth == 0) _gl.Disable(EnableCap.DepthTest); else _gl.Enable(EnableCap.DepthTest);
        _gl.BlendEquation((GLEnum)lastBlendEq);
        _gl.Viewport(lastViewport[0], lastViewport[1], (uint)lastViewport[2], (uint)lastViewport[3]);
        _gl.Scissor(lastScissorBox[0], lastScissorBox[1], (uint)lastScissorBox[2], (uint)lastScissorBox[3]);

        static unsafe float* GetMatrix4x4(Matrix4x4 m) => (float*)&m;
    }

    public void Dispose()
    {
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteProgram(_program);
        _gl.DeleteTexture(_fontTex);
        ImGui.DestroyContext();
    }
}
