using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KREAN.Core.Scenes;

/// <summary>Vector3 as [x, y, z] – compact and human-editable.</summary>
public sealed class Vector3Converter : JsonConverter<Vector3>
{
    public override Vector3 Read(ref Utf8JsonReader r, Type type, JsonSerializerOptions o)
    {
        if (r.TokenType != JsonTokenType.StartArray) throw new JsonException("Vector3 must be [x,y,z]");
        r.Read(); float x = r.GetSingle();
        r.Read(); float y = r.GetSingle();
        r.Read(); float z = r.GetSingle();
        r.Read(); // EndArray
        return new Vector3(x, y, z);
    }

    public override void Write(Utf8JsonWriter w, Vector3 v, JsonSerializerOptions o)
    {
        w.WriteStartArray();
        w.WriteNumberValue(v.X);
        w.WriteNumberValue(v.Y);
        w.WriteNumberValue(v.Z);
        w.WriteEndArray();
    }
}

/// <summary>Quaternion as [x, y, z, w].</summary>
public sealed class QuaternionConverter : JsonConverter<Quaternion>
{
    public override Quaternion Read(ref Utf8JsonReader r, Type type, JsonSerializerOptions o)
    {
        if (r.TokenType != JsonTokenType.StartArray) throw new JsonException("Quaternion must be [x,y,z,w]");
        r.Read(); float x = r.GetSingle();
        r.Read(); float y = r.GetSingle();
        r.Read(); float z = r.GetSingle();
        r.Read(); float w = r.GetSingle();
        r.Read(); // EndArray
        return new Quaternion(x, y, z, w);
    }

    public override void Write(Utf8JsonWriter w, Quaternion q, JsonSerializerOptions o)
    {
        w.WriteStartArray();
        w.WriteNumberValue(q.X);
        w.WriteNumberValue(q.Y);
        w.WriteNumberValue(q.Z);
        w.WriteNumberValue(q.W);
        w.WriteEndArray();
    }
}
