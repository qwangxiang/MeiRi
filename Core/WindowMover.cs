using System;
using System.Windows;
using System.Windows.Interop;
using MeiRi.Interop;

namespace MeiRi;

/// <summary>Drags a single borderless window by its header using physical screen coordinates.</summary>
public sealed class WindowMover
{
    private Window? _window;
    private Native.POINT _startCursor;
    private Native.RECT _startRect;

    public bool Active { get; private set; }

    public void Begin(Window window)
    {
        _window = window;
        Native.GetCursorPos(out _startCursor);
        Native.GetWindowRect(WindowEffects.Handle(window), out _startRect);
        Active = true;
    }

    public void Update()
    {
        if (!Active || _window is null) return;
        Native.GetCursorPos(out var c);
        WindowEffects.SetPhysicalPosition(_window,
            _startRect.Left + c.X - _startCursor.X,
            _startRect.Top + c.Y - _startCursor.Y);
    }

    public void End()
    {
        Active = false;
        _window = null;
    }
}
