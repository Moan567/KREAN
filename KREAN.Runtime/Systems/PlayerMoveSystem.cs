using System.Numerics;
using KREAN.Core;
using KREAN.Core.Components;
using KREAN.Core.ECS;
using KREAN.Core.Physics;
using Silk.NET.Input;

namespace KREAN.Runtime.Systems;

/// <summary>
/// Quake / Half-Life style movement: ground friction, air strafing (bunny-hop friendly),
/// swept-box collision with wall sliding and stair stepping. Constants are in Quake units, converted to metres.
/// </summary>
public sealed class PlayerMoveSystem : ISystem
{
    const float U = Units.QuakeToMeters;

    public const float MaxSpeed = 320f * U;
    public const float Gravity = 800f * U;
    public const float JumpSpeed = 270f * U;
    public const float StopSpeed = 100f * U;
    public const float AirWishCap = 30f * U;
    public const float StepHeight = 18f * U;
    public const float Accel = 10f;
    public const float AirAccel = 10f;
    public const float Friction = 4f;

    const float MaxStep = 1f / 125f;   // physics sub-step (seconds)
    const int MaxBumps = 4;

    /// <summary>Player hull: 32 wide, 56 tall (Quake). Origin is the box centre.</summary>
    public static readonly Vector3 Half = new(16f * U, 28f * U, 16f * U);

    public float Sensitivity { get; set; } = 0.09f;   // degrees per mouse count

    readonly CollisionWorld _collision;
    readonly InputState _input;

    public PlayerMoveSystem(CollisionWorld collision, InputState input)
    {
        _collision = collision;
        _input = input;
    }

    public void Update(World world, float dt)
    {
        world.Query<Transform, Velocity, PlayerController>(
            (Entity e, ref Transform t, ref Velocity v, ref PlayerController pc) =>
            {
                // --- look
                if (_input.MouseCaptured)
                {
                    pc.Yaw -= _input.MouseDelta.X * Sensitivity;
                    pc.Pitch = Math.Clamp(pc.Pitch - _input.MouseDelta.Y * Sensitivity, -89f, 89f);
                }
                if (_input.WasPressed(Key.V)) pc.NoClip = !pc.NoClip;
                t.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, pc.Yaw * Units.Deg2Rad);

                // --- input
                float fmove = (_input.IsDown(Key.W) ? 1f : 0f) - (_input.IsDown(Key.S) ? 1f : 0f);
                float smove = (_input.IsDown(Key.D) ? 1f : 0f) - (_input.IsDown(Key.A) ? 1f : 0f);
                bool jump = _input.IsDown(Key.Space);
                float rise = (_input.IsDown(Key.Space) ? 1f : 0f) - (_input.IsDown(Key.ControlLeft) ? 1f : 0f);

                // --- move (sub-stepped so low frame rates don't tunnel or change jump height much)
                int steps = Math.Max(1, (int)MathF.Ceiling(dt / MaxStep));
                float h = dt / steps;

                var pos = t.Position;
                var vel = v.Linear;

                for (int i = 0; i < steps; i++)
                {
                    if (pc.NoClip) FlyMove(ref pos, ref vel, pc, fmove, smove, rise, h);
                    else WalkMove(ref pos, ref vel, ref pc, fmove, smove, jump, h);
                }

                t.Position = pos;
                v.Linear = vel;
            });
    }

    // ------------------------------------------------------------------ walking

    void WalkMove(ref Vector3 pos, ref Vector3 vel, ref PlayerController pc,
                  float fmove, float smove, bool jump, float dt)
    {
        float yaw = pc.Yaw * Units.Deg2Rad;
        var forward = new Vector3(MathF.Cos(yaw), 0f, -MathF.Sin(yaw));
        var right = new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));

        var wish = forward * fmove + right * smove;
        float wishSpeed = MathF.Min(wish.Length(), 1f) * MaxSpeed;
        var wishDir = wish.LengthSquared() > 1e-6f ? Vector3.Normalize(wish) : Vector3.Zero;

        pc.Grounded = CheckGround(pos, vel);

        if (pc.Grounded && jump)
        {
            vel.Y = JumpSpeed;
            pc.Grounded = false;
        }

        if (pc.Grounded)
        {
            ApplyFriction(ref vel, dt);
            Accelerate(ref vel, wishDir, wishSpeed, Accel, dt);
            vel.Y = 0f;
        }
        else
        {
            AirAccelerate(ref vel, wishDir, wishSpeed, AirAccel, dt);
            vel.Y -= Gravity * dt;
        }

        StepSlideMove(ref pos, ref vel, dt, pc.Grounded);
    }

    bool CheckGround(Vector3 pos, Vector3 vel)
    {
        if (vel.Y > 180f * U) return false;   // launching upwards
        var tr = _collision.Trace(pos, pos - Vector3.UnitY * (2f * U), Half);
        return !tr.AllSolid && tr.Fraction < 1f && tr.Normal.Y >= 0.7f;
    }

    static void ApplyFriction(ref Vector3 vel, float dt)
    {
        float speed = MathF.Sqrt(vel.X * vel.X + vel.Z * vel.Z);
        if (speed < 1e-4f) { vel.X = 0f; vel.Z = 0f; return; }

        float control = MathF.Max(speed, StopSpeed);
        float drop = control * Friction * dt;
        float scale = MathF.Max(speed - drop, 0f) / speed;
        vel.X *= scale;
        vel.Z *= scale;
    }

    static void Accelerate(ref Vector3 vel, Vector3 wishDir, float wishSpeed, float accel, float dt)
    {
        float add = wishSpeed - Vector3.Dot(vel, wishDir);
        if (add <= 0f) return;
        float accelSpeed = MathF.Min(accel * wishSpeed * dt, add);
        vel += wishDir * accelSpeed;
    }

    // Same as Accelerate, but the "current speed" test uses a tiny cap – this is what makes strafe-jumping work.
    static void AirAccelerate(ref Vector3 vel, Vector3 wishDir, float wishSpeed, float accel, float dt)
    {
        float capped = MathF.Min(wishSpeed, AirWishCap);
        float add = capped - Vector3.Dot(vel, wishDir);
        if (add <= 0f) return;
        float accelSpeed = MathF.Min(accel * wishSpeed * dt, add);
        vel += wishDir * accelSpeed;
    }

    // ------------------------------------------------------------------ collision response

    void StepSlideMove(ref Vector3 pos, ref Vector3 vel, float dt, bool grounded)
    {
        var startPos = pos;
        var startVel = vel;

        bool hit = SlideMove(ref pos, ref vel, dt);
        if (!hit || !grounded) return;

        var downPos = pos;
        var downVel = vel;

        // Try again from a raised position (stairs / small ledges).
        pos = startPos;
        vel = startVel;

        var up = _collision.Trace(pos, pos + Vector3.UnitY * StepHeight, Half);
        if (up.AllSolid) { pos = downPos; vel = downVel; return; }

        float stepped = up.EndPos.Y - startPos.Y;
        pos = up.EndPos;

        SlideMove(ref pos, ref vel, dt);

        var down = _collision.Trace(pos, pos - Vector3.UnitY * stepped, Half);
        if (!down.AllSolid) pos = down.EndPos;

        // Landed on something too steep to stand on: keep the plain slide result.
        if (down.Fraction < 1f && down.Normal.Y < 0.7f) { pos = downPos; vel = downVel; return; }

        float distDown = HorizontalDistSq(downPos - startPos);
        float distStep = HorizontalDistSq(pos - startPos);
        if (distStep <= distDown + 1e-7f) { pos = downPos; vel = downVel; return; }

        if (down.Fraction < 1f) vel = ClipVelocity(vel, down.Normal);
    }

    /// <summary>Quake's SV_FlyMove: slide along up to a few surfaces. Returns true if anything was hit.</summary>
    bool SlideMove(ref Vector3 pos, ref Vector3 vel, float dt)
    {
        var original = vel;
        var primal = vel;
        float timeLeft = dt;
        bool hitAnything = false;

        Span<Vector3> planes = stackalloc Vector3[5];
        int numPlanes = 0;

        for (int bump = 0; bump < MaxBumps; bump++)
        {
            if (vel.LengthSquared() < 1e-9f) break;

            var end = pos + vel * timeLeft;
            var tr = _collision.Trace(pos, end, Half);

            if (tr.AllSolid) { vel = Vector3.Zero; break; }   // stuck inside geometry

            if (tr.Fraction > 0f) pos = tr.EndPos;
            if (tr.Fraction >= 1f) break;

            hitAnything = true;
            timeLeft -= timeLeft * tr.Fraction;

            if (numPlanes >= planes.Length) { vel = Vector3.Zero; break; }
            planes[numPlanes++] = tr.Normal;

            // Find a velocity that doesn't run into any of the planes we've touched.
            Vector3 newVel = default;
            int i, j = 0;
            for (i = 0; i < numPlanes; i++)
            {
                newVel = ClipVelocity(original, planes[i]);
                for (j = 0; j < numPlanes; j++)
                    if (j != i && Vector3.Dot(newVel, planes[j]) < 0f) break;
                if (j == numPlanes) break;
            }

            if (i != numPlanes)
            {
                vel = newVel;
            }
            else
            {
                // Wedged in a corner: slide along the crease.
                if (numPlanes != 2) { vel = Vector3.Zero; break; }
                var dir = Vector3.Cross(planes[0], planes[1]);
                if (dir.LengthSquared() < 1e-8f) { vel = Vector3.Zero; break; }
                dir = Vector3.Normalize(dir);
                vel = dir * Vector3.Dot(dir, vel);
            }

            // Don't oscillate in sloped corners.
            if (Vector3.Dot(vel, primal) <= 0f) { vel = Vector3.Zero; break; }
        }

        return hitAnything;
    }

    static Vector3 ClipVelocity(Vector3 vel, Vector3 normal)
    {
        float backoff = Vector3.Dot(vel, normal);
        backoff = backoff < 0f ? backoff * 1.001f : backoff / 1.001f;
        return vel - normal * backoff;
    }

    static float HorizontalDistSq(Vector3 d) => d.X * d.X + d.Z * d.Z;

    // ------------------------------------------------------------------ noclip / editor fly camera

    static void FlyMove(ref Vector3 pos, ref Vector3 vel, in PlayerController pc,
                        float fmove, float smove, float rise, float dt)
    {
        float yaw = pc.Yaw * Units.Deg2Rad;
        var look = PlayerController.LookDirection(pc.Yaw, pc.Pitch);
        var right = new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));

        var wish = look * fmove + right * smove + Vector3.UnitY * rise;
        if (wish.LengthSquared() > 1f) wish = Vector3.Normalize(wish);

        pos += wish * (MaxSpeed * 1.5f) * dt;
        vel = Vector3.Zero;
    }
}
