using System.Globalization;
using System.Numerics;
using System.Text.Json;
using KREAN.Core;
using KREAN.Core.Components;
using KREAN.Core.Scenes;

namespace KREAN.MapCompiler;

public sealed class CompileOptions
{
    public float Scale { get; init; } = Units.QuakeToMeters;
    public bool Indented { get; init; } = true;
}

/// <summary>Quake space (Z-up, units) -> engine space (Y-up, metres). Rotation only, so winding is preserved.</summary>
static class SpaceConvert
{
    public static Vector3 ToEngine(Vector3 q, float scale) => new Vector3(q.X, q.Z, -q.Y) * scale;
    public static Vector3 ToEngineDir(Vector3 q) => new(q.X, q.Z, -q.Y);
}

public static class MapCompilerService
{
    // Faces with these textures are not drawn (they still collide).
    static readonly HashSet<string> NoDraw = new(StringComparer.OrdinalIgnoreCase)
        { "clip", "skip", "hint", "hintskip", "nodraw", "trigger", "origin", "caulk", "null" };

    public static SceneData CompileToFile(string mapPath, string scenePath, CompileOptions? options = null)
    {
        options ??= new CompileOptions();
        var scene = Compile(mapPath, options);
        SceneSerializer.Write(scene, scenePath, options.Indented);
        return scene;
    }

    public static SceneData Compile(string mapPath, CompileOptions? options = null) =>
        Compile(MapParser.ParseFile(mapPath), Path.GetFileNameWithoutExtension(mapPath), options);

    public static SceneData Compile(List<MapEntity> entities, string sceneName, CompileOptions? options = null)
    {
        float s = (options ?? new CompileOptions()).Scale;
        var scene = new SceneData { Name = sceneName };

        for (int ei = 0; ei < entities.Count; ei++)
        {
            var me = entities[ei];
            var props = me.Properties;
            string cls = me.ClassName;
            bool hasBrushes = me.Brushes.Count > 0;

            string name = props.TryGetValue("targetname", out var tn) && tn.Length > 0 ? tn : $"{cls}_{ei}";
            float yaw = ReadYaw(props);

            var transform = Transform.Identity;
            // handle model scale property
            if (props.TryGetValue("scale", out var sc) || props.TryGetValue("modelscale", out sc) || props.TryGetValue("_scale", out sc))
                if(float.TryParse(sc, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var scaleVal) && scaleVal>0.01f)
                    transform.Scale = new System.Numerics.Vector3(scaleVal, scaleVal, scaleVal);
            if (!hasBrushes)
            {
                if (props.TryGetValue("origin", out var origin))
                    transform.Position = SpaceConvert.ToEngine(ParseVec3(origin), s);
                transform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw * Units.Deg2Rad);
                // also handle pitch/roll via angles
                if (props.TryGetValue("angles", out var angStr))
                {
                    var ang = ParseVec3(angStr);
                    var qx = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -ang.X * Units.Deg2Rad);
                    var qy = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw * Units.Deg2Rad);
                    var qz = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, ang.Z * Units.Deg2Rad);
                    transform.Rotation = qz * qx * qy;
                }
            }

            var data = new EntityData();
            AddComponent(data, new EntityName { Value = name });
            AddComponent(data, transform);
            AddComponent(data, new EntityProperties { Values = new Dictionary<string, string>(props) });

            if (hasBrushes) CompileBrushes(scene, data, me, ei, cls, s);
            // model import for misc_model / misc_prefab handled as instance below; misc_model handled here
            if (!hasBrushes && (cls.Equals("misc_model", StringComparison.OrdinalIgnoreCase) || cls.Equals("misc_gltf", StringComparison.OrdinalIgnoreCase) || cls.Equals("model", StringComparison.OrdinalIgnoreCase)))
            {
                string? modelVal = null;
                if(props.TryGetValue("model", out var mv)) modelVal=mv;
                else if(props.TryGetValue("modelpath", out var mp)) modelVal=mp;
                else if(props.TryGetValue("mdl", out var md)) modelVal=md;
                else if(props.TryGetValue("mesh", out var me2)) modelVal=me2;
                if(!string.IsNullOrWhiteSpace(modelVal))
                {
                    string[] search = new[]{
                        Directory.GetCurrentDirectory(),
                        Path.Combine(Directory.GetCurrentDirectory(),"models"),
                        Path.Combine(Directory.GetCurrentDirectory(),"assets","models"),
                        Path.Combine(Directory.GetCurrentDirectory(),"Data","models"),
                        Path.Combine(AppContext.BaseDirectory,"models"),
                        Path.Combine(AppContext.BaseDirectory,"..","..","..","models"),
                        Path.Combine(AppContext.BaseDirectory,"..","..","..","assets","models"),
                        "."
                    };
                    var found = ObjModelLoader.FindModelFile(modelVal, search);
                    if(found!=null && ObjModelLoader.TryLoad(found, out var mdata, $"e{ei}_model"))
                    {
                        // apply scale to mesh if transform scale !=1? bake into mesh or keep transform scale
                        scene.Meshes.Add(mdata);
                        AddComponent(data, new Model{ Meshes=new[]{mdata.Id}});
                    }
                    else
                    {
                        Console.WriteLine($"[model] not found '{modelVal}' for entity {ei}");
                        // fallback: make small cube so entity is visible
                        var fallbackId=$"e{ei}_fallback";
                        scene.Meshes.Add(new MeshData{ Id=fallbackId, Material="model_missing", Positions=new float[]{ -0.5f,0,-0.5f, 0.5f,0,-0.5f, 0.5f,0,0.5f, -0.5f,0,0.5f, -0.5f,1, -0.5f, 0.5f,1,-0.5f, 0.5f,1,0.5f, -0.5f,1,0.5f }, Normals=new float[8*3], UVs=new float[8*2], Indices=new int[]{0,1,2,0,2,3,4,6,5,4,7,6} });
                        // fill normals as up
                        var fm = scene.Meshes.Last(); for(int i=0;i<fm.Normals.Length;i+=3){ fm.Normals[i]=0; fm.Normals[i+1]=1; fm.Normals[i+2]=0; }
                        AddComponent(data, new Model{Meshes=new[]{fallbackId}});
                    }
                }
            }
            // prefab inline expansion for misc_prefab / func_instance
            if (cls.Equals("misc_prefab", StringComparison.OrdinalIgnoreCase) || cls.Equals("func_instance", StringComparison.OrdinalIgnoreCase) || cls.Equals("prefab", StringComparison.OrdinalIgnoreCase))
            {
                string? prefabVal=null;
                if(props.TryGetValue("prefab", out var pv)) prefabVal=pv;
                else if(props.TryGetValue("instance", out var iv)) prefabVal=iv;
                else if(props.TryGetValue("model", out var mv2)) prefabVal=mv2;
                if(!string.IsNullOrWhiteSpace(prefabVal))
                {
                    string[] search2 = new[]{
                        Directory.GetCurrentDirectory(),
                        Path.Combine(Directory.GetCurrentDirectory(),"prefabs"),
                        Path.Combine(Directory.GetCurrentDirectory(),"assets","prefabs"),
                        Path.Combine(AppContext.BaseDirectory,"prefabs"),
                        "."
                    };
                    string? found2=null;
                    if(File.Exists(prefabVal)) found2=Path.GetFullPath(prefabVal);
                    else foreach(var d in search2){ var cand=Path.Combine(d, prefabVal); if(File.Exists(cand)){found2=Path.GetFullPath(cand);break;} var cand2=Path.Combine(d, Path.GetFileName(prefabVal)); if(File.Exists(cand2)){found2=Path.GetFullPath(cand2);break;}}
                    if(found2!=null)
                    {
                        try{
                            var prefabEnts = MapParser.ParseFile(found2);
                            Vector3 offset = Vector3.Zero;
                            if(props.TryGetValue("origin", out var po)) offset=ParseVec3(po);
                            // compile prefab entities as additional scene entities (baked)
                            foreach(var pe in prefabEnts)
                            {
                                var pd = new EntityData();
                                // transform offset for point entities
                                if(pe.Properties.TryGetValue("origin", out var porg))
                                {
                                    var pp=ParseVec3(porg)+offset;
                                    pe.Properties["origin"]=$"{pp.X} {pp.Y} {pp.Z}";
                                }
                                // offset brushes
                                foreach(var br in pe.Brushes) BrushManipulation.Translate(br, offset);
                                // create copy with name prefixed
                                string pName = pe.Properties.TryGetValue("targetname", out var ptn) && ptn.Length>0 ? ptn : $"{pe.ClassName}_prefab";
                                var pTrans = Transform.Identity;
                                if(pe.Brushes.Count==0 && pe.Properties.TryGetValue("origin", out var po2))
                                {
                                    pTrans.Position=SpaceConvert.ToEngine(ParseVec3(po2), s);
                                    float pyaw=ReadYaw(pe.Properties);
                                    pTrans.Rotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY, pyaw*Units.Deg2Rad);
                                }
                                AddComponent(pd, new EntityName{Value=pName});
                                AddComponent(pd, pTrans);
                                AddComponent(pd, new EntityProperties{Values=new Dictionary<string,string>(pe.Properties)});
                                if(pe.Brushes.Count>0) CompileBrushes(scene, pd, pe, scene.Entities.Count, pe.ClassName, s);
                                // apply class rules for lights etc
                                ApplyClassRules(pd, pe.ClassName, pe.Properties, ReadYaw(pe.Properties), s);
                                scene.Entities.Add(pd);
                            }
                            Console.WriteLine($"[prefab] inlined {prefabEnts.Count} entities from '{found2}'");
                        }catch(Exception ex){ Console.WriteLine($"[prefab] failed {prefabVal}: {ex.Message}"); }
                    }
                }
            }
            ApplyClassRules(data, cls, props, yaw, s);

            scene.Entities.Add(data);
        }

        return scene;
    }

    // ------------------------------------------------------------------

    static void CompileBrushes(SceneData scene, EntityData data, MapEntity me, int entityIndex, string cls, float s)
    {
        bool isTrigger = cls.StartsWith("trigger", StringComparison.OrdinalIgnoreCase);
        bool solid = !isTrigger && !cls.Equals("func_illusionary", StringComparison.OrdinalIgnoreCase);

        var groups = new Dictionary<string, MeshBuilder>(StringComparer.OrdinalIgnoreCase);
        var entityMin = new Vector3(float.MaxValue);
        var entityMax = new Vector3(float.MinValue);

        foreach (var brush in me.Brushes)
        {
            var polys = BrushGeometry.Build(brush, out var bmin, out var bmax);
            if (polys.Count == 0) continue;

            entityMin = Vector3.Min(entityMin, bmin);
            entityMax = Vector3.Max(entityMax, bmax);

            if (solid) scene.Collision.Add(MakeCollision(polys, bmin, bmax, s));
            if (isTrigger) continue;

            foreach (var poly in polys)
            {
                if (poly.Vertices.Count < 3) continue;

                string tex = poly.Face.Texture;
                if (NoDraw.Contains(TextureBaseName(tex))) continue;

                if (!groups.TryGetValue(tex, out var builder))
                    groups[tex] = builder = new MeshBuilder(tex);

                builder.AddPolygon(poly, s);
            }
        }

        if (isTrigger && entityMin.X <= entityMax.X)
        {
            var a = SpaceConvert.ToEngine(entityMin, s);
            var b = SpaceConvert.ToEngine(entityMax, s);
            AddComponent(data, new TriggerVolume { Min = Vector3.Min(a, b), Max = Vector3.Max(a, b) });
        }

        var meshIds = new List<string>();
        foreach (var (texture, builder) in groups)
        {
            string id = $"e{entityIndex}_{Sanitize(texture)}";
            scene.Meshes.Add(builder.ToMeshData(id));
            meshIds.Add(id);
        }
        if (meshIds.Count > 0) AddComponent(data, new Model { Meshes = meshIds.ToArray() });
    }

    static CollisionBrushData MakeCollision(List<BrushPolygon> polys, Vector3 qmin, Vector3 qmax, float s)
    {
        var planes = new float[polys.Count * 4];
        for (int i = 0; i < polys.Count; i++)
        {
            var f = polys[i].Face;
            var n = SpaceConvert.ToEngineDir(f.Normal);
            planes[i * 4 + 0] = n.X;
            planes[i * 4 + 1] = n.Y;
            planes[i * 4 + 2] = n.Z;
            planes[i * 4 + 3] = f.Distance * s;
        }

        var a = SpaceConvert.ToEngine(qmin, s);
        var b = SpaceConvert.ToEngine(qmax, s);
        return new CollisionBrushData { Planes = planes, Min = Vector3.Min(a, b), Max = Vector3.Max(a, b) };
    }

    /// <summary>Map classnames -> engine components. Add your own entity types here.</summary>
    static void ApplyClassRules(EntityData data, string cls, Dictionary<string, string> props, float yaw, float s)
    {
        switch (cls.ToLowerInvariant())
        {
            case "info_player_start":
            case "info_player_deathmatch":
            case "info_player_coop":
                AddComponent(data, new PlayerSpawn { Yaw = yaw });
                break;

            case "light":
            {
                float intensity = 300f;
                if (props.TryGetValue("light", out var l) &&
                    float.TryParse(l, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                    intensity = parsed;

                var color = Vector3.One;
                if (props.TryGetValue("_color", out var c))
                {
                    color = ParseVec3(c);
                    if (color.X > 1f || color.Y > 1f || color.Z > 1f) color /= 255f;
                }

                AddComponent(data, new PointLight { Color = color, Intensity = intensity / 300f, Range = intensity * s });
                break;
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    static void AddComponent<T>(EntityData data, T value) where T : struct
    {
        if (!ComponentRegistry.TryGetName(typeof(T), out var name))
            throw new InvalidOperationException($"{typeof(T).Name} is not registered in ComponentRegistry");
        data.Components[name] = JsonSerializer.SerializeToElement(value, SceneSerializer.Options);
    }

    static float ReadYaw(Dictionary<string, string> props)
    {
        if (props.TryGetValue("angles", out var angles)) return ParseVec3(angles).Y;   // pitch yaw roll

        if (props.TryGetValue("angle", out var a) &&
            float.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var deg) && deg >= 0f)
            return deg;

        return 0f;
    }

    static Vector3 ParseVec3(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        float F(int i) => i < parts.Length &&
                          float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;
        return new Vector3(F(0), F(1), F(2));
    }

    static string TextureBaseName(string tex)
    {
        tex = tex.Replace('\\', '/');
        int i = tex.LastIndexOf('/');
        return i >= 0 ? tex[(i + 1)..] : tex;
    }

    static string Sanitize(string s) => new(s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}

sealed class MeshBuilder
{
    readonly string _material;
    readonly List<float> _pos = new(), _nrm = new(), _uv = new();
    readonly List<int> _idx = new();

    public MeshBuilder(string material) => _material = material;

    public void AddPolygon(BrushPolygon poly, float scale)
    {
        var face = poly.Face;
        var n = SpaceConvert.ToEngineDir(face.Normal);
        int baseIndex = _pos.Count / 3;

        foreach (var v in poly.Vertices)
        {
            var p = SpaceConvert.ToEngine(v, scale);
            _pos.Add(p.X); _pos.Add(p.Y); _pos.Add(p.Z);
            _nrm.Add(n.X); _nrm.Add(n.Y); _nrm.Add(n.Z);

            var uv = TextureMath.ComputeUV(face, v);
            _uv.Add(uv.X); _uv.Add(uv.Y);
        }

        for (int i = 1; i < poly.Vertices.Count - 1; i++)
        {
            _idx.Add(baseIndex);
            _idx.Add(baseIndex + i);
            _idx.Add(baseIndex + i + 1);
        }
    }

    public KREAN.Core.Scenes.MeshData ToMeshData(string id) => new()
    {
        Id = id,
        Material = _material,
        Positions = _pos.ToArray(),
        Normals = _nrm.ToArray(),
        UVs = _uv.ToArray(),
        Indices = _idx.ToArray()
    };
}
