using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MeiRi.Interop;

public sealed class ScreenInfo
{
    public IntPtr Handle { get; init; }
    public Native.RECT Bounds { get; init; }
    public Native.RECT Work { get; init; }
}

public static class ScreenUtil
{
    public static List<ScreenInfo> GetMonitors()
    {
        var list = new List<ScreenInfo>(4);
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
            if (Native.GetMonitorInfo(monitor, ref mi))
            {
                list.Add(new ScreenInfo { Handle = monitor, Bounds = mi.rcMonitor, Work = mi.rcWork });
            }
            return true;
        }, IntPtr.Zero);

        // stable order: left-to-right, then top-to-bottom
        list.Sort((a, b) => a.Bounds.Left != b.Bounds.Left
            ? a.Bounds.Left.CompareTo(b.Bounds.Left)
            : a.Bounds.Top.CompareTo(b.Bounds.Top));
        return list;
    }

    public static Native.RECT WorkAreaAt(int x, int y)
    {
        var monitor = Native.MonitorFromPoint(new Native.POINT(x, y), Native.MONITOR_DEFAULTTONEAREST);
        return Native.GetMonitorWorkArea(monitor);
    }
}
