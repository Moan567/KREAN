using System.Globalization;
using System.Numerics;
using KREAN.Core.Scenes;

namespace KREAN.MapCompiler;

public static class ObjModelLoader
{
    public static bool TryLoad(string path, out MeshData mesh, string id = "")
    {
        mesh = null!;
        try
        {
            if (!File.Exists(path)) return false;
            var lines = File.ReadAllLines(path);
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var outPos = new List<float>();
            var outNrm = new List<float>();
            var outUv = new List<float>();
            var outIdx = new List<int>();
            int vertexCounter = 0;
            // map for dedup: key -> index
            var vertMap = new Dictionary<string,int>();
            foreach(var raw in lines)
            {
                var line = raw.Trim();
                if(line.Length==0 || line.StartsWith("#")) continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if(parts.Length==0) continue;
                if(parts[0]=="v" && parts.Length>=4)
                {
                    if(float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                       float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) &&
                       float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                        positions.Add(new Vector3(x,y,z));
                }
                else if(parts[0]=="vn" && parts.Length>=4)
                {
                    if(float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                       float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) &&
                       float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                    {
                        var n = new Vector3(x,y,z);
                        if(n.LengthSquared()>1e-6f) n=Vector3.Normalize(n);
                        normals.Add(n);
                    }
                }
                else if(parts[0]=="vt" && parts.Length>=3)
                {
                    if(float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var u) &&
                       float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                        uvs.Add(new Vector2(u,v));
                }
                else if(parts[0]=="f")
                {
                    // triangulate fan
                    var faceVerts = new List<int>();
                    for(int i=1;i<parts.Length;i++)
                    {
                        var token = parts[i];
                        var idxParts = token.Split('/');
                        int pi = -1, ti = -1, ni = -1;
                        if(idxParts.Length>=1 && int.TryParse(idxParts[0], out var pIdx)) pi = pIdx<0 ? positions.Count + pIdx : pIdx-1;
                        if(idxParts.Length>=2 && idxParts[1].Length>0 && int.TryParse(idxParts[1], out var tIdx)) ti = tIdx<0 ? uvs.Count + tIdx : tIdx-1;
                        if(idxParts.Length>=3 && int.TryParse(idxParts[2], out var nIdx)) ni = nIdx<0 ? normals.Count + nIdx : nIdx-1;
                        string key = $"{pi}/{ti}/{ni}";
                        if(!vertMap.TryGetValue(key, out var outIndex))
                        {
                            outIndex = vertexCounter++;
                            vertMap[key]=outIndex;
                            var p = pi>=0 && pi<positions.Count ? positions[pi] : Vector3.Zero;
                            var n = ni>=0 && ni<normals.Count ? normals[ni] : Vector3.UnitY;
                            var uv = ti>=0 && ti<uvs.Count ? uvs[ti] : Vector2.Zero;
                            outPos.Add(p.X); outPos.Add(p.Y); outPos.Add(p.Z);
                            outNrm.Add(n.X); outNrm.Add(n.Y); outNrm.Add(n.Z);
                            outUv.Add(uv.X); outUv.Add(1f-uv.Y); // flip V
                        }
                        faceVerts.Add(vertMap[key]);
                    }
                    for(int i=1;i<faceVerts.Count-1;i++)
                    {
                        outIdx.Add(faceVerts[0]);
                        outIdx.Add(faceVerts[i]);
                        outIdx.Add(faceVerts[i+1]);
                    }
                }
            }
            if(outPos.Count==0) return false;
            // if no normals, compute faceted normals
            if(outNrm.Count==0 || outNrm.All(v=>v==0))
            {
                // zero out and recompute per triangle via cross
                for(int i=0;i<outNrm.Count;i++) outNrm[i]=0;
                // need positions per vertex index mapping? use outPos/outIdx
                var vertNormals = new Vector3[vertexCounter];
                for(int i=0;i<outIdx.Count;i+=3)
                {
                    int a=outIdx[i], b=outIdx[i+1], c=outIdx[i+2];
                    var pa=new Vector3(outPos[a*3], outPos[a*3+1], outPos[a*3+2]);
                    var pb=new Vector3(outPos[b*3], outPos[b*3+1], outPos[b*3+2]);
                    var pc=new Vector3(outPos[c*3], outPos[c*3+1], outPos[c*3+2]);
                    var n=Vector3.Normalize(Vector3.Cross(pb-pa, pc-pa));
                    vertNormals[a]+=n; vertNormals[b]+=n; vertNormals[c]+=n;
                }
                outNrm.Clear();
                for(int i=0;i<vertexCounter;i++){ var n=vertNormals[i]; if(n.LengthSquared()>1e-6f) n=Vector3.Normalize(n); else n=Vector3.UnitY; outNrm.Add(n.X); outNrm.Add(n.Y); outNrm.Add(n.Z); }
            }
            mesh = new MeshData{ Id = string.IsNullOrEmpty(id) ? Path.GetFileNameWithoutExtension(path) : id, Material = Path.GetFileNameWithoutExtension(path), Positions = outPos.ToArray(), Normals = outNrm.ToArray(), UVs = outUv.ToArray(), Indices = outIdx.ToArray() };
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[obj] failed {path}: {ex.Message}");
            return false;
        }
    }

    public static string? FindModelFile(string modelValue, string[] searchDirs)
    {
        if(string.IsNullOrWhiteSpace(modelValue)) return null;
        modelValue = modelValue.Replace('\\','/').Trim();
        // direct
        if(File.Exists(modelValue)) return Path.GetFullPath(modelValue);
        // try as given extension or add .obj
        string[] candidates = new[]{ modelValue, modelValue+".obj", modelValue+".OBJ" };
        foreach(var cand in candidates)
        {
            foreach(var dir in searchDirs)
            {
                var p = Path.Combine(dir, cand);
                if(File.Exists(p)) return Path.GetFullPath(p);
                // also try basename only
                var baseName = Path.GetFileName(cand);
                var p2 = Path.Combine(dir, baseName);
                if(File.Exists(p2)) return Path.GetFullPath(p2);
            }
        }
        // search recursively in models dirs
        foreach(var dir in searchDirs)
        {
            if(!Directory.Exists(dir)) continue;
            try{
                foreach(var f in Directory.GetFiles(dir, Path.GetFileName(modelValue), SearchOption.AllDirectories))
                    if(string.Equals(Path.GetFileNameWithoutExtension(f), Path.GetFileNameWithoutExtension(modelValue), StringComparison.OrdinalIgnoreCase))
                        return f;
                // also search .obj variant
                var objName = Path.GetFileNameWithoutExtension(modelValue)+".obj";
                foreach(var f in Directory.GetFiles(dir, objName, SearchOption.AllDirectories)) return f;
            }catch{}
        }
        return null;
    }
}
