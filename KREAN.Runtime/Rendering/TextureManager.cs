using Silk.NET.OpenGL;
using StbImageSharp;

namespace KREAN.Runtime.Rendering;

public sealed class GpuTexture : IDisposable
{
    public uint Handle { get; }
    readonly GL _gl;
    public GpuTexture(GL gl, uint handle) { _gl = gl; Handle = handle; }
    public void Dispose() => _gl.DeleteTexture(Handle);
}

public sealed class TextureManager : IDisposable
{
    readonly GL _gl;
    readonly Dictionary<string, GpuTexture> _cache = new(StringComparer.OrdinalIgnoreCase);
    List<string> _searchPaths = new() { "textures", "assets/textures", "Data/textures", "." };
    readonly List<FileSystemWatcher> _watchers = new();

    public TextureManager(GL gl) { _gl = gl; }

    public void SetSearchPaths(params string[] paths) { _searchPaths = paths.ToList(); }
    public void EnableHotReload()
    {
        foreach(var w in _watchers) try{ w.Dispose(); }catch{}
        _watchers.Clear();
        foreach(var dir in _searchPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if(!Directory.Exists(dir)) continue;
            try{
                var watcher=new FileSystemWatcher(dir){ IncludeSubdirectories=true, NotifyFilter=NotifyFilters.LastWrite|NotifyFilters.FileName|NotifyFilters.Size, Filter="*.*", EnableRaisingEvents=true };
                watcher.Changed += OnTextureFileChanged;
                watcher.Created += OnTextureFileChanged;
                watcher.Renamed += OnTextureFileChanged;
                watcher.Deleted += OnTextureFileChanged;
                _watchers.Add(watcher);
            }catch{}
        }
    }
    void OnTextureFileChanged(object s, FileSystemEventArgs e)
    {
        var name=Path.GetFileNameWithoutExtension(e.FullPath);
        if(string.IsNullOrWhiteSpace(name)) return;
        var keys=_cache.Keys.Where(k=>k.Equals(name,StringComparison.OrdinalIgnoreCase) || k.Replace('\\','/').Split('/').Last().Equals(name,StringComparison.OrdinalIgnoreCase)).ToList();
        foreach(var k in keys){ if(_cache.ContainsKey(k)){ _cache.Remove(k); Console.WriteLine($"[hotreload] texture invalidated {k} <- {e.FullPath}"); } }
    }

    public bool TryGet(string material, out GpuTexture tex)
    {
        if (_cache.TryGetValue(material, out tex!)) return true;
        // try load
        var loaded = TryLoad(material);
        if (loaded != null) { _cache[material] = loaded; tex = loaded; return true; }
        return false;
    }

    GpuTexture? TryLoad(string material)
    {
        // material may be like "textures/wall" or "wall"
        string baseName = material.Replace('\\','/').Split('/').Last();
        // search
        foreach (var dir in _searchPaths)
        {
            foreach (var ext in new[]{"png","jpg","jpeg","bmp","tga"})
            {
                string cand = Path.Combine(dir, baseName + "." + ext);
                if (File.Exists(cand))
                {
                    var t = LoadFromFile(cand);
                    if (t != null) return t;
                }
                string cand2 = Path.Combine(dir, material + "." + ext);
                if (File.Exists(cand2))
                {
                    var t2 = LoadFromFile(cand2);
                    if (t2 != null) return t2;
                }
            }
        }
        // fallback: try absolute
        if (File.Exists(material))
        {
            var t = LoadFromFile(material);
            if (t != null) return t;
        }
        return null;
    }

    GpuTexture? LoadFromFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var img = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            uint handle = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, handle);
            unsafe
            {
                fixed (byte* ptr = img.Data)
                    _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba, (uint)img.Width, (uint)img.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, ptr);
            }
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.LinearMipmapLinear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.Repeat);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.Repeat);
            _gl.GenerateMipmap(TextureTarget.Texture2D);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            return new GpuTexture(_gl, handle);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[tex] failed to load '{path}': {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        foreach(var w in _watchers) try{ w.Dispose(); }catch{}
        _watchers.Clear();
        foreach (var t in _cache.Values) t.Dispose();
        _cache.Clear();
    }
}
