using System.Numerics;

namespace KREAN.Runtime.Rendering;

/// <summary>Placeholder until real textures exist: every material name gets a stable pastel colour.</summary>
public static class MaterialPalette
{
    static readonly Dictionary<string, Vector3> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Vector3 ColorFor(string material)
    {
        if (Cache.TryGetValue(material, out var cached)) return cached;

        uint h = 2166136261;
        foreach (char c in material) h = (h ^ c) * 16777619u;

        float hue = (h % 360) / 360f;
        var color = HsvToRgb(hue, 0.45f, 0.95f);
        Cache[material] = color;
        return color;
    }

    static Vector3 HsvToRgb(float h, float s, float v)
    {
        float F(float n)
        {
            float k = (n + h * 6f) % 6f;
            return v - v * s * MathF.Max(MathF.Min(MathF.Min(k, 4f - k), 1f), 0f);
        }
        return new Vector3(F(5f), F(3f), F(1f));
    }
}
