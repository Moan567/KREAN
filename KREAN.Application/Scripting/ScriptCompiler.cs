using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace KREAN.Application.Scripting;

public static class ScriptCompiler
{
    public static Assembly? Compile(string scriptsRoot, out string diagnostics)
    {
        diagnostics = "";
        if (!Directory.Exists(scriptsRoot))
        {
            diagnostics = $"[scripts] folder not found: {Path.GetFullPath(scriptsRoot)}";
            return null;
        }

        var files = Directory.GetFiles(scriptsRoot, "*.cs", SearchOption.AllDirectories);
        if (files.Length == 0)
        {
            diagnostics = $"[scripts] no .cs files in {scriptsRoot}";
            return null;
        }

        var trees = new List<SyntaxTree>(files.Length);
        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            var tree = CSharpSyntaxTree.ParseText(text, path: f);
            trees.Add(tree);
        }

        var refs = CollectReferences();

        var compilation = CSharpCompilation.Create(
            assemblyName: "KREAN.Scripts",
            syntaxTrees: trees,
            references: refs,
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable,
                concurrentBuild: true));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);

        if (!result.Success)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[scripts] compile failed ({files.Length} files):");
            foreach (var d in result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                sb.AppendLine(d.ToString());
            diagnostics = sb.ToString();
            Console.WriteLine(diagnostics);
            return null;
        }

        ms.Seek(0, SeekOrigin.Begin);
        // Use collectible ALC so hot reload can unload (net8+). For simplicity use Default; hot reload will just load new assembly with different name.
        // To allow unloading, use custom ALC:
        var alc = new AssemblyLoadContext($"KREAN.Scripts_{Guid.NewGuid():N}", isCollectible: true);
        var asm = alc.LoadFromStream(ms);
        diagnostics = $"[scripts] compiled {files.Length} files -> {asm.FullName}";
        Console.WriteLine(diagnostics);
        return asm;
    }

    static List<MetadataReference> CollectReferences()
    {
        var refs = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            if (!seen.Add(path)) return;
            refs.Add(MetadataReference.CreateFromFile(path));
        }

        // Core runtime assemblies
        Add(typeof(object).Assembly.Location);
        Add(typeof(Console).Assembly.Location);
        Add(typeof(System.Numerics.Vector3).Assembly.Location);
        Add(typeof(System.Linq.Enumerable).Assembly.Location);
        Add(typeof(System.Text.Json.JsonSerializer).Assembly.Location);

        // KREAN assemblies
        foreach (var asm in new[] { typeof(KREAN.Core.ECS.World).Assembly, typeof(KREAN.Runtime.Engine).Assembly, typeof(KREAN.MapCompiler.MapParser).Assembly, typeof(ScriptCompiler).Assembly })
            Add(asm.Location);

        // Trusted platform assemblies (all framework dlls) — add all to ensure script compilation has every System.* reference
        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (!string.IsNullOrEmpty(tpa))
        {
            foreach (var p in tpa.Split(Path.PathSeparator))
                Add(p);
        }

        // Also ensure Silk.NET etc are referenced if scripts want input
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location)))
        {
            if (asm.FullName?.Contains("Silk.NET") == true) Add(asm.Location);
        }

        return refs;
    }
}
