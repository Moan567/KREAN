using System.Numerics;
using KREAN.Core;
using KREAN.Core.Components;
using KREAN.Core.ECS;
using KREAN.Runtime;
using Silk.NET.Input;

namespace KREAN.Editor;

/// <summary>Professional FPS fly camera — Hold RMB to mouselook, WASD move, Q/Z or Space for vertical, Shift sprint, Alt slow.</summary>
public sealed class EditorCameraController
{
    public float BaseSpeed { get; set; } = 4.0f; // m/s
    public float SprintMultiplier { get; set; } = 3.0f;
    public float SlowMultiplier { get; set; } = 0.3f;
    public float Sensitivity { get; set; } = 0.14f; // deg per pixel
    public float Smoothing { get; set; } = 18f;
    public float WheelSpeedStep { get; set; } = 0.35f;

    Vector3 _velSmoothed;
    float _yaw, _pitch;

    public void SetFromPlayer(Entity player, World world)
    {
        if (!world.IsAlive(player)) return;
        var pc = world.Get<PlayerController>(player);
        _yaw = pc.Yaw;
        _pitch = pc.Pitch;
    }

    public void FocusOn(Vector3 targetEngine, Entity player, World world, float distance = 6f)
    {
        if (!world.IsAlive(player)) return;
        ref var tr = ref world.Get<Transform>(player);
        var pc = world.Get<PlayerController>(player);
        // place camera back along view dir
        var fwd = PlayerController.LookDirection(pc.Yaw, pc.Pitch);
        tr.Position = targetEngine - fwd * distance;
        tr.Position.Y += 1.0f; // lift a bit
        _velSmoothed = Vector3.Zero;
    }

    public void Update(World world, Entity player, InputState input, float dt, bool lookActive, bool allowFlyKeys = true)
    {
        if (!world.IsAlive(player)) return;
        ref var tr = ref world.Get<Transform>(player);
        ref var pc = ref world.Get<PlayerController>(player);

        // look
        if (lookActive)
        {
            _yaw -= input.MouseDelta.X * Sensitivity;
            _pitch = Math.Clamp(_pitch - input.MouseDelta.Y * Sensitivity, -89f, 89f);
        }
        else
        {
            // sync from pc if externally changed (e.g. load)
            _yaw = pc.Yaw;
            _pitch = pc.Pitch;
        }
        pc.Yaw = _yaw;
        pc.Pitch = _pitch;
        tr.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, _yaw * Units.Deg2Rad);

        bool flyKey = allowFlyKeys && IsFlyKeyDown(input);
        if (!lookActive && !flyKey) 
        {
            // damping when not flying
            _velSmoothed = Vector3.Lerp(_velSmoothed, Vector3.Zero, Math.Clamp(dt * 8f, 0, 1));
            return;
        }

        // input axes — Z for down (not Ctrl), Q also down for legacy, E/Space up
        float fwd = (input.IsDown(Key.W) ? 1 : 0) - (input.IsDown(Key.S) ? 1 : 0);
        float strafe = (input.IsDown(Key.D) ? 1 : 0) - (input.IsDown(Key.A) ? 1 : 0);
        float up = 0;
        if (input.IsDown(Key.E) || input.IsDown(Key.Space)) up += 1;
        if (input.IsDown(Key.Q) || input.IsDown(Key.Z)) up -= 1;

        // speed modifiers
        float speed = BaseSpeed;
        bool shift = input.IsDown(Key.ShiftLeft) || input.IsDown(Key.ShiftRight);
        bool slow = input.IsDown(Key.AltLeft) || input.IsDown(Key.AltRight);
        if (shift) speed *= SprintMultiplier;
        if (slow) speed *= SlowMultiplier;

        // wish direction in world space
        var lookDir = PlayerController.LookDirection(_yaw, _pitch);
        var right = Vector3.Normalize(Vector3.Cross(lookDir, Vector3.UnitY));
        // for fly, forward is lookDir projected? No, use full lookDir for true fly
        Vector3 wish = lookDir * fwd + right * strafe + Vector3.UnitY * up;
        if (wish.LengthSquared() > 1e-6f) wish = Vector3.Normalize(wish);
        else wish = Vector3.Zero;

        Vector3 targetVel = wish * speed;
        // smoothing
        float lerp = Math.Clamp(dt * Smoothing, 0, 1);
        _velSmoothed = Vector3.Lerp(_velSmoothed, targetVel, lerp);
        tr.Position += _velSmoothed * dt;
    }

    public void Freeze() => _velSmoothed = Vector3.Zero;

    public void AdjustSpeed(float wheelDelta)
    {
        BaseSpeed = Math.Clamp(BaseSpeed + wheelDelta * WheelSpeedStep, 0.5f, 25f);
    }

    static bool IsFlyKeyDown(InputState input) =>
        input.IsDown(Key.W) || input.IsDown(Key.S) || input.IsDown(Key.A) || input.IsDown(Key.D) ||
        input.IsDown(Key.Q) || input.IsDown(Key.Z) || input.IsDown(Key.E) || input.IsDown(Key.Space);
}
