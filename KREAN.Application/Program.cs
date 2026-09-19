using KREAN.Application.Game;
using KREAN.MapCompiler;

string? mapPath = null;
string? scenePath = null;
string scriptsRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Scripts");
 // fallback to project source Scripts folder when running via dotnet run
if (!Directory.Exists(scriptsRoot))
    scriptsRoot = Path.Combine(Directory.GetCurrentDirectory(), "KREAN.Application", "Scripts");
if (!Directory.Exists(scriptsRoot))
    scriptsRoot = Path.Combine(Directory.GetCurrentDirectory(), "Scripts");
if (!Directory.Exists(scriptsRoot))
    scriptsRoot = Path.GetFullPath("Scripts");

bool showHelp = false;
bool noScripts = false;

for (int i = 0; i < args.Length; i++)
{
    var a = args[i];
    if (a == "--help" || a == "-h") showHelp = true;
    else if (a == "--no-scripts") noScripts = true;
    else if (a == "--scripts" && i + 1 < args.Length) scriptsRoot = args[++i];
    else if (a == "--sample") mapPath = "sample.map";
    else if (a.StartsWith("-")) { Console.WriteLine($"Unknown flag: {a}"); showHelp = true; }
    else
    {
        var ext = Path.GetExtension(a).ToLowerInvariant();
        if (ext == ".map") mapPath = a;
        else if (ext == ".scene.json" || ext == ".json") scenePath = a;
        else mapPath = a;
    }
}

if (showHelp) { PrintHelp(scriptsRoot); return 0; }

// handle --sample generation via MapCompiler
if (mapPath == "sample.map" && !File.Exists(mapPath))
{
    File.WriteAllText(mapPath, SampleMap.Generate());
    Console.WriteLine($"[app] wrote sample.map");
}

if (mapPath == null && scenePath == null)
{
    // default play: try sample.map -> compile if needed, or sample.scene.json
    if (File.Exists("sample.map")) mapPath = "sample.map";
    else if (File.Exists("sample.scene.json")) scenePath = "sample.scene.json";
    else
    {
        File.WriteAllText("sample.map", SampleMap.Generate());
        mapPath = "sample.map";
        Console.WriteLine("[app] no level specified — created sample.map");
    }
}

if (noScripts) scriptsRoot = ""; // disable

Console.WriteLine($"[app] KREAN.Application — map={mapPath ?? "none"} scene={scenePath ?? "none"} scripts={(string.IsNullOrEmpty(scriptsRoot) ? "disabled" : scriptsRoot)}");

using var app = new GameApp(mapPath, scenePath, scriptsRoot);
app.Run();
return 0;

static void PrintHelp(string scriptsRoot)
{
    Console.WriteLine($"""
        KREAN.Application — Play .map / .scene with compiled scripts
        Usage:
          KREAN.Application [level.map | level.scene.json] [options]

        Options:
          --scripts <path>   folder containing .cs scripts (default: {scriptsRoot})
          --no-scripts       disable script compilation (run engine only)
          --sample           create sample.map if missing and run it
          --help, -h         show this help

        Scripts:
          Stored and compiled from KREAN.Application/Scripts (Roslyn, hot reload).
          Any class implementing ISystem / IGameScript is auto-discovered.
          Examples in Scripts/Examples: SpinSystem, PlayerTweak, TriggerFeedback
          See KREAN.Application/Scripts/README.md

        Controls (in game):
          WASD move, Mouse look, Space jump, V noclip, Esc release mouse

        Map pipeline:
          .map (TrenchBroom) --MapCompiler--> .scene.json --Runtime--> game
          Editor saves .map; Application compiles on play if needed.

        Examples:
          dotnet run --project KREAN.Application -- sample.map
          dotnet run --project KREAN.Application -- myLevel.map --scripts ./MyScripts
          dotnet run --project KREAN.Application -- myLevel.scene.json --no-scripts
        """);
}
