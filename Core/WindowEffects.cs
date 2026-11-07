using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MeiRi.Interop;

namespace MeiRi;

public static class WindowEffects
{
    public static IntPtr Handle(Window window) => new WindowInteropHelper(window).Handle;

    public static void MakeToolWindow(Window window, bool noActivate = true)
    {
        var hwnd = Handle(window);
        if (hwnd == IntPtr.Zero) return;
        long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        ex |= Native.WS_EX_TOOLWINDOW;
        if (noActivate) ex |= Native.WS_EX_NOACTIVATE;
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>
    /// Makes the window invisible to the mouse: clicks and hover go straight through to whatever
    /// is underneath. Used for the hover preview, which by design sits right under the cursor —
    /// without this the calendar would get a leave/enter pair on every show and flicker.
    /// </summary>
    public static void MakeClickThrough(Window window)
    {
        var hwnd = Handle(window);
        if (hwnd == IntPtr.Zero) return;
        long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        ex |= Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
    }

    public static void SetPhysicalPosition(Window window, int x, int y)
    {
        var hwnd = Handle(window);
        if (hwnd == IntPtr.Zero) return;
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    /// <summary>
    /// Puts a window directly above another one in the z-order. <c>SetWindowPos</c> inserts the
    /// window *after* the given handle, which is below it, so the window that currently sits above
    /// <paramref name="below"/> is used as the anchor instead.
    /// </summary>
    public static void PlaceRightAbove(Window window, Window below)
    {
        var hwnd = Handle(window);
        var belowHwnd = Handle(below);
        if (hwnd == IntPtr.Zero || belowHwnd == IntPtr.Zero || hwnd == belowHwnd) return;
        var anchor = Native.GetWindow(belowHwnd, Native.GW_HWNDPREV);
        Native.SetWindowPos(hwnd, anchor, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    public static (int Width, int Height) PhysicalSize(Window window)
    {
        Native.GetWindowRect(Handle(window), out var r);
        return (r.Width, r.Height);
    }

    public static bool PointInside(Window window, int screenX, int screenY)
    {
        Native.GetWindowRect(Handle(window), out var r);
        return screenX >= r.Left && screenX < r.Right && screenY >= r.Top && screenY < r.Bottom;
    }

    /// <summary>
    /// Brings a window to the foreground even when the current foreground window belongs
    /// to another process (the note window is created from a desktop double click, so the
    /// normal Activate() path is blocked by the foreground lock).
    /// </summary>
    public static void ForceForeground(Window window)
    {
        var hwnd = Handle(window);
        if (hwnd == IntPtr.Zero) return;
        var foreground = Native.GetForegroundWindow();
        if (foreground == hwnd) return;
        uint foregroundThread = Native.GetWindowThreadProcessId(foreground, out _);
        uint currentThread = Native.GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != currentThread
                        && Native.AttachThreadInput(foregroundThread, currentThread, true);
        try
        {
            Native.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) Native.AttachThreadInput(foregroundThread, currentThread, false);
        }
    }

    /// <summary>
    /// Puts the window at the top of the normal z band without touching the keyboard focus.
    /// Used for windows created by the mouse hook: the foreground lock rejects SetForegroundWindow
    /// there, so the window would otherwise stay behind whatever else is maximised.
    /// </summary>
    public static void RaiseWithoutActivation(Window window)
    {
        var hwnd = Handle(window);
        if (hwnd == IntPtr.Zero) return;
        const uint flags = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE;
        // hopping through topmost is the reliable way to the front of the normal band
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, flags);
        Native.SetWindowPos(hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0, flags);
    }

    /// <summary>Places a window at a physical position, clamped to the work area of that point.</summary>
    public static void PlaceWindow(Window window, int x, int y)
    {
        var work = ScreenUtil.WorkAreaAt(x, y);
        var (w, h) = PhysicalSize(window);
        int nx = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - w));
        int ny = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - h));
        SetPhysicalPosition(window, nx, ny);
    }
}
