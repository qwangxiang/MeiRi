using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MeiRi.Interop;
using MeiRi.Models;
using MeiRi.Services;
using MeiRi.Views;

namespace MeiRi;

/// <summary>
/// Dev-only (<c>MeiRi.exe --drop</c>): opens the day editor and drags one task out of it onto a
/// day of the calendar with synthetic input, logging at every step what is under the pointer, how
/// opaque the editor card is and whether the drop really moved the task. Writes screen captures,
/// so a card that still fogs the calendar can be seen instead of guessed.
/// </summary>
public static class DevDrop
{
    private const string OutDir = @"D:\Projects\MeiRi\docs\shots";
    private const int Steps = 8;

    public static void Run(CalendarHost host, DataStore store, Action done)
    {
        var calendar = host.Windows
            .OrderBy(w => VisualTreeHelper.GetDpi(w).DpiScaleX)
            .FirstOrDefault();
        if (calendar is null)
        {
            done();
            return;
        }

        Native.GetCursorPos(out var originalCursor);
        var foreground = Native.GetForegroundWindow();

        var srcDate = new DateOnly(2026, 10, 27);
        var dstDate = new DateOnly(2026, 10, 30);
        var src = store.GetOrCreate(srcDate);
        var item = new TodoItem { Text = "拖到 30 号试试看" };
        src.FreeItems.Add(item);

        var dstCell = host.ViewModel.Cells.FirstOrDefault(c => c.Date == dstDate);
        if (dstCell is null)
        {
            Log.Write("drop# no cell for " + dstDate);
            src.FreeItems.Remove(item);
            done();
            return;
        }

        DayEditorWindow? editor = null;
        var press = default(Native.POINT);
        var target = default(Native.POINT);
        var editorRect = default(Native.RECT);
        int step = 0;
        int move = 0;

        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(120) };
        timer.Tick += (_, _) =>
        {
            try
            {
                switch (step)
                {
                    case 0:
                    {
                        host.OpenDay(srcDate, calendar, toggle: false);
                        editor = host.EditorWindow;
                        step++;
                        break;
                    }

                    case 1:
                    {
                        if (editor is null || !editor.IsVisible)
                        {
                            Log.Write("drop# editor did not open");
                            timer.Stop();
                            Finish();
                            break;
                        }

                        if (!Native.GetWindowRect(WindowEffects.Handle(editor), out editorRect))
                        {
                            Log.Write("drop# no editor rect");
                            timer.Stop();
                            Finish();
                            break;
                        }

                        var row = FindElement(editor.Board, item);
                        var cellEl = FindCell(calendar, dstCell);
                        if (row is null || cellEl is null)
                        {
                            Log.Write($"drop# row={(row is null ? "none" : "ok")} cell={(cellEl is null ? "none" : "ok")}");
                            timer.Stop();
                            Finish();
                            break;
                        }

                        var rowBox = ScreenRect(row);
                        var cellBox = ScreenRect(cellEl);
                        press = new Native.POINT
                        {
                            X = (int)(rowBox.Left + rowBox.Width * 0.4),
                            Y = (int)(rowBox.Top + rowBox.Height * 0.5)
                        };
                        target = new Native.POINT
                        {
                            X = (int)(cellBox.Left + cellBox.Width * 0.5),
                            Y = (int)(cellBox.Top + cellBox.Height * 0.5)
                        };

                        Log.Write($"drop#1 editor=[{editorRect.Left},{editorRect.Top} " +
                                  $"{editorRect.Right - editorRect.Left}x{editorRect.Bottom - editorRect.Top}] " +
                                  $"row=[{(int)rowBox.Left},{(int)rowBox.Top} {(int)rowBox.Width}x{(int)rowBox.Height}] " +
                                  $"dst={dstDate} cell=[{(int)cellBox.Left},{(int)cellBox.Top} " +
                                  $"{(int)cellBox.Width}x{(int)cellBox.Height}] " + host.DescribeLayout());

                        Native.SetCursorPos(press.X, press.Y);
                        var under = Native.WindowFromPoint(press);
                        Log.Write($"drop#1 press={press.X},{press.Y} hit={Native.ClassNameOf(under)} " +
                                  $"ours={Native.WindowFromPoint(press) == WindowEffects.Handle(editor)}");
                        Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
                        step++;
                        move = 0;
                        break;
                    }

                    case 2:
                    {
                        move++;
                        double t = Math.Min(1.0, move / (double)Steps);
                        int x = (int)(press.X + (target.X - press.X) * t);
                        int y = (int)(press.Y + (target.Y - press.Y) * t);
                        Native.SetCursorPos(x, y);

                        var under = Native.WindowFromPoint(new Native.POINT { X = x, Y = y });
                        var local = new Point(x, y);
                        Log.Write($"drop#2 {move} cur={x},{y} cardOpacity={editor!.Opacity:F2} " +
                                  $"inCard={WindowEffects.PointInside(editor, x, y)} " +
                                  $"hit={(Native.WindowFromPoint(new Native.POINT { X = x, Y = y }) == WindowEffects.Handle(editor) ? "editor" : Native.ClassNameOf(under) + ":" + Owner(under))} " +
                                  $"cell={host.DayCellAt(x, y)?.Date.ToString() ?? "none"}");

                        if (move < Steps) break;
                        step++;
                        break;
                    }

                    case 3:
                    {
                        Capture(calendar, editor, "drop-a.png", "drop#3 over target day");
                        Log.Write($"drop#3 cardOpacity={editor!.Opacity:F2} " + host.DescribeLayout());
                        step++;
                        break;
                    }

                    case 4:
                    {
                        Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
                        step++;
                        break;
                    }

                    case 5:
                    {
                        var now = store.GetExisting(dstDate);
                        bool inDst = now is not null && (now.FreeItems.Contains(item) ||
                            now.Categories.Any(c => c.Items.Contains(item)));
                        bool inSrc = src.FreeItems.Contains(item) || src.Categories.Any(c => c.Items.Contains(item));
                        Log.Write($"drop#5 after release inDst={inDst} inSrc={inSrc} " +
                                  $"cardOpacity={editor!.Opacity:F2} " + host.DescribeLayout());
                        Capture(calendar, editor, "drop-b.png", "drop#5 after release");
                        step++;
                        break;
                    }

                    case 6:
                        timer.Stop();
                        Finish();
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Write("drop# failed: " + ex);
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
                // put the test task back where it came from, then take it away again
                foreach (var bucket in new[] { src, store.GetExisting(dstDate) })
                {
                    if (bucket is null) continue;
                    bucket.FreeItems.Remove(item);
                    foreach (var cat in bucket.Categories) cat.Items.Remove(item);
                }
                store.NotifyTarget(null);
                host.CloseEditor();
                Native.SetCursorPos(originalCursor.X, originalCursor.Y);
                if (foreground != IntPtr.Zero) Native.SetForegroundWindow(foreground);
                Log.Write("drop# cleaned up");
            }
            catch (Exception ex)
            {
                Log.Write("drop# cleanup failed: " + ex);
            }
            done();
        }
    }

    private static string Owner(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == Environment.ProcessId ? "ours" : "pid" + pid;
    }

    private static FrameworkElement? FindElement(DependencyObject root, object data)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement el && ReferenceEquals(el.DataContext, data)
                && el.ActualWidth > 0 && el.ActualHeight > 0)
                return el;
            if (child is DependencyObject deeper && FindElement(child, data) is { } found) return found;
        }
        return null;
    }

    private static FrameworkElement? FindCell(DependencyObject root, ViewModels.DayCell cell)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement el && ReferenceEquals(el.DataContext, cell)
                && el.ActualWidth > 0 && el.ActualHeight > 0)
                return el;
            if (child is DependencyObject deeper && FindCell(deeper, cell) is { } found) return found;
        }
        return null;
    }

    private static Rect ScreenRect(FrameworkElement el)
    {
        var origin = el.PointToScreen(new Point(0, 0));
        return new Rect(origin.X, origin.Y, el.ActualWidth, el.ActualHeight);
    }

    private static void Capture(CalendarWindow calendar, Window? editor, string file, string tag)
    {
        try
        {
            Directory.CreateDirectory(OutDir);
            var box = new List<Native.RECT>();
            if (Native.GetWindowRect(calendar.Handle, out var cr)) box.Add(cr);
            if (editor is not null && Native.GetWindowRect(WindowEffects.Handle(editor), out var er)) box.Add(er);
            if (box.Count == 0) return;
            int left = box.Min(r => r.Left) - 12;
            int top = box.Min(r => r.Top) - 12;
            int right = box.Max(r => r.Right) + 12;
            int bottom = box.Max(r => r.Bottom) + 12;
            var path = Native.CaptureScreen(left, top, right - left, bottom - top, Path.Combine(OutDir, file));
            Log.Write($"{tag}: captured {path ?? "FAILED"} region=[{left},{top} {right - left}x{bottom - top}]");
        }
        catch (Exception ex)
        {
            Log.Write($"{tag}: capture failed: {ex.Message}");
        }
    }
}
