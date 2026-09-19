using System.Text;

namespace KREAN.MapCompiler;

/// <summary>Generates a small test level (room, stairs, pillar, crate) so you can test without TrenchBroom.</summary>
public static class SampleMap
{
    public static string Generate()
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Game: Generic");
        sb.AppendLine("// Format: Standard");
        sb.AppendLine("// entity 0");
        sb.AppendLine("{");
        sb.AppendLine("\"classname\" \"worldspawn\"");

        // room shell
        Box(-512, -512, -16, 512, 512, 0, "floor");
        Box(-512, -512, 256, 512, 512, 272, "ceiling");
        Box(-528, -528, -16, -512, 528, 272, "wall");
        Box(512, -528, -16, 528, 528, 272, "wall");
        Box(-512, -528, -16, 512, -512, 272, "wall");
        Box(-512, 512, -16, 512, 528, 272, "wall");

        // stairs (12 units per step, climbable) + platform
        for (int i = 0; i < 5; i++)
            Box(128 + i * 32, -64, 0, 288, 64, 12 * (i + 1), "step");
        Box(288, -64, 0, 480, 64, 60, "step");

        // pillar and a jumpable crate
        Box(-256, -256, 0, -192, -192, 192, "pillar");
        Box(-128, 128, 0, 0, 192, 40, "crate");

        sb.AppendLine("}");

        sb.AppendLine("// entity 1");
        sb.AppendLine("{");
        sb.AppendLine("\"classname\" \"info_player_start\"");
        sb.AppendLine("\"origin\" \"-320 0 28\"");
        sb.AppendLine("\"angle\" \"0\"");
        sb.AppendLine("}");

        sb.AppendLine("// entity 2");
        sb.AppendLine("{");
        sb.AppendLine("\"classname\" \"light\"");
        sb.AppendLine("\"origin\" \"0 0 200\"");
        sb.AppendLine("\"light\" \"400\"");
        sb.AppendLine("}");

        return sb.ToString();

        // Faces are wound so the plane normal points OUT of the box (Quake convention).
        void Box(int x0, int y0, int z0, int x1, int y1, int z1, string tex)
        {
            sb.AppendLine("{");
            sb.AppendLine($"{P(x0, y0, z0)} {P(x0, y1, z0)} {P(x0, y0, z1)} {tex} 0 0 0 1 1");   // -X
            sb.AppendLine($"{P(x1, y0, z0)} {P(x1, y0, z1)} {P(x1, y1, z0)} {tex} 0 0 0 1 1");   // +X
            sb.AppendLine($"{P(x0, y0, z0)} {P(x0, y0, z1)} {P(x1, y0, z0)} {tex} 0 0 0 1 1");   // -Y
            sb.AppendLine($"{P(x0, y1, z0)} {P(x1, y1, z0)} {P(x0, y1, z1)} {tex} 0 0 0 1 1");   // +Y
            sb.AppendLine($"{P(x0, y0, z0)} {P(x1, y0, z0)} {P(x0, y1, z0)} {tex} 0 0 0 1 1");   // -Z
            sb.AppendLine($"{P(x0, y0, z1)} {P(x0, y1, z1)} {P(x1, y0, z1)} {tex} 0 0 0 1 1");   // +Z
            sb.AppendLine("}");
        }

        static string P(int x, int y, int z) => $"( {x} {y} {z} )";
    }
}
