using System.Numerics;
using System.Text.Json;

namespace KREAN.Editor;

public sealed class EditorSettings
{
    public float GridSize { get; set; } = 8f;
    public bool GridSnapEnabled { get; set; } = true;
    public Vector3 BrushDefaultSize { get; set; } = new Vector3(64,64,64);
    public string DefaultTexture { get; set; } = "wall";
    public string ActiveLayer { get; set; } = "Default";
    public Dictionary<string,bool> LayerVisibility { get; set; } = new(StringComparer.OrdinalIgnoreCase){{"Default",true}};
    public bool ShowSceneBrowser { get; set; } = true;
    public bool ShowMaterialBrowser { get; set; } = true;
    public bool ShowEntityPalette { get; set; } = false;
    public bool ShowOrtho { get; set; } = true;
    public bool ShowSearchReport { get; set; } = false;
    public Vector2 TopPan { get; set; } = Vector2.Zero;
    public float TopZoom { get; set; } = 0.6f;
    public Vector2 FrontPan { get; set; } = Vector2.Zero;
    public float FrontZoom { get; set; } = 0.6f;
    public Vector2 SidePan { get; set; } = Vector2.Zero;
    public float SideZoom { get; set; } = 0.6f;

    static string PathFor(string? mapPath)
    {
        // global editor settings next to executable or cwd
        var dir = string.IsNullOrEmpty(mapPath) ? Directory.GetCurrentDirectory() : Path.GetDirectoryName(Path.GetFullPath(mapPath)) ?? Directory.GetCurrentDirectory();
        // try project root
        foreach(var cand in new[]{ Path.Combine(dir,"editor.json"), Path.Combine(Directory.GetCurrentDirectory(),"editor.json"), Path.Combine(AppContext.BaseDirectory,"editor.json")})
            if(File.Exists(cand)) return cand;
        return Path.Combine(Directory.GetCurrentDirectory(),"editor.json");
    }

    public static EditorSettings Load(string? mapPath=null)
    {
        try
        {
            var p = PathFor(mapPath);
            if(File.Exists(p))
            {
                var json=File.ReadAllText(p);
                var s=JsonSerializer.Deserialize<EditorSettings>(json, new JsonSerializerOptions{PropertyNameCaseInsensitive=true, IncludeFields=true});
                if(s!=null) return s;
            }
        }catch{}
        return new EditorSettings();
    }

    public void Save(string? mapPath=null)
    {
        try
        {
            var p = Path.Combine(Directory.GetCurrentDirectory(),"editor.json");
            // also try to save next to map if map exists
            if(!string.IsNullOrEmpty(mapPath))
            {
                var d=Path.GetDirectoryName(Path.GetFullPath(mapPath));
                if(!string.IsNullOrEmpty(d) && Directory.Exists(d)) p=Path.Combine(d,"editor.json");
            }
            var json=JsonSerializer.Serialize(this, new JsonSerializerOptions{WriteIndented=true, IncludeFields=true});
            File.WriteAllText(p, json);
        }catch{}
    }

    public void ApplyTo(MapEditorSession s, EditorUI ui)
    {
        s.GridSize = GridSize;
        s.GridSnapEnabled = GridSnapEnabled;
        s.BrushDefaultSize = BrushDefaultSize;
        s.DefaultTexture = DefaultTexture;
        s.ActiveLayer = ActiveLayer;
        foreach(var kv in LayerVisibility) s.SetLayerVisible(kv.Key, kv.Value);
        // ui
        ui.SetShowFlags(ShowSceneBrowser, ShowMaterialBrowser, ShowEntityPalette, ShowOrtho);
        ui.SetOrtho(TopPan, TopZoom, FrontPan, FrontZoom, SidePan, SideZoom);
        if(ShowSearchReport) ui.ShowSearchReport=true;
    }

    public void CaptureFrom(MapEditorSession s, EditorUI ui)
    {
        GridSize=s.GridSize;
        GridSnapEnabled=s.GridSnapEnabled;
        BrushDefaultSize=s.BrushDefaultSize;
        DefaultTexture=s.DefaultTexture;
        ActiveLayer=s.ActiveLayer;
        LayerVisibility=new Dictionary<string,bool>(s.LayerVisibility, StringComparer.OrdinalIgnoreCase);
        ShowSceneBrowser=ui.GetShowFlag("scene");
        ShowMaterialBrowser=ui.GetShowFlag("material");
        ShowEntityPalette=ui.GetShowFlag("entity");
        ShowOrtho=ui.GetShowFlag("ortho");
        ShowSearchReport=ui.ShowSearchReport;
        var ortho=ui.GetOrtho();
        TopPan=ortho.topPan; TopZoom=ortho.topZoom;
        FrontPan=ortho.frontPan; FrontZoom=ortho.frontZoom;
        SidePan=ortho.sidePan; SideZoom=ortho.sideZoom;
    }
}
