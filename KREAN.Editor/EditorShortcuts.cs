using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;

namespace KREAN.Editor;

/// <summary>Central professional shortcut handling — TrenchBroom/Blender style.</summary>
public static class EditorShortcuts
{
    public static bool WantsTextInput => ImGui.GetIO().WantTextInput;

    public static bool IsCtrlDown(KREAN.Runtime.InputState input) => input.IsDown(Key.ControlLeft) || input.IsDown(Key.ControlRight);
    public static bool IsShiftDown(KREAN.Runtime.InputState input) => input.IsDown(Key.ShiftLeft) || input.IsDown(Key.ShiftRight);
    public static bool IsAltDown(KREAN.Runtime.InputState input) => input.IsDown(Key.AltLeft) || input.IsDown(Key.AltRight);

    public static string HelpText => """
        Tools (Brush is default):
          B or 2 — Brush: LMB-drag empty to draw, click for cube, Alt+Arrows extrude, Shift+A arch
          Q or 1 — Select: click to pick, drag/gizmo (incl. XY/XZ/YZ planes) to move
          3 — Face: click face/blue square → arrows / wheel / drag extrude, X/Y/Z lock
          4 — Vertex: click green cross → arrows / PgUpDn / drag to move corner, X/Y/Z lock
          Tab — cycle tool  |  F — frame  |  Esc — cancel / clear highlight
        Navigation:
          Hold RMB — Freelook  |  WASD move  |  E/Space up, Q/Z down  |  Shift sprint, Alt slow
          Wheel — speed (0.5–25)  |  in Face/while-drawing: brush height/thickness  |  V — noclip
        Selection & edit:
          LMB click — pick brush/entity  |  Drag — move (X/Y/Z lock)  |  Alt-drag duplicate (brush or entity)
          Ctrl+click highlight multiple → Ctrl+D / Alt+drag duplicates all highlighted, arrows/gizmo moves all
          Ctrl+D duplicate, Ctrl+Alt+X/Y/Z flip, Ctrl+R rotate 90, Ctrl+H hollow, Del delete, Ctrl+Z/Y undo/redo
        Grid & Brush:
          G snap toggle (hold Shift to bypass)  |  [ / ] grid size  |  Arrows / Shift 8x nudge
          Brush menu → Create Arch… — inner radius / wall / depth / segments → edit wedges with Face/Vertex
        """;
}
