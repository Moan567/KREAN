using System.Numerics;
using System.Text.Json;

namespace KREAN.Core.Scenes;

/// <summary>The on-disk scene (.scene.json). This is what the runtime loads – never the .map.</summary>
public sealed class SceneData
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Untitled";
    public List<EntityData> Entities { get; set; } = new();
    public List<MeshData> Meshes { get; set; } = new();
    public List<CollisionBrushData> Collision { get; set; } = new();
}

public sealed class EntityData
{
    /// <summary>componentName -> component json (names come from ComponentRegistry).</summary>
    public Dictionary<string, JsonElement> Components { get; set; } = new();
}

public sealed class MeshData
{
    public string Id { get; set; } = "";
    public string Material { get; set; } = "";
    public float[] Positions { get; set; } = Array.Empty<float>();  // xyz
    public float[] Normals { get; set; } = Array.Empty<float>();    // xyz
    public float[] UVs { get; set; } = Array.Empty<float>();        // uv in texels (divide by texture size later)
    public int[] Indices { get; set; } = Array.Empty<int>();
}

/// <summary>Convex brush as planes: flat [nx, ny, nz, d, ...]; inside is dot(n, p) &lt;= d.</summary>
public sealed class CollisionBrushData
{
    public float[] Planes { get; set; } = Array.Empty<float>();
    public Vector3 Min { get; set; }
    public Vector3 Max { get; set; }
}
