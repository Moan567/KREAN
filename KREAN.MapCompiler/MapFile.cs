using System.Globalization;
using System.Numerics;

namespace KREAN.MapCompiler;

public sealed class MapEntity
{
    public Dictionary<string, string> Properties { get; } = new();
    public List<MapBrush> Brushes { get; } = new();
    public string ClassName => Properties.GetValueOrDefault("classname", "");
}

public sealed class MapBrush
{
    public List<MapFace> Faces { get; } = new();
}

public sealed class MapFace
{
    public Vector3 P1, P2, P3;
    public string Texture = "";

    // Valve 220 projection axes (ignored for Standard format)
    public bool IsValve;
    public Vector3 UAxis, VAxis;

    public float UOffset, VOffset, Rotation;
    public float ScaleX = 1f, ScaleY = 1f;

    // Plane (Quake space). Interior of the brush is dot(Normal, p) <= Distance.
    public Vector3 Normal;
    public float Distance;
    public bool Valid;

    public void ComputePlane()
    {
        // Quake winding: normal points out of the brush.
        var n = Vector3.Cross(P3 - P1, P2 - P1);
        if (n.LengthSquared() < 1e-8f) { Valid = false; return; }
        Normal = Vector3.Normalize(n);
        Distance = Vector3.Dot(Normal, P1);
        Valid = true;
    }
}

/// <summary>Parses TrenchBroom / Quake .map files (Standard and Valve 220 formats). Patches are not supported.</summary>
public static class MapParser
{
    readonly record struct Token(string Text, bool Quoted);

    sealed class TokenReader
    {
        readonly List<Token> _tokens;
        int _i;

        public TokenReader(List<Token> tokens) => _tokens = tokens;

        public bool End => _i >= _tokens.Count;

        public Token Peek()
        {
            if (End) throw new FormatException("Unexpected end of .map file");
            return _tokens[_i];
        }

        public Token Next()
        {
            if (End) throw new FormatException("Unexpected end of .map file");
            return _tokens[_i++];
        }

        public bool PeekIs(string symbol) => !End && !_tokens[_i].Quoted && _tokens[_i].Text == symbol;

        public void Expect(string symbol)
        {
            var t = Next();
            if (t.Quoted || t.Text != symbol)
                throw new FormatException($"Expected '{symbol}' but found '{t.Text}'");
        }

        public float Float() => float.Parse(Next().Text, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    public static List<MapEntity> ParseFile(string path) => Parse(File.ReadAllText(path));

    public static List<MapEntity> Parse(string text)
    {
        var reader = new TokenReader(Tokenize(text));
        var entities = new List<MapEntity>();
        while (!reader.End)
        {
            reader.Expect("{");
            entities.Add(ParseEntity(reader));
        }
        return entities;
    }

    static MapEntity ParseEntity(TokenReader r)
    {
        var entity = new MapEntity();
        while (true)
        {
            if (r.PeekIs("}")) { r.Next(); return entity; }

            if (r.PeekIs("{")) { r.Next(); entity.Brushes.Add(ParseBrush(r)); continue; }

            var key = r.Next();
            if (!key.Quoted) throw new FormatException($"Unexpected token '{key.Text}' in entity");
            var value = r.Next();
            entity.Properties[key.Text] = value.Text;
        }
    }

    static MapBrush ParseBrush(TokenReader r)
    {
        var brush = new MapBrush();
        while (true)
        {
            if (r.PeekIs("}")) { r.Next(); return brush; }

            if (r.PeekIs("("))
            {
                var face = ParseFace(r);
                if (face.Valid) brush.Faces.Add(face);
                continue;
            }

            throw new NotSupportedException(
                $"Unsupported brush syntax near '{r.Peek().Text}' (patches / brushDef are not supported).");
        }
    }

    static MapFace ParseFace(TokenReader r)
    {
        var f = new MapFace
        {
            P1 = ReadPoint(r),
            P2 = ReadPoint(r),
            P3 = ReadPoint(r),
            Texture = r.Next().Text
        };

        if (r.PeekIs("["))
        {
            // Valve 220: [ ux uy uz uoff ] [ vx vy vz voff ] rot xscale yscale
            f.IsValve = true;
            r.Expect("[");
            f.UAxis = new Vector3(r.Float(), r.Float(), r.Float());
            f.UOffset = r.Float();
            r.Expect("]");
            r.Expect("[");
            f.VAxis = new Vector3(r.Float(), r.Float(), r.Float());
            f.VOffset = r.Float();
            r.Expect("]");
            f.Rotation = r.Float();
            f.ScaleX = r.Float();
            f.ScaleY = r.Float();
        }
        else
        {
            // Standard: xoff yoff rot xscale yscale
            f.UOffset = r.Float();
            f.VOffset = r.Float();
            f.Rotation = r.Float();
            f.ScaleX = r.Float();
            f.ScaleY = r.Float();
        }

        // Skip anything extra (Quake 2 style content/surface/value numbers).
        while (!r.End && !r.PeekIs("(") && !r.PeekIs("}")) r.Next();

        f.ComputePlane();
        return f;
    }

    static Vector3 ReadPoint(TokenReader r)
    {
        r.Expect("(");
        var v = new Vector3(r.Float(), r.Float(), r.Float());
        r.Expect(")");
        return v;
    }

    static List<Token> Tokenize(string s)
    {
        var list = new List<Token>();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];

            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
                continue;
            }

            if (c == '"')
            {
                int start = ++i;
                while (i < s.Length && s[i] != '"') i++;
                list.Add(new Token(s.Substring(start, i - start), true));
                i++;
                continue;
            }

            if (c is '{' or '}' or '(' or ')' or '[' or ']')
            {
                list.Add(new Token(c.ToString(), false));
                i++;
                continue;
            }

            int st = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) &&
                   s[i] is not ('{' or '}' or '(' or ')' or '[' or ']' or '"'))
                i++;
            list.Add(new Token(s.Substring(st, i - st), false));
        }
        return list;
    }
}
