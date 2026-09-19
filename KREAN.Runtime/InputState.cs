using System.Numerics;
using Silk.NET.Input;

namespace KREAN.Runtime;

public sealed class InputState
{
    readonly HashSet<Key> _down = new();
    readonly HashSet<Key> _pressed = new();
    Vector2 _lastMouse;
    bool _hasLastMouse;

    public Vector2 MouseDelta { get; private set; }
    public bool MouseCaptured { get; set; }

    public bool IsDown(Key key) => _down.Contains(key);
    public bool WasPressed(Key key) => _pressed.Contains(key);

    public void KeyDown(Key key) { if (_down.Add(key)) _pressed.Add(key); }
    public void KeyUp(Key key) => _down.Remove(key);

    public void MouseMove(Vector2 position)
    {
        if (_hasLastMouse) MouseDelta += position - _lastMouse;
        _lastMouse = position;
        _hasLastMouse = true;
    }

    public void ResetMouse() { _hasLastMouse = false; MouseDelta = default; }

    public void EndFrame()
    {
        _pressed.Clear();
        MouseDelta = default;
    }
}
