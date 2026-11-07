using System;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;
using MeiRi.Interop;
using MeiRi.Services;
using MeiRi.Views;

namespace MeiRi;

/// <summary>
/// Dev-only (<c>MeiRi.exe --drag</c>): drags a calendar widget and a note window with synthetic
/// input — the same code path a real mouse takes — and logs, step by step, how far the window moved
/// compared with the pointer. A window that travels twice as far as the pointer shows up as
/// <c>rect</c> = 2 x <c>cur</c>; a drop that is thrown away shows up as a rectangle that returns to
/// the starting one.
/// </summary>
public static class DevDrag
{
    private const int Steps = 12;

    public static void Run(CalendarHost host, QuickNoteManager notes, Action done)
    {
        if (host.Windows.Count == 0)
        {
            done();
            return;
        }

        var settings = App.Instance.Settings;
        CalendarHost.LogTimings = true;
        bool hasPos0 = settings.HasPosition;
        double ox0 = settings.OffsetX, oy0 = settings.OffsetY;

        Native.GetCursorPos(out var originalCursor);
        var foreground = Native.GetForegroundWindow();

        var calHwnd = IntPtr.Zero;
        var calBase = default(Native.RECT);
        var calPress = default(Native.POINT);
        double calDpi = 1;

        IntPtr noteHwnd = IntPtr.Zero;
        QuickNoteWindow? noteWindow = null;
        var noteBase = default(Native.RECT);
        var notePress = default(Native.POINT);

        int phase = 0, tick = 0;
        var timer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(12)
        };

        timer.Tick += (_, _) =>
        {
            tick++;
            try
            {
                switch (phase)
                {
                    case 0:
                    {
                        Log.Write("drag# layout " + host.DescribeLayout());
                        foreach (var window in host.Windows)
                        {
                            var hwnd = WindowEffects.Handle(window);
                            if (!Native.GetWindowRect(hwnd, out var rect)) continue;
                            double dpi = VisualTreeHelper.GetDpi(window).DpiScaleX;
                            var point = FindPressPoint(hwnd, rect, dpi);
                            Log.Write($"drag# widget rect=[{rect.Left},{rect.Top} {rect.Width}x{rect.Height}] " +
                                      $"dpi={dpi:F2} press={point?.X},{point?.Y}");
                            if (point is null) continue;
                            calHwnd = hwnd;
                            calBase = rect;
                            calDpi = dpi;
                            calPress = point.Value;
                            break;
                        }

                        if (calHwnd == IntPtr.Zero)
                        {
                            Log.Write("drag# no reachable widget header (covered by another window)");
                            timer.Stop();
                            Finish();
                            break;
                        }

                        Native.SetCursorPos(calPress.X, calPress.Y);
                        var under = Native.WindowFromPoint(calPress);
                        Native.GetWindowThreadProcessId(under, out uint underPid);
                        Log.Write($"drag# press={calPress.X},{calPress.Y} hit={Native.ClassNameOf(under)} " +
                                  $"pid={underPid} ours={underPid == Environment.ProcessId} " +
                                  $"offsets={(int)ox0},{(int)oy0} has={hasPos0}");
                        Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
                        phase = 1;
                        tick = 0;
                        break;
                    }

                    case 1:
                    {
                        Native.GetWindowRect(calHwnd, out var r);
                        Native.GetCursorPos(out var c);
                        Log.Write($"drag#cal {tick} cur={c.X - calPress.X},{c.Y - calPress.Y} " +
                                  $"rect={r.Left - calBase.Left},{r.Top - calBase.Top}");
                        Native.SetCursorPos(calPress.X + tick * 9, calPress.Y + tick * 4);
                        if (tick < Steps) break;
                        Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
                        Native.GetWindowRect(calHwnd, out var up);
                        Log.Write($"drag#cal release rect={up.Left - calBase.Left},{up.Top - calBase.Top} " +
                                  $"saved={(int)settings.OffsetX},{(int)settings.OffsetY} has={settings.HasPosition}");
                        phase = 2;
                        tick = 0;
                        break;
                    }

                    case 2:
                    {
                        if (tick < 6) break;
                        Native.GetWindowRect(calHwnd, out var settled);
                        Log.Write($"drag#cal settled+{tick} rect={settled.Left - calBase.Left},{settled.Top - calBase.Top} " +
                                  $"saved={(int)settings.OffsetX},{(int)settings.OffsetY}");
                        noteWindow = notes.OpenAt(900, 520);
                        noteHwnd = WindowEffects.Handle(noteWindow);
                        phase = 3;
                        tick = 0;
                        break;
                    }

                    case 3:
                        if (tick < 5) break;
                        Native.GetWindowRect(noteHwnd, out noteBase);
                        notePress = new Native.POINT { X = noteBase.Left + 40, Y = noteBase.Top + 18 };
                        Native.SetCursorPos(notePress.X, notePress.Y);
                        Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
                        Log.Write($"drag#note start rect=[{noteBase.Left},{noteBase.Top} {noteBase.Width}x{noteBase.Height}] " +
                                  $"press={notePress.X},{notePress.Y} dpi={VisualTreeHelper.GetDpi(noteWindow).DpiScaleX:F2}");
                        phase = 4;
                        tick = 0;
                        break;

                    case 4:
                    {
                        Native.GetWindowRect(noteHwnd, out var r);
                        Native.GetCursorPos(out var c);
                        Log.Write($"drag#note {tick} cur={c.X - notePress.X},{c.Y - notePress.Y} " +
                                  $"rect={r.Left - noteBase.Left},{r.Top - noteBase.Top}");
                        Native.SetCursorPos(notePress.X + tick * 11, notePress.Y + tick * 5);
                        if (tick < Steps) break;
                        Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
                        Native.GetWindowRect(noteHwnd, out var up);
                        Log.Write($"drag#note release rect={up.Left - noteBase.Left},{up.Top - noteBase.Top}");
                        phase = 5;
                        tick = 0;
                        break;
                    }

                    case 5:
                    {
                        if (tick < 5) break;
                        Native.RECT settled = default;
                        if (noteHwnd != IntPtr.Zero) Native.GetWindowRect(noteHwnd, out settled);
                        Log.Write($"drag#note settled+{tick} rect={settled.Left - noteBase.Left},{settled.Top - noteBase.Top}");
                        timer.Stop();
                        Finish();
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("drag# failed: " + ex);
                timer.Stop();
                Finish();
            }
        };

        timer.Start();
        return;

        void Finish()
        {
            try
            {
                noteWindow?.Close();
                if (hasPos0)
                {
                    settings.HasPosition = true;
                    settings.OffsetX = ox0;
                    settings.OffsetY = oy0;
                }
                else
                {
                    settings.HasPosition = false;
                }
                SettingsService.Save(settings);
                Native.SetCursorPos(originalCursor.X, originalCursor.Y);
                if (foreground != IntPtr.Zero) Native.SetForegroundWindow(foreground);
                Log.Write($"drag# restored offsets={ox0},{oy0} has={hasPos0}");
            }
            catch (Exception ex)
            {
                Log.Write("drag# restore failed: " + ex);
            }
            done();
        }
    }

    /// <summary>
    /// The widget can be covered by other windows, so a synthetic press only reaches it where no
    /// other window is on top. Scans the header band for a point that really belongs to it.
    /// </summary>
    private static Native.POINT? FindPressPoint(IntPtr hwnd, Native.RECT rect, double dpi)
    {
        if (dpi <= 0) dpi = 1;
        int y0 = rect.Top + (int)Math.Round(40 * dpi);
        int y1 = rect.Top + (int)Math.Round(50 * dpi);
        int x0 = rect.Left + (int)Math.Round(46 * dpi);
        int x1 = rect.Right - (int)Math.Round(130 * dpi);
        for (int y = y0; y <= y1; y += 3)
        {
            for (int x = x0; x <= x1; x += 5)
            {
                var p = new Native.POINT { X = x, Y = y };
                if (Native.WindowFromPoint(p) == hwnd) return p;
            }
        }
        return null;
    }
}
