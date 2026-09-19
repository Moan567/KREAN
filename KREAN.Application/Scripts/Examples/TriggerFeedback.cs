using System;
using System.Numerics;
using KREAN.Application.Scripting;
using KREAN.Core.Components;
using KREAN.Core.ECS;

/// <summary>
/// Example entity script: when player enters any TriggerVolume, prints to console.
/// Shows how to read trigger_* brush volumes that were stored from .map compile.
/// Attach via trigger_* entities compiled from .map (they carry TriggerVolume component).
/// </summary>
public sealed class TriggerFeedbackSystem : IGameScript
{
    float _cooldown;

    public void Update(World world, float dt)
    {
        _cooldown -= dt;
        // find player
        Vector3 playerPos = default;
        bool found = false;
        world.Query<Transform, PlayerController>((Entity e, ref Transform t, ref PlayerController pc) =>
        {
            playerPos = t.Position;
            found = true;
        });
        if (!found) return;

        world.Query<TriggerVolume, EntityProperties>((Entity e, ref TriggerVolume vol, ref EntityProperties props) =>
        {
            bool inside = playerPos.X >= vol.Min.X && playerPos.X <= vol.Max.X
                       && playerPos.Y >= vol.Min.Y && playerPos.Y <= vol.Max.Y
                       && playerPos.Z >= vol.Min.Z && playerPos.Z <= vol.Max.Z;
            if (inside && _cooldown <= 0f)
            {
                string target = props.Values.TryGetValue("target", out var v) ? v : "(no target)";
                Console.WriteLine($"[trigger] player inside {vol.Min}..{vol.Max} target={target}");
                _cooldown = 1.5f;
            }
        });
    }
}
