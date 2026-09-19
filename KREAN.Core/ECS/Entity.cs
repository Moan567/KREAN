namespace KREAN.Core.ECS;

/// <summary>Lightweight handle. Generation makes stale handles detectable after an entity is destroyed.</summary>
public readonly record struct Entity(int Id, int Generation)
{
    public static readonly Entity Null = new(-1, 0);
    public bool IsNull => Id < 0;
    public override string ToString() => IsNull ? "Entity(null)" : $"Entity({Id}v{Generation})";
}
