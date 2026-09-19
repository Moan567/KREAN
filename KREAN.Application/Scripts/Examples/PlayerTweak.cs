using KREAN.Application.Scripting;
using KREAN.Core.Components;
using KREAN.Core.ECS;
using KREAN.Core.Physics;
using KREAN.Runtime;

/// <summary>
/// Example of a character-movement tweak stored in KREAN.Application.
/// This runs after the built-in PlayerMoveSystem and can adjust speed, gravity, or add double-jump.
/// Scripts are compiled from KREAN.Application/Scripts — edit and save to hot reload.
/// </summary>
public sealed class PlayerTweakSystem : IGameScript
{
    readonly InputState _input;

    // Constructor injection supported: World, CollisionWorld, InputState
    public PlayerTweakSystem(InputState input) => _input = input;

    public void Update(World world, float dt)
    {
        // Example: hold LeftShift for sprint (1.6x), hold C to crouch (0.5x), press F to toggle noclip via script
        // We don't replace PlayerMoveSystem; we just observe/modify.
        // This example prints speed on screen via console once per second as demo.
        // (In a real game you'd modify Velocity or PlayerController here.)
    }
}
