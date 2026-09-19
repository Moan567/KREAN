using System;
using System.Numerics;
using KREAN.Application.Scripting;
using KREAN.Core.Components;
using KREAN.Core.ECS;

/// <summary>
/// Example script: spins every entity that has a Model and whose name contains "spin".
/// Drop this file into KREAN.Application/Scripts and it will be compiled automatically.
/// Hot reload is enabled — just save.
/// </summary>
public sealed class SpinSystem : IGameScript
{
    public void Update(World world, float dt)
    {
        world.Query<Transform, Model>((Entity e, ref Transform t, ref Model m) =>
        {
            // Optional filter: only spin if EntityName contains spin
            if (world.Has<EntityName>(e))
            {
                var name = world.Get<EntityName>(e).Value;
                if (!name.Contains("spin", StringComparison.OrdinalIgnoreCase)) return;
            }
            // rotate around Y
            var spin = Quaternion.CreateFromAxisAngle(Vector3.UnitY, dt * 1.2f);
            t.Rotation = spin * t.Rotation;
        });
    }
}
