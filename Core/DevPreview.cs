using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;
using MeiRi.Interop;
using MeiRi.Services;
using MeiRi.Views;

namespace MeiRi;

/// <summary>
/// Dev-only (<c>MeiRi.exe --preview</c>): reproduces the hover preview on screen and writes both
/// the z-order and a real screen capture at each step, so a preview that ends up behind the
/// calendar can be seen (and not only inferred from an index).
/// </summary>
public static class DevPreview
{
    private const string OutDir = @"D:\Projects\MeiRi\docs\shots";

    public static void Run(CalendarHost host, DataStore store, Action done)
    {
        // the widget the user hovers on most: the 100 % monitor one
        var calendar = host.Windows
            .OrderBy(w => VisualTreeHelper.GetDpi(w).DpiScaleX)
            .FirstOrDefault();
        if (calendar is null)
        {
            done();
            return;
        }

        Native.GetCursorPos(out var originalCursor);

        var date = new DateOnly(2026, 10, 28);
        var day = store.GetOrCreate(date);
        var item = new Models.TodoItem
        {
            Text = "看一下预览卡片的层次是否正常",
            Note = "带备注的一行，用来把预览撑高一点"
        };
        day.FreeItems.Add(item);

        var cell = host.ViewModel.Cells.FirstOrDefault(c => c.Date == date);
        if (cell is null)
        {
            Log.Write("preview# no cell for " + date);
            day.FreeItems.Remove(item);
            done();
            return;
        }

        int step = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        timer.Tick += (_, _) =>
        {
            step++;
            try
            {
                switch (step)
                {
                    case 1:
                        Click(calendar);
                        ShowAt(host, calendar, cell);
                        Report(host, calendar, "preview#1 click then hover", "preview-a.png");
                        break;
                    case 2:
                        Click(calendar);
                        ShowAt(host, calendar, cell);
                        Report(host, calendar, "preview#2 click then hover again", "preview-b.png");
                        break;
                    case 3:
                        // clicking the calendar while the card is already on screen is the case the
                        // user hit: does the widget jump above its own preview?
                        Click(calendar);
                        Report(host, calendar, "preview#3 click while card visible", "preview-c.png");
                        break;
                    case 4:
                        host.HidePreviewNow();
                        day.FreeItems.Remove(item);
                        Native.SetCursorPos(originalCursor.X, originalCursor.Y);
                        timer.Stop();
                        done();
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Write("preview# failed: " + ex);
                timer.Stop();
                done();
            }
        };
        timer.Start();
    }

    private static void Click(CalendarWindow calendar)
    {
        // a real click is what raises the widget now; the foreground dance is enough for the z-order
        WindowEffects.ForceForeground(calendar);
        WindowEffects.RaiseWithoutActivation(calendar);
    }

    private static void ShowAt(CalendarHost host, CalendarWindow calendar, ViewModels.DayCell cell)
    {
        // put the pointer over the day, the way a hover would, then show the card right there
        var local = new System.Windows.Point(0, 0);
        if (calendar.Handle != IntPtr.Zero)
        {
            Native.GetWindowRect(calendar.Handle, out var r);
            local = new System.Windows.Point(r.Left + r.Width / 2.0, r.Top + r.Height / 2.0);
        }
        Native.SetCursorPos((int)local.X, (int)local.Y);
        host.ShowPreview(cell, calendar);
    }

    private static void Report(CalendarHost host, CalendarWindow calendar, string tag, string file)
    {
        var preview = host.PreviewHandle;
        long ex = preview == IntPtr.Zero ? 0 : Native.GetWindowLongPtr(preview, Native.GWL_EXSTYLE).ToInt64();
        Log.Write($"{tag}: preview=0x{preview.ToInt64():X} " +
                  $"owner=0x{Native.GetWindow(preview, Native.GW_OWNER).ToInt64():X} ex=0x{ex:X} " +
                  host.DescribeLayout());

        try
        {
            Directory.CreateDirectory(OutDir);
            var box = new List<Native.RECT>();
            if (Native.GetWindowRect(calendar.Handle, out var cr)) box.Add(cr);
            if (preview != IntPtr.Zero && Native.GetWindowRect(preview, out var pr)) box.Add(pr);
            if (box.Count == 0) return;
            int left = box.Min(r => r.Left) - 12;
            int top = box.Min(r => r.Top) - 12;
            int right = box.Max(r => r.Right) + 12;
            int bottom = box.Max(r => r.Bottom) + 12;
            var path = Native.CaptureScreen(left, top, right - left, bottom - top, Path.Combine(OutDir, file));
            Log.Write($"{tag}: captured {path ?? "FAILED"}");
        }
        catch (Exception captureError)
        {
            Log.Write($"{tag}: capture failed: {captureError.Message}");
        }
    }
}
