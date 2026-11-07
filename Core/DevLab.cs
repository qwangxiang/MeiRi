using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using MeiRi.Interop;
using MeiRi.Services;
using MeiRi.Views;

namespace MeiRi;

/// <summary>
/// Dev-only measurements for the two problems that cannot be judged from the code alone:
/// how expensive a keystroke in a note is (<c>--typing</c>), and what the flyout menu actually
/// puts on screen (<c>--flyout</c>). Both write into the normal log file.
/// </summary>
public static class DevLab
{
    // ------------------------------------------------------------------ interactions

    /// <summary>
    /// Dev-only (<c>--frames</c>): how long the frequent interactions of the calendar cost —
    /// opening and closing the day card, switching days on an open card, and the backlog.
    /// </summary>
    public static void Interactions(CalendarHost host, DataStore store, Action done)
    {
        var calendar = host.Windows.FirstOrDefault();
        if (calendar is null)
        {
            done();
            return;
        }
        var today = DateOnly.FromDateTime(DateTime.Today);
        CalendarHost.LogTimings = true;
        var steps = new List<Action>();
        // warm up a throw-away card of each kind first, so the timed opens below are the
        // "second" ones — that is what a pre-warmed app would feel like
        steps.Add(() =>
        {
            var watch = Stopwatch.StartNew();
            var warm = new DayEditorWindow(host, store) { ShowActivated = false, Left = -20000, Top = -20000 };
            warm.Show();
            warm.UpdateLayout();
            warm.CloseForShutdown();
            var backlog = new BacklogWindow(host, store) { ShowActivated = false, Left = -20000, Top = -20000 };
            backlog.Show();
            backlog.UpdateLayout();
            backlog.CloseForShutdown();
            Log.Write($"frames# warmup={watch.ElapsedMilliseconds}ms");
        });
        foreach (var date in new[] { today, today.AddDays(1), today.AddDays(2), today })
        {
            var d = date;
            steps.Add(() => Time($"open {d:MM-dd}", () => host.OpenDay(d, calendar, toggle: false)));
            steps.Add(() => Time($"switch {d:MM-dd}", () => host.OpenDay(d, calendar, toggle: false)));
        }
        steps.Add(() => Time("close editor", host.CloseEditor));
        steps.Add(() => Time("open backlog", () => host.OpenBacklog(calendar, toggle: false)));
        steps.Add(() => Time("close backlog", host.CloseBacklog));
        // behaviour checks: re-clicking the same day closes the card, and the two cards exclude
        // each other
        steps.Add(() =>
        {
            host.OpenDay(today, calendar, toggle: false);
            bool before = host.IsEditorOpen(today);
            host.OpenDay(today, calendar);
            Log.Write($"frames# toggle day: open={before} afterSecondClick={host.IsEditorOpen(today)}");
        });
        steps.Add(() =>
        {
            host.OpenDay(today, calendar, toggle: false);
            host.OpenBacklog(calendar, toggle: false);
            Log.Write($"frames# exclusive: editor={host.IsEditorOpen(today)} backlog={host.IsBacklogOpen}");
            host.OpenBacklog(calendar);
            Log.Write($"frames# toggle backlog: backlogAfterSecondClick={host.IsBacklogOpen}");
        });
        steps.Add(() =>
        {
            host.CloseEditor();
            host.CloseBacklog();
            done();
        });
        Chain(steps, 400);
    }

    private static void Time(string tag, Action action)
    {
        var watch = Stopwatch.StartNew();
        action();
        double sync = watch.Elapsed.TotalMilliseconds;
        Pump();
        Log.Write($"frames# {tag}: sync={sync:F1}ms toIdle={watch.Elapsed.TotalMilliseconds:F1}ms");
    }

    // ------------------------------------------------------------------ typing

    public static void Typing(Action done)
    {
        var manager = new QuickNoteManager();
        var window = manager.OpenAt(240, 240);
        Log.Write($"typing# start ({RenderCapability.Tier >> 16} render tier) " + MeasureDevice());

        var steps = new List<Action>
        {
            () => RunTypingSuite(window),
            () =>
            {
                window.Close();
                manager.CloseAll();
                done();
            }
        };
        Chain(steps, 700);
    }

    private static void RunTypingSuite(QuickNoteWindow window)
    {
        var editor = window.EditorBox;
        var card = window.CardElement;
        try
        {
            Keystrokes(window, editor, "warmup", 120);

            var shadow = card.Effect; // null: the shadow now lives on a static ring behind the card
            foreach (var size in new[] { (430.0, 300.0), (900.0, 820.0) })
            {
                window.Width = size.Item1;
                window.Height = size.Item2;
                Pump();
                Fill(editor, 3000);
                FrameTest(window, $"size={size.Item1}x{size.Item2} shipped");
            }

            // reference: the old design, blur applied to the card that holds the text
            card.Effect = (Effect)window.FindResource("CardShadow");
            Pump();
            Fill(editor, 3000);
            FrameTest(window, "size=900x820 blur-on-card");
            card.Effect = null;

            window.Width = 430;
            window.Height = 300;
            // text length is the other axis: WPF re-formats the whole buffer on every layout pass
            foreach (int length in new[] { 2000, 12000, 50000 })
            {
                Fill(editor, length);
                Keystrokes(window, editor, $"len={length}", 6);
            }
        }
        catch (Exception ex)
        {
            Log.Write("typing# failed " + ex);
        }
    }

    /// <summary>40 characters as fast as the keyboard can produce them, then one flush.</summary>
    private static void Burst(TextBox editor, string tag)
    {
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 40; i++) editor.AppendText("字");
        double queue = watch.Elapsed.TotalMilliseconds;
        Pump();
        Log.Write($"typing#   burst {tag}: queue={queue:F0}ms flush={watch.Elapsed.TotalMilliseconds - queue:F0}ms");
    }

    /// <summary>
    /// Frames per second the compositor can deliver while text is being typed. This is the only
    /// measurement that includes the render thread (the blur of a drop shadow and the
    /// UpdateLayeredWindow upload both live there, not on the dispatcher).
    /// </summary>
    private static void FrameTest(QuickNoteWindow window, string tag)
    {
        var editor = window.EditorBox;
        int frames = 0;
        var stopwatch = Stopwatch.StartNew();
        double worstGap = 0;
        double lastFrame = 0;
        void OnFrame(object? sender, EventArgs e)
        {
            double now = stopwatch.Elapsed.TotalMilliseconds;
            if (lastFrame > 0) worstGap = Math.Max(worstGap, now - lastFrame);
            lastFrame = now;
            frames++;
        }

        var typist = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(30) };
        typist.Tick += (_, _) => editor.AppendText("字");
        CompositionTarget.Rendering += OnFrame;
        typist.Start();
        stopwatch.Start();

        var frame = new DispatcherFrame();
        var stopper = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        stopper.Tick += (_, _) =>
        {
            stopper.Stop();
            frame.Continue = false;
        };
        stopper.Start();
        Dispatcher.PushFrame(frame);

        typist.Stop();
        CompositionTarget.Rendering -= OnFrame;
        double seconds = stopwatch.Elapsed.TotalSeconds;
        Log.Write($"typing#   frames {tag}: {frames / seconds:F0} fps, worst gap {worstGap:F0} ms");
    }

    private static string MeasureDevice()
    {
        try
        {
            var d3d = System.Windows.Media.RenderCapability.Tier >> 16;
            string mode = System.Windows.Media.RenderOptions.ProcessRenderMode.ToString();
            return $"tier={d3d} mode={mode}";
        }
        catch
        {
            return "unknown";
        }
    }

    private static void Fill(TextBox editor, int length)
    {
        var text = length == 0 ? string.Empty : new string('测', length);
        var watch = Stopwatch.StartNew();
        editor.Text = text;
        editor.CaretIndex = text.Length;
        Pump();
        Log.Write($"typing# fill {length}: {watch.Elapsed.TotalMilliseconds:F1} ms");
    }

    /// <summary>Per-keystroke cost, split into binding+layout and the frame the user actually waits for.</summary>
    private static void Keystrokes(QuickNoteWindow window, TextBox editor, string tag, int count)
    {
        var watch = Stopwatch.StartNew();
        double sync = 0, frame = 0;
        for (int i = 0; i < count; i++)
        {
            var t0 = watch.Elapsed.TotalMilliseconds;
            editor.AppendText("字");
            var t1 = watch.Elapsed.TotalMilliseconds;
            Pump();
            var t2 = watch.Elapsed.TotalMilliseconds;
            sync += t1 - t0;
            frame += t2 - t1;
        }
        Log.Write($"typing# {tag}: append={sync / count:F2}ms frame={frame / count:F2}ms total={(sync + frame) / count:F2}ms");
    }

    private static void Pump()
        => Dispatcher.CurrentDispatcher.Invoke(new Action(() => { }), DispatcherPriority.Input);

    // ------------------------------------------------------------------ taskbar

    /// <summary>
    /// Dev-only: samples the taskbar from left to right and prints what the hit test sees at every
    /// point, so the "empty strip vs icon" rule can be checked against the real taskbar.
    /// </summary>
    public static void Taskbar(Action done)
    {
        var taskbar = Native.FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero)
        {
            Log.Write("taskbar# not found");
            done();
            return;
        }
        Native.GetWindowRect(taskbar, out var rect);
        Log.Write($"taskbar# rect={rect.Left},{rect.Top} {rect.Width}x{rect.Height}");
        int count = 0;
        Native.EnumChildWindows(taskbar, (hwnd, _) =>
        {
            if (count++ > 24) return false;
            Native.GetWindowRect(hwnd, out var r);
            Log.Write($"taskbar# child {Native.ClassNameOf(hwnd),-28} [{r.Left},{r.Top} {r.Width}x{r.Height}] " +
                      $"vis={Native.IsWindowVisible(hwnd)} parent={Native.ClassNameOf(Native.GetParent(hwnd))}");
            return true;
        }, IntPtr.Zero);
        int y = rect.Top + rect.Height / 2;
        Log.Write("taskbar# tree:\n" + TaskbarQuery.DescribeTree(taskbar));
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 16; i++)
        {
            int x = rect.Left + 20 + (rect.Width - 40) * i / 15;
            var before = watch.Elapsed.TotalMilliseconds;
            bool blank = TaskbarQuery.IsBlankArea(x, y);
            var spent = watch.Elapsed.TotalMilliseconds - before;
            Log.Write($"taskbar# x={x} blank={blank} query={spent:F1}ms {TaskbarQuery.Describe(x, y)}");
        }
        done();
    }

    // ------------------------------------------------------------------ flyout

    public static void Flyout(string? dir, Action done)
    {
        var manager = new QuickNoteManager();
        var window = manager.OpenAt(240, 240);
        var steps = new List<Action>
        {
            () =>
            {
                MenuFlyout.Show(window.FlyoutAnchor, System.Windows.Controls.Primitives.PlacementMode.Bottom,
                    MenuFlyout.Item("复制全部内容", "\uE8C8", () => { }),
                    MenuFlyout.Item("清空便签", "\uE72C", () => { }),
                    MenuFlyout.Separator(),
                    MenuFlyout.Item("关闭", "\uE711", () => { }, danger: true));
                Log.Write("flyout# shown");
            },
            () => Describe(dir, window),
            () =>
            {
                MenuFlyout.Close();
                window.Close();
                manager.CloseAll();
                done();
            }
        };
        Chain(steps, 600);
    }

    private static void Describe(string? dir, QuickNoteWindow window)
    {
        var hwnd = MenuFlyout.LastPopupHandle;
        var anchor = window.FlyoutAnchor;
        Log.Write($"flyout# popup=0x{hwnd.ToInt64():X} dpi-anchor={VisualTreeHelper.GetDpi(anchor).DpiScaleX:F2} " +
                  $"dpi-window={VisualTreeHelper.GetDpi(window).DpiScaleX:F2} " +
                  $"dpi-win32={Native.GetDpiForWindow(WindowEffects.Handle(window))} " +
                  $"dpi-system={Native.GetDpiForSystem()} " +
                  $"screen={Native.GetSystemMetrics(Native.SM_CXSCREEN)}x{Native.GetSystemMetrics(Native.SM_CYSCREEN)} " +
                  $"wpf-screen={SystemParameters.PrimaryScreenWidth:F0}x{SystemParameters.PrimaryScreenHeight:F0} " +
                  $"anchor={anchor.ActualWidth:F0}x{anchor.ActualHeight:F0}");
        if (hwnd == IntPtr.Zero) return;
        Native.GetWindowRect(hwnd, out var rect);
        long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        Log.Write($"flyout# rect={rect.Left},{rect.Top} {rect.Width}x{rect.Height} ex=0x{ex:X} class={Native.ClassNameOf(hwnd)}");
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            Directory.CreateDirectory(dir);
            int pad = 8;
            var file = Native.CaptureScreen(rect.Left - pad, rect.Top - pad, rect.Width + pad * 2,
                rect.Height + pad * 2, Path.Combine(dir, "live-flyout.png"));
            Log.Write("flyout# captured " + (file ?? "FAILED"));
            // the note window itself, for comparison: a layered window that shows text correctly
            Native.GetWindowRect(WindowEffects.Handle(window), out var wr);
            Native.CaptureScreen(wr.Left, wr.Top, wr.Width, wr.Height, Path.Combine(dir, "live-note.png"));
        }
        catch (Exception captureError)
        {
            Log.Write("flyout# capture failed: " + captureError.Message);
        }
    }

    private static void Chain(List<Action> steps, int intervalMs)
    {
        int index = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(intervalMs) };
        timer.Tick += (_, _) =>
        {
            if (index >= steps.Count)
            {
                timer.Stop();
                return;
            }
            try
            {
                steps[index++]();
            }
            catch (Exception ex)
            {
                Log.Write("devlab failed: " + ex);
                timer.Stop();
            }
        };
        timer.Start();
    }
}
