using System.Numerics;
using System.Text.RegularExpressions;

namespace KREAN.Editor;

public sealed class FgdProperty
{
    public string Name = "";
    public string Type = "";
    public string Description = "";
    public string DefaultValue = "";
}

public sealed class FgdClass
{
    public string ClassName = "";
    public string Description = "";
    public string Kind = ""; // PointClass, SolidClass, BaseClass
    public List<FgdProperty> Properties = new();
    public Vector3 Color = new(0.8f,0.8f,0.8f); // from color() meta
}

public static class FgdParser
{
    static readonly Regex ClassRegex = new(@"@(?<kind>\w+)\s*(?:base\s*\([^)]*\)\s*)*=\s*(?<name>\w+)\s*:\s*""(?<desc>[^""]*)""\s*(?:\[(?<block>.*?)\])?", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    static readonly Regex PropRegex = new(@"(?<key>\w+)\s*\(\s*(?<type>\w+)\s*\)\s*:\s*""(?<desc>[^""]*)""\s*(?::\s*""(?<def>[^""]*)"")?", RegexOptions.Singleline);

    public static List<FgdClass> ParseText(string text)
    {
        var list = new List<FgdClass>();
        // remove // comments line wise
        var lines = text.Split('\n');
        var cleaned = string.Join("\n", lines.Select(l=> {
            int idx=l.IndexOf("//");
            if(idx>=0) return l.Substring(0,idx);
            return l;
        }));
        foreach(Match m in ClassRegex.Matches(cleaned))
        {
            var kind=m.Groups["kind"].Value;
            if(kind.Equals("BaseClass", StringComparison.OrdinalIgnoreCase)) continue;
            var cls=new FgdClass{ ClassName=m.Groups["name"].Value, Description=m.Groups["desc"].Value, Kind=kind };
            var block=m.Groups["block"].Value;
            if(!string.IsNullOrWhiteSpace(block))
            {
                // try extract color
                var colorMatch = Regex.Match(block, @"color\s*\(\s*(\d+)\s+(\d+)\s+(\d+)\s*\)", RegexOptions.IgnoreCase);
                if(colorMatch.Success)
                {
                    if(byte.TryParse(colorMatch.Groups[1].Value, out var r) && byte.TryParse(colorMatch.Groups[2].Value, out var g) && byte.TryParse(colorMatch.Groups[3].Value, out var b))
                        cls.Color = new Vector3(r/255f, g/255f, b/255f);
                }
                foreach(Match pm in PropRegex.Matches(block))
                {
                    var prop=new FgdProperty{ Name=pm.Groups["key"].Value, Type=pm.Groups["type"].Value, Description=pm.Groups["desc"].Value, DefaultValue=pm.Groups["def"].Value };
                    // skip meta like color, size, etc already handled
                    if(prop.Name.Equals("color", StringComparison.OrdinalIgnoreCase) || prop.Name.Equals("size", StringComparison.OrdinalIgnoreCase) || prop.Name.Equals("studio", StringComparison.OrdinalIgnoreCase)) continue;
                    cls.Properties.Add(prop);
                }
            }
            list.Add(cls);
        }
        return list;
    }

    public static List<FgdClass> TryLoadFromSearch()
    {
        string[] candidates = new[]{
            "game.fgd","quake.fgd","KREAN.fgd","korean.fgd",
            Path.Combine("assets","game.fgd"),
            Path.Combine("Data","game.fgd"),
            Path.Combine(AppContext.BaseDirectory,"game.fgd"),
            Path.Combine(Directory.GetCurrentDirectory(),"game.fgd"),
        };
        foreach(var p in candidates)
        {
            if(File.Exists(p))
            {
                try{ var txt=File.ReadAllText(p); var res=ParseText(txt); if(res.Count>0){ Console.WriteLine($"[fgd] loaded {res.Count} classes from '{p}'"); return res; } }catch{}
            }
        }
        // also scan any .fgd in cwd
        try{
            foreach(var f in Directory.GetFiles(Directory.GetCurrentDirectory(),"*.fgd")){ try{ var txt=File.ReadAllText(f); var res=ParseText(txt); if(res.Count>0){ Console.WriteLine($"[fgd] loaded {res.Count} from '{f}'"); return res; }}catch{} }
        }catch{}
        return new List<FgdClass>();
    }
}
