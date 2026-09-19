using System.Numerics;

namespace KREAN.Core.Components;

// Rule of thumb: components are plain data (structs). Behaviour lives in systems.
// Serialized components must have public FIELDS (System.Text.Json is configured with IncludeFields).

public struct EntityName
{
    public string Value;
}

public struct Transform
{
    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 Scale;

    public static Transform Identity => new()
    {
        Position = Vector3.Zero,
        Rotation = Quaternion.Identity,
        Scale = Vector3.One
    };

    public readonly Matrix4x4 ToMatrix() =>
        Matrix4x4.CreateScale(Scale) *
        Matrix4x4.CreateFromQuaternion(Rotation) *
        Matrix4x4.CreateTranslation(Position);
}

public struct Velocity
{
    public Vector3 Linear;
}

public struct Camera
{
    public float FovDegrees;
    public float Near;
    public float Far;
}

/// <summary>Runtime state of the first-person player. Not serialized.</summary>
public struct PlayerController
{
    public float Yaw;        // degrees, rotation around +Y (0 = facing +X)
    public float Pitch;      // degrees, +up
    public float EyeOffset;  // metres above the collision-box centre
    public bool Grounded;
    public bool NoClip;

    public static Vector3 LookDirection(float yawDeg, float pitchDeg)
    {
        float y = yawDeg * Units.Deg2Rad;
        float p = pitchDeg * Units.Deg2Rad;
        float cp = MathF.Cos(p);
        return new Vector3(cp * MathF.Cos(y), MathF.Sin(p), -cp * MathF.Sin(y));
    }
}

/// <summary>References compiled meshes stored in the scene file by id.</summary>
public struct Model
{
    public string[] Meshes;
}

public struct PointLight
{
    public Vector3 Color;
    public float Intensity;
    public float Range;   // metres
}

public struct PlayerSpawn
{
    public float Yaw;
}

/// <summary>Raw key/value pairs from the .map entity (targetname, target, custom keys...).</summary>
public struct EntityProperties
{
    public Dictionary<string, string> Values;
}

/// <summary>Axis-aligned volume from a trigger_* brush entity (engine space).</summary>
public struct TriggerVolume
{
    public Vector3 Min;
    public Vector3 Max;
}

/// <summary>Tag: entity is created by the runtime and must never be saved into a scene.</summary>
public struct Transient
{
}
