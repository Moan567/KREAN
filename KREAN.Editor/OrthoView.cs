using System.Numerics;
using ImGuiNET;
using KREAN.MapCompiler;

namespace KREAN.Editor;

public enum OrthoAxis { Top, Front, Side }

public sealed class OrthoView
{
    public OrthoAxis Axis;
    public Vector2 Pan = Vector2.Zero;
    public float Zoom = 1f;
    public bool Panning;
    public Vector2 _panStart;
    public Vector2 _mouseStart;

    public OrthoView(OrthoAxis axis){ Axis=axis; Zoom=0.6f; }

    public Vector2 QuakeToScreen(Vector3 q, Vector2 origin, float scale)
    {
        // project quake to 2D canvas
        Vector2 p = Axis switch{ OrthoAxis.Top => new Vector2(q.X, -q.Y), OrthoAxis.Front => new Vector2(q.X, -q.Z), OrthoAxis.Side => new Vector2(q.Y, -q.Z), _=>Vector2.Zero };
        return origin + (p + Pan) * scale * Zoom;
    }
    public Vector3 ScreenToQuake(Vector2 screen, Vector2 origin, float scale, float fixedCoord)
    {
        Vector2 p = (screen - origin) / (scale*Zoom) - Pan;
        return Axis switch{ OrthoAxis.Top => new Vector3(p.X, -p.Y, fixedCoord), OrthoAxis.Front => new Vector3(p.X, fixedCoord, -p.Y), OrthoAxis.Side => new Vector3(fixedCoord, p.X, -p.Y), _=>Vector3.Zero };
    }
    public Vector2 GetFixedCoordCenter(MapEditorSession s)
    {
        var c=s.GetSelectedCenter();
        return Vector2.Zero;
    }
}

public static class OrthoRenderer
{
    public static void DrawOrthoWindow(string title, OrthoView view, MapEditorSession session, Action recompile, Action<string> setStatus)
    {
        ImGui.SetNextWindowSize(new Vector2(320,260), ImGuiCond.FirstUseEver);
        if(!ImGui.Begin(title)) { ImGui.End(); return; }
        var avail = ImGui.GetContentRegionAvail();
        var screenPos = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        var canvasMin = screenPos;
        var canvasMax = screenPos + avail;
        // background
        draw.AddRectFilled(canvasMin, canvasMax, ImGui.ColorConvertFloat4ToU32(new Vector4(0.12f,0.12f,0.13f,1f)));
        // grid
        float grid = session.GridSize;
        if(grid>0)
        {
            // grid lines every grid * zoom
            float step = grid * view.Zoom * 0.6f; // scale factor 0.6 matches QuakeToScreen scale
            if(step>4 && step<200)
            {
                Vector2 origin = canvasMin + avail*0.5f;
                for(float x = origin.X + view.Pan.X*view.Zoom*0.6f; x<canvasMax.X; x+=step) draw.AddLine(new Vector2(x,canvasMin.Y), new Vector2(x,canvasMax.Y), ImGui.ColorConvertFloat4ToU32(new Vector4(0.22f,0.22f,0.24f,1f)));
                for(float x = origin.X + view.Pan.X*view.Zoom*0.6f - step; x>canvasMin.X; x-=step) draw.AddLine(new Vector2(x,canvasMin.Y), new Vector2(x,canvasMax.Y), ImGui.ColorConvertFloat4ToU32(new Vector4(0.22f,0.22f,0.24f,1f)));
                for(float y = origin.Y + view.Pan.Y*view.Zoom*0.6f; y<canvasMax.Y; y+=step) draw.AddLine(new Vector2(canvasMin.X,y), new Vector2(canvasMax.X,y), ImGui.ColorConvertFloat4ToU32(new Vector4(0.22f,0.22f,0.24f,1f)));
                for(float y = origin.Y + view.Pan.Y*view.Zoom*0.6f - step; y>canvasMin.Y; y-=step) draw.AddLine(new Vector2(canvasMin.X,y), new Vector2(canvasMax.X,y), ImGui.ColorConvertFloat4ToU32(new Vector4(0.22f,0.22f,0.24f,1f)));
            }
            // axis center cross
            Vector2 origin2 = canvasMin + avail*0.5f + view.Pan*view.Zoom*0.6f;
            draw.AddLine(origin2 - new Vector2(15,0), origin2+ new Vector2(15,0), ImGui.ColorConvertFloat4ToU32(new Vector4(0.6f,0.2f,0.2f,1f)), 1f);
            draw.AddLine(origin2 - new Vector2(0,15), origin2+ new Vector2(0,15), ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f,0.6f,0.2f,1f)), 1f);
        }
        // draw brushes as rects
        float scale=0.6f;
        Vector2 originC = canvasMin + avail*0.5f;
        for(int ei=0;ei<session.Entities.Count;ei++)
        {
            var e=session.Entities[ei];
            if(!session.IsEntityVisible(e)) continue;
            foreach(var br in e.Brushes)
            {
                BrushManipulation.GetBounds(br, out var mn, out var mx);
                if(mn.X>mx.X) continue;
                Vector2 a = view.QuakeToScreen(mn, originC, scale);
                Vector2 b = view.QuakeToScreen(mx, originC, scale);
                Vector2 rmin = new Vector2(Math.Min(a.X,b.X), Math.Min(a.Y,b.Y));
                Vector2 rmax = new Vector2(Math.Max(a.X,b.X), Math.Max(a.Y,b.Y));
                // cull if outside window
                if(rmax.X<canvasMin.X || rmin.X>canvasMax.X || rmax.Y<canvasMin.Y || rmin.Y>canvasMax.Y) continue;
                bool isSel = ei==session.SelectedIndex && session.SelectedBrush!=null && e.Brushes.IndexOf(session.SelectedBrush)==e.Brushes.IndexOf(br);
                if(session.MultiSelectedBrushes.Contains((ei, e.Brushes.IndexOf(br)))) isSel=true;
                uint col = isSel ? ImGui.ColorConvertFloat4ToU32(new Vector4(1f,0.85f,0.2f,1f)) :
                           e.ClassName=="worldspawn" ? ImGui.ColorConvertFloat4ToU32(new Vector4(0.6f,0.6f,0.65f,1f)) :
                           ImGui.ColorConvertFloat4ToU32(new Vector4(0.35f,0.65f,1f,1f));
                draw.AddRect(rmin, rmax, col, 0, ImDrawFlags.None, isSel?2f:1f);
                if(isSel) draw.AddRectFilled(rmin, rmax, ImGui.ColorConvertFloat4ToU32(new Vector4(1f,0.85f,0.2f,0.15f)));
            }
            if(e.Brushes.Count==0 && e.Properties.TryGetValue("origin", out var o))
            {
                var p=MapEditorSession.ParseVec3Public(o);
                Vector2 pt=view.QuakeToScreen(p, originC, scale);
                if(pt.X>=canvasMin.X && pt.X<=canvasMax.X && pt.Y>=canvasMin.Y && pt.Y<=canvasMax.Y)
                {
                    bool isSel=ei==session.SelectedIndex;
                    uint ccol = isSel? ImGui.ColorConvertFloat4ToU32(new Vector4(1f,0.9f,0.2f,1f)) : ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f,0.9f,0.3f,1f));
                    draw.AddCircleFilled(pt, 4f, ccol);
                    draw.AddText(pt + new Vector2(6,-6), ccol, e.ClassName);
                }
            }
        }
        // selection bounds highlight
        if(session.Selected!=null)
        {
            session.GetSelectedBounds(out var smn, out var smx);
            if(smn.X<=smx.X)
            {
                Vector2 a=view.QuakeToScreen(smn, originC, scale);
                Vector2 b=view.QuakeToScreen(smx, originC, scale);
                draw.AddRect(new Vector2(Math.Min(a.X,b.X),Math.Min(a.Y,b.Y)), new Vector2(Math.Max(a.X,b.X),Math.Max(a.Y,b.Y)), ImGui.ColorConvertFloat4ToU32(new Vector4(1f,0.3f,0.2f,1f)),0,ImDrawFlags.None,1.5f);
            }
        }
        // input handling
        var io=ImGui.GetIO();
        bool hovering = ImGui.IsWindowHovered();
        if(hovering)
        {
            if(ImGui.IsMouseDragging(ImGuiMouseButton.Middle) || (ImGui.IsMouseDragging(ImGuiMouseButton.Left) && io.KeyAlt))
            {
                if(!view.Panning){ view.Panning=true; view._panStart=view.Pan; view._mouseStart=io.MousePos; }
                Vector2 delta = io.MousePos - view._mouseStart;
                view.Pan = view._panStart + delta / (scale*view.Zoom);
            }
            else view.Panning=false;
            if(io.MouseWheel!=0 && hovering)
            {
                float nz = Math.Clamp(view.Zoom + io.MouseWheel*0.1f*view.Zoom, 0.1f, 8f);
                view.Zoom=nz;
            }
            if(ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !io.KeyAlt && !view.Panning)
            {
                Vector2 mp = io.MousePos;
                // pick topmost brush under cursor
                int bestEi=-1, bestBi=-1; float bestArea=float.MaxValue;
                for(int ei=0;ei<session.Entities.Count;ei++)
                {
                    var e=session.Entities[ei];
                    if(!session.IsEntityVisible(e)) continue;
                    for(int bi=0;bi<e.Brushes.Count;bi++)
                    {
                        BrushManipulation.GetBounds(e.Brushes[bi], out var mn, out var mx);
                        Vector2 a=view.QuakeToScreen(mn,originC,scale);
                        Vector2 b=view.QuakeToScreen(mx,originC,scale);
                        Vector2 rmin=new Vector2(Math.Min(a.X,b.X),Math.Min(a.Y,b.Y));
                        Vector2 rmax=new Vector2(Math.Max(a.X,b.X),Math.Max(a.Y,b.Y));
                        if(mp.X>=rmin.X && mp.X<=rmax.X && mp.Y>=rmin.Y && mp.Y<=rmax.Y)
                        {
                            float area=(rmax.X-rmin.X)*(rmax.Y-rmin.Y);
                            if(area<bestArea){ bestArea=area; bestEi=ei; bestBi=bi; }
                        }
                    }
                    if(e.Brushes.Count==0 && e.Properties.TryGetValue("origin", out var o2))
                    {
                        var p=MapEditorSession.ParseVec3Public(o2);
                        Vector2 pt=view.QuakeToScreen(p,originC,scale);
                        if((pt-mp).Length()<8f){ bestEi=ei; bestBi=-1; bestArea=0; }
                    }
                }
                if(bestEi>=0)
                {
                    bool ctrl=io.KeyCtrl;
                    if(ctrl)
                    {
                        if(bestBi>=0)
                        {
                            if(session.MultiSelectedBrushes.Count==0 && session.SelectedBrush!=null) session.MultiSelectedBrushes.Add((session.SelectedIndex, session.SelectedBrushIndex));
                            session.ToggleMultiBrush(bestEi,bestBi);
                            session.SelectBrush(bestEi,bestBi,-1);
                        }
                        else session.ToggleMultiEntity(bestEi);
                    }
                    else
                    {
                        session.ClearAllMulti();
                        if(bestBi>=0) session.SelectBrush(bestEi,bestBi,-1);
                        else session.Select(bestEi);
                    }
                    recompile();
                    setStatus($"Picked {bestEi}:{bestBi} in {title}");
                }
            }
        }
        ImGui.End();
    }
}
