using System.Text.Json;
using System.Text.Json.Serialization;
using KREAN.Core.Components;
using KREAN.Core.ECS;

namespace KREAN.Core.Scenes;

public static class SceneSerializer
{
    public static readonly JsonSerializerOptions Options = CreateOptions();
    static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };

    static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions
        {
            WriteIndented = true,
            IncludeFields = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };
        o.Converters.Add(new Vector3Converter());
        o.Converters.Add(new QuaternionConverter());
        return o;
    }

    public static SceneData Read(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<SceneData>(stream, Options)
               ?? throw new InvalidDataException($"'{path}' is not a valid scene file.");
    }

    public static void Write(SceneData scene, string path, bool indented = true)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(scene, indented ? Options : Compact));
    }

    /// <summary>Creates one ECS entity per EntityData and attaches its components.</summary>
    public static void Populate(World world, SceneData scene)
    {
        foreach (var data in scene.Entities)
        {
            var entity = world.CreateEntity();
            foreach (var (name, json) in data.Components)
            {
                if (!ComponentRegistry.TryGetType(name, out var type))
                {
                    Console.WriteLine($"[scene] unknown component '{name}' – skipped");
                    continue;
                }

                object? value = JsonSerializer.Deserialize(json, type, Options);
                if (value != null) world.SetComponent(entity, type, value);
            }
        }
    }

    /// <summary>Turns the live world back into scene data (editor "Save"). Mesh/collision data is carried over.</summary>
    public static SceneData Capture(World world, SceneData baseScene)
    {
        var scene = new SceneData
        {
            Version = baseScene.Version,
            Name = baseScene.Name,
            Meshes = baseScene.Meshes,
            Collision = baseScene.Collision
        };

        foreach (var entity in world.AllEntities())
        {
            if (world.Has<Transient>(entity)) continue;

            var data = new EntityData();
            foreach (var (type, value) in world.GetAllComponents(entity))
            {
                if (!ComponentRegistry.TryGetName(type, out var name)) continue;
                data.Components[name] = JsonSerializer.SerializeToElement(value, type, Options);
            }
            scene.Entities.Add(data);
        }
        return scene;
    }
}
