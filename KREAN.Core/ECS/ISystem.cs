namespace KREAN.Core.ECS;

public interface ISystem
{
    /// <summary>Called once after the scene has been loaded.</summary>
    void Init(World world) { }

    /// <summary>Called every frame. dt is in seconds.</summary>
    void Update(World world, float dt);
}
