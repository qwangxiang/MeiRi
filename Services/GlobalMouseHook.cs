using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using MeiRi.Interop;

namespace MeiRi.Services;

/// <summary>
/// Global low-level mouse hook that turns a double click on the taskbar into "new note".
/// <para>
/// The hook runs on its <b>own thread</b> with its own message pump. A low-level hook is called
/// from the thread that installed it, so installing it on the UI thread would make every mouse
/// movement wait for the next free dispatcher slot — any layout or render work in the app would
/// then be felt as a laggy, "sticky" cursor across the whole desktop.
/// </para>
/// </summary>
public sealed class GlobalMouseHook : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public Native.POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private readonly Native.HookProc _proc;
    private readonly Dispatcher _dispatcher;
    private Thread? _thread;
    private IntPtr _hook;
    private uint _threadId;
    private volatile bool _stopping;
    private int _lastTime;
    private Native.POINT _lastPoint;
    private IntPtr _lastHwnd = IntPtr.Zero;
    private bool _disposed;

    /// <summary>Diagnostics: mouse-move events seen by the hook thread.</summary>
    public long MovesSeen;

    public event Action<int, int>? TaskbarDoubleClick;

    public GlobalMouseHook()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _proc = Callback;
    }

    public bool IsInstalled => _hook != IntPtr.Zero;

    public void Install()
    {
        if (_thread is not null) return;
        using var ready = new ManualResetEventSlim(false);
        _thread = new Thread(() => HookLoop(ready))
        {
            IsBackground = true,
            Name = "MeiRi.MouseHook",
            Priority = ThreadPriority.AboveNormal
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(2000);
    }

    private void HookLoop(ManualResetEventSlim ready)
    {
        _threadId = Native.GetCurrentThreadId();
        try
        {
            _hook = Native.SetWindowsHookEx(WH_MOUSE_LL, _proc, Native.GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) Log.Write("mouse hook failed: " + Marshal.GetLastWin32Error());
            else Log.Write("mouse hook installed on hook thread " + _threadId);
        }
        catch (Exception ex)
        {
            Log.Write("mouse hook install failed: " + ex.Message);
        }
        ready.Set();

        while (!_stopping && Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }

        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            // keep this path allocation-free and native-call-light: it runs for every mouse event
            if (nCode >= 0 && wParam == 0x0200) MovesSeen++;
            if (nCode >= 0 && wParam == WM_LBUTTONDOWN)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                int now = Environment.TickCount;
                IntPtr hwnd = Native.WindowFromPoint(info.pt);
                bool onTaskbar = hwnd != IntPtr.Zero && IsTaskbar(hwnd);

                if (!onTaskbar)
                {
                    _lastTime = 0;
                    _lastHwnd = IntPtr.Zero;
                }
                else
                {
                    bool second = _lastHwnd == hwnd
                                  && now - _lastTime <= Native.GetDoubleClickTime()
                                  && Math.Abs(info.pt.X - _lastPoint.X) <= Native.GetSystemMetrics(Native.SM_CXDOUBLECLK)
                                  && Math.Abs(info.pt.Y - _lastPoint.Y) <= Native.GetSystemMetrics(Native.SM_CYDOUBLECLK);
                    if (second)
                    {
                        _lastTime = 0;
                        _lastHwnd = IntPtr.Zero;
                        var pt = info.pt;
                        _dispatcher.BeginInvoke(new Action(() => TaskbarDoubleClick?.Invoke(pt.X, pt.Y)),
                            DispatcherPriority.Input);
                    }
                    else
                    {
                        _lastTime = now;
                        _lastPoint = info.pt;
                        _lastHwnd = hwnd;
                    }
                }
            }
        }
        catch
        {
            // never let a hook callback take the desktop down
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool IsTaskbar(IntPtr hwnd)
    {
        var root = Native.GetAncestor(hwnd, Native.GA_ROOT);
        if (root == IntPtr.Zero) root = hwnd;
        string cls = Native.ClassNameOf(root);
        return cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopping = true;
        if (_threadId != 0) Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
