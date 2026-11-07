using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MeiRi.Interop;
using MeiRi.Services;

namespace MeiRi;

/// <summary>
/// Dev-only diagnostics (<c>MeiRi.exe --probe</c>): opens the floating windows the way a click
/// would, moves the calendar, and writes every rectangle plus its z-order index to the log.
/// </summary>
public static class DevProbe
{
    public static void Run(CalendarHost host, DataStore store, Action done)
    {
        var foreground = Native.GetForegroundWindow();
        Native.GetCursorPos(out var original);
        var calendar = host.Windows.FirstOrDefault();
        if (calendar is null)
        {
            done();
            return;
        }

        int step = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        timer.Tick += (_, _) =>
        {
            step++;
            try
            {
                switch (step)
                {
                    case 1:
                        Log.Write("probe#1 " + host.DescribeLayout());
                        var w = ScreenUtil.WorkAreaAt(10, 10);
                        Log.Write($"probe#dpi cal={VisualTreeHelper.GetDpi(calendar).DpiScaleX:F2} " +
                                  $"sys={SystemParameters.PrimaryScreenWidth:F0}x{SystemParameters.PrimaryScreenHeight:F0} " +
                                  $"work=[{w.Left},{w.Top} {w.Width}x{w.Height}]");
                        host.OpenDay(DateOnly.FromDateTime(DateTime.Today), calendar);
                        break;
                    case 2:
                        Log.Write("probe#2 " + host.DescribeLayout());
                        Native.GetWindowRect(calendar.Handle, out var rect);
                        Native.SetCursorPos(rect.Left + 60, rect.Top + 20);
                        Native.GetCursorPos(out var start);
                        host.BeginWindowDrag(calendar, start);
                        Native.SetCursorPos(start.X + 60, start.Y + 40);
                        host.DragWindowsToCursor();
                        Log.Write("probe#2b " + host.DescribeLayout());
                        host.EndWindowDrag(moved: false);
                        break;
                    case 3:
                        Log.Write("probe#3 " + host.DescribeLayout());
                        host.OpenBacklog(calendar);
                        break;
                    case 4:
                        Log.Write("probe#4 " + host.DescribeLayout());
                        MenuFlyout.Show(calendar.MenuAnchor, System.Windows.Controls.Primitives.PlacementMode.Bottom,
                            MenuFlyout.Item("打开今天", "\uE787", () => { }),
                            MenuFlyout.Item("待定区", "\uE8A5", () => { }),
                            MenuFlyout.Separator(),
                            MenuFlyout.Item("退出", "\uE7E8", () => { }, danger: true));
                        break;
                    case 5:
                        Log.Write("probe#5 popups: " + DescribeProcessWindows());
                        MenuFlyout.Close();
                        break;
                    case 6:
                        // the hover preview must be click-through, otherwise showing it steals the
                        // pointer from the calendar cell and the tooltip flickers in a loop. It is
                        // also the window directly above its calendar, below any open day card.
                        Native.GetWindowRect(calendar.Handle, out var cr);
                        Native.SetCursorPos(cr.Left + cr.Width / 2, cr.Top + cr.Height / 2);
                        var date = new DateOnly(2027, 1, 1);
                        var day = store.GetOrCreate(date);
                        var probeItem = new Models.TodoItem { Text = "probe" };
                        day.FreeItems.Add(probeItem);
                        host.ShowPreview(new ViewModels.DayCell(date, true), calendar);
                        Native.GetCursorPos(out var at);
                        var under = Native.WindowFromPoint(at);
                        var previewHwnd = host.PreviewHandle;
                        Log.Write($"probe#6 preview: hwnd=0x{previewHwnd.ToInt64():X} " +
                                  $"clickThrough={HasTransparent(previewHwnd)} " +
                                  $"underCursor={Native.ClassNameOf(Native.GetAncestor(under, Native.GA_ROOT))} " +
                                  $"underIsPreview={under == previewHwnd} " +
                                  host.DescribeLayout() + " windows: " + DescribeProcessWindows());
                        day.FreeItems.Remove(probeItem);
                        host.HidePreviewNow();
                        break;
                    case 7:
                        var a1 = new DateOnly(2027, 1, 2);
                        var a2 = new DateOnly(2027, 1, 3);
                        var i1 = new Models.TodoItem { Text = "probe-a" };
                        var i2 = new Models.TodoItem { Text = "probe-b", Note = "带备注的一行，用来模拟真实内容长度" };
                        store.GetOrCreate(a1).FreeItems.Add(i1);
                        store.GetOrCreate(a2).FreeItems.Add(i2);
                        CalendarHost.LogPreviewTimings = true;
                        for (int it = 0; it < 6; it++)
                        {
                            var d = it % 2 == 0 ? a1 : a2;
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            host.ShowPreview(new ViewModels.DayCell(d, true), calendar);
                            double sync = sw.Elapsed.TotalMilliseconds;
                            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                                new Action(() => { }), DispatcherPriority.ApplicationIdle);
                            Log.Write($"probe#7 sync={sync:F1}ms toIdle={sw.Elapsed.TotalMilliseconds:F1}ms");
                        }
                        var cycle = System.Diagnostics.Stopwatch.StartNew();
                        for (int it = 0; it < 12; it++)
                        {
                            host.HidePreviewNow();
                            host.ShowPreview(new ViewModels.DayCell(it % 2 == 0 ? a1 : a2, true), calendar);
                        }
                        cycle.Stop();
                        Log.Write($"probe#7b hide+show x12: {cycle.Elapsed.TotalMilliseconds / 12:F1} ms per cycle");
                        store.GetOrCreate(a1).FreeItems.Remove(i1);
                        store.GetOrCreate(a2).FreeItems.Remove(i2);
                        host.HidePreviewNow();
                        break;
                    case 8:
                        var cell = host.ViewModel.Cells[10];
                        var repaint = System.Diagnostics.Stopwatch.StartNew();
                        for (int i = 0; i < 5; i++)
                        {
                            cell.IsDropTarget = i % 2 == 0;
                            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                                new Action(() => { }), DispatcherPriority.ApplicationIdle);
                        }
                        repaint.Stop();
                        Log.Write($"probe#8 calendar repaint x5: {repaint.Elapsed.TotalMilliseconds / 5:F1} ms each");
                        cell.IsDropTarget = false;
                        break;
                    default:
                        Log.Write("probe#9 " + host.DescribeLayout());
                        timer.Stop();
                        Native.SetCursorPos(original.X, original.Y);
                        if (foreground != IntPtr.Zero) Native.SetForegroundWindow(foreground);
                        done();
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Write("probe failed: " + ex);
                timer.Stop();
                done();
            }
        };
        timer.Start();
    }

    /// <summary>
    /// Dev-only (<c>--hold</c>): keeps the hover preview on screen with the mouse untouched and
    /// reports the sustained CPU load — a high number means something is rendering continuously.
    /// </summary>
    public static void Hold(CalendarHost host, DataStore store, bool showPreview, Action done)
    {
        var calendar = host.Windows.FirstOrDefault();
        if (calendar is null)
        {
            done();
            return;
        }
        var date = DateOnly.FromDateTime(DateTime.Today);
        var day = store.GetOrCreate(date);
        var temp = new Models.TodoItem { Text = "hold-probe" };
        day.FreeItems.Add(temp);
        if (showPreview) host.ShowPreview(new ViewModels.DayCell(date, true), calendar);

        var start = System.Diagnostics.Stopwatch.StartNew();
        var cpu0 = ReadCpu();
        int ticks = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            ticks++;
            var cpu = ReadCpu() - cpu0;
            Log.Write($"hold t={start.Elapsed.TotalSeconds:F0}s cpu={cpu / start.Elapsed.TotalSeconds:F1}% " +
                      $"ws={GC.GetTotalMemory(false) / 1048576}MB");
            if (ticks < 8) return;
            timer.Stop();
            day.FreeItems.Remove(temp);
            host.HidePreviewNow();
            done();
        };
        timer.Start();
    }

    private static double ReadCpu()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return process.TotalProcessorTime.TotalMilliseconds;
    }

    /// <summary>Whether WS_EX_TRANSPARENT is set on the window under the cursor.</summary>
    private static bool HasTransparent(IntPtr hwnd)
    {
        var root = Native.GetAncestor(hwnd, Native.GA_ROOT);
        if (root == IntPtr.Zero) root = hwnd;
        long ex = Native.GetWindowLongPtr(root, Native.GWL_EXSTYLE).ToInt64();
        return (ex & Native.WS_EX_TRANSPARENT) != 0;
    }

    /// <summary>All visible top-level windows owned by this process: class, rect and z index.</summary>
    private static string DescribeProcessWindows()
    {
        var sb = new System.Text.StringBuilder();
        uint pid = (uint)Environment.ProcessId;
        var ours = new System.Collections.Generic.List<IntPtr>();
        Native.EnumWindows((hwnd, _) =>
        {
            Native.GetWindowThreadProcessId(hwnd, out uint owner);
            if (owner != pid) return true;
            if (!Native.IsWindowVisible(hwnd)) return true;
            ours.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        int z = 0;
        var top = Native.GetTopWindow(IntPtr.Zero);
        var order = new System.Collections.Generic.Dictionary<IntPtr, int>();
        while (top != IntPtr.Zero && z < 800)
        {
            order[top] = z++;
            top = Native.GetWindow(top, Native.GW_HWNDNEXT);
        }

        foreach (var hwnd in ours)
        {
            Native.GetWindowRect(hwnd, out var r);
            long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            sb.Append(Native.ClassNameOf(hwnd))
                .Append(" [").Append(r.Left).Append(',').Append(r.Top).Append(' ')
                .Append(r.Width).Append('x').Append(r.Height).Append("] z=")
                .Append(order.TryGetValue(hwnd, out int zi) ? zi : -1)
                .Append(" ex=0x").Append(ex.ToString("X"))
                .Append("; ");
        }
        return sb.ToString();
    }
}
