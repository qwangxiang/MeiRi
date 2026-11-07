using System;
using System.Runtime.InteropServices;

namespace MeiRi.Interop;

/// <summary>
/// The documented <c>IVirtualDesktopManager</c> API. Notes are per-window, so two notes on two
/// virtual desktops must never see each other: windows of a background desktop keep their old
/// screen rectangle (they are simply not composited), which otherwise looks like a drop target.
/// </summary>
public static class VirtualDesktops
{
    [ComImport]
    [Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A")]
    [ClassInterface(ClassInterfaceType.None)]
    private class VirtualDesktopManager
    {
    }

    [ComImport]
    [Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out int onCurrentDesktop);
        [PreserveSig] int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
        [PreserveSig] int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
    }

    private static IVirtualDesktopManager? _manager;
    private static bool _failed;

    private static IVirtualDesktopManager? Manager()
    {
        if (_manager is not null || _failed) return _manager;
        try
        {
            _manager = (IVirtualDesktopManager)new VirtualDesktopManager();
        }
        catch (Exception ex)
        {
            // virtual desktops simply do not exist on some systems (or the interface is absent)
            _failed = true;
            Log.Write("virtual desktop manager unavailable: " + ex.Message);
        }
        return _manager;
    }

    /// <summary>True when the window is on the desktop the user is currently looking at.
    /// Falls back to true whenever the information is unavailable.</summary>
    public static bool IsOnCurrentDesktop(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var manager = Manager();
        if (manager is null) return true;
        try
        {
            return manager.IsWindowOnCurrentVirtualDesktop(hwnd, out int onCurrent) != 0 || onCurrent != 0;
        }
        catch
        {
            return true;
        }
    }
}
