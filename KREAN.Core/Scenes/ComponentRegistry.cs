using KREAN.Core.Components;

namespace KREAN.Core.Scenes;

/// <summary>
/// Maps component types to stable names used in scene files.
/// Only registered components are saved/loaded – runtime-only state (PlayerController) is left out on purpose.
/// Register your own game components with ComponentRegistry.Register&lt;T&gt;("name").
/// </summary>
public static class ComponentRegistry
{
    static readonly Dictionary<string, Type> ByName = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<Type, string> ByType = new();

    static ComponentRegistry()
    {
        Register<EntityName>("name");
        Register<Transform>("transform");
        Register<Velocity>("velocity");
        Register<Camera>("camera");
        Register<Model>("model");
        Register<PointLight>("pointLight");
        Register<PlayerSpawn>("playerSpawn");
        Register<EntityProperties>("properties");
        Register<TriggerVolume>("triggerVolume");
    }

    public static void Register<T>(string name) where T : struct
    {
        ByName[name] = typeof(T);
        ByType[typeof(T)] = name;
    }

    public static bool TryGetType(string name, out Type type) => ByName.TryGetValue(name, out type!);
    public static bool TryGetName(Type type, out string name) => ByType.TryGetValue(type, out name!);
}
